using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessDeck.Core.Interop;

/// <summary>
/// 全部 Win32 P/Invoke 声明集中在此，便于审计。
/// 这里只做声明，不含业务逻辑。
/// </summary>
internal static class NativeMethods
{
    // ---------- 作业对象限制标志 ----------

    /// <summary>
    /// 关闭最后一个作业句柄时，内核终止作业内所有进程。
    /// 这是「整棵进程树一起收干净」的关键，取代 taskkill /T /F。
    /// </summary>
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    /// <summary>作业内进程派生子进程时，子进程自动加入同一作业（Win8+ 默认行为，显式声明更清晰）。</summary>
    public const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;

    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;

    // ---------- 进程创建标志 ----------

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    /// <summary>
    /// STARTUPINFO.dwFlags：表示 hStdInput/hStdOutput/hStdError 三个字段有效。
    ///
    /// ConPTY 场景下它不可或缺：必须设置该标志，并把三个句柄都填成
    /// <see cref="INVALID_HANDLE_VALUE"/>，才能阻止子进程继承父进程的标准句柄，
    /// 从而让伪控制台真正接管子进程的 stdin/stdout/stderr。
    ///
    /// 少这一步的症状非常隐蔽：CreatePseudoConsole 成功、能读到 conhost 的握手序列、
    /// 进程也正常启动 —— 但子进程 isatty()==false，输出漏进父进程控制台，管道里一个字都没有。
    /// </summary>
    public const int STARTF_USESTDHANDLES = 0x00000100;

    /// <summary>INVALID_HANDLE_VALUE，配合 <see cref="STARTF_USESTDHANDLES"/> 使用。</summary>
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---------- 进程访问权限 ----------

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;

    public const uint STILL_ACTIVE = 259;

    // ---------- 进程线程属性 ----------

    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new(0x00020016);

    // ---------- kernel32: 句柄与作业对象 ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(
        SafeJobHandle hJob,
        JOBOBJECTINFOCLASS infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryInformationJobObject(
        SafeJobHandle hJob,
        JOBOBJECTINFOCLASS infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessInJob(IntPtr processHandle, SafeJobHandle? jobHandle, out bool result);

    // ---------- kernel32: 进程创建与线程 ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    // ---------- kernel32: 进程/线程属性列表 ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    // ---------- kernel32: ConPTY 伪控制台 ----------
    // 用途：让 CLI 以为自己接在真实终端上，从而输出颜色/进度条/交互提示。
    // 用普通管道读 stdout 会让很多程序改变行为（静默、缓冲、乱码）。

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    // ---------- kernel32: 匿名管道 ----------

    /// <summary>
    /// 创建匿名管道。句柄以 IntPtr 返回而非 SafeFileHandle：
    /// SafeFileHandle 的默认构造函数不是公开的，P/Invoke 的 out SafeHandle 封送不可靠，
    /// 由调用方显式包装更安全。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    // ---------- user32: 不可见桌面 ----------
    //
    // 用途：把被启动的进程树整体放到一个永远不会被切换到的 Win32 桌面上。
    // 这样它以及它派生出的**所有**孙进程创建的窗口都在那个桌面上，
    // 用户完全看不到 —— 这是唯一能覆盖「孙进程自己开新控制台」的通用手段，
    // 靠 CreateProcess 的创建标志是管不住孙进程的。

    /// <summary>DESKTOP_ALL_ACCESS 的常用近似值。</summary>
    public const uint DESKTOP_GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(
        string lpszDesktop,
        string? lpszDevice,
        IntPtr pDevmode,
        uint dwFlags,
        uint dwDesiredAccess,
        IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);
}
