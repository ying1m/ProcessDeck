namespace ProcessDeck.Core;

/// <summary>
/// ProcessDeck 在用户机器上的固定路径。
/// 集中在这里，避免各模块各写一份 %APPDATA% 拼接逻辑后逐渐漂移。
/// </summary>
public static class DeckPaths
{
    /// <summary>%APPDATA%\ProcessDeck</summary>
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProcessDeck");

    /// <summary>默认配置文件：%APPDATA%\ProcessDeck\deck.json</summary>
    public static string DefaultConfigurationFile { get; } = Path.Combine(AppDataRoot, "deck.json");

    /// <summary>用户自定义卡片目录：%APPDATA%\ProcessDeck\cards</summary>
    public static string UserCardsDirectory { get; } = Path.Combine(AppDataRoot, "cards");

    /// <summary>用户自定义主题目录：%APPDATA%\ProcessDeck\themes</summary>
    public static string UserThemesDirectory { get; } = Path.Combine(AppDataRoot, "themes");
}
