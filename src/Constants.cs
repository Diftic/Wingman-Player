namespace pulsenet;

internal static class Constants
{
    public const string ApplicationName = "PulseNet Player";
    public const string MutexId = "pulsenet-3C8F4A2D-91B7-4E56-8D0C-7A3F2B1E9C84";
    public const string AppDataFolderName = "pulsenet-radio";
    public const string SettingsFileName = "settings.json";
    public const string WebView2CacheFolderName       = "WebView2Cache";
    public const string BannerWebView2CacheFolderName = "WebView2BannerCache";
    public const string PlayerVirtualHost       = "pulsenet.local";
    public const string PlayerRendererFolder    = "Renderer";

    // Frame canvas dimensions — sized so the cutout in the Wingman frame art lands on a 16:9 video rect.
    public const int FrameDisplayWidth  = 1252;
    public const int FrameDisplayHeight = 731;

    // Mini banner — small click-through "now playing" tile in the lower-right.
    public const int BannerWidth  = 400;
    public const int BannerHeight = 100;
    public const int BannerMargin = 16;
}
