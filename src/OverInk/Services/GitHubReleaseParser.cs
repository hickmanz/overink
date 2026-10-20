using System.Text.Json;

namespace OverInk.Services;

public sealed record UpdateOffer(string Version, string InstallerUrl, string ChecksumsUrl);

/// <summary>Reads the installer and checksum assets from a GitHub release payload.</summary>
public static class GitHubReleaseParser
{
    public const string LatestApiUrl = "https://api.github.com/repos/hickmanz/overink/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/hickmanz/overink/releases/latest";
    private const string DownloadPathPrefix = "/hickmanz/overink/releases/download/";

    public static UpdateOffer? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagProp))
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

        return new UpdateOffer(version, installer, sums);
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
}
