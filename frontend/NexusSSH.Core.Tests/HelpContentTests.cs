using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public class HelpContentTests
{
    [Theory]
    [InlineData("0.20.0", "0.21.0", true)]
    [InlineData("0.21.0", "0.21.0", false)]
    [InlineData("0.22.0", "0.21.0", false)]
    [InlineData("", "0.21.0", false)]
    [InlineData(null, "0.21.0", false)]
    [InlineData("kaputt", "0.21.0", false)]
    public void Only_a_newer_version_than_the_one_seen_before_counts_as_an_update(string? before, string now, bool expected)
        => Assert.Equal(expected, HelpContent.IsUpdate(before, now));

    [Fact]
    public void The_first_start_remembers_the_version_without_opening_the_help_and_an_update_opens_it_once()
    {
        var shell = TestShell.Create(out _);

        Assert.False(shell.NoteVersion("0.21.0"));
        Assert.False(shell.IsHelpOpen);
        Assert.Equal("0.21.0", shell.Settings.LastSeenVersion);

        Assert.True(shell.NoteVersion("0.22.0"));
        Assert.True(shell.IsHelpOpen);

        shell.IsHelpOpen = false;
        Assert.False(shell.NoteVersion("0.22.0"));
        Assert.False(shell.IsHelpOpen);
    }

    [Fact]
    public void Help_and_settings_are_never_open_together()
    {
        var shell = TestShell.Create(out _);

        shell.RunCommand("view.help");
        Assert.True(shell.IsHelpOpen);

        shell.IsSettingsOpen = true;
        Assert.False(shell.IsHelpOpen);

        shell.RunCommand("view.help");
        Assert.False(shell.IsSettingsOpen);
    }

    [Fact]
    public void The_topics_name_the_shortcuts_as_they_are_set_right_now()
    {
        var shell = TestShell.Create(out _);

        Assert.Contains(HelpContent.Topics(shell.Commands), topic => topic.Text.Contains("Ctrl+Shift+P", StringComparison.Ordinal));
        Assert.Contains(HelpContent.Shortcuts(shell.Commands), shortcut => shortcut is { Keys: "Ctrl+Shift+F" });
        Assert.DoesNotContain(HelpContent.Shortcuts(shell.Commands), shortcut => shortcut.Title.EndsWith('…'));
        Assert.All(HelpContent.Topics(shell.Commands), topic => Assert.DoesNotContain("{", topic.Text, StringComparison.Ordinal));
        Assert.NotEmpty(HelpContent.WhatsNew());
    }
}
