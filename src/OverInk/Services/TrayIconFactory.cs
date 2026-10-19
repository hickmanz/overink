using System.Drawing;
using System.IO;

namespace OverInk.Services;

public static class TrayIconFactory
{
    public static Icon Create()
    {
        if (TryLoadFileIcon(AppIconPaths.IcoPath, out var fileIcon))
        {
            return fileIcon;
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var associated = Icon.ExtractAssociatedIcon(exe);
                if (associated is not null)
                {
                    return (Icon)associated.Clone();
                }
            }
        }
        catch
        {
            // Fall through to drawn mascot.
        }

        return BrandIcons.CreateTrayIcon();
    }

    private static bool TryLoadFileIcon(string path, out Icon icon)
    {
        icon = null!;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var loaded = new Icon(path);
            icon = (Icon)loaded.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
