using System.Text.Json;
using System.Text.Json.Serialization;

namespace KefRemote.Desktop;

public sealed class AppSettings
{
    public string DriverId { get; set; } = "kef-w2";
    public string Host { get; set; } = "";

    /// <summary>Device password encrypted for the current OS user by the front end. Never stored in plain text.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Upper bound for raises from this app, 1–100. Lowering is always allowed.</summary>
    public int AppVolumeLimit { get; set; } = 30;
    public bool StartWithWindows { get; set; }

    /// <summary>Volume units per hotkey press (1, 2, 3, 5) or <see cref="VolumeSteps.Perceptual"/>.</summary>
    public int HotkeyVolumeStep { get; set; } = 1;
    public bool ShowVolumeOsd { get; set; } = true;

    /// <summary>Raise the volume through a short overshoot so it sounds the same as when lowered to it.</summary>
    public bool ApproachFromAbove { get; set; }

    /// <summary>Finish the speaker icon is drawn in (KefFinish.Id), or null for the app's own colours.</summary>
    public string? SpeakerFinish { get; set; }

    /// <summary>The one-time notice that closing the settings window keeps the app in the tray was shown.</summary>
    public bool TrayNoticeShown { get; set; }

    /// <summary>Global shortcuts. Empty means the defaults; an entry with Key 0 means that action is turned off.</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Settings as JSON in one file. Missing or broken files fall back to defaults.</summary>
public sealed class SettingsStore(string directory)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = directory;
    string FilePath => Path.Combine(Directory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
