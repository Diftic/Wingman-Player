namespace wingman_player.Settings;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Models;

public sealed class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppDataFolderName,
        Constants.SettingsFileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<SettingsManager> _logger;

    public WingmanPlayerSettings Current { get; private set; }

    public event EventHandler<WingmanPlayerSettings>? SettingsChanged;

    public SettingsManager(ILogger<SettingsManager> logger)
    {
        _logger = logger;
        Current = Load();
    }

    public void Save(WingmanPlayerSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);

            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }

    private WingmanPlayerSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            _logger.LogInformation("No settings file found at {Path}; using defaults", SettingsPath);
            return new WingmanPlayerSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<WingmanPlayerSettings>(json, JsonOptions)
                   ?? new WingmanPlayerSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", SettingsPath);
            return new WingmanPlayerSettings();
        }
    }
}
