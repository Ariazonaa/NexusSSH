using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Tastenkürzel selbst belegen: ändern, entfernen, zurücksetzen – und was sich nicht belegen lässt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ShortcutTests
{
    public ShortcutTests() => L.Init(AppLanguage.English);

    private static KeyChord? Of(ShellViewModel shell, string id) => shell.Commands.Single(command => command.Id == id).Shortcut;

    [Fact]
    public void A_command_gets_a_new_shortcut_that_window_and_terminal_both_learn()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Null(shell.SetShortcut("terminal.clear", new KeyChord("KeyK")));

        Assert.Equal(new KeyChord("KeyK"), Of(shell, "terminal.clear"));
        Assert.Equal("Shift+KeyK", settings.Shortcuts["terminal.clear"]);
        Assert.Contains(shell.TerminalShortcuts, shortcut => shortcut is { Code: "KeyK", Shift: true, Id: "terminal.clear" });
        Assert.Equal([nameof(shell.Commands), nameof(shell.TerminalShortcuts), nameof(shell.ShortcutRows)], changed);
        // Die Palette zeigt das neue Kürzel (der Befehl braucht eine Sitzung, um dort zu stehen).
        shell.OpenSession(shell.Profiles[0]);
        Assert.Contains(shell.SearchPalette(">"), item => item.Shortcut == "Ctrl+Shift+K");

        var row = shell.ShortcutRows.Single(candidate => candidate.Id == "terminal.clear");
        Assert.Equal(("Ctrl+Shift+K", true), (row.Shortcut, row.IsCustom));
        Assert.DoesNotContain(shell.ShortcutRows, candidate => candidate.Id.StartsWith("theme.", StringComparison.Ordinal));

        // Ohne Umschalt geht eine Funktionstaste, und sie führt den Befehl auch aus.
        Assert.Null(shell.SetShortcut("view.settings", new KeyChord("F2", Shift: false)));
        Assert.Equal("F2", settings.Shortcuts["view.settings"]);
    }

    [Fact]
    public void A_shortcut_another_command_has_or_the_shell_needs_is_refused()
    {
        var shell = TestShell.Create(out _);

        // Strg+Umschalt+F sucht im Terminal.
        Assert.Equal("Ctrl+Shift+F already belongs to “Find in terminal”.".Replace("Find in terminal", shell.Commands.Single(command => command.Id == "terminal.find").Title), shell.SetShortcut("terminal.clear", new KeyChord("KeyF")));
        Assert.Null(Of(shell, "terminal.clear"));

        foreach (var chord in new[] { new KeyChord("KeyK", Shift: false), new KeyChord("KeyC"), new KeyChord("KeyV"), new KeyChord("Escape"), new KeyChord("Enter", Shift: false) })
        {
            Assert.StartsWith("This shortcut cannot be used", shell.SetShortcut("terminal.clear", chord));
        }

        Assert.Equal("There is no such command.", shell.SetShortcut("nope", new KeyChord("KeyK")));
        Assert.Empty(shell.Settings.Shortcuts);
    }

    [Fact]
    public void A_shortcut_can_be_removed_and_given_back()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);

        Assert.Null(shell.SetShortcut("terminal.find", null));
        Assert.Null(Of(shell, "terminal.find"));
        Assert.Equal(string.Empty, settings.Shortcuts["terminal.find"]);
        Assert.DoesNotContain(shell.TerminalShortcuts, shortcut => shortcut.Id == "terminal.find");

        // Das frei gewordene Kürzel kann ein anderer Befehl bekommen – dann geht das Zurücksetzen nicht mehr einfach so.
        Assert.Null(shell.SetShortcut("terminal.clear", new KeyChord("KeyF")));
        Assert.Contains("already belongs to", shell.ResetShortcut("terminal.find"));

        Assert.Null(shell.ResetShortcut("terminal.clear"));
        Assert.Null(shell.ResetShortcut("terminal.find"));
        Assert.Equal(new KeyChord("KeyF"), Of(shell, "terminal.find"));
        Assert.Empty(settings.Shortcuts);

        shell.SetShortcut("terminal.clear", new KeyChord("KeyK"));
        shell.ResetShortcuts();
        Assert.Null(Of(shell, "terminal.clear"));
        Assert.Empty(settings.Shortcuts);
    }

    [Fact]
    public void Stored_shortcuts_apply_at_the_start_and_what_no_longer_fits_is_skipped()
    {
        var settings = new AppSettings
        {
            Shortcuts =
            {
                ["terminal.clear"] = "Shift+KeyK",
                ["terminal.find"] = string.Empty,
                ["view.settings"] = "Shift+KeyE",
                ["gone.command"] = "Shift+KeyQ",
                ["terminal.save"] = "KeyS",
                ["session.duplicate"] = "nonsense",
            },
        };

        var shell = TestShell.Create(out _, settings);

        Assert.Equal(new KeyChord("KeyK"), Of(shell, "terminal.clear"));
        Assert.Null(Of(shell, "terminal.find"));

        // Die Einstellungen nehmen sich Strg+Umschalt+E – der Befehl, der es vorgegeben hatte, verliert es.
        Assert.Equal(new KeyChord("KeyE"), Of(shell, "view.settings"));
        Assert.Null(Of(shell, "terminal.splitRight"));

        // Nicht belegbar oder unlesbar: Der Befehl bleibt, wie vorgegeben.
        Assert.Null(Of(shell, "terminal.save"));
        Assert.Null(Of(shell, "session.duplicate"));
        Assert.Equal(shell.Commands.Count(command => command.Shortcut is not null), shell.Commands.Where(command => command.Shortcut is not null).Select(command => command.Shortcut).Distinct().Count());
    }

    [Theory]
    [InlineData("Shift+KeyK", "KeyK", true)]
    [InlineData("F5", "F5", false)]
    [InlineData("Shift+Tab", "Tab", true)]
    [InlineData("Digit1", "Digit1", false)]
    public void A_shortcut_is_stored_as_text_and_read_back(string setting, string code, bool shift)
    {
        var chord = KeyChord.Parse(setting);

        Assert.Equal(new KeyChord(code, shift), chord);
        Assert.Equal(setting, chord!.ToSetting());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("KeyK")]
    [InlineData("Shift+KeyC")]
    [InlineData("Ctrl+KeyK")]
    [InlineData("Shift+")]
    [InlineData("F13")]
    public void Anything_else_is_no_shortcut(string? setting)
    {
        Assert.Null(KeyChord.Parse(setting));
    }

    [Fact]
    public void Settings_keep_only_entries_of_a_sensible_size()
    {
        var settings = new AppSettings { Shortcuts = { ["terminal.clear"] = "Shift+KeyK", [new string('x', 65)] = "F5", ["a"] = new string('y', 33) } };

        Assert.Equal(["terminal.clear"], settings.Sanitize().Shortcuts.Keys);
        Assert.All(KeyChord.Codes, code => Assert.NotEmpty(KeyChord.KeyName(code)));
    }

    [Fact]
    public void Arrow_keys_can_be_bound_with_control_and_shift()
    {
        var shell = TestShell.Create(out _, new AppSettings());

        Assert.All(new[] { "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown" }, code => Assert.Contains(code, KeyChord.Codes));
        Assert.Equal(new KeyChord("ArrowLeft"), Of(shell, "terminal.focusLeft"));
        Assert.Equal(new KeyChord("ArrowRight"), Of(shell, "terminal.focusRight"));
        Assert.Equal(new KeyChord("ArrowUp"), Of(shell, "terminal.focusUp"));
        Assert.Equal(new KeyChord("ArrowDown"), Of(shell, "terminal.focusDown"));
        Assert.Equal(new KeyChord("KeyZ"), Of(shell, "terminal.zoomPane"));
        Assert.Null(Of(shell, "terminal.equalize"));

        // Pfeile ohne Umschalt gehören dem Programm im Terminal (Zeile, Verlauf, Cursor).
        Assert.False(new KeyChord("ArrowLeft", Shift: false).IsAllowed);
        Assert.Null(KeyChord.Parse("ArrowUp"));
        Assert.Equal(new KeyChord("ArrowUp"), KeyChord.Parse("Shift+ArrowUp"));
        Assert.Equal("Ctrl+Shift+←", new KeyChord("ArrowLeft").Display);
    }
}
