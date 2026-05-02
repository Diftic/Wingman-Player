namespace wingman_player;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Models;
using Services;
using Settings;
using UI;
using System.Windows;

public partial class App : Application
{
    private IHost? _host;
    private TrayIcon? _trayIcon;
    private OverlayWindow? _overlay;
    private MiniBannerWindow? _banner;
    private GlobalHotkeyListener? _listener;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        var settings = _host.Services.GetRequiredService<SettingsManager>();
        _listener    = _host.Services.GetRequiredService<GlobalHotkeyListener>();
        var listener      = _listener;
        var overlayLogger = _host.Services.GetRequiredService<ILogger<OverlayWindow>>();
        var commandBridge = _host.Services.GetRequiredService<PlayerCommandBridge>();

        // OverlayWindow must be created on the STA (UI) thread.
        // Pre-size to the fixed frame canvas. Position off-screen so the WebView2
        // HwndHost initialises invisibly, then snap to centre on first show.
        _overlay = new OverlayWindow(settings, listener, overlayLogger, commandBridge);
        var overlay = _overlay;
        overlay.Width  = Constants.FrameDisplayWidth;
        overlay.Height = Constants.FrameDisplayHeight;
        overlay.Left   = -(Constants.FrameDisplayWidth + 100);
        overlay.Top    = 0;
        overlay.Show(); // WebView2 begins initialising in the background

        // Mini banner — minimised "now playing" tile in the lower-right.
        // Created up-front so its WebView2 is ready by the time the user first minimises.
        // Window stays Visible at an off-screen position; ShowBanner() snaps it to the
        // bottom-right, HideBanner() parks it off-screen again. Avoids the WebView2
        // init race that occurs when the window is collapsed before its first render.
        var bannerLogger = _host.Services.GetRequiredService<ILogger<MiniBannerWindow>>();
        _banner = new MiniBannerWindow(settings, bannerLogger);
        _banner.SetHotkeyLabel(settings.Current.ToggleHotkey.ToString());
        _banner.Show();
        var banner = _banner;

        // Check for updates while WebView2 loads; splash waits for the result.
        var updateLog  = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UpdateChecker");
        var splashLog  = _host.Services.GetRequiredService<ILogger<UI.SplashWindow>>();
        var updateInfo = await Services.UpdateChecker.CheckAsync(updateLog);

        var splash = new UI.SplashWindow(splashLog);
        splash.SetHotkeyLabel(settings.Current.ToggleHotkey.ToString());
        if (updateInfo.HasUpdate)
            splash.ShowUpdateBanner(updateInfo);
        splash.Show();

        // Tray icon
        _trayIcon = new TrayIcon();
        _trayIcon.ExitRequested += (_, _) => Dispatcher.Invoke(Shutdown);
        _trayIcon.ResetWindowRequested += (_, _) => overlay.ResetWindow();

        // Surface a previously-failed MSI update: if the last auto-update attempt
        // wrote a failure status, show a tray balloon and expose a manual retry.
        // Clearing the status prevents repeat warnings on later launches.
        var lastUpdate = Services.SelfUpdateService.ReadLastResult();
        if (lastUpdate is { Success: false })
        {
            _trayIcon.SetPendingUpdateRetry(lastUpdate.MsiPath);
            _trayIcon.ShowBalloon(
                "Update didn't install",
                $"msiexec returned {lastUpdate.ExitCode}. Right-click the tray icon → Retry failed update.",
                System.Windows.Forms.ToolTipIcon.Warning);
            Services.SelfUpdateService.ClearLastResult();
        }


        // First hotkey press closes the splash; subsequent presses just toggle.
        void OnFirstHotkey(object? s, EventArgs _)
        {
            Dispatcher.InvokeAsync(splash.Close);
            listener.HotkeyPressed -= OnFirstHotkey;
        }
        listener.HotkeyPressed += OnFirstHotkey;

