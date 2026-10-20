using OverInk.Models;
using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public class UpdateReminderTests
{
    [Fact]
    public void Prompts_for_a_version_that_was_never_dismissed()
    {
        Assert.True(UpdateReminder.ShouldPromptAutomatically(new AppSettings(), "0.4.1"));
    }

    [Fact]
    public void Keeps_prompting_until_dismissed_three_times()
    {
        var settings = new AppSettings();

        for (var i = 1; i < UpdateReminder.MaxDismissals; i++)
        {
            UpdateReminder.RecordDismissal(settings, "0.4.1");
            Assert.True(UpdateReminder.ShouldPromptAutomatically(settings, "0.4.1"), $"still prompts after {i} dismissal(s)");
        }

        UpdateReminder.RecordDismissal(settings, "0.4.1");
        Assert.False(UpdateReminder.ShouldPromptAutomatically(settings, "0.4.1"));
        Assert.Equal(UpdateReminder.MaxDismissals, settings.SkippedUpdateCount);
    }

    [Fact]
    public void A_newer_release_starts_the_count_over()
    {
        var settings = new AppSettings();
        for (var i = 0; i < UpdateReminder.MaxDismissals; i++)
        {
            UpdateReminder.RecordDismissal(settings, "0.4.1");
        }

        Assert.False(UpdateReminder.ShouldPromptAutomatically(settings, "0.4.1"));
        Assert.True(UpdateReminder.ShouldPromptAutomatically(settings, "0.4.2"));

        UpdateReminder.RecordDismissal(settings, "0.4.2");
        Assert.Equal("0.4.2", settings.SkippedUpdateVersion);
        Assert.Equal(1, settings.SkippedUpdateCount);
    }

    [Fact]
    public void Settings_saved_before_the_counter_existed_still_prompt()
    {
        // Older settings.json has the skipped version but no count (reads as 0).
        var settings = new AppSettings { SkippedUpdateVersion = "0.4.1" };
        Assert.True(UpdateReminder.ShouldPromptAutomatically(settings, "0.4.1"));
    }
}
