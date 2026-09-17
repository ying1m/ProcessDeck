using System.Drawing;
using System.Windows.Forms;

namespace ProcessDeck.App.Services;

/// <summary>
/// 托盘图标与右键菜单。
///
/// 之所以启用 WinForms：WPF 自身没有托盘 API，而 NotifyIcon 由 WindowsDesktop
/// 运行时自带，不需要任何第三方包 —— 符合「只依赖微软官方组件」的约束。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "ProcessDeck",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 ProcessDeck", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>
    /// 从自身可执行文件里取出嵌入的图标（由 &lt;ApplicationIcon&gt; 打进 exe）。
    /// 取不到就退回系统默认图标 —— 托盘没有图标比图标难看更糟。
    /// </summary>
    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(executablePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(executablePath);

                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception)
        {
            // 忽略：下面有兜底。
        }

        return SystemIcons.Application;
    }

    /// <summary>用户要求把面板唤到前台。</summary>
    public event Action? ShowRequested;

    /// <summary>用户要求真正退出进程。</summary>
    public event Action? ExitRequested;

    public void Dispose()
    {
        // 必须先隐藏再释放，否则托盘区会残留一个“幽灵图标”直到鼠标划过。
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
