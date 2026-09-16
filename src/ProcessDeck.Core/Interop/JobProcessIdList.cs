using System.Runtime.InteropServices;

namespace ProcessDeck.Core.Interop;

/// <summary>
/// JOBOBJECT_BASIC_PROCESS_ID_LIST 的固定头部。
///
/// 注意：这是个变长结构 —— 头部之后紧跟着 NumberOfProcessIdsInList 个 SIZE_T（PID）。
/// 因此不能用 PtrToStructure 一次性读出来，必须手工按偏移读取。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_PROCESS_ID_LIST_HEADER
{
    /// <summary>内核分配给该作业的 PID 槽位数（通常大于等于实际个数）。</summary>
    public uint NumberOfAssignedProcesses;

    /// <summary>列表中真实有效的 PID 个数。</summary>
    public uint NumberOfProcessIdsInList;
}
