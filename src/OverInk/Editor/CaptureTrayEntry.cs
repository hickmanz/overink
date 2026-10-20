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

    private static readonly System.Text.RegularExpressions.Regex DefaultNamePattern =
        new(@"^Capture_\d{8}_\d{6}(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Friendly card title: the capture time for auto-named files, otherwise the user's name.</summary>
    public string Title => TitleFor(Item);

    public static string TitleFor(CaptureItem item) =>
        DefaultNamePattern.IsMatch(item.DisplayName) ? FriendlyTime(item.CreatedAt) : item.DisplayName;

    /// <summary>Secondary line: only shown for renamed captures (so the time is not lost).</summary>
    public string Subtitle => DefaultNamePattern.IsMatch(Item.DisplayName) ? string.Empty : FriendlyTime(Item.CreatedAt);

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

    public void RefreshThumbnail() => Thumbnail = Item.CreateThumbnail();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
