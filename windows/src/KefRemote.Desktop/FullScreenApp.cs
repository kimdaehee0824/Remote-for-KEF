namespace KefRemote.Desktop;

/// <summary>The foreground window as the app reads it from Windows, in physical screen pixels.</summary>
/// <param name="Client">The window's client area on the screen.</param>
/// <param name="Monitor">The whole monitor the window is on, taskbar included.</param>
/// <param name="Maximized">The window is maximized.</param>
/// <param name="HasCaption">The window keeps a title bar style, even if it draws its own.</param>
/// <param name="IsShell">The desktop, the taskbar or another window of the Windows shell.</param>
/// <param name="IsOwn">One of this app's windows.</param>
public readonly record struct ForegroundWindow(PixelRect Client, PixelRect Monitor, bool Maximized, bool HasCaption, bool IsShell, bool IsOwn);

/// <summary>
/// Whether a game or a video is in front in full screen. The volume indicator stays hidden then and the keys only
/// change the volume: a window on top of an exclusive full-screen game can make it drop out of full screen, and
/// over a borderless one it still covers the picture and can make the game stutter.
/// </summary>
public static class FullScreenApp
{
    /// <summary>
    /// The window's own content covers its whole monitor, taskbar edge included. A maximized window with a title bar
    /// does not count: with an auto-hide taskbar, apps that draw their own title bar (browsers, editors) can reach
    /// every edge while maximized, and those are not full screen.
    /// </summary>
    public static bool FillsMonitor(ForegroundWindow window)
    {
        if (window.IsShell || window.IsOwn) return false;
        if (window.Maximized && window.HasCaption) return false;
        PixelRect client = window.Client, monitor = window.Monitor;
        if (monitor.Width <= 0 || monitor.Height <= 0) return false;
        return client.X <= monitor.X && client.Y <= monitor.Y && client.Right >= monitor.Right && client.Bottom >= monitor.Bottom;
    }
}
