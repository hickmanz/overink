using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverInk.Services;

/// <param name="Notes">A short, cleaned-up bullet list taken from the release body, or null when there is nothing useful.</param>
/// <param name="IsPrerelease">True for a GitHub pre-release (a beta tag).</param>
public sealed record UpdateOffer(
    string Version,
    string InstallerUrl,
    string ChecksumsUrl,
    string? Notes = null,
    bool IsPrerelease = false);

/// <summary>Reads the installer and checksum assets from a GitHub release payload.</summary>
public static partial class GitHubReleaseParser
{
    public const string LatestApiUrl = "https://api.github.com/repos/hickmanz/overink/releases/latest";
    /// <summary>The recent-releases list. Unlike <see cref="LatestApiUrl"/>, it includes pre-releases.</summary>
    public const string ReleasesListApiUrl = "https://api.github.com/repos/hickmanz/overink/releases?per_page=10";
    public const string ReleasesPageUrl = "https://github.com/hickmanz/overink/releases/latest";
    private const string DownloadPathPrefix = "/hickmanz/overink/releases/download/";
    private const int MaxNoteLines = 8;
    private const int MaxNoteLineLength = 140;

    public static UpdateOffer? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    /// <summary>Parses the releases-list payload, skipping drafts and releases without an installer.</summary>
    public static IReadOnlyList<UpdateOffer> ParseMany(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var offers = new List<UpdateOffer>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return offers;
        }

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            if (ParseRelease(release) is { } offer)
            {
                offers.Add(offer);
            }
        }

        return offers;
    }

    private static UpdateOffer? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagProp))
        {
            return null;
        }

        var tag = tagProp.GetString();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var version = AppVersion.Normalize(tag);
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? setup = null;
        string? exactSetup = null;
        string? sums = null;
        var expected = $"OverInk-Setup-{version}.exe";
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sums = url;
            }
            else if (name.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                exactSetup = url;
            }
            else if (name.StartsWith("OverInk-Setup-", StringComparison.OrdinalIgnoreCase)
                     && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                setup ??= url;
            }
        }

        var installer = exactSetup ?? setup;
        if (installer == null || sums == null)
        {
            return null;
        }

        var body = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
            ? bodyProp.GetString()
            : null;
        var prerelease = (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
                         || version.Contains('-');
        return new UpdateOffer(version, installer, sums, FormatNotes(body), prerelease);
    }

    /// <summary>
    /// Boils a release body down to a short bullet list: drops GitHub's "What's Changed" header, the
    /// "Full Changelog" link and "New Contributors" section, and strips PR links and author handles.
    /// Returns null when nothing useful is left (for example a body that is only a link).
    /// </summary>
    public static string? FormatNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var lines = new List<string>();
        var skippingContributors = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#').Trim().Trim('*').Trim();
                skippingContributors = heading.StartsWith("New Contributors", StringComparison.OrdinalIgnoreCase);
                var generic = heading.StartsWith("What's Changed", StringComparison.OrdinalIgnoreCase)
                              || heading.StartsWith("What’s Changed", StringComparison.OrdinalIgnoreCase);
                if (skippingContributors || generic)
                {
                    continue;
                }

                // Any other heading ("Highlights", "Fixes") is kept as plain text.
                line = heading;
            }
            else if (skippingContributors)
            {
                continue;
            }

            if (line.Contains("Full Changelog", StringComparison.OrdinalIgnoreCase)
                && (line.Contains("http", StringComparison.OrdinalIgnoreCase) || line.StartsWith("**", StringComparison.Ordinal)))
            {
                continue;
            }

            var bullet = line.Length > 1 && line[0] is '-' or '*' or '•' && char.IsWhiteSpace(line[1]);
            var text = bullet ? line[1..].Trim() : line;
            text = AuthorSuffix().Replace(text, "");
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            text = MarkdownLink().Replace(text, "$1");
            text = BareUrl().Replace(text, "").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > MaxNoteLineLength)
            {
                text = text[..(MaxNoteLineLength - 1)].TrimEnd() + "…";
            }

            lines.Add(bullet || lines.Count > 0 ? "• " + text : text);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var shown = lines.Take(MaxNoteLines).ToList();
        if (lines.Count > MaxNoteLines)
        {
            shown.Add($"…and {lines.Count - MaxNoteLines} more");
        }

        return string.Join('\n', shown);
    }

    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.StartsWith(DownloadPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>" by @user in https://github.com/.../pull/12" at the end of an auto-generated line.</summary>
    [GeneratedRegex(@"\s+by\s+@\S+(\s+in\s+\S+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSuffix();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrl();
}
