namespace wingman_player.UI;

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

/// <summary>
/// Minimised "now playing" banner: a small, click-through, always-on-top tile
/// in the lower-right of the working area. Receives track titles + hotkey
/// label pushes from <see cref="OverlayWindow"/> via <c>PostWebMessageAsJson</c>.
/// </summary>
public partial class MiniBannerWindow : Window
{
    private readonly SettingsManager _settings;
    private readonly ILogger<MiniBannerWindow> _logger;

    private CoreWebView2Environment? _env;
    private bool   _webViewReady;
    private bool   _bannerReady;
    private bool   _bannerShown;
    private bool   _editMode;
    private bool   _suppressLocationSave;
    private HWND   _hwnd;
    private string _pendingStation = "Wingman Player";
    private string _pendingTitle   = string.Empty;
    private string _pendingHotkey  = "F8";

    public MiniBannerWindow(SettingsManager settings, ILogger<MiniBannerWindow> logger)
    {
        _settings = settings;
        _logger   = logger;

        InitializeComponent();

        // Pre-size from saved scale, but stay off-screen until ShowBanner() snaps to
        // position. WebView2 still initialises invisibly at the off-screen spot.
        var factor = _settings.Current.BannerScalePct / 100.0;
        Width  = Constants.BannerWidth  * factor;
        Height = Constants.BannerHeight * factor;
        Left   = -(Width + 100);
        Top    = 0;

        LocationChanged += OnLocationChanged;
    }

    public bool IsBannerVisible => _bannerShown;

    public void ShowBanner()
    {
        AnchorToSavedOrDefault();
        Visibility = Visibility.Visible;
        _bannerShown = true;
        if (_bannerReady)
            FlushPending();
    }

    public void HideBanner()
    {
        // Move off-screen instead of collapsing — staying Visible keeps the WebView2
        // render surface alive so we don't pay re-init cost on the next ShowBanner().
        _suppressLocationSave = true;
        Left = -(Width + 100);
        Top  = 0;
        _suppressLocationSave = false;
        _bannerShown = false;
    }

    public void SetTitle(string? title)
    {
        _pendingTitle = title ?? string.Empty;
        PushIfReady("title", _pendingTitle);
    }

    public void SetStation(string? station)
    {
        _pendingStation = string.IsNullOrWhiteSpace(station) ? "Wingman Player" : station!;
        PushIfReady("station", _pendingStation);
    }

    public void SetHotkeyLabel(string label)
    {
        _pendingHotkey = string.IsNullOrWhiteSpace(label) ? "F8" : label;
        PushIfReady("hotkey", _pendingHotkey);
    }

    /// <summary>
    /// Toggle interactability. In edit mode the banner is interactable (drag, focus,
    /// hover, etc.); otherwise WS_EX_TRANSPARENT routes all mouse events to the
    /// window beneath. Edit mode is what the Miniplayer Settings sub-panel uses.
    /// </summary>
    public void SetEditMode(bool editing)
    {
        _editMode = editing;
        ApplyTransparentExStyle(!editing);
        if (editing && !_bannerShown)
            ShowBanner();
    }

    public void SetLocked(bool locked)
    {
        _settings.Save(_settings.Current with { BannerLocked = locked });
    }

