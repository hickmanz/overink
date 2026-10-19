using System.IO;
using System.Windows.Media.Imaging;

namespace OverInk.Editor;

public sealed class CaptureItem
{
    public required string FlattenedPath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? SidecarBaseName { get; init; }

    public string DisplayName => Path.GetFileNameWithoutExtension(FlattenedPath);

    public bool HasEditableSidecars => SidecarBaseName is not null;

    public BitmapImage CreateThumbnail(int decodeHeight = 120)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.DecodePixelHeight = decodeHeight;
        image.UriSource = new Uri(FlattenedPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
