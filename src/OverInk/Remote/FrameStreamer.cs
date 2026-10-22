using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Services;

namespace OverInk.Remote;

public sealed class FrameStreamer : IDisposable
{
    private readonly OverlayWindow _overlay;
    private readonly Func<AppSettings> _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _forceNextFrame;

    public FrameStreamer(OverlayWindow overlay, Func<AppSettings> settings)
    {
        _overlay = overlay;
        _settings = settings;
    }

    public event Action<byte[]>? FrameReady;

    public void RequestImmediateFrame() => _forceNextFrame = true;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        var lastHash = Array.Empty<byte>();
        var lastSentUtc = DateTime.UtcNow;
        var seq = 0u;
        var sentOnce = false;

        while (!token.IsCancellationRequested)
        {
            var settings = _settings();
            var minInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, settings.RemoteMaxFps));
            await Task.Delay(minInterval, token).ConfigureAwait(false);

            try
            {
                // Read before capturing so a frame never claims a revision newer than what it shows.
                var inkRevision = _overlay.InkRevision;
                var bounds = StreamTargetResolver.GetStreamPixels(settings);
                var width = (int)Math.Max(1, bounds.Width);
                var height = (int)Math.Max(1, bounds.Height);

                using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(full))
                {
                    CaptureArea(g, bounds, width, height);
                }

                var maxEdge = RemoteStreamEstimator.GetEffectiveMaxEdge(settings);
                var scale = Math.Min(1.0, maxEdge / (double)Math.Max(width, height));
                var targetW = Math.Max(1, (int)(width * scale));
                var targetH = Math.Max(1, (int)(height * scale));

                using var scaled = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, 0, 0, targetW, targetH);
                }

                var thumbHash = ComputeThumbHash(scaled);
                var changed = !thumbHash.SequenceEqual(lastHash);
                if (_forceNextFrame)
                {
                    _forceNextFrame = false;
                    changed = true;
                }

                var keepAlive = DateTime.UtcNow - lastSentUtc > TimeSpan.FromSeconds(2);
                if (!sentOnce && !changed && !keepAlive)
                {
                    // Always push at least one frame so remote clients see the desktop quickly.
                    changed = true;
                }

                if (!changed && !keepAlive)
                {
                    continue;
                }

                sentOnce = true;

                lastHash = thumbHash;
                lastSentUtc = DateTime.UtcNow;

                using var ms = new MemoryStream();
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, settings.RemoteJpegQuality);
                scaled.Save(ms, codec, encoderParams);
                var jpeg = ms.ToArray();
                if (jpeg.Length > 1024 * 1024)
                {
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Max(40, settings.RemoteJpegQuality - 20));
                    ms.SetLength(0);
                    scaled.Save(ms, codec, encoderParams);
                    jpeg = ms.ToArray();
                }

                var payload = BuildBinaryFrame(seq++, inkRevision, (ushort)targetW, (ushort)targetH, jpeg);
                FrameReady?.Invoke(payload);
            }
            catch (Exception ex)
            {
                RemoteDebugLog.Write($"frame capture error: {ex.Message}");
            }
        }
    }

    /// <summary>Colour of desktop areas no monitor covers, so the iPad shows them as "no screen here" rather than black.</summary>
    internal static readonly Color GapColor = Color.FromArgb(0x23, 0x25, 0x2a);

    /// <summary>
    /// Copies the screen area into the graphics surface. With several monitors the streamed rectangle can include
    /// empty desktop (monitors of different sizes or offsets); those gaps are filled with <see cref="GapColor"/>
    /// and only the real monitors are captured.
    /// </summary>
    private static void CaptureArea(Graphics g, System.Windows.Rect bounds, int width, int height)
    {
        var parts = DesktopLayout.Intersections(MonitorHelper.GetAllMonitors(), bounds);
        if (parts.Count > 0 && !DesktopLayout.CoversFully(parts.Select(p => p.Pixels), bounds))
        {
            g.Clear(GapColor);
            foreach (var (_, part) in parts)
            {
                CopyScreen(g, (int)part.X, (int)part.Y, (int)(part.X - bounds.X), (int)(part.Y - bounds.Y),
                    (int)Math.Max(1, part.Width), (int)Math.Max(1, part.Height));
            }

            return;
        }

        CopyScreen(g, (int)bounds.X, (int)bounds.Y, 0, 0, width, height);
    }

    private static void CopyScreen(Graphics g, int sourceX, int sourceY, int destX, int destY, int width, int height) =>
        // .NET 10+ rejects combined SourceCopy|CaptureBlt as an enum value; SourceCopy matches ScreenCaptureService.
        g.CopyFromScreen(sourceX, sourceY, destX, destY, new Size(width, height), CopyPixelOperation.SourceCopy);

    private static byte[] BuildBinaryFrame(uint seq, long inkRevision, ushort w, ushort h, byte[] jpeg)
    {
        using var ms = new MemoryStream(13 + jpeg.Length);
        ms.WriteByte(RemoteProtocol.BinaryFrameType);
        ms.Write(BitConverter.GetBytes(seq));
        ms.Write(BitConverter.GetBytes(inkRevision));
        ms.Write(BitConverter.GetBytes(w));
        ms.Write(BitConverter.GetBytes(h));
        ms.Write(jpeg, 0, jpeg.Length);
        return ms.ToArray();
    }

    private static byte[] ComputeThumbHash(Bitmap bitmap)
    {
        // Keeps the frame's aspect ratio (a 64x36 grid squeezes a wide multi-monitor desktop into too few columns)
        // and averages the source pixels, so a small change such as one typed character still alters the hash.
        const int tw = 128;
        var th = Math.Clamp((int)Math.Round(tw * bitmap.Height / (double)Math.Max(1, bitmap.Width)), 24, 96);
        using var thumb = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(bitmap, 0, 0, tw, th);
        }

        var rect = new Rectangle(0, 0, tw, th);
        var data = thumb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * th];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SHA256.HashData(bytes);
        }
        finally
        {
            thumb.UnlockBits(data);
        }
    }

    public void Dispose() => Stop();
}
