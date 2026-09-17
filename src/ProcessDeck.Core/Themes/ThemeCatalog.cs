using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcessDeck.Core.Themes;

/// <summary>
/// 主题包清单 <c>theme.json</c>。
///
/// 一个主题包就是一个目录：
/// <code>
/// nord/
///   theme.json      必需，本清单
/// </code>
///
/// 主题不注入任何 JS，只提供一组 CSS 自定义属性（<c>--xxx</c>）的值。
/// </summary>
public sealed class ThemeManifest
{
    /// <summary>主题标识。留空则用目录名。</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? Version { get; set; }

    /// <summary>
    /// "dark" 或 "light"。
    /// 作为基底：<see cref="Vars"/> 里没覆盖到的变量会从这个内置主题继承，
    /// 所以一个主题只需要写它真正想改的那几个变量。
    /// </summary>
    public string Base { get; set; } = "dark";

    /// <summary>CSS 自定义属性 → 值。键必须以 <c>--</c> 开头。</summary>
    public Dictionary<string, string> Vars { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>已发现的主题。</summary>
public sealed record ThemeDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? Version { get; init; }

    /// <summary>"dark" 或 "light"。</summary>
    public required string Base { get; init; }

    /// <summary>"builtin" 或 "user"。</summary>
    public required string Source { get; init; }

    public required string SourceLabel { get; init; }

    /// <summary>要写进 <c>document.documentElement.style</c> 的变量。</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } = new Dictionary<string, string>();
}

/// <summary>主题搜索根。</summary>
public sealed record ThemeRoot(string Directory, string Source, string SourceLabel);

/// <summary>
/// 主题发现与校验。
///
/// 这里是**安全边界的一部分**：主题只被允许设置 CSS 自定义属性，
/// 且变量值要过一遍白名单检查。原因是一个可分享的主题如果能把
/// <c>url(...)</c> 塞进某个被用作 <c>background</c> 的变量里，
/// 打开面板就会向攻击者控制的地址发起请求 ——
/// 等于一个静默的「谁在用这个主题」信标，甚至能携带 token（如果被拼进去）。
/// 面板的 CSP 会挡住大部分，但在这里直接拒绝更省事也更明确。
/// </summary>
public static class ThemeCatalog
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex ValidVariableName = new(@"^--[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>内置的深色与浅色主题。它们定义在 panel.css 里，这里只登记名字。</summary>
    public static IReadOnlyList<ThemeDescriptor> BuiltIn { get; } = new[]
    {
        new ThemeDescriptor
        {
            Id = "dark",
            Name = "深色",
            Description = "默认主题。",
            Base = "dark",
            Source = "builtin",
            SourceLabel = "内置",
        },
        new ThemeDescriptor
        {
            Id = "light",
            Name = "浅色",
            Description = "默认主题的浅色版本。",
            Base = "light",
            Source = "builtin",
            SourceLabel = "内置",
        },
    };

    /// <summary>扫描全部搜索根。内置主题永远排在最前面（它们不能被覆盖）。</summary>
    public static IReadOnlyList<ThemeDescriptor> Discover(
        IEnumerable<ThemeRoot> roots,
        out IReadOnlyList<string> problems)
    {
        var issueList = new List<string>();
        var found = new List<ThemeDescriptor>(BuiltIn);

        // 内置深色/浅色的 id 不允许被用户主题顶掉。
        var seenIds = new HashSet<string>(BuiltIn.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

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
                issueList.Add($"无法读取主题目录 {root.Directory}：{ex.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                var folderName = Path.GetFileName(directory);
                var manifestPath = Path.Combine(directory, "theme.json");

                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                ThemeManifest? manifest;

                try
                {
                    manifest = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(manifestPath), ReadOptions);
                }
                catch (Exception ex)
                {
                    issueList.Add($"主题 {folderName} 的 theme.json 解析失败：{ex.Message}");
                    continue;
                }

                if (manifest is null)
                {
                    issueList.Add($"主题 {folderName} 的 theme.json 内容为空。");
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(manifest.Id) ? folderName : manifest.Id.Trim();

                if (!seenIds.Add(id))
                {
                    issueList.Add($"主题 id 重复或与内置主题冲突，已忽略：{id}（{directory}）");
                    continue;
                }

                var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                var rejected = 0;

                foreach (var pair in manifest.Vars)
                {
                    if (!ValidVariableName.IsMatch(pair.Key) || !IsSafeValue(pair.Value))
                    {
                        rejected++;
                        continue;
                    }

                    variables[pair.Key] = pair.Value;
                }

                if (rejected > 0)
                {
                    issueList.Add($"主题 {id} 有 {rejected} 个变量被安全校验拒绝（只允许 -- 开头的自定义属性，且不允许 url()/expression()/花括号等）。");
                }

                found.Add(new ThemeDescriptor
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? id : manifest.Name.Trim(),
                    Description = manifest.Description,
                    Author = manifest.Author,
                    Version = manifest.Version,
                    Base = string.Equals(manifest.Base, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
                    Source = root.Source,
                    SourceLabel = root.SourceLabel,
                    Vars = variables,
                });
            }
        }

        problems = issueList;
        return found;
    }

    /// <summary>
    /// 变量值白名单。
    /// 允许颜色、长度、字体名等常见 token；拒绝一切可以触发网络请求或跳出声明块的东西。
    /// </summary>
    private static bool IsSafeValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200)
        {
            return false;
        }

        var lowered = value.ToLowerInvariant();

        return !lowered.Contains("url(")
               && !lowered.Contains("expression(")
               && !lowered.Contains("@import")
               && !value.Contains(';')
               && !value.Contains('{')
               && !value.Contains('}')
               && !value.Contains('<')
               && !value.Contains('>')
               && !value.Contains('\n')
               && !value.Contains('\r');
    }
}
