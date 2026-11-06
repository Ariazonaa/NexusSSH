using NexusSSH.Core.Mock;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Der Baum der Verzeichnisse neben der Dateiliste: lädt nach, folgt der Liste, kostet ausgeblendet nichts.</summary>
public sealed class DirectoryTreeTests
{
    private readonly MockRemoteFileSystem _files = new MockRemoteFileSystem("/var/www")
        .File("/var/www/html/index.html", 100, hoursAgo: 1)
        .File("/var/www/.git/HEAD", 10, hoursAgo: 1)
        .File("/var/www/nginx.conf", 200, hoursAgo: 2)
        .File("/var/log/syslog", 300, hoursAgo: 3)
        .File("/etc/hosts", 10, hoursAgo: 4);

    private async Task<SftpBrowserViewModel> OpenAsync(bool showTree = true, bool showHidden = true)
    {
        var browser = new SftpBrowserViewModel(_files) { ShowTree = showTree, ShowHidden = showHidden };
        await browser.LoadAsync();
        return browser;
    }

    private static DirectoryNode Root(SftpBrowserViewModel browser) => Assert.Single(browser.TreeRoots);

    private static IEnumerable<string> Names(DirectoryNode node) => node.Children.Select(child => child.Name);

    private static DirectoryNode Child(DirectoryNode node, string name) => node.Children.Single(child => child.Name == name);

    [Fact]
    public async Task The_tree_opens_down_to_the_shown_directory_and_marks_it()
    {
        var browser = await OpenAsync();

        var root = Root(browser);
        Assert.Equal("/", root.DisplayName);
        Assert.True(root.IsExpanded);
        Assert.Equal(["etc", "var"], Names(root));
        var var = Child(root, "var");
        Assert.True(var.IsExpanded);
        Assert.Equal(["log", "www"], Names(var));

        // Was die Liste über das angezeigte Verzeichnis weiß, kennt auch der Baum – ohne eigene Anfrage.
        var www = Child(var, "www");
        Assert.Same(www, browser.CurrentNode);
        Assert.Equal("/var/www", www.Path);
        Assert.Equal([".git", "html"], Names(www));
        Assert.False(www.HasUnrealizedChildren);
        Assert.Equal(["/var/www", "/", "/var"], _files.Listed);

        // Noch nicht aufgeklappte Knoten zeigen einen Pfeil, auch wenn sie leer sein könnten.
        Assert.True(Child(root, "etc").HasUnrealizedChildren);
    }

    [Fact]
    public async Task Expanding_a_node_loads_its_directories_once()
    {
        var browser = await OpenAsync();
        var etc = Child(Root(browser), "etc");

        await browser.ExpandAsync(etc);
        await browser.ExpandAsync(etc);

        Assert.Empty(etc.Children);
        Assert.False(etc.HasUnrealizedChildren);
        Assert.Single(_files.Listed, path => path == "/etc");
    }

    [Fact]
    public async Task Choosing_a_node_shows_its_directory_and_the_tree_follows_the_list()
    {
        var browser = await OpenAsync();
        var log = Child(Child(Root(browser), "var"), "log");

        await browser.OpenNodeAsync(log);
        Assert.Equal("/var/log", browser.CurrentPath);
        Assert.Same(log, browser.CurrentNode);

        await browser.NavigateToAsync("/etc");
        Assert.Same(Child(Root(browser), "etc"), browser.CurrentNode);

        await browser.GoBackCommand.ExecuteAsync(null);
        Assert.Same(log, browser.CurrentNode);
    }

    [Fact]
    public async Task A_hidden_tree_asks_the_server_for_nothing()
    {
        var browser = await OpenAsync(showTree: false);
        await browser.NavigateToAsync("/var/www/html");

        Assert.Equal(["/var/www", "/var/www/html"], _files.Listed);
        Assert.Null(browser.CurrentNode);

        // Eingeblendet holt er nach, was fehlt.
        browser.ShowTree = true;
        await Task.Yield();
        Assert.Equal("/var/www/html", browser.CurrentNode?.Path);
    }

    [Fact]
    public async Task Hidden_directories_follow_the_setting_but_the_way_to_the_shown_one_stays()
    {
        var browser = await OpenAsync(showHidden: false);
        var www = browser.CurrentNode!;
        Assert.Equal(["html"], Names(www));

        await browser.NavigateToAsync("/var/www/.git");
        Assert.Equal("/var/www/.git", browser.CurrentNode?.Path);
        Assert.Contains(".git", Names(www));

        browser.ShowHidden = true;
        Assert.Equal([".git", "html"], Names(www));
        browser.ShowHidden = false;
        await Task.Yield();
        Assert.Equal("/var/www/.git", browser.CurrentNode?.Path);
    }

    [Fact]
    public async Task A_directory_on_the_way_that_cannot_be_listed_still_leads_on()
    {
        _files.Denied.Add("/var");
        var browser = await OpenAsync();

        var var = Child(Root(browser), "var");
        Assert.Equal(["www"], Names(var));
        Assert.False(var.HasUnrealizedChildren);
        Assert.Equal("/var/www", browser.CurrentNode?.Path);
        Assert.False(browser.HasError, "der Baum meldet keine Fehler, die niemand ausgelöst hat");

        // Wer selbst ein Verzeichnis aufklappt, das sich nicht auflisten lässt, erfährt es.
        _files.Denied.Add("/etc");
        await browser.ExpandAsync(Child(Root(browser), "etc"));
        Assert.True(browser.HasError);
    }

    [Fact]
    public async Task A_new_folder_appears_in_the_tree_without_reloading_it()
    {
        var browser = await OpenAsync();
        var www = browser.CurrentNode!;
        www.Children[1].IsExpanded = true;

        Assert.True(await browser.CreateFolderAsync("backup"));

        Assert.Equal([".git", "backup", "html"], Names(www));
        Assert.True(Child(www, "html").IsExpanded, "vorhandene Knoten bleiben, wie sie waren");
    }

    [Fact]
    public async Task Another_connection_starts_a_new_tree()
    {
        var browser = await OpenAsync();
        var before = Root(browser);

        browser.Attach(new MockRemoteFileSystem("/srv").File("/srv/data/a.txt", 1, hoursAgo: 1));
        Assert.NotSame(before, Root(browser));
        Assert.Null(browser.CurrentNode);

        await browser.LoadAsync();
        Assert.Equal("/srv", browser.CurrentNode?.Path);
        Assert.Equal(["data"], Names(browser.CurrentNode!));
    }
}
