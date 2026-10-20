using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverInk.Editor;
using OverInk.Models;

namespace OverInk.Remote;

public sealed record RemoteLibraryItemDto(string Id, string Name, long CreatedAt, int W, int H, string Rev, bool Editable);

public sealed record RemoteLibraryStroke(string Color, double Size, float[] Pts, bool IgnorePressure);

/// <summary><c>Veil</c> is the capture's white wash (0–0.9); the original is served clean and the client applies it.</summary>
public sealed record RemoteLibraryStrokes(string Rev, int W, int H, List<RemoteLibraryStroke> Strokes, double Veil = 0);

/// <summary><c>Veil</c> is optional: when absent, the capture's stored veil is kept.</summary>
public sealed record RemoteLibrarySave(string BaseRev, List<RemoteLibraryStroke> Strokes, double? Veil = null);

/// <summary>
/// HTTP side of the iPad capture library: list, thumbnails, original image, ink read/write, delete.
/// Library data is too large for the 64 KB WebSocket message limit and <c>&lt;img&gt;</c> tags cannot send
/// headers, so it is served over plain HTTP guarded by a per-connection capability token (<c>?k=</c>)
/// that is only handed to the paired owner device.
/// </summary>
public sealed class RemoteLibraryService : IDisposable
{
    public const int MaxStrokes = 20_000;
    public const int MaxPointsPerStroke = 200_000;
    private const int ThumbEdge = 480;
    private const int MaxOriginalEdge = 4096;

    private readonly CaptureLibrary _library;
    private readonly Func<bool> _isEnabled;

    private readonly object _tokenGate = new();
    private readonly Dictionary<string, string> _tokenClient = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenByDevice = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    private readonly ConcurrentDictionary<string, byte[]> _thumbs = new();
    private readonly ConcurrentDictionary<string, (int W, int H)> _sizes = new();
    private (string Key, byte[] Bytes)? _scaledOriginal;

    private readonly Timer _debounce;
    private readonly object _snapshotGate = new();
    private Dictionary<string, string> _lastRevisions = new(StringComparer.OrdinalIgnoreCase);

    public RemoteLibraryService(CaptureLibrary library, Func<bool> isEnabled)
    {
        _library = library;
        _isEnabled = isEnabled;
        _debounce = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _lastRevisions = Snapshot();
        }
        catch
        {
            // Folder unavailable at start; the first change event rebuilds the snapshot.
        }

