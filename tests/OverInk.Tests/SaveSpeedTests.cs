using System.IO;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverInk.Editor;
using Xunit;

namespace OverInk.Tests;

public sealed class SaveSpeedTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overink-speed-" + Guid.NewGuid().ToString("N"));

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

    private static BitmapSource Solid(int w, int h, Color color, bool freeze = true)
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
        if (freeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static Stroke Line()
    {
        var points = new StylusPointCollection { new StylusPoint(2, 2), new StylusPoint(20, 20) };
        return new Stroke(points);
    }

    [Fact]
    public void SaveDocument_skips_the_original_when_it_did_not_change()
    {
        using var library = new CaptureLibrary(_folder);
        var original = Solid(40, 30, Colors.White);
        var item = library.CreateFromCapture(original);
        var originalPath = library.OriginalPathFor(item);
        var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(originalPath, stamp);

        library.SaveDocument(item, original, new StrokeCollection { Line() }, Solid(40, 30, Colors.Red), 0, originalChanged: false);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(originalPath));

        library.SaveDocument(item, original, new StrokeCollection(), Solid(40, 30, Colors.Red), 0, originalChanged: true);
        Assert.NotEqual(stamp, File.GetLastWriteTimeUtc(originalPath));
    }

    [Fact]
    public void SaveDocument_restores_a_missing_original_even_when_unchanged()
    {
        using var library = new CaptureLibrary(_folder);
        var original = Solid(40, 30, Colors.White);
        var item = library.CreateFromCapture(original);
        File.Delete(library.OriginalPathFor(item));

        library.SaveDocument(item, original, new StrokeCollection(), original, 0, originalChanged: false);

        Assert.True(File.Exists(library.OriginalPathFor(item)));
    }

    [Fact]
    public async Task SaveDocument_can_run_on_a_worker_thread_with_cloned_ink()
    {
        using var library = new CaptureLibrary(_folder);
        var original = Solid(40, 30, Colors.White);
        var item = library.CreateFromCapture(original);

        var ink = new StrokeCollection { Line() };
        StrokeGroups.Set(ink[0], "grp");
        var copy = ink.Clone();
        var flattened = Solid(40, 30, Colors.Blue);

        await Task.Run(() => library.SaveDocument(item, original, copy, flattened, 0.25, originalChanged: false));

        var (_, loaded) = library.LoadDocument(item);
        Assert.Single(loaded);
        Assert.Equal("grp", StrokeGroups.Get(loaded[0]));
        Assert.Equal(0.25, library.LoadVeil(item), 3);
    }

    [Fact]
    public void CreateFromCapture_without_ink_writes_identical_flattened_and_original()
    {
        using var library = new CaptureLibrary(_folder);
        var capture = Solid(40, 30, Colors.Green);

        var item = library.CreateFromCapture(capture, null, 0, out var flattened);

        Assert.Same(capture, flattened);
        Assert.Equal(File.ReadAllBytes(item.FlattenedPath), File.ReadAllBytes(library.OriginalPathFor(item)));
    }

    [Fact]
    public void CreateFromCapture_with_ink_handles_a_capture_that_is_not_frozen()
    {
        using var library = new CaptureLibrary(_folder);
        var capture = Solid(40, 30, Colors.Green, freeze: false);
        var ink = new StrokeCollection { Line() };

        var item = library.CreateFromCapture(capture, ink, 0.2, out var flattened);

        Assert.NotSame(capture, flattened);
        Assert.True(File.Exists(item.FlattenedPath));
        Assert.True(File.Exists(library.OriginalPathFor(item)));
    }
}
