using System.Runtime.InteropServices;

namespace ProcessDeck.Core.Interop;

/// <summary>GetExtendedTcpTable 的查询类别。</summary>
internal enum TCP_TABLE_CLASS
{
    TcpTableBasicListener = 0,
    TcpTableBasicConnections = 1,
    TcpTableBasicAll = 2,
    TcpTableOwnerPidListener = 3,
    TcpTableOwnerPidConnections = 4,

    /// <summary>带所属进程 PID 的全部 TCP 端点 —— 我们需要的就是这个。</summary>
    TcpTableOwnerPidAll = 5,

    TcpTableOwnerModuleListener = 6,
    TcpTableOwnerModuleConnections = 7,
    TcpTableOwnerModuleAll = 8,
}

/// <summary>MIB_TCPROW_OWNER_PID：一条 TCP 端点记录。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPROW_OWNER_PID
{
    public uint State;
    public uint LocalAddr;

    /// <summary>本机端口，网络字节序（高低字节与直觉相反，需转换）。</summary>
    public uint LocalPort;

    public uint RemoteAddr;
    public uint RemotePort;

    /// <summary>占用该端点的进程 PID —— 端口归属的起点。</summary>
    public uint OwningPid;
}

/// <summary>
/// IP Helper API。相比解析 netstat 文本输出，
/// 这是结构化、原子、无本地化的正确做法。
/// </summary>
internal static class IpHlpApi
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const uint NO_ERROR = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>MIB_TCP_STATE_LISTEN，表示该端点处于监听状态。</summary>
    public const uint MIB_TCP_STATE_LISTEN = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TCP_TABLE_CLASS tableClass,
        uint reserved);
}
