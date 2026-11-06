using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Befehlspalette (Spec §13): Server, Tabs und Befehle hinter einem Suchfeld.</summary>
[Collection(LanguageCollection.Name)]
public sealed class CommandPaletteTests
{
    private static IEnumerable<string> Titles(IEnumerable<PaletteItem> items, PaletteKind kind)
        => items.Where(item => item.Kind == kind).Select(item => item.Title);

    [Fact]
    public void An_empty_query_lists_servers_first_then_the_commands_that_work_right_now()
    {
        var shell = TestShell.Create(out _);

        var items = shell.SearchPalette(null);

        Assert.Equal(["web01", "database01", "staging", "test01"], items.Take(4).Select(item => item.Title));
        Assert.All(items.Skip(4), item => Assert.Equal(PaletteKind.Command, item.Kind));
        var commands = Titles(items, PaletteKind.Command).ToList();
        Assert.Contains("Add server…", commands);
        Assert.Contains("Color scheme: Dracula", commands);
        Assert.DoesNotContain("Split terminal: side by side", commands);
        Assert.DoesNotContain("Close tab", commands);
        Assert.DoesNotContain("Open command palette", commands);
    }

    [Fact]
    public void Servers_show_their_address_and_group_and_open_a_session()
    {
        var shell = TestShell.Create(out _);

        var staging = Assert.Single(shell.SearchPalette("stag"));

        Assert.Equal(PaletteKind.Server, staging.Kind);
        Assert.Equal("deploy@10.0.2.10:2222  ·  Development", staging.Detail);
        staging.Run();
        Assert.Equal("staging", shell.ActiveSession!.Title);
    }

    [Fact]
    public void Every_word_of_the_query_must_match_title_or_detail()
    {
        var shell = TestShell.Create(out _);

        Assert.Equal(["web01", "database01"], Titles(shell.SearchPalette("production"), PaletteKind.Server));
        Assert.Equal(["Color scheme: Solarized Dark"], shell.SearchPalette("scheme solar").Select(item => item.Title));
        Assert.Empty(shell.SearchPalette("gibt es nicht"));
    }

    [Fact]
    public void The_prefix_restricts_the_list_to_commands()
    {
        var shell = TestShell.Create(out var backend);
        shell.OpenSession(backend.GetProfiles()[0]);

        var items = shell.SearchPalette("> split");

        Assert.All(items, item => Assert.Equal(PaletteKind.Command, item.Kind));
        Assert.Equal(["Split terminal: side by side", "Split terminal: stacked"], items.Select(item => item.Title));
        Assert.Equal("Ctrl+Shift+E", items[0].Shortcut);
    }

    [Fact]
    public void With_a_session_its_commands_appear_and_run()
    {
        var shell = TestShell.Create(out var backend);
        var first = shell.OpenSession(backend.GetProfiles()[0]);

        Assert.Single(shell.SearchPalette("> stacked")).Run();

        Assert.Equal(2, shell.Sessions.Count);
        Assert.False(shell.Layout.RootSideBySide());
        Assert.True(first.IsSplit);

        Assert.Single(shell.SearchPalette("> remove split")).Run();
        Assert.False(first.IsSplit);
        Assert.Empty(shell.SearchPalette("> remove split"));
    }

    [Fact]
    public void Open_tabs_can_be_switched_to_and_put_next_to_the_active_one()
    {
        var shell = TestShell.Create(out var backend);
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        var database = shell.OpenSession(backend.GetProfiles()[1]);

        var items = shell.SearchPalette("web01");

        Assert.Equal(["web01"], Titles(items, PaletteKind.Server));
        Assert.Equal(["Switch to web01"], Titles(items, PaletteKind.Tab));
        Assert.Equal(["Split terminal with web01"], Titles(items, PaletteKind.Command));

        items.Single(item => item.Kind == PaletteKind.Command).Run();
        Assert.Equal([database, web], shell.Layout.Panes);

        items.Single(item => item.Kind == PaletteKind.Tab).Run();
        Assert.Same(web, shell.ActiveSession);
    }

    [Fact]
    public void Commands_run_by_id_and_refuse_what_does_not_apply()
    {
        var shell = TestShell.Create(out var backend);

        Assert.False(shell.RunCommand("session.close"), "ohne Sitzung gibt es nichts zu schließen");
        Assert.False(shell.RunCommand("format.disk"));

        shell.OpenSession(backend.GetProfiles()[0]);
        Assert.True(shell.RunCommand("session.duplicate"));
        Assert.Equal(2, shell.Sessions.Count);
        Assert.True(shell.RunCommand("session.close"));
        Assert.Single(shell.Sessions);

        Assert.True(shell.RunCommand("view.snippets"));
        Assert.False(shell.IsSnippetsVisible);
    }

    [Fact]
    public void Commands_that_need_the_window_ask_for_it()
    {
        var shell = TestShell.Create(out var backend);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;
        shell.OpenSession(backend.GetProfiles()[0]);

        shell.RunCommand("view.palette");
        shell.RunCommand("server.add");
        shell.RunCommand("server.import");
        shell.RunCommand("terminal.clear");
        shell.FocusTerminal();

        Assert.Equal(
            [UiRequest.FocusCommandBar, UiRequest.AddServer, UiRequest.ImportSshConfig, UiRequest.ClearTerminal, UiRequest.FocusTerminal],
            requests);
    }

