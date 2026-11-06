using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Spalten der Dateilisten: wonach sortiert ist, welche zu sehen sind und wie breit sie sind.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FileColumnsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusssh-columns-" + Guid.NewGuid().ToString("N"));

    public FileColumnsTests() => L.Init(AppLanguage.English);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private AppSettings Load(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, json);
        return new SettingsStore(SettingsFile).Load();
    }

    private static string[] Visible(FileColumns columns) => [.. columns.Available.Where(columns.IsVisible).Select(column => column.ToString())];

    [Fact]
    public void A_click_on_a_column_sorts_by_it_and_a_second_click_turns_it_round()
    {
        var shell = TestShell.Create(out _);
        var changes = 0;
        shell.RemoteColumns.Changed += (_, _) => changes++;
        var session = shell.OpenSession(shell.Profiles[0]);

        shell.RemoteColumns.SortBy(FileColumn.Owner);
        Assert.Equal((FileSort.Owner, false), (shell.RemoteColumns.Sort, shell.RemoteColumns.Descending));
        Assert.Equal((FileSort.Owner, false), (session.Files.Sort, session.Files.SortDescending));

        shell.RemoteColumns.SortBy(FileColumn.Owner);
        Assert.Equal((FileSort.Owner, true), (shell.RemoteColumns.Sort, shell.RemoteColumns.Descending));
        Assert.True(session.Files.SortDescending);

        // Eine andere Spalte beginnt wieder aufsteigend.
        shell.RemoteColumns.SortBy(FileColumn.Size);
        Assert.Equal((FileSort.Size, false), (shell.RemoteColumns.Sort, shell.RemoteColumns.Descending));
        Assert.Equal(3, changes);

        // Die Wahl steht in den Einstellungen und gilt für jede neue Sitzung.
        Assert.Equal((FileSort.Size, false), (shell.Settings.FileSort, shell.Settings.FileSortDescending));
        Assert.Equal(FileSort.Size, shell.OpenSession(shell.Profiles[1]).Files.Sort);

        // Die Namen aus der alten Oberfläche der Shell zeigen dasselbe.
        Assert.Equal(FileSort.Size, shell.FileSort);
        shell.FileSortDescending = true;
        Assert.True(shell.RemoteColumns.Descending);
    }

    [Fact]
    public async Task Folders_stay_first_whatever_the_column()
    {
        var files = new MockRemoteFileSystem("/srv")
            .File("/srv/a.txt", 10, hoursAgo: 5)
            .File("/srv/b.tar.gz", 20, hoursAgo: 4)
            .File("/srv/c", 30, hoursAgo: 3)
            .File("/srv/d.sh", 40, hoursAgo: 2)
            .File("/srv/dirA/x", 1, hoursAgo: 1)
            .File("/srv/dirB/x", 1, hoursAgo: 1);
        await files.SetOwnerAsync("/srv/a.txt", "zed", "audio");
        await files.SetOwnerAsync("/srv/b.tar.gz", "adm", "wheel");
        await files.SetOwnerAsync("/srv/dirA", "zed", "audio");
        await files.SetOwnerAsync("/srv/dirB", "adm", "wheel");
        await files.SetPermissionsAsync("/srv/a.txt", 0b111_000_000);
        await files.SetPermissionsAsync("/srv/b.tar.gz", 0b100_000_000);
        await files.SetPermissionsAsync("/srv/dirB", 0b111_111_111);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        string[] Order(FileSort sort, bool descending)
        {
            (browser.Sort, browser.SortDescending) = (sort, descending);
            var names = browser.Entries.Select(entry => entry.Name).ToArray();
            Assert.Equal(
                browser.Entries.Select(entry => entry.IsDirectory),
                browser.Entries.Select(entry => entry.IsDirectory).OrderByDescending(isDirectory => isDirectory));
            return names;
        }

        // Ordner stehen immer vorn; bei gleichem Wert bleibt die Reihenfolge des Servers.
        Assert.Equal(["dirB", "dirA", "b.tar.gz", "c", "d.sh", "a.txt"], Order(FileSort.Owner, false));
        Assert.Equal(["dirA", "dirB", "a.txt", "c", "d.sh", "b.tar.gz"], Order(FileSort.Owner, true));
        Assert.Equal(["dirA", "dirB", "a.txt", "c", "d.sh", "b.tar.gz"], Order(FileSort.Group, false));
        Assert.Equal(["dirB", "dirA", "b.tar.gz", "c", "d.sh", "a.txt"], Order(FileSort.Group, true));

        // Nach den Rechten als Zahl: 0400 vor 0644 vor 0700, bei den Ordnern 0755 vor 0777.
        Assert.Equal(["dirA", "dirB", "b.tar.gz", "c", "d.sh", "a.txt"], Order(FileSort.Permissions, false));
        Assert.Equal(["dirB", "dirA", "a.txt", "c", "d.sh", "b.tar.gz"], Order(FileSort.Permissions, true));

        // Nach der Art: Ordner, dann „File“ (ohne Endung), „GZ file“, „SH file“, „TXT file“.
        Assert.Equal(["dirA", "dirB", "c", "b.tar.gz", "d.sh", "a.txt"], Order(FileSort.Type, false));
        Assert.Equal(["dirA", "dirB", "a.txt", "d.sh", "b.tar.gz", "c"], Order(FileSort.Type, true));
    }

    [Fact]
    public void The_type_is_the_kind_or_the_ending()
    {
        string Remote(string name, bool directory = false, bool link = false, bool special = false, bool dangling = false)
            => Format.EntryType(new RemoteEntry(name, directory, 0, DateTimeOffset.MinValue, IsLink: link, IsSpecial: special, IsDangling: dangling));

        Assert.Equal("GZ file", Remote("a.tar.gz"));
        Assert.Equal("TXT file", Remote("Notes.txt"));
        Assert.Equal("File", Remote(".bashrc"));
        Assert.Equal("File", Remote("Makefile"));
        Assert.Equal("File", Remote("archive."));
        Assert.Equal("Folder", Remote("src", directory: true));

        // Als Endung gilt nur ein kurzes Wort aus Buchstaben und Ziffern – der Name kommt vom Server.
        Assert.Equal("12345678 file", Remote("x.12345678"));
        Assert.Equal("File", Remote("x.123456789"));
        Assert.Equal("File", Remote("x.a b"));
        Assert.Equal("File", Remote("x.\u202Etxt"));
        Assert.Equal("File", Remote("x.ä"));

        // Jeder symbolische Link ist ein Link – gleich worauf er zeigt (wie im Eigenschaften-Dialog), auch auf ein Gerät.
        Assert.Equal("Symbolic link", Remote("current", directory: true, link: true));
        Assert.Equal("Symbolic link", Remote("latest.log", link: true));
        Assert.Equal("Symbolic link", Remote("gone", link: true, special: true, dangling: true));
        Assert.Equal("Symbolic link", Remote("unknown", link: true, special: true));
        Assert.Equal("Special file (pipe, device or socket)", Remote("queue.fifo", special: true));

        var local = (string name, bool directory, bool drive) => Format.EntryType(new LocalEntry(name, name, directory, 0, null, drive));
        Assert.Equal("ZIP file", local("a.zip", false, false));
        Assert.Equal("Folder", local("src", true, false));
        Assert.Equal(string.Empty, local("C:\\", true, true));

        // Der Text ist übersetzt.
        L.Init(AppLanguage.German);
        Assert.Equal("ZIP-Datei", Remote("a.zip"));
        Assert.Equal("Ordner", Remote("src", directory: true));
        L.Init(AppLanguage.French);
        Assert.Equal("Fichier ZIP", Remote("a.zip"));
        L.Init(AppLanguage.Spanish);
        Assert.Equal("Archivo ZIP", Remote("a.zip"));
    }

    [Fact]
    public async Task Links_stand_together_when_sorted_by_type_whatever_they_point_at()
    {
        var files = new MockRemoteFileSystem("/srv")
            .File("/srv/a.txt", 10, hoursAgo: 2)
            .File("/srv/dirA/x", 1, hoursAgo: 1)
            .Link("/srv/b-link", "a.txt")
            .Link("/srv/dirlink", "dirA")
            .Link("/srv/dead", "nowhere");
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        // Nach der Art stehen nur echte Ordner vorn; ein Link auf einen Ordner, einer auf eine Datei und einer ins Leere
        // heißen gleich und stehen beieinander – aufsteigend wie absteigend (bei gleicher Art bleibt die Reihenfolge des Servers).
        browser.Sort = FileSort.Type;
        Assert.Equal(["dirA", "dirlink", "b-link", "dead", "a.txt"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal(
            ["Folder", "Symbolic link", "Symbolic link", "Symbolic link", "TXT file"],
            browser.Entries.Select(entry => entry.TypeText));

        browser.SortDescending = true;
        Assert.Equal(["dirA", "a.txt", "dirlink", "b-link", "dead"], browser.Entries.Select(entry => entry.Name));

        // Nach jeder anderen Spalte bleibt ein Link auf einen Ordner bei den Ordnern: Man kann ihn betreten.
        browser.SortDescending = false;
        browser.Sort = FileSort.Name;
        Assert.Equal(["dirA", "dirlink", "a.txt", "b-link", "dead"], browser.Entries.Select(entry => entry.Name));
        browser.Sort = FileSort.Size;
        Assert.Equal(["dirA", "dirlink", "dead", "a.txt", "b-link"], browser.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void Widths_are_kept_within_bounds_and_survive_the_settings_file()
    {
        var shell = TestShell.Create(out _);
        var columns = shell.RemoteColumns;
        Assert.Equal((40.0, 480.0), (FileColumns.MinWidth, FileColumns.MaxWidth));

        columns.SetWidth(FileColumn.Size, 10);
        Assert.Equal(40, columns.WidthOf(FileColumn.Size));
        columns.SetWidth(FileColumn.Size, 9999);
        Assert.Equal(480, columns.WidthOf(FileColumn.Size));
        columns.SetWidth(FileColumn.Size, double.NaN);
        Assert.Equal(480, columns.WidthOf(FileColumn.Size));
        columns.SetWidth(FileColumn.Modified, 133.5);

        // Was der Benutzer nie angefasst hat, hat die Breite, die die Spalte von Haus aus hat.
        Assert.InRange(columns.WidthOf(FileColumn.Owner), FileColumns.MinWidth, FileColumns.MaxWidth);

        Directory.CreateDirectory(_directory);
        var store = new SettingsStore(SettingsFile);
        store.Save(shell.Settings);
        var loaded = store.Load();
        var again = TestShell.Create(out _, loaded);

        Assert.Equal(480, again.RemoteColumns.WidthOf(FileColumn.Size));
        Assert.Equal(133.5, again.RemoteColumns.WidthOf(FileColumn.Modified));
        Assert.Equal(columns.WidthOf(FileColumn.Owner), again.RemoteColumns.WidthOf(FileColumn.Owner));

        // Eine von Hand geänderte Datei bringt keine Breite aus dem Rahmen.
        var edited = new AppSettings { RemoteColumns = new ColumnSet(["Size"], new() { ["Size"] = 5, ["Modified"] = 1e9, ["Type"] = double.PositiveInfinity }) }.Sanitize();
        Assert.Equal(40, edited.RemoteColumns!.Widths["Size"]);
        Assert.Equal(480, edited.RemoteColumns.Widths["Modified"]);
        Assert.False(edited.RemoteColumns.Widths.ContainsKey("Type"));
    }

    [Fact]
    public void Columns_can_be_switched_and_the_name_is_always_there()
    {
        var shell = TestShell.Create(out _);
        var columns = shell.RemoteColumns;
        var changes = 0;
        columns.Changed += (_, _) => changes++;

        // Von Haus aus: Name und Größe, wie bisher.
        Assert.Equal(["Name", "Size"], Visible(columns));
        Assert.Equal(
            [FileColumn.Name, FileColumn.Size, FileColumn.Modified, FileColumn.Permissions, FileColumn.Owner, FileColumn.Group, FileColumn.Type],
            columns.Available);

        columns.Toggle(FileColumn.Owner);
        columns.Toggle(FileColumn.Modified);
        Assert.Equal(["Name", "Size", "Modified", "Owner"], Visible(columns));
        Assert.Equal(["Size", "Modified", "Owner"], shell.Settings.RemoteColumns!.Visible);
        columns.Toggle(FileColumn.Size);
        Assert.Equal(["Name", "Modified", "Owner"], Visible(columns));
        Assert.Equal(3, changes);

        // Der Name lässt sich nicht abschalten.
        columns.Toggle(FileColumn.Name);
        Assert.True(columns.IsVisible(FileColumn.Name));
        Assert.Equal(3, changes);

        // Wer nach einer ausgeblendeten Spalte sortiert, bleibt dabei – das Sortieren hängt nicht an der Anzeige.
        columns.SortBy(FileColumn.Size);
        Assert.Equal(FileSort.Size, columns.Sort);

        // Die alten Schalter der Shell sind dieselbe Wahl.
        Assert.True(shell.ShowFileDates);
        Assert.False(shell.ShowFilePermissions);
        shell.ShowFilePermissions = true;
        Assert.True(columns.IsVisible(FileColumn.Permissions));
        shell.ShowFileDates = false;
        Assert.False(columns.IsVisible(FileColumn.Modified));
    }

    [Fact]
    public void Old_switches_for_date_and_rights_become_visible_columns()
    {
        var settings = Load("""{ "ShowFileDates": true, "ShowFilePermissions": true, "FileSort": "Modified", "FileSortDescending": true }""");

        Assert.Equal(["Size", "Modified", "Permissions"], settings.RemoteColumns!.Visible);
        var shell = TestShell.Create(out _, settings);
        Assert.Equal(["Name", "Size", "Modified", "Permissions"], Visible(shell.RemoteColumns));
        Assert.True(shell.ShowFileDates);
        Assert.True(shell.ShowFilePermissions);
        Assert.Equal((FileSort.Modified, true), (shell.RemoteColumns.Sort, shell.RemoteColumns.Descending));

        // Nur der Schalter für das Datum.
        Assert.Equal(["Size", "Modified"], Load("""{ "ShowFileDates": true }""").RemoteColumns!.Visible);

        // Ohne alte Schalter gilt, was die App immer zeigte: die Größe.
        Assert.Equal(["Size"], Load("{}").RemoteColumns!.Visible);

        // Steht beides in der Datei, gilt die neue Wahl.
        Assert.Equal(
            ["Owner"],
            Load("""{ "ShowFileDates": true, "RemoteColumns": { "Visible": ["Owner"] } }""").RemoteColumns!.Visible);

        // Die Schalter sind danach verbraucht: Eine neu gespeicherte Datei zeigt sie nicht noch einmal.
        var store = new SettingsStore(SettingsFile);
        store.Save(settings);
        Assert.False(store.Load().ShowFileDates);
    }

    [Fact]
    public void A_settings_file_cannot_name_a_column_that_does_not_exist()
    {
        var settings = Load("""{ "RemoteColumns": { "Visible": ["evil", "Size", "Name", "Owner", "Owner", "3", "owner", null], "Widths": { "evil": 100, "Size": 99999, "7": 50 } } }""");

        Assert.Equal(["Size", "Owner"], settings.RemoteColumns!.Visible);
        Assert.Equal(["Size"], [.. settings.RemoteColumns.Widths.Keys]);
        Assert.Equal(480, settings.RemoteColumns.Widths["Size"]);

        // Fehlende oder leere Teile ergeben die Vorgabe bzw. nichts, nie eine Ausnahme.
        Assert.Equal(["Size"], Load("""{ "RemoteColumns": { } }""").RemoteColumns!.Visible);
        Assert.Equal(["Size"], Load("""{ "RemoteColumns": null }""").RemoteColumns!.Visible);
        Assert.Empty(Load("""{ "RemoteColumns": { "Visible": [] } }""").RemoteColumns!.Visible);

        // Links gibt es weder Rechte noch Besitzer noch Gruppe.
        var local = Load("""{ "LocalColumns": { "Visible": ["Owner", "Group", "Permissions", "Type", "Modified"], "Widths": { "Owner": 90, "Type": 90 } } }""").LocalColumns!;
        Assert.Equal(["Modified", "Type"], local.Visible);
        Assert.Equal(["Type"], [.. local.Widths.Keys]);
        Assert.Equal(["Size", "Modified"], Load("{}").LocalColumns!.Visible);

        // Eine Sortierung, die es nicht gibt, fällt auf den Namen zurück.
        var sorted = new AppSettings { FileSort = (FileSort)99, LocalSort = (FileSort)99 }.Sanitize();
        Assert.Equal((FileSort.Name, FileSort.Name), (sorted.FileSort, sorted.LocalSort));
        Assert.Equal(FileSort.Name, new AppSettings { LocalSort = FileSort.Owner }.Sanitize().LocalSort);
        Assert.Equal(FileSort.Owner, new AppSettings { FileSort = FileSort.Owner }.Sanitize().FileSort);

        // Auch ein Shell über Einstellungen, die niemand bereinigt hat, kommt damit zurecht.
        var shell = TestShell.Create(out _, new AppSettings { RemoteColumns = new ColumnSet(null!, null!), LocalColumns = new ColumnSet(["evil"], []), LocalSort = FileSort.Group });
        Assert.Equal(["Name", "Size"], Visible(shell.RemoteColumns));
        Assert.Equal(["Name"], Visible(shell.LocalColumns));
        Assert.Equal(FileSort.Name, shell.LocalColumns.Sort);
    }

    [Fact]
    public void The_local_list_sorts_on_its_own_and_has_no_owner_column()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "folder"));
        File.WriteAllBytes(Path.Combine(_directory, "big.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(_directory, "small.txt"), new byte[5]);
        File.WriteAllBytes(Path.Combine(_directory, "notes.md"), new byte[500]);
        var shell = TestShell.Create(out _, new AppSettings { LocalFolder = _directory });
        var session = shell.OpenSession(shell.Profiles[0]);

        Assert.Equal(
            [FileColumn.Name, FileColumn.Size, FileColumn.Modified, FileColumn.Type],
            shell.LocalColumns.Available);
        Assert.Equal(["Name", "Size", "Modified"], Visible(shell.LocalColumns));

        // Besitzer, Gruppe und Rechte kennt die lokale Liste nicht – sie lassen sich weder zuschalten noch wählen.
        shell.LocalColumns.Toggle(FileColumn.Owner);
        shell.LocalColumns.SortBy(FileColumn.Permissions);
        Assert.Equal(["Name", "Size", "Modified"], Visible(shell.LocalColumns));
        Assert.Equal(FileSort.Name, shell.LocalColumns.Sort);
        Assert.False(shell.LocalColumns.IsVisible(FileColumn.Group));

        // Die Wahl links ist nicht die Wahl rechts.
        shell.LocalColumns.SortBy(FileColumn.Size);
        Assert.Equal(["folder", "small.txt", "notes.md", "big.bin"], shell.Local.Entries.Select(entry => entry.Name));
        Assert.Equal(FileSort.Name, session.Files.Sort);
        Assert.Equal(FileSort.Size, shell.Settings.LocalSort);
        Assert.Equal(FileSort.Name, shell.Settings.FileSort);

        shell.RemoteColumns.SortBy(FileColumn.Owner);
        Assert.Equal(FileSort.Size, shell.Local.Sort);
        Assert.Equal(FileSort.Owner, session.Files.Sort);

        shell.LocalColumns.SortBy(FileColumn.Size);
        Assert.Equal(["folder", "big.bin", "notes.md", "small.txt"], shell.Local.Entries.Select(entry => entry.Name));
        Assert.True(shell.Settings.LocalSortDescending);

        shell.LocalColumns.SortBy(FileColumn.Type);
        Assert.Equal(["folder", "big.bin", "notes.md", "small.txt"], shell.Local.Entries.Select(entry => entry.Name));
        shell.LocalColumns.SortBy(FileColumn.Type);
        Assert.Equal(["folder", "small.txt", "notes.md", "big.bin"], shell.Local.Entries.Select(entry => entry.Name));

        // Die Wahl überlebt einen Neustart: Die Liste der nächsten Shell beginnt so.
        var next = TestShell.Create(out _, new AppSettings { LocalFolder = _directory, LocalSort = FileSort.Size, LocalSortDescending = true });
        Assert.Equal(["folder", "big.bin", "notes.md", "small.txt"], next.Local.Entries.Select(entry => entry.Name));
        Assert.Equal((FileSort.Size, true), (next.LocalColumns.Sort, next.LocalColumns.Descending));
    }

    [Fact]
    public async Task Names_sort_the_same_way_in_both_lists()
    {
        // Dieselben Namen wie im Test des Kerns (sftp.rs, names_sort_the_way_one_reads_them).
        string[] given =
        [
            "datei-10.log", "Datei-2.log", "datei-1.log", "datei-02.log", "datei-100.log", "datei.log", "v1.10", "v1.9",
            "Zebra", "apfel", "10", "9", "ä",
        ];
        string[] expected =
        [
            "9", "10", "apfel", "datei-1.log", "Datei-2.log", "datei-02.log", "datei-10.log", "datei-100.log",
            "datei.log", "v1.9", "v1.10", "Zebra", "ä",
        ];

        Assert.Equal(expected, given.Order(Format.NameOrder));
        Assert.Equal(expected, Enumerable.Reverse(given).Order(Format.NameOrder));

        // Groß und Klein gelten gleich, führende Nullen nicht; ein längerer Name steht hinter seinem Anfang.
        Assert.Equal(0, Format.CompareNames("abc", "ABC"));
        Assert.Equal(0, Format.CompareNames("a007", "a7"));
        Assert.True(Format.CompareNames("a", "a1") < 0);
        Assert.True(Format.CompareNames("datei9", "datei10") < 0);
        Assert.True(Format.CompareNames("datei10", "datei9") > 0);
        // Bei Gleichstand entscheidet der Name genau (groß vor klein) – wie im Kern, damit die Reihenfolge feststeht.
        Assert.True(Format.NameOrder.Compare("Datei-2.log", "datei-02.log") < 0);
        Assert.Equal(0, Format.NameOrder.Compare("abc", "abc"));

        // Verglichen werden Zeichen, nicht Einheiten von UTF-16: U+FF5E steht vor U+1F600, obwohl dessen erste Einheit kleiner ist.
        Assert.True(Format.CompareNames("\uFF5E", "\U0001F600") < 0);
        Assert.True(Format.NameOrder.Compare("\uFF5E", "\U0001F600") < 0);

        // Die lokale Liste
        Directory.CreateDirectory(_directory);
        foreach (var name in given)
        {
            File.WriteAllText(Path.Combine(_directory, name), string.Empty);
        }

        var local = new LocalBrowserViewModel(_directory);
        Assert.Equal(expected, local.Entries.Select(entry => entry.Name));
        local.SortDescending = true;
        Assert.Equal(Enumerable.Reverse(expected), local.Entries.Select(entry => entry.Name));

        // … und die Liste des Servers (hier das Beispiel-Dateisystem, das wie der Kern sortiert liefert).
        var files = new MockRemoteFileSystem("/srv");
        foreach (var name in given)
        {
            files.File("/srv/" + name, 1, hoursAgo: 1);
        }

        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        Assert.Equal(expected, browser.Entries.Select(entry => entry.Name));
    }

    // Die Zeilenvorlagen der Dateilisten (XAML: Tag und Ui.RemoteColumnWidth(n) usw.) nennen die Spalten als Zahl.
    [Fact]
    public void The_column_numbers_in_the_row_templates_are_the_values_of_the_enum()
    {
        Assert.Equal(0, (int)FileColumn.Name);
        Assert.Equal(1, (int)FileColumn.Size);
        Assert.Equal(2, (int)FileColumn.Modified);
        Assert.Equal(3, (int)FileColumn.Permissions);
        Assert.Equal(4, (int)FileColumn.Owner);
        Assert.Equal(5, (int)FileColumn.Group);
        Assert.Equal(6, (int)FileColumn.Type);
    }
}
