using System.Reflection;

namespace OverInk.Services;

/// <summary>The version baked into this build, and comparisons against GitHub release tags.</summary>
public static class AppVersion
{
    public static string Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return Normalize(info);
            }

            var assembly = Assembly.GetExecutingAssembly().GetName().Version;
            return assembly == null
                ? "0.0.0"
                : $"{assembly.Major}.{assembly.Minor}.{Math.Max(assembly.Build, 0)}";
        }
    }

    /// <summary>Strips a leading v and any build metadata (<c>0.2.0+commit</c>).</summary>
    public static string Normalize(string version)
    {
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var space = text.IndexOf(' ');
        if (space >= 0)
        {
            text = text[..space];
        }

        return text;
    }

    /// <summary>True when <paramref name="candidate"/> is a newer release than <paramref name="current"/>.</summary>
    public static bool IsNewer(string current, string candidate)
    {
        var currentParts = Split(current);
        var candidateParts = Split(candidate);
        if (currentParts == null || candidateParts == null)
        {
            return false;
        }

        var compared = candidateParts.Value.Core.CompareTo(currentParts.Value.Core);
        if (compared != 0)
        {
            return compared > 0;
        }

        // Same numbers: a stable tag is newer than a prerelease, and prereleases compare by their suffix.
        if (currentParts.Value.Pre == null)
        {
            return false;
        }

        if (candidateParts.Value.Pre == null)
        {
            return true;
        }

        return string.CompareOrdinal(candidateParts.Value.Pre, currentParts.Value.Pre) > 0;
    }

    public static string FormatLastChecked(DateTime? value)
    {
        if (value == null)
        {
            return "Never";
        }

        var utc = value.Value.Kind switch
        {
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            DateTimeKind.Utc => value.Value,
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
        return utc.ToLocalTime().ToString("MMM d, yyyy, h:mm tt");
    }

    private static (Version Core, string? Pre)? Split(string raw)
    {
        var text = Normalize(raw);
        var dash = text.IndexOf('-');
        var coreText = dash >= 0 ? text[..dash] : text;
        var pre = dash >= 0 ? text[(dash + 1)..] : null;
        if (string.IsNullOrEmpty(pre))
        {
            pre = null;
        }

        if (!Version.TryParse(coreText, out var parsed))
        {
            return null;
        }

        var core = new Version(parsed.Major, parsed.Minor, parsed.Build < 0 ? 0 : parsed.Build);
        return (core, pre);
    }
}
