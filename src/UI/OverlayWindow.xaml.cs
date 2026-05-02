namespace pulsenet.UI;

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Models;
using Models.Keyboard;
using Services;
using Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

public partial class OverlayWindow : Window
{
    private readonly SettingsManager _settings;
    private readonly GlobalHotkeyListener _hotkeyListener;
    private readonly ILogger<OverlayWindow> _logger;

    private HWND _hwnd;
    private HWND _chromeHwnd;  // cached WebView2 Chrome child HWND for scroll forwarding
    private HWND _prevFgHwnd;  // foreground window captured just before we steal it

    // Global low-level mouse hook — intercepts WM_MOUSEWHEEL regardless of focus
    private UnhookWindowsHookExSafeHandle? _mouseHookHandle;
    private HHOOK _mouseHookId = HHOOK.Null;
    private HOOKPROC? _mouseHookProc;
    private Thread? _mouseHookThread;
    private uint _mouseHookThreadId;

    // Global low-level keyboard hook — forwards an allow-list of YouTube playback /
    // volume keys to the embed when the cursor is over the video rect, without
    // requiring the user to click the video first.
    private UnhookWindowsHookExSafeHandle? _keyboardHookHandle;
    private HHOOK _keyboardHookId = HHOOK.Null;
    private HOOKPROC? _keyboardHookProc;
    private Thread? _keyboardHookThread;
    private uint _keyboardHookThreadId;
    private readonly HashSet<uint> _hoverKeysDown = new();

    // Allow-list: YouTube playback + volume keys. vk → renderer-side action name.
    private static readonly Dictionary<uint, string> HoverForwardKeys = new()
    {
        [0x20] = "space", // VK_SPACE  — play/pause
        [0x4B] = "space", // K         — play/pause (YouTube alias)
        [0x4D] = "mute",  // M         — mute toggle
        [0x25] = "left",  // VK_LEFT   — seek -5s
        [0x27] = "right", // VK_RIGHT  — seek +5s
        [0x26] = "up",    // VK_UP     — volume +5
        [0x28] = "down",  // VK_DOWN   — volume -5
    };

    private CoreWebView2Environment? _env;
    private bool   _webViewReady;
    private bool   _isVisible;
    private bool   _navigationErrorShown;
    private double _opacity    = 1.0;
    private int    _zoomPct   = 100;
    private bool   _dragLocked           = false;
    private double _dpiScale             = 1.0;
    private volatile bool _hookDragging  = false;
    private volatile int  _hookDragStartX, _hookDragStartY;
    private volatile int  _hookDragWinLeft, _hookDragWinTop;
    private volatile int  _lastHookMouseX, _lastHookMouseY;

    public event EventHandler? OverlayShown;
    public event EventHandler? OverlayHidden;
    public event Action<string, string>? BalloonTipRequested;
    public event Action<string>? NowPlayingChanged;
    public event Action<string>? StationChanged;
    public event Action<bool>?   BannerEditModeChanged;
    public event Action<bool>?   BannerLockChanged;
    public event Action<double>? BannerOpacityChanged;
    public event Action<int>? BannerScaleChanged;
    public event Action?         BannerResetRequested;

    public OverlayWindow(
        SettingsManager settings,
        GlobalHotkeyListener hotkeyListener,
        ILogger<OverlayWindow> logger)
    {
        _settings = settings;
        _hotkeyListener = hotkeyListener;
        _logger = logger;

        InitializeComponent();

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public void Toggle()
    {
        if (_isVisible)
            HideOverlay(restoreFocus: true);
        else
            ShowOverlay();
    }

    public bool IsOverlayVisible => _isVisible;

    public void EnsureVisible()
    {
        if (!_isVisible)
            ShowOverlay();
    }

    /// <summary>
    /// Hides without restoring foreground — used when an external app already took foreground.
    /// </summary>
    public void EnsureHidden()
    {
        if (_isVisible)
            HideOverlay(restoreFocus: false);
    }

    /// <summary>
    /// Recovery action invoked from the tray menu: restore zoom to 100%, re-centre on
    /// the primary monitor, persist, and ensure the overlay is visible. Lets a tester
    /// recover when the window has been resized tiny and dragged off-screen.
    /// </summary>
    public void ResetWindow()
    {
        Dispatcher.Invoke(() =>
        {
            ApplyZoom(100);
            CenterOnScreen();
            _settings.Save(_settings.Current with
            {
                WindowLeft = Left,
                WindowTop = Top,
                WebViewZoomPct = _zoomPct,
            });
            EnsureVisible();
            // Push the new zoom value into the settings panel's input so the displayed
            // value matches the actual state. ShowOverlay already syncs on first show,
            // but a reset while the overlay is already visible needs an explicit push.
            if (_webViewReady)
                _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildSyncScript(_opacity, _zoomPct));
            _logger.LogInformation("Window reset to default size and centred on primary monitor");
        });
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new HWND(new WindowInteropHelper(this).Handle);

        // Cache DPI scale (physical px per WPF DIP) for coordinate conversion in mouse hook.
        var src = PresentationSource.FromVisual(this);
        _dpiScale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        // WndProc hook — catches WM_MOUSEWHEEL when the overlay window itself has focus.
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);

