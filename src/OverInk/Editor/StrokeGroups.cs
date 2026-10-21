using System.Windows.Ink;

namespace OverInk.Editor;

/// <summary>
/// Marks strokes that belong to one drawn entity (e.g. an arrow's shaft and head) with a shared id, stored as ISF
/// property data so it survives the <c>.isf</c> sidecar. Strokes without a group are their own entity.
/// </summary>
public static class StrokeGroups
{
    public static readonly Guid PropertyId = new("6f0c5d1e-3b7a-4c2e-9a41-5d8e2b7f1c90");

    public static string? Get(Stroke stroke)
    {
        if (!stroke.ContainsPropertyData(PropertyId))
        {
            return null;
        }

        return stroke.GetPropertyData(PropertyId) as string;
    }

    public static void Set(Stroke stroke, string? group)
    {
        if (string.IsNullOrEmpty(group))
        {
            if (stroke.ContainsPropertyData(PropertyId))
            {
                stroke.RemovePropertyData(PropertyId);
            }

            return;
        }

        stroke.AddPropertyData(PropertyId, group);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Copies the group (if any) from one stroke to another, e.g. after cloning or splitting.</summary>
    public static void Copy(Stroke from, Stroke to) => Set(to, Get(from));
}
