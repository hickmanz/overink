using System.IO;
using Microsoft.Win32;

namespace OverInk.Services;

/// <summary>
/// An in-place update is only safe for the copy the Inno Setup installer registered.
/// A portable unzip has no install directory for that setup exe to replace.
/// </summary>
public static class InstallDetector
{
    public const string UninstallKeyName = "{15B47256-D1E1-4A5D-AA7A-7215E2C445A1}_is1";

    public static bool IsRunningInstalledCopy(string? processPath = null)
    {
        processPath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var installLocation = FindInstallLocation();
        if (installLocation == null)
        {
            return false;
        }

        var runningDir = Path.GetDirectoryName(processPath);
        if (runningDir == null)
        {
            return false;
        }

        return string.Equals(NormalizeDir(runningDir), NormalizeDir(installLocation), StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindInstallLocation()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}");
                    if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    {
                        return location;
                    }
                }
                catch (System.Security.SecurityException)
                {
                    // A locked-down account can hide HKLM. Per-user installs still live in HKCU.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return null;
    }

    private static string NormalizeDir(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
