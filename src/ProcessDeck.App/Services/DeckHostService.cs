using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProcessDeck.Core.Configuration;
using ProcessDeck.Core.Supervision;

namespace ProcessDeck.App.Services;

/// <summary>面板上单张卡片需要的数据。</summary>
public sealed record AppSnapshot
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Group { get; init; }

    /// <summary>stopped / starting / running / stopping / failed</summary>
    public required string State { get; init; }

    public int? Port { get; init; }

    public int? Pid { get; init; }

    /// <summary>整棵进程树当前进程数。</summary>
    public int Processes { get; init; }

    /// <summary>实际探测到的「本应用占用的端口 → 占用者 PID」。</summary>
    public IReadOnlyDictionary<int, int> Ports { get; init; } = new Dictionary<int, int>();

    public string? Error { get; init; }

    public string? StopStage { get; init; }

    public bool HasConsole { get; init; }

    public required string StartCommand { get; init; }

    public string? StopCommand { get; init; }

    public string? Accent { get; init; }

    /// <summary>最近若干行终端输出（已剥离 ANSI）。</summary>
    public required IReadOnlyList<string> LogTail { get; init; }
}

/// <summary>推给面板的完整快照。</summary>
public sealed record DeckSnapshot
{
    public required IReadOnlyList<AppSnapshot> Apps { get; init; }

    public required string Theme { get; init; }

    public JsonNode? Layout { get; init; }

    public required string ConfigurationPath { get; init; }

    public string? LoadError { get; init; }

    /// <summary>引擎自述，显示在状态栏。</summary>
    public required string EngineState { get; init; }
}

/// <summary>
/// 面板的数据与控制中枢。
///
/// 职责：持有配置与全部 <see cref="AppSupervisor"/>，把引擎状态整理成可直接渲染的快照，
/// 并暴露面板能发起的动作。它不碰任何 WPF/WebView2 类型 ——
/// 这样引擎逻辑可以脱离界面单独测试。
/// </summary>
public sealed class DeckHostService : IDisposable
{
    /// <summary>每张卡片最多推多少行日志。太多会把 IPC 变成瓶颈。</summary>
    private const int LogTailLines = 30;

    private readonly ConfigurationStore _store;
    private readonly List<AppSupervisor> _supervisors = new();
    private readonly object _gate = new();
    private readonly Timer _portWatchTimer;

    private bool _disposed;

    public DeckHostService(ConfigurationStore? store = null)
    {
        _store = store ?? new ConfigurationStore();
        Configuration = new DeckConfiguration();

        Reload();

        // 外部因素也会改变端口占用（别的程序抢了、应用自己退了），
        // 所以需要定期让面板重新取一次快照，而不是只在状态机变化时刷新。
        _portWatchTimer = new Timer(
            _ => RaiseInvalidated(),
            state: null,
            dueTime: TimeSpan.FromSeconds(3),
            period: TimeSpan.FromSeconds(3));
    }

    public DeckConfiguration Configuration { get; private set; }

    public string ConfigurationPath => _store.FilePath;

    public string? LoadError { get; private set; }

    /// <summary>快照需要重推。可能在任意线程触发。</summary>
    public event Action? SnapshotInvalidated;

    /// <summary>需要提示用户的消息（level, message）。</summary>
    public event Action<string, string>? Notice;

    /// <summary>从磁盘重新读取配置，并重建全部托管对象。</summary>
    public void Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            foreach (var supervisor in _supervisors)
            {
                supervisor.Changed -= OnSupervisorChanged;
                supervisor.Dispose();
            }

            _supervisors.Clear();

            Configuration = _store.Load(out var error);
            LoadError = error;

