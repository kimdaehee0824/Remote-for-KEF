using KefRemote.Core;

namespace KefRemote.Desktop;

/// <summary>Which of the four monochrome status glyphs the tray or menu bar shows.</summary>
public enum StatusGlyph
{
    On,
    Muted,
    Standby,
    Offline,
}

/// <summary>
/// Text and icon choices shared by the flyout, tray tooltip and OSD, so every surface says the same thing.
/// One place per fact: power is the switch, input is the selected tile, volume is the number. The subtitle under
/// the speaker name is the model unless something is out of the ordinary.
/// </summary>
public static class SpeakerPresentation
{
    public static string Standby => Loc.T("대기", "Standby", "スタンバイ");
    public static string Offline => Loc.T("연결 끊김", "Offline", "オフライン");
    static string Connecting => Loc.T("연결하는 중", "Connecting", "接続中");
    static string SetUp => Loc.T("스피커를 설정하세요", "Set up your speaker", "スピーカーを設定してください");
    static string TurningOn => Loc.T("켜는 중", "Turning on", "電源を入れています");

    public static StatusGlyph Glyph(SpeakerController c) => c.Phase switch
    {
        SpeakerPhase.On => c.State.Muted ? StatusGlyph.Muted : StatusGlyph.On,
        SpeakerPhase.Standby => StatusGlyph.Standby,
        _ => StatusGlyph.Offline,
    };

    public static string Title(SpeakerController c) => c.Device?.Name is { Length: > 0 } name ? name : "Remote for KEF";

    /// <summary>Second header line: the model normally, the state only when it is not the ordinary "on".</summary>
    public static string Subtitle(SpeakerController c, bool configured)
    {
        if (!configured) return SetUp;
        switch (c.Operation)
        {
            case SpeakerOperation.Source when c.OperationSource != null:
                string label = ShortLabel(SourceLabel(c, c.OperationSource));
                return Loc.T($"{label}{Korean.Ro(label)} 바꾸는 중", $"Switching to {label}", $"{label}に切り替え中");
            case SpeakerOperation.PowerOn:
                return TurningOn;
            case SpeakerOperation.PowerOff:
                return Loc.T("끄는 중", "Turning off", "電源を切っています");
        }
        if (c.IsWaking) return TurningOn;
        return c.Phase switch
        {
            SpeakerPhase.On => c.Device?.Model ?? "",
            SpeakerPhase.Standby => Standby,
            SpeakerPhase.Offline => Offline,
            _ => Connecting,
        };
    }

    public static bool SubtitleIsError(SpeakerController c, bool configured) =>
        configured && c.Operation == SpeakerOperation.None && c.Phase == SpeakerPhase.Offline;

    /// <summary>"상한 30" plus where the number comes from.</summary>
    public static string LimitSource(SpeakerController c) =>
        c.LimitFromDevice ? Loc.T("KEF 기기 설정", "KEF device setting", "KEF機器の設定") : Loc.T("앱 설정", "app setting", "アプリの設定");

    /// <summary>Tray tooltip, at most 127 characters: name on the first line, the essentials on the second.</summary>
    public static string Tooltip(SpeakerController c, bool configured)
    {
        string first = configured ? Title(c) : "Remote for KEF";
        string second = !configured ? SetUp : c.Phase switch
        {
            SpeakerPhase.On => c.State.Muted
                ? Loc.T("음소거", "Muted", "ミュート") + Loc.T(", ", ", ", "、") + SourceLabel(c, c.State.SourceId)
                : Loc.T($"음량 {c.State.Volume}, ", $"Volume {c.State.Volume}, ", $"音量 {c.State.Volume}、") + SourceLabel(c, c.State.SourceId),
            SpeakerPhase.Standby => Standby,
            SpeakerPhase.Offline => Offline,
            _ => Connecting,
        };
        string text = first + "\n" + second;
        return text.Length > 127 ? text[..127] : text;
    }

    public static string SourceLabel(SpeakerController c, string? id) =>
        c.Driver?.Sources.FirstOrDefault(s => s.Id == id)?.Label ?? id ?? "-";

    /// <summary>Tile caption: "USB-C / PC" becomes "USB-C", "TV (HDMI eARC)" becomes "TV".</summary>
    public static string ShortLabel(string label)
    {
        int cut = label.IndexOf(" / ", StringComparison.Ordinal);
        if (cut < 0) cut = label.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? label[..cut] : label;
    }

    /// <summary>Hotkey summary for the flyout footer, e.g. "Ctrl + Alt + ↑ ↓". Null when volume keys are off.</summary>
    public static string? VolumeKeysHint(IReadOnlyList<HotkeyBinding> bindings)
    {
        var up = bindings.FirstOrDefault(b => b.Action == HotkeyAction.VolumeUp);
        var down = bindings.FirstOrDefault(b => b.Action == HotkeyAction.VolumeDown);
        if (up is not { IsAssigned: true } || down is not { IsAssigned: true }) return null;
        if (up.Modifiers == down.Modifiers)
        {
            string keys = $"{VirtualKeys.Name(up.Key)} {VirtualKeys.Name(down.Key)}";
            if (up.Modifiers == HotkeyModifiers.None) return keys;
            string chord = up.ToString();
            return chord[..chord.LastIndexOf(" + ", StringComparison.Ordinal)] + " + " + keys;
        }
        return $"{up} / {down}";
    }
}
