namespace UsageBar.Core;

/// <summary>A rectangle in physical screen pixels; right and bottom are exclusive, as in a Win32 RECT.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Intersects(PixelRect o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
}

public enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>
/// Where the flyout opens: against the taskbar the icon sits on, the way Windows' own flyouts do. The taskbar's
/// rectangle decides the edge, not the work area, because an app bar or an auto-hidden taskbar changes the work area.
/// </summary>
public static class FlyoutPlacement
{
    public static TaskbarEdge EdgeOf(PixelRect taskbar, PixelRect monitor)
    {
        if (taskbar.Width >= taskbar.Height)
            return taskbar.Top - monitor.Top <= monitor.Bottom - taskbar.Bottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        return taskbar.Left - monitor.Left <= monitor.Right - taskbar.Right ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>The area the flyout may cover: the work area less the taskbar (an auto-hidden one is not taken out of it).</summary>
    public static (PixelRect Area, TaskbarEdge Edge) Area(PixelRect work, PixelRect monitor, PixelRect? taskbar)
    {
        if (taskbar is not { } t || !t.Intersects(monitor))
        {
            // No taskbar on this monitor: guess the edge from the work area.
            var guess = work.Bottom < monitor.Bottom ? TaskbarEdge.Bottom
                : work.Top > monitor.Top ? TaskbarEdge.Top
                : work.Left > monitor.Left ? TaskbarEdge.Left
                : work.Right < monitor.Right ? TaskbarEdge.Right
                : TaskbarEdge.Bottom;
            return (work, guess);
        }
        var edge = EdgeOf(t, monitor);
        var area = edge switch
        {
            TaskbarEdge.Bottom => work with { Bottom = Math.Min(work.Bottom, t.Top) },
            TaskbarEdge.Top => work with { Top = Math.Max(work.Top, t.Bottom) },
            TaskbarEdge.Left => work with { Left = Math.Max(work.Left, t.Right) },
            _ => work with { Right = Math.Min(work.Right, t.Left) },
        };
        return (area, edge);
    }

    /// <summary>The flyout's top-left corner: centred on the icon along the taskbar, <paramref name="margin"/> from it, and on screen.</summary>
    public static (int X, int Y) Place(PixelRect work, PixelRect monitor, PixelRect? taskbar, int anchorX, int anchorY,
                                       int width, int height, int margin)
    {
        var (a, edge) = Area(work, monitor, taskbar);
        int x = anchorX - width / 2, y = anchorY - height / 2;
        switch (edge)
        {
            case TaskbarEdge.Bottom: y = a.Bottom - margin - height; break;
            case TaskbarEdge.Top: y = a.Top + margin; break;
            case TaskbarEdge.Left: x = a.Left + margin; break;
            case TaskbarEdge.Right: x = a.Right - margin - width; break;
        }
        x = Math.Clamp(x, a.Left + margin, Math.Max(a.Left + margin, a.Right - margin - width));
        y = Math.Clamp(y, a.Top + margin, Math.Max(a.Top + margin, a.Bottom - margin - height));
        return (x, y);
    }
}
