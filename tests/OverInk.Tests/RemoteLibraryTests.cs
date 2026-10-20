using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using OverInk.Editor;
using OverInk.Remote;
using Xunit;

namespace OverInk.Tests;

public sealed class RemoteLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overink-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static BitmapSource Solid(int w, int h, Color color)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA work failed", error);
        }

        return result;
    }

    // ------------------------------------------------------------ CaptureLibrary

    [Fact]
    public void TryGet_resolves_existing_ids_only()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));

        Assert.NotNull(library.TryGet(item.Id));
        Assert.Equal(item.FlattenedPath, library.TryGet(item.Id.ToUpperInvariant())!.FlattenedPath);

        foreach (var bad in new[] { null, "", "  ", "missing", @"..\outside", "../outside", @"..\..\Windows\win", item.Id + "/../x", item.Id + ".png" })
        {
            Assert.Null(library.TryGet(bad));
        }
    }

    [Fact]
    public void CreateFromCapture_never_overwrites_a_capture_taken_in_the_same_second()
    {
        using var library = new CaptureLibrary(_folder);
        var ids = Enumerable.Range(0, 6).Select(_ => library.CreateFromCapture(Solid(8, 8, Colors.White)).Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ids.Count, library.Enumerate().Count);
    }

    [Fact]
    public void GetRevision_changes_when_ink_is_saved()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));
        var before = library.GetRevision(item);

        Thread.Sleep(60);
        library.SaveEdit(item, new StrokeCollection(), Solid(40, 30, Colors.White));

        Assert.NotEqual(before, library.GetRevision(item));
    }

    [Fact]
    public void PromoteToEditable_gives_a_plain_png_sidecars()
    {
        using var library = new CaptureLibrary(_folder);
        var plain = Path.Combine(_folder, "dropped.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Solid(20, 10, Colors.Blue)));
        using (var stream = File.Create(plain))
        {
            encoder.Save(stream);
        }

        var item = library.TryGet("dropped")!;
        Assert.False(item.HasEditableSidecars);

        var editable = library.PromoteToEditable(item);

        Assert.True(editable.HasEditableSidecars);
        Assert.True(File.Exists(library.OriginalPathFor(editable)));
        Assert.True(library.TryGet("dropped")!.HasEditableSidecars);
        Assert.Empty(library.LoadStrokes(editable));
    }

    // ------------------------------------------------------------ ink conversion

    [Fact]
    public void Strokes_round_trip_through_dtos_and_isf()
    {
        var dtos = new List<RemoteLibraryStroke>
        {
            new("#E53935", 6, new float[] { 10, 20, 0.25f, 30, 40, 0.75f, 50, 60, 1f }, IgnorePressure: false),
            new("#1E88E5", 3, new float[] { 5, 5, 0.5f, 100, 5, 0.5f }, IgnorePressure: true)
        };

        var strokes = RemoteLibraryService.BuildStrokes(dtos);
        using var stream = new MemoryStream();
        strokes.Save(stream);
        stream.Position = 0;
        var loaded = RemoteLibraryService.ToDtos(new StrokeCollection(stream));

        Assert.Equal(2, loaded.Count);
        Assert.Equal("#E53935", loaded[0].Color);
        Assert.Equal(6, loaded[0].Size, 1);
        Assert.False(loaded[0].IgnorePressure);
        Assert.Equal(3, loaded[0].Pts.Length / 3);
        Assert.Equal(30, loaded[0].Pts[3], 1);
        Assert.Equal(40, loaded[0].Pts[4], 1);
        Assert.Equal(0.75f, loaded[0].Pts[5], 2);
        Assert.True(loaded[1].IgnorePressure);
        Assert.Equal("#1E88E5", loaded[1].Color);
    }

    [Fact]
    public void BuildStrokes_clamps_and_skips_bad_values()
    {
        var strokes = RemoteLibraryService.BuildStrokes(new[]
        {
            new RemoteLibraryStroke("not-a-color", 9999, new[] { 1f, 2f, 5f, float.NaN, 3f, 0.5f }, false),
            new RemoteLibraryStroke("#000000", 0.0, new[] { float.NaN, float.NaN, 0.5f }, false)
        });

        // The second stroke has no usable point and is dropped.
        var only = Assert.Single(strokes);
        Assert.Equal(200, only.DrawingAttributes.Width, 3);
        Assert.Single(only.StylusPoints);
        Assert.Equal(1f, only.StylusPoints[0].PressureFactor, 3);
    }

    [Fact]
    public void TryValidate_rejects_malformed_ink()
    {
        Assert.True(RemoteLibraryService.TryValidate(new List<RemoteLibraryStroke>(), out _));
        Assert.True(RemoteLibraryService.TryValidate(new[] { new RemoteLibraryStroke("#000", 3, new float[3], false) }, out _));

        Assert.False(RemoteLibraryService.TryValidate(new[] { new RemoteLibraryStroke("#000", 3, new float[4], false) }, out var problem));
        Assert.NotNull(problem);
        Assert.False(RemoteLibraryService.TryValidate(new[] { new RemoteLibraryStroke("#000", 3, Array.Empty<float>(), false) }, out _));
        Assert.False(RemoteLibraryService.TryValidate(new[] { new RemoteLibraryStroke("#000", double.NaN, new float[3], false) }, out _));
        Assert.False(RemoteLibraryService.TryValidate(
            Enumerable.Repeat(new RemoteLibraryStroke("#000", 3, new float[3], false), RemoteLibraryService.MaxStrokes + 1).ToList(),
            out _));
    }

    [Fact]
    public void Flatten_draws_ink_over_the_original()
    {
        var (inside, outside) = RunSta(() =>
        {
            var strokes = RemoteLibraryService.BuildStrokes(new[]
            {
                new RemoteLibraryStroke("#FF0000", 10, new float[] { 10, 50, 0.5f, 90, 50, 0.5f }, IgnorePressure: true)
            });
            var flat = CaptureRenderer.Flatten(Solid(100, 100, Colors.White), strokes);

            var pixel = new byte[4];
            flat.CopyPixels(new Int32Rect(50, 50, 1, 1), pixel, 4, 0);
            var inkPixel = (R: pixel[2], G: pixel[1], B: pixel[0]);
            flat.CopyPixels(new Int32Rect(50, 10, 1, 1), pixel, 4, 0);
            var bgPixel = (R: pixel[2], G: pixel[1], B: pixel[0]);
            return (inkPixel, bgPixel);
        });

        Assert.True(inside.R > 200 && inside.G < 60 && inside.B < 60, $"expected red ink, got {inside}");
        Assert.True(outside.R > 240 && outside.G > 240 && outside.B > 240, $"expected white, got {outside}");
    }

    [Fact]
    public void CreateFromCapture_with_ink_keeps_the_screen_clean_and_the_ink_editable()
    {
        using var library = new CaptureLibrary(_folder);
        var (item, loadedCount, original, flattened) = RunSta(() =>
        {
            var ink = RemoteLibraryService.BuildStrokes(new[]
            {
                new RemoteLibraryStroke("#FF0000", 10, new float[] { 10, 50, 0.5f, 90, 50, 0.5f }, IgnorePressure: true)
            });
            var created = library.CreateFromCapture(Solid(100, 100, Colors.White), ink);
            var strokes = library.LoadStrokes(created);

            Pixel Read(BitmapSource source)
            {
                var px = new byte[4];
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(50, 50, 1, 1), px, 4, 0);
                return new Pixel(px[2], px[1], px[0]);
            }

            return (created, strokes.Count, Read(library.LoadOriginal(created)), Read(LoadPng(created.FlattenedPath)));
        });

        Assert.Equal(1, loadedCount);
        Assert.True(original.R > 240 && original.G > 240 && original.B > 240, $"original must stay clean, got {original}");
        Assert.True(flattened.R > 200 && flattened.G < 60 && flattened.B < 60, $"flattened must show the ink, got {flattened}");
        Assert.True(item.HasEditableSidecars);
    }

    [Fact]
    public void Veil_is_stored_beside_the_capture_and_never_baked_into_the_original()
    {
        using var library = new CaptureLibrary(_folder);
        var result = RunSta(() =>
        {
            Pixel Read(BitmapSource source)
            {
                var px = new byte[4];
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(5, 5, 1, 1), px, 4, 0);
                return new Pixel(px[2], px[1], px[0]);
            }

            var created = library.CreateFromCapture(Solid(20, 20, Colors.Black), veil: 0.5);
            var veiled = Read(LoadPng(created.FlattenedPath));
            var original = Read(library.LoadOriginal(created));
            var storedVeil = library.LoadVeil(created);

            // Editing the veil re-flattens from the clean original, so it can go up, down or away.
            var lighter = CaptureRenderer.Flatten(library.LoadOriginal(created), library.LoadStrokes(created), 0.25);
            var cleared = CaptureRenderer.Flatten(library.LoadOriginal(created), library.LoadStrokes(created), 0);

            library.SaveDocument(created, library.LoadOriginal(created), library.LoadStrokes(created), cleared, 0);
            var afterClear = library.LoadVeil(created);

            return (veiled, original, storedVeil, Read(lighter), Read(cleared), afterClear);
        });

        Assert.InRange(result.veiled.R, 120, 135);
        Assert.Equal(0, result.original.R);
        Assert.Equal(0.5, result.storedVeil, 3);
        Assert.InRange(result.Item4.R, 60, 68);
        Assert.Equal(0, result.Item5.R);
        Assert.Equal(0, result.afterClear);
    }

    private readonly record struct Pixel(byte R, byte G, byte B);

    private static BitmapSource LoadPng(string path)
    {
        var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    [Fact]
    public void Overlay_shapes_convert_to_polyline_strokes() => RunSta(() =>
    {
        var red = new SolidColorBrush(Colors.Red);

        var rect = new System.Windows.Shapes.Rectangle { Width = 40, Height = 20, Stroke = red, StrokeThickness = 4 };
        System.Windows.Controls.Canvas.SetLeft(rect, 10);
        System.Windows.Controls.Canvas.SetTop(rect, 5);
        var rectStroke = Assert.Single(OverInk.Overlay.ShapeStrokeConverter.ToStrokes(rect));
        var rb = rectStroke.GetBounds();
        Assert.Equal(5, rectStroke.StylusPoints.Count);
        Assert.True(rb.Left <= 10 && rb.Right >= 50 && rb.Top <= 5 && rb.Bottom >= 25, $"rect bounds {rb}");
        Assert.Equal(4, rectStroke.DrawingAttributes.Width);
        Assert.Equal(Colors.Red, rectStroke.DrawingAttributes.Color);

        var ellipse = new System.Windows.Shapes.Ellipse { Width = 30, Height = 30, Stroke = red, StrokeThickness = 2 };
        var ellipseStroke = Assert.Single(OverInk.Overlay.ShapeStrokeConverter.ToStrokes(ellipse));
        Assert.True(ellipseStroke.StylusPoints.Count > 40);

        var arrow = OverInk.Overlay.ShapeDrawingHelper.CreateShapeElement(
            OverInk.Models.SketchTool.Arrow, new System.Windows.Point(0, 0), new System.Windows.Point(100, 0), Colors.Blue, 3);
        var arrowStrokes = OverInk.Overlay.ShapeStrokeConverter.ToStrokes(arrow).ToList();
        Assert.Equal(2, arrowStrokes.Count);
        Assert.Equal(2, arrowStrokes[0].StylusPoints.Count);
        Assert.Equal(Colors.Blue, arrowStrokes[0].DrawingAttributes.Color);
        return true;
    });

    // ------------------------------------------------------------ tokens

    [Fact]
    public void Tokens_are_per_connection_and_one_per_device()
    {
        using var library = new CaptureLibrary(_folder);
        using var service = new RemoteLibraryService(library, () => true);

        Assert.False(service.IsValidToken(null));
        Assert.False(service.IsValidToken("nope"));

        var first = service.IssueToken("device-1", "client-a");
        Assert.True(service.IsValidToken(first));

        // A newer connection from the same device replaces the old token.
        var second = service.IssueToken("device-1", "client-b");
        Assert.NotEqual(first, second);
        Assert.False(service.IsValidToken(first));
        Assert.True(service.IsValidToken(second));

        // Revoking the old client (its socket closing late) must not kill the new token.
        service.RevokeClient("client-a");
        Assert.True(service.IsValidToken(second));

        service.RevokeClient("client-b");
        Assert.False(service.IsValidToken(second));

        var third = service.IssueToken("device-2", "client-c");
        service.RevokeAll();
        Assert.False(service.IsValidToken(third));
    }

    // ------------------------------------------------------------ HTTP

    private static async Task<WebApplication> StartAsync(RemoteLibraryService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        service.Map(app);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Http_endpoints_require_a_valid_token()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));
        var enabled = true;
        using var service = new RemoteLibraryService(library, () => enabled);
        await using var app = await StartAsync(service);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/lib")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/lib?k=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/lib/{item.Id}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/lib/{item.Id}/original?k=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.DeleteAsync($"/lib/{item.Id}?k=wrong")).StatusCode);
        Assert.True(library.Enumerate().Count == 1, "an unauthorized delete must not remove the capture");

        var token = service.IssueToken("device", "client");
        var list = await http.GetAsync($"/lib?k={token}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(item.Id, entry.GetProperty("id").GetString());
        Assert.Equal(40, entry.GetProperty("w").GetInt32());
        Assert.Equal(30, entry.GetProperty("h").GetInt32());
        Assert.True(entry.GetProperty("editable").GetBoolean());

        var thumb = await http.GetAsync($"/lib/{item.Id}/thumb?k={token}");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0xFF, (await thumb.Content.ReadAsByteArrayAsync())[0]);

        var original = await http.GetAsync($"/lib/{item.Id}/original?k={token}");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);

        // The library can be switched off from the PC without restarting the server.
        enabled = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/lib?k={token}")).StatusCode);
    }

    [Fact]
    public async Task Http_unknown_and_path_like_ids_are_not_found()
    {
        using var library = new CaptureLibrary(_folder);
        library.CreateFromCapture(Solid(40, 30, Colors.White));
        using var service = new RemoteLibraryService(library, () => true);
        await using var app = await StartAsync(service);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var token = service.IssueToken("device", "client");

        foreach (var id in new[] { "missing", "..%2F..%2Fwindows%2Fwin", "..%5C..%5Cwindows%5Cwin", "Capture_x%2F..%2F.." })
        {
            foreach (var route in new[] { "thumb", "original", "strokes" })
            {
                var response = await http.GetAsync($"/lib/{id}/{route}?k={token}");
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{id}/{route} -> {(int)response.StatusCode}");
            }

            Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/lib/{id}?k={token}")).StatusCode);
        }
    }

    [Fact]
    public async Task Http_save_with_a_stale_revision_is_a_conflict_and_leaves_the_capture_untouched()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));
        var currentRev = library.GetRevision(item);
        using var service = new RemoteLibraryService(library, () => true);
        await using var app = await StartAsync(service);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var token = service.IssueToken("device", "client");

        var stale = await http.PostAsJsonAsync(
            $"/lib/{item.Id}/strokes?k={token}",
            new { baseRev = "1", strokes = Array.Empty<object>() },
            RemoteJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(currentRev, body.RootElement.GetProperty("rev").GetString());
        Assert.Equal(currentRev, library.GetRevision(item));

        var unauthorized = await http.PostAsJsonAsync(
            $"/lib/{item.Id}/strokes?k=wrong",
            new { baseRev = currentRev, strokes = Array.Empty<object>() },
            RemoteJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public void Veil_follows_a_rename_is_kept_by_saves_that_omit_it_and_goes_with_delete()
    {
        using var library = new CaptureLibrary(_folder);
        var (renamed, veilAfterRename, veilAfterOmit, veilAfterSet) = RunSta(() =>
        {
            var created = library.CreateFromCapture(Solid(20, 20, Colors.Black), veil: 0.4);
            var moved = library.Rename(created, "veiled")!;
            var afterRename = library.LoadVeil(moved);

            // The tablet's save path: no veil given means the stored one stays.
            library.SaveEdit(moved, new StrokeCollection(), Solid(20, 20, Colors.Black));
            var afterOmit = library.LoadVeil(moved);

            library.SaveEdit(moved, new StrokeCollection(), Solid(20, 20, Colors.Black), 0.8);
            return (moved, afterRename, afterOmit, library.LoadVeil(moved));
        });

        Assert.Equal(0.4, veilAfterRename, 3);
        Assert.Equal(0.4, veilAfterOmit, 3);
        Assert.Equal(0.8, veilAfterSet, 3);

        library.Delete(renamed);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.veil", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Http_save_rejects_malformed_ink()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));
        var rev = library.GetRevision(item);
        using var service = new RemoteLibraryService(library, () => true);
        await using var app = await StartAsync(service);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var token = service.IssueToken("device", "client");

        var badPoints = await http.PostAsJsonAsync(
            $"/lib/{item.Id}/strokes?k={token}",
            new { baseRev = rev, strokes = new[] { new { color = "#000000", size = 3, pts = new[] { 1f, 2f }, ignorePressure = false } } },
            RemoteJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, badPoints.StatusCode);

        var notJson = await http.PostAsync(
            $"/lib/{item.Id}/strokes?k={token}",
            new StringContent("{ nope", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
    }

    [Fact]
    public async Task Http_listing_reports_the_saved_revision_and_image_size()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(200, 100, Colors.White));
        var strokes = RemoteLibraryService.BuildStrokes(new[]
        {
            new RemoteLibraryStroke("#43A047", 8, new float[] { 20, 30, 0.5f, 180, 70, 0.5f }, IgnorePressure: true)
        });
        Thread.Sleep(60);
        library.SaveEdit(item, strokes, Solid(200, 100, Colors.White));
        var rev = library.GetRevision(item);

        using var service = new RemoteLibraryService(library, () => true);
        await using var app = await StartAsync(service);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var token = service.IssueToken("device", "client");

        // The strokes endpoint marshals to the WPF dispatcher, which a test host does not have; the pure ink
        // conversion is covered above, so this only checks that the listing reflects the saved state.
        var list = await http.GetFromJsonAsync<JsonElement>($"/lib?k={token}");
        var entry = list.EnumerateArray().Single();
        Assert.Equal(rev, entry.GetProperty("rev").GetString());
        Assert.Equal(200, entry.GetProperty("w").GetInt32());
        Assert.Equal(100, entry.GetProperty("h").GetInt32());
    }

    // ------------------------------------------------------------ change notifications

    [Fact]
    public async Task Changes_are_reported_once_and_own_saves_do_not_echo()
    {
        using var library = new CaptureLibrary(_folder);
        var item = library.CreateFromCapture(Solid(40, 30, Colors.White));
        using var service = new RemoteLibraryService(library, () => true);

        var events = new List<(bool ListChanged, string[] Ids)>();
        var signal = new SemaphoreSlim(0);
        service.Changed += (listChanged, changed) =>
        {
            lock (events)
            {
                events.Add((listChanged, changed.Select(c => c.Id).ToArray()));
            }

            signal.Release();
        };

        library.StartWatching();

        // An ink save made elsewhere (the PC editor) is reported as a change to that capture.
        Thread.Sleep(60);
        library.SaveEdit(item, new StrokeCollection(), Solid(40, 30, Colors.White));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "no change event after SaveEdit");
        lock (events)
        {
            Assert.Contains(events, e => e.Ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase));
        }

        // A new capture changes the list.
        library.CreateFromCapture(Solid(8, 8, Colors.White));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "no change event after a new capture");
        lock (events)
        {
            Assert.Contains(events, e => e.ListChanged);
        }
    }
}
