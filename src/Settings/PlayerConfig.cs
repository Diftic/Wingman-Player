namespace wingman_player.Settings;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// System-side configuration distinct from <see cref="Models.WingmanPlayerSettings"/>.
/// Settings are user-tweakable from the in-overlay panel and round-trip through the
/// renderer; config is system-tweakable only by hand-editing the file. Keeping the
/// command-server port (and any future system knobs) out of the renderer-visible
/// surface means a careless settings serialization can never overwrite it.
///
/// File: %APPDATA%\wingman_player\config.json. Absent file = defaults.
/// </summary>
public sealed record PlayerConfig
{
    public int CommandServerPort { get; init; } = 17330;
}

public static class PlayerConfigLoader
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppDataFolderName,
        "config.json");

    public static PlayerConfig Load(ILogger? log = null)
    {
        if (!File.Exists(ConfigPath))
        {
            log?.LogInformation("No config.json at {Path}; using defaults", ConfigPath);
            return new PlayerConfig();
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<PlayerConfig>(json) ?? new PlayerConfig();
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Failed to load config.json from {Path}; using defaults", ConfigPath);
            return new PlayerConfig();
        }
    }
}
