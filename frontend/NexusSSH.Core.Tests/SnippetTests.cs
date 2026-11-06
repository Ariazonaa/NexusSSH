using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Gespeicherte Befehle (Snippets): anlegen, in der Palette finden, ins aktive Terminal schicken.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SnippetTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);

    private readonly ScriptedBackend _backend = new(Profile);

    private ShellViewModel Connected()
    {
        var shell = TestShell.Create(_backend);
        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        return shell;
    }

    [Fact]
    public void A_snippet_is_typed_into_the_active_terminal_and_run_only_if_it_says_so()
    {
        var shell = Connected();

        Assert.True(shell.RunSnippet(new Snippet("logs", "journalctl -fu nginx", Run: true)));
        Assert.True(shell.RunSnippet(new Snippet("edit", "sudo nano ", Run: false)));

        Assert.Equal(["journalctl -fu nginx\r", "sudo nano "], _backend.Last.Input);
    }

    [Fact]
    public void Several_lines_are_sent_like_several_presses_of_enter()
    {
        var shell = Connected();

        shell.RunSnippet(new Snippet("deploy", "cd /srv/app\r\ngit pull\nmake\n", Run: true));
        shell.RunSnippet(new Snippet("half", "echo one\necho two", Run: false));

        Assert.Equal(["cd /srv/app\rgit pull\rmake\r", "echo one\recho two"], _backend.Last.Input);
    }

    [Fact]
    public void Without_a_connected_session_nothing_is_sent()
    {
        var shell = TestShell.Create(_backend);
        var snippet = new Snippet("logs", "tail -f app.log", Run: true);
        Assert.False(shell.RunSnippet(snippet));

        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Authenticating));
        Assert.False(shell.RunSnippet(snippet), "sonst landete der Befehl im Passwortfeld des Servers");
        Assert.Empty(_backend.Last.Input);
    }

    [Fact]
    public void Snippets_are_kept_in_the_settings_and_blank_ones_are_dropped()
    {
        var shell = TestShell.Create(_backend);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.SetSnippets([new Snippet("logs", "tail -f app.log", true), new Snippet("  ", "x", true), new Snippet("empty", string.Empty, false)]);

        Assert.Equal(["logs"], shell.Snippets.Select(snippet => snippet.Name));
        Assert.Same(shell.Settings.Snippets, shell.Snippets);
        Assert.Contains(nameof(ShellViewModel.Snippets), changed);
    }

    [Fact]
    public void The_palette_offers_snippets_only_to_a_connected_terminal()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets([new Snippet("logs", "journalctl -fu nginx\n# second line", true)]);
        Assert.DoesNotContain(shell.SearchPalette("logs"), item => item.Kind == PaletteKind.Snippet);

        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        var item = Assert.Single(shell.SearchPalette("logs"), item => item.Kind == PaletteKind.Snippet);
        Assert.Equal("Command: logs", item.Title);
        Assert.Equal("journalctl -fu nginx…", item.Detail);
        item.Run();
        Assert.Equal(["journalctl -fu nginx\r# second line\r"], _backend.Last.Input);

        // Auch der Text des Befehls wird durchsucht, und mit „>“ stehen die Befehle mit in der Liste.
        Assert.Contains(shell.SearchPalette("journalctl"), found => found.Kind == PaletteKind.Snippet);
        Assert.Contains(shell.SearchPalette(">"), found => found.Kind == PaletteKind.Snippet);
    }

    [Fact]
    public void A_long_snippet_is_shortened_in_the_palette()
    {
        var shell = Connected();
        shell.SetSnippets([new Snippet("long", new string('x', 200), false)]);

        var item = Assert.Single(shell.SearchPalette("long"), item => item.Kind == PaletteKind.Snippet);

        Assert.Equal(new string('x', 60) + "…", item.Detail);
    }

    [Fact]
    public void Managing_snippets_identities_and_known_servers_leads_to_their_page_of_the_home_page()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        shell.IsSettingsOpen = true;

        Assert.True(shell.RunCommand("snippets.manage"));

        // Die Startseite ist „kein Tab aktiv“ – die Sitzung läuft weiter –, und die Einstellungen liegen nicht darüber.
        Assert.Equal(HomePage.Snippets, shell.HomePage);
        Assert.Null(shell.ActiveSession);
        Assert.False(shell.IsSettingsOpen);
        Assert.Contains(session, shell.Sessions);

        Assert.True(shell.RunCommand("home.identities"));
        Assert.Equal(HomePage.Identities, shell.HomePage);

        // Die bekannten Server werden erst gelesen, wenn ihre Seite aufgeht.
        Assert.Empty(shell.KnownHosts);
        Assert.True(shell.RunCommand("home.knownHosts"));
        Assert.Equal(HomePage.KnownHosts, shell.HomePage);
        Assert.NotEmpty(shell.KnownHosts);

        // „Startseite“ führt von einer Verwaltung zurück zu den Servern, auch ohne aktiven Tab.
        Assert.True(shell.RunCommand("view.home"));
        Assert.Equal(HomePage.Servers, shell.HomePage);
        Assert.False(shell.RunCommand("view.home"));
    }

    [Fact]
    public void The_editor_says_when_something_changed_so_a_page_can_save_at_once()
    {
        var editor = new SnippetsEditorViewModel([new Snippet("uptime", "uptime", Run: true)]);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.Selected = editor.Rows[0];
        Assert.Equal(0, changes);

        editor.Rows[0].Text = "uptime -p";
        var added = editor.Add();
        added.Name = "disk";
        added.Text = "df -h";
        Assert.Equal(4, changes);
        Assert.Equal(["uptime", "disk"], editor.ToSnippets().Select(snippet => snippet.Name));

        editor.RemoveSelected();
        Assert.Equal(5, changes);
    }

    [Fact]
    public void The_editor_adds_selects_and_removes_rows()
    {
        L.Init(AppLanguage.English);
        var editor = new SnippetsEditorViewModel([new Snippet("a", "echo a", true), new Snippet("b", "echo b", false)]);
        Assert.Equal("a", editor.Selected?.Name);
        Assert.True(editor.HasSelection);

        var added = editor.Add();
        Assert.Same(added, editor.Selected);
        Assert.Equal(("New command", string.Empty, true), (added.Name, added.Text, added.Run));

        editor.RemoveSelected();
        Assert.Equal("b", editor.Selected?.Name);
        editor.RemoveSelected();
        Assert.Equal("a", editor.Selected?.Name);
        editor.RemoveSelected();
        Assert.False(editor.HasSelection);
        editor.RemoveSelected();
        Assert.Empty(editor.Rows);
    }

    [Fact]
    public void The_editor_returns_what_was_typed_without_unfinished_rows()
    {
        var editor = new SnippetsEditorViewModel([new Snippet("a", "echo a", true)]);
        editor.Rows[0].Name = "  renamed ";
        editor.Rows[0].Text = "echo b\n";
        editor.Rows[0].Run = false;
        editor.Add();
        editor.Add().Text = "   ";

        Assert.Equal([new Snippet("renamed", "echo b\n", false)], editor.ToSnippets());
        Assert.Equal("  renamed ", editor.Rows[0].ToString());
    }

    [Fact]
    public void The_column_on_the_right_filters_by_name_and_command()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets([new("Platz prüfen", "df -h", true), new("nginx neu starten", "sudo systemctl restart nginx", true), new("Speicher", "free -h", true)]);
        Assert.Equal(3, shell.FilteredSnippets.Count);
        Assert.False(shell.HasNoSnippets);

        shell.SnippetFilter = "NGINX";
        Assert.Equal(["nginx neu starten"], shell.FilteredSnippets.Select(snippet => snippet.Name));

        // Mehrere Wörter müssen alle vorkommen – im Namen oder im Befehl.
        shell.SnippetFilter = "free speicher";
        Assert.Equal(["Speicher"], shell.FilteredSnippets.Select(snippet => snippet.Name));

        shell.SnippetFilter = "gibt es nicht";
        Assert.Empty(shell.FilteredSnippets);
        Assert.True(shell.HasNoSnippetMatch);

        // Neue Befehle stehen gleich in der Liste, gefiltert wie die übrigen.
        shell.SnippetFilter = "-h";
        shell.SetSnippets([.. shell.Snippets, new("Container", "docker ps", true)]);
        Assert.Equal(["Platz prüfen", "Speicher"], shell.FilteredSnippets.Select(snippet => snippet.Name));
        shell.SnippetFilter = "docker";
        Assert.Equal(["Container"], shell.FilteredSnippets.Select(snippet => snippet.Name));

        shell.SetSnippets([]);
        Assert.True(shell.HasNoSnippets);
        Assert.False(shell.HasNoSnippetMatch);
    }

    [Fact]
    public void Enter_in_the_search_runs_the_first_match_and_only_with_a_shell()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets([new("Speicher", "free -h", true), new("Platz", "df -h", true)]);
        List<string> changed = [];
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        // Ohne Sitzung ist die Spalte grau.
        Assert.False(shell.CanRunSnippets);
        Assert.True(shell.CannotRunSnippets);
        Assert.False(shell.RunFirstSnippet());

        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.True(shell.CanRunSnippets);
        Assert.Contains(nameof(ShellViewModel.CanRunSnippets), changed);

        shell.SnippetFilter = "df";
        Assert.True(shell.RunFirstSnippet());
        Assert.Equal(["df -h\r"], _backend.Last.Input);

        // Ein Konto nur für Dateien hat keine Shell.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected, FilesOnly: true));
        Assert.False(shell.CanRunSnippets);
        Assert.False(shell.RunFirstSnippet());
    }

    [Fact]
    public void The_pen_and_the_plus_open_the_editor_on_the_start_page()
    {
        var shell = TestShell.Create(_backend);
        var session = shell.OpenSession(Profile);
        shell.SetSnippets([new("a", "echo a", true), new("b", "echo b", true)]);
        var requests = 0;
        shell.SnippetEditRequested += () => requests++;

        shell.EditSnippet(shell.Snippets[1]);
        Assert.Equal(1, requests);
        Assert.Null(shell.ActiveSession);
        Assert.Equal(HomePage.Snippets, shell.HomePage);
        Assert.Equal(1, shell.TakeSnippetToEdit());
        Assert.Null(shell.TakeSnippetToEdit());

        // „+“: ein neuer Befehl. Die Sitzung läuft weiter.
        shell.EditSnippet(null);
        Assert.Equal(-1, shell.TakeSnippetToEdit());
        Assert.Contains(session, shell.Sessions);
    }
}
