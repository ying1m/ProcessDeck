using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ProcessDeck.App.Services;

/// <summary>
/// WebView2 面板与主程序之间的消息桥。
///
/// 为什么不用「监听 127.0.0.1 + token」的方案：
///   WebView2 自带 <c>postMessage</c> / <c>WebMessageReceived</c> 通道，
///   它走进程内消息，**根本不打开任何网络端口**，也不存在 token 泄漏、
///   被其他本地进程探测或 CSRF 的问题。安全性严格优于本地 HTTP 服务，
///   同时少一个需要维护的端口。
///
/// 线程模型：
///   Host 侧的状态变化可能来自任意线程（输出泵、探针任务、定时器），
///   而 WebView2 的调用必须在 UI 线程。这里用一个常驻 DispatcherTimer
///   轮询「脏标记」后统一推送，避免跨线程调用的各种坑。
/// </summary>
public sealed class PanelBridge : IDisposable
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>面板主文档的地址前缀，用于 IPC 来源校验。</summary>
    private const string TrustedPanelDocument = "https://processdeck.local/index.html";

    private readonly CoreWebView2 _core;
    private readonly DeckHostService _host;
    private readonly DispatcherTimer _pushTimer;

    private volatile bool _dirty = true;
    private bool _disposed;

    public PanelBridge(CoreWebView2 core, DeckHostService host, Dispatcher dispatcher)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        _host.SnapshotInvalidated += MarkDirty;
        _host.Notice += OnNotice;

        _core.WebMessageReceived += OnWebMessageReceived;

        _pushTimer = new DispatcherTimer(PushInterval, DispatcherPriority.Background, OnPushTick, dispatcher);
        _pushTimer.Start();
    }

    private void MarkDirty() => _dirty = true;

    private void OnPushTick(object? sender, EventArgs e)
    {
        if (!_dirty || _disposed)
        {
            return;
        }

        _dirty = false;

        try
        {
            var snapshot = _host.BuildSnapshot();
            var payload = new JsonObject
            {
                ["type"] = "snapshot",
                ["data"] = JsonSerializer.SerializeToNode(snapshot, DeckJson.Options),
            };

            _core.PostWebMessageAsJson(payload.ToJsonString());
        }
        catch (Exception ex)
        {
            // 面板可能正在导航或已关闭，不能让它把应用带崩。
            ShellLog.Write($"推送快照失败：{ex.Message}");
        }
    }

    private void OnNotice(string level, string message)
    {
        try
        {
            var payload = new JsonObject
            {
                ["type"] = "notice",
                ["level"] = level,
                ["message"] = message,
            };

            _core.PostWebMessageAsJson(payload.ToJsonString());
        }
        catch (Exception ex)
        {
            ShellLog.Write($"推送提示失败：{ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 必须校验来源，这不是可选项 —— 实测确认：
        // WebView2 会把 window.chrome.webview **一并暴露给沙箱 iframe**，
        // 而自定义卡片正是跑在 sandbox="allow-scripts" 的 iframe 里。
        // 若这里不拦，一个被别人分享的卡片就能绕过白名单 API，
        // 直接调用 stopApp / reloadConfig / savePreferences。
        string? source;

        try
        {
            source = e.Source;
        }
        catch (Exception ex)
        {
            // 来源地址本身可能读不出来（不透明源的框架上是常见情况），
            // 读不到就必须按不可信处理，而不能让它抛出去变成未处理异常 ——
            // 否则攻击被挡住了，日志里却什么都不留下。
            ShellLog.Write($"IPC 来源不可读，已按不可信拒绝：{ex.Message}");
            return;
        }

        if (!IsTrustedSource(source))
        {
            ShellLog.Write($"已拒绝来自非面板来源的 IPC 消息：{source}");
            return;
        }

        string json;

        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            ShellLog.Write($"读取面板消息失败：{ex.Message}");
            return;
        }

        _ = HandleMessageAsync(json);
    }

    /// <summary>
    /// 可信来源：面板主文档本身。
    /// 卡片 iframe 的地址前缀与 <see cref="TrustedPanelDocument"/> 不同，
    /// 沙箱框架的源更是不可读，都会在这里被挡下。
    /// </summary>
    private static bool IsTrustedSource(string? source)
        => !string.IsNullOrEmpty(source)
           && source.StartsWith(TrustedPanelDocument, StringComparison.OrdinalIgnoreCase);

    private async Task HandleMessageAsync(string json)
    {
        string type;
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
            type = root?["type"]?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex)
        {
            ShellLog.Write($"面板消息不是合法 JSON：{ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        // 面板发来的命令一律当作不可信输入：只按类型分派，不做任何动态求值。
        try
        {
            switch (type)
            {
                case "hello":
                    _dirty = true;
                    break;

                case "startApp":
                    await _host.StartAppAsync(RequireString(root, "appId")).ConfigureAwait(false);
                    break;

                case "stopApp":
                    await _host.StopAppAsync(RequireString(root, "appId")).ConfigureAwait(false);
                    break;

                case "restartApp":
                    await _host.RestartAppAsync(RequireString(root, "appId")).ConfigureAwait(false);
                    break;

                case "reloadConfig":
                    _host.Reload();
                    break;

                case "openConfigFolder":
                    _host.OpenConfigFolder();
                    break;

                case "saveApp":
                {
                    // 面板传来的定义一律当作不可信输入：先反序列化，
                    // 再走与手改 JSON 完全相同的校验路径（AppDefinition.Validate）。
                    var definition = root?["app"]
                        ?.Deserialize<ProcessDeck.Core.Configuration.AppDefinition>(DeckJson.Options);

                    if (definition is null)
                    {
                        throw new InvalidOperationException("saveApp 消息缺少 app 字段。");
                    }

                    _host.TrySaveApp(definition);
                    break;
                }

                case "deleteApp":
                    await _host.DeleteAppAsync(RequireString(root, "appId")).ConfigureAwait(false);
                    break;

                case "ensureConfigFile":
                    _host.EnsureConfigurationFile();
                    break;

                case "savePreferences":
                    _host.SavePreferences(
                        root?["theme"]?.GetValue<string>(),
                        root?["layout"]);
                    break;

                default:
                    ShellLog.Write($"收到未知的面板消息类型：{type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Write($"处理面板消息 {type} 失败：{ex}");
            OnNotice("error", $"操作失败：{ex.Message}");
        }
    }

    private static string RequireString(JsonNode? root, string property)
    {
        var value = root?[property]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"消息缺少必需字段 {property}。");
        }

        return value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pushTimer.Stop();
        _core.WebMessageReceived -= OnWebMessageReceived;
        _host.SnapshotInvalidated -= MarkDirty;
        _host.Notice -= OnNotice;
    }
}
