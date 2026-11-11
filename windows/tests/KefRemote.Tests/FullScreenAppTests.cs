using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

/// <summary>When the volume indicator stays hidden because a game or a video fills the screen.</summary>
public class FullScreenAppTests
{
    // 4K at 200 % with a 96-pixel taskbar, and a 1440p monitor to its right.
    static readonly PixelRect Main = new(0, 0, 3840, 2160);
    static readonly PixelRect Second = new(3840, 0, 2560, 1440);
    static readonly PixelRect MainWork = Main with { Height = 2064 };

    static ForegroundWindow Window(PixelRect client, PixelRect monitor, bool maximized = false, bool caption = false) =>
        new(client, monitor, maximized, caption, IsShell: false, IsOwn: false);

    [Fact]
    public void BorderlessGameFillsTheMonitor()
    {
        Assert.True(FullScreenApp.FillsMonitor(Window(Main, Main)));
    }

    [Fact]
    public void GameOnTheSecondMonitorFillsItsOwnMonitor()
    {
        Assert.True(FullScreenApp.FillsMonitor(Window(Second, Second)));
        Assert.False(FullScreenApp.FillsMonitor(Window(Second, Main)));
    }

    [Fact]
    public void ExclusiveGameAtALowerResolutionFillsTheMonitorInThatMode()
    {
        var mode = new PixelRect(0, 0, 1920, 1080);
        Assert.True(FullScreenApp.FillsMonitor(Window(mode, mode)));
    }

    [Fact]
    public void ClientLargerThanTheMonitorStillFillsIt()
    {
        Assert.True(FullScreenApp.FillsMonitor(Window(new PixelRect(-8, -8, 3856, 2176), Main)));
    }

    [Fact]
    public void BrowserVideoInFullScreenFillsTheMonitor()
    {
        // F11 and video full screen drop the title bar and leave the maximized state.
        Assert.True(FullScreenApp.FillsMonitor(Window(Main, Main, maximized: false, caption: false)));
    }

    [Fact]
    public void OrdinaryWindowsDoNotCount()
    {
        Assert.False(FullScreenApp.FillsMonitor(Window(new PixelRect(400, 300, 1600, 1000), Main, caption: true)));
        // Maximized next to a docked taskbar: the taskbar band stays uncovered.
        Assert.False(FullScreenApp.FillsMonitor(Window(MainWork, Main, maximized: true, caption: true)));
        // Maximized with a normal title bar: the client area starts below it.
        Assert.False(FullScreenApp.FillsMonitor(Window(new PixelRect(0, 64, 3840, 2096), Main, maximized: true, caption: true)));
    }

    [Fact]
    public void MaximizedAppDrawingItsOwnTitleBarWithAnAutoHideTaskbarDoesNotCount()
    {
        // Work area is the whole monitor with an auto-hide taskbar, and the client area includes the custom title bar.
        Assert.False(FullScreenApp.FillsMonitor(Window(Main, Main, maximized: true, caption: true)));
    }

    [Fact]
    public void MaximizedWindowWithoutATitleBarCountsWhenItFillsTheMonitor()
    {
        Assert.True(FullScreenApp.FillsMonitor(Window(Main, Main, maximized: true, caption: false)));
    }

    [Fact]
    public void DesktopAndOwnWindowsDoNotCount()
    {
        Assert.False(FullScreenApp.FillsMonitor(Window(Main, Main) with { IsShell = true }));
        Assert.False(FullScreenApp.FillsMonitor(Window(Main, Main) with { IsOwn = true }));
    }

    [Fact]
    public void UnknownMonitorDoesNotCount()
    {
        Assert.False(FullScreenApp.FillsMonitor(Window(Main, default)));
    }
}
