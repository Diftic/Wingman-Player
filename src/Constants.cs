namespace wingman_player;

internal static class Constants
{
    public const string ApplicationName = "Wingman Player";
    public const string MutexId = "wingman_player-64459292-292A-417A-9E12-E6E00A3040B5";
    public const string AppDataFolderName = "wingman_player";
    public const string SettingsFileName = "settings.json";
    public const string WebView2CacheFolderName       = "WebView2Cache";
    public const string BannerWebView2CacheFolderName = "WebView2BannerCache";
    public const string PlayerVirtualHost       = "wingman.local";
    public const string PlayerRendererFolder    = "Renderer";

    // Frame canvas dimensions — sized so the cutout in the Wingman frame art lands on a 16:9 video rect.
    public const int FrameDisplayWidth  = 1252;
    public const int FrameDisplayHeight = 731;

    // Mini banner — small click-through "now playing" tile in the lower-right.
    public const int BannerWidth  = 400;
    public const int BannerHeight = 100;
    public const int BannerMargin = 16;
}
