namespace KefRemote.Desktop;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>Screen edge the taskbar sits on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Where the flyout and the volume indicator go so the taskbar never covers them. A docked taskbar is already
/// cut out of the monitor's work area. An auto-hide taskbar is not: the work area is the whole monitor, yet the
/// taskbar slides back over the bottom of it whenever the pointer reaches that edge (and it is up while its tray
/// icon is being clicked). Its band is taken out here, so both kinds of taskbar leave the same space.
/// </summary>
public static class TaskbarPlacement
{
    /// <summary>Thickness assumed when an auto-hide taskbar's own size looks wrong: the Windows 11 taskbar height.</summary>
    public const int DefaultThicknessDip = 48;

    /// <summary>The edge a docked taskbar takes from the work area, or null when the work area is the whole monitor.</summary>
    public static TaskbarEdge? DockedEdge(PixelRect work, PixelRect monitor)
    {
        if (work.Bottom < monitor.Bottom) return TaskbarEdge.Bottom;
        if (work.Y > monitor.Y) return TaskbarEdge.Top;
        if (work.X > monitor.X) return TaskbarEdge.Left;
        if (work.Right < monitor.Right) return TaskbarEdge.Right;
        return null;
    }

    /// <summary>
    /// Thickness of an auto-hide taskbar from its window rectangle, or the Windows 11 height when that rectangle is
    /// too thin or too thick to be a taskbar (for example if the shell shrinks the window while it is hidden).
    /// </summary>
    public static int AutoHideThickness(PixelRect bar, TaskbarEdge edge, PixelRect monitor, double scale)
    {
        bool horizontal = edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
        int thickness = horizontal ? bar.Height : bar.Width;
        int span = horizontal ? monitor.Height : monitor.Width;
        int min = (int)Math.Round(24 * scale);
        return thickness >= min && thickness <= span / 2 ? thickness : (int)Math.Round(DefaultThicknessDip * scale);
    }

    /// <summary>
    /// The part of the monitor windows can use without going under the taskbar, and the taskbar's edge.
    /// <paramref name="autoHideEdge"/> is the edge of an auto-hide taskbar on this monitor, if there is one.
    /// Without any taskbar the bottom edge is assumed, the Windows default.
    /// </summary>
    public static (PixelRect Area, TaskbarEdge Edge) FreeArea(PixelRect work, PixelRect monitor, TaskbarEdge? autoHideEdge, int autoHideThickness)
    {
        if (DockedEdge(work, monitor) is TaskbarEdge docked) return (work, docked);
        if (autoHideEdge is not TaskbarEdge edge || autoHideThickness <= 0) return (work, TaskbarEdge.Bottom);
        int t = autoHideThickness;
        var area = edge switch
        {
            TaskbarEdge.Top => work with { Y = work.Y + t, Height = work.Height - t },
            TaskbarEdge.Left => work with { X = work.X + t, Width = work.Width - t },
            TaskbarEdge.Right => work with { Width = work.Width - t },
            _ => work with { Height = work.Height - t },
        };
        return (area, edge);
    }

    /// <summary>
    /// Top-left corner for a window of <paramref name="width"/> × <paramref name="height"/> next to the taskbar near
    /// <paramref name="anchorX"/>, <paramref name="anchorY"/>, inside <paramref name="area"/> with <paramref name="margin"/>
    /// pixels to spare, like the Windows flyouts.
    /// </summary>
    public static (int X, int Y) NearTaskbar(PixelRect area, TaskbarEdge edge, int anchorX, int anchorY, int width, int height, int margin)
    {
        int minX = area.X + margin, maxX = Math.Max(minX, area.Right - width - margin);
        int minY = area.Y + margin, maxY = Math.Max(minY, area.Bottom - height - margin);
        int centeredX = Math.Clamp(anchorX - width / 2, minX, maxX);
        int centeredY = Math.Clamp(anchorY - height / 2, minY, maxY);
        return edge switch
        {
            TaskbarEdge.Top => (centeredX, minY),
            TaskbarEdge.Left => (minX, centeredY),
            TaskbarEdge.Right => (maxX, centeredY),
            _ => (centeredX, maxY),
        };
    }

    /// <summary>Top-left corner for a window in the bottom right corner of <paramref name="area"/>, like the volume indicator.</summary>
    public static (int X, int Y) BottomRight(PixelRect area, int width, int height, int margin) =>
        (Math.Max(area.X + margin, area.Right - width - margin), Math.Max(area.Y + margin, area.Bottom - height - margin));
}
