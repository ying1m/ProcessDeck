using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// 一个不可见的 Win32 桌面。
///
/// 解决的问题：被启动的程序**自己**再拉起的孙进程，它开不开窗口是它的自由，
/// 启动标志管不到 —— 例如某个脚本里写了
/// <c>Start-Process cmd.exe ... -WindowStyle Minimized</c>，
/// 或者 Node 用 <c>detached: true</c> 派生（Windows 上会给子进程独立控制台）。
///
/// 做法是把进程树整体放到另一个桌面上。窗口是真实存在的，只是永远不在
/// 用户当前的桌面上，所以看不见、也点不到。
///
/// 代价（必须说清楚）：
///   被放上去的程序**无法显示任何界面** —— 托盘图标、对话框、窗口全都出不来。
///   只适合真正的后台服务，不适合需要用户交互的程序。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HiddenDesktop : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    /// <param name="name">
    /// 桌面名。不能与 "Default" 或 "Winlogon" 重名。
    /// 建议带进程或应用标识，避免多次启动时互相干扰。
    /// </param>
    public HiddenDesktop(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("桌面名不能为空。", nameof(name));
        }

        Name = name;

        // 只传桌面名（不带 WinSta0\ 前缀）表示挂在当前窗口站下。
        _handle = NativeMethods.CreateDesktopW(
            name,
            lpszDevice: null,
            pDevmode: IntPtr.Zero,
            dwFlags: 0,
            dwDesiredAccess: NativeMethods.DESKTOP_GENERIC_ALL,
            lpsa: IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"CreateDesktop 失败，无法建立不可见桌面「{name}」。");
        }
    }

    /// <summary>桌面名，直接填进 STARTUPINFO.lpDesktop。</summary>
    public string Name { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 关掉我们这一份句柄。已经在上面运行的进程不受影响，
        // 桌面会在最后一个引用了它的进程消失时才真正销毁。
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseDesktop(_handle);
        }
    }
}
