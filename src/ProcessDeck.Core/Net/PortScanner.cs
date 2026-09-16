using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProcessDeck.Core.Interop;

namespace ProcessDeck.Core.Net;

/// <summary>一个 TCP 端点的快照。</summary>
/// <param name="Port">本机端口（已从网络字节序转换）。</param>
/// <param name="LocalAddress">本机地址（网络字节序原始值）。</param>
/// <param name="ProcessId">占用该端点的进程 PID。</param>
/// <param name="IsListening">是否处于 LISTEN 状态。</param>
public sealed record TcpEndpoint(int Port, uint LocalAddress, int ProcessId, bool IsListening);

/// <summary>
/// 端口占用扫描器。
///
/// 刻意不解析 <c>netstat -ano</c> 的文本输出：那种做法慢、受系统语言影响、且输出本身可能被截断。
/// 这里直接调 IP Helper API 拿结构化快照。
/// </summary>
[SupportedOSPlatform("windows")]
public static class PortScanner
{
    /// <summary>获取本机全部 IPv4 TCP 端点快照。</summary>
    public static IReadOnlyList<TcpEndpoint> GetTcpEndpoints()
    {
        var size = 0;

        // 第一次调用只为了问出所需缓冲区大小，返回 ERROR_INSUFFICIENT_BUFFER 是预期行为。
        var result = IpHlpApi.GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            bOrder: false,
            IpHlpApi.AF_INET,
            TCP_TABLE_CLASS.TcpTableOwnerPidAll,
            reserved: 0);

        if (result != IpHlpApi.ERROR_INSUFFICIENT_BUFFER && result != IpHlpApi.NO_ERROR)
        {
            throw new Win32Exception((int)result, "GetExtendedTcpTable 预查询失败。");
        }

        if (size <= 0)
        {
            return Array.Empty<TcpEndpoint>();
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = IpHlpApi.GetExtendedTcpTable(
                buffer,
                ref size,
                bOrder: false,
                IpHlpApi.AF_INET,
                TCP_TABLE_CLASS.TcpTableOwnerPidAll,
                reserved: 0);

            if (result != IpHlpApi.NO_ERROR)
            {
                throw new Win32Exception((int)result, "GetExtendedTcpTable 查询失败。");
            }

            // 布局：DWORD 条目数，随后紧接 MIB_TCPROW_OWNER_PID 数组。
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var rows = new List<TcpEndpoint>(count);

            for (var i = 0; i < count; i++)
            {
                var rowPtr = IntPtr.Add(buffer, sizeof(uint) + (i * rowSize));
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);

                rows.Add(new TcpEndpoint(
                    Port: DecodePort(row.LocalPort),
                    LocalAddress: row.LocalAddr,
                    ProcessId: unchecked((int)row.OwningPid),
                    IsListening: row.State == IpHlpApi.MIB_TCP_STATE_LISTEN));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>获取所有正在监听的本机端口，按端口号排序。</summary>
    public static IReadOnlyList<TcpEndpoint> GetListeningEndpoints()
        => GetTcpEndpoints()
            .Where(e => e.IsListening)
            .OrderBy(e => e.Port)
            .ToArray();

    /// <summary>查询指定端口上的监听者；没有则返回 null。</summary>
    public static TcpEndpoint? FindListener(int port)
        => GetTcpEndpoints().FirstOrDefault(e => e.Port == port && e.IsListening);

    /// <summary>端口是否已被占用（用于启动前预检）。</summary>
    public static bool IsPortInUse(int port)
        => GetTcpEndpoints().Any(e => e.Port == port && e.IsListening);

    /// <summary>
    /// 解析占用端口的进程信息，用于把裸 PID 变成用户看得懂的东西。
    /// 进程可能已经退出，此时返回 null 而不是抛异常。
    /// </summary>
    public static string? DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return $"{process.ProcessName} (PID {processId})";
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 本机端口在网络字节序里的低 16 位是高字节在前，必须交换。
    /// 这个坑几乎所有手写 netstat 替代品都会踩。
    /// </summary>
    private static int DecodePort(uint networkOrderPort)
        => (int)(((networkOrderPort & 0x000000FFu) << 8) | ((networkOrderPort & 0x0000FF00u) >> 8));
}
