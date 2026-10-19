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

    public CaptureItem CreateFromCapture(BitmapSource capture)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var baseName = $"Capture_{stamp}";
        var flattenedPath = Path.Combine(_folder, $"{baseName}.png");

        SaveBitmap(capture, flattenedPath);
        WriteSidecarOriginal(baseName, capture);
        WriteStrokes(baseName, new StrokeCollection());

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
