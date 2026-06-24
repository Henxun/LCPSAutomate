using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NLog;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LCPSAutomate
{

    public class Automate : IDisposable
    {
        private static readonly object _lock = new object(); // 用于防止并发读取同一个文件
        private readonly string _directory;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private FileSystemWatcher? _watcher;
        private ConcurrentDictionary<string, long> _readRecors = new ConcurrentDictionary<string, long>();
        private ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private Task? _handleTask;
        private SqliteDataAccess? _db;
        private CancellationTokenSource? _cts;



        public Automate(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        private async Task ProcessNewContent(CancellationToken ct)
        {
            while(!ct.IsCancellationRequested)
            {

                if (_queue.TryDequeue(out var content))
                {
                    await Submit(ExtractQR(content));
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        }

        private IEnumerable<string> ExtractQR(string text)
        {
            var matches = Regex.Matches(text, "<QR読込>:\\s(.{150})\\[CR\\]");
            foreach (Match m in matches)
            {
                if (m.Groups.Count > 1)
                {
                    yield return m.Groups[1].Value;
                }
            }
        }

        public async Task Start()
        {
            if (_cts != null) throw new InvalidOperationException("Already started.");
            _cts = new CancellationTokenSource();
            _handleTask = Task.Run(() => ProcessNewContent(_cts.Token), _cts.Token);
            // 初始化数据库（放在 Start 中，使用目录下的文件）
            try
            {
                _db = new SqliteDataAccess();

                // 尝试从数据库恢复每个已知文件的上次读取位置
                var records = await _db.GetFileReadRecordsAsync();
                foreach (var rec in records)
                {
                    _readRecors.TryAdd(rec.FilePath, rec.LastPosition);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("初始化数据库失败: " + ex.Message);
            }
            StartFileWatcher();
            _logger.Info("Automate started.");
        }

        public void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            try { _handleTask?.Wait(1000); } catch { }
            _watcher?.Dispose();
            _watcher = null;
            _cts.Dispose();
            _cts = null;
            try { _db?.Dispose(); } catch { }
            _db = null;
            _logger.Info("Automate stopped.");
        }

        private void StartFileWatcher()
        {
            var di = new DirectoryInfo(_directory);
            if (!di.Exists)
            {
                // 如果文件不存在，可以创建空文件，或者记录日志
                _logger.Info("目录不存在");
                return;
            }

            _watcher = new FileSystemWatcher(di.FullName, "*.txt")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnFileChanged;
            //_watcher.Created += OnFileChanged;
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            _watcher.EnableRaisingEvents = true;
            _logger.Info($"开始监视文件：{_directory}");
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            _logger.Info($"File Renamed: {e.OldFullPath} -> {e.FullPath}");
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            _logger.Info($"File Created: {e.FullPath}");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            _logger.Info($"File Changed: {e.FullPath}");
            // 文件写入事件可能会触发多次，异步处理并做简单去抖
            Task.Run(async () =>
            {
                await Task.Delay(150); // 等待写入结束
                if (e.ChangeType == WatcherChangeTypes.Deleted || e.ChangeType == WatcherChangeTypes.Renamed) return;

                if (!_readRecors.ContainsKey(e.FullPath))
                {
                    _readRecors.TryAdd(e.FullPath, 0);
                }

                await HandleFileChangeAsync(e.FullPath);
            });
        }

        private async Task HandleFileChangeAsync(string filePath)
        {
            _logger.Info($"处理文件变化: {filePath}");
            string content = string.Empty;
            // 尝试读取文件（处理被占用的情况）
            Thread.Sleep(100);

            try
            {
                string newContent = string.Empty;
                using (var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    // 如果文件被清空（例如重写）
                    if (fs.Length < _readRecors[filePath])
                    {
                        _readRecors[filePath] = 0;
                    }

                    _logger.Info($"当前文件上次读取位置：{_readRecors[filePath]}");

                    fs.Seek(_readRecors[filePath], SeekOrigin.Begin);

                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        newContent = reader.ReadToEnd();
                        _readRecors[filePath] = fs.Position;
                    }
                    _logger.Info($"当前文件更新读取位置：{_readRecors[filePath]}");
                }


                if (!string.IsNullOrEmpty(newContent))
                {
                    _queue.Enqueue(newContent);
                    // 持久化当前读取位置
                    try
                    {
                        if (_db != null)
                        {
                            var pos = _readRecors[filePath];
                            // fire-and-forget but await to ensure write
                            await _db.UpsertFileReadRecordAsync(new FileReadRecord { FilePath = filePath, LastPosition = pos });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("保存文件读取位置失败: " + ex.Message);
                    }
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("读取失败：" + ex.Message);
            }
            
        }

        private async Task Submit(IEnumerable<string> qrList)
        {
            try
            {
                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                var winElement = desktop.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window).And(cf.ByName(FlaUIUitls.TARGET_WINDOW_TITLE))).AsWindow();
                if (winElement == null)
                {
                    _logger.Info("设置文本时未找到目标窗口。");
                    return;
                }

                var window = winElement.AsWindow();
                var tbElement = window.FindFirstDescendant(cf => cf.ByAutomationId(FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID));
                if (tbElement == null)
                {
                    _logger.Info("未找到目标文本框（AutomationId: " + FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID + "）。");
                    return;
                }

                var tb = tbElement.AsTextBox();
                foreach(var qr in qrList)
                {
                    tb.Text = qr.Trim();
                    await Task.Delay(200);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
                    await Task.Delay(200);
                    // 判断是否弹出了错误弹窗
                    var isProcessed = true;
                    foreach (var modal in window.ModalWindows)
                    {
                        var title = modal.Title;
                        if (title.Contains("错误") || title.Contains("警告"))
                        {
                            var okButton = modal.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName("确定")))?.AsButton();
                            okButton?.Invoke();
                            _logger.Info("提交 QR 时出现错误弹窗，已关闭。");
                            isProcessed = false;
                        }
                    }
                    try
                    {
                        if (_db != null)
                        {
                            await _db.AddOrIgnoreRecordAsync(new Records { Qr = qr, IsProcessed = isProcessed });
                            await _db.MarkRecordProcessedByQrAsync(qr);
                        }
                        else
                        {
                            using var dataAccess = new SqliteDataAccess();
                            await dataAccess.AddOrIgnoreRecordAsync(new Records { Qr = qr, IsProcessed = isProcessed });
                            await dataAccess.MarkRecordProcessedByQrAsync(qr);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Info("保存 QR 到数据库失败: " + ex.Message);
                    }
                    _logger.Info("已提交 QR: " + qr);
                    await Task.Delay(200);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("设置文本时异常: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
