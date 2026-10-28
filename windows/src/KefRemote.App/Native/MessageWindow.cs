using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KefRemote.App.Native;

/// <summary>
/// Invisible top-level window for what WinUI windows do not expose: global hotkeys, tray icon callbacks and shell
/// broadcasts (taskbar restarted, theme or display changed). A message-only window would miss the broadcasts.
/// Create and use it on the UI thread; its messages arrive through the WinUI message loop.
/// </summary>
sealed class MessageWindow : IDisposable
{
    const string ClassName = "KefRemote.MessageWindow";
    public const uint TrayCallbackMessage = Win32.WM_APP + 1;

    // Kept in a static field so the delegate outlives every window that uses it.
    static readonly Win32.WndProc Proc = StaticWndProc;
    static readonly Dictionary<IntPtr, MessageWindow> Live = [];
    static bool classRegistered;

    readonly uint taskbarCreated;

    public IntPtr Handle { get; private set; }

    public event Action<int>? Hotkey;

    /// <summary>Tray notification (NOTIFYICON_VERSION_4): event code and the anchor point in screen pixels.</summary>
    public event Action<int, int, int>? TrayCallback;

    public event Action? TaskbarCreated;

    /// <summary>Light/dark mode or accent color changed.</summary>
    public event Action? ThemeChanged;

    /// <summary>Resolution, scaling or monitor layout changed.</summary>
    public event Action? DisplayChanged;

    public MessageWindow()
    {
        IntPtr instance = Win32.GetModuleHandle(null);
        if (!classRegistered)
        {
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (Win32.RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            classRegistered = true;
        }
        taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        Handle = Win32.CreateWindowEx((uint)Win32.WS_EX_TOOLWINDOW, ClassName, "Remote for KEF", Win32.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Live[Handle] = this;
    }

    static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Live.TryGetValue(hWnd, out var window))
        {
            try
            {
                if (window.Dispatch(msg, wParam, lParam)) return IntPtr.Zero;
            }
            catch (Exception e)
            {
                // Never let an exception unwind through native code.
                Log.Write($"message 0x{msg:X} failed", e);
            }
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    bool Dispatch(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                Hotkey?.Invoke(wParam.ToInt32());
                return true;
            case TrayCallbackMessage:
                long l = lParam.ToInt64(), w = wParam.ToInt64();
                TrayCallback?.Invoke((int)(l & 0xFFFF), (short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF));
                return true;
            case Win32.WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") ThemeChanged?.Invoke();
                return false;
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
                DisplayChanged?.Invoke();
                return false;
        }
        if (msg == taskbarCreated && msg != 0)
        {
            TaskbarCreated?.Invoke();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Live.Remove(Handle);
        Win32.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
