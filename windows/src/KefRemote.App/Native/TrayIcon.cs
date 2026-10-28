using System.Runtime.InteropServices;
using KefRemote.Desktop;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace KefRemote.App.Native;

/// <summary>
/// The notification area icon. Any click (left, right, Enter, menu key) raises <see cref="Activated"/>:
/// the design has one flyout and no context menu.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const uint IconId = 1;
    const int RetryMs = 3000, MaxRetries = 20;

    readonly MessageWindow window;
    readonly DispatcherQueueTimer retry;
    int retries;
    IntPtr icon;
    StatusGlyph glyph = StatusGlyph.Offline;
    string tooltip = "Remote for KEF";
    bool added;
    bool disposed;

    /// <summary>The user activated the icon. Arguments: anchor point in screen pixels.</summary>
    public event Action<int, int>? Activated;

    public TrayIcon(MessageWindow window, DispatcherQueue queue)
    {
        this.window = window;
        retry = queue.CreateTimer();
        retry.Interval = TimeSpan.FromMilliseconds(RetryMs);
        retry.Tick += (_, _) => Add();
        window.TrayCallback += OnCallback;
        // Explorer restarted or started after us (sign-in): the old icon is gone.
        window.TaskbarCreated += () => { added = false; retries = 0; Add(); };
        window.ThemeChanged += Reload;
        window.DisplayChanged += Reload;
        Add();
    }

    public void Update(StatusGlyph nextGlyph, string nextTooltip)
    {
        if (nextGlyph == glyph && nextTooltip == tooltip) return;
        bool iconChanged = nextGlyph != glyph;
        glyph = nextGlyph;
        tooltip = nextTooltip;
        if (!added) return;
        if (iconChanged) LoadIcon();
        var data = Data(Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>Windows shows this as a notification from the tray icon.</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!added) return;
        var data = Data(Win32.NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = Win32.NIIF_INFO | Win32.NIIF_NOSOUND;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>Icon bounds in screen pixels, or null when Windows does not say (e.g. hidden in the overflow).</summary>
    public Win32.RECT? Bounds()
    {
        if (!added) return null;
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = IconId };
        return Win32.Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Width > 0 ? rect : null;
    }

    void Add()
    {
        if (disposed || added) return;
        LoadIcon();
        var data = Data(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        if (added)
        {
            retry.Stop();
            data.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
        }
        else if (++retries <= MaxRetries)
        {
            // The taskbar is not ready yet, typically right after sign-in. TaskbarCreated usually arrives first.
            retry.Start();
        }
        else
        {
            Log.Write("tray icon could not be added");
        }
    }

    void OnCallback(int code, int x, int y)
    {
        switch (code)
        {
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
            case Win32.WM_CONTEXTMENU:
                Activated?.Invoke(x, y);
                break;
        }
    }

    void Reload()
    {
        if (!added) return;
        LoadIcon();
        var data = Data(Win32.NIF_ICON);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Loads the frame that matches the small icon size at the taskbar's DPI, in the color for the taskbar.
    /// The taskbar follows the Windows mode (SystemUsesLightTheme), not the app mode.
    /// </summary>
    void LoadIcon()
    {
        string name = glyph switch
        {
            StatusGlyph.On => "on",
            StatusGlyph.Muted => "muted",
            StatusGlyph.Standby => "standby",
            _ => "offline",
        };
        string color = TaskbarIsLight() ? "black" : "white";
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Tray", $"tray-{name}-{color}.ico");
        uint dpi = Win32.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = Win32.GetDpiForSystem();
        int size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
        IntPtr next = Win32.LoadImage(IntPtr.Zero, path, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        if (next == IntPtr.Zero)
        {
            Log.Write("tray icon missing: " + path);
            return;
        }
        IntPtr old = icon;
        icon = next;
        // The shell copies the icon on NIM_ADD/NIM_MODIFY, but only after the call; free the old one afterwards.
        if (old != IntPtr.Zero) Win32.DestroyIcon(old);
    }

    static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    Win32.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = MessageWindow.TrayCallbackMessage,
        hIcon = icon,
        szTip = tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retry.Stop();
        if (added)
        {
            var data = Data(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            added = false;
        }
        if (icon != IntPtr.Zero) Win32.DestroyIcon(icon);
        icon = IntPtr.Zero;
    }
}