            foreach (var definition in Configuration.Apps)
            {
                var supervisor = new AppSupervisor(definition);
                supervisor.Changed += OnSupervisorChanged;
                _supervisors.Add(supervisor);
            }
        }

        RaiseInvalidated();
    }

    /// <summary>构造当前完整快照。</summary>
    public DeckSnapshot BuildSnapshot()
    {
        List<AppSupervisor> supervisors;
        DeckConfiguration configuration;
        string? loadError;

        lock (_gate)
        {
            supervisors = _supervisors.ToList();
            configuration = Configuration;
            loadError = LoadError;
        }

        var apps = new List<AppSnapshot>(supervisors.Count);

        foreach (var supervisor in supervisors)
        {
            var definition = supervisor.Definition;

            IReadOnlyDictionary<int, int> ports;
            try
            {
                ports = supervisor.OccupiedPorts();
            }
            catch (Exception)
            {
                ports = new Dictionary<int, int>();
            }

            var logTail = supervisor.LogTail;
            if (logTail.Count > LogTailLines)
            {
                logTail = logTail.Skip(logTail.Count - LogTailLines).ToArray();
            }

            apps.Add(new AppSnapshot
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description,
                Group = definition.Group,
                State = supervisor.State.ToString().ToLowerInvariant(),
                Port = definition.Port,
                Pid = supervisor.RootProcessId,
                Processes = supervisor.ActiveProcessCount,
                Ports = ports,
                Error = supervisor.LastError,
                StopStage = supervisor.LastStopStage == StopStage.None
                    ? null
                    : supervisor.LastStopStage.ToString(),
                HasConsole = supervisor.Definition.Readiness is not null,
                StartCommand = definition.StartCommand,
                StopCommand = definition.StopCommand,
                Accent = definition.Accent,
                LogTail = logTail,
            });
        }

        var running = apps.Count(a => a.State == "running");
        var failed = apps.Count(a => a.State == "failed");

        return new DeckSnapshot
        {
            Apps = apps,
            Theme = configuration.Theme,
            Layout = configuration.Layout,
            ConfigurationPath = _store.FilePath,
            LoadError = loadError,
            EngineState = $"共 {apps.Count} 个应用 · 运行中 {running} · 异常 {failed}",
        };
    }

    // ------------------------------------------------------------------
    // 面板动作
    // ------------------------------------------------------------------

    public async Task StartAppAsync(string appId)
    {
        var supervisor = Find(appId);
        if (supervisor is null)
        {
            RaiseNotice("error", $"找不到应用：{appId}");
            return;
        }

        try
        {
            await supervisor.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseNotice("error", $"启动「{supervisor.Definition.Name}」失败：{ex.Message}");
        }
    }

    public async Task StopAppAsync(string appId)
    {
        var supervisor = Find(appId);
        if (supervisor is null)
        {
            RaiseNotice("error", $"找不到应用：{appId}");
            return;
        }

        try
        {
            await supervisor.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseNotice("error", $"停止「{supervisor.Definition.Name}」失败：{ex.Message}");
        }
    }

    public async Task RestartAppAsync(string appId)
    {
        await StopAppAsync(appId).ConfigureAwait(false);
        await StartAppAsync(appId).ConfigureAwait(false);
    }

    /// <summary>持久化前端拥有的偏好（主题、布局）。</summary>
    public void SavePreferences(string? theme, JsonNode? layout)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(theme))
            {
                Configuration.Theme = theme;
            }

            if (layout is not null)
            {
                Configuration.Layout = layout as JsonObject ?? Configuration.Layout;
            }

            try
            {
                _store.Save(Configuration);
            }
            catch (Exception ex)
            {
                RaiseNotice("error", $"保存配置失败：{ex.Message}");
                return;
            }
        }

        RaiseInvalidated();
    }

    /// <summary>把当前配置写回磁盘（首次运行时用于生成带注释的模板）。</summary>
    public void EnsureConfigurationFile()
    {
        if (_store.Exists())
        {
            return;
        }

        try
        {
            _store.Save(Configuration);
        }
        catch (Exception ex)
        {
            RaiseNotice("error", $"创建配置文件失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 拉起所有配置了 autoStart 的应用。
    /// 刻意做成「不等待」：某个应用启动慢或起不来，不应该拖住界面显示。
    /// </summary>
    public void StartAutoStartApps()
    {
        List<string> ids;

        lock (_gate)
        {
            ids = _supervisors
                .Where(s => s.Definition.AutoStart)
                .Select(s => s.Definition.Id)
                .ToList();
        }

        foreach (var id in ids)
        {
            _ = StartAppAsync(id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _portWatchTimer.Dispose();

        lock (_gate)
        {
            foreach (var supervisor in _supervisors)
            {
                supervisor.Changed -= OnSupervisorChanged;
                supervisor.Dispose();
            }

            _supervisors.Clear();
        }
    }

    private AppSupervisor? Find(string appId)
    {
        lock (_gate)
        {
            return _supervisors.FirstOrDefault(s =>
                string.Equals(s.Definition.Id, appId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnSupervisorChanged(AppSupervisor supervisor) => RaiseInvalidated();

    private void RaiseInvalidated() => SnapshotInvalidated?.Invoke();

    private void RaiseNotice(string level, string message) => Notice?.Invoke(level, message);
}

/// <summary>IPC 消息的 JSON 序列化选项（与面板约定的 camelCase 一致）。</summary>
public static class DeckJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
