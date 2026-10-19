using OverInk.Models;
using OverInk.Services;

namespace OverInk.Remote;

public sealed record RemoteStreamEstimate(
    int MonitorPixelWidth,
    int MonitorPixelHeight,
    int MonitorDipWidth,
    int MonitorDipHeight,
    double ScalePercent,
    int StreamWidth,
    int StreamHeight,
    int StreamLongEdge,
    int MonitorLongEdge,
    double DownscaleRatio,
    string MonitorLabel,
    string Guidance,
    double EstimatedKbps);

public static class RemoteStreamEstimator
{
    public static RemoteStreamEstimate Estimate(AppSettings settings)
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        var pw = Math.Max(1, (int)geometry.Pixels.Width);
        var ph = Math.Max(1, (int)geometry.Pixels.Height);
        var dw = Math.Max(1, (int)geometry.Dips.Width);
        var dh = Math.Max(1, (int)geometry.Dips.Height);
        var longEdge = Math.Max(pw, ph);
        var maxEdge = Math.Max(320, settings.RemoteMaxEdge);
        var scale = Math.Min(1.0, maxEdge / (double)longEdge);
        var streamW = Math.Max(1, (int)(pw * scale));
        var streamH = Math.Max(1, (int)(ph * scale));
        var streamLong = Math.Max(streamW, streamH);
        var dipScale = pw / (double)dw;
        var label = ClassifyMonitor(pw, ph);
        var downscale = streamLong / (double)longEdge;
        var guidance = BuildGuidance(downscale, streamLong, longEdge, settings);
        var kbps = EstimateKbps(streamW, streamH, settings.RemoteMaxFps, settings.RemoteJpegQuality);

        return new RemoteStreamEstimate(
            pw, ph, dw, dh,
            Math.Round(dipScale * 100, 0),
            streamW, streamH, streamLong, longEdge,
            downscale,
            label,
            guidance,
            kbps);
    }

    public static (int MaxEdge, int Fps, int Quality) PresetBalanced(int monitorLongEdge) =>
        (Math.Min(1920, monitorLongEdge), 10, 75);

    public static (int MaxEdge, int Fps, int Quality) PresetSharp(int monitorLongEdge) =>
        (Math.Min(2560, monitorLongEdge), 12, 82);

    public static (int MaxEdge, int Fps, int Quality) PresetNative(int monitorLongEdge) =>
        (Math.Min(7680, monitorLongEdge), 10, 85);

    public static (int MaxEdge, int Fps, int Quality) PresetEco() => (1280, 6, 65);

    private static string ClassifyMonitor(int w, int h)
    {
        var longEdge = Math.Max(w, h);
        if (longEdge >= 3800) return "4K-class display";
        if (longEdge >= 2500) return "1440p-class display";
        if (longEdge >= 1900) return "1080p-class display";
        return "Standard display";
    }

    private static string BuildGuidance(double downscaleRatio, int streamLong, int monitorLong, AppSettings settings)
    {
        if (downscaleRatio >= 0.98)
        {
            return "Streaming at full monitor resolution — best for pinch-zoom on remote clients.";
        }

        var pct = (int)Math.Round(downscaleRatio * 100);
        if (pct < 55)
        {
            return $"Only {pct}% of native resolution — remote zoom will look soft. Try “Sharp” or “Native” preset.";
        }

        if (settings.RemoteMaxFps <= 6)
        {
            return "Low frame rate saves Wi‑Fi; motion on screen will look choppy.";
        }

        if (settings.RemoteJpegQuality < 65)
        {
            return "JPEG quality is low — text and fine lines may show compression artifacts.";
        }

        return $"Downscaling to {pct}% of native ({streamLong}px long edge). Good balance for most Wi‑Fi.";
    }

    private static double EstimateKbps(int streamW, int streamH, int fps, int quality)
    {
        var pixels = streamW * (double)streamH;
        var bytesPerFrame = pixels * (quality / 100.0) * 0.12;
        bytesPerFrame = Math.Clamp(bytesPerFrame, 25_000, 1_200_000);
        return bytesPerFrame * Math.Max(1, fps) * 8 / 1000.0;
    }
}