    public void SetOpacity(double opacity)
    {
        var clamped = Math.Clamp(opacity, 0.20, 1.0);
        _settings.Save(_settings.Current with { BannerOpacity = clamped });
        if (_webViewReady)
            _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildOpacityScript(clamped));
    }

    public void SetScale(int scalePct)
    {
        var pct    = Math.Clamp(scalePct, 20, 120);
        var factor = pct / 100.0;
        _suppressLocationSave = true;
        Width  = Constants.BannerWidth  * factor;
        Height = Constants.BannerHeight * factor;
        _suppressLocationSave = false;
        if (_webViewReady)
            WebView.ZoomFactor = factor;
        _settings.Save(_settings.Current with { BannerScalePct = pct });
    }

    public void ResetPosition()
    {
        var work = SystemParameters.WorkArea;
        var newLeft = work.Left + (work.Width  - Width)  / 2;
        var newTop  = work.Top  + (work.Height - Height) / 2;
        Left = newLeft;
        Top  = newTop;
        _settings.Save(_settings.Current with { BannerLeft = newLeft, BannerTop = newTop });
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new HWND(new WindowInteropHelper(this).Handle);

        // Hide from Alt+Tab + initial click-through state.
        ApplyTransparentExStyle(transparent: true);
    }

    private void ApplyTransparentExStyle(bool transparent)
    {
        if (_hwnd == HWND.Null) return;
        var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        if (transparent)
            ex |=  (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        else
            ex &= ~(int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex);
        // Force the system to re-evaluate hit-testing & frame styles. Without this
        // the style bits are updated but mouse events keep passing through (or vice
        // versa) until the window is moved/resized.
        const SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED;
        PInvoke.SetWindowPos(_hwnd, HWND.Null, 0, 0, 0, 0, flags);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // Persist user-driven moves (drag) but ignore the off-screen parking we
        // do in HideBanner() and the snap performed by ShowBanner.
        if (_suppressLocationSave || !_bannerShown) return;
        if (Left < -1000) return; // off-screen guard
        _settings.Save(_settings.Current with { BannerLeft = Left, BannerTop = Top });
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Separate cache folder from the overlay — sharing a userDataFolder with
            // mismatched env options (overlay passes `--disk-cache-size=0`) makes the
            // second CreateAsync call throw, silently failing WebView2 init here.
            var cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Constants.AppDataFolderName,
                Constants.BannerWebView2CacheFolderName);

            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            _env = await CoreWebView2Environment.CreateAsync(userDataFolder: cacheFolder);
            await WebView.EnsureCoreWebView2Async(_env);

            WebView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;

            // Reuse the player's virtual host mapping so banner.html resolves
            // against the same Renderer/ folder.
            var rendererFolder = Path.Combine(AppContext.BaseDirectory, Constants.PlayerRendererFolder);
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                Constants.PlayerVirtualHost,
                rendererFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            WebView.Source = new Uri($"https://{Constants.PlayerVirtualHost}/banner.html");
            _webViewReady = true;
            WebView.ZoomFactor = _settings.Current.BannerScalePct / 100.0;
            _logger.LogInformation("Banner WebView2 ready");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Banner WebView2 initialisation failed");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        if (!msg.StartsWith('{')) return;

        try
        {
            using var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("type", out var t)) return;
            switch (t.GetString())
            {
                case "banner-ready":
                    _bannerReady = true;
                    FlushPending();
                    _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildOpacityScript(_settings.Current.BannerOpacity));
                    break;

                case "bannerDragStart":
                    // JS fires this on mousedown. Kick off a native window-drag loop via
                    // ReleaseCapture + WM_NCLBUTTONDOWN with HTCAPTION so Windows handles
                    // the move-with-mouse loop itself. Only honour it when the sub-panel
                    // is open and the banner is unlocked.
                    if (_editMode && !_settings.Current.BannerLocked)
                    {
                        const uint WM_NCLBUTTONDOWN = 0x00A1;
                        const int  HTCAPTION        = 2;
                        PInvoke.ReleaseCapture();
                        PInvoke.SendMessage(_hwnd, WM_NCLBUTTONDOWN, (WPARAM)(nuint)HTCAPTION, 0);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Banner web message parse error: {Ex}", ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void AnchorToSavedOrDefault()
    {
        var s = _settings.Current;
        _suppressLocationSave = true;
        if (s.BannerLeft.HasValue && s.BannerTop.HasValue)
        {
            Left = s.BannerLeft.Value;
            Top  = s.BannerTop.Value;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right  - Width  - Constants.BannerMargin;
            Top  = work.Bottom - Height - Constants.BannerMargin;
        }
        _suppressLocationSave = false;
    }

    private static string BuildOpacityScript(double opacity) =>
        $"document.body && (document.body.style.opacity={opacity:F3});";

    private void FlushPending()
    {
        Push("hotkey",  _pendingHotkey);
        Push("station", _pendingStation);
        Push("title",   _pendingTitle);
    }

    private void PushIfReady(string type, string value)
    {
        if (_webViewReady && _bannerReady)
            Push(type, value);
    }

    private void Push(string type, string value)
    {
        var payload = JsonSerializer.Serialize(new { type, value });
        try
        {
            WebView.CoreWebView2.PostWebMessageAsJson(payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Banner push failed: {Ex}", ex.Message);
        }
    }
}
