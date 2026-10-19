using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OverInk.Editor;

public sealed class CaptureTrayEntry : INotifyPropertyChanged
{
    public CaptureTrayEntry(CaptureItem item)
    {
        Item = item;
        RefreshThumbnail();
    }

    public CaptureItem Item { get; set; }

    public string Label => Item.DisplayName;

    public string TimeLabel => Item.CreatedAt.ToString("g");

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

    public void RefreshThumbnail() => Thumbnail = Item.CreateThumbnail();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
