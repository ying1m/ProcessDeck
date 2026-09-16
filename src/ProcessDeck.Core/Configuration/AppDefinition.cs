using System.Text.Json.Serialization;

namespace ProcessDeck.Core.Configuration;

/// <summary>就绪探针类型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadinessKind>))]
public enum ReadinessKind
{
    /// <summary>不做探测，进程存活一小段时间即视为就绪。</summary>
    None = 0,

    /// <summary>端口开始监听，且监听者属于本应用的作业对象。</summary>
    Port = 1,

    /// <summary>HTTP 请求返回成功状态码。</summary>
    Http = 2,

    /// <summary>终端输出匹配正则。</summary>
    Log = 3,
}

/// <summary>
/// 就绪判定配置。
///
/// 为什么必须有它：进程「起来了」不等于服务「可用了」。
/// 没有探针时 UI 会显示「运行中」，但用户点开链接发现根本连不上 ——
/// 这是所有同类工具最常见的体验缺陷。
/// </summary>
public sealed class ReadinessProbeDefinition
{
    public ReadinessKind Kind { get; set; } = ReadinessKind.None;

    /// <summary>HTTP 探针的地址，例如 http://127.0.0.1:8080/health 。</summary>
    public string? Url { get; set; }

    /// <summary>日志探针的正则，例如 "Ready in|listening on" 。</summary>
    public string? Pattern { get; set; }

    /// <summary>最长等待秒数，超时判定启动失败。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>轮询间隔毫秒。</summary>
    public int IntervalMs { get; set; } = 300;

    /// <summary>HTTP 探针认为成功的状态码下限（含）。</summary>
    public int HttpSuccessFrom { get; set; } = 200;

    /// <summary>HTTP 探针认为成功的状态码上限（含）。</summary>
    public int HttpSuccessTo { get; set; } = 399;
}

/// <summary>
/// 一个受管应用的定义。这是用户的配置文件里每一条记录对应的模型。
/// </summary>
public sealed class AppDefinition
{
    /// <summary>稳定标识，用于面板布局持久化与 IPC 引用。留空则自动生成。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>分组名，面板可以按组排布。</summary>
    public string? Group { get; set; }

    /// <summary>
    /// 启动命令。原样交给 CreateProcess，不做转义改写 ——
    /// PowerShell 命令里大量含引号/管道/分号，二次转义是最大的翻车点。
    /// </summary>
    public string StartCommand { get; set; } = string.Empty;

    /// <summary>
    /// 优雅停止命令（可选）。例如 "xxx stop"、"pg_ctl stop -D data"。
    /// 留空则停止时直接进入信号阶段。
    /// </summary>
    public string? StopCommand { get; set; }

    /// <summary>工作目录，支持 %VAR% 形式的环境变量展开。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>追加/覆盖的环境变量。</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>主端口。用于启动前冲突预检与端口就绪判定。</summary>
    public int? Port { get; set; }

    /// <summary>其他关注端口（例如附带的管理端口），仅用于面板展示。</summary>
    public List<int> ExtraPorts { get; set; } = new();

    /// <summary>就绪探针。</summary>
    public ReadinessProbeDefinition Readiness { get; set; } = new();

    /// <summary>优雅停止的最长等待秒数，超时后强制关作业对象。</summary>
    public int StopTimeoutSeconds { get; set; } = 15;

    /// <summary>无探针时，进程存活多少秒即视为就绪。</summary>
    public int StartGraceSeconds { get; set; } = 2;

    /// <summary>ProcessDeck 启动时是否自动拉起。</summary>
    public bool AutoStart { get; set; }

    /// <summary>面板上的强调色（可选），交给前端自由使用。</summary>
    public string? Accent { get; set; }

    /// <summary>校验并补齐默认值，返回问题列表（空表示没问题）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("name 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(StartCommand))
        {
            problems.Add("startCommand 不能为空。");
        }

        if (Port is <= 0 or > 65535)
        {
            problems.Add($"port 取值非法：{Port}。");
        }

        if (StopTimeoutSeconds is <= 0 or > 600)
        {
            problems.Add($"stopTimeoutSeconds 应在 1..600 之间，当前 {StopTimeoutSeconds}。");
        }

        if (Readiness.Kind == ReadinessKind.Http && string.IsNullOrWhiteSpace(Readiness.Url))
        {
            problems.Add("readiness.kind 为 http 时必须提供 readiness.url。");
        }

        if (Readiness.Kind == ReadinessKind.Log && string.IsNullOrWhiteSpace(Readiness.Pattern))
        {
            problems.Add("readiness.kind 为 log 时必须提供 readiness.pattern。");
        }

        if (Readiness.Kind == ReadinessKind.Port && Port is null)
        {
            problems.Add("readiness.kind 为 port 时必须提供 port。");
        }

        return problems;
    }

    /// <summary>生成一个可读且稳定的默认 Id。</summary>
    public void EnsureId()
    {
        if (!string.IsNullOrWhiteSpace(Id))
        {
            return;
        }

        var slug = new string((Name ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        if (slug.Length == 0)
        {
            slug = "app";
        }

        Id = slug.Length > 32 ? slug[..32] : slug;
    }
}
