using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Dateiliste filtern und Ordner je Server als Lesezeichen merken.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FileListExtrasTests
{
    public FileListExtrasTests() => L.Init(AppLanguage.English);

    private static async Task<SftpBrowserViewModel> BrowserAsync()
    {
        var files = new MockRemoteFileSystem("/var/www")
            .File("/var/www/html/index.html", 100, hoursAgo: 1)
            .File("/var/www/nginx.conf", 200, hoursAgo: 2)
            .File("/var/www/Nginx.bak", 200, hoursAgo: 2)
            .File("/var/www/.htaccess", 5, hoursAgo: 2)
            .File("/var/www/Assets/logo.svg", 300, hoursAgo: 3);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        return browser;
    }

    [Fact]
    public async Task The_filter_keeps_what_contains_the_text_and_says_how_much_that_is()
    {
        var browser = await BrowserAsync();
        Assert.Equal("5 entries", browser.CountText);

        browser.Filter = "NGINX";

        Assert.True(browser.IsFiltered);
        Assert.Equal(["Nginx.bak", "nginx.conf"], browser.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Equal("2 of 5 entries", browser.CountText);

        // Nichts gefunden ist nicht „der Ordner ist leer“.
        browser.Filter = "nothing like that";
        Assert.Empty(browser.Entries);
        Assert.False(browser.IsEmpty);
        Assert.Equal("0 of 5 entries", browser.CountText);

        // Ausgeblendetes zählt nicht mit, auch nicht in der Summe.
        browser.ShowHidden = false;
        browser.Filter = "ht";
        Assert.Equal(["html"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal("1 of 4 entries", browser.CountText);

        browser.Filter = string.Empty;
        Assert.Equal("4 entries", browser.CountText);
    }

    [Fact]
    public async Task The_filter_ends_with_the_folder_but_survives_a_refresh()
    {
        var browser = await BrowserAsync();
        browser.Filter = "ht";

        await browser.NavigateToAsync("/var/www");
        Assert.Equal("ht", browser.Filter);
        Assert.Equal([".htaccess", "html"], browser.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));

        await browser.NavigateToAsync("/var/www/html");
        Assert.Equal(string.Empty, browser.Filter);
        Assert.Equal(["index.html"], browser.Entries.Select(entry => entry.Name));

        // Ein Wechsel, der scheitert, lässt Ansicht und Filter stehen.
        browser.Filter = "index";
        await browser.NavigateToAsync("/does/not/exist");
        Assert.Equal("index", browser.Filter);
    }

    [Fact]
    public async Task Showing_a_path_lifts_a_filter_that_hides_it()
    {
        var browser = await BrowserAsync();
        browser.Filter = "html";

        Assert.True(await browser.ShowPathAsync("/var/www/nginx.conf"));

        Assert.Equal(string.Empty, browser.Filter);
        Assert.Equal("nginx.conf", browser.SelectedEntry?.Name);
    }

    [Fact]
    public void Bookmarks_belong_to_the_server_and_follow_the_folder_shown()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        var home = session.Files.CurrentPath;
        var changes = new List<string?>();
        shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(shell.CanBookmarkCurrentFolder);
        Assert.False(shell.IsCurrentFolderBookmarked);
        Assert.Empty(shell.ActiveBookmarks);

        shell.ToggleBookmark();

        Assert.True(shell.IsCurrentFolderBookmarked);
        Assert.Equal([home], shell.ActiveBookmarks);
        Assert.Equal([home], shell.Settings.Bookmarks[session.Profile.Id]);
        Assert.Contains(nameof(ShellViewModel.ActiveBookmarks), changes);

        // Ein zweiter Tab zum selben Server sieht dieselben, ein anderer Server keine.
        var second = shell.OpenSession(shell.Profiles[0]);
        second.Start();
        Assert.Equal([home], shell.ActiveBookmarks);
        var other = shell.OpenSession(shell.Profiles[1]);
        other.Start();
        Assert.Empty(shell.ActiveBookmarks);

        shell.ActiveSession = session;
        shell.ToggleBookmark();
        Assert.Empty(shell.ActiveBookmarks);
        Assert.False(shell.Settings.Bookmarks.ContainsKey(session.Profile.Id));

        // Ohne aktive Sitzung gibt es nichts zu merken.
        shell.ShowHome();
        Assert.False(shell.CanBookmarkCurrentFolder);
        shell.ToggleBookmark();
        Assert.Empty(shell.Settings.Bookmarks);
    }

    [Fact]
    public void A_bookmark_can_be_removed_by_its_path_and_goes_with_its_profile()
    {
        var shell = TestShell.Create(out _);
        var profile = shell.Profiles[0];
        shell.OpenSession(profile).Start();
        FolderBookmarks.Add(shell.Settings, profile.Id, "/etc/nginx/");
        FolderBookmarks.Add(shell.Settings, profile.Id, "/var/log");

        Assert.Equal(["/etc/nginx", "/var/log"], shell.ActiveBookmarks);

        shell.RemoveBookmark("/etc/nginx");
        Assert.Equal(["/var/log"], shell.ActiveBookmarks);

        shell.DeleteProfile(profile);
        Assert.Empty(shell.Settings.Bookmarks);
    }

    [Fact]
    public void Bookmarks_are_limited_and_a_damaged_list_is_put_right()
    {
        var settings = new AppSettings();
        Assert.True(FolderBookmarks.Add(settings, "p", "/a/../b"));
        Assert.False(FolderBookmarks.Add(settings, "p", "/b/"));
        Assert.False(FolderBookmarks.Add(settings, string.Empty, "/b"));
        Assert.True(FolderBookmarks.Contains(settings, "p", "/b"));

        for (var number = 0; number < FolderBookmarks.MaxPerProfile + 5; number++)
        {
            FolderBookmarks.Add(settings, "p", $"/folder/{number}");
        }

        Assert.Equal(FolderBookmarks.MaxPerProfile, FolderBookmarks.Of(settings, "p").Count);

        settings.Bookmarks = new()
        {
            ["p"] = ["/srv//www/", "relative", string.Empty, "/srv/www", null!],
            [string.Empty] = ["/x"],
            ["empty"] = [],
            ["gone"] = null!,
        };
        settings.Sanitize();

        var kept = Assert.Single(settings.Bookmarks);
        Assert.Equal("p", kept.Key);
        Assert.Equal(["/srv/www"], kept.Value);
    }

    [Fact]
    public async Task The_size_of_a_folder_is_asked_on_request_and_shown_as_text()
    {
        var browser = await BrowserAsync();
        await browser.NavigateToAsync("/var");

        // Alles unter /var/www: 100 + 200 + 200 + 5 + 300 Bytes.
        var www = browser.Entries.Single(entry => entry.Name == "www");
        Assert.Equal("805 B  (805 B)", await browser.FolderSizeTextAsync(www));

        // Gibt es den Ordner nicht mehr, steht dort der Grund statt einer Zahl.
        var gone = www with { Name = "gone" };
        Assert.Contains("No such file", await browser.FolderSizeTextAsync(gone));
    }
}
