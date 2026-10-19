using Microsoft.Win32;

namespace OverInk.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OverInk";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null)
        {
            return;
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
