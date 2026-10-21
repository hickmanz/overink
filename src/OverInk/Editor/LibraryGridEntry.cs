using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace OverInk.Editor;

/// <summary>One card in the full-library grid. Its thumbnail is only decoded once the card is about to be seen.</summary>
public sealed class LibraryGridEntry : INotifyPropertyChanged
{
    /// <summary>Decode height for grid thumbnails (cards show them about 112 px tall; the extra is for high-DPI).</summary>
    private const int ThumbnailHeight = 170;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _requested;
    private long _stamp = -1;
    private object? _thumbnail;
    private bool _isChecked;
    private bool _showCheck;

    public LibraryGridEntry(CaptureItem item)
    {
        Item = item;
    }

    public CaptureItem Item { get; private set; }

    /// <summary>
    /// Adopts the library's current description of this capture (its sidecar state can change after an edit) and
    /// re-reads the thumbnail the next time the card is in view if the image file changed.
    /// </summary>
    public void Update(CaptureItem item)
    {
        var changed = item.CreatedAt != Item.CreatedAt || item.SidecarBaseName != Item.SidecarBaseName;
        Item = item;
        if (changed)
        {
            OnPropertyChanged(string.Empty);
        }

        if (_requested && ThumbnailStore.StampOf(item.FlattenedPath) != _stamp)
        {
            _requested = false;
        }
    }

    public string Title => CaptureTrayEntry.TitleFor(Item);

    public string Subtitle => CaptureTrayEntry.SubtitleFor(Item);

    /// <summary>Lower-case text the search box matches against.</summary>
    public string SearchText => $"{Item.DisplayName} {Title} {Subtitle}".ToLowerInvariant();

    public object? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Ticked in select mode.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while the grid is in select mode, which shows the tick circle on every card.</summary>
    public bool ShowCheck
    {
        get => _showCheck;
        set
        {
            if (_showCheck == value)
            {
                return;
            }

            _showCheck = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Starts loading the thumbnail (once). Cheap to call repeatedly.</summary>
    public void EnsureThumbnail()
    {
        if (_requested)
        {
            return;
        }

        _requested = true;
        var path = Item.FlattenedPath;
        var stamp = ThumbnailStore.StampOf(path);
        _stamp = stamp;
        if (ThumbnailStore.TryGet(path, ThumbnailHeight, stamp) is { } cached)
        {
            Thumbnail = cached;
            return;
        }

        ThumbnailStore.LoadAsync(Item, ThumbnailHeight, stamp, _dispatcher, image => Thumbnail = image);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
