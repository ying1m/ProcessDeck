using System.IO;

namespace ProcessDeck.App.Services;

/// <summary>
/// 极简诊断日志。
///
/// 为什么必须有：承载 WebView2 的桌面应用一旦出现「界面显示不对」，
/// 用界面本身是没法诊断的（坏掉的正是界面）。
/// 窗口尺寸、DPI 缩放、WebView2 视口这些数据只能落到文件里才能查。
/// </summary>
public static class ShellLog
{
    private static readonly object Gate = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProcessDeck",
        "shell.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                File.AppendAllText(
                    LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写失败绝不能影响主流程。
        }
    }

    public static void Reset()
    {
        try
        {
            File.Delete(LogFilePath);
        }
        catch
        {
            // 文件不存在或被占用都无所谓。
        }
    }
}
