namespace wingman_player.Services;

using Microsoft.Extensions.Logging;
using UI;

/// <summary>
/// Thread-safe shim between the HTTP command surface (background accept thread)
/// and the WebView2 player (UI thread). The HTTP server is constructed during
/// host startup; the OverlayWindow is constructed afterwards. The bridge
/// late-binds: OverlayWindow registers itself on construction, and the HTTP
/// server resolves the bridge by DI and waits until the overlay is attached
/// before forwarding scripts.
///
/// Returning null/empty from the *Async methods means "not ready yet" — the
/// HTTP server surfaces that as 503 Service Unavailable.
/// </summary>
public sealed class PlayerCommandBridge
{
    private readonly ILogger<PlayerCommandBridge> _logger;
    private OverlayWindow? _overlay;

    public PlayerCommandBridge(ILogger<PlayerCommandBridge> logger)
    {
        _logger = logger;
    }

    public void AttachOverlay(OverlayWindow overlay)
    {
        _overlay = overlay;
        _logger.LogInformation("PlayerCommandBridge attached to OverlayWindow");
    }

    public bool IsReady => _overlay is not null;

    /// <summary>
    /// Latest update-check result, populated on app startup and whenever a
    /// manual check is run. Surfaced through /player/state so the skill (and
    /// therefore Wingman / the user) knows when an update is available.
    /// </summary>
    public UpdateInfo? LatestUpdate { get; set; }

    /// <summary>
    /// Fire-and-forget script. Returns true if the overlay was available and
    /// the script was dispatched; false if the overlay isn't attached yet.
    /// </summary>
    public async Task<bool> ExecuteAsync(string script)
    {
        var overlay = _overlay;
        if (overlay is null) return false;
        try
        {
            await overlay.ExecutePlayerScriptAsync(script);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bridge ExecuteAsync failed");
            return false;
        }
    }

    /// <summary>
    /// Script-with-result. Returns the JSON-encoded value the script evaluated
    /// to (per CoreWebView2.ExecuteScriptAsync), or null if the overlay isn't
    /// attached yet.
    /// </summary>
    public async Task<string?> ExecuteWithResultAsync(string script)
    {
        var overlay = _overlay;
        if (overlay is null) return null;
        try
        {
            return await overlay.ExecutePlayerScriptAsync(script);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bridge ExecuteWithResultAsync failed");
            return null;
        }
    }

    /// <summary>
    /// Bring the overlay on-screen if it isn't already. Used by HTTP-driven
    /// playback-starting commands (/player/load, /player/play, /next, /previous,
    /// /seek) so a voice request like "play X" actually shows the player —
    /// and so the WebView2 surface becomes visible, satisfying browser
    /// autoplay-policy heuristics that would otherwise hold playback in a
    /// "loaded but not playing" state on a hidden window.
    /// </summary>
    public async Task EnsureOverlayVisibleAsync()
    {
        var overlay = _overlay;
        if (overlay is null) return;
        try
        {
            await overlay.Dispatcher.InvokeAsync(overlay.EnsureVisible);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bridge EnsureOverlayVisibleAsync failed");
        }
    }

    /// <summary>
    /// Move the overlay off-screen if it isn't already hidden. Used by the
    /// "minimize player" voice tool and any HTTP-driven hide path (e.g. the
    /// idle-timeout expiry initiated from the renderer state-change stream).
    /// </summary>
    public async Task EnsureOverlayHiddenAsync()
    {
        var overlay = _overlay;
        if (overlay is null) return;
        try
        {
            await overlay.Dispatcher.InvokeAsync(overlay.EnsureHidden);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bridge EnsureOverlayHiddenAsync failed");
        }
    }

    /// <summary>
    /// Start a 15-second idle countdown after which the overlay self-hides.
    /// Used by /player/stop. Cancelled by any playback-starting command (or
    /// by a renderer state→PLAYING message).
    /// </summary>
    public void StartIdleHideCountdown()
    {
        _overlay?.StartIdleHideCountdown();
    }

    /// <summary>
    /// Cancel any running idle countdown. Called by playback-starting routes.
    /// </summary>
    public void CancelIdleHideCountdown()
    {
        _overlay?.CancelIdleHideCountdown();
    }
}
