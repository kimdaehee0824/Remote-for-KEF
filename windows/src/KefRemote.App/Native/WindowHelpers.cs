using System.Runtime.InteropServices;
using KefRemote.Desktop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KefRemote.App.Native;

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

    /// <summary>
    /// The part of <paramref name="display"/> the taskbar never covers, and the taskbar's edge. The work area alone
    /// is not enough: with "Automatically hide the taskbar" it is the whole monitor, and the taskbar slides back over
    /// its edge whenever the pointer goes there, for example to click the tray icon.
    /// </summary>
    public static (PixelRect Area, TaskbarEdge Edge) FreeArea(DisplayArea display)
    {
        PixelRect work = Pixels(display.WorkArea), monitor = Pixels(display.OuterBounds);
        TaskbarEdge? autoHideEdge = null;
        int thickness = 0;
        if (TaskbarPlacement.DockedEdge(work, monitor) is null && AutoHideTaskbar(monitor) is (TaskbarEdge edge, PixelRect bar))
        {
            autoHideEdge = edge;
            double scale = ScaleAt(new PointInt32(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2));
            thickness = TaskbarPlacement.AutoHideThickness(bar, edge, monitor, scale);
        }
        return TaskbarPlacement.FreeArea(work, monitor, autoHideEdge, thickness);
    }

    static readonly (uint Code, TaskbarEdge Edge)[] AppBarEdges =
        [(Win32.ABE_BOTTOM, TaskbarEdge.Bottom), (Win32.ABE_TOP, TaskbarEdge.Top), (Win32.ABE_LEFT, TaskbarEdge.Left), (Win32.ABE_RIGHT, TaskbarEdge.Right)];

    /// <summary>The auto-hide taskbar on an edge of <paramref name="monitor"/> and its window rectangle, if there is one.</summary>
    static (TaskbarEdge Edge, PixelRect Bar)? AutoHideTaskbar(PixelRect monitor)
    {
        foreach (var (code, edge) in AppBarEdges)
        {
            var data = new Win32.APPBARDATA
            {
                cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
                uEdge = code,
                rc = new Win32.RECT { Left = monitor.X, Top = monitor.Y, Right = monitor.Right, Bottom = monitor.Bottom },
            };
            IntPtr bar = Win32.SHAppBarMessage(Win32.ABM_GETAUTOHIDEBAREX, ref data);
            if (bar == IntPtr.Zero || !Win32.IsWindow(bar)) continue;
            return (edge, Win32.GetWindowRect(bar, out var r) ? new PixelRect(r.Left, r.Top, r.Width, r.Height) : default);
        }
        return null;
    }

    static PixelRect Pixels(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);

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
