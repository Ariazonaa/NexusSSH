using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Rechte, Besitzer und Gruppe ändern – auch für einen ganzen Ordner –, und der Dateibrowser, der sich einen neuen
/// Kanal holt, wenn der Server nicht mehr antwortet.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class PropertyChangeTests
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    private static readonly RemoteEntry File = new("app.conf", IsDirectory: false, 10, DateTimeOffset.MinValue, Permissions: 0b110_100_100, Owner: "root", Group: "root");
    private static readonly RemoteEntry Folder = new("www", IsDirectory: true, 0, DateTimeOffset.MinValue, Permissions: 0b111_101_101, Owner: "root", Group: "staff");

    public PropertyChangeTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Only_what_differs_is_changed()
    {
        Assert.Null(PropertyChange.From(File, "644", "root", "root", recursive: false));
        Assert.Null(PropertyChange.From(File, "0644", " root ", "", recursive: false));

        Assert.Equal(new PropertyChange(0b110_000_000, null, null, false), PropertyChange.From(File, "600", "root", "root", recursive: false));
        Assert.Equal(new PropertyChange(null, "www-data", null, false), PropertyChange.From(File, "644", "www-data", "root", recursive: false));
        Assert.Equal(new PropertyChange(null, null, "33", false), PropertyChange.From(File, "644", "root", " 33 ", recursive: false));

        // „Auch alles darin“ gibt es nur bei einem Ordner.
        Assert.Null(PropertyChange.From(File, "644", "root", "root", recursive: true));
    }

    [Fact]
    public void For_a_whole_folder_everything_in_the_fields_applies()
    {
        // Für den Ordner selbst stimmen Besitzer und Gruppe schon – darin kann es anders aussehen.
        Assert.Equal(new PropertyChange(null, "root", "staff", true), PropertyChange.From(Folder, "755", "root", "staff", recursive: true));
        Assert.Null(PropertyChange.From(Folder, "755", "root", "staff", recursive: false));

        // Die Rechte wandern nur mit, wenn sie geändert wurden: Wer die Gruppe eines Baums ändert, macht nicht
        // nebenbei jede Datei darin ausführbar.
        Assert.Equal(new PropertyChange(null, "root", "www-data", true), PropertyChange.From(Folder, "755", "root", "www-data", recursive: true));
        Assert.Equal(new PropertyChange(0b110_100_000, "root", "staff", true), PropertyChange.From(Folder, "640", "root", "staff", recursive: true));
    }

    [Theory]
    [InlineData("64", "root", "root")]
    [InlineData("888", "root", "root")]
    [InlineData("", "root", "root")]
    [InlineData("644", "two words", "root")]
    [InlineData("644", "root", "a:b")]
    [InlineData("644", "tab\tname", "root")]
    public void Input_that_makes_no_sense_changes_nothing(string mode, string owner, string group)
    {
        Assert.Null(PropertyChange.From(File, mode, owner, group, recursive: false));
    }

    [Fact]
    public void Without_known_permissions_only_the_owner_can_change()
    {
        var entry = File with { Permissions = null };

        Assert.Null(PropertyChange.From(entry, "", "root", "root", recursive: false));
        Assert.Equal(new PropertyChange(null, "deploy", null, false), PropertyChange.From(entry, "", "deploy", "root", recursive: false));
    }

    [Fact]
    public async Task The_browser_changes_owner_first_and_then_permissions_and_shows_the_result()
    {
        var files = new MockRemoteFileSystem("/srv", null, "web01")
            .File("/srv/www/index.html", 10, hoursAgo: 1)
            .File("/srv/www/css/site.css", 5, hoursAgo: 1)
            .File("/srv/readme.txt", 1, hoursAgo: 1);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        var folder = browser.Entries.Single(entry => entry.Name == "www");

        Assert.True(await browser.ApplyAsync(folder, new PropertyChange(0b110_100_000, "www-data", "www-data", Recursive: true)));

        // Der Ordner bleibt betretbar, die Dateien darin bekommen genau die Zahl.
        folder = browser.Entries.Single(entry => entry.Name == "www");
        Assert.Equal(("drwxr-x--- (750)", "www-data", "www-data"), (Format.Permissions(folder.Permissions, true, false), folder.Owner, folder.Group));
        var inside = files.List("/srv/www");
        Assert.All(inside, entry => Assert.Equal("www-data", entry.Owner));
        Assert.Equal("-rw-r----- (640)", Format.Permissions(inside.Single(entry => entry.Name == "index.html").Permissions, false, false));
        Assert.Equal("drwxr-x--- (750)", Format.Permissions(inside.Single(entry => entry.Name == "css").Permissions, true, false));
        Assert.Equal("-rw-r----- (640)", Format.Permissions(files.List("/srv/www/css").Single().Permissions, false, false));

        // Daneben ändert sich nichts.
        Assert.Equal("root", browser.Entries.Single(entry => entry.Name == "readme.txt").Owner);
    }

    [Fact]
    public void Recursive_chmod_chown_and_reopen_go_to_the_core_as_json()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        var files = connection.Files!;

        _ = files.SetPermissionsAsync("/srv/www", 0b110_100_000, recursive: true);
        _ = files.SetPermissionsAsync("/srv/www", 0b110_100_000);
        _ = files.SetOwnerAsync("/srv/www", "www-data", null, recursive: true);
        _ = files.SetOwnerAsync("/srv/a.txt", null, "33");
        _ = files.ReopenAsync();

        var chmod = transport.Sent("sftp.chmod").ToList();
        Assert.True(chmod[0].GetProperty("recursive").GetBoolean());
        Assert.False(chmod[1].TryGetProperty("recursive", out _));

        var chown = transport.Sent("sftp.chown").ToList();
        Assert.Equal(("/srv/www", "www-data", true), (chown[0].GetProperty("path").GetString(), chown[0].GetProperty("owner").GetString(), chown[0].GetProperty("recursive").GetBoolean()));
        Assert.False(chown[0].TryGetProperty("group", out _));
        Assert.Equal("33", chown[1].GetProperty("group").GetString());
        Assert.False(chown[1].TryGetProperty("owner", out _));

        var reopen = Assert.Single(transport.Sent("sftp.reopen"));
        Assert.Equal(1UL, reopen.GetProperty("session").GetUInt64());
        Assert.True(reopen.TryGetProperty("request", out _));
    }

    [Fact]
    public async Task A_directory_that_does_not_answer_is_reported_and_the_browser_gets_a_new_channel()
    {
        var files = new MockRemoteFileSystem("/srv", null, "web01").File("/srv/www/index.html", 10, hoursAgo: 1);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        // Der Server antwortet nicht: Das Verzeichnis bleibt, wo es war, und es wird nicht gleich noch einmal gewartet.
        files.FailNextList = "timeout";
        await browser.NavigateToAsync("/srv/www");

        Assert.Equal("/srv", browser.CurrentPath);
        Assert.Equal("The server did not answer in time. (Timeout)", browser.Error);
        Assert.Equal(1, files.Reopened);

        // Auf dem neuen Kanal geht es weiter.
        await browser.NavigateToAsync("/srv/www");
        Assert.Equal("/srv/www", browser.CurrentPath);
        Assert.Null(browser.Error);
        Assert.Equal(1, files.Reopened);
    }

    [Fact]
    public async Task A_closed_channel_is_reopened_and_the_listing_repeated()
    {
        var files = new MockRemoteFileSystem("/srv", null, "web01").File("/srv/www/index.html", 10, hoursAgo: 1);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        files.FailNextList = "connection";
        await browser.NavigateToAsync("/srv/www");

        Assert.Equal("/srv/www", browser.CurrentPath);
        Assert.Null(browser.Error);
        Assert.Equal(1, files.Reopened);
    }

    [Fact]
    public void An_owner_the_server_does_not_know_is_said_in_a_sentence()
    {
        Assert.Equal("The server does not know this user or group. (niemand)", FailureText.Describe("unknown_owner", "niemand"));
    }

    [Fact]
    public void A_server_without_room_for_another_channel_names_max_sessions()
    {
        var text = FailureText.Describe("no_channel", "Failed to open channel (ConnectFailed)");
        Assert.StartsWith("The server allows no further channel on this connection", text);
        Assert.Contains("MaxSessions", text);
    }
}
