using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// Win32 作业对象（Job Object）封装 —— 整个 ProcessDeck 的核心机制。
///
/// 解决的问题：
///   Windows 没有进程组，父进程被强杀后子进程会变成孤儿继续占着端口。
///   常见的错误做法是 <c>taskkill /PID x /T /F</c>（配合隐藏窗口），
///   这既是竞态（遍历时新进程还在生）也是杀软的典型恶意行为特征。
///
/// 正确做法：
///   把被管理进程放进一个作业对象，其后代进程会自动继承归属。
///   一旦在作业上设置 <see cref="NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"/>，
///   关闭作业句柄时内核会原子地终止作业内所有进程 ——
///   不管它衍生出多少层命令行，全部一起回收。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;
    private bool _disposed;

    /// <summary>作业句柄关闭时是否连带终止全部成员进程。</summary>
    public bool KillOnClose { get; }

    /// <summary>可选的作业名（同名作业可被 OpenJobObject 附加，本版本仅用于诊断标识）。</summary>
    public string? Name { get; }

    /// <param name="name">可选作业名，便于在调试器中识别。</param>
    /// <param name="killOnClose">
    /// true（默认）= 句柄关闭即回收整棵进程树。除非要长时间托管进程，否则始终用 true。
    /// </param>
    public JobObject(string? name = null, bool killOnClose = true)
    {
        Name = name;

        _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, name);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "CreateJobObject 失败，无法建立进程树托管容器。");
        }

        KillOnClose = killOnClose;

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = killOnClose ? NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE : 0u,
            },
        };

        if (!TryApplyExtendedLimits(limits, out var error))
        {
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject(ExtendedLimitInformation) 失败。");
        }
    }

    /// <summary>
    /// 把一个进程收进作业。
    /// 必须在进程仍处于 <see cref="NativeMethods.CREATE_SUSPENDED"/> 状态时调用，
    /// 否则进程可能在被收编前就已经派生出逃脱的后代。
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 常见原因是目标进程已处于另一个不允许 breakaway 的作业中
    /// （例如从某些 IDE 或受限终端启动 ProcessDeck 自身时）。
    /// </exception>
    public void AssignProcess(IntPtr processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (processHandle == IntPtr.Zero || processHandle == new IntPtr(-1))
        {
            throw new ArgumentException("进程句柄无效。", nameof(processHandle));
        }

        if (NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        var hint = error == 5
            ? "（ERROR_ACCESS_DENIED：目标进程已属于另一个不允许 breakaway 的作业对象，"
              + "通常是因为 ProcessDeck 自己正被某个 IDE / 受限宿主以作业方式启动。）"
            : string.Empty;

        throw new Win32Exception(error, $"AssignProcessToJobObject 失败。{hint}");
    }

    /// <summary>
    /// 立即终止作业内所有进程。这是「硬停」路径，
    /// 正常停止应当先尝试应用自己的停止命令，再走这里兜底。
    /// </summary>
    public void Terminate(uint exitCode = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject 失败。");
        }
    }

    /// <summary>
    /// 当前仍存活在作业内的进程数。用于判断「是否真的收干净了」，
    /// 也是 UI 上“已停止”状态的可信依据（而不是猜）。
    /// </summary>
    public int ActiveProcessCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                        _handle,
                        JOBOBJECTINFOCLASS.BasicAccountingInformation,
                        buffer,
                        (uint)size,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "QueryInformationJobObject(BasicAccountingInformation) 失败。");
                }

                var info = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer);
                return (int)info.ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>作业内曾经出现过的进程总数，便于诊断“启动时是不是炸出了一堆子进程”。</summary>
    public int TotalProcessCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                        _handle,
                        JOBOBJECTINFOCLASS.BasicAccountingInformation,
                        buffer,
                        (uint)size,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "QueryInformationJobObject(BasicAccountingInformation) 失败。");
                }

                var info = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer);
                return (int)info.TotalProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// 作业内当前存活进程的 PID 列表。
    ///
    /// 这是「端口归属」的基础：拿到占用端口的 PID 后，
    /// 只要它出现在某个应用的作业成员里，就能确定是哪个应用占了端口。
    /// 不需要遍历父进程链（那既慢又会被 PID 复用骗到），
    /// 也不会把无关进程误判成该应用的子孙。
    /// </summary>
    public IReadOnlyList<int> MemberProcessIds
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var headerSize = Marshal.SizeOf<JOBOBJECT_BASIC_PROCESS_ID_LIST_HEADER>();
            var requested = Math.Max(ActiveProcessCount, 8) + 16;
            const int errorMoreData = 234;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var totalSize = headerSize + (requested * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(totalSize);

                try
                {
                    if (NativeMethods.QueryInformationJobObject(
                            _handle,
                            JOBOBJECTINFOCLASS.BasicProcessIdList,
                            buffer,
                            (uint)totalSize,
                            out _))
                    {
                        // 结构体末尾是变长 PID 数组，第二个 DWORD 才是真实个数。
                        var count = Marshal.ReadInt32(buffer, sizeof(uint));
                        var pids = new List<int>(count);

                        for (var i = 0; i < count; i++)
                        {
                            var pid = Marshal.ReadIntPtr(buffer, headerSize + (i * IntPtr.Size));
                            pids.Add(pid.ToInt32());
                        }

                        return pids;
                    }

                    var error = Marshal.GetLastWin32Error();

                    // 分配不够大：内核会把实际需要的大小写在头部，据此重试。
                    if (error == errorMoreData)
                    {
                        requested = Marshal.ReadInt32(buffer) + 8;
                        continue;
                    }

                    throw new Win32Exception(
                        error,
                        "QueryInformationJobObject(BasicProcessIdList) 失败。");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            throw new InvalidOperationException(
                "作业成员列表查询重试 4 次仍未成功：作业内进程数量在持续剧烈变化。");
        }
    }

    /// <summary>判断给定进程是否已经处于某个作业中，用于启动前的环境自检。</summary>
    public static bool IsProcessInAnyJob(IntPtr processHandle)
    {
        if (!NativeMethods.IsProcessInJob(processHandle, null, out var inJob))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IsProcessInJob 失败。");
        }

        return inJob;
    }

    private bool TryApplyExtendedLimits(JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits, out int error)
    {
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            var ok = NativeMethods.SetInformationJobObject(
                _handle,
                JOBOBJECTINFOCLASS.ExtendedLimitInformation,
                buffer,
                (uint)size);

            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 关闭作业句柄。若 <see cref="KillOnClose"/> 为 true，
    /// 内核会在这一步终止作业内所有残留进程 —— 这就是「停止」的最终保证。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
