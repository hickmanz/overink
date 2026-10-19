using System.IO;
using System.Windows.Ink;
using System.Windows.Media.Imaging;

namespace OverInk.Editor;

public sealed class CaptureLibrary : IDisposable
{
    public const string SidecarFolderName = ".overink";

    private readonly string _folder;
    private FileSystemWatcher? _watcher;

    public CaptureLibrary(string? customFolder = null)
    {
        _folder = string.IsNullOrWhiteSpace(customFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "OverInk")
            : customFolder;
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(SidecarDirectory);
        TryHideSidecarFolder();
    }

    public string FolderPath => _folder;

    private string SidecarDirectory => Path.Combine(_folder, SidecarFolderName);

    public event Action? LibraryChanged;

    public void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Created += (_, _) => RaiseChanged();
        _watcher.Deleted += (_, _) => RaiseChanged();
        _watcher.Renamed += (_, _) => RaiseChanged();
        _watcher.Changed += (_, e) =>
        {
            if (e.Name is null || e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RaiseChanged();
        };
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyList<CaptureItem> Enumerate()
    {
        if (!Directory.Exists(_folder))
        {
            return Array.Empty<CaptureItem>();
        }

        return Directory
            .EnumerateFiles(_folder, "*.png", SearchOption.TopDirectoryOnly)
            .Select(TryCreateItem)
            .Where(item => item is not null)
            .Cast<CaptureItem>()
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Resolves an id to an item that currently exists in the library. Only ids returned by
    /// <see cref="Enumerate"/> match, so path-like or unknown ids can never reach the file system.
    /// </summary>
    public CaptureItem? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Enumerate().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opaque version string that changes whenever the capture's image or ink is written (last write time
    /// of the newest backing file).
    /// </summary>
    public string GetRevision(CaptureItem item)
    {
        long ticks = 0;

        void Consider(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    ticks = Math.Max(ticks, File.GetLastWriteTimeUtc(path).Ticks);
                }
            }
            catch
            {
                // A file mid-replace; the next change event will correct the revision.
            }
        }

        Consider(item.FlattenedPath);
        if (item.HasEditableSidecars)
        {
            Consider(SidecarPath(item.SidecarBaseName!, ".orig.png"));
            Consider(SidecarPath(item.SidecarBaseName!, ".isf"));
        }

        return ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Loads just the untouched original image of a capture.</summary>
    public BitmapSource LoadOriginal(CaptureItem item) => LoadBitmap(OriginalPathFor(item));

    /// <summary>Loads just the saved ink of a capture (empty if it has none).</summary>
    public StrokeCollection LoadStrokes(CaptureItem item)
    {
        var strokesPath = StrokesPathFor(item);
        if (item.HasEditableSidecars && File.Exists(strokesPath))
        {
            using var stream = File.OpenRead(strokesPath);
            return new StrokeCollection(stream);
        }

        return new StrokeCollection();
    }

    /// <summary>
    /// Gives a plain PNG (one dropped into the folder, no sidecars) editable sidecars: the PNG itself becomes
    /// the original and the ink starts empty. Returns the item unchanged if it is already editable.
    /// </summary>
    public CaptureItem PromoteToEditable(CaptureItem item)
    {
        if (item.HasEditableSidecars)
        {
            return item;
        }

        var baseName = item.DisplayName;
        File.Copy(item.FlattenedPath, SidecarPath(baseName, ".orig.png"), overwrite: true);
        WriteStrokes(baseName, new StrokeCollection());
        return new CaptureItem
        {
            FlattenedPath = item.FlattenedPath,
            CreatedAt = item.CreatedAt,
            SidecarBaseName = baseName
        };
    }

    /// <summary>Writes new ink and the re-flattened PNG without touching the original image.</summary>
    public void SaveEdit(CaptureItem item, StrokeCollection strokes, BitmapSource flattened)
    {
        if (!item.HasEditableSidecars)
        {
            throw new InvalidOperationException("Capture has no editable sidecars.");
        }

        SaveBitmap(flattened, item.FlattenedPath);
        WriteStrokes(item.SidecarBaseName!, strokes);
    }

    /// <summary>
    /// Saves a new capture. <paramref name="capture"/> is the clean screen; <paramref name="ink"/> (image-pixel
    /// coordinates, e.g. what was drawn on the overlay) is stored as editable ink on top of it, and the
    /// flattened PNG shows both. Must run on the UI thread when ink is given (flattening renders WPF visuals).
    /// </summary>
    public CaptureItem CreateFromCapture(BitmapSource capture, StrokeCollection? ink = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var baseName = $"Capture_{stamp}";
        var flattenedPath = Path.Combine(_folder, $"{baseName}.png");
        for (var suffix = 2; File.Exists(flattenedPath); suffix++)
        {
            baseName = $"Capture_{stamp}_{suffix}";
            flattenedPath = Path.Combine(_folder, $"{baseName}.png");
        }

        ink ??= new StrokeCollection();
        var flattened = ink.Count == 0 ? capture : CaptureRenderer.Flatten(capture, ink);

        SaveBitmap(flattened, flattenedPath);
        WriteSidecarOriginal(baseName, capture);
        WriteStrokes(baseName, ink);

        return new CaptureItem
        {
            FlattenedPath = flattenedPath,
            CreatedAt = File.GetCreationTime(flattenedPath),
            SidecarBaseName = baseName
        };
    }

    public string OriginalPathFor(CaptureItem item) =>
        item.HasEditableSidecars
            ? SidecarPath(item.SidecarBaseName!, ".orig.png")
            : item.FlattenedPath;

    public string StrokesPathFor(CaptureItem item) =>
        item.HasEditableSidecars
            ? SidecarPath(item.SidecarBaseName!, ".isf")
            : string.Empty;

    public (BitmapSource Original, StrokeCollection Strokes) LoadDocument(CaptureItem item)
    {
        var origPath = OriginalPathFor(item);
        var original = LoadBitmap(origPath);

        StrokeCollection strokes;
        var strokesPath = StrokesPathFor(item);
        if (item.HasEditableSidecars && File.Exists(strokesPath))
        {
            using var stream = File.OpenRead(strokesPath);
            strokes = new StrokeCollection(stream);
        }
        else
        {
            strokes = new StrokeCollection();
        }

        return (original, strokes);
    }

    public void SaveDocument(
        CaptureItem item,
        BitmapSource original,
        StrokeCollection strokes,
        BitmapSource flattened)
    {
        SaveBitmap(flattened, item.FlattenedPath);

        if (item.HasEditableSidecars)
        {
            WriteSidecarOriginal(item.SidecarBaseName!, original);
            WriteStrokes(item.SidecarBaseName!, strokes);
        }
    }

    public CaptureItem? Rename(CaptureItem item, string newBaseName)
    {
        newBaseName = SanitizeBaseName(newBaseName);
        if (string.IsNullOrWhiteSpace(newBaseName))
        {
            return null;
        }

        var newFlattened = Path.Combine(_folder, $"{newBaseName}.png");
        if (File.Exists(newFlattened) &&
            !PathsEqual(newFlattened, item.FlattenedPath))
        {
            return null;
        }

        File.Move(item.FlattenedPath, newFlattened);

        string? newSidecar = item.SidecarBaseName;
        if (item.HasEditableSidecars)
        {
            var oldBase = item.SidecarBaseName!;
            MoveIfExists(SidecarPath(oldBase, ".orig.png"), SidecarPath(newBaseName, ".orig.png"));
            MoveIfExists(SidecarPath(oldBase, ".isf"), SidecarPath(newBaseName, ".isf"));
            newSidecar = newBaseName;
        }

        RaiseChanged();
        return new CaptureItem
        {
            FlattenedPath = newFlattened,
            CreatedAt = File.GetCreationTime(newFlattened),
            SidecarBaseName = newSidecar
        };
    }

    public void Delete(CaptureItem item)
    {
        if (File.Exists(item.FlattenedPath))
        {
            File.Delete(item.FlattenedPath);
        }

        if (item.HasEditableSidecars)
        {
            var orig = SidecarPath(item.SidecarBaseName!, ".orig.png");
            var strokes = SidecarPath(item.SidecarBaseName!, ".isf");
            if (File.Exists(orig))
            {
                File.Delete(orig);
            }

            if (File.Exists(strokes))
            {
                File.Delete(strokes);
            }
        }

        RaiseChanged();
    }

    private string SidecarPath(string sidecarBaseName, string suffix) =>
        Path.Combine(SidecarDirectory, $"{sidecarBaseName}{suffix}");

    private void WriteSidecarOriginal(string baseName, BitmapSource original) =>
        SaveBitmap(original, SidecarPath(baseName, ".orig.png"));

    private void WriteStrokes(string baseName, StrokeCollection strokes)
    {
        var path = SidecarPath(baseName, ".isf");
        using var stream = File.Create(path);
        strokes.Save(stream);
    }

    private CaptureItem? TryCreateItem(string flattenedPath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(flattenedPath);
            var hasSidecar = File.Exists(SidecarPath(name, ".orig.png"));

            return new CaptureItem
            {
                FlattenedPath = flattenedPath,
                CreatedAt = File.GetCreationTime(flattenedPath),
                SidecarBaseName = hasSidecar ? name : null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var temp = path + ".tmp";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(temp))
        {
            encoder.Save(stream);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temp, path);
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string SanitizeBaseName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    private static void MoveIfExists(string from, string to)
    {
        if (!File.Exists(from))
        {
            return;
        }

        if (File.Exists(to))
        {
            File.Delete(to);
        }

        File.Move(from, to);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void RaiseChanged() => LibraryChanged?.Invoke();

    private void TryHideSidecarFolder()
    {
        try
        {
            if (Directory.Exists(SidecarDirectory))
            {
                File.SetAttributes(SidecarDirectory, File.GetAttributes(SidecarDirectory) | FileAttributes.Hidden);
            }
        }
        catch
        {
            // Best-effort; library still works if hiding fails.
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
