using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Supervision;

/// <summary>启动一个受管进程所需的参数。</summary>
public sealed record LaunchOptions
{
    /// <summary>
    /// 完整命令行。与 <see cref="System.Diagnostics.ProcessStartInfo"/> 不同，
    /// 这里原样交给 CreateProcess，不做任何转义改写 ——
    /// PowerShell 的命令里大量含有引号/管道/分号，二次转义是最常见的翻车点。
    /// </summary>
    public required string CommandLine { get; init; }

    /// <summary>工作目录。null 表示继承当前目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// 追加或覆盖的环境变量。为空则完全继承当前进程环境。
    /// 注意实现上会基于当前环境构造完整环境块，而不是只传增量 ——
    /// 只传增量会让子进程丢掉 PATH 等关键变量。
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>是否分配一个独立控制台窗口。</summary>
    public bool CreateNewConsole { get; init; } = true;

    /// <summary>是否完全无窗口启动。与 <see cref="CreateNewConsole"/> 互斥。</summary>
    public bool NoWindow { get; init; }

    /// <summary>
    /// 是否用 ConPTY 伪控制台承载进程。
    ///
    /// 打开后子进程看到一个真实终端（<c>stdout.isatty() == true</c>），
    /// 输出可从 <see cref="SupervisedProcess.Output"/> 读取。
    /// 此时 <see cref="CreateNewConsole"/> 与 <see cref="NoWindow"/> 会被忽略。
    /// </summary>
    public bool UsePseudoConsole { get; init; }

    /// <summary>伪控制台初始列数。</summary>
    public short ConsoleColumns { get; init; } = 120;

    /// <summary>伪控制台初始行数。</summary>
    public short ConsoleRows { get; init; } = 30;

    /// <summary>可选诊断名，仅用于作业对象标识。</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// 一个处于作业对象托管下的进程。
///
/// 生命周期语义：<see cref="Dispose"/> 会关闭作业句柄，
/// 由于作业设置了 KILL_ON_JOB_CLOSE，内核将终止该进程及其**全部后代**。
/// 调用方无需（也不应该）再去遍历进程树逐个杀。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisedProcess : IDisposable
{
    private readonly SafeProcessHandle _processHandle;
    private readonly ConPtySession? _console;
    private readonly Thread? _outputPump;
    private bool _disposed;

    internal SupervisedProcess(int processId, SafeProcessHandle processHandle, JobObject job, ConPtySession? console)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        Job = job;
        _console = console;
        Output = new TerminalOutputBuffer();

        if (console is not null)
        {
            _outputPump = new Thread(PumpConsoleOutput)
            {
                IsBackground = true,
                Name = $"ProcessDeck-pty-pump-{processId}",
            };
            _outputPump.Start();
        }
    }

    /// <summary>根进程 PID。注意：真正的「进程组」由 <see cref="Job"/> 表示，不是这个 PID。</summary>
    public int ProcessId { get; }

    /// <summary>托管整棵进程树的作业对象。</summary>
    public JobObject Job { get; }

    /// <summary>终端输出缓冲。始终非 null；未启用伪控制台时永远为空。</summary>
    public TerminalOutputBuffer Output { get; }

    /// <summary>是否使用伪控制台承载。</summary>
    public bool HasPseudoConsole => _console is not null;

    /// <summary>作业内当前存活进程数（含根进程与它派生出的所有子孙）。</summary>
    public int ActiveProcessCount => Job.ActiveProcessCount;

    /// <summary>曾进入作业的进程总数，用于诊断「一条命令炸出多少子进程」。</summary>
    public int TotalProcessCount => Job.TotalProcessCount;

