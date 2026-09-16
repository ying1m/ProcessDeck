using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessDeck.Core.Cards;

/// <summary>
/// 卡片包的清单文件 <c>card.json</c>。
///
/// 一个卡片包就是一个目录：
/// <code>
/// my-card/
///   card.json      必需，本清单
///   card.html      默认入口，在沙箱 iframe 中运行
///   card.js        可选
///   card.css       可选
/// </code>
/// </summary>
public sealed class CardManifest
{
    /// <summary>卡片标识。留空则用目录名。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? Version { get; set; }

    /// <summary>入口文件名，必须在卡片目录内，不允许出现路径穿越。</summary>
    public string Entry { get; set; } = "card.html";

    /// <summary>在卡片选择器里的排序权重，越小越靠前。</summary>
    public int Order { get; set; } = 100;
}

/// <summary>已发现的卡片。</summary>
public sealed record CardDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? Version { get; init; }

    /// <summary>"builtin" 或 "user"。</summary>
    public required string Source { get; init; }

    /// <summary>给人看的来源标签。</summary>
    public required string SourceLabel { get; init; }

    /// <summary>沙箱 iframe 要加载的完整地址。</summary>
    public required string EntryUrl { get; init; }

    public int Order { get; init; }
}

/// <summary>一个卡片搜索根：某个目录 + 它对应的虚拟主机地址前缀。</summary>
/// <param name="Directory">磁盘目录。</param>
/// <param name="BaseUrl">该目录映射到的 URL 前缀，末尾不带斜杠。</param>
/// <param name="Source">来源标识。</param>
/// <param name="SourceLabel">来源显示名。</param>
public sealed record CardRoot(string Directory, string BaseUrl, string Source, string SourceLabel);

/// <summary>
/// 卡片发现。
///
/// 只做「读清单 + 校验 + 拼地址」，不加载任何卡片代码 ——
/// 卡片代码永远在 WebView2 的沙箱 iframe 里跑，宿主进程不解释它。
/// </summary>
public static class CardCatalog
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 扫描全部搜索根。先出现的根优先，同 id 的后面会被忽略并记入问题列表。
    /// </summary>
    public static IReadOnlyList<CardDescriptor> Discover(
        IEnumerable<CardRoot> roots,
        out IReadOnlyList<string> problems)
    {
        var issueList = new List<string>();
        var found = new List<CardDescriptor>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Directory))
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root.Directory);
            }
            catch (Exception ex)
            {
                issueList.Add($"无法读取卡片目录 {root.Directory}：{ex.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                var folderName = Path.GetFileName(directory);
                var manifestPath = Path.Combine(directory, "card.json");

                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                CardManifest? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<CardManifest>(File.ReadAllText(manifestPath), ReadOptions);
                }
                catch (Exception ex)
                {
                    issueList.Add($"卡片 {folderName} 的 card.json 解析失败：{ex.Message}");
                    continue;
                }

                if (manifest is null)
                {
                    issueList.Add($"卡片 {folderName} 的 card.json 内容为空。");
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(manifest.Id) ? folderName : manifest.Id.Trim();

                if (!seenIds.Add(id))
                {
                    issueList.Add($"卡片 id 重复，已忽略：{id}（{directory}）");
                    continue;
                }

                // 入口必须就是卡片目录里的一个文件名：绝对路径、子目录、.. 都属于路径穿越尝试。
                var entry = string.IsNullOrWhiteSpace(manifest.Entry) ? "card.html" : manifest.Entry.Trim();

                if (Path.IsPathRooted(entry)
                    || entry.Contains("..", StringComparison.Ordinal)
                    || entry.Contains('/')
                    || entry.Contains('\\'))
                {
                    issueList.Add($"卡片 {id} 的 entry 必须是卡片目录内的文件名，当前为：{entry}");
                    continue;
                }

                var entryFile = Path.Combine(directory, entry);
                if (!File.Exists(entryFile))
                {
                    issueList.Add($"卡片 {id} 缺少入口文件：{entry}");
                    continue;
                }

                found.Add(new CardDescriptor
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? id : manifest.Name.Trim(),
                    Description = manifest.Description,
                    Author = manifest.Author,
                    Version = manifest.Version,
                    Source = root.Source,
                    SourceLabel = root.SourceLabel,
                    EntryUrl = $"{root.BaseUrl}/{Uri.EscapeDataString(folderName)}/{entry}",
                    Order = manifest.Order,
                });
            }
        }

        problems = issueList;

        return found
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Source, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
