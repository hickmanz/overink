using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverInk.Editor;
using Xunit;

namespace OverInk.Tests;

public sealed class LibraryOrderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overink-order-" + Guid.NewGuid().ToString("N"));

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

    private static BitmapSource Tiny()
    {
        var pixels = new byte[4 * 4 * 4];
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        bitmap.Freeze();
        return bitmap;
    }

    private static List<CaptureItem> MakeCaptures(CaptureLibrary library, int count)
    {
        var made = new List<CaptureItem>();
        for (var i = 0; i < count; i++)
        {
            made.Add(library.CreateFromCapture(Tiny()));
            Thread.Sleep(25);
        }

        return made;
    }

    [Fact]
    public void Library_defaults_to_newest_first()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 4);

        Assert.Equal(made.AsEnumerable().Reverse().Select(i => i.Id), library.Enumerate().Select(i => i.Id));
    }

    [Fact]
    public void EnumerateRecent_returns_only_the_newest()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 5);

        var recent = library.EnumerateRecent(2);

        Assert.Equal(new[] { made[4].Id, made[3].Id }, recent.Select(i => i.Id));
        Assert.Empty(library.EnumerateRecent(0));
        Assert.Equal(5, library.EnumerateRecent(50).Count);
    }

    [Fact]
    public void MoveToFront_puts_captures_first_and_keeps_their_relative_order()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 5); // library order: 4 3 2 1 0

        library.MoveToFront(new[] { made[0], made[2] });

        // 2 was newer than 0, so 2 stays ahead of 0; both are ahead of the rest.
        Assert.Equal(
            new[] { made[2].Id, made[0].Id, made[4].Id, made[3].Id, made[1].Id },
            library.Enumerate().Select(i => i.Id));
    }

    [Fact]
    public void A_capture_taken_after_a_move_is_still_first()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 3);
        library.MoveToFront(new[] { made[0] });
        Thread.Sleep(25);

        var fresh = library.CreateFromCapture(Tiny());

        Assert.Equal(fresh.Id, library.Enumerate()[0].Id);
        Assert.Equal(made[0].Id, library.Enumerate()[1].Id);
    }

    [Fact]
    public void Order_survives_reopening_the_library()
    {
        var made = new List<CaptureItem>();
        using (var library = new CaptureLibrary(_folder))
        {
            made = MakeCaptures(library, 3);
            library.MoveToFront(new[] { made[0] });
        }

        using var reopened = new CaptureLibrary(_folder);
        Assert.Equal(made[0].Id, reopened.Enumerate()[0].Id);
    }

    [Fact]
    public void Rename_and_delete_keep_the_order_file_tidy()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 3);
        library.MoveToFront(new[] { made[0] });

        var renamed = library.Rename(made[0], "Renamed");
        Assert.NotNull(renamed);
        Assert.Equal("Renamed", library.Enumerate()[0].Id);

        library.Delete(renamed!);
        var json = File.ReadAllText(Path.Combine(_folder, CaptureLibrary.SidecarFolderName, "order.json"));
        Assert.DoesNotContain("Renamed", json);
        Assert.DoesNotContain(made[0].Id, json);
    }

    [Fact]
    public void A_damaged_order_file_falls_back_to_creation_order()
    {
        Directory.CreateDirectory(Path.Combine(_folder, CaptureLibrary.SidecarFolderName));
        File.WriteAllText(Path.Combine(_folder, CaptureLibrary.SidecarFolderName, "order.json"), "{ not json");

        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 2);

        Assert.Equal(made[1].Id, library.Enumerate()[0].Id);
    }

    [Fact]
    public void SetFrontOrder_applies_exactly_the_given_order_and_keeps_the_rest_behind()
    {
        using var library = new CaptureLibrary(_folder);
        var made = MakeCaptures(library, 5); // library order: 4 3 2 1 0

        // Drag card 1 to the very front, and card 4 to the end of the first four.
        library.SetFrontOrder(new[] { made[1], made[3], made[2], made[4] });

        Assert.Equal(
            new[] { made[1].Id, made[3].Id, made[2].Id, made[4].Id, made[0].Id },
            library.Enumerate().Select(i => i.Id));

        // And again, moving a card backwards past newer-created ones.
        library.SetFrontOrder(new[] { made[3], made[2], made[4], made[1] });
        Assert.Equal(
            new[] { made[3].Id, made[2].Id, made[4].Id, made[1].Id, made[0].Id },
            library.Enumerate().Select(i => i.Id));
    }
}