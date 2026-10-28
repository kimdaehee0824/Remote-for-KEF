using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KefRemote.App.Native;

/// <summary>Taskbar edge of a display, inferred from where its work area is cut.</summary>
enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

static class WindowHelpers
{
    public static IntPtr Hwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static AppWindow AppWindowOf(Window window) => AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd(window)));

    /// <summary>
    /// Borderless, unresizable, always on top and absent from the taskbar and Alt+Tab: the shape of a tray flyout.
    /// The thin border keeps the Windows 11 rounded corners and shadow.
    /// </summary>
    public static void MakePopup(Window window, bool activates)
    {
        var appWindow = AppWindowOf(window);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;

        IntPtr hwnd = Hwnd(window);
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW;
        ex &= ~Win32.WS_EX_APPWINDOW;
        if (!activates) ex |= Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        int corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Sizes the window so its client area is <paramref name="client"/> pixels. AppWindow.ResizeClient still adds a
    /// caption's height after SetBorderAndTitleBar(true, false) removed it, which leaves an empty band in the popup,
    /// so the frame is taken from the window's current outer and client sizes instead.
    /// </summary>
    public static void SetClientSize(AppWindow appWindow, SizeInt32 client)
    {
        SizeInt32 outer = appWindow.Size, inner = appWindow.ClientSize;
        int frameWidth = Math.Max(0, outer.Width - inner.Width), frameHeight = Math.Max(0, outer.Height - inner.Height);
        appWindow.Resize(new SizeInt32(client.Width + frameWidth, client.Height + frameHeight));
    }

    public static DisplayArea DisplayAt(PointInt32 point) => DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest);

    public static TaskbarEdge EdgeOf(DisplayArea display)
    {
        RectInt32 work = display.WorkArea, outer = display.OuterBounds;
        if (work.Y + work.Height < outer.Y + outer.Height) return TaskbarEdge.Bottom;
        if (work.Y > outer.Y) return TaskbarEdge.Top;
        if (work.X > outer.X) return TaskbarEdge.Left;
        if (work.X + work.Width < outer.X + outer.Width) return TaskbarEdge.Right;
        return TaskbarEdge.Bottom; // auto-hide taskbar: assume the default position
    }

    /// <summary>
    /// Places a window of <paramref name="size"/> next to the taskbar near <paramref name="anchor"/>, inside the work area
    /// with <paramref name="margin"/> pixels to spare, like the Windows flyouts.
    /// </summary>
    public static PointInt32 PlaceNearTaskbar(DisplayArea display, PointInt32 anchor, SizeInt32 size, int margin)
    {
        RectInt32 work = display.WorkArea;
        int minX = work.X + margin, maxX = work.X + work.Width - size.Width - margin;
        int minY = work.Y + margin, maxY = work.Y + work.Height - size.Height - margin;
        int centeredX = Math.Clamp(anchor.X - size.Width / 2, minX, Math.Max(minX, maxX));
        int centeredY = Math.Clamp(anchor.Y - size.Height / 2, minY, Math.Max(minY, maxY));
        return EdgeOf(display) switch
        {
            TaskbarEdge.Top => new PointInt32(centeredX, minY),
            TaskbarEdge.Left => new PointInt32(minX, centeredY),
            TaskbarEdge.Right => new PointInt32(Math.Max(minX, maxX), centeredY),
            _ => new PointInt32(centeredX, Math.Max(minY, maxY)),
        };
    }

    public static PointInt32 CursorPosition() =>
        Win32.GetCursorPos(out var p) ? new PointInt32(p.X, p.Y) : new PointInt32(0, 0);

    public static double ScaleAt(PointInt32 point) => Win32.DpiAt(point.X, point.Y) / 96.0;

    /// <summary>True when the foreground window is <paramref name="hwnd"/> or one of its popups (menus, tooltips).</summary>
    public static bool OwnsForeground(IntPtr hwnd)
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        return foreground == hwnd || (foreground != IntPtr.Zero && Win32.GetAncestor(foreground, Win32.GA_ROOTOWNER) == hwnd);
    }
}
