using System.Drawing;

namespace OverInk.Services;

public static class TrayIconFactory
{
    public static Icon Create() => BrandIcons.CreateTrayIcon();
}
