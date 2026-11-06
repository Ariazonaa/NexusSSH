using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>„Zwei Spalten“: ein lokaler Ordner neben dem Dateibrowser des Servers.</summary>
[Collection(LanguageCollection.Name)]
public sealed class DualFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-dual-" + Guid.NewGuid().ToString("N"));

    public DualFilesTests()
    {
        L.Init(AppLanguage.English);
        Directory.CreateDirectory(Path.Combine(_root, "Zeta"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha", "inner"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello");
        File.WriteAllBytes(Path.Combine(_root, "Big.bin"), new byte[2048]);
        File.WriteAllText(Path.Combine(_root, ".secret"), "x");
        File.SetAttributes(Path.Combine(_root, ".secret"), FileAttributes.Hidden);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_local_list_goes_back_and_forward_like_the_servers()
    {
        var local = new LocalBrowserViewModel(_root);
        Assert.False(local.CanGoBack);

        local.NavigateTo(Path.Combine(_root, "alpha"));
        local.NavigateTo(Path.Combine(_root, "alpha", "inner"));
        local.Refresh(); // derselbe Ordner noch einmal ist kein Schritt
        Assert.True(local.CanGoBack);
        Assert.False(local.CanGoForward);

        local.GoBack();
        Assert.Equal(Path.Combine(_root, "alpha"), local.CurrentPath);
        Assert.True(local.CanGoForward);
        local.GoBack();
        Assert.Equal(_root, local.CurrentPath);
        Assert.False(local.CanGoBack);

        local.GoForward();
        Assert.Equal(Path.Combine(_root, "alpha"), local.CurrentPath);

        // Ein neuer Weg verwirft das „vor“.
        local.NavigateTo(Path.Combine(_root, "Zeta"));
        Assert.False(local.CanGoForward);
        local.GoBack();
        Assert.Equal(Path.Combine(_root, "alpha"), local.CurrentPath);
    }

    [Fact]
    public void Delete_forever_skips_the_recycle_bin()
    {
        var local = new LocalBrowserViewModel(_root) { Recycle = _ => throw new InvalidOperationException("recycled") };
        local.Delete(local.Entries.Where(entry => entry.Name == "notes.txt"), forever: true);

        Assert.False(File.Exists(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void The_local_folder_lists_folders_first_and_walks_in_and_out()
    {
        var local = new LocalBrowserViewModel(_root);

        Assert.Equal(_root, local.CurrentPath);
        Assert.Equal(["alpha", "Zeta", "Big.bin", "notes.txt"], local.Entries.Select(entry => entry.Name));
        Assert.Equal("4 entries", local.CountText);
        var big = local.Entries.Single(entry => entry.Name == "Big.bin");
        Assert.Equal((false, 2048L, "2.0 KB"), (big.IsDirectory, big.Size, big.SizeText));
        Assert.Equal(("This computer", LocalBrowserViewModel.Computer), (local.Segments[0].Name, local.Segments[0].FullPath));
        Assert.Equal(_root, local.Segments[^1].FullPath);

        local.Open(local.Entries.Single(entry => entry.Name == "alpha"));
        Assert.Equal(Path.Combine(_root, "alpha"), local.CurrentPath);
        Assert.Equal(["inner"], local.Entries.Select(entry => entry.Name));

        // Eine Datei zu „öffnen“ wechselt nirgendwohin.
        local.GoUp();
        local.Open(local.Entries.Single(entry => entry.Name == "notes.txt"));
        Assert.Equal(_root, local.CurrentPath);

        // Verstecktes auf Wunsch.
        local.ShowHidden = true;
        Assert.Contains(".secret", local.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_leaves_the_view_where_it_was_and_says_why()
    {
        var local = new LocalBrowserViewModel(_root);

        Assert.False(local.NavigateTo(Path.Combine(_root, "gone")));

        Assert.Equal(_root, local.CurrentPath);
        Assert.True(local.HasError);
        Assert.Equal(4, local.Entries.Count);

        Assert.True(local.NavigateTo(Path.Combine(_root, "Zeta")));
        Assert.False(local.HasError);
    }

    [Fact]
    public void Above_the_top_folder_stand_the_drives_and_nothing_can_be_downloaded_there()
    {
        var local = new LocalBrowserViewModel(_root);
        while (local.CanGoUp)
        {
            local.GoUp();
        }

        Assert.Equal(LocalBrowserViewModel.Computer, local.CurrentPath);
        Assert.False(local.IsFolder);
        Assert.NotEmpty(local.Entries);
        Assert.All(local.Entries, entry => Assert.True(entry is { IsDrive: true, IsDirectory: true }));
        Assert.Single(local.Segments);

        local.Open(local.Entries[0]);
        Assert.True(local.IsFolder);
    }

    [Fact]
    public void The_view_is_an_option_that_replaces_the_narrow_file_pane_and_covers_the_terminal()
    {
        var settings = new AppSettings { LocalFolder = _root };
        var shell = TestShell.Create(out _, settings);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.IsSftpVisible = true;

        // Ohne Sitzung gibt es nichts zu zeigen, auch eingeschaltet.
        Assert.True(shell.RunCommand("view.dualFiles"));
        Assert.True(settings.DualFiles);
        Assert.False(shell.ShowsDualFiles);
        Assert.Contains(nameof(ShellViewModel.ShowsDualFiles), changed);

        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        Assert.Equal((true, false, false), (shell.ShowsDualFiles, shell.ShowsTerminal, shell.ShowsFiles));
        Assert.Equal(_root, shell.Local.CurrentPath);

        // Der schmale Bereich zählt dann auch nicht als Bereich für Strg+F6.
        Assert.NotEqual(ShellPane.Files, shell.PaneAfter(null, +1));

        shell.IsDualFiles = false;
        Assert.Equal((false, true, true), (shell.ShowsDualFiles, shell.ShowsTerminal, shell.ShowsFiles));

        // Wohin die linke Spalte zeigt, merkt sich die App für das nächste Mal.
        shell.Local.NavigateTo(Path.Combine(_root, "alpha"));
        Assert.Equal(Path.Combine(_root, "alpha"), settings.LocalFolder);
    }

    [Fact]
    public void The_local_list_is_sorted_by_its_own_choice_with_folders_always_first()
    {
        var shell = TestShell.Create(out _, new AppSettings { LocalFolder = _root });
        var local = shell.Local;
        local.ShowHidden = false;

        shell.LocalColumns.SortBy(FileColumn.Size);
        Assert.Equal(["alpha", "Zeta", "notes.txt", "Big.bin"], local.Entries.Select(entry => entry.Name));

        shell.LocalColumns.SortBy(FileColumn.Size);
        Assert.Equal(["alpha", "Zeta", "Big.bin", "notes.txt"], local.Entries.Select(entry => entry.Name));

        shell.LocalColumns.SortBy(FileColumn.Name);
        Assert.Equal(["alpha", "Zeta", "Big.bin", "notes.txt"], local.Entries.Select(entry => entry.Name));
        shell.LocalColumns.SortBy(FileColumn.Name);
        Assert.Equal(["Zeta", "alpha", "notes.txt", "Big.bin"], local.Entries.Select(entry => entry.Name));

        // Die Sortierung des Servers daneben ändert daran nichts.
        shell.FileSort = FileSort.Size;
        Assert.Equal(["Zeta", "alpha", "notes.txt", "Big.bin"], local.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void Folders_can_be_created_and_entries_renamed_but_never_over_something_that_exists()
    {
        var local = new LocalBrowserViewModel(_root);

        Assert.True(local.CreateFolder(" new "));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
        Assert.Contains("new", local.Entries.Select(entry => entry.Name));

        // Was es schon gibt, bleibt – und die Ansicht sagt, warum nichts geschah.
        Assert.False(local.CreateFolder("notes.txt"));
        Assert.True(local.HasError);
        Assert.False(local.CreateFolder("a/b"));
        Assert.False(local.CreateFolder(".."));

        var notes = local.Entries.Single(entry => entry.Name == "notes.txt");
        Assert.False(local.Rename(notes, "Big.bin"));
        Assert.Equal(2048, new FileInfo(Path.Combine(_root, "Big.bin")).Length);
        Assert.False(local.Rename(notes, "..\\escaped.txt"));

        Assert.True(local.Rename(notes, "todo.txt"));
        Assert.False(local.HasError);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "todo.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "notes.txt")));

        Assert.True(local.Rename(local.Entries.Single(entry => entry.Name == "alpha"), "beta"));
        Assert.True(Directory.Exists(Path.Combine(_root, "beta", "inner")));
    }

    [Fact]
    public void Deleting_goes_to_the_recycle_bin_where_there_is_one_and_never_touches_a_drive()
    {
        var local = new LocalBrowserViewModel(_root);
        var drive = new LocalEntry("C:\\", Path.GetPathRoot(_root)!, true, 0, null, IsDrive: true);
        Assert.False(local.Delete([drive]));
        Assert.False(local.Rename(drive, "x"));

        // Mit Papierkorb: Die Oberfläche bekommt die Pfade, hier wird nichts selbst gelöscht.
        var recycled = new List<string>();
        local.Recycle = paths =>
        {
            recycled.AddRange(paths);
            return true;
        };
        Assert.True(local.Delete(local.Entries.Where(entry => entry.Name is "notes.txt" or "alpha").Append(drive)));
        Assert.Equal([Path.Combine(_root, "alpha"), Path.Combine(_root, "notes.txt")], recycled);
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));

        // Lehnt der Papierkorb ab, steht das da.
        local.Recycle = _ => false;
        Assert.False(local.Delete(local.Entries.Where(entry => entry.Name == "notes.txt")));
        Assert.True(local.HasError);

        // Ohne Papierkorb: endgültig, Ordner samt Inhalt.
        local.Recycle = null;
        Assert.True(local.Delete(local.Entries.Where(entry => entry.Name is "notes.txt" or "alpha")));
        Assert.False(local.HasError);
        Assert.Equal(["Zeta", "Big.bin"], local.Entries.Select(entry => entry.Name));
        Assert.False(Directory.Exists(Path.Combine(_root, "alpha")));
    }

    [Fact]
    public async Task Uploads_go_to_the_folder_the_server_shows_and_downloads_to_the_local_one()
    {
        var shell = TestShell.Create(out _, new AppSettings { LocalFolder = _root, DualFiles = true });
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        await session.Files.LoadAsync();
        var files = (MockRemoteFileSystem)session.Files.FileSystem!;

        // Ohne Auswahl geschieht nichts; Laufwerke lassen sich nicht hochladen.
        Assert.False(shell.UploadFromLocal([]));
        Assert.False(shell.UploadFromLocal([new LocalEntry("C:\\", "C:\\", true, 0, null, IsDrive: true)]));

        Assert.True(shell.UploadFromLocal(shell.Local.Entries.Where(entry => entry.Name is "notes.txt" or "alpha")));
        Assert.Equal(
            [(Path.Combine(_root, "alpha"), session.Files.CurrentPath), (Path.Combine(_root, "notes.txt"), session.Files.CurrentPath)],
            files.Uploads.Select(upload => (upload.LocalPath, upload.RemoteDirectory)));

        var wanted = session.Files.Entries.Where(entry => !entry.IsDirectory).Take(1).ToList();
        Assert.True(shell.DownloadToLocal(wanted));
        Assert.Equal((session.Files.PathOf(wanted[0]), _root), files.Downloads[^1]);

        // In die Liste der Laufwerke lässt sich nichts laden.
        shell.Local.NavigateTo(LocalBrowserViewModel.Computer);
        Assert.False(shell.DownloadToLocal(wanted));
    }
}
