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
        private Task? _pollTask;
        private SqliteDataAccess? _db;
        private CancellationTokenSource? _cts;

        // 轮询兜底：记录每个 .txt 文件上次观察到的大小 / 最后写入时间
        private ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc)> _pollState
            = new ConcurrentDictionary<string, (long, DateTime)>();
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        // 每个文件一把锁，保证 watcher 事件和轮询不会并发读同一个文件 / 重复入队同一段内容
        private ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        // 轮询心跳：每分钟打一次 "我还在跑"，避免 3 秒一行刷屏，又能确认轮询没死
        private DateTime _lastPollHeartbeatUtc = DateTime.MinValue;
        private static readonly TimeSpan PollHeartbeatInterval = TimeSpan.FromMinutes(1);
        private long _pollTickCount = 0;
        private long _eventCount = 0;       // watcher 触发的事件计数
        private long _pollHandleCount = 0;  // 轮询发现并入队的次数



        public Automate(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        private async Task ProcessNewContent(CancellationToken ct)
        {
            _logger.Info("消费线程启动");
            while(!ct.IsCancellationRequested)
            {

                if (_queue.TryDequeue(out var content))
                {
                    var qrList = ExtractQR(content).ToList();
                    _logger.Info($"[Consume] 出队一段内容 长度={content.Length} 提取QR={qrList.Count} 剩余队列≈{_queue.Count}");
                    if (qrList.Count > 0)
                    {
                        await Submit(qrList);
                    }
                    else
                    {
                        _logger.Debug("[Consume] 内容中无 QR 匹配，跳过提交");
                    }
                }
                else
                {
                    await Task.Delay(500);
                }
            }
            _logger.Info("消费线程已退出");
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
            _logger.Info($"==== Automate 启动 ==== 目录={_directory} 轮询间隔={PollInterval.TotalSeconds}s");
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
                _logger.Info($"数据库初始化完成，恢复 {records.Count} 个文件的读取位置");
                foreach (var rec in records)
                {
                    _logger.Debug($"  恢复位置: {rec.FilePath} @ {rec.LastPosition}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "初始化数据库失败");
            }
            StartFileWatcher();
            _pollTask = Task.Run(() => PollDirectoryLoopAsync(_cts.Token), _cts.Token);
            _logger.Info("Automate started.");
        }

        public void Stop()
        {
            if (_cts == null) return;
            _logger.Info($"==== Automate 停止 ==== 累计 watcher 事件={_eventCount} 轮询发现变化={_pollHandleCount} 轮询循环次数={_pollTickCount}");
            _cts.Cancel();
            try { _handleTask?.Wait(1000); } catch (Exception ex) { _logger.Warn($"等待处理任务退出超时: {ex.Message}"); }
            try { _pollTask?.Wait(1000); } catch (Exception ex) { _logger.Warn($"等待轮询任务退出超时: {ex.Message}"); }
            _watcher?.Dispose();
            _watcher = null;
            _cts.Dispose();
            _cts = null;
            try { _db?.Dispose(); } catch (Exception ex) { _logger.Warn($"关闭数据库异常: {ex.Message}"); }
            _db = null;
            _logger.Info("Automate stopped.");
        }

        private void StartFileWatcher()
        {
            var di = new DirectoryInfo(_directory);
            if (!di.Exists)
            {
                _logger.Warn($"监视目录不存在: {_directory}");
                return;
            }

            _watcher = new FileSystemWatcher(di.FullName, "*.txt")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = false,
                // 默认 8KB 容易在高频写入时溢出，提到 64KB
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
            _logger.Info($"FileSystemWatcher 已启动 | 目录={di.FullName} 过滤=*.txt 缓冲区=64KB 通知项=FileName|LastWrite|CreationTime|Size");

            // 启动时扫一遍现有 .txt，把每个文件交给处理管道走一次
            // —— 这样程序启动前就已经存在的、或启动期间错过事件的文件也能被消费
            try
            {
                var existing = di.EnumerateFiles("*.txt").ToList();
                _logger.Info($"启动扫描：发现 {existing.Count} 个 .txt 文件");
                foreach (var f in existing)
                {
                    _readRecors.TryAdd(f.FullName, 0);
                    _pollState[f.FullName] = (f.Length, f.LastWriteTimeUtc);
                    var lastPos = _readRecors.TryGetValue(f.FullName, out var p) ? p : 0;
                    _logger.Info($"  [Startup] {f.Name} 大小={f.Length} 已读={lastPos} 修改={f.LastWriteTime:HH:mm:ss.fff}");
                    QueueHandle(f.FullName, "Startup");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动扫描目录失败");
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // 缓冲区溢出 / 监视句柄失效等错误以前是静默的，这里显式记录
            var inner = e.GetException();
            _logger.Error(inner, $"FileSystemWatcher 错误: {inner.GetType().Name}: {inner.Message} —— 可能是缓冲区溢出或监视句柄失效，轮询会继续兜底");
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Interlocked.Increment(ref _eventCount);
            _logger.Info($"[Watcher.Renamed] {e.OldFullPath} -> {e.FullPath}");
            // 后台服务可能以 “写临时文件 + Rename 成最终名” 的原子写入方式落盘，
            // 这种情况下只会触发 Renamed，不会触发 Changed/Created，必须接进处理管道
            if (e.FullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                QueueHandle(e.FullPath, "Renamed");
            }
            else
            {
                _logger.Debug($"[Watcher.Renamed] 目标不是 .txt，忽略: {e.FullPath}");
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            Interlocked.Increment(ref _eventCount);
            _logger.Info($"[Watcher.Created] {e.FullPath}");
            // 一次性写完即关闭的新文件可能只发 Created 不发 Changed，所以也要走处理
            QueueHandle(e.FullPath, "Created");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            Interlocked.Increment(ref _eventCount);
            // Changed 事件非常密集，用 Debug 级，避免 Info 日志被刷爆
            _logger.Debug($"[Watcher.Changed] {e.FullPath} 类型={e.ChangeType}");
            if (e.ChangeType == WatcherChangeTypes.Deleted || e.ChangeType == WatcherChangeTypes.Renamed) return;
            QueueHandle(e.FullPath, "Changed");
        }

        // 统一入口：事件 / 启动扫描 / 轮询都走这里，做去抖 + 处理
        private void QueueHandle(string fullPath, string source)
        {
            Task.Run(async () =>
            {
                await Task.Delay(150); // 等待写入结束
                if (!_readRecors.ContainsKey(fullPath))
                {
                    _readRecors.TryAdd(fullPath, 0);
                }
                var sem = _fileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
                // 如果同一文件已有处理在跑，记录等待 —— 排查并发竞争用
                var waited = !await sem.WaitAsync(0);
                if (waited)
                {
                    _logger.Debug($"[{source}] 等待文件锁: {fullPath}");
                    await sem.WaitAsync();
                }
                try
                {
                    await HandleFileChangeAsync(fullPath, source);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"[{source}] 处理文件失败: {fullPath}");
                }
                finally
                {
                    sem.Release();
                }
            });
        }

        // 轮询兜底：每 PollInterval 扫描一次目录，对比 Length / LastWriteTimeUtc，
        // 任何变化都走 QueueHandle。即使 FileSystemWatcher 因某些原因沉默（驱动/杀软 hook、
        // 缓冲区已溢出、服务用了奇怪的 IO 模式），轮询也能补上事件，最大延迟 = PollInterval。
        private async Task PollDirectoryLoopAsync(CancellationToken ct)
        {
            _logger.Info($"轮询线程启动，间隔={PollInterval.TotalSeconds}s，心跳间隔={PollHeartbeatInterval.TotalMinutes}min");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Increment(ref _pollTickCount);
                    var di = new DirectoryInfo(_directory);
                    if (!di.Exists)
                    {
                        // 目录消失是异常情况，必须显眼
                        _logger.Warn($"[Poll] 监视目录不存在: {_directory}");
                    }
                    else
                    {
                        var changedCount = 0;
                        var fileCount = 0;
                        foreach (var f in di.EnumerateFiles("*.txt"))
                        {
                            fileCount++;
                            var key = f.FullName;
                            var snapshot = (f.Length, f.LastWriteTimeUtc);
                            if (_pollState.TryGetValue(key, out var prev))
                            {
                                if (prev.Length != snapshot.Length || prev.LastWriteUtc != snapshot.LastWriteTimeUtc)
                                {
                                    _logger.Info($"[Poll] 检测到变化: {f.Name} 大小 {prev.Length}->{snapshot.Length} 修改 {prev.LastWriteUtc:HH:mm:ss.fff}->{snapshot.LastWriteTimeUtc:HH:mm:ss.fff}");
                                    _pollState[key] = snapshot;
                                    Interlocked.Increment(ref _pollHandleCount);
                                    changedCount++;
                                    QueueHandle(key, "Poll");
                                }
                            }
                            else
                            {
                                // 第一次看到这个文件 —— 可能 watcher 事件已经处理过，也可能没有；
                                // 让 HandleFileChangeAsync 凭 _readRecors 里的位置去判断是否真的有新内容
                                _logger.Info($"[Poll-New] 发现新文件: {f.Name} 大小={snapshot.Length} 修改={snapshot.LastWriteTimeUtc:HH:mm:ss.fff}");
                                _pollState[key] = snapshot;
                                Interlocked.Increment(ref _pollHandleCount);
                                changedCount++;
                                QueueHandle(key, "Poll-New");
                            }
                        }

                        // 心跳：每 PollHeartbeatInterval 一行，证明轮询还活着 + 给出摘要
                        var nowUtc = DateTime.UtcNow;
                        if (nowUtc - _lastPollHeartbeatUtc >= PollHeartbeatInterval)
                        {
                            _lastPollHeartbeatUtc = nowUtc;
                            _logger.Info($"[Poll-Heartbeat] 轮询正常 | 累计循环={_pollTickCount} watcher事件={_eventCount} 轮询入队={_pollHandleCount} 当前.txt文件数={fileCount}");
                        }
                        else if (changedCount > 0)
                        {
                            _logger.Debug($"[Poll] 本轮入队 {changedCount} 项（共 {fileCount} 个 .txt）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "轮询目录失败");
                }

                try { await Task.Delay(PollInterval, ct); }
                catch (TaskCanceledException) { break; }
            }
            _logger.Info("轮询线程已退出");
        }

        private async Task HandleFileChangeAsync(string filePath, string source = "?")
        {
            // 尝试读取文件（处理被占用的情况）
            Thread.Sleep(100);

            try
            {
                string newContent = string.Empty;
                long lengthBefore;
                long oldPos;
                long newPos;
                using (var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    lengthBefore = fs.Length;
                    oldPos = _readRecors[filePath];

                    // 如果文件被清空（例如重写）
                    if (fs.Length < _readRecors[filePath])
                    {
                        _logger.Warn($"[{source}] 文件被截断/重写: {filePath} 大小={fs.Length} < 上次位置={_readRecors[filePath]}，从头读");
                        _readRecors[filePath] = 0;
                        oldPos = 0;
                    }

                    fs.Seek(_readRecors[filePath], SeekOrigin.Begin);

                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        newContent = reader.ReadToEnd();
                        _readRecors[filePath] = fs.Position;
                    }
                    newPos = _readRecors[filePath];
                }

                var byteDelta = newPos - oldPos;
                if (!string.IsNullOrEmpty(newContent))
                {
                    // 预先看看本次内容里有多少个 QR，便于和提交端日志对账
                    var qrCount = ExtractQR(newContent).Count();
                    _logger.Info($"[{source}] 读取 {filePath} | 位置 {oldPos}->{newPos} (+{byteDelta}B) 文件总长={lengthBefore} 字符数={newContent.Length} 匹配QR={qrCount}");

                    _queue.Enqueue(newContent);
                    _logger.Debug($"[{source}] 已入队，当前处理队列长度≈{_queue.Count}");

                    // 持久化当前读取位置
                    try
                    {
                        if (_db != null)
                        {
                            var pos = _readRecors[filePath];
                            // fire-and-forget but await to ensure write
                            await _db.UpsertFileReadRecordAsync(new FileReadRecord { FilePath = filePath, LastPosition = pos });
                            _logger.Debug($"[{source}] 持久化位置: {filePath} @ {pos}");
                        }
                        else
                        {
                            _logger.Warn($"[{source}] _db 为 null，跳过位置持久化");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"[{source}] 保存文件读取位置失败");
                    }
                }
                else
                {
                    // 这是常见情况（watcher 和轮询同时触发后，第二次读到 0 字节），用 Debug
                    _logger.Debug($"[{source}] {filePath} 无新内容 (位置={oldPos} 总长={lengthBefore})");
                }
            }
            catch (IOException ex)
            {
                _logger.Error(ex, $"[{source}] 读取文件失败: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[{source}] 处理文件出现未预期异常: {filePath}");
            }
        }

        private async Task Submit(IEnumerable<string> qrList)
        {
            var qrs = qrList as IList<string> ?? qrList.ToList();
            _logger.Info($"[Submit] 开始提交 {qrs.Count} 个 QR");
            try
            {
                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                var winElement = desktop.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window).And(cf.ByName(FlaUIUitls.TARGET_WINDOW_TITLE))).AsWindow();
                if (winElement == null)
                {
                    _logger.Warn($"[Submit] 未找到目标窗口 \"{FlaUIUitls.TARGET_WINDOW_TITLE}\"，丢弃本批 {qrs.Count} 个 QR");
                    return;
                }

                var window = winElement.AsWindow();
                var tbElement = window.FindFirstDescendant(cf => cf.ByAutomationId(FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID));
                if (tbElement == null)
                {
                    _logger.Warn($"[Submit] 未找到目标文本框 AutomationId={FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID}，丢弃本批 {qrs.Count} 个 QR");
                    return;
                }

                var tb = tbElement.AsTextBox();
                int ok = 0, failed = 0;
                foreach (var qr in qrs)
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
                            _logger.Warn($"[Submit] QR={qr} 出现弹窗 标题=\"{title}\"，已点确定");
                            isProcessed = false;
                        }
                    }
                    if (isProcessed) ok++; else failed++;
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
                        _logger.Error(ex, $"[Submit] 保存 QR 到数据库失败 QR={qr}");
                    }
                    _logger.Info($"[Submit] {(isProcessed ? "OK" : "FAIL")} QR={qr}");
                    await Task.Delay(200);
                }
                _logger.Info($"[Submit] 本批完成 成功={ok} 失败={failed}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[Submit] 提交过程出现异常");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