    /// <summary>根进程是否已退出。注意：根退出不代表树空了，仍需看 <see cref="ActiveProcessCount"/>。</summary>
    public bool HasRootExited
    {
        get
        {
            if (_disposed)
            {
                return true;
            }

            return !NativeMethods.GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var code)
                   || code != NativeMethods.STILL_ACTIVE;
        }
    }

    /// <summary>根进程退出码；仍在运行或无法读取时返回 null。</summary>
    public int? RootExitCode
    {
        get
        {
            if (_disposed)
            {
                return null;
            }

            if (!NativeMethods.GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var code)
                || code == NativeMethods.STILL_ACTIVE)
            {
                return null;
            }

            return unchecked((int)code);
        }
    }

    /// <summary>把文本送入伪控制台，相当于用户在终端里敲字（可用于回答交互式提示）。</summary>
    public void WriteInput(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_console is null)
        {
            throw new InvalidOperationException(
                "该进程未使用伪控制台，无法写入输入。请在 LaunchOptions 中设置 UsePseudoConsole = true。");
        }

        _console.Write(text);
    }

    /// <summary>调整伪控制台尺寸，避免子进程按旧宽度折行导致日志错位。</summary>
    public void ResizeConsole(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _console?.Resize(columns, rows);
    }

    /// <summary>
    /// 关闭作业句柄 → 内核回收整棵进程树。
    /// 这是「硬停」：不等待、不给机会做清理。优雅停止应由上层先发停止命令。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 1) 先收进程树：ConPTY 的输出管道会随之关闭，泵线程得以自然结束。
        Job.Dispose();

        _outputPump?.Join(TimeSpan.FromMilliseconds(1500));

        // 2) 再拆伪控制台。
        _console?.Dispose();

        // 泵线程可能仍阻塞在 Read 上，再给一次机会。
        if (_outputPump is { IsAlive: true })
        {
            _outputPump.Join(TimeSpan.FromMilliseconds(1000));
        }

        _processHandle.Dispose();
    }

    /// <summary>后台读取伪控制台输出，喂给 <see cref="TerminalOutputBuffer"/>。</summary>
    private void PumpConsoleOutput()
    {
        var stream = _console!.OutputStream;
        var buffer = new byte[8192];

        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                Output.Append(buffer, read);
            }
        }
        catch (IOException)
        {
            // 管道被关闭，正常退出路径。
        }
        catch (ObjectDisposedException)
        {
            // 会话已释放。
        }
        catch (InvalidOperationException)
        {
            // 流已关闭。
        }
        finally
        {
            Output.MarkClosed();
        }
    }
}

