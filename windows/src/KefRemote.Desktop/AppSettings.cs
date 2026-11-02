using System.Text.Json;
using System.Text.Json.Serialization;

namespace KefRemote.Desktop;

public sealed class AppSettings
{
    public string DriverId { get; set; } = "kef-w2";

    /// <summary>
    /// The registered speakers. Null in settings written before 0.5, which had one speaker in <see cref="Host"/> and
    /// the fields after it; <see cref="SpeakerList.Migrate"/> turns that into the first entry.
    /// </summary>
    public List<SpeakerEntry>? Speakers { get; set; }

    /// <summary>Id of the speaker the flyout, tray and shortcuts work on.</summary>
    public string? SelectedSpeaker { get; set; }

    /// <summary>The one speaker of 0.4 and earlier. Kept as it was so an older version still finds it.</summary>
    public string Host { get; set; } = "";

    /// <summary>0.4 and earlier, see <see cref="SpeakerEntry.ProtectedPassword"/>.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>0.4 and earlier, see <see cref="SpeakerEntry.AppVolumeLimit"/>.</summary>
    public int AppVolumeLimit { get; set; } = 30;
    public bool StartWithWindows { get; set; }

    /// <summary>Volume units per hotkey press (1, 2, 3, 5) or <see cref="VolumeSteps.Perceptual"/>.</summary>
    public int HotkeyVolumeStep { get; set; } = 1;
    public bool ShowVolumeOsd { get; set; } = true;

    /// <summary>Show what the speaker is playing in a card above the flyout, with play/pause and skip.</summary>
    public bool ShowNowPlaying { get; set; } = true;

    /// <summary>0.4 and earlier, see <see cref="SpeakerEntry.ApproachFromAbove"/>.</summary>
    public bool ApproachFromAbove { get; set; }

    /// <summary>0.4 and earlier, see <see cref="SpeakerEntry.Finish"/>.</summary>
    public string? SpeakerFinish { get; set; }

    /// <summary>0.4 and earlier: the picked model per speaker address, see <see cref="SpeakerEntry.PickedModel"/>.</summary>
    public Dictionary<string, string> SpeakerModels { get; set; } = [];

    /// <summary>0.4 and earlier: the last input per speaker address, see <see cref="SpeakerEntry.LastInput"/>.</summary>
    public Dictionary<string, string> LastInputs { get; set; } = [];

    /// <summary>The one-time notice that closing the settings window keeps the app in the tray was shown.</summary>
    public bool TrayNoticeShown { get; set; }

    /// <summary>Look for a new release once a day, download it in the background and apply it on the next start.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>When the last release check finished, in UTC.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>The release whose "ready to install" notice was already shown, so it shows once per version.</summary>
    public string? UpdateNoticeShownFor { get; set; }

    /// <summary>Global shortcuts. Empty means the defaults; an entry with Key 0 means that action is turned off.</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    [JsonIgnore]
    public bool IsConfigured => SpeakerList.All(this).Any(e => e.IsConfigured);
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