        // Mouse hook proc delegate kept alive here; thread is started/stopped with the overlay.
        _mouseHookProc = MouseHookProc;
        _keyboardHookProc = KeyboardHookProc;

        // Solution 1.5: explicitly clear WS_EX_TOOLWINDOW so OBS Window Capture
        // (and any other capture tool that filters tool windows out of its source
        // list) can see the player. Alt+Tab and taskbar hiding are still
        // achieved by WPF's hidden-owner pattern (created automatically from
        // ShowInTaskbar="False"); the tool-window flag was redundant for that
        // purpose and was the single thing locking streamers out of OBS.
        var exStyle = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(
            _hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            exStyle & ~(int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await InitializeWebViewAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        StopMouseHook();
        StopKeyboardHook();
        base.OnClosed(e);
    }

    // -------------------------------------------------------------------------
    // Show / Hide
    // -------------------------------------------------------------------------

    private void ShowOverlay()
    {
        if (!_webViewReady)
        {
            _logger.LogDebug("Overlay toggled before WebView2 was ready — ignoring");
            return;
        }

        StartMouseHook();
        StartKeyboardHook();

        var s = _settings.Current;
        if (s.WindowLeft.HasValue && s.WindowTop.HasValue)
        {
            Left = s.WindowLeft.Value;
            Top  = s.WindowTop.Value;
        }
        else
        {
            CenterOnScreen();
        }

        _isVisible = true;
        Visibility = Visibility.Visible;
        UpdateLayout();

        StealForeground();

        // Re-inject styles, restore zoom, and sync slider UI every time the overlay is shown.
        _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildOverlayStyleScript(_opacity));
        _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildSyncScript(_opacity, _zoomPct));
        WebView.ZoomFactor = _zoomPct / 100.0;

