namespace wingman_player.Services;

using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;


public record UpdateInfo(bool HasUpdate, string Version, string DownloadUrl, string MsiUrl);

internal static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/Diftic/Wingman-Player/releases/latest";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    static UpdateChecker()
    {
        var v     = Assembly.GetExecutingAssembly().GetName().Version;
        var label = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Wingman-Player/{label}");
    }

    public static async Task<UpdateInfo> CheckAsync(ILogger? log = null)
    {
        log?.LogInformation("Checking for updates — {Url}", ApiUrl);
        try
        {
            var release = await _http.GetFromJsonAsync<GhRelease>(ApiUrl);
            if (release is null)
            {
                log?.LogWarning("Update check returned null response");
                return None;
            }

            log?.LogInformation("Latest release: {Tag}", release.TagName);

            var latest  = Parse(release.TagName.TrimStart('v'));
            var current = Current();

            if (!IsNewer(latest, current))
            {
                log?.LogInformation("Up to date (current: {Cur}, latest: {Lat})",
                    $"{current.Maj}.{current.Min}.{current.Pat}",
                    $"{latest.Maj}.{latest.Min}.{latest.Pat}");
                return None;
            }

            var exe = release.Assets
                .FirstOrDefault(a => a.Name.Equals("Wingman-Player.exe", StringComparison.OrdinalIgnoreCase));
            var msi = release.Assets
                .FirstOrDefault(a => a.Name.Equals("Wingman-Player-Setup.msi", StringComparison.OrdinalIgnoreCase));

            if (exe is null)
            {
                log?.LogWarning("Update {Tag} has no .exe asset — skipping", release.TagName);
                return None;
            }

            log?.LogInformation("Update available: {Tag} — exe: {ExeUrl}, msi: {MsiUrl}",
                release.TagName, exe.BrowserDownloadUrl, msi?.BrowserDownloadUrl ?? "none");
            return new UpdateInfo(true, release.TagName.TrimStart('v'),
                exe.BrowserDownloadUrl, msi?.BrowserDownloadUrl ?? string.Empty);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Update check failed");
            return None;
        }
    }

    private static readonly UpdateInfo None = new(false, string.Empty, string.Empty, string.Empty);

    private static (int Maj, int Min, int Pat) Current()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? (0, 0, 0) : (v.Major, v.Minor, v.Build);
    }

    private static (int Maj, int Min, int Pat) Parse(string raw)
    {
        var p = raw.Split('.');
        int N(int i) => p.Length > i && int.TryParse(p[i], out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }

    private static bool IsNewer((int Maj, int Min, int Pat) a, (int Maj, int Min, int Pat) b)
    {
        if (a.Maj != b.Maj) return a.Maj > b.Maj;
        if (a.Min != b.Min) return a.Min > b.Min;
        return a.Pat > b.Pat;
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("assets")]   public List<GhAsset> Assets { get; init; } = [];
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]                 public string Name               { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
