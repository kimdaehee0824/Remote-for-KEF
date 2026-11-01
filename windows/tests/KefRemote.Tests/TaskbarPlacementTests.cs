using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Flyout and volume indicator placement next to a docked or auto-hide taskbar.</summary>
public class TaskbarPlacementTests
{
    // 4K at 200 %: the Windows 11 taskbar is 48 × 2 = 96 pixels.
    static readonly PixelRect Monitor = new(0, 0, 3840, 2160);
    const double Scale = 2;
    const int Margin = 24, Width = 720, Height = 600;

    [Fact]
    public void DockedTaskbarKeepsTheWorkArea()
    {
        var work = Monitor with { Height = 2064 };
        var (area, edge) = TaskbarPlacement.FreeArea(work, Monitor, autoHideEdge: null, autoHideThickness: 0);
        Assert.Equal(work, area);
        Assert.Equal(TaskbarEdge.Bottom, edge);
        Assert.Equal((3000 - Width / 2, 2064 - Height - Margin), TaskbarPlacement.NearTaskbar(area, edge, 3000, 2070, Width, Height, Margin));
    }

    [Fact]
    public void AutoHideTaskbarLeavesTheSameSpaceAsADockedOne()
    {
        // Hidden on Windows: the window keeps its size and moves down so only a sliver shows.
        var hidden = new PixelRect(0, 2158, 3840, 96);
        int thickness = TaskbarPlacement.AutoHideThickness(hidden, TaskbarEdge.Bottom, Monitor, Scale);
        Assert.Equal(96, thickness);

        var (area, edge) = TaskbarPlacement.FreeArea(Monitor, Monitor, TaskbarEdge.Bottom, thickness);
        Assert.Equal(Monitor with { Height = 2064 }, area);
        Assert.Equal(TaskbarEdge.Bottom, edge);

        var (_, y) = TaskbarPlacement.NearTaskbar(area, edge, 3000, 2100, Width, Height, Margin);
        Assert.Equal(2064 - Height - Margin, y);
        Assert.True(y + Height <= 2160 - 96, "the flyout must end above the revealed taskbar");
    }

    [Fact]
    public void VolumeIndicatorStaysAboveAnAutoHideTaskbar()
    {
        var (area, _) = TaskbarPlacement.FreeArea(Monitor, Monitor, TaskbarEdge.Bottom, 96);
        Assert.Equal((3840 - 600 - Margin, 2064 - 112 - Margin), TaskbarPlacement.BottomRight(area, 600, 112, Margin));
    }

    [Theory]
    [InlineData(2)]    // shrunk to a sliver
    [InlineData(0)]    // no rectangle
    [InlineData(1500)] // more than half the screen
    public void ImplausibleTaskbarSizeFallsBackToTheWindows11Height(int height) =>
        Assert.Equal(96, TaskbarPlacement.AutoHideThickness(new PixelRect(0, 2160 - height, 3840, height), TaskbarEdge.Bottom, Monitor, Scale));

    [Fact]
    public void AutoHideTaskbarOnOtherEdges()
    {
        var top = TaskbarPlacement.FreeArea(Monitor, Monitor, TaskbarEdge.Top, 96);
        Assert.Equal(new PixelRect(0, 96, 3840, 2064), top.Area);
        Assert.Equal((100, 96 + Margin), TaskbarPlacement.NearTaskbar(top.Area, top.Edge, 100 + Width / 2, 40, Width, Height, Margin));

        var left = TaskbarPlacement.FreeArea(Monitor, Monitor, TaskbarEdge.Left, 120);
        Assert.Equal(new PixelRect(120, 0, 3720, 2160), left.Area);
        Assert.Equal(120 + Margin, TaskbarPlacement.NearTaskbar(left.Area, left.Edge, 60, 2100, Width, Height, Margin).X);

        var right = TaskbarPlacement.FreeArea(Monitor, Monitor, TaskbarEdge.Right, 120);
        Assert.Equal(new PixelRect(0, 0, 3720, 2160), right.Area);
        Assert.Equal(3720 - Width - Margin, TaskbarPlacement.NearTaskbar(right.Area, right.Edge, 3800, 2100, Width, Height, Margin).X);
    }

    [Fact]
    public void ThicknessIsMeasuredAcrossTheEdge() =>
        Assert.Equal(120, TaskbarPlacement.AutoHideThickness(new PixelRect(-118, 0, 120, 2160), TaskbarEdge.Left, Monitor, Scale));

    [Fact]
    public void MonitorWithoutATaskbarUsesItsBottomEdge()
    {
        var second = new PixelRect(-1920, 0, 1920, 1080);
        var (area, edge) = TaskbarPlacement.FreeArea(second, second, autoHideEdge: null, autoHideThickness: 0);
        Assert.Equal(second, area);
        Assert.Equal(TaskbarEdge.Bottom, edge);
        Assert.Equal((-1920 + 12, 1080 - 300 - 12), TaskbarPlacement.NearTaskbar(area, edge, -1900, 1079, 360, 300, 12));
    }

    [Fact]
    public void DockedTaskbarWinsOverAnAutoHideReport()
    {
        var work = Monitor with { Height = 2064 };
        Assert.Equal(work, TaskbarPlacement.FreeArea(work, Monitor, TaskbarEdge.Bottom, 96).Area);
    }
}
