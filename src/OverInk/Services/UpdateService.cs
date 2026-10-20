using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace OverInk.Services;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    UpdateOffer? Offer,
    bool CanInstall,
    bool ReachedServer)
{
    public static UpdateCheckResult UpToDate(string message) =>
        new(UpdateCheckStatus.UpToDate, message, null, false, true);

    public static UpdateCheckResult Available(string message, UpdateOffer offer, bool canInstall) =>
        new(UpdateCheckStatus.UpdateAvailable, message, offer, canInstall, true);

    public static UpdateCheckResult Fail(string message, bool reachedServer = false) =>
        new(UpdateCheckStatus.Failed, message, null, false, reachedServer);
}

/// <summary>
/// Looks up the latest GitHub release and, when this copy was installed by OverInk Setup,
/// downloads that installer and starts it silently.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<bool> _isInstalledCopy;
    private readonly string _downloadDirectory;

    public UpdateService(HttpClient? httpClient = null, Func<bool>? isInstalledCopy = null, string? downloadDirectory = null)
    {
        _ownsClient = httpClient == null;
        _http = httpClient ?? CreateClient();
        _isInstalledCopy = isInstalledCopy ?? (() => InstallDetector.IsRunningInstalledCopy());
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OverInk",
            "updates");
    }

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(AppVersion.Current, cancellationToken);

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleaseParser.LatestApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Fail("No release has been published yet.", reachedServer: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Fail($"GitHub returned {(int)response.StatusCode}.", reachedServer: true);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var offer = GitHubReleaseParser.Parse(json);
            if (offer == null)
            {
                return UpdateCheckResult.Fail("The latest release doesn't include an installer.", reachedServer: true);
            }

            if (!AppVersion.IsNewer(currentVersion, offer.Version))
            {
                return UpdateCheckResult.UpToDate("You're on the latest version.");
            }

            var canInstall = _isInstalledCopy();
            var message = canInstall
                ? $"Version {offer.Version} is available."
                : $"Version {offer.Version} is available. This copy wasn't installed with OverInk Setup, so it can't update itself.";
            return UpdateCheckResult.Available(message, offer, canInstall);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Fail("The update check timed out.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Fail("Couldn't reach GitHub to check for updates.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Fail("GitHub returned an unexpected response.");
        }
    }

    public async Task<string> DownloadVerifiedInstallerAsync(UpdateOffer offer, CancellationToken cancellationToken = default)
    {
        if (!GitHubReleaseParser.IsTrustedDownloadUrl(offer.InstallerUrl)
            || !GitHubReleaseParser.IsTrustedDownloadUrl(offer.ChecksumsUrl))
        {
            throw new InvalidOperationException("The release assets aren't on the OverInk GitHub releases page.");
        }

        Directory.CreateDirectory(_downloadDirectory);
        var fileName = $"OverInk-Setup-{SafeVersion(offer.Version)}.exe";
        var setupPath = Path.Combine(_downloadDirectory, fileName);
        var sumsPath = Path.Combine(_downloadDirectory, "SHA256SUMS.txt");

        await DownloadAsync(offer.ChecksumsUrl, sumsPath, cancellationToken).ConfigureAwait(false);
        await DownloadAsync(offer.InstallerUrl, setupPath, cancellationToken).ConfigureAwait(false);

        var sums = await File.ReadAllTextAsync(sumsPath, cancellationToken).ConfigureAwait(false);
        if (!ChecksumFile.Matches(sums, fileName, setupPath)
            && !ChecksumFile.Matches(sums, Path.GetFileName(new Uri(offer.InstallerUrl).LocalPath), setupPath))
        {
            File.Delete(setupPath);
            throw new InvalidDataException("The downloaded installer didn't match SHA256SUMS.txt.");
        }

        return setupPath;
    }

    public static void LaunchInstaller(string setupPath)
    {
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            // Very silent: no wizard. The installer closes this process, then the setup script starts the new copy.
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS",
            UseShellExecute = true
        });
        if (started == null)
        {
            throw new InvalidOperationException("Windows didn't start the installer.");
        }
    }

    public static void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = GitHubReleaseParser.ReleasesPageUrl,
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var partial = destination + ".partial";
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(partial))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        File.Move(partial, destination, overwrite: true);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OverInk");
        return client;
    }

    private static string SafeVersion(string version)
    {
        var cleaned = new string(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "update" : cleaned;
    }
}
