using System.Threading;
using System.Windows;

namespace ProcessDeck.App;

/// <summary>
/// 应用入口。负责单实例约束与主窗口创建。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\ProcessDeck.SingleInstance.v1";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：托盘应用若被重复启动，会出现多个图标且各自持有一份进程状态，必须避免。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // TODO: 后续改为通过命名管道通知已有实例把窗口唤到前台，而不是弹框。
            MessageBox.Show(
                "ProcessDeck 已经在运行了（看任务栏右下角托盘图标）。",
                "ProcessDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new ProcessDeck.App.MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
