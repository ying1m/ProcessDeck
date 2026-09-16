using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ProcessDeck.Core.Configuration;
using ProcessDeck.Core.Net;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// 单个应用的生命周期管理器。
///
/// 它把底层三个能力（Job Object 进程树、ConPTY 终端、端口归属）组合成一个
/// UI 可以直接驱动的状态机，并负责三件容易做错的事：
///
///   1. <b>就绪判定</b>：进程存在 ≠ 服务可用。必须靠探针把 Starting 推到 Running。
///   2. <b>停止三态</b>：停止命令 → Ctrl+C → 关作业对象，逐级降级。
///      只做最后一段的工具，会经常丢掉应用的清理逻辑（数据文件损坏、端口未释放）。
///   3. <b>端口归属</b>：把裸 PID 变成「哪个应用占了端口」。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppSupervisor : IDisposable
{
    private static readonly HttpClient ProbeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private readonly object _gate = new();

    private SupervisedProcess? _process;
    private TerminalOutputBuffer? _output;
    private AppState _state = AppState.Stopped;
    private string? _lastError;
    private StopStage _lastStopStage = StopStage.None;
    private CancellationTokenSource? _lifetimeCts;
    private bool _disposed;

    public AppSupervisor(AppDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Definition.EnsureId();
    }

    public AppDefinition Definition { get; }

    public AppState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>最近一次失败原因；成功启动后清空。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>最近一次停止实际走到了哪一段。</summary>
    public StopStage LastStopStage
    {
        get
        {
            lock (_gate)
            {
                return _lastStopStage;
            }
        }
    }

    /// <summary>根进程 PID；未运行时为 null。</summary>
    public int? RootProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.ProcessId;
            }
        }
    }

    /// <summary>整棵进程树当前的进程数（含子孙）。这是「真的还在跑吗」的可信依据。</summary>
    public int ActiveProcessCount
    {
        get
        {
            SupervisedProcess? process;
            lock (_gate)
            {
                process = _process;
            }

            if (process is null)
            {
                return 0;
            }

            try
            {
                return process.ActiveProcessCount;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>作业内全部进程 PID。</summary>
    public IReadOnlyList<int> MemberProcessIds
    {
        get
        {
            SupervisedProcess? process;
            lock (_gate)
            {
                process = _process;
            }

            if (process is null)
            {
                return Array.Empty<int>();
            }

            try
            {
                return process.Job.MemberProcessIds;
            }
            catch (ObjectDisposedException)
            {
                return Array.Empty<int>();
            }
        }
    }

    /// <summary>
    /// 该应用当前真正占用的监听端口 → 占用者 PID。
    /// 判定依据是「监听者 PID 出现在本应用作业的成员列表里」，
    /// 不做父进程链回溯（那既慢又会被 PID 复用骗到）。
    /// </summary>
    public IReadOnlyDictionary<int, int> OccupiedPorts()
    {
        var candidates = new List<int>();

        if (Definition.Port is int mainPort)
        {
            candidates.Add(mainPort);
        }

        candidates.AddRange(Definition.ExtraPorts);

        if (candidates.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var members = MemberProcessIds;
        if (members.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var memberSet = members.ToHashSet();
        var result = new Dictionary<int, int>();

        foreach (var endpoint in PortScanner.GetListeningEndpoints())
        {
            if (candidates.Contains(endpoint.Port) && memberSet.Contains(endpoint.ProcessId))
            {
                result[endpoint.Port] = endpoint.ProcessId;
            }
        }

        return result;
    }

    /// <summary>终端输出快照。停止之后仍然可读，便于在 UI 上展示「为什么挂了」。</summary>
    public IReadOnlyList<string> LogTail
    {
        get
        {
            lock (_gate)
            {
                return _output?.Snapshot() ?? Array.Empty<string>();
            }
        }
    }

    /// <summary>状态或数据发生变化。可能在后台线程触发，UI 侧需自行调度。</summary>
    public event Action<AppSupervisor>? Changed;

    /// <summary>
    /// 启动应用。
    /// 会先做端口冲突预检，然后拉起进程、等待就绪探针通过。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_state is AppState.Starting or AppState.Running or AppState.Stopping)
            {
                throw new InvalidOperationException(
                    $"「{Definition.Name}」当前状态为 {_state}，不能重复启动。");
            }
        }

        var problems = Definition.Validate();
        if (problems.Count > 0)
        {
            Fail(string.Join(" ", problems));
            return;
        }

        _lastStopStage = StopStage.None;
        SetState(AppState.Starting);

        // ---- 端口冲突预检 ----
        if (Definition.Port is int port && PortScanner.FindListener(port) is { } occupant)
        {
            var occupantDescription = PortScanner.DescribeProcess(occupant.ProcessId) ?? $"PID {occupant.ProcessId}";
            Fail($"端口 {port} 已被 {occupantDescription} 占用，启动被拒绝。");
            return;
        }

        // ---- 拉起进程 ----
        SupervisedProcess process;
        try
        {
            var options = new LaunchOptions
            {
                CommandLine = Definition.StartCommand,
                WorkingDirectory = ResolveWorkingDirectory(),
                EnvironmentVariables = Definition.Environment,
                UsePseudoConsole = true,
                ConsoleColumns = 120,
                ConsoleRows = 30,
                DisplayName = $"processdeck-{Definition.Id}",
            };

            process = ProcessLauncher.Start(options);
        }
        catch (Exception ex)
        {
            Fail($"启动失败：{ex.Message}");
            return;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_gate)
        {
            _process = process;
            _output = process.Output;
            _lifetimeCts?.Dispose();
            _lifetimeCts = linkedCts;
        }

        RaiseChanged();

        // ---- 等待就绪 ----
        var ready = await WaitForReadyAsync(process, linkedCts.Token).ConfigureAwait(false);

        if (ready)
        {
            lock (_gate)
            {
                _lastError = null;
            }

            SetState(AppState.Running);
            return;
        }

        // 就绪失败：把已经拉起来的进程树收掉，避免留下一个「起了但没起来」的僵尸应用。
        var failure = LastError ?? "就绪判定失败。";
        ForceStop(process);
        Fail(failure);
    }

    /// <summary>
    /// 停止应用 —— 三态降级。
    ///
    ///   1) 执行应用自己的停止命令，给它做完清理的机会；
    ///   2) 往伪控制台送 Ctrl+C（等价于用户在终端按 Ctrl+C）；
    ///   3) 仍未退出则关闭作业对象句柄，由内核一次性回收整棵树。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SupervisedProcess? process;

        lock (_gate)
        {
            process = _process;

            if (process is null)
            {
                _lastStopStage = StopStage.AlreadyStopped;
                SetStateLocked(AppState.Stopped);
            }
            else if (_state == AppState.Stopping)
            {
                return;
            }
        }

        if (process is null)
        {
            RaiseChanged();
            return;
        }

        SetState(AppState.Stopping);

        var totalTimeout = TimeSpan.FromSeconds(Math.Clamp(Definition.StopTimeoutSeconds, 1, 600));

        // ---- 第 1 段：优雅停止命令 ----
        if (!string.IsNullOrWhiteSpace(Definition.StopCommand))
        {
            RunStopCommand();

            if (await WaitForTreeExitAsync(process, totalTimeout, cancellationToken).ConfigureAwait(false))
            {
                Finish(process, StopStage.StopCommand);
                return;
            }

            AppendNotice($"停止命令在 {totalTimeout.TotalSeconds:F0} 秒内没能结束进程，继续降级。");
        }

        // ---- 第 2 段：Ctrl+C ----
        if (process.HasPseudoConsole)
        {
            try
            {
                process.WriteInput("\u0003");
            }
            catch (Exception ex)
            {
                AppendNotice($"发送 Ctrl+C 失败：{ex.Message}");
            }

            var interruptTimeout = TimeSpan.FromSeconds(Math.Min(5, totalTimeout.TotalSeconds));
            if (await WaitForTreeExitAsync(process, interruptTimeout, cancellationToken).ConfigureAwait(false))
            {
                Finish(process, StopStage.Interrupt);
                return;
            }
        }

        // ---- 第 3 段：强杀 ----
        AppendNotice("进程未响应停止请求，强制回收整棵进程树。");
        Finish(process, StopStage.ForceKill);
    }

    /// <summary>停止并释放全部资源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SupervisedProcess? process;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            process = _process;
            _process = null;
            cts = _lifetimeCts;
            _lifetimeCts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略
        }

        cts?.Dispose();

        // SupervisedProcess.Dispose 会关闭作业句柄 → 内核回收整棵进程树。
        process?.Dispose();
    }

    // ------------------------------------------------------------------
    // 就绪判定
    // ------------------------------------------------------------------

    private async Task<bool> WaitForReadyAsync(SupervisedProcess process, CancellationToken cancellationToken)
    {
        var probe = Definition.Readiness ?? new ReadinessProbeDefinition();

        // 无探针：进程存活一小段时间即认为就绪。
        if (probe.Kind == ReadinessKind.None)
        {
            var grace = TimeSpan.FromSeconds(Math.Clamp(Definition.StartGraceSeconds, 0, 120));
            if (grace > TimeSpan.Zero)
            {
                await Task.Delay(grace, cancellationToken).ConfigureAwait(false);
            }

            if (IsTreeGone(process))
            {
                SetError(BuildEarlyExitMessage(process));
                return false;
            }

            return true;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(probe.TimeoutSeconds, 1, 1800));
        var interval = Math.Clamp(probe.IntervalMs, 50, 5000);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                SetError("启动被取消。");
                return false;
            }

            if (IsTreeGone(process))
            {
                SetError(BuildEarlyExitMessage(process));
                return false;
            }

            if (await IsSatisfiedAsync(probe, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        SetError($"等待就绪超时（{timeout.TotalSeconds:F0} 秒，探针类型 {probe.Kind}）。");
        return false;
    }

    private async Task<bool> IsSatisfiedAsync(ReadinessProbeDefinition probe, CancellationToken cancellationToken)
    {
        switch (probe.Kind)
        {
            case ReadinessKind.Port:
                return Definition.Port is int port && OccupiedPorts().ContainsKey(port);

            case ReadinessKind.Http:
            {
                if (string.IsNullOrWhiteSpace(probe.Url))
                {
                    return false;
                }

                try
                {
                    using var response = await ProbeHttpClient
                        .GetAsync(probe.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    var status = (int)response.StatusCode;
                    return status >= probe.HttpSuccessFrom && status <= probe.HttpSuccessTo;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
            }

            case ReadinessKind.Log:
            {
                if (string.IsNullOrWhiteSpace(probe.Pattern))
                {
                    return false;
                }

                var text = string.Join('\n', LogTail);

                try
                {
                    return Regex.IsMatch(text, probe.Pattern, RegexOptions.Multiline);
                }
                catch (ArgumentException)
                {
                    // 用户写的正则非法：不能让它把启动流程卡死，视为未就绪并给出提示。
                    SetError($"就绪探针正则非法：{probe.Pattern}");
                    return false;
                }
            }

            default:
                return true;
        }
    }

    private string BuildEarlyExitMessage(SupervisedProcess process)
    {
        var exitCode = process.RootExitCode;
        var tail = LogTail;

        var message = exitCode is null
            ? "进程在就绪前就退出了。"
            : $"进程在就绪前退出，退出码 {exitCode}。";

        if (tail.Count > 0)
        {
            var lastLines = tail.TakeLast(5).Where(l => !string.IsNullOrWhiteSpace(l));
            var preview = string.Join(" / ", lastLines);
            if (preview.Length > 0)
            {
                message += $" 最后输出：{preview}";
            }
        }

        return message;
    }

    // ------------------------------------------------------------------
    // 停止辅助
    // ------------------------------------------------------------------

    private void RunStopCommand()
    {
        try
        {
            using var stopper = ProcessLauncher.Start(new LaunchOptions
            {
                CommandLine = Definition.StopCommand!,
                WorkingDirectory = ResolveWorkingDirectory(),
                EnvironmentVariables = Definition.Environment,
                CreateNewConsole = false,
                NoWindow = true,
                DisplayName = $"processdeck-{Definition.Id}-stop",
            });

            // 停止命令本身也可能挂住，最多等 10 秒，然后连它的进程树一起收掉。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (stopper.ActiveProcessCount == 0)
                {
                    break;
                }

                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            AppendNotice($"停止命令执行失败：{ex.Message}");
        }
    }

    private static async Task<bool> WaitForTreeExitAsync(
        SupervisedProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (IsTreeGone(process))
            {
                return true;
            }

            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
        }

        return IsTreeGone(process);
    }

    private static bool IsTreeGone(SupervisedProcess process)
    {
        try
        {
            return process.ActiveProcessCount == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static void ForceStop(SupervisedProcess process)
    {
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
            // 收尾失败不影响状态收敛。
        }
    }

    private void Finish(SupervisedProcess process, StopStage stage)
    {
        CancellationTokenSource? cts;

        lock (_gate)
        {
            _process = null;
            _lastStopStage = stage;
            _lastError = null;
            cts = _lifetimeCts;
            _lifetimeCts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略
        }

        cts?.Dispose();

        // 关作业句柄 → 整棵树由内核回收；ConPTY 会话与输出泵一并收尾。
        process.Dispose();

        // 注意：_output 刻意保留，停止后 UI 仍能显示最后一次运行的日志。
        SetState(AppState.Stopped);
    }

    // ------------------------------------------------------------------
    // 状态与通知
    // ------------------------------------------------------------------

    private string? ResolveWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(Definition.WorkingDirectory))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(Definition.WorkingDirectory);

        if (!Directory.Exists(expanded))
        {
            AppendNotice($"工作目录不存在，已忽略：{expanded}");
            return null;
        }

        return expanded;
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }

        SetState(AppState.Failed);
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    private void SetState(AppState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        RaiseChanged();
    }

    private void SetStateLocked(AppState state)
    {
        _state = state;
    }

    private void RaiseChanged() => Changed?.Invoke(this);

    private void AppendNotice(string message)
    {
        lock (_gate)
        {
            _output?.AppendText($"{Environment.NewLine}[ProcessDeck] {message}");
        }

        RaiseChanged();
    }
}
