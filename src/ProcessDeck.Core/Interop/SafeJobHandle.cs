using Microsoft.Win32.SafeHandles;

namespace ProcessDeck.Core.Interop;

/// <summary>
/// 作业对象句柄的安全包装。ReleaseHandle 关闭句柄时，
/// 若作业设置了 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，内核会连带终止作业内全部进程。
/// 也就是说：Dispose 这个对象 = 把整棵进程树收干净。
/// </summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeJobHandle(IntPtr existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
