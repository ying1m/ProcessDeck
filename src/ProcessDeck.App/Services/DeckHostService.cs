using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProcessDeck.Core;
using ProcessDeck.Core.Cards;
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

    /// <summary>
    /// 完整的应用定义。面板的编辑表单需要它来预填字段。
    /// 刻意随快照一起下发而不是另开查询接口 —— 定义只有几百字节，省一次往返更划算。
    /// </summary>
    public AppDefinition? Definition { get; init; }
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

    /// <summary>可用卡片（内置 + 用户自定义）。</summary>
    public IReadOnlyList<CardDescriptor> Cards { get; init; } = Array.Empty<CardDescriptor>();

    /// <summary>卡片发现过程中的问题，用于提示用户而不是静默失败。</summary>
    public IReadOnlyList<string> CardProblems { get; init; } = Array.Empty<string>();
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

    private IReadOnlyList<CardDescriptor> _cards = Array.Empty<CardDescriptor>();
    private IReadOnlyList<string> _cardProblems = Array.Empty<string>();

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

            RefreshCards();

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
        IReadOnlyList<CardDescriptor> cards;
        IReadOnlyList<string> cardProblems;

        lock (_gate)
        {
            supervisors = _supervisors.ToList();
            configuration = Configuration;
            loadError = LoadError;
            cards = _cards;
            cardProblems = _cardProblems;
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
                Definition = definition,
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
            Cards = cards,
            CardProblems = cardProblems,
        };
    }

    /// <summary>
    /// 扫描内置与用户卡片目录。
    ///
    /// 卡片代码永远不进入宿主进程：这里只读清单、校验路径、拼出沙箱 iframe 的地址。
    /// </summary>
    private void RefreshCards()
    {
        var roots = new[]
        {
            new CardRoot(
                Path.Combine(AppContext.BaseDirectory, "wwwroot", "cards"),
                "https://processdeck.local/cards",
                "builtin",
                "内置"),

            // 用户卡片放在数据目录下，映射到第二个虚拟主机。
            new CardRoot(
                DeckPaths.UserCardsDirectory,
                "https://processdeck-cards.local",
                "user",
                "用户"),
        };

        _cards = CardCatalog.Discover(roots, out var problems);
        _cardProblems = problems;
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

    /// <summary>
    /// 新增或更新一个应用定义。
    ///
    /// 运行中的定义**不允许就地修改**：<see cref="AppSupervisor"/> 持有的是定义对象引用，
    /// 直接替换会让界面与实际运行的进程脱节（命令改了、进程还是旧的，
    /// 用户会误以为改动已生效）。要求先停止，语义才明确。
    /// </summary>
    public bool TrySaveApp(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = definition.Validate();
        if (problems.Count > 0)
        {
            RaiseNotice("error", string.Join(" ", problems));
            return false;
        }

        definition.EnsureId();

        AppSupervisor? existing;

        lock (_gate)
        {
            existing = _supervisors.FirstOrDefault(s =>
                string.Equals(s.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is not null && existing.State is AppState.Starting or AppState.Running or AppState.Stopping)
        {
            RaiseNotice("error", $"「{existing.Definition.Name}」正在运行，请先停止再修改。");
            return false;
        }

        lock (_gate)
        {
            var index = Configuration.Apps.FindIndex(a =>
                string.Equals(a.Id, definition.Id, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                Configuration.Apps[index] = definition;
            }
            else
            {
                Configuration.Apps.Add(definition);
            }

            try
            {
                _store.Save(Configuration);
            }
            catch (Exception ex)
            {
                RaiseNotice("error", $"保存配置失败：{ex.Message}");
                return false;
            }

            if (existing is not null)
            {
                existing.Changed -= OnSupervisorChanged;
                existing.Dispose();
                _supervisors.Remove(existing);
            }

            var supervisor = new AppSupervisor(definition);
            supervisor.Changed += OnSupervisorChanged;
            _supervisors.Add(supervisor);
        }

        RaiseNotice("info", $"已保存「{definition.Name}」。");
        RaiseInvalidated();
        return true;
    }

    /// <summary>
    /// 删除一个应用。若它正在运行，会先走完整的停止流程再移除 ——
    /// 否则会留下一个没人管、却还占着端口的进程。
    /// </summary>
    public async Task<bool> DeleteAppAsync(string appId)
    {
        AppSupervisor? existing;

        lock (_gate)
        {
            existing = _supervisors.FirstOrDefault(s =>
                string.Equals(s.Definition.Id, appId, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
        {
            RaiseNotice("error", $"找不到应用：{appId}");
            return false;
        }

        if (existing.State is not AppState.Stopped and not AppState.Failed)
        {
            await existing.StopAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            existing.Changed -= OnSupervisorChanged;
            existing.Dispose();
            _supervisors.Remove(existing);

            Configuration.Apps.RemoveAll(a =>
                string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

            try
            {
                _store.Save(Configuration);
            }
            catch (Exception ex)
            {
                RaiseNotice("error", $"保存配置失败：{ex.Message}");
                return false;
            }
        }

        RaiseNotice("info", $"已删除「{existing.Definition.Name}」。");
        RaiseInvalidated();
        return true;
    }

    /// <summary>在资源管理器里打开配置目录，方便用户直接手改 JSON。</summary>
    public void OpenConfigFolder()
    {
        var directory = Path.GetDirectoryName(_store.FilePath);

        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            RaiseNotice("error", $"打开配置目录失败：{ex.Message}");
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