        _library.LibraryChanged += OnLibraryChanged;
    }

    /// <summary>Raised (off the UI thread) after library files change: whether the set of captures changed, and ids whose content changed.</summary>
    public event Action<bool, IReadOnlyList<(string Id, string Rev)>>? Changed;

    // ---------------------------------------------------------------- tokens

    /// <summary>Issues the capability token for one connection. A device holds one token at a time; a newer connection replaces the old.</summary>
    public string IssueToken(string deviceId, string clientId)
    {
        var token = CreateToken();
        lock (_tokenGate)
        {
            if (_tokenByDevice.TryGetValue(deviceId, out var old))
            {
                _tokenClient.Remove(old);
            }

            _tokenClient[token] = clientId;
            _tokenByDevice[deviceId] = token;
        }

        return token;
    }

    public void RevokeClient(string clientId)
    {
        lock (_tokenGate)
        {
            foreach (var (token, owner) in _tokenClient.ToArray())
            {
                if (owner != clientId)
                {
                    continue;
                }

                _tokenClient.Remove(token);
                foreach (var (device, deviceToken) in _tokenByDevice.ToArray())
                {
                    if (deviceToken == token)
                    {
                        _tokenByDevice.Remove(device);
                    }
                }
            }
        }
    }

    public void RevokeAll()
    {
        lock (_tokenGate)
        {
            _tokenClient.Clear();
            _tokenByDevice.Clear();
        }
    }

    public bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_tokenGate)
        {
            return _tokenClient.ContainsKey(token);
        }
    }

    private static string CreateToken()
    {
        Span<byte> buffer = stackalloc byte[24];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ---------------------------------------------------------------- routes

    public void Map(IEndpointRouteBuilder app)
    {
        // Cast so the IResult return value is written to the response (not discarded as a RequestDelegate).
        app.MapGet("/lib", (Delegate)ListAsync);
        app.MapGet("/lib/{id}/thumb", ThumbAsync);
        app.MapGet("/lib/{id}/original", OriginalAsync);
        app.MapGet("/lib/{id}/strokes", GetStrokesAsync);
        app.MapPost("/lib/{id}/strokes", SaveStrokesAsync);
        app.MapPost("/lib/{id}/duplicate", DuplicateAsync);
        app.MapDelete("/lib/{id}", DeleteAsync);
    }

    private IResult? Reject(HttpContext ctx)
    {
        if (!RemoteNetworkHelper.IsAllowedRemoteAddress(ctx.Connection.RemoteIpAddress))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!_isEnabled() || !IsValidToken(ctx.Request.Query["k"].ToString()))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return null;
    }

    private async Task<IResult> ListAsync(HttpContext ctx)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var items = await Task.Run(() => _library.Enumerate().Select(ToDto).ToList()).ConfigureAwait(false);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(items, RemoteJson.Options);
    }

    private async Task<IResult> ThumbAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var item = _library.TryGet(id);
        if (item is null || !File.Exists(item.FlattenedPath))
        {
            return Results.NotFound();
        }

        var key = $"{item.Id}|{_library.GetRevision(item)}";
        if (!_thumbs.TryGetValue(key, out var bytes))
        {
            await _thumbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await Task.Run(() => BuildThumbnail(item.FlattenedPath)).ConfigureAwait(false);
            }
            catch
            {
                return Results.NotFound();
            }
            finally
            {
                _thumbGate.Release();
            }

            if (_thumbs.Count > 400)
            {
                _thumbs.Clear();
            }

            _thumbs[key] = bytes;
        }

        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(bytes, "image/jpeg");
    }

    private async Task<IResult> OriginalAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var item = _library.TryGet(id);
        if (item is null)
        {
            return Results.NotFound();
        }

        var path = _library.OriginalPathFor(item);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        ctx.Response.Headers.CacheControl = "private, max-age=3600";


        if (!PngInfo.TryReadSize(path, out var w, out var h) || Math.Max(w, h) <= MaxOriginalEdge)
        {
            return Results.File(OpenShared(path), "image/png");
        }

        // Very large captures are downscaled so Safari's canvas limits and the Wi-Fi link are not stressed.
        // Strokes are exchanged in normalized coordinates, so the smaller image maps back exactly.
        var key = $"{item.Id}|{_library.GetRevision(item)}";
        if (_scaledOriginal is { } cached && cached.Key == key)
        {
            return Results.File(cached.Bytes, "image/png");
        }

        var scaled = await Task.Run(() => BuildScaledPng(path, w, h)).ConfigureAwait(false);
        _scaledOriginal = (key, scaled);
        return Results.File(scaled, "image/png");
    }

    private async Task<IResult> GetStrokesAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var item = _library.TryGet(id);
        if (item is null)
        {
            return Results.NotFound();
        }

        var dto = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var rev = _library.GetRevision(item);
            var (w, h) = GetSize(item);
            StrokeCollection strokes;
            try
            {
                strokes = _library.LoadStrokes(item);
            }
            catch
            {
                strokes = new StrokeCollection();
            }

            return new RemoteLibraryStrokes(rev, w, h, ToDtos(strokes), _library.LoadVeil(item));
        }).Task.ConfigureAwait(false);

        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(dto, RemoteJson.Options);
    }

    private async Task<IResult> SaveStrokesAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        RemoteLibrarySave? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RemoteLibrarySave>(ctx.Request.Body, RemoteJson.Options)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Malformed JSON.");
        }

        string? problem = null;
        if (request is null || request.Strokes is null || !TryValidate(request.Strokes, out problem))
        {
            return Results.BadRequest(problem ?? "Missing strokes.");
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var item = _library.TryGet(id);
            if (item is null)
            {
                return Results.NotFound();
            }

            var current = _library.GetRevision(item);
            if (!string.Equals(current, request.BaseRev, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "conflict", rev = current }, RemoteJson.Options, statusCode: StatusCodes.Status409Conflict);
            }

            var newRev = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var editable = _library.PromoteToEditable(item);
                var strokes = BuildStrokes(request.Strokes);
                var original = _library.LoadOriginal(editable);
                var veil = request.Veil is { } sent && double.IsFinite(sent)
                    ? Math.Clamp(sent, 0, CaptureLibrary.MaxVeil)
                    : _library.LoadVeil(editable);
                var flattened = CaptureRenderer.Flatten(original, strokes, veil);
                _library.SaveEdit(editable, strokes, flattened, veil);
                return _library.GetRevision(editable);
            }).Task.ConfigureAwait(false);

            _thumbs.Clear();
            lock (_snapshotGate)
            {
                // Our own write must not echo back as "changed on the PC".
                _lastRevisions[item.Id] = newRev;
            }

            return Results.Json(new { rev = newRev }, RemoteJson.Options);
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"lib save failed id={id}: {ex.Message}");
            return Results.Problem("Saving failed.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Copies a capture under a new name and returns the new library entry.</summary>
    private async Task<IResult> DuplicateAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var item = _library.TryGet(id);
        if (item is null)
        {
            return Results.NotFound();
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var copy = await Task.Run(() => _library.Duplicate(item)).ConfigureAwait(false);
            if (copy is null)
            {
                return Results.Problem("Duplicating failed.");
            }

            return Results.Json(ToDto(copy), RemoteJson.Options);
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"lib duplicate failed id={id}: {ex.Message}");
            return Results.Problem("Duplicating failed.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<IResult> DeleteAsync(HttpContext ctx, string id)
    {
        if (Reject(ctx) is { } denied)
        {
            return denied;
        }

        var item = _library.TryGet(id);
        if (item is null)
        {
            return Results.NotFound();
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => _library.Delete(item)).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }

        _thumbs.Clear();
        return Results.Ok(new { ok = true });
    }

    // ---------------------------------------------------------------- change notification

    private void OnLibraryChanged() => _debounce.Change(300, Timeout.Infinite);

    private void OnDebounced()
    {
        try
        {
            var current = Snapshot();
            bool listChanged;
            var changed = new List<(string Id, string Rev)>();
            lock (_snapshotGate)
            {
                listChanged = current.Count != _lastRevisions.Count
                              || current.Keys.Any(k => !_lastRevisions.ContainsKey(k));
                foreach (var (id, rev) in current)
                {
                    if (_lastRevisions.TryGetValue(id, out var previous) && previous != rev)
                    {
                        changed.Add((id, rev));
                    }
                }

                _lastRevisions = current;
            }

            if (listChanged || changed.Count > 0)
            {
                _thumbs.Clear();
                Changed?.Invoke(listChanged, changed);
            }
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"lib change scan failed: {ex.Message}");
        }
    }

    private Dictionary<string, string> Snapshot()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _library.Enumerate())
        {
            map[item.Id] = _library.GetRevision(item);
        }

        return map;
    }

    // ---------------------------------------------------------------- helpers

    private RemoteLibraryItemDto ToDto(CaptureItem item)
    {
        var (w, h) = GetSize(item);
        return new RemoteLibraryItemDto(
            item.Id,
            item.DisplayName,
            new DateTimeOffset(item.CreatedAt).ToUnixTimeMilliseconds(),
            w,
            h,
            _library.GetRevision(item),
            item.HasEditableSidecars);
    }

    private (int W, int H) GetSize(CaptureItem item)
    {
        var key = $"{item.Id}|{_library.GetRevision(item)}";
        if (_sizes.TryGetValue(key, out var size))
        {
            return size;
        }

        PngInfo.TryReadSize(_library.OriginalPathFor(item), out var w, out var h);
        if (_sizes.Count > 2000)
        {
            _sizes.Clear();
        }

        _sizes[key] = (w, h);
        return (w, h);
    }

    /// <summary>Checks client-supplied ink so a bad request cannot create absurd strokes or exhaust memory.</summary>
    public static bool TryValidate(IReadOnlyList<RemoteLibraryStroke> strokes, out string? problem)
    {
        problem = null;
        if (strokes.Count > MaxStrokes)
        {
            problem = "Too many strokes.";
            return false;
        }

        foreach (var s in strokes)
        {
            if (s is null || s.Pts is null || s.Pts.Length < 3 || s.Pts.Length % 3 != 0 || s.Pts.Length / 3 > MaxPointsPerStroke)
            {
                problem = "Invalid stroke points.";
                return false;
            }

            if (double.IsNaN(s.Size) || double.IsInfinity(s.Size))
            {
                problem = "Invalid stroke size.";
                return false;
            }
        }

        return true;
    }

    public static StrokeCollection BuildStrokes(IEnumerable<RemoteLibraryStroke> dtos)
    {
        var strokes = new StrokeCollection();
        foreach (var dto in dtos)
        {
            var points = new StylusPointCollection();
            for (var i = 0; i + 2 < dto.Pts.Length; i += 3)
            {
                var x = dto.Pts[i];
                var y = dto.Pts[i + 1];
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
                {
                    continue;
                }

                var pressure = float.IsNaN(dto.Pts[i + 2]) ? 0.5f : Math.Clamp(dto.Pts[i + 2], 0f, 1f);
                points.Add(new StylusPoint(x, y, pressure));
            }

            if (points.Count == 0)
            {
                continue;
            }

            var size = Math.Clamp(dto.Size, 0.5, 200);
            var attributes = new DrawingAttributes
            {
                Color = ColorHex.TryParse(dto.Color, out var color) ? System.Windows.Media.Color.FromRgb(color.R, color.G, color.B) : System.Windows.Media.Colors.Red,
                Width = size,
                Height = size,
                FitToCurve = false,
                IgnorePressure = dto.IgnorePressure
            };
            strokes.Add(new Stroke(points, attributes));
        }

        return strokes;
    }

    public static List<RemoteLibraryStroke> ToDtos(StrokeCollection strokes)
    {
        var list = new List<RemoteLibraryStroke>(strokes.Count);
        foreach (var stroke in strokes)
        {
            var points = stroke.StylusPoints;
            if (points.Count == 0)
            {
                continue;
            }

            var pts = new float[points.Count * 3];
            for (var i = 0; i < points.Count; i++)
            {
                pts[i * 3] = (float)points[i].X;
                pts[i * 3 + 1] = (float)points[i].Y;
                pts[i * 3 + 2] = points[i].PressureFactor;
            }

            list.Add(new RemoteLibraryStroke(
                ColorHex.ToRgbHex(stroke.DrawingAttributes.Color),
                stroke.DrawingAttributes.Width,
                pts,
                stroke.DrawingAttributes.IgnorePressure));
        }

        return list;
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);

    private static byte[] BuildThumbnail(string path)
    {
        PngInfo.TryReadSize(path, out var w, out var h);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (w >= h && w > ThumbEdge)
        {
            image.DecodePixelWidth = ThumbEdge;
        }
        else if (h > w && h > ThumbEdge)
        {
            image.DecodePixelHeight = ThumbEdge;
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] BuildScaledPng(string path, int w, int h)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (w >= h)
        {
            image.DecodePixelWidth = MaxOriginalEdge;
        }
        else
        {
            image.DecodePixelHeight = MaxOriginalEdge;
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public void Dispose()
    {
        _library.LibraryChanged -= OnLibraryChanged;
        _debounce.Dispose();
    }
}

internal static class PngInfo
{
    /// <summary>Reads width and height from the IHDR chunk without decoding the image.</summary>
    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
            return width > 0 && height > 0;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }
}
