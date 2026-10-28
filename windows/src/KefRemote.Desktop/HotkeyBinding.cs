using System.Text.Json.Serialization;

namespace KefRemote.Desktop;

[JsonConverter(typeof(JsonStringEnumConverter<HotkeyAction>))]
public enum HotkeyAction
{
    VolumeUp,
    VolumeDown,
    ToggleMute,
}

// Values match the RegisterHotKey MOD_* flags so they can be passed through unchanged.
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// One user-assigned global shortcut. <see cref="Key"/> is a Win32 virtual-key code; 0 means unassigned.
/// </summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public HotkeyModifiers Modifiers { get; set; }
    public int Key { get; set; }

    [JsonIgnore]
    public bool IsAssigned => Key != 0;

    public HotkeyBinding() { }

    public HotkeyBinding(HotkeyAction action, HotkeyModifiers modifiers, int key)
    {
        Action = action;
        Modifiers = modifiers;
        Key = key;
    }

    public HotkeyBinding Clone() => new(Action, Modifiers, Key);

    public bool SameChord(HotkeyBinding other) =>
        IsAssigned && other.IsAssigned && Modifiers == other.Modifiers && Key == other.Key;

    /// <summary>
    /// Ctrl+Alt+Up/Down/M were verified on the real speaker with the prototype, so they stay the defaults.
    /// </summary>
    public static List<HotkeyBinding> Defaults() =>
    [
        new(HotkeyAction.VolumeUp, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.Up),
        new(HotkeyAction.VolumeDown, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.Down),
        new(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.M),
    ];

    /// <summary>
    /// Returns one binding per action. Missing actions fall back to their default, so an empty
    /// settings list means "defaults" while an explicit Key=0 entry means "turned off".
    /// </summary>
    public static List<HotkeyBinding> Normalize(IEnumerable<HotkeyBinding>? saved)
    {
        var defaults = Defaults();
        var result = new List<HotkeyBinding>();
        foreach (var fallback in defaults)
        {
            var match = saved?.FirstOrDefault(b => b.Action == fallback.Action);
            result.Add(match?.Clone() ?? fallback);
        }
        return result;
    }

    /// <summary>
    /// A chord needs at least one modifier, except keys that ordinary typing never produces.
    /// Win-only chords are refused because Windows reserves most of them.
    /// </summary>
    public string? Validate()
    {
        if (!IsAssigned) return null;
        if (VirtualKeys.IsModifier(Key)) return "수정 키만으로는 단축키를 만들 수 없습니다";
        var withoutWin = Modifiers & ~HotkeyModifiers.Win;
        if (withoutWin == HotkeyModifiers.None && !VirtualKeys.IsStandalone(Key))
            return "Ctrl, Alt, Shift 중 하나 이상을 함께 눌러 주세요";
        if (withoutWin == HotkeyModifiers.Shift && !VirtualKeys.IsStandalone(Key) && !VirtualKeys.IsFunctionKey(Key))
            return "Shift만 조합하면 일반 입력과 겹칩니다. Ctrl 또는 Alt를 함께 눌러 주세요";
        return null;
    }

    public override string ToString()
    {
        if (!IsAssigned) return "없음";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKeys.Name(Key));
        return string.Join(" + ", parts);
    }

    public static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.VolumeUp => "음량 올리기",
        HotkeyAction.VolumeDown => "음량 내리기",
        HotkeyAction.ToggleMute => "음소거 / 해제",
        _ => action.ToString(),
    };

    /// <summary>
    /// Returns the first problem in a full set of bindings: an invalid chord or two actions sharing one.
    /// </summary>
    public static string? ValidateSet(IReadOnlyList<HotkeyBinding> bindings)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            var error = bindings[i].Validate();
            if (error != null) return $"{ActionName(bindings[i].Action)}: {error}";
            for (int j = 0; j < i; j++)
            {
                if (bindings[i].SameChord(bindings[j]))
                    return $"{ActionName(bindings[j].Action)}와 {ActionName(bindings[i].Action)}에 같은 단축키({bindings[i]})가 지정되어 있습니다";
            }
        }
        return null;
    }
}

/// <summary>
/// The Win32 virtual-key codes this feature names or treats specially. Kept free of WinForms types
/// so the logic can be tested on any OS.
/// </summary>
public static class VirtualKeys
{
    public const int Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Menu = 0x12,
        Pause = 0x13, Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E, M = 0x4D,
        LWin = 0x5B, RWin = 0x5C, Apps = 0x5D, NumPad0 = 0x60, Multiply = 0x6A, Add = 0x6B, Subtract = 0x6D,
        Decimal = 0x6E, Divide = 0x6F, F1 = 0x70, F12 = 0x7B, F13 = 0x7C, F24 = 0x87, Scroll = 0x91,
        LShift = 0xA0, RMenu = 0xA5, VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF,
        MediaNext = 0xB0, MediaPlayPause = 0xB3;

    public static bool IsModifier(int vk) =>
        vk is Shift or Control or Menu or LWin or RWin || vk is >= LShift and <= RMenu;

    public static bool IsFunctionKey(int vk) => vk is >= F1 and <= F24;

    /// <summary>Keys that are safe to bind without a modifier: F13–F24, Pause, Scroll Lock and media keys.</summary>
    public static bool IsStandalone(int vk) =>
        vk is >= F13 and <= F24 or Pause or Scroll || vk is >= VolumeMute and <= MediaPlayPause;

    public static string Name(int vk)
    {
        switch (vk)
        {
            case Up: return "↑";
            case Down: return "↓";
            case Left: return "←";
            case Right: return "→";
            case PageUp: return "Page Up";
            case PageDown: return "Page Down";
            case Home: return "Home";
            case End: return "End";
            case Insert: return "Insert";
            case Delete: return "Delete";
            case Space: return "Space";
            case Enter: return "Enter";
            case Back: return "Backspace";
            case Pause: return "Pause";
            case Scroll: return "Scroll Lock";
            case Apps: return "메뉴";
            case Multiply: return "Num *";
            case Add: return "Num +";
            case Subtract: return "Num -";
            case Decimal: return "Num .";
            case Divide: return "Num /";
            case VolumeMute: return "음소거 키";
            case VolumeDown: return "볼륨 내림 키";
            case VolumeUp: return "볼륨 올림 키";
            case 0xB0: return "다음 트랙 키";
            case 0xB1: return "이전 트랙 키";
            case 0xB2: return "정지 키";
            case MediaPlayPause: return "재생/일시정지 키";
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
        }
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= NumPad0 and <= NumPad0 + 9) return "Num " + (vk - NumPad0);
        if (IsFunctionKey(vk)) return "F" + (vk - F1 + 1);
        return $"키 0x{vk:X2}";
    }
}
