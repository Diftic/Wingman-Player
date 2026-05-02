namespace wingman_player.UI;

using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

/// <summary>
///     System tray icon with state-based icons and a context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _retryUpdateItem;
    private string? _pendingMsiPath;

    public event EventHandler? ExitRequested;
    public event EventHandler? ResetWindowRequested;

    public TrayIcon()
    {
        _icon = LoadEmbeddedIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = Constants.ApplicationName,
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        _retryUpdateItem = new ToolStripMenuItem("Retry failed update...", null, OnRetryUpdate) { Visible = false };
        menu.Items.Add(_retryUpdateItem);
        menu.Items.Add(new ToolStripSeparator { Visible = false });
        menu.Items.Add("Reset Player Window", null, (_, _) => ResetWindowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        => _notifyIcon.ShowBalloonTip(3000, title, message, icon);

    /// <summary>
    /// Expose a "Retry failed update..." item in the context menu. When clicked it
    /// launches the cached MSI via ShellExecute — the same path a user would take
    /// by double-clicking the installer, so AV never treats it as heuristic.
    /// </summary>
    public void SetPendingUpdateRetry(string msiPath)
    {
        _pendingMsiPath = msiPath;
        _retryUpdateItem.Visible = true;
        // Show the separator that sits right after the retry item.
        if (_notifyIcon.ContextMenuStrip is { } strip && strip.Items.Count > 1)
            strip.Items[1].Visible = true;
    }

    private void OnRetryUpdate(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingMsiPath) || !System.IO.File.Exists(_pendingMsiPath))
        {
            ShowBalloon("Retry update", "The cached installer is no longer available.", ToolTipIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_pendingMsiPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Retry update failed", ex.Message, ToolTipIcon.Error);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    // -------------------------------------------------------------------------
    // Icon loading
    // -------------------------------------------------------------------------

    private static Icon LoadEmbeddedIcon()
    {
        var asm    = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("wingman_player.Assets.icon.ico");
        if (stream is null)
            return CreateFallbackIcon(Color.FromArgb(248, 113, 113)); // crimson fallback
        return new Icon(stream, 16, 16);
    }

    private static Icon CreateFallbackIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }

        var hIcon = bmp.GetHicon();
        var icon  = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        DestroyIcon(hIcon);
        return clone;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
