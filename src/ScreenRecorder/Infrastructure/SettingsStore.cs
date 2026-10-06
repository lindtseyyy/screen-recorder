using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenRecorder.Encoding;

namespace ScreenRecorder.Infrastructure;

/// <summary>User settings, persisted as JSON. See PLAN §7.3.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("saveFolder")]
    public string SaveFolder { get; set; } = DefaultSaveFolder();

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 30;

    [JsonPropertyName("quality")]
    public VideoQuality Quality { get; set; } = VideoQuality.Medium;

    [JsonPropertyName("showCursor")]
    public bool ShowCursor { get; set; } = true;

    [JsonPropertyName("systemAudio")]
    public bool SystemAudio { get; set; } = true;

    [JsonPropertyName("microphone")]
    public bool Microphone { get; set; } = false;

    [JsonPropertyName("microphoneId")]
    public string? MicrophoneId { get; set; }

    [JsonPropertyName("lastMonitor")]
    public string? LastMonitorDeviceName { get; set; }

    public static string DefaultSaveFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");
}

/// <summary>Loads/saves <see cref="AppSettings"/> at %LOCALAPPDATA%\ScreenRecorder\settings.json.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRecorder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (settings is not null)
                {
                    if (settings.Fps is not (30 or 60))
                        settings.Fps = 30;
                    if (!Enum.IsDefined(settings.Quality))
                        settings.Quality = VideoQuality.Medium;
                    if (string.IsNullOrWhiteSpace(settings.SaveFolder))
                        settings.SaveFolder = AppSettings.DefaultSaveFolder();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load settings, using defaults: " + ex.Message);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Write then swap: a crash or power cut mid-write never leaves a
            // half-written file (which would silently reset every setting).
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save settings: " + ex.Message);
        }
    }
}
