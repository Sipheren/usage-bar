using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>Where the flyout opens, in physical pixels: beside the taskbar the icon is on, never over it.</summary>
public class FlyoutPlacementTests
{
    static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    const int W = 360, H = 400, M = 12;

    [Fact]
    public void BottomTaskbarOpensAboveItNearTheIcon()
    {
        var work = Monitor with { Bottom = 1032 };
        var taskbar = new PixelRect(0, 1032, 1920, 1080);
        var (x, y) = FlyoutPlacement.Place(work, Monitor, taskbar, 1850, 1056, W, H, M);
        Assert.Equal(1032 - M - H, y);
        Assert.Equal(1920 - M - W, x);   // clamped inside the right edge
    }

    [Fact]
    public void AutoHideTaskbarStillOpensAboveIt()
    {
        // An auto-hidden taskbar leaves the work area as the whole monitor.
        var taskbar = new PixelRect(0, 1032, 1920, 1080);
        var (_, y) = FlyoutPlacement.Place(Monitor, Monitor, taskbar, 1000, 1056, W, H, M);
        Assert.Equal(1032 - M - H, y);
    }

    [Fact]
    public void AnAppBarOnTheRightDoesNotMoveTheFlyoutOffTheBottomTaskbar()
    {
        var work = new PixelRect(0, 0, 1800, 1032);   // a docked app bar takes the right 120 px
        var taskbar = new PixelRect(0, 1032, 1920, 1080);
        var (x, y) = FlyoutPlacement.Place(work, Monitor, taskbar, 1000, 1056, W, H, M);
        Assert.Equal(1032 - M - H, y);
        Assert.Equal(1000 - W / 2, x);
    }

    [Fact]
    public void TopTaskbarOpensBelowIt()
    {
        var taskbar = new PixelRect(0, 0, 1920, 48);
        var (_, y) = FlyoutPlacement.Place(Monitor with { Top = 48 }, Monitor, taskbar, 1000, 24, W, H, M);
        Assert.Equal(48 + M, y);
    }

    [Fact]
    public void SideTaskbarsOpenBesideThemAtTheIconHeight()
    {
        var left = new PixelRect(0, 0, 60, 1080);
        var (lx, ly) = FlyoutPlacement.Place(Monitor with { Left = 60 }, Monitor, left, 30, 900, W, H, M);
        Assert.Equal(60 + M, lx);
        Assert.Equal(1080 - M - H, ly);   // centred on the icon, then kept on screen

        var right = new PixelRect(1860, 0, 1920, 1080);
        var (rx, ry) = FlyoutPlacement.Place(Monitor with { Right = 1860 }, Monitor, right, 1890, 500, W, H, M);
        Assert.Equal(1860 - M - W, rx);
        Assert.Equal(500 - H / 2, ry);
    }

    [Fact]
    public void ATaskbarOnAnotherMonitorFallsBackToTheWorkArea()
    {
        var second = new PixelRect(1920, 0, 3840, 1080);
        var taskbar = new PixelRect(0, 1032, 1920, 1080);   // the taskbar is on the first monitor
        var (_, y) = FlyoutPlacement.Place(second with { Bottom = 1040 }, second, taskbar, 2500, 500, W, H, M);
        Assert.Equal(1040 - M - H, y);
    }

    [Fact]
    public void ATallerFlyoutThanTheScreenStaysAtTheTop()
    {
        var work = Monitor with { Bottom = 1032 };
        var taskbar = new PixelRect(0, 1032, 1920, 1080);
        var (_, y) = FlyoutPlacement.Place(work, Monitor, taskbar, 1000, 1056, W, 2000, M);
        Assert.Equal(M, y);
    }

    [Fact]
    public void EdgeComesFromTheTaskbarShapeAndSide()
    {
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.EdgeOf(new PixelRect(0, 1032, 1920, 1080), Monitor));
        Assert.Equal(TaskbarEdge.Top, FlyoutPlacement.EdgeOf(new PixelRect(0, 0, 1920, 48), Monitor));
        Assert.Equal(TaskbarEdge.Left, FlyoutPlacement.EdgeOf(new PixelRect(0, 0, 60, 1080), Monitor));
        Assert.Equal(TaskbarEdge.Right, FlyoutPlacement.EdgeOf(new PixelRect(1860, 0, 1920, 1080), Monitor));
    }
}
