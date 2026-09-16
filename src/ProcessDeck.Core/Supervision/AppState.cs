namespace ProcessDeck.Core.Supervision;

/// <summary>
/// 单个应用的状态。
///
/// 刻意把「启动中」和「运行中」分开：进程存在不等于服务可用，
/// 中间隔着就绪探测。UI 上这个区别就是「转圈」和「绿灯」的差别。
/// </summary>
public enum AppState
{
    /// <summary>未运行。</summary>
    Stopped = 0,

    /// <summary>进程已拉起，正在等待就绪探针通过。</summary>
    Starting = 1,

    /// <summary>就绪探针已通过，服务可用。</summary>
    Running = 2,

    /// <summary>正在执行停止流程（停止命令 / 信号 / 强杀三段）。</summary>
    Stopping = 3,

    /// <summary>启动失败、就绪超时，或运行中异常退出。</summary>
    Failed = 4,
}

/// <summary>停止流程实际走到了哪一段，用于验证与诊断。</summary>
public enum StopStage
{
    /// <summary>未执行过停止。</summary>
    None = 0,

    /// <summary>应用自己的停止命令把它停掉了（最优雅）。</summary>
    StopCommand = 1,

    /// <summary>往伪控制台发送 Ctrl+C 之后退出。</summary>
    Interrupt = 2,

    /// <summary>前两段超时，最后由内核关作业对象强杀。</summary>
    ForceKill = 3,

    /// <summary>本来就没在运行。</summary>
    AlreadyStopped = 4,
}
