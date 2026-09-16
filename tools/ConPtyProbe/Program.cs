using System.Runtime.InteropServices;
using System.Text;
using static ProbeConstants;
using static Win32;

// ============================================================================
// ConPtyProbe —— 最小化 ConPTY 隔离实验
//
// 已确认的事实（上一轮）：
//   * CreatePseudoConsole 成功 —— 输出管道能读到 conhost 的 16 字节握手序列
//     "\x1b[?9001h\x1b[?1004h"，说明伪控制台本身建立起来了。
//   * lpValue 必须传「句柄值本身」（传 &句柄 会读到 0 字节）。
//   * 但子进程的 echo 输出出现在**父进程控制台**上，管道里一个字都没有。
//
// 本轮要区分两种可能：
//   (A) 子进程压根没被挂到伪控制台上（stdout 被继承了）
//   (B) 挂上了，但输出没能通过管道送达
//
// 判据：让子进程自己把 sys.stdout.isatty() 写进文件。
//   文件说 isatty=True 而管道为空  => (B)
//   文件说 isatty=False               => (A)
// ============================================================================

Console.OutputEncoding = Encoding.UTF8;

var reportPath = Path.Combine(Path.GetTempPath(), "conpty_child_report.txt");
var scriptPath = Path.Combine(Path.GetTempPath(), "conpty_probe_child.py");
File.Delete(reportPath);

File.WriteAllText(scriptPath, $$"""
    import sys
    with open(r"{{reportPath}}", "a", encoding="utf-8") as f:
        f.write("variant=%s isatty=%s stdout=%r\n" % (sys.argv[1], sys.stdout.isatty(), sys.stdout))
    print("CHILD_MARKER_" + sys.argv[1])
    sys.stdout.flush()
    """);

Console.WriteLine($"STARTUPINFOEX 大小 = {Marshal.SizeOf<STARTUPINFOEX>()} 字节（x64 期望 104）");
Console.WriteLine($"STARTUPINFO 大小   = {Marshal.SizeOf<STARTUPINFO>()} 字节（x64 期望 96）");
Console.WriteLine();

var variants = new[]
{
    new Variant("V1", "inherit=TRUE", InheritHandles: true, UseStdHandles: false),
    new Variant("V5", "inherit=FALSE", InheritHandles: false, UseStdHandles: false),
    new Variant("V8", "inherit=TRUE + STARTF_USESTDHANDLES(无效句柄)", InheritHandles: true, UseStdHandles: true),
    new Variant("V9", "inherit=FALSE + STARTF_USESTDHANDLES(无效句柄)", InheritHandles: false, UseStdHandles: true),
};

var succeeded = 0;

foreach (var variant in variants)
{
    Console.WriteLine($"--- {variant.Id}: {variant.Description} ---");
    var commandLine = $"cmd.exe /c python \"{scriptPath}\" {variant.Id}";
    var ok = RunProbe(variant, commandLine, variant.Id, out var detail);
    Console.WriteLine($"    {detail}");
    Console.WriteLine(ok ? "    => 成功" : "    => 失败");
    Console.WriteLine();

    if (ok)
    {
        succeeded++;
    }
}

Console.WriteLine($"{succeeded}/{variants.Length} 个变体成功");
Console.WriteLine();
Console.WriteLine("=== 子进程自述（关键判据）===");
Console.WriteLine(File.Exists(reportPath) ? File.ReadAllText(reportPath) : "(子进程没有写报告，说明它可能根本没跑起来)");

return succeeded > 0 ? 0 : 1;

