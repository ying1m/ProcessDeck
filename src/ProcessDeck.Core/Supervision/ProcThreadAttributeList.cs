using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// STARTUPINFOEX 的进程/线程属性列表。
///
/// 它的存在只为了一件事：把 ConPTY 的伪控制台句柄挂到即将创建的子进程上。
/// 生命周期必须严格包住 CreateProcess 调用 —— 进程创建完成后即可释放。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcThreadAttributeList : IDisposable
{
    private IntPtr _buffer;
    private bool _disposed;

    internal IntPtr Pointer =>
        _disposed ? throw new ObjectDisposedException(nameof(ProcThreadAttributeList)) : _buffer;

    internal ProcThreadAttributeList(int attributeCount)
    {
        // 第一次调用必然失败并返回 ERROR_INSUFFICIENT_BUFFER，
        // 它只是用来问出需要多大的缓冲区，这里不能当错误处理。
        var size = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);

        if (size == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "InitializeProcThreadAttributeList 无法确定缓冲区大小。");
        }

        _buffer = Marshal.AllocHGlobal(size);

        if (!NativeMethods.InitializeProcThreadAttributeList(_buffer, attributeCount, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            throw new Win32Exception(error, "InitializeProcThreadAttributeList 失败。");
        }
    }

    /// <summary>把伪控制台句柄设为子进程的控制台。</summary>
    internal void SetPseudoConsole(IntPtr pseudoConsoleHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 关键细节：PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE 的 lpValue 就是 HPCON 句柄值本身，
        // 而不是「指向句柄的指针」。官方 C 样例写作
        //     UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, sizeof(hPC), ...)
        // 因为 HPCON 本身就是 PVOID，句柄值直接当指针用。
        // 若多包一层取地址，ConPTY 会收到一个垃圾句柄 ——
        // 症状非常隐蔽：进程照常启动，但一个字节的输出都读不到。
        if (!NativeMethods.UpdateProcThreadAttribute(
                _buffer,
                dwFlags: 0,
                attribute: NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                lpValue: pseudoConsoleHandle,
                cbSize: (IntPtr)IntPtr.Size,
                lpPreviousValue: IntPtr.Zero,
                lpReturnSize: IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "UpdateProcThreadAttribute(PSEUDOCONSOLE) 失败。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_buffer != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_buffer);
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
