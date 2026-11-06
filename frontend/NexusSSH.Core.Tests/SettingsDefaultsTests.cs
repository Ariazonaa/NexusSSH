using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Einstellungen: was von der Vorgabe abweicht, der Weg zurück je Einstellung, und die Suche darin.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SettingsDefaultsTests
{
    [Fact]
    public void A_fresh_installation_has_nothing_marked_as_changed()
    {
        var shell = TestShell.Create(out _);

        Assert.NotEmpty(shell.ResettableSettings);
        Assert.All(shell.ResettableSettings, key => Assert.True(shell.IsDefaultSetting(key), key));
        Assert.True(shell.IsDefaultSetting("no such setting"));
        Assert.False(shell.ResetSetting("no such setting"));
    }

    [Fact]
    public void Right_click_pastes_on_a_fresh_installation_but_a_stored_choice_stays()
    {
        Assert.True(new AppSettings().MouseCopyPaste);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize("""{"MouseCopyPaste":false}""", SettingsJsonContext.Default.AppSettings)!.MouseCopyPaste);
    }

    [Fact]
    public void A_changed_setting_is_marked_and_goes_back_alone()
    {
        var shell = TestShell.Create(out _);
        var changes = new List<string?>();
        shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        shell.BellSound = true;
        shell.TerminalFontSize += 3;
        shell.Settings.EditorPath = @"C:\tools\editor.exe";
        shell.SoundTransferFailed = false;
        shell.AlertDiskPercent = 70;

        Assert.Equal(
            ["BellSound", "EditorPath", "ServerAlerts", "SoundTransferFailed", "TerminalFontSize"],
            shell.ResettableSettings.Where(key => !shell.IsDefaultSetting(key)).Order(StringComparer.Ordinal));

        // Eine geht zurück, die anderen bleiben, wie der Benutzer sie gestellt hat.
        Assert.True(shell.ResetSetting("TerminalFontSize"));
        Assert.Equal(new AppSettings().TerminalFontSize, shell.TerminalFontSize);
        Assert.True(shell.BellSound);

        // Die Seite der Einstellungen erfährt es – auch bei dem, was sie selbst in die Einstellungen schreibt.
        changes.Clear();
        Assert.True(shell.ResetSetting("EditorPath"));
        Assert.Equal(string.Empty, shell.Settings.EditorPath);
        Assert.Contains(nameof(ShellViewModel.ResettableSettings), changes);

        // Eine Zeile mit mehreren Werten geht als Ganzes zurück.
        Assert.True(shell.ResetSetting("ServerAlerts"));
        Assert.Equal(90, shell.Settings.AlertDiskPercent);

        foreach (var key in shell.ResettableSettings)
        {
            shell.ResetSetting(key);
        }

        Assert.All(shell.ResettableSettings, key => Assert.True(shell.IsDefaultSetting(key), key));
        Assert.True(shell.SoundTransferFailed);
        Assert.False(shell.BellSound);
    }

    [Theory]
    [InlineData("Einen Ton spielen, wenn ein Transfer scheitert", "ton", true)]
    [InlineData("Hinweise und Töne", "tone", true)]
    [InlineData("Schriftgröße des Terminals", "GROSSE terminal", true)]
    [InlineData("Schriftgröße des Terminals", "terminal schrift", true)]
    [InlineData("Schriftgröße des Terminals", "terminal farbe", false)]
    [InlineData("Réduire les animations", "reduire", true)]
    [InlineData("Anything", "", true)]
    [InlineData("Anything", "   ", true)]
    [InlineData("", "x", false)]
    [InlineData(null, "x", false)]
    public void The_search_wants_every_word_and_ignores_case_and_accents(string? text, string query, bool expected)
        => Assert.Equal(expected, SettingsSearch.Matches(text, query));
}
