using System.IO;

namespace OverInk.Remote;

public static class RemoteDebugLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OverInk",
        "remote.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // ignore logging failures
            }
        }
    }

    public static string LogFilePath => LogPath;
}
