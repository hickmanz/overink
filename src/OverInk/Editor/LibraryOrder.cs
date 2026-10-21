using System.IO;
using System.Text.Json;

namespace OverInk.Editor;

/// <summary>
/// Which captures were pulled to the front of the library by hand. A capture sorts by its creation time unless
/// it was moved to the front later, in which case it sorts by that moment. The moments live in a small JSON
/// file in the library's sidecar folder so the image files' own dates are never touched.
/// </summary>
public sealed class LibraryOrder
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _movedAt = new(StringComparer.OrdinalIgnoreCase);

    public LibraryOrder(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>The time a capture is ordered by: its creation time, or when it was last moved to the front.</summary>
    public DateTime SortKey(string id, DateTime createdAt)
    {
        lock (_gate)
        {
            if (_movedAt.TryGetValue(id, out var ticks))
            {
                var moved = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                if (moved > createdAt)
                {
                    return moved;
                }
            }
        }

        return createdAt;
    }

    /// <summary>
    /// Puts the captures first, keeping the order given (first id ends up frontmost). <paramref name="existingIds"/>
    /// is every capture currently in the library; stamps for captures that are gone are dropped.
    /// </summary>
    public void MoveToFront(IReadOnlyList<string> idsFrontFirst, IEnumerable<string> existingIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            var existing = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _movedAt.Keys.Where(k => !existing.Contains(k)).ToList())
            {
                _movedAt.Remove(stale);
            }

            for (var i = 0; i < idsFrontFirst.Count; i++)
            {
                _movedAt[idsFrontFirst[i]] = nowUtc.Ticks - i;
            }

            Save();
        }
    }

    public void Rename(string oldId, string newId)
    {
        lock (_gate)
        {
            if (_movedAt.Remove(oldId, out var ticks))
            {
                _movedAt[newId] = ticks;
                Save();
            }
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_movedAt.Remove(id))
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_path));
            if (loaded is null)
            {
                return;
            }

            foreach (var (id, ticks) in loaded)
            {
                _movedAt[id] = ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file just means captures fall back to creation order.
        }
    }

    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_movedAt));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ordering is a convenience; failing to remember it must not break the library.
        }
    }
}
