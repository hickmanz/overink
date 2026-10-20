using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public class UpdateCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "overink-cleanup-" + Guid.NewGuid().ToString("N"));

    public UpdateCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Removes_everything_once_the_installed_version_is_current()
    {
        Touch("OverInk-Setup-0.4.1.exe");
        Touch("SHA256SUMS.txt");
        Touch("setup.log");
        Touch("OverInk-Setup-0.4.1.exe.partial");

        UpdateService.CleanUpDownloads("0.4.1", _dir);

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Removes_installers_for_older_versions_too()
    {
        Touch("OverInk-Setup-0.4.0.exe");

        UpdateService.CleanUpDownloads("0.4.1", _dir);

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Keeps_a_newer_installer_and_its_support_files()
    {
        var newer = Touch("OverInk-Setup-0.5.0.exe");
        var sums = Touch("SHA256SUMS.txt");
        var log = Touch("setup.log");
        Touch("OverInk-Setup-0.4.0.exe");
        Touch("leftover.partial");

        UpdateService.CleanUpDownloads("0.4.1", _dir);

        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(sums));
        Assert.True(File.Exists(log));
        Assert.False(File.Exists(Path.Combine(_dir, "OverInk-Setup-0.4.0.exe")));
        Assert.False(File.Exists(Path.Combine(_dir, "leftover.partial")));
    }

    [Fact]
    public void Missing_folder_is_fine()
    {
        UpdateService.CleanUpDownloads("0.4.1", Path.Combine(_dir, "nope"));
    }

    [Fact]
    public void A_locked_installer_is_left_for_next_time_without_throwing()
    {
        var path = Touch("OverInk-Setup-0.4.1.exe");
        Touch("SHA256SUMS.txt");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            UpdateService.CleanUpDownloads("0.4.1", _dir);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(_dir, "SHA256SUMS.txt")));
        }

        UpdateService.CleanUpDownloads("0.4.1", _dir);
        Assert.False(Directory.Exists(_dir));
    }
}
