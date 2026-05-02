namespace wingman_player.UI;

using Microsoft.Extensions.Logging;
using wingman_player.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public partial class SplashWindow : Window
{
    private readonly ILogger<SplashWindow> _log;
    private UpdateInfo _updateInfo = new(false, string.Empty, string.Empty, string.Empty);

    public SplashWindow(ILogger<SplashWindow> logger)
    {
        _log = logger;
        InitializeComponent();
    }

    public void SetHotkeyLabel(string hotkey) => HotkeyLabel.Text = hotkey;

    public void ShowUpdateBanner(UpdateInfo info)
    {
        _updateInfo         = info;
        UpdateLabel.Text    = $"v{info.Version} available";
        UpdateButton.Content = "Update & Restart";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateLabel.Text       = "Downloading...";
        UpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _log.LogInformation("User initiated update — version {Ver}", _updateInfo.Version);
            await SelfUpdateService.ApplyAsync(_updateInfo, () => Dispatcher.Invoke(Application.Current.Shutdown), _log);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Self-update failed");
            UpdateLabel.Text        = "Update failed. Try again";
            UpdateButton.Content    = "Retry";
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled  = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd  = new WindowInteropHelper(this).Handle;
        var color = 0x00281A0C; // #0c1a28 as COLORREF — matches SettingsWindow caption colour
        DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
}
