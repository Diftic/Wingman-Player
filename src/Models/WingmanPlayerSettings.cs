namespace wingman_player.Models;

using Keyboard;

public enum MinimizeMode
{
    Banner,
    Tray,
}

public record WingmanPlayerSettings
{
    public KeyboardShortcut ToggleHotkey    { get; set; } = new([KeyboardKey.F8]);
    public int WebViewZoomPct               { get; set; } = 100;
    public Guid InstallationId              { get; init; } = Guid.NewGuid();
    public double? WindowLeft               { get; set; } = null;
    public double? WindowTop                { get; set; } = null;
    public MinimizeMode MinimizeMode        { get; set; } = MinimizeMode.Banner;
    public bool BannerLocked                { get; set; } = true;
    public double BannerOpacity             { get; set; } = 1.0;
    public int BannerScalePct               { get; set; } = 100;
    public double? BannerLeft               { get; set; } = null;
    public double? BannerTop                { get; set; } = null;
}