        OverlayShown?.Invoke(this, EventArgs.Empty);
        _logger.LogDebug("Overlay shown");
    }

    private void HideOverlay(bool restoreFocus)
    {
        StopMouseHook();
        StopKeyboardHook();

        // Persist the on-screen position BEFORE we move off-screen so the next
        // ShowOverlay can restore it. The off-screen sentinel must never be the
        // saved value.
        _settings.Save(_settings.Current with { WindowLeft = Left, WindowTop = Top, WebViewZoomPct = _zoomPct });

        _isVisible = false;

        // Solution 1.5: park off-screen instead of collapsing visibility. WPF
        // continues compositing the window, DWM keeps rendering it, and OBS
        // Window Capture (WGC mode) keeps streaming the player to viewers
        // pixel-perfectly — even though the streamer no longer sees it on any
        // of their monitors. F9 to show again moves it back to the saved pos.
        Left = -(Constants.FrameDisplayWidth + 100);
        Top  = 0;

        if (restoreFocus && _prevFgHwnd != HWND.Null)
            PInvoke.SetForegroundWindow(_prevFgHwnd);

        OverlayHidden?.Invoke(this, EventArgs.Empty);
        _logger.LogDebug("Overlay parked off-screen (restoreFocus={R})", restoreFocus);
    }

    // -------------------------------------------------------------------------
    // Settings hot-reload
    // -------------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, PulsenetSettings settings)
    {
        Dispatcher.Invoke(() =>
        {
            if (settings.WebViewZoomPct != _zoomPct)
                ApplyZoom(settings.WebViewZoomPct);
        });
    }

    private static string BuildPlayerUrl() =>
        $"https://{Constants.PlayerVirtualHost}/index.html";

    // -------------------------------------------------------------------------
    // Focus helpers
    // -------------------------------------------------------------------------

    private unsafe void StealForeground()
    {
        var fg = PInvoke.GetForegroundWindow();

        if (fg != HWND.Null && !IsOurProcess(fg))
            _prevFgHwnd = fg;

        uint fgPid;
        var fgThread = PInvoke.GetWindowThreadProcessId(fg, &fgPid);
        var uiThread = PInvoke.GetCurrentThreadId();

        bool attached = fgThread != 0 && fgThread != uiThread
            && PInvoke.AttachThreadInput(fgThread, uiThread, true);

        PInvoke.BringWindowToTop(_hwnd);

        if (attached)
            PInvoke.AttachThreadInput(fgThread, uiThread, false);

        _logger.LogDebug("StealForeground: prevFg={Fg:X} attached={Att}", (nint)_prevFgHwnd.Value, attached);
    }

    private static unsafe bool IsOurProcess(HWND hwnd)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid == (uint)Environment.ProcessId;
    }

    private unsafe void ForceToForeground()
    {
        var fgHwnd   = PInvoke.GetForegroundWindow();
        uint pid;
        var fgThread = PInvoke.GetWindowThreadProcessId(fgHwnd, &pid);
        var uiThread = PInvoke.GetCurrentThreadId();

        _logger.LogDebug(
            "ForceToForeground: fgHwnd={Fg:X} fgThread={FgT} uiThread={UiT}",
            (nint)fgHwnd.Value, fgThread, uiThread);

        var attached = fgThread != uiThread
            && PInvoke.AttachThreadInput(fgThread, uiThread, true);

        var fgResult    = PInvoke.SetForegroundWindow(_hwnd);
        var topResult   = PInvoke.BringWindowToTop(_hwnd);
        var focusResult = PInvoke.SetFocus(_hwnd);

        _logger.LogDebug(
            "ForceToForeground: attached={Att} SetForeground={Fg} BringToTop={Top} SetFocus={Foc}",
            attached, (bool)fgResult, (bool)topResult, focusResult != HWND.Null);

        if (attached)
            PInvoke.AttachThreadInput(fgThread, uiThread, false);
    }

    public void ApplyOpacity(double value)
    {
        _opacity = Math.Clamp(value, 0.1, 1.0);
        _logger.LogDebug("ApplyOpacity: {Opacity:F2}", _opacity);
        if (_webViewReady)
            _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildOverlayStyleScript(_opacity));
    }

    public void ApplyZoom(int zoomPct)
    {
        _zoomPct = Math.Clamp(zoomPct, 30, 100);
        _logger.LogDebug("ApplyZoom: {Zoom}%", _zoomPct);
        double factor = _zoomPct / 100.0;
        // Resize the window and scale WebView2 content in the same operation to avoid blink.
        Width  = Constants.FrameDisplayWidth  * factor;
        Height = Constants.FrameDisplayHeight * factor;
        if (_webViewReady)
            WebView.ZoomFactor = factor;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();

        if (!msg.StartsWith('{')) return;

        try
        {
            using var doc  = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;

            switch (t.GetString())
            {
                case "lock":
                    _dragLocked = root.GetProperty("locked").GetBoolean();
                    _hookDragging = false;
                    break;

                case "startDrag":
                    if (!_dragLocked)
                    {
                        PInvoke.GetWindowRect(_hwnd, out var dragRect);
                        _hookDragStartX  = _lastHookMouseX;
                        _hookDragStartY  = _lastHookMouseY;
                        _hookDragWinLeft = dragRect.left;
                        _hookDragWinTop  = dragRect.top;
                        _hookDragging    = true;
                    }
                    break;

                case "opacity":
                    var opacity = root.GetProperty("value").GetDouble();
                    Dispatcher.Invoke(() => ApplyOpacity(opacity));
                    break;

                case "zoom":
                    var pct = root.GetProperty("pct").GetInt32();
                    Dispatcher.Invoke(() =>
                    {
                        ApplyZoom(pct);
                        _settings.Save(_settings.Current with { WebViewZoomPct = _zoomPct });
                    });
                    break;

                case "hotkey-focus":
                    _hotkeyListener.Paused = root.GetProperty("active").GetBoolean();
                    break;

                case "hoverVideo":
                    _cursorOverVideo = root.GetProperty("over").GetBoolean();
                    break;

                case "keyForwardCapable":
                    _canForwardKeys = root.GetProperty("value").GetBoolean();
                    break;

                case "nowPlaying":
                    var title = root.GetProperty("title").GetString() ?? string.Empty;
                    NowPlayingChanged?.Invoke(title);
                    break;

                case "currentStation":
                    var label = root.GetProperty("label").GetString() ?? string.Empty;
                    StationChanged?.Invoke(label);
                    break;

                case "bannerEditMode":
                    BannerEditModeChanged?.Invoke(root.GetProperty("value").GetBoolean());
                    break;

                case "bannerLock":
                    BannerLockChanged?.Invoke(root.GetProperty("locked").GetBoolean());
                    break;

                case "bannerOpacity":
                    BannerOpacityChanged?.Invoke(root.GetProperty("value").GetDouble());
                    break;

                case "bannerScale":
                    BannerScaleChanged?.Invoke(root.GetProperty("value").GetInt32());
                    break;

                case "bannerReset":
                    BannerResetRequested?.Invoke();
                    break;

                case "minimizeMode":
                    var modeStr = root.GetProperty("value").GetString();
                    if (Enum.TryParse<Models.MinimizeMode>(modeStr, ignoreCase: true, out var mode))
                        _settings.Save(_settings.Current with { MinimizeMode = mode });
                    break;

                case "openUrl":
                    OpenInDefaultBrowser(root.GetProperty("url").GetString());
                    break;

                case "hotkey":
                    var keys = root.GetProperty("keys").EnumerateArray()
                        .Select(k => k.GetString() ?? "")
                        .Select(name => Enum.TryParse<KeyboardKey>(name, out var k) ? k : KeyboardKey.Unknown)
                        .Where(k => k != KeyboardKey.Unknown)
                        .ToArray();
                    var shortcut = new KeyboardShortcut(keys);
                    if (shortcut.IsValid)
                        _settings.Save(_settings.Current with { ToggleHotkey = shortcut });
                    break;

                case "checkForUpdates":
                    _ = HandleCheckForUpdatesAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Unhandled web message: {Ex}", ex.Message);
        }
    }

    // CSS opacity on the html element is the correct approach for WebView2 —
    // Window.Opacity and SetLayeredWindowAttributes both fail because WebView2's
    // DirectComposition surface bypasses the host window's alpha channel.
    // CSS opacity runs inside Chromium's GPU compositor and correctly blends
    // the page against the transparent WPF window background.
    // Zoom is handled by WebView.ZoomFactor (synchronous, no blink).
    // CSS here covers only opacity and transparency.
    private static string BuildOverlayStyleScript(double opacity) =>
        $"(function(){{" +
        $"var s=document.getElementById('__ol_style');" +
        $"if(!s){{s=document.createElement('style');s.id='__ol_style';" +
        $"(document.head||document.documentElement).appendChild(s);}}" +
        $"s.textContent='html,body{{background:transparent!important}}" +
        $"html{{opacity:{opacity:F3}!important}}';" +
        $"}})();";

    // Syncs slider positions and displayed values to match current C# state.
    // Called on ShowOverlay so the UI reflects actual state after a hide/show cycle.
    private string BuildSyncScript(double opacity, int zoomPct)
    {
        int opacityDisplay = (int)Math.Round((opacity - 0.30) / 0.70 * 100);
        var hotkeyLabel = System.Text.Json.JsonSerializer.Serialize(_settings.Current.ToggleHotkey.ToString());
        var minimizeMode = System.Text.Json.JsonSerializer.Serialize(_settings.Current.MinimizeMode.ToString());
        var s = _settings.Current;
        int bannerScale = s.BannerScalePct;
        int bannerOp = (int)Math.Round(s.BannerOpacity * 100);
        var bannerLockText = s.BannerLocked
            ? "\\uD83D\\uDD12 Banner locked"
            : "\\uD83D\\uDD13 Banner unlocked";
        var versionLiteral = System.Text.Json.JsonSerializer.Serialize(GetAppVersionLabel());
        return $"(function(){{" +
               $"var os=document.getElementById('opacity-slider');" +
               $"var ov=document.getElementById('opacity-val');" +
               $"var zi=document.getElementById('zoom-input');" +
               $"var hi=document.getElementById('hotkey-input');" +
               $"var ms=document.getElementById('minimize-mode-select');" +
               $"var sp=document.getElementById('settings-panel');" +
               $"var mp=document.getElementById('miniplayer-settings-panel');" +
               $"var stp=document.getElementById('streamer-settings-panel');" +
               $"var bsc=document.getElementById('banner-scale-input');" +
               $"var bos=document.getElementById('banner-opacity-slider');" +
               $"var bov=document.getElementById('banner-opacity-val');" +
               $"var blb=document.getElementById('banner-lock-btn');" +
               $"if(os){{os.value={opacityDisplay};if(ov)ov.textContent='{opacityDisplay}%';}}" +
               $"if(zi)zi.value={zoomPct};" +
               $"if(hi)hi.value={hotkeyLabel};" +
               $"if(ms)ms.value={minimizeMode};" +
               $"if(bsc)bsc.value={bannerScale};" +
               $"if(bos){{bos.value={bannerOp};if(bov)bov.textContent='{bannerOp}%';}}" +
               $"if(blb){{blb.textContent='{bannerLockText}';blb.classList.toggle('locked',{(s.BannerLocked ? "true" : "false")});}}" +
               $"window.__pulsenetVersion={versionLiteral};" +
               $"if(typeof window.__pulsenetRefreshVersionLabel==='function')window.__pulsenetRefreshVersionLabel();" +
               // Reset to main settings page when overlay re-opens.
               $"if(sp)sp.classList.add('hidden');" +
               $"if(mp)mp.classList.add('hidden');" +
               $"if(stp)stp.classList.add('hidden');" +
               $"}})();";
    }

    private static string GetAppVersionLabel()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // User-initiated update check from the settings-panel version banner.
    // CheckAsync runs off the UI thread; the prompt and any side-effects
    // (Application.Shutdown for the in-place update) must marshal back.
    private async Task HandleCheckForUpdatesAsync()
    {
        UpdateInfo info;
        try
        {
            info = await UpdateChecker.CheckAsync(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual update check threw");
            info = new UpdateInfo(false, string.Empty, string.Empty, string.Empty);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var current = GetAppVersionLabel();
                if (info.HasUpdate)
                {
                    var result = MessageBox.Show(
                        this,
                        $"A new version is available.\n\nCurrent: v{current}\nLatest: v{info.Version}\n\nUpdate and restart now?",
                        "PulseNet Player: Update available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        _ = SelfUpdateService.ApplyAsync(
                            info,
                            () => Dispatcher.Invoke(() => Application.Current.Shutdown()),
                            _logger);
                    }
                }
                else
                {
                    MessageBox.Show(
                        this,
                        $"PulseNet Player is up to date (v{current}).",
                        "PulseNet Player",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            finally
            {
                if (_webViewReady)
                {
                    _ = WebView.CoreWebView2.ExecuteScriptAsync(
                        "if(typeof window.__pulsenetUpdateCheckDone==='function')window.__pulsenetUpdateCheckDone();");
                }
            }
        });
    }

    // -------------------------------------------------------------------------
    // Global mouse hook — WH_MOUSE_LL intercepts WM_MOUSEWHEEL system-wide.
    // Installed only while the overlay is visible so it never adds latency to
    // other apps when the overlay is hidden.
    // -------------------------------------------------------------------------

    private void StartMouseHook()
    {
        if (_mouseHookThread is { IsAlive: true }) return;
        _mouseHookThread = new Thread(RunMouseHook) { IsBackground = true, Name = "MouseHook" };
        _mouseHookThread.Start();
    }

    private void StopMouseHook()
    {
        if (_mouseHookThreadId != 0)
            PInvoke.PostThreadMessage(_mouseHookThreadId, PInvoke.WM_QUIT, 0, 0);
        _mouseHookHandle?.Dispose();
        _mouseHookHandle = null;
        _mouseHookId = HHOOK.Null;
        _mouseHookThreadId = 0;
    }

    private void RunMouseHook()
    {
        _mouseHookThreadId = PInvoke.GetCurrentThreadId();

        var handle = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseHookProc!, default, 0);
        if (handle.IsInvalid)
        {
            _logger.LogWarning("Failed to install mouse hook");
            return;
        }

        _mouseHookHandle = handle;
        _mouseHookId = new HHOOK(handle.DangerousGetHandle());
        _logger.LogInformation("Mouse hook installed");

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    private LRESULT MouseHookProc(int nCode, WPARAM wparam, LPARAM lparam)
    {
        const uint WM_MOUSEWHEEL = 0x020A;
        const uint WM_MOUSEMOVE  = 0x0200;
        const uint WM_LBUTTONUP  = 0x0202;
        // SWP_ASYNCWINDOWPOS posts the move to the UI thread without blocking the hook.
        const SET_WINDOW_POS_FLAGS SWP_ASYNC =
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (SET_WINDOW_POS_FLAGS)0x4000;

        if (nCode >= 0 && _isVisible)
        {
            var msg = (uint)wparam.Value;
            var hs  = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lparam);

            // Always track cursor position so startDrag (from JS) can use the exact
            // mousedown coordinates even though it arrives slightly later via IPC.
            _lastHookMouseX = hs.pt.X;
            _lastHookMouseY = hs.pt.Y;

            // Drag is started exclusively by JS via {type:'startDrag'} — JS knows
            // whether the click was on frame space vs. a button or the video area.
            if (msg == WM_MOUSEMOVE && _hookDragging)
            {
                int newLeft = _hookDragWinLeft + (hs.pt.X - _hookDragStartX);
                int newTop  = _hookDragWinTop  + (hs.pt.Y - _hookDragStartY);
                PInvoke.SetWindowPos(_hwnd, HWND.Null, newLeft, newTop, 0, 0, SWP_ASYNC);
            }
            else if (msg == WM_LBUTTONUP)
            {
                if (_hookDragging)
                {
                    _hookDragging = false;
                    // Sync WPF Left/Top so position-save in HideOverlay is accurate.
                    PInvoke.GetWindowRect(_hwnd, out var rect);
                    Dispatcher.BeginInvoke(() => { Left = rect.left / _dpiScale; Top = rect.top / _dpiScale; });
                }
            }
        }

        if (nCode >= 0 && (uint)wparam.Value == WM_MOUSEWHEEL && _isVisible && _webViewReady)
        {
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lparam);

            // Only intercept scroll when the cursor is inside the overlay window.
            PInvoke.GetWindowRect(_hwnd, out var winRect);
            if (hs.pt.X < winRect.left || hs.pt.X > winRect.right ||
                hs.pt.Y < winRect.top  || hs.pt.Y > winRect.bottom)
                return PInvoke.CallNextHookEx(_mouseHookId, nCode, wparam, lparam);

            short delta = (short)(hs.mouseData >> 16);
            int scrollPx = -(delta / 120) * 100;

            int cx = hs.pt.X;
            int cy = hs.pt.Y;

            _logger.LogDebug("MouseHook scroll: delta={D} px={P} at ({X},{Y})", delta, scrollPx, cx, cy);

            Dispatcher.InvokeAsync(() =>
            {
                if (!_webViewReady) return;

                var script =
                    $"(function(){{" +
                    $"var el=document.elementFromPoint({cx},{cy});" +
                    $"while(el&&el!==document.documentElement){{" +
                    $"var s=window.getComputedStyle(el);" +
                    $"var ov=s.overflowY;" +
                    $"if((ov==='auto'||ov==='scroll')&&el.scrollHeight>el.clientHeight){{" +
                    $"el.scrollTop+={scrollPx};return;" +
                    $"}}" +
                    $"el=el.parentElement;}}" +
                    $"window.scrollBy(0,{scrollPx});" +
                    $"}})();";

                _ = WebView.CoreWebView2.ExecuteScriptAsync(script);
            });

            return (LRESULT)1;
        }

        return PInvoke.CallNextHookEx(_mouseHookId, nCode, wparam, lparam);
    }

    // -------------------------------------------------------------------------
    // Global keyboard hook — WH_KEYBOARD_LL forwards a small allow-list of
    // YouTube playback / volume keys to the embed when the cursor hovers the
    // video rect. Without this the user has to click the video to give the
    // iframe focus before the embed responds to keys, which the non-activating
    // overlay design otherwise prevents.
    // -------------------------------------------------------------------------

    private void StartKeyboardHook()
    {
        if (_keyboardHookThread is { IsAlive: true }) return;
        _hoverKeysDown.Clear();
        _keyboardHookThread = new Thread(RunKeyboardHook) { IsBackground = true, Name = "KeyboardHook" };
        _keyboardHookThread.Start();
    }

    private void StopKeyboardHook()
    {
        if (_keyboardHookThreadId != 0)
            PInvoke.PostThreadMessage(_keyboardHookThreadId, PInvoke.WM_QUIT, 0, 0);
        _keyboardHookHandle?.Dispose();
        _keyboardHookHandle = null;
        _keyboardHookId = HHOOK.Null;
        _keyboardHookThreadId = 0;
        _hoverKeysDown.Clear();
    }

    private void RunKeyboardHook()
    {
        _keyboardHookThreadId = PInvoke.GetCurrentThreadId();

        var handle = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardHookProc!, default, 0);
        if (handle.IsInvalid)
        {
            _logger.LogWarning("Failed to install keyboard hook");
            return;
        }

        _keyboardHookHandle = handle;
        _keyboardHookId = new HHOOK(handle.DangerousGetHandle());
        _logger.LogInformation("Keyboard hook installed");

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    private LRESULT KeyboardHookProc(int nCode, WPARAM wparam, LPARAM lparam)
    {
        const uint WM_KEYDOWN    = 0x0100;
        const uint WM_KEYUP      = 0x0101;
        const uint WM_SYSKEYDOWN = 0x0104;
        const uint WM_SYSKEYUP   = 0x0105;

        if (nCode >= 0 && _isVisible && _webViewReady)
        {
            var msg = (uint)wparam.Value;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lparam);
                if (HoverForwardKeys.TryGetValue(kbd.vkCode, out var action)
                    && _cursorOverVideo
                    && _canForwardKeys)
                {
                    if (isDown)
                    {
                        // Skip auto-repeat: only forward on transition up→down so a
                        // held Space/M doesn't toggle play/mute repeatedly.
                        if (_hoverKeysDown.Add(kbd.vkCode))
                        {
                            var name = action;
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (_webViewReady)
                                    _ = WebView.CoreWebView2.ExecuteScriptAsync(
                                        $"window.__pulsenetForwardKey&&window.__pulsenetForwardKey('{name}');");
                            });
                        }
                    }
                    else
                    {
                        _hoverKeysDown.Remove(kbd.vkCode);
                    }
                    return (LRESULT)1; // swallow so the foreground app doesn't also receive it
                }
            }
        }

        return PInvoke.CallNextHookEx(_keyboardHookId, nCode, wparam, lparam);
    }

    // Hover state pushed by the renderer (mouseover/mouseleave on #video-wrap).
    // Avoids DPI/zoom math — the renderer always knows precisely when the cursor
    // is over the video element, regardless of monitor DPI or window zoom.
    private volatile bool _cursorOverVideo;

    // Pushed by the renderer whenever the API player is created/destroyed or the
    // user toggles between API mode and the raw live-stream iframe. The hook
    // only swallows keys when we're actually able to forward them — otherwise
    // it falls through, preserving YouTube's native click-then-keys flow.
    private volatile bool _canForwardKeys;

    // -------------------------------------------------------------------------
    // Scroll forwarding via WndProc
    // -------------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST   = 0x0084;
        const int WM_MOUSEWHEEL  = 0x020A;
        const int HTCLIENT       = 1;

        // With AllowsTransparency=True and a fully transparent WPF background,
        // WPF returns HTTRANSPARENT for every pixel, so Windows routes all mouse
        // events past the window to whatever lies behind it — including the WebView2
        // child HWND.  Intercepting WM_NCHITTEST and returning HTCLIENT tells
        // Windows the entire client area is interactive and delivers events normally.
        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTCLIENT);
        }

        if (msg == WM_MOUSEWHEEL && _webViewReady)
        {
            if (_chromeHwnd == HWND.Null)
                _chromeHwnd = FindChromeChildHwnd(_hwnd);

            if (_chromeHwnd != HWND.Null)
            {
                PInvoke.PostMessage(_chromeHwnd, (uint)WM_MOUSEWHEEL, (nuint)(nint)wParam, (nint)lParam);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private static unsafe HWND FindChromeChildHwnd(HWND parent)
    {
        HWND found = HWND.Null;
        var sb = new System.Text.StringBuilder(256);

        PInvoke.EnumChildWindows(parent, (child, _) =>
        {
            sb.Clear();
            GetWindowClassName((nint)child.Value, sb, sb.Capacity);
            if (sb.ToString().Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return false;
            }
            return true;
        }, 0);

        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetWindowClassName(nint hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // -------------------------------------------------------------------------
    // Positioning
    // -------------------------------------------------------------------------

    private void CenterOnScreen()
    {
        var sw = SystemParameters.PrimaryScreenWidth;
        var sh = SystemParameters.PrimaryScreenHeight;
        Left = (sw - Width)  / 2;
        Top  = (sh - Height) / 2;
    }

    // -------------------------------------------------------------------------
    // WebView2
    // -------------------------------------------------------------------------

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Constants.AppDataFolderName,
                Constants.WebView2CacheFolderName);

            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            // Disable disk cache so local Renderer file changes take effect immediately
            // without requiring a manual cache clear between runs.
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disk-cache-size=0"
            };
            _env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: cacheFolder,
                options: envOptions);
            await WebView.EnsureCoreWebView2Async(_env);
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            WebView.CoreWebView2.NavigationStarting  += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.NewWindowRequested  += OnNewWindowRequested;
            WebView.CoreWebView2.WebMessageReceived  += OnWebMessageReceived;

            // Define window.__pulsenetVersion before any page script runs, so the
            // first render of the version label sees the real number instead of
            // the "0.0.0" placeholder. BuildSyncScript still re-injects on every
            // overlay show so a future in-place update is picked up without restart.
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__pulsenetVersion={System.Text.Json.JsonSerializer.Serialize(GetAppVersionLabel())};");

            // Map the virtual hostname to the local Renderer folder so the player
            // page is served over https (required for YouTube IFrame API to work).
            var rendererFolder = Path.Combine(AppContext.BaseDirectory, Constants.PlayerRendererFolder);
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                Constants.PlayerVirtualHost,
                rendererFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            // Intercept all pulsenet.local requests and serve renderer files directly
            // with Cache-Control: no-store so CSS/JS changes take effect immediately
            // after a rebuild without requiring a manual cache clear.
            WebView.CoreWebView2.AddWebResourceRequestedFilter(
                $"https://{Constants.PlayerVirtualHost}/*",
                CoreWebView2WebResourceContext.All);
            WebView.CoreWebView2.WebResourceRequested += OnRendererResourceRequested;

            var initialUrl = BuildPlayerUrl();
            WebView.Source = new Uri(initialUrl);

            _webViewReady = true;
            _logger.LogInformation("WebView2 ready — player URL: {Url}", initialUrl);

            ApplyZoom(_settings.Current.WebViewZoomPct);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _logger.LogError("WebView2 runtime is not installed");
            BalloonTipRequested?.Invoke(
                "WebView2 not installed",
                "PulseNet Player requires the Microsoft Edge WebView2 Runtime. Please install it and restart.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebView2 initialization failed");
        }
    }

    private void OnRendererResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri      = new Uri(e.Request.Uri);
            var relative = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative))
                relative = "index.html";

            var rendererFolder = Path.Combine(AppContext.BaseDirectory, Constants.PlayerRendererFolder);
            var filePath = Path.Combine(rendererFolder, relative);

            // Security: ensure the resolved path stays inside the renderer folder.
            if (!filePath.StartsWith(rendererFolder, StringComparison.OrdinalIgnoreCase))
                return;

            if (!File.Exists(filePath)) return;

            var mime = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".html"         => "text/html; charset=utf-8",
                ".css"          => "text/css; charset=utf-8",
                ".js"           => "application/javascript; charset=utf-8",
                ".png"          => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg"          => "image/svg+xml",
                ".ico"          => "image/x-icon",
                _               => "application/octet-stream",
            };

            var stream = File.OpenRead(filePath);
            e.Response = _env!.CreateWebResourceResponse(
                stream, 200, "OK",
                $"Content-Type: {mime}\r\nCache-Control: no-store, no-cache");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Renderer resource handler error: {Ex}", ex.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Allow internal WebView2 URIs (data:, about:blank etc.)
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (IsAllowedOrigin(uri)) return;

        // Non-allowed main-frame navigation (e.g. OAuth redirect or external link) —
        // cancel and open in a shared-session popup so auth cookies stay in this env.
        _logger.LogDebug("Opening external origin in popup: {Uri}", e.Uri);
        e.Cancel = true;
        OpenAsAuthPopup(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webViewReady = true;
            _navigationErrorShown = false;
            _ = WebView.CoreWebView2.ExecuteScriptAsync(BuildOverlayStyleScript(_opacity));
            return;
        }

        // OperationCanceled is raised when we cancel the navigation ourselves — not a real error.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;

        if (_navigationErrorShown) return;
        _navigationErrorShown = true;

        _logger.LogWarning("Navigation failed: {Error}", e.WebErrorStatus);

        WebView.NavigateToString(
            "<html><body style='background:#1a1a1a;color:#ccc;font-family:sans-serif;padding:24px'>" +
            "<h2 style='color:#e88'>Could not load PulseNet Player</h2>" +
            "<p>The local player page failed to load.</p>" +
            "<p>Ensure the Renderer folder is present alongside the executable.</p>" +
            "</body></html>");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (_env is null)
        {
            e.Handled = true;
            OpenInDefaultBrowser(e.Uri);
            return;
        }

        // All new-window requests (OAuth popups, YouTube sign-in, etc.) are opened
        // inside a shared WebView2 popup so auth cookies end up in the same environment.
        var deferral = e.GetDeferral();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wv = new Microsoft.Web.WebView2.Wpf.WebView2();
                var popup = new Window
                {
                    Title = "Sign In",
                    Width  = e.WindowFeatures.HasSize ? e.WindowFeatures.Width  : 500,
                    Height = e.WindowFeatures.HasSize ? e.WindowFeatures.Height : 700,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Content = wv,
                };

                popup.Show();
                await wv.EnsureCoreWebView2Async(_env);

                wv.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.Invoke(popup.Close);
                wv.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

                e.NewWindow = wv.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create popup; falling back to browser");
                e.Handled = true;
                OpenInDefaultBrowser(e.Uri);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OpenAsAuthPopup(string uri)
    {
        if (_env is null)
        {
            _logger.LogWarning("Auth popup requested but WebView2 env not ready; falling back to browser");
            OpenInDefaultBrowser(uri);
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wv = new Microsoft.Web.WebView2.Wpf.WebView2();
                var popup = new Window
                {
                    Title = "Sign In",
                    Width  = 500,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Content = wv,
                };

                popup.Show();
                await wv.EnsureCoreWebView2Async(_env);

                wv.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var dest)) return;
                    if (!IsAllowedOrigin(dest)) return;

                    // Auth callback redirected back to our origin — complete in main window.
                    _logger.LogInformation("Auth popup returned to player origin; completing in main window");
                    args.Cancel = true;
                    Dispatcher.Invoke(() =>
                    {
                        popup.Close();
                        WebView.Source = dest;
                    });
                };

                wv.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.Invoke(popup.Close);
                wv.CoreWebView2.NewWindowRequested  += OnNewWindowRequested;

                wv.Source = new Uri(uri);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open auth popup; falling back to browser");
                OpenInDefaultBrowser(uri);
            }
        });
    }

    private static void OpenInDefaultBrowser(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private bool IsAllowedOrigin(Uri uri)
    {
        // Only the local player virtual host is allowed in the main frame.
        // All other origins (YouTube, Google OAuth, etc.) become popups so that
        // auth cookies remain in the same WebView2 environment.
        return string.Equals(uri.Host, Constants.PlayerVirtualHost, StringComparison.OrdinalIgnoreCase);
    }
}
