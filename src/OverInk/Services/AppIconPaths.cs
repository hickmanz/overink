using System.IO;

namespace OverInk.Services;

public static class AppIconPaths
{
    public const string IcoFileName = "app.ico";
    public const string PngFileName = "app.png";

    public static string AssetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string IcoPath => Path.Combine(AssetsDirectory, IcoFileName);

    public static string PngPath => Path.Combine(AssetsDirectory, PngFileName);
}
