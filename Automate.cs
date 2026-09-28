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
        private ConcurrentDictionary<string, byte> _scheduledQrs = new ConcurrentDictionary<string, byte>();
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
            while (!ct.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var qr))
                {
                    _logger.Info($"[Consume] 开始处理 QR={DescribeQr(qr)} 剩余队列≈{_queue.Count}");
                    try
                    {
                        await SubmitQrAsync(qr, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"[Consume] QR 提交出现未处理异常 QR={DescribeQr(qr)}");
                        await SaveSubmitFailureAsync(qr, $"未处理异常: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        _scheduledQrs.TryRemove(qr, out _);
                    }
                }
                else
                {
                    try { await Task.Delay(500, ct); }
                    catch (TaskCanceledException) { break; }
                }
            }
            _logger.Info("消费线程已退出");
        }

        private void EnqueuePendingQr(string qr, string source)
        {
            if (_scheduledQrs.TryAdd(qr, 0))
            {
                _queue.Enqueue(qr);
                _logger.Debug($"[{source}] QR 已进入提交队列 QR={DescribeQr(qr)} 队列长度≈{_queue.Count}");
            }
            else
            {
                _logger.Debug($"[{source}] QR 已在队列或正在提交，跳过重复调度 QR={DescribeQr(qr)}");
            }
        }

        private static string DescribeQr(string qr)
        {
            if (string.IsNullOrEmpty(qr)) return "<empty>";
            if (qr.Length <= 16) return $"{qr}(len={qr.Length})";
            return $"{qr[..8]}...{qr[^8..]}(len={qr.Length})";
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

            try
            {
                _db = new SqliteDataAccess();

                // 恢复每个已知文件的读取位置。
                var records = await _db.GetFileReadRecordsAsync();
                foreach (var rec in records)
                {
                    _readRecors.TryAdd(rec.FilePath, rec.LastPosition);
                }

                // 先恢复数据库中的待提交 QR，再启动消费线程。这样异常退出后不会丢失提交任务。
                var pendingRecords = (await _db.GetUnprocessedRecordsAsync()).ToList();
                foreach (var pending in pendingRecords)
                {
                    EnqueuePendingQr(pending.Qr, "Startup-Recovery");
                }

                _logger.Info($"数据库初始化完成，恢复 {records.Count} 个文件读取位置、{pendingRecords.Count} 个待提交 QR");
                foreach (var rec in records)
                {
                    _logger.Debug($"  恢复位置: {rec.FilePath} @ {rec.LastPosition}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "初始化数据库失败，自动化不会启动以避免 QR 丢失");
                _cts.Dispose();
                _cts = null;
                _db?.Dispose();
                _db = null;
                throw;
            }

            try
            {
                StartFileWatcher();
                await InitializeBaselineAsync();
                // 基线完整持久化后才启动 watcher、消费线程和轮询，避免启动阶段处理旧文件。
                if (_watcher != null) _watcher.EnableRaisingEvents = true;
                _logger.Info("FileSystemWatcher 事件已开启");
                _handleTask = Task.Run(() => ProcessNewContent(_cts.Token), _cts.Token);
                _pollTask = Task.Run(() => PollDirectoryLoopAsync(_cts.Token), _cts.Token);
                _logger.Info("Automate started.");
            }
            catch
            {
                _watcher?.Dispose();
                _watcher = null;
                _db?.Dispose();
                _db = null;
                _cts.Dispose();
                _cts = null;
                throw;
            }
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
            // 注意：EnableRaisingEvents 不在这里打开。先让 InitializeBaselineAsync 把已有文件
            // 的基线位置设好，再开事件，避免基线设好之前 OnFileChanged 用 0 把历史全读一遍。
            _logger.Info($"FileSystemWatcher 已注册回调（事件待基线就绪后开启）| 目录={di.FullName} 过滤=*.txt 缓冲区=64KB");
        }

        // 启动基线：程序启动前已经存在的所有 .txt 文件，一律把“当前末尾”作为基线。
        // 不再从数据库中的历史位置回读旧文件；即使 FileSystemWatcher 在启用后产生延迟/伪事件，
        // 也只能从本次启动时的末尾继续读取。启动后新建的文件仍从 0 开始读取。
        private async Task InitializeBaselineAsync()
        {
            var di = new DirectoryInfo(_directory);
            if (!di.Exists) return;

            try
            {
                var existing = di.EnumerateFiles("*.txt").ToList();
                _logger.Info($"启动基线扫描：发现 {existing.Count} 个 .txt 文件（全部跳过启动前的历史内容）");
                foreach (var f in existing)
                {
                    _pollState[f.FullName] = (f.Length, f.LastWriteTimeUtc);
                    var hadPreviousPosition = _readRecors.TryGetValue(f.FullName, out var previousPosition);

                    // 无论数据库中是否存在旧位置，都覆盖为本次启动时的文件末尾。
                    _readRecors[f.FullName] = f.Length;
                    _logger.Info(
                        $"  [Baseline-Skip] {f.Name} 大小={f.Length} " +
                        $"历史位置={(hadPreviousPosition ? previousPosition : "<none>")} -> 启动基线={f.Length}");

                    try
                    {
                        if (_db == null)
                        {
                            throw new InvalidOperationException("数据库未初始化，无法保存启动基线");
                        }

                        await _db.UpsertFileReadRecordAsync(new FileReadRecord
                        {
                            FilePath = f.FullName,
                            LastPosition = f.Length
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"持久化启动基线失败: {f.FullName}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动基线扫描失败");
                throw;
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

                // FileSystemWatcher 偶尔会在刚启用时产生延迟/伪事件。只有文件元数据相对
                // 启动基线确实发生变化时才打开文件；Poll 来源已经在轮询中完成了该判断。
                if (!source.StartsWith("Poll", StringComparison.Ordinal) && !UpdateSnapshotIfChanged(fullPath, source))
                {
                    return;
                }

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

        private bool UpdateSnapshotIfChanged(string fullPath, string source)
        {
            try
            {
                var file = new FileInfo(fullPath);
                if (!file.Exists)
                {
                    _logger.Debug($"[{source}] 文件已不存在，忽略事件: {fullPath}");
                    return false;
                }

                file.Refresh();
                var current = (file.Length, file.LastWriteTimeUtc);
                if (_pollState.TryGetValue(fullPath, out var previous) &&
                    previous.Length == current.Length &&
                    previous.LastWriteUtc == current.LastWriteTimeUtc)
                {
                    _logger.Debug($"[{source}] 文件元数据相对基线未变化，忽略延迟/伪事件: {fullPath}");
                    return false;
                }

                _pollState[fullPath] = current;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"[{source}] 校验文件变化失败，将交给读取流程处理: {fullPath}");
                return true;
            }
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
            // 给写入方一个短暂的落盘窗口；文件锁保证同一文件不会并发读取。
            await Task.Delay(100);

            try
            {
                string newContent;
                long lengthBefore;
                long previousPosition = _readRecors.GetOrAdd(filePath, 0);
                long readFrom = previousPosition;
                long newPosition;

                using (var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    lengthBefore = fs.Length;
                    if (fs.Length < previousPosition)
                    {
                        _logger.Warn($"[{source}] 文件被截断/重写: {filePath} 大小={fs.Length} < 上次位置={previousPosition}，从头读");
                        readFrom = 0;
                    }

                    fs.Seek(readFrom, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    newContent = await reader.ReadToEndAsync();
                    newPosition = fs.Position;
                }

                var positionChanged = newPosition != previousPosition;
                if (string.IsNullOrEmpty(newContent) && !positionChanged)
                {
                    // watcher 和轮询可能先后触发，第二次通常没有新字节。
                    _logger.Debug($"[{source}] {filePath} 无新内容 (位置={previousPosition} 总长={lengthBefore})");
                    return;
                }

                var qrs = ExtractQR(newContent).Select(qr => qr.Trim()).ToList();
                _logger.Info($"[{source}] 读取 {filePath} | 位置 {readFrom}->{newPosition} (+{newPosition - readFrom}B) 文件总长={lengthBefore} 字符数={newContent.Length} 匹配QR={qrs.Count}");

                if (_db == null)
                {
                    throw new InvalidOperationException("数据库未初始化，拒绝推进文件读取位置");
                }

                // 待提交 QR 与读取位置必须在同一个事务中落库。事务完成后才更新内存位置和提交队列。
                var pendingQrs = await _db.PersistPendingRecordsAndFilePositionAsync(
                    qrs,
                    new FileReadRecord { FilePath = filePath, LastPosition = newPosition });

                _readRecors[filePath] = newPosition;
                _logger.Debug($"[{source}] 已原子持久化 {pendingQrs.Count} 个待提交 QR，文件位置={newPosition}");

                foreach (var qr in pendingQrs)
                {
                    EnqueuePendingQr(qr, source);
                }
            }
            catch (IOException ex)
            {
                _logger.Error(ex, $"[{source}] 读取文件失败: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[{source}] 处理文件出现未预期异常，读取位置不会推进: {filePath}");
            }
        }

        private async Task SubmitQrAsync(string qr, CancellationToken ct)
        {
            const int maxAttempts = 3;
            string lastError = "未知提交错误";

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var enterPressed = false;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var automation = new UIA3Automation();
                    var desktop = automation.GetDesktop();
                    var winElement = desktop.FindFirstChild(cf =>
                        cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)
                            .And(cf.ByName(FlaUIUitls.TARGET_WINDOW_TITLE)));
                    if (winElement == null)
                    {
                        lastError = $"未找到目标窗口 {FlaUIUitls.TARGET_WINDOW_TITLE}";
                        throw new InvalidOperationException(lastError);
                    }

                    var window = winElement.AsWindow();

                    // 先处理上一条提交遗留的模态窗口，否则主窗口文本框会处于 Disabled 状态。
                    var preExistingModal = TryHandleModalWindow(window, qr, "提交前");
                    if (preExistingModal != null)
                    {
                        _logger.Warn($"[Submit] 提交前发现遗留模态窗口，等待主窗口恢复 QR={DescribeQr(qr)} Modal=\"{preExistingModal}\"");
                        await Task.Delay(300, ct);
                    }

                    var textBox = await WaitForEnabledTextBoxAsync(window, ct);
                    if (textBox == null)
                    {
                        lastError = $"目标文本框在等待超时后仍不可用 AutomationId={FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID}";
                        throw new InvalidOperationException(lastError);
                    }

                    textBox.Focus();
                    textBox.Text = qr.Trim();
                    await Task.Delay(200, ct);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
                    enterPressed = true;

                    // 弹窗可能异步出现，不能只在固定的 200ms 时刻检查一次。
                    var modalTitle = await WaitForAndHandleModalAsync(window, qr, ct);
                    if (modalTitle != null)
                    {
                        lastError = $"HandyClient 返回模态窗口: {modalTitle}";
                        await SaveSubmitFailureAsync(qr, lastError);
                        _logger.Warn($"[Submit] FAIL QR={DescribeQr(qr)} Attempt={attempt}/{maxAttempts} Error={lastError}");
                        return;
                    }

                    if (_db == null) throw new InvalidOperationException("数据库未初始化");
                    await _db.MarkRecordProcessedByQrAsync(qr);
                    _logger.Info($"[Submit] OK QR={DescribeQr(qr)} Attempt={attempt}/{maxAttempts}");
                    await Task.Delay(200, ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                    _logger.Warn(ex, $"[Submit] 尝试失败 QR={DescribeQr(qr)} Attempt={attempt}/{maxAttempts} EnterPressed={enterPressed}");

                    // Enter 已经发出时，结果是不确定的。为避免重复提交，不自动再次按 Enter。
                    if (enterPressed)
                    {
                        lastError = $"提交结果未知（Enter 已发送）: {lastError}";
                        break;
                    }

                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(500, ct);
                    }
                }
            }

            await SaveSubmitFailureAsync(qr, lastError);
            _logger.Error($"[Submit] FAIL QR={DescribeQr(qr)} Error={lastError}");
        }

        private async Task<TextBox?> WaitForEnabledTextBoxAsync(Window window, CancellationToken ct)
        {
            const int attempts = 15;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // 等待期间持续处理可能延迟出现的遗留弹窗。
                TryHandleModalWindow(window, "<pending>", "等待文本框");

                var element = window.FindFirstDescendant(cf =>
                    cf.ByAutomationId(FlaUIUitls.TARGET_TEXT_BOX_AUTOMATION_ID));
                if (element != null && element.IsEnabled)
                {
                    return element.AsTextBox();
                }

                await Task.Delay(200, ct);
            }

            return null;
        }

        private async Task<string?> WaitForAndHandleModalAsync(Window window, string qr, CancellationToken ct)
        {
            // 最多等待 1.5 秒，覆盖 HandyClient 异步弹窗晚于原 200ms 检查点的情况。
            const int attempts = 15;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var title = TryHandleModalWindow(window, qr, "提交后");
                if (title != null)
                {
                    return title;
                }
                await Task.Delay(100, ct);
            }
            return null;
        }

        private string? TryHandleModalWindow(Window window, string qr, string stage)
        {
            foreach (var modal in window.ModalWindows)
            {
                var title = modal.Title ?? string.Empty;
                _logger.Warn($"[Submit] {stage}检测到模态窗口 QR={DescribeQr(qr)} Title=\"{title}\"");

                if (title.Contains("错误") || title.Contains("警告"))
                {
                    var okButton = modal.FindFirstDescendant(cf =>
                        cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)
                            .And(cf.ByName("确定")))?.AsButton();
                    if (okButton != null && okButton.IsEnabled)
                    {
                        okButton.Invoke();
                        _logger.Warn($"[Submit] 已关闭模态窗口 QR={DescribeQr(qr)} Title=\"{title}\"");
                    }
                    else
                    {
                        _logger.Error($"[Submit] 模态窗口中未找到可用的“确定”按钮 QR={DescribeQr(qr)} Title=\"{title}\"");
                    }
                }
                else
                {
                    _logger.Error($"[Submit] 未识别的模态窗口，不执行盲目点击 QR={DescribeQr(qr)} Title=\"{title}\"");
                }

                return string.IsNullOrWhiteSpace(title) ? "<无标题>" : title;
            }

            return null;
        }

        private async Task SaveSubmitFailureAsync(string qr, string error)
        {
            try
            {
                if (_db == null)
                {
                    _logger.Error($"[Submit] 数据库未初始化，无法保存失败状态 QR={DescribeQr(qr)} Error={error}");
                    return;
                }

                await _db.MarkRecordFailedByQrAsync(qr, error);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[Submit] 保存失败状态异常 QR={DescribeQr(qr)} OriginalError={error}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
