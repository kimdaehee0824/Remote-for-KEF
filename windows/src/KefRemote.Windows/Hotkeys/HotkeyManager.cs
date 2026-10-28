using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KefRemote.Windows;

public enum HotkeyRegistration
{
    Off,
    Registered,
    /// <summary>RegisterHotKey refused the chord, almost always because another app already owns it.</summary>
    InUse,
}

/// <summary>
/// Registers global shortcuts on a hidden message-only window so they work while the app lives only in the tray.
/// Create, use and dispose it on the UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    const int FirstId = 0x4B45; // arbitrary, stays inside the 0x0000–0xBFFF application range

    readonly MessageWindow window;
    readonly Dictionary<int, HotkeyAction> registered = [];
    List<HotkeyBinding> bindings = [];
    bool suspended;
    bool disposed;

    /// <summary>Raised on the UI thread. Volume actions repeat while the key is held.</summary>
    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Status { get; private set; } =
        new Dictionary<HotkeyAction, HotkeyRegistration>();

    public HotkeyManager()
    {
        window = new MessageWindow(this);
    }

    /// <summary>Replaces every registration. Returns the outcome per action so the UI can show conflicts.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Apply(IEnumerable<HotkeyBinding> next)
    {
        bindings = next.Select(b => b.Clone()).ToList();
        return suspended ? Status : RegisterAll();
    }

    /// <summary>
    /// Releases every chord, e.g. while the settings dialog is capturing a new one. Otherwise pressing the
    /// current chord would trigger the action instead of reaching the capture box.
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
            // Holding volume keys should keep stepping like the Windows volume keys; mute should not flicker.
            uint modifiers = (uint)binding.Modifiers;
            if (binding.Action == HotkeyAction.ToggleMute) modifiers |= MOD_NOREPEAT;
            if (RegisterHotKey(window.Handle, id, modifiers, (uint)binding.Key))
            {
                registered[id] = binding.Action;
                status[binding.Action] = HotkeyRegistration.Registered;
            }
            else
            {
                status[binding.Action] = HotkeyRegistration.InUse;
            }
            id++;
        }
        Status = status;
        return status;
    }

    void UnregisterAll()
    {
        foreach (var id in registered.Keys) UnregisterHotKey(window.Handle, id);
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
        UnregisterAll();
        window.DestroyHandle();
    }

    sealed class MessageWindow : NativeWindow
    {
        static readonly IntPtr HWND_MESSAGE = new(-3);
        readonly HotkeyManager owner;

        public MessageWindow(HotkeyManager owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "KefRemote.Hotkeys" });
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                owner.OnHotkey(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
