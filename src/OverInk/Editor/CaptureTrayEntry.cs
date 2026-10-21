using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace OverInk.Editor;

public sealed class CaptureTrayEntry : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private const int TrayThumbnailHeight = 120;

    private long _shownStamp = -1;
    private int _loadGeneration;

    public CaptureTrayEntry(CaptureItem item)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Item = item;
        RefreshThumbnail();
    }

    public CaptureItem Item { get; private set; }

    /// <summary>Adopts a fresh description of the same capture (e.g. after a rename or sidecar change).</summary>
    public void Update(CaptureItem item)
    {
        if (string.Equals(item.FlattenedPath, Item.FlattenedPath, StringComparison.OrdinalIgnoreCase) &&
            item.CreatedAt == Item.CreatedAt &&
            item.SidecarBaseName == Item.SidecarBaseName)
        {
            return;
        }

        Item = item;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public string Label => Item.DisplayName;

    public string TimeLabel => Item.CreatedAt.ToString("g");

    private static readonly System.Text.RegularExpressions.Regex DefaultNamePattern =
        new(@"^Capture_\d{8}_\d{6}(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Friendly card title: the capture time for auto-named files, otherwise the user's name.</summary>
    public string Title => TitleFor(Item);

    public static string TitleFor(CaptureItem item) =>
        DefaultNamePattern.IsMatch(item.DisplayName) ? FriendlyTime(item.CreatedAt) : item.DisplayName;

    /// <summary>Secondary line: only shown for renamed captures (so the time is not lost).</summary>
    public string Subtitle => SubtitleFor(Item);

    public static string SubtitleFor(CaptureItem item) =>
        DefaultNamePattern.IsMatch(item.DisplayName) ? string.Empty : FriendlyTime(item.CreatedAt);

    private static string FriendlyTime(DateTime time)
    {
        var now = DateTime.Now;
        var clock = time.ToString("h:mm tt");
        if (time.Date == now.Date)
        {
            return $"Today, {clock}";
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return $"Yesterday, {clock}";
        }

        return time.Year == now.Year ? $"{time:MMM d}, {clock}" : $"{time:MMM d, yyyy}, {clock}";
    }

    private object? _thumbnail;
    public object? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Makes the thumbnail match the file on disk. Cheap when nothing changed; otherwise the image is decoded on a
    /// worker thread so the editor never stalls on it (the old thumbnail stays up meanwhile).
    /// </summary>
    public void RefreshThumbnail()
    {
        var path = Item.FlattenedPath;
        var stamp = ThumbnailStore.StampOf(path);
        if (stamp == _shownStamp && _thumbnail is not null)
        {
            return;
        }

        if (ThumbnailStore.TryGet(path, TrayThumbnailHeight, stamp) is { } cached)
        {
            _shownStamp = stamp;
            Thumbnail = cached;
            return;
        }

        var generation = ++_loadGeneration;
        ThumbnailStore.LoadAsync(Item, TrayThumbnailHeight, stamp, _dispatcher, image =>
        {
            if (generation == _loadGeneration)
            {
                _shownStamp = stamp;
                Thumbnail = image;
            }
        });
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
