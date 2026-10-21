using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OverInk.Editor;

/// <summary>
/// Decoded library thumbnails, kept while the image file is unchanged (same write time and size). Decoding a
/// full-size PNG is the slow part of listing the library, so each is decoded once, on a worker thread.
/// </summary>
internal static class ThumbnailStore
{
    private const int MaxEntries = 600;

    private static readonly ConcurrentDictionary<string, (long Stamp, BitmapImage Image)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes whenever the file's content or size does; 0 when the file can't be read.</summary>
    public static long StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static BitmapImage? TryGet(string path, int height, long stamp) =>
        stamp != 0 && Cache.TryGetValue(Key(path, height), out var hit) && hit.Stamp == stamp ? hit.Image : null;

    /// <summary>
    /// Decodes on a worker thread and hands the result to <paramref name="done"/> on <paramref name="dispatcher"/>.
    /// A file caught mid-write is skipped; the change that finishes the write triggers another refresh.
    /// </summary>
    public static void LoadAsync(CaptureItem item, int height, long stamp, Dispatcher dispatcher, Action<BitmapImage> done)
    {
        Task.Run(() =>
        {
            try
            {
                var image = item.CreateThumbnail(height);
                if (Cache.Count > MaxEntries)
                {
                    Cache.Clear();
                }

                Cache[Key(item.FlattenedPath, height)] = (stamp, image);
                dispatcher.BeginInvoke(() => done(image));
            }
            catch
            {
                // Mid-write or gone; see above.
            }
        });
    }

    private static string Key(string path, int height) => $"{height}|{path}";
}
