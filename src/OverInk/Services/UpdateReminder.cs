using OverInk.Models;

namespace OverInk.Services;

/// <summary>
/// "Not now" snoozes the startup prompt for a release rather than silencing it forever: the prompt comes
/// back on the next few launches, then stays quiet until a newer release (or a manual Check now).
/// </summary>
public static class UpdateReminder
{
    public const int MaxDismissals = 3;

    /// <summary>Whether the automatic startup check should prompt for <paramref name="version"/>.</summary>
    public static bool ShouldPromptAutomatically(AppSettings settings, string version) =>
        !string.Equals(settings.SkippedUpdateVersion, version, StringComparison.OrdinalIgnoreCase)
        || settings.SkippedUpdateCount < MaxDismissals;

    /// <summary>Counts a "Not now" for <paramref name="version"/>; a different version starts over at one.</summary>
    public static void RecordDismissal(AppSettings settings, string version)
    {
        if (string.Equals(settings.SkippedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            settings.SkippedUpdateCount++;
        }
        else
        {
            settings.SkippedUpdateVersion = version;
            settings.SkippedUpdateCount = 1;
        }
    }
}
