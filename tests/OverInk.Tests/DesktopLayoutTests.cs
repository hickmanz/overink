using System.Windows;
using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public sealed class DesktopLayoutTests
{
    // 1920x1080 primary at 100%, with a 4K monitor to its right at 150%.
    private static readonly MonitorGeometry Primary =
        MonitorGeometry.FromPixels(new Rect(0, 0, 1920, 1080), 96, 96, new IntPtr(1), @"\\.\DISPLAY1", isPrimary: true);

    private static readonly MonitorGeometry RightHiDpi =
        MonitorGeometry.FromPixels(new Rect(1920, 0, 3840, 2160), 144, 144, new IntPtr(2), @"\\.\DISPLAY2");

    // A 1440p monitor placed to the LEFT of the primary, so its pixel coordinates are negative.
    private static readonly MonitorGeometry LeftOfPrimary =
        MonitorGeometry.FromPixels(new Rect(-2560, -180, 2560, 1440), 96, 96, new IntPtr(3), @"\\.\DISPLAY3");

    [Fact]
    public void FromPixels_DividesByDpiScale()
    {
        Assert.Equal(1.5, RightHiDpi.ScaleX, 6);
        Assert.Equal(new Rect(1280, 0, 2560, 1440), RightHiDpi.Dips);
        Assert.Equal(1.0, Primary.ScaleY, 6);
    }

    [Fact]
    public void VirtualDesktop_IsBoundingBoxIncludingNegativeCoordinates()
    {
        var desktop = DesktopLayout.VirtualDesktop(new[] { Primary, RightHiDpi, LeftOfPrimary });

        Assert.Equal(-2560, desktop.Left);
        Assert.Equal(-180, desktop.Top);
        Assert.Equal(2560 + 1920 + 3840, desktop.Width);
        Assert.Equal(2160 + 180, desktop.Height);
    }

    [Fact]
    public void VirtualDesktop_OfNothing_IsEmpty()
    {
        Assert.True(DesktopLayout.VirtualDesktop(Array.Empty<MonitorGeometry>()).IsEmpty);
    }

    [Fact]
    public void MonitorAt_UsesHalfOpenEdgesSoNeighboursNeverBothMatch()
    {
        var monitors = new[] { Primary, RightHiDpi };

        Assert.Equal(Primary, DesktopLayout.MonitorAt(monitors, new Point(1919, 500)));
        Assert.Equal(RightHiDpi, DesktopLayout.MonitorAt(monitors, new Point(1920, 500)));
        Assert.Null(DesktopLayout.MonitorAt(monitors, new Point(-1, 0)));
    }

    [Fact]
    public void NearestMonitor_PicksClosestWhenPointIsInAGap()
    {
        // Primary is 1080 tall; the right monitor is taller, so (1000, 1500) is below primary and left of the right one.
        var monitors = new[] { Primary, RightHiDpi };

        var nearest = DesktopLayout.NearestMonitor(monitors, new Point(1000, 1500));

        Assert.Equal(Primary, nearest);
        Assert.Equal(RightHiDpi, DesktopLayout.NearestMonitor(monitors, new Point(5000, 1500)));
    }

    [Fact]
    public void NearestMonitor_WithNoMonitors_IsNull()
    {
        Assert.Null(DesktopLayout.NearestMonitor(Array.Empty<MonitorGeometry>(), new Point(0, 0)));
    }

    [Fact]
    public void MonitorForRect_ChoosesTheMonitorWithMostOverlap()
    {
        var monitors = new[] { Primary, RightHiDpi };

        // A window straddling the seam, mostly on the right monitor.
        var straddling = new Rect(1800, 100, 800, 400);
        Assert.Equal(RightHiDpi, DesktopLayout.MonitorForRect(monitors, straddling));

        // Mostly on the primary.
        Assert.Equal(Primary, DesktopLayout.MonitorForRect(monitors, new Rect(1200, 100, 800, 400)));
    }

    [Fact]
    public void MonitorForRect_FullyOffDesktop_FallsBackToNearest()
    {
        var monitors = new[] { Primary, RightHiDpi };

        Assert.Equal(RightHiDpi, DesktopLayout.MonitorForRect(monitors, new Rect(9000, 100, 200, 200)));
    }

    [Fact]
    public void SameLayout_IgnoresOrderButNotBounds()
    {
        var a = new[] { Primary, RightHiDpi };
        var reordered = new[] { RightHiDpi, Primary };
        var moved = new[]
        {
            Primary,
            MonitorGeometry.FromPixels(new Rect(1920, 0, 2560, 1440), 96, 96, new IntPtr(2), @"\\.\DISPLAY2")
        };

        Assert.True(DesktopLayout.SameLayout(a, reordered));
        Assert.False(DesktopLayout.SameLayout(a, moved));
        Assert.False(DesktopLayout.SameLayout(a, new[] { Primary }));
    }

    [Fact]
    public void SameLayout_DetectsADpiChange()
    {
        var before = new[] { RightHiDpi };
        var after = new[] { MonitorGeometry.FromPixels(RightHiDpi.Pixels, 96, 96, RightHiDpi.MonitorHandle, RightHiDpi.DeviceName) };

        Assert.False(DesktopLayout.SameLayout(before, after));
    }

    [Fact]
    public void Normalized_RoundTripsThroughAnAreaWithNegativeOrigin()
    {
        var area = DesktopLayout.VirtualDesktop(new[] { Primary, RightHiDpi, LeftOfPrimary });
        var pixel = new Point(-1000, 700);

        var (nx, ny) = DesktopLayout.PixelsToNormalized(pixel, area);
        var back = DesktopLayout.NormalizedToPixels(nx, ny, area);

        Assert.Equal(pixel.X, back.X, 6);
        Assert.Equal(pixel.Y, back.Y, 6);
    }

    [Fact]
    public void NormalizedToPixels_ClampsOutsideZeroToOne()
    {
        var area = new Rect(100, 50, 400, 200);

        Assert.Equal(new Point(100, 50), DesktopLayout.NormalizedToPixels(-3, -3, area));
        Assert.Equal(new Point(500, 250), DesktopLayout.NormalizedToPixels(9, 9, area));
    }

    [Fact]
    public void MonitorLocalDips_ConvertToPixelsUsingThatMonitorsScale()
    {
        // 100 DIPs right of the 150% monitor's left edge is 150 physical pixels.
        var pixel = DesktopLayout.MonitorLocalDipsToPixels(RightHiDpi, new Point(100, 40));

        Assert.Equal(1920 + 150, pixel.X, 6);
        Assert.Equal(60, pixel.Y, 6);

        var back = DesktopLayout.PixelsToMonitorLocalDips(RightHiDpi, pixel);
        Assert.Equal(100, back.X, 6);
        Assert.Equal(40, back.Y, 6);
    }
}