/// <summary>
/// 受管进程启动器。
///
/// 关键顺序（不能颠倒）：CreateProcess(CREATE_SUSPENDED) → 收进作业 → ResumeThread。
/// 若先 Resume 再收编，进程可能在被收编前就已派生出逃逸的后代，
/// 那棵树就永远收不干净了 —— 这是很多同类工具的经典 bug。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessLauncher
{
    public static SupervisedProcess Start(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.CommandLine))
        {
            throw new ArgumentException("命令行不能为空。", nameof(options));
        }

        if (options.CreateNewConsole && options.NoWindow && !options.UsePseudoConsole)
        {
            throw new ArgumentException("CreateNewConsole 与 NoWindow 不能同时为 true。", nameof(options));
        }

        ConPtySession? console = null;
        ProcThreadAttributeList? attributes = null;
        JobObject? job = null;

        try
        {
            if (options.UsePseudoConsole)
            {
                console = ConPtySession.Create(options.ConsoleColumns, options.ConsoleRows);
            }

            job = new JobObject(options.DisplayName);

            var creationFlags = NativeMethods.CREATE_SUSPENDED
                                | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            if (console is not null)
            {
                creationFlags |= NativeMethods.EXTENDED_STARTUPINFO_PRESENT;
            }
            else if (options.CreateNewConsole)
            {
                creationFlags |= NativeMethods.CREATE_NEW_CONSOLE;
            }
            else if (options.NoWindow)
            {
                creationFlags |= NativeMethods.CREATE_NO_WINDOW;
            }

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                },
                lpAttributeList = IntPtr.Zero,
            };

            if (console is not null)
            {
                attributes = new ProcThreadAttributeList(1);
                attributes.SetPseudoConsole(console.Handle);
                startupInfo.lpAttributeList = attributes.Pointer;

                // 实测结论（tools/ConPtyProbe 的变体矩阵得出，官方样例并未体现）：
                // 必须显式声明 STARTF_USESTDHANDLES 并把三个标准句柄填成无效值，
                // 否则 CreateProcess 会把父进程的标准句柄交给子进程，
                // 伪控制台虽然建立成功（能读到 conhost 握手序列），
                // 子进程却仍然 isatty()==false、输出漏进父进程控制台，管道里读不到任何东西。
                startupInfo.StartupInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
                startupInfo.StartupInfo.hStdInput = NativeMethods.INVALID_HANDLE_VALUE;
                startupInfo.StartupInfo.hStdOutput = NativeMethods.INVALID_HANDLE_VALUE;
                startupInfo.StartupInfo.hStdError = NativeMethods.INVALID_HANDLE_VALUE;
            }

            // CreateProcessW 会就地修改命令行缓冲区，必须传入可变的 StringBuilder。
            var commandLine = new StringBuilder(options.CommandLine);

            // 环境块只在 CreateProcess 期间有效，用完立即释放。
            var environmentBlock = BuildEnvironmentBlock(options.EnvironmentVariables);

            bool created;
            int createError = 0;
            PROCESS_INFORMATION processInfo;

            try
            {
                created = NativeMethods.CreateProcessW(
                    lpApplicationName: null,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    // 伪控制台通过属性列表传递，不依赖句柄继承；
                    // 实测 bInheritHandles=true 反而会让子进程拿到父进程的句柄而挂不上伪控制台。
                    bInheritHandles: false,
                    dwCreationFlags: creationFlags,
                    lpEnvironment: environmentBlock,
                    lpCurrentDirectory: options.WorkingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out processInfo);

                if (!created)
                {
                    // 必须在 finally 里释放环境块**之前**取错误码，
                    // 否则 FreeHGlobal 会覆盖 GetLastError 的内容。
                    createError = Marshal.GetLastWin32Error();
                }
            }
            finally
            {
                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }
            }

            if (!created)
            {
                throw new Win32Exception(
                    createError,
                    $"CreateProcess 失败：{options.CommandLine}");
            }

            var processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            var threadHandle = new SafeProcessHandle(processInfo.hThread, ownsHandle: true);

            try
            {
                // 进程此刻仍在挂起态，尚未执行任何用户代码，收编是原子的。
                job.AssignProcess(processInfo.hProcess);

                var resumed = NativeMethods.ResumeThread(processInfo.hThread);
                if (resumed == unchecked((uint)-1))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread 失败。");
                }
            }
            catch
            {
                // 收编或恢复失败：不能让这个挂起进程留下来。
                NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                threadHandle.Dispose();
                processHandle.Dispose();
                throw;
            }

            threadHandle.Dispose();

            // PTY 侧管道句柄的释放时机是这里，不能更早、也不能更晚：
            //
            //   更早（CreateProcess 刚返回就关）：进程还处于 CREATE_SUSPENDED，
            //   ConPTY 尚未把控制台接到子进程上，提前关会让子进程退化成继承父进程的
            //   stdout —— 症状是「子进程 isatty=false，输出漏进父进程控制台，自己一条都读不到」。
            //
            //   更晚（一直留到 Dispose）：输出管道的写端始终有一个句柄开着，
            //   子进程退出后读取端永远等不到 EOF，输出泵会永久阻塞。
            console?.ReleasePtySideHandles();

            // 属性列表只服务于 CreateProcess，创建完成即可释放。
            // 必须用 ?.：未启用伪控制台时它本来就是 null。
            attributes?.Dispose();
            attributes = null;

            return new SupervisedProcess(
                (int)processInfo.dwProcessId,
                processHandle,
                job,
                console);
        }
        catch
        {
            attributes?.Dispose();

            // 失败路径上作业与伪控制台也不能泄漏。
            job?.Dispose();
            console?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 构造 CreateProcess 需要的 UTF-16 环境块。
    ///
    /// 格式要求很严格：每个条目是 <c>KEY=VALUE\0</c>，整块以额外的 <c>\0</c> 结束，
    /// 且键必须按不区分大小写的顺序排列（Windows 内部假定如此，乱序时偶发丢变量）。
    /// </summary>
    /// <returns>需要释放的非托管内存；返回 <see cref="IntPtr.Zero"/> 表示直接继承父进程环境。</returns>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return IntPtr.Zero;
        }

        // 必须以当前环境为基底合并，只传增量会让子进程丢掉 PATH、SystemRoot 等关键变量。
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var pair in overrides)
        {
            merged[pair.Key] = pair.Value ?? string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var pair in merged)
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');

        return Marshal.StringToHGlobalUni(builder.ToString());
    }
}
