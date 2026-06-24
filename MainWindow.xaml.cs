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
            var directoryToWatch = FolderTextBox.Text;

            Task.Run(async () =>
            {
                var isReady = FlaUIUitls.DetectWindow();
                OnStatusChanged(isReady);
                if (isReady)
                {
                    _automate = new Automate(directoryToWatch);
                    await _automate.Start();

                    this.Dispatcher.Invoke(() =>
                    {
                        StartButton.IsEnabled = false;
                        BrowseButton.IsEnabled = false;
                    });
                }
            });

            // 使用当前选中的文件夹路径
            string targetDirectory = FolderTextBox.Text;

            // 20秒后启动日志写入线程
            Task.Delay(1000).ContinueWith(_ =>
            {
                Task.Run(() =>
                {
                    LogWriterTest logWriter = new LogWriterTest(targetDirectory);
                    for(var i = 0; i < 10; i++)
                    {
                        try
                        {
                            logWriter.WriteTestLog();
                            _logger.Info($"测试日志已成功写入到: {targetDirectory}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"写入测试日志时发生错误: {ex.Message}");
                        }
                    }
                });
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
                if (!isReady)
                {
                    _automate?.Stop();
                }
                await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}