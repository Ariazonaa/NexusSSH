using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ordner der Snippets: in der Spalte, im Editor der Startseite, in den Einstellungen und im Import.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SnippetFolderTests : IDisposable
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);

    private readonly ScriptedBackend _backend = new(Profile);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusssh-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Absichtlich durcheinander: Ordner wechseln sich ab, einer ohne Ordner steht mittendrin.
    private static readonly Snippet[] Mixed =
    [
        new("update", "apt update", true, "Pakete"),
        new("df", "df -h", true, "Wartung"),
        new("loose", "uptime", true),
        new("upgrade", "apt full-upgrade", true, "Pakete"),
        new("trim", "fstrim -va", true, "Wartung"),
    ];

    private static string Shape(IEnumerable<SnippetItem> items)
        => string.Join(" ", items.Select(item => item.IsHeading ? $"[{item.Folder}{(item.IsCollapsed ? "+" : string.Empty)} {item.Count}]" : item.Name));

    [Fact]
    public void The_column_shows_loose_snippets_first_then_each_folder_under_its_heading()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets(Mixed);

        Assert.Equal("loose [Pakete 2] update upgrade [Wartung 2] df trim", Shape(shell.SnippetItems));
        Assert.Equal(["loose", "update", "upgrade", "df", "trim"], shell.FilteredSnippets.Select(snippet => snippet.Name));

        // Gespeichert bleibt die Reihenfolge, wie sie kam – geordnet wird beim Zeigen.
        Assert.Equal(Mixed, shell.Snippets);
    }

    [Fact]
    public void A_collapsed_folder_keeps_its_heading_hides_its_snippets_and_stays_collapsed()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets(Mixed);
        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        shell.ToggleSnippetFolder("Pakete");
        Assert.Equal("loose [Pakete+ 2] [Wartung 2] df trim", Shape(shell.SnippetItems));
        Assert.Equal(["Pakete"], shell.Settings.CollapsedSnippetFolders);

        // Enter in der Suche nimmt den ersten Befehl, der zu sehen ist – nie einen eingeklappten.
        shell.SnippetFilter = "apt";
        Assert.Equal("[Pakete+ 2]", Shape(shell.SnippetItems));
        Assert.False(shell.HasNoSnippetMatch, "die Überschrift steht da, also ist es kein „nichts passt“");
        Assert.False(shell.RunFirstSnippet());
        shell.SnippetFilter = string.Empty;

        shell.ToggleSnippetFolder("Pakete");
        Assert.Empty(shell.Settings.CollapsedSnippetFolders);
        shell.ToggleSnippetFolder("Wartung");

        // Fällt der Ordner weg, vergisst die Spalte ihn – ein neuer gleichen Namens wäre sonst gleich zu.
        shell.SetSnippets([.. Mixed.Where(snippet => snippet.Folder != "Wartung")]);
        Assert.Empty(shell.Settings.CollapsedSnippetFolders);
    }

    [Fact]
    public void Searching_shows_only_the_folders_with_a_match()
    {
        var shell = TestShell.Create(_backend);
        shell.SetSnippets(Mixed);

        shell.SnippetFilter = "upgrade";
        Assert.Equal("[Pakete 1] upgrade", Shape(shell.SnippetItems));

        shell.SnippetFilter = "nichts";
        Assert.Empty(shell.SnippetItems);
        Assert.True(shell.HasNoSnippetMatch);
    }

    [Fact]
    public void A_heading_reads_its_folder_count_and_state_to_a_screen_reader()
    {
        var before = L.Language;
        L.Init(AppLanguage.German);
        try
        {
            Assert.Equal("Ordner Wartung (3), eingeklappt", new SnippetItem(null, "Wartung", IsCollapsed: true, Count: 3).ToString());
            Assert.Equal("Ordner Wartung (1)", new SnippetItem(null, "Wartung", Count: 1).ToString());
            Assert.Equal("df", new SnippetItem(new Snippet("df", "df -h", true, "Wartung"), "Wartung").ToString());
        }
        finally
        {
            L.Init(before);
        }
    }

    [Fact]
    public void The_editor_lists_by_folder_and_marks_the_first_row_of_each()
    {
        var editor = new SnippetsEditorViewModel(Mixed);

        Assert.Equal(["loose", "update", "upgrade", "df", "trim"], editor.Rows.Select(row => row.Name));
        Assert.Equal(["", "Pakete", "", "Wartung", ""], editor.Rows.Select(row => row.Heading));
        Assert.Equal(["Pakete", "Wartung"], editor.FolderSuggestions(string.Empty));
        Assert.Equal(["Wartung"], editor.FolderSuggestions(" wart "));
    }

    [Fact]
    public void A_snippet_moves_only_within_its_folder()
    {
        var editor = new SnippetsEditorViewModel(Mixed);
        var saves = 0;
        editor.Changed += () => saves++;

        editor.Selected = editor.Rows.Single(row => row.Name == "update");
        Assert.False(editor.CanMoveUp, "über ihm steht ein Befehl ohne Ordner");
        Assert.True(editor.CanMoveDown);
        Assert.True(editor.MoveSelected(1));
        Assert.Equal(["loose", "upgrade", "update", "df", "trim"], editor.Rows.Select(row => row.Name));
        Assert.Equal("Pakete", editor.Rows[1].Heading);
        Assert.Equal(string.Empty, editor.Rows[2].Heading);

        Assert.False(editor.MoveSelected(1), "darunter beginnt ein anderer Ordner");
        Assert.Equal(1, saves);
        Assert.Equal(["loose", "upgrade", "update", "df", "trim"], editor.ToSnippets().Select(snippet => snippet.Name));
    }

    [Fact]
    public void Changing_the_folder_puts_the_snippet_last_in_it_and_a_new_folder_last()
    {
        var editor = new SnippetsEditorViewModel(Mixed);
        var loose = editor.Rows[0];

        editor.SetFolder(loose, "  Wartung ");
        Assert.Equal(["update", "upgrade", "df", "trim", "loose"], editor.Rows.Select(row => row.Name));
        Assert.Equal("Wartung", loose.Folder);

        var df = editor.Rows.Single(row => row.Name == "df");
        editor.SetFolder(df, "Info");
        Assert.Equal(["update", "upgrade", "trim", "loose", "df"], editor.Rows.Select(row => row.Name));
        Assert.Equal(["Pakete", "", "Wartung", "", "Info"], editor.Rows.Select(row => row.Heading));

        // Leer heißt ohne Ordner – der Befehl steht dann wieder oben.
        editor.SetFolder(df, string.Empty);
        Assert.Equal("df", editor.Rows[0].Name);
        Assert.Equal(new Snippet("df", "df -h", true), editor.ToSnippets()[0]);
    }

    [Fact]
    public void A_new_snippet_lands_in_the_folder_of_the_chosen_one()
    {
        var editor = new SnippetsEditorViewModel(Mixed);
        editor.Selected = editor.Rows.Single(row => row.Name == "update");

        var added = editor.Add();

        Assert.Equal("Pakete", added.Folder);
        Assert.Same(added, editor.Rows[3]);
        Assert.Same(added, editor.Selected);
    }

    [Fact]
    public void A_folder_name_loses_line_breaks_direction_marks_and_excess_length()
    {
        Assert.Equal("evil x", SnippetFolders.Clean(" ‮evil\n x\t"));
        Assert.Equal(string.Empty, SnippetFolders.Clean(null));
        Assert.Equal(SnippetFolders.MaxLength, SnippetFolders.Clean(new string('a', 500)).Length);
    }

    [Fact]
    public void Settings_from_before_folders_load_and_odd_folders_are_cleaned()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
              "Snippets": [
                { "Name": "old", "Text": "uptime", "Run": true },
                { "Name": "null", "Text": "df -h", "Run": true, "Folder": null },
                { "Name": "odd", "Text": "free -h", "Run": false, "Folder": " Wart‮ung\n" }
              ],
              "CollapsedSnippetFolders": [ "Wartung", "", "x\ny" ]
            }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.Equal([new Snippet("old", "uptime", true), new Snippet("null", "df -h", true), new Snippet("odd", "free -h", false, "Wartung")], settings.Snippets);
        Assert.Equal(["Wartung"], settings.CollapsedSnippetFolders);
    }

    [Fact]
    public void Folders_travel_in_an_export_and_come_back_cleaned()
    {
        var file = new ExportFile { Snippets = [new Snippet("trim", "fstrim -va", true, "Wartung‮\r\n")] };

        var plan = DataExchange.Plan(file, [], new AppSettings(), home: null, keyExists: _ => true);

        Assert.Equal([new Snippet("trim", "fstrim -va", true, "Wartung")], plan.Snippets);
    }
}
