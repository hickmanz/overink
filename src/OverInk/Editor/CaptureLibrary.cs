using System.IO;
using System.Windows.Ink;
using System.Windows.Media.Imaging;

namespace OverInk.Editor;

public sealed class CaptureLibrary : IDisposable
{
    public const string SidecarFolderName = ".overink";

    /// <summary>Upper limit for a capture's veil; past this the screenshot is mostly washed out.</summary>
    public const double MaxVeil = 0.9;

    private readonly string _folder;
    private FileSystemWatcher? _watcher;
    private readonly LibraryOrder _order;

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
        _order = new LibraryOrder(Path.Combine(SidecarDirectory, "order.json"));
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
        // Our own saves write "x.png.tmp" and then swap it in; only the final swap is worth reporting.
        _watcher.Created += (_, e) =>
        {
            if (!IsTempFile(e.Name))
            {
                RaiseChanged();
            }
        };
        _watcher.Deleted += (_, e) =>
        {
            if (!IsTempFile(e.Name))
            {
                RaiseChanged();
            }
        };
        _watcher.Renamed += (_, _) => RaiseChanged();
        _watcher.Changed += (_, e) =>
        {
            if (e.Name is null || IsTempFile(e.Name))
            {
                return;
            }

            RaiseChanged();
        };
        _watcher.EnableRaisingEvents = true;
    }

    private static bool IsTempFile(string? name) =>
        name is not null && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

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
            .OrderByDescending(item => _order.SortKey(item.Id, item.CreatedAt))
            .ToList();
    }

    /// <summary>The newest <paramref name="limit"/> captures, in library order (what the editor's library bar shows).</summary>
    public IReadOnlyList<CaptureItem> EnumerateRecent(int limit) => Enumerate().Take(Math.Max(0, limit)).ToList();

    /// <summary>
    /// Fixes the order of the first captures in the library: <paramref name="itemsFrontFirst"/> becomes the new
    /// front of the library, in exactly that order (used when cards are dragged around in the library bar).
    /// Captures not listed keep their place after them.
    /// </summary>
    public void SetFrontOrder(IReadOnlyList<CaptureItem> itemsFrontFirst)
    {
        if (itemsFrontFirst.Count == 0)
        {
            return;
        }

        _order.MoveToFront(itemsFrontFirst.Select(i => i.Id).ToList(), Enumerate().Select(i => i.Id), DateTime.UtcNow);
        RaiseChanged();
    }
    /// <summary>
    /// Pulls captures to the front of the library, keeping their current order relative to each other. Their
    /// files are not touched; the new position is remembered in the library's own order file.
    /// </summary>
    public void MoveToFront(IEnumerable<CaptureItem> items)
    {
        var wanted = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        var all = Enumerate();
        var ids = all.Where(i => wanted.Contains(i.Id)).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        _order.MoveToFront(ids, all.Select(i => i.Id), DateTime.UtcNow);
        RaiseChanged();
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
            Consider(SidecarPath(item.SidecarBaseName!, ".veil"));
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
    /// The white-veil opacity (0–<see cref="MaxVeil"/>) stored with a capture; 0 when it has none. Kept as
    /// a sidecar so the veil stays adjustable instead of being baked into the original screenshot.
    /// </summary>
    public double LoadVeil(CaptureItem item)
    {
        if (!item.HasEditableSidecars)
        {
            return 0;
        }

        try
        {
            var path = SidecarPath(item.SidecarBaseName!, ".veil");
            if (File.Exists(path) &&
                double.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var veil))
            {
                return Math.Clamp(veil, 0, MaxVeil);
            }
        }
        catch (IOException)
        {
            // Mid-write by another client; treat as no veil this time.
        }

        return 0;
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
    public void SaveEdit(CaptureItem item, StrokeCollection strokes, BitmapSource flattened, double? veil = null)
    {
        if (!item.HasEditableSidecars)
        {
            throw new InvalidOperationException("Capture has no editable sidecars.");
        }

        SaveBitmap(flattened, item.FlattenedPath);
        WriteStrokes(item.SidecarBaseName!, strokes);
        if (veil is { } value)
        {
            WriteVeil(item.SidecarBaseName!, value);
        }
    }

    /// <summary>
    /// Saves a new capture. <paramref name="capture"/> is the clean screen; <paramref name="ink"/> (image-pixel
    /// coordinates, e.g. what was drawn on the overlay) is stored as editable ink on top of it, and the
    /// flattened PNG shows both. Must run on the UI thread when ink is given (flattening renders WPF visuals).
    /// </summary>
    public CaptureItem CreateFromCapture(BitmapSource capture, StrokeCollection? ink = null, double veil = 0) =>
        CreateFromCapture(capture, ink, veil, out _);

    /// <summary>
    /// Same as <see cref="CreateFromCapture(BitmapSource, StrokeCollection?, double)"/>, and also hands back the
    /// flattened image so the caller (e.g. the clipboard copy) doesn't have to render it a second time.
    /// </summary>
    public CaptureItem CreateFromCapture(
        BitmapSource capture, StrokeCollection? ink, double veil, out BitmapSource flattened)
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
        veil = Math.Clamp(veil, 0, MaxVeil);
        var plain = ink.Count == 0 && veil <= 0;
        flattened = plain ? capture : CaptureRenderer.Flatten(capture, ink, veil);

        var originalPath = SidecarPath(baseName, ".orig.png");
        if (plain)
        {
            // The flattened image *is* the original: encode once and copy, instead of encoding the same pixels twice.
            SaveBitmap(flattened, flattenedPath);
            File.Copy(flattenedPath, originalPath, overwrite: true);
        }
        else
        {
            var shown = flattened;
            RunBoth(
                () => SaveBitmap(shown, flattenedPath),
                () => WriteSidecarOriginal(baseName, capture),
                shown,
                capture);
        }

        WriteStrokes(baseName, ink);
        WriteVeil(baseName, veil);

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
        BitmapSource flattened,
        double veil = 0,
        bool originalChanged = true)
    {
        // The original only changes when the capture is cropped (or first promoted); re-encoding it on every
        // save was about half of the cost of a save. It is still written if its file is missing.
        var writeOriginal = item.HasEditableSidecars &&
            (originalChanged || !File.Exists(SidecarPath(item.SidecarBaseName!, ".orig.png")));
        if (writeOriginal)
        {
            var baseName = item.SidecarBaseName!;
            RunBoth(
                () => SaveBitmap(flattened, item.FlattenedPath),
                () => WriteSidecarOriginal(baseName, original),
                flattened,
                original);
        }
        else
        {
            SaveBitmap(flattened, item.FlattenedPath);
        }

        if (item.HasEditableSidecars)
        {
            WriteStrokes(item.SidecarBaseName!, strokes);
            WriteVeil(item.SidecarBaseName!, veil);
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
        _order.Rename(item.Id, newBaseName);

        string? newSidecar = item.SidecarBaseName;
        if (item.HasEditableSidecars)
        {
            var oldBase = item.SidecarBaseName!;
            MoveIfExists(SidecarPath(oldBase, ".orig.png"), SidecarPath(newBaseName, ".orig.png"));
            MoveIfExists(SidecarPath(oldBase, ".isf"), SidecarPath(newBaseName, ".isf"));
            MoveIfExists(SidecarPath(oldBase, ".veil"), SidecarPath(newBaseName, ".veil"));
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

    /// <summary>Matches the " copy" / " copy 3" tail that duplicates get, so a copy of a copy doesn't pile them up.</summary>
    private static readonly System.Text.RegularExpressions.Regex CopySuffix =
        new(@" copy( \d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Copies a capture (flattened image, clean original, ink and veil) under a new name: "name copy", then
    /// "name copy 2" and so on. The copy is stamped as new, so it lists first. Returns null if copying failed
    /// (nothing is left behind in that case). This only copies files, so it can run on any thread.
    /// </summary>
    public CaptureItem? Duplicate(CaptureItem item)
    {
        if (!File.Exists(item.FlattenedPath))
        {
            return null;
        }

        var baseName = UniqueCopyName(item.DisplayName);
        var newFlattened = Path.Combine(_folder, $"{baseName}.png");
        var made = new List<string>();
        try
        {
            File.Copy(item.FlattenedPath, newFlattened, overwrite: false);
            made.Add(newFlattened);

            if (item.HasEditableSidecars)
            {
                foreach (var suffix in new[] { ".orig.png", ".isf", ".veil" })
                {
                    var from = SidecarPath(item.SidecarBaseName!, suffix);
                    if (!File.Exists(from))
                    {
                        continue;
                    }

                    var to = SidecarPath(baseName, suffix);
                    File.Copy(from, to, overwrite: true);
                    made.Add(to);
                }
            }

            // A copy keeps the source's timestamps; make it new so it sorts to the top of the library.
            File.SetCreationTime(newFlattened, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var path in made)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // best effort
                }
            }

            return null;
        }

        RaiseChanged();
        return new CaptureItem
        {
            FlattenedPath = newFlattened,
            CreatedAt = File.GetCreationTime(newFlattened),
            SidecarBaseName = item.HasEditableSidecars ? baseName : null
        };
    }

    private string UniqueCopyName(string name)
    {
        var stem = CopySuffix.Replace(name, string.Empty);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = name;
        }

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem} copy" : $"{stem} copy {n}";
            if (!File.Exists(Path.Combine(_folder, $"{candidate}.png")))
            {
                return candidate;
            }
        }
    }

    public void Delete(CaptureItem item)
    {
        _order.Remove(item.Id);
        if (File.Exists(item.FlattenedPath))
        {
            File.Delete(item.FlattenedPath);
        }

        if (item.HasEditableSidecars)
        {
            var orig = SidecarPath(item.SidecarBaseName!, ".orig.png");
            var strokes = SidecarPath(item.SidecarBaseName!, ".isf");
            var veil = SidecarPath(item.SidecarBaseName!, ".veil");
            if (File.Exists(veil))
            {
                File.Delete(veil);
            }

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

    private void WriteVeil(string baseName, double veil)
    {
        var path = SidecarPath(baseName, ".veil");
        veil = Math.Clamp(veil, 0, MaxVeil);
        if (veil < 0.005)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllText(path, veil.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

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

    /// <summary>
    /// Runs two PNG encodes side by side (they dominate the cost of a save) when both images can be used from
    /// another thread, i.e. are frozen; otherwise one after the other.
    /// </summary>
    private static void RunBoth(Action first, Action second, BitmapSource a, BitmapSource b)
    {
        static bool Shareable(BitmapSource s)
        {
            if (!s.IsFrozen && s.CanFreeze)
            {
                s.Freeze();
            }

            return s.IsFrozen;
        }

        if (Shareable(a) && Shareable(b))
        {
            Parallel.Invoke(first, second);
        }
        else
        {
            first();
            second();
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
