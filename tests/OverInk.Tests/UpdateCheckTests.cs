using System.Net;
using System.Security.Cryptography;
using System.Text;
using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.1.9", false)]
    [InlineData("v0.2.0", "0.3.0", true)]
    [InlineData("0.2.0-beta.1", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0-beta.2", false)]
    [InlineData("0.2", "0.2.0", false)]
    [InlineData("1.0.0+abcdef", "1.0.1", true)]
    public void IsNewer_compares_release_tags(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void FormatLastChecked_is_never_until_a_check_succeeds()
    {
        Assert.Equal("Never", AppVersion.FormatLastChecked(null));
        var text = AppVersion.FormatLastChecked(new DateTime(2026, 10, 20, 2, 40, 0, DateTimeKind.Utc));
        Assert.NotEqual("Never", text);
        Assert.Contains("2026", text);
    }

    [Fact]
    public void Parser_reads_the_setup_exe_and_checksums()
    {
        const string json = """
            {
              "tag_name": "v1.4.0",
              "assets": [
                {
                  "name": "OverInk-1.4.0-win-x64.zip",
                  "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v1.4.0/OverInk-1.4.0-win-x64.zip"
                },
                {
                  "name": "OverInk-Setup-1.4.0.exe",
                  "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v1.4.0/OverInk-Setup-1.4.0.exe"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v1.4.0/SHA256SUMS.txt"
                }
              ]
            }
            """;

        var offer = GitHubReleaseParser.Parse(json);
        Assert.NotNull(offer);
        Assert.Equal("1.4.0", offer.Version);
        Assert.EndsWith("OverInk-Setup-1.4.0.exe", offer.InstallerUrl);
        Assert.EndsWith("SHA256SUMS.txt", offer.ChecksumsUrl);
    }

    [Fact]
    public void Parser_rejects_a_release_without_an_installer()
    {
        const string json = """
            { "tag_name": "v1.0.0", "assets": [ { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/sums" } ] }
            """;
        Assert.Null(GitHubReleaseParser.Parse(json));
    }

    [Fact]
    public void Checksum_accepts_the_matching_file_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "overink-checksum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "OverInk-Setup-1.0.0.exe");
            File.WriteAllText(path, "installer-bytes");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installer-bytes"))).ToLowerInvariant();
            var sums = $"{hash}  OverInk-Setup-1.0.0.exe\n{new string('a', 64)}  Other.exe\n";

            Assert.True(ChecksumFile.Matches(sums, "OverInk-Setup-1.0.0.exe", path));
            Assert.False(ChecksumFile.Matches(sums, "Missing.exe", path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Check_reports_an_installable_update_for_an_installed_copy()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(ReleaseJson("2.0.0")));
        using var http = new HttpClient(handler);
        using var service = new UpdateService(http, isInstalledCopy: () => true, downloadDirectory: Path.GetTempPath());

        var result = await service.CheckAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.CanInstall);
        Assert.True(result.ReachedServer);
        Assert.Equal("2.0.0", result.Offer?.Version);
    }

    [Fact]
    public async Task Check_reports_up_to_date_when_the_tag_matches()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(ReleaseJson("1.2.0")));
        using var http = new HttpClient(handler);
        using var service = new UpdateService(http, isInstalledCopy: () => true, downloadDirectory: Path.GetTempPath());

        var result = await service.CheckAsync("1.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.CanInstall);
    }

    [Fact]
    public async Task Check_will_not_install_over_a_portable_copy()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(ReleaseJson("3.0.0")));
        using var http = new HttpClient(handler);
        using var service = new UpdateService(http, isInstalledCopy: () => false, downloadDirectory: Path.GetTempPath());

        var result = await service.CheckAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.False(result.CanInstall);
        Assert.Contains("wasn't installed", result.Message);
    }

    [Fact]
    public async Task Download_keeps_the_installer_only_when_the_hash_matches()
    {
        var payload = "installer-bytes"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var sums = $"{hash}  OverInk-Setup-9.9.9.exe\n";
        var dir = Path.Combine(Path.GetTempPath(), "overink-update-" + Guid.NewGuid().ToString("N"));
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = url.Contains("SHA256SUMS", StringComparison.Ordinal) ? sums : Encoding.UTF8.GetString(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });

        try
        {
            using var http = new HttpClient(handler);
            using var service = new UpdateService(http, () => true, dir);
            var offer = new UpdateOffer(
                "9.9.9",
                "https://github.com/hickmanz/overink/releases/download/v9.9.9/OverInk-Setup-9.9.9.exe",
                "https://github.com/hickmanz/overink/releases/download/v9.9.9/SHA256SUMS.txt");

            var path = await service.DownloadVerifiedInstallerAsync(offer);

            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_rejects_an_asset_that_is_not_on_github()
    {
        using var http = new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var service = new UpdateService(http, () => true, Path.GetTempPath());
        var offer = new UpdateOffer("1.0.0", "https://example.com/setup.exe", "https://example.com/SHA256SUMS.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadVerifiedInstallerAsync(offer));
    }

    [Theory]
    [InlineData("0.3.0-beta.2", "0.3.0-beta.10", true)]
    [InlineData("0.3.0-beta.10", "0.3.0-beta.2", false)]
    [InlineData("0.3.0-alpha.5", "0.3.0-beta.1", true)]
    [InlineData("0.3.0-beta", "0.3.0-beta.1", true)]
    [InlineData("0.3.0-beta.1", "0.3.0", true)]
    [InlineData("0.2.5", "0.3.0-beta.1", true)]
    public void IsNewer_orders_prereleases_numerically(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void Notes_drop_the_generated_scaffolding_and_keep_the_titles()
    {
        const string body = """
            ## What's Changed
            * Add beta updates switch by @hickmanz in https://github.com/hickmanz/overink/pull/12
            * Fix toolbar clamp on vertical dock by @hickmanz in https://github.com/hickmanz/overink/pull/13

            ## New Contributors
            * @someone made their first contribution in https://github.com/hickmanz/overink/pull/14

            **Full Changelog**: https://github.com/hickmanz/overink/compare/v0.2.0...v0.3.0
            """;

        var notes = GitHubReleaseParser.FormatNotes(body);

        Assert.Equal("• Add beta updates switch\n• Fix toolbar clamp on vertical dock", notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("**Full Changelog**: https://github.com/hickmanz/overink/compare/v0.1.0...v0.2.0")]
    public void Notes_are_null_when_nothing_useful_remains(string? body)
    {
        Assert.Null(GitHubReleaseParser.FormatNotes(body));
    }

    [Fact]
    public void Notes_keep_hand_written_bullets_and_cap_long_lists()
    {
        var body = string.Join('\n', Enumerable.Range(1, 11).Select(i => $"- Change {i}"));

        var lines = GitHubReleaseParser.FormatNotes(body)!.Split('\n');

        Assert.Equal(9, lines.Length);
        Assert.Equal("• Change 1", lines[0]);
        Assert.Equal("…and 3 more", lines[^1]);
    }

    [Fact]
    public void Parser_carries_the_release_notes_and_prerelease_flag()
    {
        const string json = """
            {
              "tag_name": "v0.3.0-beta.1",
              "prerelease": true,
              "body": "- Try the new thing",
              "assets": [
                { "name": "OverInk-Setup-0.3.0-beta.1.exe", "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v0.3.0-beta.1/OverInk-Setup-0.3.0-beta.1.exe" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v0.3.0-beta.1/SHA256SUMS.txt" }
              ]
            }
            """;

        var offer = GitHubReleaseParser.Parse(json);

        Assert.NotNull(offer);
        Assert.True(offer.IsPrerelease);
        Assert.Equal("• Try the new thing", offer.Notes);
    }

    [Fact]
    public void ParseMany_skips_drafts_and_releases_without_assets()
    {
        var json = $"[{ReleaseJson("2.0.0-beta.1")}, {ReleaseJson("1.9.0").Replace("{", "{ \"draft\": true,")}, {{ \"tag_name\": \"v1.8.0\", \"assets\": [] }}]";

        var offers = GitHubReleaseParser.ParseMany(json);

        Assert.Equal(["2.0.0-beta.1"], offers.Select(o => o.Version));
    }

    [Fact]
    public async Task Check_ignores_prereleases_unless_beta_updates_are_on()
    {
        var requested = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return JsonResponse(request.RequestUri.AbsoluteUri.Contains("/latest", StringComparison.Ordinal)
                ? ReleaseJson("1.0.0")
                : $"[{ReleaseJson("1.1.0-beta.3")}, {ReleaseJson("1.1.0-beta.10")}, {ReleaseJson("1.0.0")}]");
        });
        using var http = new HttpClient(handler);
        using var service = new UpdateService(http, isInstalledCopy: () => true, downloadDirectory: Path.GetTempPath());

        var stable = await service.CheckAsync("1.0.0", includePrereleases: false);
        var beta = await service.CheckAsync("1.0.0", includePrereleases: true);

        Assert.Equal(UpdateCheckStatus.UpToDate, stable.Status);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, beta.Status);
        Assert.Equal("1.1.0-beta.10", beta.Offer?.Version);
        Assert.Contains("(beta)", beta.Message);
        Assert.Contains("/latest", requested[0]);
        Assert.DoesNotContain("/latest", requested[1]);
    }

    [Fact]
    public async Task Check_prefers_the_stable_release_over_its_own_beta()
    {
        var handler = new ScriptedHandler(_ => JsonResponse($"[{ReleaseJson("1.1.0-beta.2")}, {ReleaseJson("1.1.0")}]"));
        using var http = new HttpClient(handler);
        using var service = new UpdateService(http, isInstalledCopy: () => true, downloadDirectory: Path.GetTempPath());

        var result = await service.CheckAsync("1.0.0", includePrereleases: true);

        Assert.Equal("1.1.0", result.Offer?.Version);
    }

    private static string ReleaseJson(string version) => $$"""
        {
          "tag_name": "v{{version}}",
          "assets": [
            {
              "name": "OverInk-Setup-{{version}}.exe",
              "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v{{version}}/OverInk-Setup-{{version}}.exe"
            },
            {
              "name": "SHA256SUMS.txt",
              "browser_download_url": "https://github.com/hickmanz/overink/releases/download/v{{version}}/SHA256SUMS.txt"
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
