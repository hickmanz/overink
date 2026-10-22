using System.Windows;
using OverInk.Models;
using OverInk.Remote;
using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public sealed class StreamTargetResolverTests
{
    // Mirrors the real mixed-DPI desktop: 150% primary (7680x2160) with a 100% monitor below it.
    private static readonly MonitorGeometry Primary = MonitorGeometry.FromPixels(
        new Rect(0, 0, 7680, 2160), 144, 144, new IntPtr(1), @"\\.\DISPLAY1", true);

    private static readonly MonitorGeometry Secondary = MonitorGeometry.FromPixels(
        new Rect(3000, 2160, 2560, 720), 96, 96, new IntPtr(2), @"\\.\DISPLAY2");

    private static readonly IReadOnlyList<MonitorGeometry> Monitors = new[] { Primary, Secondary };

    [Fact]
    public void FollowToolbar_UsesTheFollowedMonitor()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.FollowToolbar };

        Assert.Equal(Secondary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, Secondary.DeviceName));
        Assert.Equal(Primary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, Primary.DeviceName));
    }

    [Fact]
    public void FollowToolbar_UnknownDevice_FallsBackToPrimary()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.FollowToolbar };

        Assert.Equal(Primary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, @"\\.\DISPLAY9"));
        Assert.Equal(Primary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, null));
    }

    [Fact]
    public void SpecificMonitor_WinsOverFollowedMonitor()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.SpecificMonitor, StreamMonitor = Secondary.DeviceName };

        Assert.Equal(Secondary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, Primary.DeviceName));
    }

    [Fact]
    public void SpecificMonitor_Unplugged_FallsBackToFollowedMonitor()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.SpecificMonitor, StreamMonitor = @"\\.\DISPLAY7" };

        Assert.Equal(Secondary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, Secondary.DeviceName));
    }

    [Fact]
    public void AllMonitors_IsTheVirtualDesktop()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.AllMonitors };

        Assert.Equal(new Rect(0, 0, 7680, 2880), StreamTargetResolver.GetStreamPixels(settings, Monitors, null));
    }

    [Fact]
    public void Region_OverridesTarget_AndLivesOnItsMonitor()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.AllMonitors };
        StreamTargetResolver.SetRegionFromPixels(settings, new Rect(3100, 2200, 1000, 300), Monitors);

        Assert.True(settings.RemoteRegionEnabled);
        Assert.Equal(Secondary.DeviceName, settings.RemoteRegionMonitor);

        var pixels = StreamTargetResolver.GetStreamPixels(settings, Monitors, Primary.DeviceName);
        Assert.Equal(3100, pixels.X, 3);
        Assert.Equal(2200, pixels.Y, 3);
        Assert.Equal(1000, pixels.Width, 3);
        Assert.Equal(300, pixels.Height, 3);
    }

    [Fact]
    public void LegacyRegion_WithoutMonitor_AppliesToPrimary()
    {
        var settings = new AppSettings
        {
            RemoteRegionEnabled = true,
            RemoteRegionMonitor = string.Empty,
            RemoteRegionX = 0.5,
            RemoteRegionY = 0.25,
            RemoteRegionW = 0.25,
            RemoteRegionH = 0.5,
        };

        var pixels = StreamTargetResolver.GetStreamPixels(settings, Monitors, Secondary.DeviceName);

        Assert.Equal(new Rect(3840, 540, 1920, 1080), pixels);
    }

    [Fact]
    public void ClearRegion_ReturnsToTarget()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.FollowToolbar };
        StreamTargetResolver.SetRegionFromPixels(settings, new Rect(100, 100, 800, 600), Monitors);
        StreamTargetResolver.ClearRegion(settings);

        Assert.False(settings.RemoteRegionEnabled);
        Assert.Equal(Primary.Pixels, StreamTargetResolver.GetStreamPixels(settings, Monitors, Primary.DeviceName));
    }

    [Fact]
    public void ClampInside_KeepsRectInsideAreaAndEnforcesMinimumSize()
    {
        var area = new Rect(0, 0, 1000, 500);

        var clamped = StreamTargetResolver.ClampInside(new Rect(900, 450, 400, 400), area);
        Assert.True(clamped.Left >= area.Left && clamped.Top >= area.Top);
        Assert.True(clamped.Right <= area.Right && clamped.Bottom <= area.Bottom);

        var tiny = StreamTargetResolver.ClampInside(new Rect(10, 10, 2, 2), area);
        Assert.True(tiny.Width >= StreamTargetResolver.MinRegionPixels);
        Assert.True(tiny.Height >= StreamTargetResolver.MinRegionPixels);
    }

    [Fact]
    public void RegionToPixels_NeverLeavesTheMonitor()
    {
        var settings = new AppSettings { RemoteRegionX = 0.9, RemoteRegionY = 0.9, RemoteRegionW = 0.5, RemoteRegionH = 0.5 };

        var pixels = StreamTargetResolver.RegionToPixels(settings, Secondary.Pixels);

        Assert.True(pixels.Right <= Secondary.Pixels.Right + 0.001);
        Assert.True(pixels.Bottom <= Secondary.Pixels.Bottom + 0.001);
    }

    [Fact]
    public void StreamMonitorRects_WholeDesktop_ListsEachMonitorAsFractions()
    {
        var rects = StreamTargetResolver.GetStreamMonitorRects(new Rect(0, 0, 7680, 2880), Monitors);

        Assert.Equal(2, rects.Count);
        Assert.Equal(new Rect(0, 0, 1, 2160.0 / 2880), rects[0].Normalized);
        Assert.True(rects[0].IsPrimary);
        Assert.Equal(3000.0 / 7680, rects[1].Normalized.X, 6);
        Assert.Equal(2160.0 / 2880, rects[1].Normalized.Y, 6);
        Assert.Equal(2560.0 / 7680, rects[1].Normalized.Width, 6);
        Assert.False(rects[1].IsPrimary);
    }

    [Fact]
    public void StreamMonitorRects_SingleMonitorOrRegion_IsEmpty()
    {
        Assert.Empty(StreamTargetResolver.GetStreamMonitorRects(Primary.Pixels, Monitors));
        Assert.Empty(StreamTargetResolver.GetStreamMonitorRects(new Rect(100, 100, 800, 600), Monitors));
    }

    [Fact]
    public void StreamEpoch_ChangesWithTheStreamedRectangle()
    {
        var a = StreamTargetResolver.GetStreamEpoch(Primary.Pixels);
        Assert.Equal(a, StreamTargetResolver.GetStreamEpoch(Primary.Pixels));
        Assert.NotEqual(a, StreamTargetResolver.GetStreamEpoch(Secondary.Pixels));
        Assert.NotEqual(a, StreamTargetResolver.GetStreamEpoch(new Rect(0, 0, 7680, 2880)));
    }

    [Fact]
    public void WholeDesktop_HasGapsWhereNoMonitorIs()
    {
        var desktop = new Rect(0, 0, 7680, 2880);
        var parts = DesktopLayout.Intersections(Monitors, desktop);

        Assert.Equal(2, parts.Count);
        Assert.False(DesktopLayout.CoversFully(parts.Select(p => p.Pixels), desktop));
        Assert.True(DesktopLayout.CoversFully(
            DesktopLayout.Intersections(Monitors, Primary.Pixels).Select(p => p.Pixels), Primary.Pixels));
    }

    [Fact]
    public void Intersections_ClipToTheArea()
    {
        var parts = DesktopLayout.Intersections(Monitors, new Rect(2500, 2000, 1000, 400));

        Assert.Equal(2, parts.Count);
        Assert.Equal(new Rect(2500, 2000, 1000, 160), parts[0].Pixels);
        Assert.Equal(new Rect(3000, 2160, 500, 240), parts[1].Pixels);
    }

    [Fact]
    public void AutoStreamSize_OfTheWholeDesktop_IsCapped()
    {
        var settings = new AppSettings { StreamTarget = StreamTarget.AllMonitors, RemoteMaxEdge = 0 };

        Assert.True(RemoteStreamEstimator.GetEffectiveMaxEdge(settings) <= RemoteStreamEstimator.AutoAllMonitorsMaxEdge);

        settings.RemoteMaxEdge = 6000;
        Assert.Equal(6000, RemoteStreamEstimator.GetEffectiveMaxEdge(settings));
    }

    [Fact]
    public void NotifyToolbarMoved_RaisesOnlyWhenTheMonitorChanges()
    {
        var raised = 0;
        void Handler() => raised++;
        StreamTargetResolver.FollowMonitorChanged += Handler;
        try
        {
            StreamTargetResolver.NotifyToolbarMoved(new Rect(100, 100, 800, 70), Monitors);
            var afterFirst = raised;
            StreamTargetResolver.NotifyToolbarMoved(new Rect(200, 120, 800, 70), Monitors);
            Assert.Equal(afterFirst, raised);

            StreamTargetResolver.NotifyToolbarMoved(new Rect(3500, 2300, 800, 70), Monitors);
            Assert.Equal(afterFirst + 1, raised);
            Assert.Equal(Secondary.DeviceName, StreamTargetResolver.FollowMonitorDevice);
        }
        finally
        {
            StreamTargetResolver.FollowMonitorChanged -= Handler;
        }
    }
}
