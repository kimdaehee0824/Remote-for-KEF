using KefRemote.Desktop;

namespace KefRemote.App.Native;

public enum HotkeyRegistration
{
    Off,
    Registered,
    /// <summary>RegisterHotKey refused the chord, almost always because another app already owns it.</summary>
    InUse,
}

/// <summary>
/// Registers global shortcuts on the hidden message window so they work while the app lives only in the tray.
/// Create, use and dispose it on the UI thread.
/// </summary>
sealed class HotkeyManager : IDisposable
{
    const uint MOD_NOREPEAT = 0x4000;
    const int FirstId = 0x4B45; // arbitrary, inside the 0x0000–0xBFFF application range

    readonly MessageWindow window;
    readonly Dictionary<int, HotkeyAction> registered = [];
    List<HotkeyBinding> bindings = [];
    bool suspended;
    bool disposed;

    /// <summary>Raised on the UI thread. Volume actions repeat while the key is held.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>Raised after registrations change, so settings can show conflicts per key.</summary>
    public event Action? StatusChanged;

    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Status { get; private set; } =
        new Dictionary<HotkeyAction, HotkeyRegistration>();

    public bool AnyInUse => Status.Values.Any(s => s == HotkeyRegistration.InUse);

    public HotkeyManager(MessageWindow window)
    {
        this.window = window;
        window.Hotkey += OnHotkey;
    }

    /// <summary>Replaces every registration and reports the outcome per action.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Apply(IEnumerable<HotkeyBinding> next)
    {
        bindings = next.Select(b => b.Clone()).ToList();
        return suspended ? Status : RegisterAll();
    }

    /// <summary>
    /// Releases every chord while the settings window captures a new one. Otherwise pressing the current chord
    /// would run the action instead of reaching the capture box.
    /// </summary>
    public void Suspend()
    {
        if (suspended) return;
        suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        if (!suspended) return;
        suspended = false;
        RegisterAll();
    }

    IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> RegisterAll()
    {
        if (disposed) return Status;
        UnregisterAll();
        var status = new Dictionary<HotkeyAction, HotkeyRegistration>();
        int id = FirstId;
        foreach (var binding in bindings)
        {
            if (!binding.IsAssigned || binding.Validate() != null)
            {
                status[binding.Action] = HotkeyRegistration.Off;
                continue;
            }
            // Holding a volume key keeps stepping like the Windows volume keys; mute must not flicker.
            uint modifiers = (uint)binding.Modifiers;
            if (binding.Action == HotkeyAction.ToggleMute) modifiers |= MOD_NOREPEAT;
            if (Win32.RegisterHotKey(window.Handle, id, modifiers, (uint)binding.Key))
            {
                registered[id] = binding.Action;
                status[binding.Action] = HotkeyRegistration.Registered;
            }
            else
            {
                status[binding.Action] = HotkeyRegistration.InUse;
                Log.Write($"hotkey {binding} for {binding.Action} is taken by another app");
            }
            id++;
        }
        Status = status;
        StatusChanged?.Invoke();
        return status;
    }

    void UnregisterAll()
    {
        foreach (var id in registered.Keys) Win32.UnregisterHotKey(window.Handle, id);
        registered.Clear();
    }

    void OnHotkey(int id)
    {
        if (registered.TryGetValue(id, out var action)) Pressed?.Invoke(action);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Hotkey -= OnHotkey;
        UnregisterAll();
    }
}
