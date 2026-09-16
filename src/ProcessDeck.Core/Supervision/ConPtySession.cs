using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// Windows 伪控制台（ConPTY，Windows 10 1809+）会话。
///
/// 为什么非它不可：
///   用匿名管道重定向 stdout 时，子进程的 stdout 不是终端（<c>isatty() == false</c>）。
///   大量 CLI 会因此改变行为 —— 去掉颜色、把单行进度条换成刷屏日志、
///   或者干脆把输出缓冲到进程退出才吐（表现为「启动了但一直没反应」）。
///   ConPTY 给子进程一个真正的终端语义，同时让我们仍然能读到输出。
///
/// 句柄所有权（顺序有讲究）：
///   PTY 侧的两个管道句柄必须保留到 CreateProcess 完成之后才能关，
///   与官方样例一致。过早关闭会让伪控制台失去读写端，症状是「进程起来了但读不到任何输出」。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySession : IDisposable
{
    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;

    private IntPtr _ptyInputRead;
    private IntPtr _ptyOutputWrite;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private bool _disposed;

    private ConPtySession(
        IntPtr pseudoConsole,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        IntPtr ptyInputRead,
        IntPtr ptyOutputWrite)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _ptyInputRead = ptyInputRead;
        _ptyOutputWrite = ptyOutputWrite;
    }

    /// <summary>伪控制台句柄，仅在 CreateProcess 期间使用。</summary>
    public IntPtr Handle => _pseudoConsole;

    /// <summary>读取端流：子进程写到终端的字节从这里流出（含 VT 转义序列）。</summary>
    public Stream OutputStream =>
        _outputStream ??= new FileStream(_outputRead, FileAccess.Read, bufferSize: 4096);

    /// <summary>创建一对管道并建立伪控制台。</summary>
    /// <param name="columns">初始列数。</param>
    /// <param name="rows">初始行数。</param>
    public static ConPtySession Create(short columns, short rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "控制台尺寸必须为正数。");
        }

        // 管道句柄必须可继承，否则子进程拿不到控制台。
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 1,
        };

        // 输入管道：PTY 持有读端，我们持有写端（用于把用户输入送进去）。
        if (!NativeMethods.CreatePipe(out var ptyInputRead, out var ourInputWrite, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(输入) 失败。");
        }

        // 输出管道：PTY 持有写端，我们持有读端。
        if (!NativeMethods.CreatePipe(out var ourOutputRead, out var ptyOutputWrite, ref securityAttributes, 0))
        {
            NativeMethods.CloseHandle(ptyInputRead);
            NativeMethods.CloseHandle(ourInputWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(输出) 失败。");
        }

        var size = new COORD(columns, rows);
        var hr = NativeMethods.CreatePseudoConsole(
            size,
            ptyInputRead,
            ptyOutputWrite,
            dwFlags: 0,
            out var pseudoConsole);

        if (hr != 0)
        {
            NativeMethods.CloseHandle(ptyInputRead);
            NativeMethods.CloseHandle(ptyOutputWrite);
            NativeMethods.CloseHandle(ourInputWrite);
            NativeMethods.CloseHandle(ourOutputRead);
            throw new Win32Exception(hr, $"CreatePseudoConsole 失败（HRESULT 0x{hr:X8}）。");
        }

        // 注意：这里刻意不关闭 ptyInputRead / ptyOutputWrite，
        // 它们要活到 CreateProcess 之后，由调用方通过 ReleasePtySideHandles 释放。
        return new ConPtySession(
            pseudoConsole,
            new SafeFileHandle(ourInputWrite, ownsHandle: true),
            new SafeFileHandle(ourOutputRead, ownsHandle: true),
            ptyInputRead,
            ptyOutputWrite);
    }

    /// <summary>
    /// 释放 PTY 侧的管道句柄。必须在 CreateProcess 成功之后调用。
    /// 伪控制台已经持有它自己需要的句柄，我们这一份用完即可归还。
    /// </summary>
    public void ReleasePtySideHandles()
    {
        if (_ptyInputRead != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_ptyInputRead);
            _ptyInputRead = IntPtr.Zero;
        }

        if (_ptyOutputWrite != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_ptyOutputWrite);
            _ptyOutputWrite = IntPtr.Zero;
        }
    }

    /// <summary>把文本送进伪控制台（相当于用户在终端里敲字）。</summary>
    public void Write(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        _inputStream ??= new FileStream(_inputWrite, FileAccess.Write, bufferSize: 1024);

        var bytes = Encoding.UTF8.GetBytes(text);
        _inputStream.Write(bytes, 0, bytes.Length);
        _inputStream.Flush();
    }

    /// <summary>
    /// 调整伪控制台尺寸。
    /// 不做这一步的话，子进程仍然按旧宽度折行（日志会出现莫名其妙的错位）。
    /// </summary>
    public void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        var hr = NativeMethods.ResizePseudoConsole(_pseudoConsole, new COORD(columns, rows));
        if (hr != 0)
        {
            throw new Win32Exception(hr, $"ResizePseudoConsole 失败（HRESULT 0x{hr:X8}）。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先关伪控制台：读取端会随之收到 EOF/错误，输出泵线程得以自然退出。
        NativeMethods.ClosePseudoConsole(_pseudoConsole);

        ReleasePtySideHandles();

        try
        {
            _inputStream?.Dispose();
        }
        catch (IOException)
        {
            // 忽略：管道另一端可能已关闭。
        }

        try
        {
            _outputStream?.Dispose();
        }
        catch (IOException)
        {
            // 忽略。
        }

        _inputWrite.Dispose();
        _outputRead.Dispose();
    }
}
