using System.Text.Json.Serialization;
using KefRemote.Core;

namespace KefRemote.Desktop;

/// <summary>
/// One registered speaker (a pair counts as one) and everything that belongs to it rather than to the app:
/// where it is, its password, the volume limit for the room it is in, and how it is drawn.
/// </summary>
public sealed class SpeakerEntry
{
    /// <summary>Made by the app when the speaker is added; never changes, even when the address does.</summary>
    public string Id { get; set; } = NewId();
    public string Host { get; set; } = "";

    /// <summary>Device password encrypted for the current OS user by the front end. Never stored in plain text.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>The name and model the speaker last reported, so the list can show it while it is off or away.</summary>
    public string? Name { get; set; }
    public string? Model { get; set; }

    /// <summary>Model picked in settings for a speaker that reports a model the app does not know.</summary>
    public string? PickedModel { get; set; }

    /// <summary>Finish the speaker is drawn in (KefFinish.Id), or null for the app's own colours.</summary>
    public string? Finish { get; set; }

    /// <summary>Upper bound for raises from this app, 1–100. Lowering is always allowed.</summary>
    public int AppVolumeLimit { get; set; } = 30;

    /// <summary>Raise the volume through a short overshoot so it sounds the same as when lowered to it.</summary>
    public bool ApproachFromAbove { get; set; }

    /// <summary>Last input seen. A speaker woken with the power switch doesn't say which input it came back on.</summary>
    public string? LastInput { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>The rules for the list of speakers in <see cref="AppSettings"/>, kept apart from the UI so they can be tested.</summary>
public static class SpeakerList
{
    /// <summary>
    /// Turns the single speaker of 0.4 and earlier into the first entry of the list, once. The old values stay where
    /// they were, so an older version opened again still finds its speaker.
    /// </summary>
    /// <returns>True when the settings changed and should be saved.</returns>
    public static bool Migrate(AppSettings settings)
    {
        if (settings.Speakers != null)
        {
            // A selection that points nowhere (edited by hand, or removed by a newer version) goes to the first.
            if (settings.Speakers.Count == 0 || Find(settings, settings.SelectedSpeaker) != null) return false;
            settings.SelectedSpeaker = settings.Speakers[0].Id;
            return true;
        }
        settings.Speakers = [];
        string host = settings.Host.Trim();
        if (host.Length > 0)
        {
            var entry = new SpeakerEntry
            {
                Host = host,
                ProtectedPassword = settings.ProtectedPassword,
                AppVolumeLimit = Math.Clamp(settings.AppVolumeLimit, 1, 100),
                ApproachFromAbove = settings.ApproachFromAbove,
                Finish = settings.SpeakerFinish,
                PickedModel = settings.SpeakerModels.GetValueOrDefault(host),
                LastInput = settings.LastInputs.GetValueOrDefault(host),
            };
            settings.Speakers.Add(entry);
            settings.SelectedSpeaker = entry.Id;
        }
        return true;
    }

    public static IReadOnlyList<SpeakerEntry> All(AppSettings settings) => settings.Speakers ?? [];

    /// <summary>The speaker the flyout, tray and shortcuts work on: the chosen one, else the first, else none.</summary>
    public static SpeakerEntry? Selected(AppSettings settings)
    {
        var all = All(settings);
        return all.FirstOrDefault(e => e.Id == settings.SelectedSpeaker) ?? all.FirstOrDefault();
    }

    public static SpeakerEntry? Find(AppSettings settings, string? id) => All(settings).FirstOrDefault(e => e.Id == id);

    /// <summary>The speaker after the selected one, wrapping around. Null with fewer than two.</summary>
    public static SpeakerEntry? Next(AppSettings settings)
    {
        var all = All(settings);
        if (all.Count < 2) return null;
        var selected = Selected(settings);
        int index = selected == null ? -1 : IndexOf(all, selected.Id);
        return all[(index + 1) % all.Count];
    }

    /// <summary>A new speaker at <paramref name="host"/>, added at the end.</summary>
    public static SpeakerEntry Add(AppSettings settings, string host, string? protectedPassword)
    {
        settings.Speakers ??= [];
        var entry = new SpeakerEntry { Host = host.Trim(), ProtectedPassword = protectedPassword };
        settings.Speakers.Add(entry);
        // Keeps whichever speaker was in use; the first one added is in use from then on.
        if (Find(settings, settings.SelectedSpeaker) == null) settings.SelectedSpeaker = settings.Speakers[0].Id;
        return entry;
    }

    /// <summary>Removes a speaker. When it was the selected one, the one before it (or the new first) takes over.</summary>
    public static bool Remove(AppSettings settings, string id)
    {
        if (settings.Speakers == null) return false;
        int index = IndexOf(settings.Speakers, id);
        if (index < 0) return false;
        bool wasSelected = Selected(settings)?.Id == id;
        settings.Speakers.RemoveAt(index);
        if (wasSelected)
            settings.SelectedSpeaker = settings.Speakers.Count == 0 ? null : settings.Speakers[Math.Max(0, index - 1)].Id;
        return true;
    }

    /// <summary>Another registered speaker already uses this address (compared as typed, case and spaces aside).</summary>
    public static SpeakerEntry? WithHost(AppSettings settings, string host, string? exceptId = null)
    {
        string wanted = host.Trim();
        return All(settings).FirstOrDefault(e => e.Id != exceptId && string.Equals(e.Host.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Name for lists and the volume display: what the speaker calls itself, else its address.</summary>
    public static string DisplayName(SpeakerEntry entry) =>
        entry.Name is { Length: > 0 } name ? name
        : entry.IsConfigured ? entry.Host
        : Loc.T("새 스피커", "New speaker", "新しいスピーカー");

    static int IndexOf(IReadOnlyList<SpeakerEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }
}
