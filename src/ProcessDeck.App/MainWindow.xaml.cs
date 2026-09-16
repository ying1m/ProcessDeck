using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using ProcessDeck.App.Services;

namespace ProcessDeck.App;

/// <summary>
/// 主窗口：WebView2 面板宿主 + 托盘生命周期 + 引擎装配。
/// </summary>
public partial class MainWindow : Window
{
    private readonly TrayIconService _tray;

    private DeckHostService? _host;
    private PanelBridge? _bridge;
    private bool _exitingForReal;

    public MainWindow()
    {
        InitializeComponent();

        // 默认尺寸不能超过当前屏幕的可用工作区，否则在小屏幕或高缩放下
        // 窗口一开就比屏幕大，底部内容直接看不见。
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width - 40);
        Height = Math.Min(Height, work.Height - 40);

        _tray = new TrayIconService();
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;

        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += (_, _) => LogGeometry("size-changed");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShellLog.Reset();
        ShellLog.Write("=== ProcessDeck shell 启动 ===");
        ShellLog.Write($"屏幕 可用工作区(dip) = {SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0}");
        LogGeometry("加载完成，初始化 WebView2 之前");

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcessDeck",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Panel.EnsureCoreWebView2Async(environment);
            LogGeometry("EnsureCoreWebView2Async 之后");

            // 把 wwwroot 映射为一个虚拟 HTTPS 源。
            // 既避开 file:// 的一堆限制，又让面板拥有稳定 origin ——
            // 后续做 CSP 和「自定义卡片沙箱」都依赖这一点。
            Panel.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "processdeck.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            Panel.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Panel.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Panel.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // 引擎与桥必须在导航之前装好，否则面板发出的第一条 hello 会丢。
            _host = new DeckHostService();
            _bridge = new PanelBridge(Panel.CoreWebView2, _host, Dispatcher);

            ShellLog.Write($"配置文件：{_host.ConfigurationPath}");

            if (_host.LoadError is { } loadError)
            {
                ShellLog.Write($"配置加载有问题：{loadError}");
            }

            Panel.CoreWebView2.Navigate("https://processdeck.local/index.html");
            ShellLog.Write("已导航到 https://processdeck.local/index.html");

            // 导航之后再拉起自动启动的应用，保证面板能收到它们的状态变化。
            _host.StartAutoStartApps();
        }
        catch (Exception ex)
        {
            ShellLog.Write($"WebView2 初始化失败: {ex}");
            MessageBox.Show(
                $"WebView2 初始化失败：\n\n{ex.Message}\n\n" +
                "通常是因为缺少 WebView2 运行时（Win11 与较新的 Win10 自带）。",
                "ProcessDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 把窗口/控件/DIP 三者的真实几何关系写进日志。
    /// 三者一旦不自洽（例如控件比窗口还大），面板下半部分就会被推到屏幕外。
    /// </summary>
    private void LogGeometry(string phase)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            ShellLog.Write(
                $"{phase}: window={ActualWidth:F0}x{ActualHeight:F0} dip | " +
                $"panel={Panel.ActualWidth:F0}x{Panel.ActualHeight:F0} dip | " +
                $"pixelsPerDip={dpi.PixelsPerDip:F2} dpiScale={dpi.DpiScaleX:F2}");
        }
        catch (Exception ex)
        {
            ShellLog.Write($"{phase}: 几何记录失败 {ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ExitApplication()
    {
        _exitingForReal = true;
        Close();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitingForReal)
        {
            // 关窗口 = 收进托盘，进程继续常驻，被管理的应用不受影响。
            e.Cancel = true;
            Hide();
            return;
        }

        // 真正退出：必须先停掉所有受管应用，否则它们会变成没有任何人管、
        // 还占着端口的孤儿进程 —— 这正是 ProcessDeck 要消灭的问题。
        _bridge?.Dispose();
        _host?.Dispose();
        _tray.Dispose();
    }
}
