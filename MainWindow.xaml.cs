using NLog;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;

namespace LCPSAutomate
{
    public partial class MainWindow : Window
    {   
        private Automate? _automate;
        private System.Timers.Timer? _timer;
        private CancellationTokenSource _cts;
        private int _consecutiveWindowDetectionFailures;
        private int _isStarting;
        private const int WindowDetectionFailureThreshold = 3;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public MainWindow()
        {
            InitializeComponent();
            // 默认将监视目录设置为 文档 文件夹
            FolderTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Loaded += MainWindow_Loaded;
            _cts = new CancellationTokenSource();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                var isReady = FlaUIUitls.DetectWindow();
                OnStatusChanged(isReady);
            });
            Task.Run(() => MonitorWindowLoopAsync(_cts.Token), _cts.Token);
            _logger.Info("应用程序已启动");
            
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FolderTextBox.Text))
            {
                System.Windows.MessageBox.Show("未选择路径");
                return;
            }

            // 启动期间立即加门闩，避免用户连续点击创建多个 Automate 实例并发操作 HandyClient。
            if (_automate != null || Interlocked.CompareExchange(ref _isStarting, 1, 0) != 0)
            {
                return;
            }

            var directoryToWatch = FolderTextBox.Text;
            StartButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;

            Task.Run(async () =>
            {
                var started = false;
                try
                {
                    var isReady = FlaUIUitls.DetectWindow();
                    OnStatusChanged(isReady);
                    if (!isReady)
                    {
                        return;
                    }

                    var automate = new Automate(directoryToWatch);
                    await automate.Start();
                    _automate = automate;
                    _consecutiveWindowDetectionFailures = 0;
                    started = true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "启动自动化失败");
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = "启动失败，请查看错误日志。";
                        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref _isStarting, 0);
                    if (!started)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StartButton.IsEnabled = true;
                            BrowseButton.IsEnabled = true;
                        });
                    }
                }
            });
        }


        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _automate?.Stop();
            _automate = null;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            StatusTextBlock.Text = "已停止监测。";
            StartButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "请选择要监视的文件夹";
            dlg.SelectedPath = FolderTextBox.Text;
            dlg.ShowNewFolderButton = true;
            var res = dlg.ShowDialog();
            if (res == System.Windows.Forms.DialogResult.OK || res == System.Windows.Forms.DialogResult.Yes)
            {
                FolderTextBox.Text = dlg.SelectedPath;
            }
        }

        private void OnStatusChanged(bool status)
        {
            Dispatcher.Invoke(() =>
            {
                if (status)
                {
                    StatusTextBlock.Text = "目标应用程序已就绪";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                else
                {
                    StatusTextBlock.Text = "目标应用程序未就绪";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                }
            });
        }

        private async Task MonitorWindowLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var isReady = FlaUIUitls.DetectWindow();
                OnStatusChanged(isReady);
                if (isReady)
                {
                    _consecutiveWindowDetectionFailures = 0;
                }
                else
                {
                    var failures = Interlocked.Increment(ref _consecutiveWindowDetectionFailures);
                    _logger.Warn($"目标窗口连续检测失败 {failures}/{WindowDetectionFailureThreshold}");
                    if (failures >= WindowDetectionFailureThreshold && _automate != null)
                    {
                        _logger.Error("目标窗口连续检测失败达到阈值，停止自动化");
                        var automate = _automate;
                        _automate = null;
                        automate.Stop();
                        Dispatcher.Invoke(() =>
                        {
                            StartButton.IsEnabled = true;
                            BrowseButton.IsEnabled = true;
                        });
                    }
                }

                try { await Task.Delay(1000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }
    }
}