static bool RunProbe(Variant variant, string commandLine, string variantId, out string detail)
{
    detail = string.Empty;

    var ptyInputRead = IntPtr.Zero;
    var ourInputWrite = IntPtr.Zero;
    var ourOutputRead = IntPtr.Zero;
    var ptyOutputWrite = IntPtr.Zero;
    var attributeList = IntPtr.Zero;
    var pseudoConsole = IntPtr.Zero;
    var processHandle = IntPtr.Zero;
    var threadHandle = IntPtr.Zero;

    try
    {
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 1,
        };

        if (!CreatePipe(out ptyInputRead, out ourInputWrite, ref securityAttributes, 0)
            || !CreatePipe(out ourOutputRead, out ptyOutputWrite, ref securityAttributes, 0))
        {
            detail = $"CreatePipe 失败 err={Marshal.GetLastWin32Error()}";
            return false;
        }

        var hr = CreatePseudoConsole(new COORD(120, 30), ptyInputRead, ptyOutputWrite, 0, out pseudoConsole);
        if (hr != 0)
        {
            detail = $"CreatePseudoConsole 失败 HRESULT=0x{hr:X8}";
            return false;
        }

        IntPtr requiredSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref requiredSize);
        attributeList = Marshal.AllocHGlobal(requiredSize);

        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref requiredSize))
        {
            detail = $"InitializeProcThreadAttributeList 失败 err={Marshal.GetLastWin32Error()}";
            return false;
        }

        if (!UpdateProcThreadAttribute(
                attributeList, 0, ProcThreadAttributePseudoConsole,
                pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            detail = $"UpdateProcThreadAttribute 失败 err={Marshal.GetLastWin32Error()}";
            return false;
        }

        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startupInfo.lpAttributeList = attributeList;

        if (variant.UseStdHandles)
        {
            // 已知绕法：显式声明「使用标准句柄」并塞入无效句柄，
            // 迫使 CreateProcess 不要从父进程继承真实的标准句柄。
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.StartupInfo.hStdInput = new IntPtr(-1);
            startupInfo.StartupInfo.hStdOutput = new IntPtr(-1);
            startupInfo.StartupInfo.hStdError = new IntPtr(-1);
        }

        var creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
        var mutableCommandLine = new StringBuilder(commandLine);

        if (!CreateProcessW(
                null, mutableCommandLine, IntPtr.Zero, IntPtr.Zero,
                variant.InheritHandles, creationFlags, IntPtr.Zero, null,
                ref startupInfo, out var processInformation))
        {
            detail = $"CreateProcess 失败 err={Marshal.GetLastWin32Error()}";
            return false;
        }

        processHandle = processInformation.hProcess;
        threadHandle = processInformation.hThread;

        // 官方样例在 CreateProcess 之后、使用之前关闭 PTY 侧句柄。
        CloseHandle(ptyInputRead);
        CloseHandle(ptyOutputWrite);
        ptyInputRead = IntPtr.Zero;
        ptyOutputWrite = IntPtr.Zero;

        var collected = new List<byte>();
        var readGate = new object();
        var readerFinished = new ManualResetEventSlim(false);

        var reader = new Thread(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    if (!ReadFile(ourOutputRead, buffer, buffer.Length, out var read, IntPtr.Zero) || read <= 0)
                    {
                        break;
                    }

                    lock (readGate)
                    {
                        for (var i = 0; i < read; i++)
                        {
                            collected.Add(buffer[i]);
                        }
                    }
                }
            }
            catch
            {
                // 管道关闭
            }
            finally
            {
                readerFinished.Set();
            }
        })
        {
            IsBackground = true,
        };

        reader.Start();
        readerFinished.Wait(TimeSpan.FromSeconds(8));

        string text;
        int byteCount;
        lock (readGate)
        {
            byteCount = collected.Count;
            text = Encoding.UTF8.GetString(collected.ToArray());
        }

        var containsMarker = text.Contains($"CHILD_MARKER_{variantId}", StringComparison.Ordinal);
        var preview = text.Replace("\u001b", "<ESC>").Replace("\r", "\\r").Replace("\n", "\\n");
        if (preview.Length > 120)
        {
            preview = preview[..120] + "…";
        }

        var childReport = ReadChildReport(variantId);
        detail = $"管道读取 {byteCount} 字节 | 含子进程标记={containsMarker} | 管道内容=[{preview}] | 子进程自述=[{childReport}]";
        return containsMarker;
    }
    finally
    {
        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(pseudoConsole);
        }

        foreach (var handle in new[] { ptyInputRead, ptyOutputWrite, ourInputWrite, ourOutputRead })
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        if (processHandle != IntPtr.Zero)
        {
            TerminateProcess(processHandle, 0);
            CloseHandle(processHandle);
        }

        if (threadHandle != IntPtr.Zero)
        {
            CloseHandle(threadHandle);
        }

        if (attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }
}

static string ReadChildReport(string variantId)
{
    var path = Path.Combine(Path.GetTempPath(), "conpty_child_report.txt");
    if (!File.Exists(path))
    {
        return "(无)";
    }

    foreach (var line in File.ReadAllLines(path))
    {
        if (line.Contains($"variant={variantId} ", StringComparison.Ordinal))
        {
            return line.Trim();
        }
    }

    return "(该变体未写入)";
}

internal sealed record Variant(string Id, string Description, bool InheritHandles, bool UseStdHandles);

internal static class ProbeConstants
{
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const int STARTF_USESTDHANDLES = 0x00000100;

    /// <summary>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)</summary>
    public static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SECURITY_ATTRIBUTES
{
    public int nLength;
    public IntPtr lpSecurityDescriptor;
    public int bInheritHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct COORD
{
    public short X;
    public short Y;

    public COORD(short x, short y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFO
{
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFOEX
{
    public STARTUPINFO StartupInfo;
    public IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

internal static partial class Win32
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);
}
