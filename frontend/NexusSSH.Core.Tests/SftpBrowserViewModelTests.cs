using NexusSSH.Core.Mock;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

public sealed class SftpBrowserViewModelTests
{
    private static async Task<SftpBrowserViewModel> CreateAsync()
    {
        var files = new MockRemoteFileSystem("/var/www")
            .File("/var/www/html/index.html", 100, hoursAgo: 1)
            .File("/var/www/nginx.conf", 200, hoursAgo: 2)
            .File("/var/www/Assets/logo.svg", 300, hoursAgo: 3)
            .File("/etc/hosts", 10, hoursAgo: 4);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        return browser;
    }

    [Fact]
    public async Task Starts_in_the_initial_path_with_directories_first()
    {
        var browser = await CreateAsync();

        Assert.Equal("/var/www", browser.CurrentPath);
        Assert.Equal(["Assets", "html", "nginx.conf"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal(["/", "var", "www"], browser.Segments.Select(segment => segment.Name));
        Assert.False(browser.CanGoBack);
        Assert.True(browser.CanGoUp);
    }

    [Fact]
    public async Task Filling_the_list_hands_the_view_one_new_list_instead_of_a_change_per_entry()
    {
        // Bei 100 000 Einträgen stand die Ansicht sonst für jede einzelne Meldung still.
        var browser = await CreateAsync();
        var before = browser.Entries;
        var perEntry = 0;
        var replaced = 0;
        before.CollectionChanged += (_, _) => perEntry++;
        browser.PropertyChanged += (_, change) => replaced += change.PropertyName == nameof(SftpBrowserViewModel.Entries) ? 1 : 0;

        browser.Filter = "nginx";

        Assert.Equal(1, replaced);
        Assert.Equal(0, perEntry);
        Assert.NotSame(before, browser.Entries);
        Assert.Equal(["nginx.conf"], browser.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task The_way_to_a_node_of_the_tree_lists_its_ancestors_from_the_root()
    {
        var browser = await CreateAsync();
        browser.ShowTree = true;
        await browser.NavigateToAsync("/var/www/html");
        var node = Assert.IsType<DirectoryNode>(browser.CurrentNode);

        Assert.Equal(["/", "/var", "/var/www"], browser.TreePathTo(node).Select(ancestor => ancestor.Path));
        Assert.Empty(browser.TreePathTo(browser.TreeRoots[0]));

        // Ein Knoten, der nicht (mehr) in diesem Baum hängt, hat keinen Weg.
        Assert.Empty(browser.TreePathTo(new DirectoryNode("else", "/somewhere/else")));

        // Die Zeile des Knotens im Baum: alles Aufgeklappte davor zählt mit – so findet die Ansicht einen Knoten,
        // den sie unter sehr vielen Geschwistern noch nicht gezeichnet hat.
        Assert.Equal(0, browser.TreeRowOf(browser.TreeRoots[0]));
        var row = Assert.IsType<int>(browser.TreeRowOf(node));
        var above = browser.TreePathTo(node);
        Assert.True(row >= above.Count, $"row {row}");
        var parent = above[^1];
        Assert.Equal(browser.TreeRowOf(parent) + 1 + parent.Children.IndexOf(node), row);
        Assert.Null(browser.TreeRowOf(new DirectoryNode("else", "/somewhere/else")));
        parent.IsExpanded = false;
        Assert.Null(browser.TreeRowOf(node));
    }

    [Fact]
    public async Task Opening_a_directory_navigates_and_opening_a_file_does_not()
    {
        var browser = await CreateAsync();

        await browser.OpenCommand.ExecuteAsync(browser.Entries.Single(entry => entry.Name == "html"));
        Assert.Equal("/var/www/html", browser.CurrentPath);

        await browser.OpenCommand.ExecuteAsync(browser.Entries.Single(entry => entry.Name == "index.html"));
        Assert.Equal("/var/www/html", browser.CurrentPath);
    }

    [Fact]
    public async Task Back_and_forward_walk_the_history()
    {
        var browser = await CreateAsync();
        await browser.NavigateToAsync("/var/www/html");
        await browser.NavigateToAsync("/etc");

        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Equal("/var/www/html", browser.CurrentPath);
        Assert.True(browser.CanGoForward);

        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Equal("/var/www", browser.CurrentPath);
        Assert.False(browser.CanGoBack);

        await browser.GoForwardCommand.ExecuteAsync(null);
        await browser.GoForwardCommand.ExecuteAsync(null);
        Assert.Equal("/etc", browser.CurrentPath);
        Assert.False(browser.CanGoForward);
    }

    [Fact]
    public async Task Navigating_somewhere_new_discards_the_forward_history()
    {
        var browser = await CreateAsync();
        await browser.NavigateToAsync("/var/www/html");
        await browser.GoBackCommand.ExecuteAsync(null);

        await browser.NavigateToAsync("/etc");

        Assert.False(browser.CanGoForward);
    }

    [Fact]
    public async Task Up_goes_to_the_parent_until_the_root()
    {
        var browser = await CreateAsync();

        await browser.GoUpCommand.ExecuteAsync(null);
        await browser.GoUpCommand.ExecuteAsync(null);

        Assert.Equal("/", browser.CurrentPath);
        Assert.False(browser.CanGoUp);
        Assert.False(browser.GoUpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Up_at_the_root_does_nothing()
    {
        // Die Rücktaste ruft den Befehl ohne Blick auf CanExecute – an der Wurzel darf das nichts tun.
        var browser = await CreateAsync();
        await browser.GoUpCommand.ExecuteAsync(null);
        await browser.GoUpCommand.ExecuteAsync(null);

        await browser.GoUpCommand.ExecuteAsync(null);

        Assert.Equal("/", browser.CurrentPath);
        Assert.Null(browser.Error);
    }

    [Fact]
    public async Task A_missing_directory_reports_an_error_and_keeps_the_current_view()
    {
        var browser = await CreateAsync();

        await browser.NavigateToAsync("/does/not/exist");

        Assert.True(browser.HasError);
        Assert.Equal("/var/www", browser.CurrentPath);
        Assert.Equal(3, browser.Entries.Count);
        Assert.False(browser.CanGoBack);

        await browser.RefreshCommand.ExecuteAsync(null);
        Assert.False(browser.HasError);
    }

    [Fact]
    public async Task An_empty_directory_is_flagged()
    {
        var browser = new SftpBrowserViewModel(new MockRemoteFileSystem("/home/nobody"));
        await browser.LoadAsync();

        Assert.True(browser.IsEmpty);
    }

    private static async Task<SftpBrowserViewModel> WithHiddenAsync()
    {
        var files = new MockRemoteFileSystem("/home/ubuntu")
            .File("/home/ubuntu/.bashrc", 100, hoursAgo: 1)
            .File("/home/ubuntu/.ssh/authorized_keys", 400, hoursAgo: 1)
            .File("/home/ubuntu/notes.txt", 200, hoursAgo: 2)
            .File("/home/ubuntu/project/readme.md", 300, hoursAgo: 3)
            .File("/home/ubuntu/project/.env", 50, hoursAgo: 3)
            .File("/home/onlyhidden/.profile", 10, hoursAgo: 4);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        return browser;
    }

    [Fact]
    public async Task Hidden_entries_can_be_switched_off_without_asking_the_server_again()
    {
        var browser = await WithHiddenAsync();
        Assert.True(browser.ShowHidden);
        Assert.Equal([".ssh", "project", ".bashrc", "notes.txt"], browser.Entries.Select(entry => entry.Name));
        browser.SelectedEntry = browser.Entries[2];

        browser.ShowHidden = false;

        Assert.Equal(["project", "notes.txt"], browser.Entries.Select(entry => entry.Name));
        Assert.Null(browser.SelectedEntry);

        // Die Einstellung gilt auch für das nächste Verzeichnis.
        await browser.OpenCommand.ExecuteAsync(browser.Entries[0]);
        Assert.Equal(["readme.md"], browser.Entries.Select(entry => entry.Name));

        browser.ShowHidden = true;
        Assert.Equal([".env", "readme.md"], browser.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task A_folder_with_only_hidden_entries_looks_empty_but_still_knows_their_names()
    {
        var browser = await WithHiddenAsync();
        browser.ShowHidden = false;

        await browser.NavigateToAsync("/home/onlyhidden");

        Assert.True(browser.IsEmpty);

        // Für die Frage „überschreiben?“ zählt, was auf dem Server liegt – nicht, was gerade zu sehen ist.
        Assert.Equal([".profile"], browser.ExistingNames([".profile", "new.txt"]));
    }

    [Theory]
    [InlineData("/etc", "/etc")]
    [InlineData("  /home/ubuntu/project/  ", "/home/ubuntu/project")]
    [InlineData("project", "/home/ubuntu/project")]
    [InlineData("./project", "/home/ubuntu/project")]
    [InlineData("..", "/home")]
    [InlineData("../onlyhidden", "/home/onlyhidden")]
    public async Task A_typed_path_is_absolute_or_relative_to_the_shown_directory(string typed, string expected)
    {
        var files = new MockRemoteFileSystem("/home/ubuntu")
            .File("/home/ubuntu/project/readme.md", 300, hoursAgo: 3)
            .File("/home/onlyhidden/.profile", 10, hoursAgo: 4)
            .File("/etc/hosts", 10, hoursAgo: 4);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        await browser.GoToAsync(typed);

        Assert.False(browser.HasError, browser.Error);
        Assert.Equal(expected, browser.CurrentPath);
        Assert.True(browser.CanGoBack);
    }

    [Fact]
    public async Task The_tilde_leads_back_to_where_the_session_started()
    {
        var browser = await WithHiddenAsync();
        await browser.NavigateToAsync("/home/onlyhidden");

        await browser.GoToAsync("~/project");
        Assert.Equal("/home/ubuntu/project", browser.CurrentPath);

        await browser.GoToAsync("~");
        Assert.Equal("/home/ubuntu", browser.CurrentPath);
    }

    [Fact]
    public async Task A_typed_path_that_does_not_exist_reports_an_error_and_an_empty_one_does_nothing()
    {
        var browser = await WithHiddenAsync();

        await browser.GoToAsync("   ");
        Assert.False(browser.HasError);
        Assert.False(browser.CanGoBack);

        await browser.GoToAsync("/nope");
        Assert.True(browser.HasError);
        Assert.Equal("/home/ubuntu", browser.CurrentPath);
    }

    [Fact]
    public async Task Opening_a_file_reports_it_with_its_full_path()
    {
        var browser = await WithHiddenAsync();
        var opened = new List<(string Name, string Path)>();
        browser.FileOpened += (sender, entry, path) =>
        {
            Assert.Same(browser, sender);
            opened.Add((entry.Name, path));
        };

        await browser.OpenCommand.ExecuteAsync(browser.Entries.Single(entry => entry.Name == "notes.txt"));
        await browser.OpenCommand.ExecuteAsync(browser.Entries.Single(entry => entry.Name == "project"));
        await browser.OpenCommand.ExecuteAsync(null);

        Assert.Equal([("notes.txt", "/home/ubuntu/notes.txt")], opened);
        Assert.Equal("/home/ubuntu/project", browser.CurrentPath);
    }

    [Fact]
    public async Task The_hidden_files_setting_reaches_every_open_browser_and_the_settings()
    {
        var settings = new Settings.AppSettings();
        var shell = TestShell.Create(out var backend, settings);
        var first = shell.OpenSession(backend.GetProfiles()[0]);
        first.Start();
        await first.Files.NavigateToAsync(backend.GetProfiles()[0].User == "root" ? "/root" : $"/home/{backend.GetProfiles()[0].User}");
        Assert.Contains(first.Files.Entries, entry => entry.IsHidden);

        Assert.True(shell.RunCommand("files.hidden"));

        Assert.False(shell.ShowHiddenFiles);
        Assert.False(settings.ShowHiddenFiles);
        Assert.DoesNotContain(first.Files.Entries, entry => entry.IsHidden);

        // Eine Sitzung, die danach entsteht, übernimmt die Einstellung.
        var second = shell.OpenSession(backend.GetProfiles()[1]);
        Assert.False(second.Files.ShowHidden);

        var restored = TestShell.Create(out _, settings);
        Assert.False(restored.ShowHiddenFiles);
    }
}
