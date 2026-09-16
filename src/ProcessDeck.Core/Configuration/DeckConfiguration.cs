using System.Text.Json.Nodes;

namespace ProcessDeck.Core.Configuration;

/// <summary>
/// 整份配置文件的根对象。
///
/// 设计取舍：<see cref="Layout"/> 刻意保持为自由 JSON 结构，由前端拥有它的形状。
/// 面板的拖拽布局、卡片尺寸、主题选择这些会随 UI 迭代频繁变化，
/// 如果在这里定成强类型，每次调整 UI 都要改后端的模型和迁移逻辑。
/// 后端只负责「原样存取」，不解释内容。
/// </summary>
public sealed class DeckConfiguration
{
    /// <summary>配置结构版本，为将来迁移预留。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>当前主题名（"dark" / "light" / 用户自定义主题名）。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>面板布局、卡片顺序、自定义卡片等，由前端自由定义。</summary>
    public JsonObject? Layout { get; set; }

    /// <summary>受管应用列表。</summary>
    public List<AppDefinition> Apps { get; set; } = new();

    /// <summary>对被管理端口的轮询间隔（毫秒），0 表示关闭轮询。</summary>
    public int PortWatchIntervalMs { get; set; } = 5000;
}