        // Hotkey → tri-state cycle. Banner-visible → restore overlay. Overlay-visible →
        // hide overlay and (in Banner mode) raise the banner. Both hidden → show overlay.
        listener.HotkeyPressed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (banner.IsBannerVisible)
            {
                banner.HideBanner();
                overlay.EnsureVisible();
                return;
            }
            if (overlay.IsOverlayVisible)
            {
                overlay.Toggle();
                if (settings.Current.MinimizeMode == MinimizeMode.Banner)
                    banner.ShowBanner();
            }
            else
            {
                overlay.Toggle();
            }
        });

        overlay.BalloonTipRequested += (title, msg) => _trayIcon.ShowBalloon(title, msg);
        overlay.NowPlayingChanged   += t => Dispatcher.Invoke(() => banner.SetTitle(t));
        overlay.StationChanged      += s => Dispatcher.Invoke(() => banner.SetStation(s));

        // Miniplayer Settings sub-panel → banner control
        overlay.BannerEditModeChanged += on => Dispatcher.Invoke(() => banner.SetEditMode(on));
        overlay.BannerLockChanged     += on => Dispatcher.Invoke(() => banner.SetLocked(on));
        overlay.BannerOpacityChanged  += v  => Dispatcher.Invoke(() => banner.SetOpacity(v));
        overlay.BannerScaleChanged    += pct => Dispatcher.Invoke(() => banner.SetScale(pct));
        overlay.BannerResetRequested  += () => Dispatcher.Invoke(banner.ResetPosition);

        // Whenever the overlay is hidden, ensure the banner exits edit mode so it
        // returns to click-through. Avoids a stale interactable banner if the user
        // hides the overlay while the Miniplayer Settings panel was open.
        overlay.OverlayHidden += (_, _) => Dispatcher.Invoke(() => banner.SetEditMode(false));

        // Keep the banner's hotkey label in sync if the user remaps the hotkey,
        // and pull the banner down immediately if the user switches to Tray mode
        // while the banner is showing.
        settings.SettingsChanged += (_, s) => Dispatcher.Invoke(() =>
        {
            banner.SetHotkeyLabel(s.ToggleHotkey.ToString());
            if (s.MinimizeMode == MinimizeMode.Tray && banner.IsBannerVisible)
                banner.HideBanner();
        });
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<SettingsManager>();

        // PlayerConfig: system-side knobs loaded once at startup from
        // %APPDATA%\wingman_player\config.json. Holds the command-server port
        // (and any future system flags). Distinct from WingmanPlayerSettings,
        // which is the user-facing surface tied to the renderer's settings panel.
        services.AddSingleton(p => PlayerConfigLoader.Load(
            p.GetService<ILoggerFactory>()?.CreateLogger("PlayerConfig")));

        // Singletons exposed for direct resolution; also registered as hosted services so
        // the Generic Host starts and stops them automatically.
        services.AddSingleton<GlobalHotkeyListener>();
        services.AddHostedService(p => p.GetRequiredService<GlobalHotkeyListener>());

        // LocalAudioStreamServer: experimental loopback HTTP server that exposes
        // the bridge's captured PCM as an endless WAV at http://127.0.0.1:17329/stream.wav
        // for OBS Media Source. Singleton + hosted so AudioBridge can resolve it
        // and start/stop are tied to the host lifecycle.
        services.AddSingleton<LocalAudioStreamServer>();
        services.AddHostedService(p => p.GetRequiredService<LocalAudioStreamServer>());

        // PlayerCommandBridge: late-bound shim between the HTTP accept thread
        // and the WebView2 player on the UI thread. OverlayWindow registers
        // itself with the bridge in its constructor; the HTTP server resolves
        // the bridge here and waits until the overlay is attached.
        services.AddSingleton<PlayerCommandBridge>();

        // WingmanPlayerHttpServer: localhost command surface for Wingman skills
        // to drive the player (play/pause/stop/next/previous/seek/state). Bound to
        // 127.0.0.1 only, no auth — same trust model as the Accountant skill's
        // local channel. Source-agnostic: /player/play takes {source, ...} so a
        // future Spotify or local-file skill can drive the same transport.
        services.AddSingleton<WingmanPlayerHttpServer>();
        services.AddHostedService(p => p.GetRequiredService<WingmanPlayerHttpServer>());

        // AudioBridge: WASAPI process-loopback that captures WebView2 audio and
        // re-emits it from Wingman-Player.exe so OBS Window Capture's
        // Capture Audio (BETA) sees Wingman Player as an audio-producing process.
        services.AddHostedService<AudioBridge>();
    }
}