    [Fact]
    public void The_find_command_opens_the_search_bar()
    {
        var shell = TestShell.Create(out var backend);
        Assert.False(shell.RunCommand("terminal.find"));

        shell.OpenSession(backend.GetProfiles()[0]);
        Assert.True(shell.RunCommand("terminal.find"));
        Assert.True(shell.Search.IsOpen);
    }

    [Fact]
    public void Theme_and_font_size_change_through_commands_and_land_in_the_settings()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        Assert.Equal("Black", shell.TerminalTheme.Name);
        Assert.Equal(TerminalThemes.DefaultFontSize, shell.TerminalFontSize);

        shell.RunCommand("theme.nord");
        shell.RunCommand("terminal.zoomIn");
        shell.RunCommand("terminal.zoomIn");

        Assert.Equal("nord", settings.TerminalTheme);
        Assert.Equal(TerminalThemes.DefaultFontSize + 2, settings.TerminalFontSize);

        shell.RunCommand("terminal.zoomReset");
        Assert.Equal(TerminalThemes.DefaultFontSize, shell.TerminalFontSize);

        for (var i = 0; i < 100; i++)
        {
            shell.RunCommand("terminal.zoomOut");
        }

        Assert.Equal(TerminalThemes.MinFontSize, shell.TerminalFontSize);
    }

    [Fact]
    public void Saved_theme_and_font_size_are_restored_and_broken_values_repaired()
    {
        var shell = TestShell.Create(out _, new AppSettings { TerminalTheme = "gruvbox-dark", TerminalFontSize = 18 });
        Assert.Equal("Gruvbox Dark", shell.TerminalTheme.Name);
        Assert.Equal(18, shell.TerminalFontSize);

        var repaired = new AppSettings { TerminalTheme = "neon-pink", TerminalFontSize = 400 }.Sanitize();
        Assert.Equal(TerminalThemes.DefaultId, repaired.TerminalTheme);
        Assert.Equal(TerminalThemes.MaxFontSize, repaired.TerminalFontSize);
    }

    [Fact]
    public void Shortcuts_are_unique_and_handed_to_the_terminal_with_their_command()
    {
        var shell = TestShell.Create(out _);

        var shortcuts = shell.TerminalShortcuts;

        Assert.Equal(shortcuts.Count, shortcuts.Select(shortcut => (shortcut.Code, shortcut.Shift)).Distinct().Count());
        Assert.Contains(new TerminalShortcut("KeyP", true, "view.palette"), shortcuts);
        Assert.Contains(new TerminalShortcut("Tab", false, "session.next"), shortcuts);
        Assert.Contains(new TerminalShortcut("Equal", false, "terminal.zoomIn"), shortcuts);
        Assert.All(shortcuts, shortcut => Assert.Contains(shell.Commands, command => command.Id == shortcut.Id));

        // Strg+C, Strg+V und die Tasten der Shell (Strg+R, Strg+W, Strg+D …) bleiben dem Terminal.
        Assert.DoesNotContain(shortcuts, shortcut => !shortcut.Shift && shortcut.Code.StartsWith("Key", StringComparison.Ordinal));
        Assert.DoesNotContain(shortcuts, shortcut => shortcut.Code is "KeyC" or "KeyV");
    }

    [Fact]
    public void Shortcuts_read_in_the_language_of_the_interface()
    {
        L.Init(AppLanguage.German);
        Assert.Equal("Strg+Umschalt+F", new KeyChord("KeyF").Display);
        Assert.Equal("Strg+0", new KeyChord("Digit0", Shift: false).Display);

        L.Init(AppLanguage.English);
        Assert.Equal("Ctrl++", new KeyChord("Equal", Shift: false).Display);
        Assert.Equal("Ctrl+Shift+Tab", new KeyChord("Tab").Display);
    }

    [Fact]
    public void Every_theme_is_complete()
    {
        Assert.Equal(TerminalThemes.All.Count, TerminalThemes.All.Select(theme => theme.Id).Distinct().Count());
        Assert.All(TerminalThemes.All, theme =>
        {
            Assert.Equal(16, theme.Ansi.Count);
            Assert.All(
                [theme.Background, theme.Foreground, theme.Cursor, theme.Selection, .. theme.Ansi],
                color => Assert.Matches("^#[0-9a-f]{6}$", color));
        });
        Assert.Same(TerminalThemes.All[0], TerminalThemes.Find("unbekannt"));
        Assert.Same(TerminalThemes.All[0], TerminalThemes.Find(null));
    }

    [Fact]
    public void The_default_theme_is_pure_black_and_the_one_before_it_stays_available()
    {
        var black = TerminalThemes.Find(TerminalThemes.DefaultId);
        Assert.Same(TerminalThemes.All[0], black);
        Assert.Equal(("#000000", "#000000"), (black.Background, black.Ansi[0]));

        // Wer Campbell schon in den Einstellungen stehen hat, behält es.
        Assert.Equal("#0c0c0c", TerminalThemes.Find("campbell").Background);
        Assert.Equal("campbell", new AppSettings { TerminalTheme = "campbell" }.Sanitize().TerminalTheme);
    }
}
