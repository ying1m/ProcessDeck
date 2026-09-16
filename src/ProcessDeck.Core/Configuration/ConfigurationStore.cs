using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessDeck.Core.Configuration;

/// <summary>
/// 配置文件的读写。
///
/// 位置：%APPDATA%\ProcessDeck\deck.json
/// 可用环境变量 PROCESSDECK_CONFIG 覆盖（便携模式与自动化测试都依赖这一点，
/// 否则测试会污染用户真实配置）。
/// </summary>
public sealed class ConfigurationStore
{
    public const string PathOverrideEnvironmentVariable = "PROCESSDECK_CONFIG";

    private static readonly JsonSerializerOptions WriteOptions = CreateOptions();
    private static readonly JsonSerializerOptions ReadOptions = CreateOptions();

    public ConfigurationStore(string? filePath = null)
    {
        FilePath = filePath
                   ?? Environment.GetEnvironmentVariable(PathOverrideEnvironmentVariable)
                   ?? DeckPaths.DefaultConfigurationFile;
    }

    /// <summary>配置文件绝对路径。</summary>
    public string FilePath { get; }

    /// <summary>文件是否已存在。</summary>
    public bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// 读取配置。文件不存在或解析失败时给出可用的默认配置，
    /// 并把错误通过 <paramref name="loadError"/> 带出去（而不是抛异常让应用起不来）。
    /// </summary>
    public DeckConfiguration Load(out string? loadError)
    {
        loadError = null;

        if (!File.Exists(FilePath))
        {
            return new DeckConfiguration();
        }

        try
        {
            var json = File.ReadAllText(FilePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new DeckConfiguration();
            }

            var configuration = JsonSerializer.Deserialize<DeckConfiguration>(json, ReadOptions)
                                ?? new DeckConfiguration();

            foreach (var app in configuration.Apps)
            {
                app.EnsureId();
            }

            return configuration;
        }
        catch (JsonException ex)
        {
            loadError = $"配置文件 JSON 解析失败（第 {ex.LineNumber} 行）：{ex.Message}";
            return new DeckConfiguration();
        }
        catch (IOException ex)
        {
            loadError = $"配置文件读取失败：{ex.Message}";
            return new DeckConfiguration();
        }
        catch (UnauthorizedAccessException ex)
        {
            loadError = $"配置文件无权限访问：{ex.Message}";
            return new DeckConfiguration();
        }
    }

    public DeckConfiguration Load() => Load(out _);

    /// <summary>保存配置。先写临时文件再替换，避免写到一半崩溃把配置写坏。</summary>
    public void Save(DeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(configuration, WriteOptions);

        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);

        if (File.Exists(FilePath))
        {
            File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporaryPath, FilePath);
        }
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 允许注释与尾逗号：用户手写配置时非常需要，且能显著降低支持成本。
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // 不把中文转义成 \uXXXX，配置文件要能直接读懂、直接改。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
