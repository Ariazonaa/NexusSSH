using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Dateiaktionen und Transfers: im Dateibrowser (gegen das Mock-Dateisystem) und im Protokoll zum Kern.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FileOperationTests
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    public FileOperationTests() => L.Init(AppLanguage.English);

    private static async Task<(SftpBrowserViewModel Browser, MockRemoteFileSystem Files, MockTransferQueue Queue)> BrowserAsync()
    {
        var queue = new MockTransferQueue();
        var files = new MockRemoteFileSystem("/var/www", queue, "web01")
            .File("/var/www/html/index.html", 100, hoursAgo: 1)
            .File("/var/www/html/css/site.css", 50, hoursAgo: 1)
            .File("/var/www/nginx.conf", 200, hoursAgo: 2);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        return (browser, files, queue);
    }

    [Fact]
    public async Task A_new_folder_appears_in_the_list()
    {
        var (browser, _, _) = await BrowserAsync();

        Assert.True(await browser.CreateFolderAsync(" backup "));

        Assert.Equal(["backup", "html", "nginx.conf"], browser.Entries.Select(entry => entry.Name));
        Assert.True(browser.Entries[0].IsDirectory);
        Assert.False(browser.HasError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task An_unusable_name_is_refused_before_asking_the_server(string name)
    {
        var (browser, _, _) = await BrowserAsync();

        Assert.False(await browser.CreateFolderAsync(name));

        Assert.Equal("Not a valid name.", browser.Error);
        Assert.Equal(2, browser.Entries.Count);
    }

    [Fact]
    public async Task A_folder_that_exists_reports_the_servers_refusal()
    {
        var (browser, _, _) = await BrowserAsync();

        Assert.False(await browser.CreateFolderAsync("html"));

        Assert.StartsWith("The server refused the action.", browser.Error);
        browser.DismissErrorCommand.Execute(null);
        Assert.False(browser.HasError);
    }

    [Fact]
    public async Task Renaming_keeps_the_entry_in_place_and_never_overwrites()
    {
        var (browser, _, _) = await BrowserAsync();
        var config = browser.Entries.Single(entry => entry.Name == "nginx.conf");

        Assert.True(await browser.RenameAsync(config, "nginx.conf.bak"));
        Assert.Equal(["html", "nginx.conf.bak"], browser.Entries.Select(entry => entry.Name));

        var renamed = browser.Entries.Single(entry => entry.Name == "nginx.conf.bak");
        Assert.False(await browser.RenameAsync(renamed, "html"));
        Assert.StartsWith("That name already exists.", browser.Error);
        Assert.Equal(["html", "nginx.conf.bak"], browser.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task A_renamed_folder_keeps_its_content()
    {
        var (browser, _, _) = await BrowserAsync();

        await browser.RenameAsync(browser.Entries.Single(entry => entry.Name == "html"), "public");
        await browser.NavigateToAsync("/var/www/public/css");

        Assert.Equal("site.css", Assert.Single(browser.Entries).Name);
    }

    [Fact]
    public async Task Deleting_takes_folders_with_their_content()
    {
        var (browser, files, _) = await BrowserAsync();

        Assert.True(await browser.DeleteAsync([.. browser.Entries]));

        Assert.True(browser.IsEmpty);
        await Assert.ThrowsAsync<RemoteFileException>(() => files.ListAsync("/var/www/html/css", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_something_already_gone_says_not_found_and_reloads()
    {
        var (browser, files, _) = await BrowserAsync();
        var stale = browser.Entries.Single(entry => entry.Name == "nginx.conf");
        await files.DeleteAsync("/var/www/nginx.conf");

        Assert.False(await browser.DeleteAsync([stale]));

        Assert.StartsWith("Not found.", browser.Error);
        Assert.Equal("html", Assert.Single(browser.Entries).Name);
    }

    [Fact]
    public async Task Uploads_go_to_the_current_directory_and_downloads_name_full_paths()
    {
        var (browser, files, queue) = await BrowserAsync();
        await browser.NavigateToAsync("/var/www/html");

        browser.Upload([LocalPath.Of("data", "logo.svg"), LocalPath.Folder("data", "photos")]);
        browser.Download([browser.Entries.Single(entry => entry.Name == "index.html")], @"D:\downloads");

        await browser.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(browser.Entries, entry => entry.Name == "logo.svg");
        Assert.Contains(browser.Entries, entry => entry.Name == "photos");
        Assert.Equal(("/var/www/html/index.html", @"D:\downloads"), Assert.Single(files.Downloads));
        Assert.Equal(["logo.svg", "photos", "index.html"], queue.Items.Skip(3).Select(item => item.FileName));
        Assert.Equal(TransferDirection.Download, queue.Items[^1].Direction);
    }

    [Fact]
    public async Task A_pipe_or_device_is_neither_opened_nor_downloaded()
    {
        var (browser, files, queue) = await BrowserAsync();
        files.Special("/var/www/queue");
        await browser.RefreshCommand.ExecuteAsync(null);
        var pipe = browser.Entries.Single(entry => entry.Name == "queue");
        var opened = 0;
        browser.FileOpened += (_, _, _) => opened++;

        // Öffnen hielte den SFTP-Server an, bis jemand in die Pipe schreibt.
        await browser.OpenCommand.ExecuteAsync(pipe);
        Assert.Equal(0, opened);
        Assert.StartsWith("This is not a regular file", browser.Error);

        // Mit anderen zusammen gewählt, bleibt sie beim Herunterladen einfach aus.
        var before = queue.Items.Count;
        browser.Download([pipe, browser.Entries.Single(entry => entry.Name == "nginx.conf")], LocalPath.Folder("downloads"));
        Assert.Equal("/var/www/nginx.conf", Assert.Single(files.Downloads).RemotePath);
        Assert.Equal(before + 1, queue.Items.Count);
    }

    [Fact]
    public async Task Names_that_already_exist_are_reported_for_the_overwrite_question()
    {
        var (browser, _, _) = await BrowserAsync();

        Assert.Equal(["nginx.conf"], browser.ExistingNames(["nginx.conf", "new.txt"]));
    }

    [Fact]
    public async Task Permissions_are_changed_like_with_chmod_and_the_list_shows_them()
    {
        var (browser, files, _) = await BrowserAsync();
        var config = browser.Entries.Single(entry => entry.Name == "nginx.conf");
        Assert.Equal("-rw-r--r-- (644)", Format.Permissions(config.Permissions, config.IsDirectory, config.IsLink));

        Assert.True(await browser.SetPermissionsAsync(config, 0b110_000_000));

        var changed = browser.Entries.Single(entry => entry.Name == "nginx.conf");
        Assert.Equal("-rw------- (600)", Format.Permissions(changed.Permissions, changed.IsDirectory, changed.IsLink));
        Assert.False(browser.HasError);

        // Etwas, das es nicht mehr gibt: Der Server lehnt ab, die Liste sagt es.
        await files.DeleteAsync("/var/www/nginx.conf");
        Assert.False(await browser.SetPermissionsAsync(changed, 0b110_100_100));
    }

    [Fact]
    public void Chmod_sends_the_path_and_the_mode_as_a_number()
    {
        var (_, transport, files) = Connected();

        _ = files.SetPermissionsAsync("/etc/app.conf", 0b110_100_000);
        _ = files.SetPermissionsAsync("/srv", 0b1_000_010_111_111_101);

        var sent = transport.Sent("sftp.chmod").ToList();
        Assert.Equal(("/etc/app.conf", 416, 1UL), (sent[0].GetProperty("path").GetString(), sent[0].GetProperty("mode").GetInt32(), sent[0].GetProperty("session").GetUInt64()));
        Assert.Equal(1533, sent[1].GetProperty("mode").GetInt32());
        Assert.True(sent[0].TryGetProperty("request", out _));
    }

    [Fact]
    public void Transfers_can_be_cancelled_and_finished_ones_cleared()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var waiting = center.Items[2];
        Assert.True(waiting.CanCancel);
        Assert.False(center.Items[1].CanCancel);

        center.Cancel(waiting);
        Assert.Equal("Cancelled", waiting.StatusText);
        Assert.False(waiting.CanCancel);

        center.ClearFinished();
        Assert.Equal("backup.tar.gz", Assert.Single(center.Items).FileName);
    }

    [Fact]
    public void A_running_transfer_shows_how_much_has_arrived_and_a_failed_one_why()
    {
        var running = new TransferItemViewModel(new TransferItem("1", "a.bin", "web01", TransferDirection.Upload, TransferState.Running, 0.5, 5 * 1024 * 1024, 10 * 1024 * 1024));
        Assert.Equal("5.0 MB / 10.0 MB", running.DetailText);

        running.Update(new TransferItem("1", "a.bin", "web01", TransferDirection.Upload, TransferState.Failed, 0.5, 0, 0, "permission_denied: /root"));
        Assert.Equal("Failed", running.StatusText);
        Assert.Equal("Permission denied. (/root)", running.DetailText);
        Assert.False(running.IsRunning);
    }

    [Theory]
    [InlineData("not_a_file: /srv/queue is a pipe, device or socket", "This is not a regular file (pipe, device, socket or a link without a target) – it can be neither opened nor transferred. (/srv/queue is a pipe, device or socket)")]
    [InlineData("unsafe_name: refusing the file name \"CON\"", "The name from the server cannot be used as a file name on this computer. (refusing the file name \"CON\")")]
    [InlineData("timeout: Timeout", "The server did not answer in time. (Timeout)")]
    [InlineData("local_not_found: gone", "The local file or folder does not exist (any more). (gone)")]
    [InlineData("something_new: x", "The server refused the action. (x)")]
    [InlineData("the editor could not be started", "the editor could not be started")]
    [InlineData("C:\\temp: no", "C:\\temp: no")]
    [InlineData(null, "")]
    public void A_failed_transfer_says_why_in_a_sentence(string? detail, string expected)
    {
        Assert.Equal(expected, FailureText.OfTransfer(detail));
    }

    [Fact]
    public void An_upload_that_finishes_is_announced_once_with_its_server()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var announced = new List<string>();
        center.UploadCompleted += announced.Add;

        // Der Upload steht bei 72 % und rückt je Schritt 2 % vor; danach startet und läuft der Download.
        for (var i = 0; i < 80; i++)
        {
            queue.Advance();
        }

        Assert.Equal(["web01"], announced);
    }

    [Fact]
    public void The_file_browser_reloads_when_an_upload_to_its_server_finishes()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        session.Start();
        var files = Assert.IsType<MockRemoteFileSystem>(backend.Connect(session.Profile).Files);
        Assert.DoesNotContain(session.Files.Entries, entry => entry.Name == "late.txt");

        // Die Datei entsteht hinter dem Rücken des Browsers; erst das Ende des Uploads lässt ihn neu laden.
        session.Files.Upload([LocalPath.Of("data", "late.txt")]);
        Assert.DoesNotContain(session.Files.Entries, entry => entry.Name == "late.txt");
        var queue = Assert.IsType<MockTransferQueue>(backend.Transfers);
        for (var i = 0; i < 200; i++)
        {
            queue.Advance();
        }

        Assert.Contains(session.Files.Entries, entry => entry.Name == "late.txt");
        Assert.NotNull(files);
    }

    // --- Protokoll zum Kern ---

    private static (CoreBackend Backend, FakeTransport Transport, IRemoteFileSystem Files) Connected()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        return (backend, transport, Assert.IsAssignableFrom<IRemoteFileSystem>(connection.Files));
    }

    [Fact]
    public void The_file_system_exists_from_connected_with_home_until_closed()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        IRemoteFileSystem? seenAtConnected = null;
        connection.StatusChanged += status => seenAtConnected = status.State == ConnectionState.Connected ? connection.Files : seenAtConnected;
        Assert.Null(connection.Files);

        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        Assert.Equal("/home/ubuntu", seenAtConnected?.InitialPath);

        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connection_lost"}""");
        Assert.Null(connection.Files);
    }

    [Fact]
    public void A_server_without_sftp_gives_no_file_system()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();

        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");

        Assert.Equal(ConnectionState.Connected, connection.Status.State);
        Assert.Null(connection.Files);
    }

    [Fact]
    public async Task Listing_waits_for_the_result_with_its_request_number()
    {
        var (_, transport, files) = Connected();

        var listing = files.ListAsync("/var/www", TestContext.Current.CancellationToken);
        var request = Assert.Single(transport.Sent("sftp.list"));
        Assert.Equal("/var/www", request.GetProperty("path").GetString());
        Assert.Equal(1UL, request.GetProperty("session").GetUInt64());
        Assert.False(listing.IsCompleted);

        var number = request.GetProperty("request").GetUInt64();
        transport.Raise($$"""{"event":"sftp.result","request":{{number + 1}},"ok":true,"entries":[]}""");
        Assert.False(listing.IsCompleted);

        transport.Raise(
            $$"""{"event":"sftp.result","request":{{number}},"ok":true,"entries":[{"name":"html","kind":"directory","link":false,"size":0,"modified":1790000000},{"name":"current","kind":"directory","link":true,"size":0},{"name":"app.log","kind":"file","link":false,"size":6291456,"modified":1790000100},{"name":"sock","kind":"other","link":false,"size":0}]}""");
        var entries = await listing;

        Assert.Equal(["html", "current", "app.log", "sock"], entries.Select(entry => entry.Name));
        Assert.Equal([true, true, false, false], entries.Select(entry => entry.IsDirectory));
        Assert.Equal([false, false, false, true], entries.Select(entry => entry.IsSpecial));
        Assert.True(entries[1].IsLink);
        Assert.Equal(6291456, entries[2].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), entries[0].Modified);
        Assert.Equal(DateTimeOffset.MinValue, entries[1].Modified);
    }

    [Fact]
    public async Task A_listed_link_carries_its_target_and_one_into_nothing_says_so()
    {
        var (_, transport, files) = Connected();

        var listing = files.ListAsync("/var/www", TestContext.Current.CancellationToken);
        var number = Assert.Single(transport.Sent("sftp.list")).GetProperty("request").GetUInt64();
        transport.Raise(
            $$"""{"event":"sftp.result","request":{{number}},"ok":true,"entries":[{"name":"aktuell","kind":"directory","link":true,"size":0,"target":"../releases/42"},{"name":"tot","kind":"other","link":true,"size":0,"target":"fehlt","dangling":true},{"name":"unbekannt","kind":"file","link":true,"size":3},{"name":"datei","kind":"file","link":false,"size":3}]}""");
        var entries = await listing;

        Assert.Equal("../releases/42", entries[0].LinkTarget);
        Assert.False(entries[0].IsDangling);

        Assert.Equal("fehlt", entries[1].LinkTarget);
        Assert.True(entries[1].IsDangling);
        Assert.True(entries[1].IsSpecial);
        Assert.True(entries[1].IsLink);

        // Ohne Angabe des Kerns (Dateizugriff über Befehle, DSM) ist nichts bekannt – und tot ist der Link damit nicht.
        Assert.Null(entries[2].LinkTarget);
        Assert.False(entries[2].IsDangling);
        Assert.Null(entries[3].LinkTarget);
    }

    [Fact]
    public async Task One_link_target_is_asked_for_with_readlink_and_a_refusal_is_an_error()
    {
        var (_, transport, files) = Connected();

        var asking = files.ReadLinkAsync("/var/www/aktuell", TestContext.Current.CancellationToken);
        var request = Assert.Single(transport.Sent("sftp.readlink"));
        Assert.Equal("/var/www/aktuell", request.GetProperty("path").GetString());
        Assert.Equal(1UL, request.GetProperty("session").GetUInt64());
        Assert.False(asking.IsCompleted);

        transport.Raise($$"""{"event":"sftp.result","request":{{request.GetProperty("request").GetUInt64()}},"ok":true,"target":"../releases/42"}""");
        Assert.Equal("../releases/42", await asking);

        var failing = files.ReadLinkAsync("/var/www/datei", TestContext.Current.CancellationToken);
        var second = transport.Sent("sftp.readlink").Last();
        transport.Raise($$"""{"event":"sftp.result","request":{{second.GetProperty("request").GetUInt64()}},"ok":false,"error":"not_found","message":"No such file"}""");
        var error = await Assert.ThrowsAsync<RemoteFileException>(() => failing);
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task The_target_of_a_link_is_resolved_from_its_folder()
    {
        var browser = new SftpBrowserViewModel(new MockRemoteFileSystem("/var/www"));
        await browser.LoadAsync();
        static RemoteEntry Link(string? target) => new("l", IsDirectory: false, Size: 0, DateTimeOffset.MinValue, IsLink: true, LinkTarget: target);

        Assert.Equal("/var/releases/42", browser.TargetOf(Link("../releases/42")));
        Assert.Equal("/etc/x", browser.TargetOf(Link("/etc/x")));
        Assert.Equal("/var/www/releases", browser.TargetOf(Link("releases")));
        Assert.Equal("/var/www", browser.TargetOf(Link(".")));
        Assert.Equal("/", browser.TargetOf(Link("../../../..")));
        Assert.Equal("/var/www/a", browser.TargetOf(Link("./a/")));

        // Ohne genanntes Ziel gibt es nichts aufzulösen.
        Assert.Null(browser.TargetOf(Link(null)));
        Assert.Null(browser.TargetOf(Link(string.Empty)));
    }

    [Fact]
    public async Task The_target_of_a_link_comes_from_the_list_or_is_asked_for_and_may_stay_unknown()
    {
        var files = new MockRemoteFileSystem("/var/www")
            .File("/var/releases/42/index.html", 100, hoursAgo: 1)
            .Link("/var/www/aktuell", "../releases/42");
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        var link = browser.Entries.Single();

        // Die Liste nennt das Ziel: Der Server wird nicht noch einmal gefragt.
        Assert.Equal("../releases/42", (await browser.WithTargetAsync(link)).LinkTarget);
        Assert.Empty(files.LinksRead);

        // Ein Server, dessen Liste es nicht nennt (Dateizugriff über Befehle): Ein Aufruf holt es.
        files.ListsLinkTargets = false;
        await browser.LoadAsync();
        var unlisted = browser.Entries.Single();
        Assert.Null(unlisted.LinkTarget);
        Assert.Equal("../releases/42", (await browser.WithTargetAsync(unlisted)).LinkTarget);
        Assert.Equal(["/var/www/aktuell"], files.LinksRead);

        // Einer, der es auch auf Nachfrage nicht sagt (DSM): Der Eintrag bleibt, wie er ist – kein Fehler.
        files.ReadsLinks = false;
        Assert.Null((await browser.WithTargetAsync(unlisted)).LinkTarget);
        Assert.False(browser.HasError);

        // Etwas, das kein Link ist, wird gar nicht erst gefragt.
        var before = files.LinksRead.Count;
        var plain = new RemoteEntry("datei", IsDirectory: false, Size: 1, DateTimeOffset.MinValue);
        Assert.Same(plain, await browser.WithTargetAsync(plain));
        Assert.Equal(before, files.LinksRead.Count);
    }

    [Fact]
    public async Task Going_to_the_target_shows_its_folder_and_selects_it_but_a_link_into_nothing_has_none()
    {
        var files = new MockRemoteFileSystem("/var/www")
            .File("/var/etc/app.conf", 10, hoursAgo: 1)
            .File("/var/releases/42/index.html", 100, hoursAgo: 1)
            .Link("/var/www/conf", "../etc/app.conf")
            .Link("/var/www/aktuell", "../releases/42")
            .Link("/var/www/tot", "../fehlt");
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        var (conf, current, dead) = (browser.Entries.Single(entry => entry.Name == "conf"), browser.Entries.Single(entry => entry.Name == "aktuell"), browser.Entries.Single(entry => entry.Name == "tot"));
        Assert.True(dead.IsDangling);

        Assert.True(await browser.GoToTargetAsync(conf));
        Assert.Equal("/var/etc", browser.CurrentPath);
        Assert.Equal("app.conf", browser.SelectedEntry?.Name);

        await browser.NavigateToAsync("/var/www");
        Assert.True(await browser.GoToTargetAsync(current));
        Assert.Equal("/var/releases/42", browser.CurrentPath);

        await browser.NavigateToAsync("/var/www");
        Assert.False(await browser.GoToTargetAsync(dead));
        Assert.Equal("/var/www", browser.CurrentPath);
    }

    [Fact]
    public async Task What_a_download_would_create_comes_from_the_core_with_the_names_of_this_computer()
    {
        var (_, transport, files) = Connected();

        var asking = files.TreeAsync("/var/www");
        var request = Assert.Single(transport.Sent("sftp.tree"));
        Assert.Equal("/var/www", request.GetProperty("path").GetString());
        Assert.Equal(1UL, request.GetProperty("session").GetUInt64());
        Assert.False(asking.IsCompleted);

        transport.Raise(
            $$"""{"event":"sftp.result","request":{{request.GetProperty("request").GetUInt64()}},"ok":true,"tree":[{"remote":"/var/www","local":"www","directory":true,"size":0,"modified":1790000000},{"remote":"/var/www/a:b.txt","local":"www/a_b.txt","directory":false,"size":12}]}""");

        Assert.Equal(
            [
                new RemoteTreeEntry("/var/www", "www", true, 0, DateTimeOffset.FromUnixTimeSeconds(1790000000)),
                new RemoteTreeEntry("/var/www/a:b.txt", "www/a_b.txt", false, 12, DateTimeOffset.MinValue),
            ],
            await asking);

        // Zu viel für eine Auskunft vorab: Das sagt der Kern, statt eine endlose Liste zu schicken.
        var huge = files.TreeAsync("/");
        var second = transport.Sent("sftp.tree").Last();
        transport.Raise($$"""{"event":"sftp.result","request":{{second.GetProperty("request").GetUInt64()}},"ok":false,"error":"too_many","message":"more than 20000 entries"}""");
        Assert.Equal("too_many", (await Assert.ThrowsAsync<RemoteFileException>(() => huge)).Code);
    }

    [Fact]
    public async Task A_refused_operation_throws_with_the_servers_reason()
    {
        var (_, transport, files) = Connected();

        var deleting = files.DeleteAsync("/etc/passwd");
        var request = Assert.Single(transport.Sent("sftp.delete"));
        transport.Raise($$"""{"event":"sftp.result","request":{{request.GetProperty("request").GetUInt64()}},"ok":false,"error":"permission_denied","message":"Permission denied"}""");

        var error = await Assert.ThrowsAsync<RemoteFileException>(() => deleting);
        Assert.Equal(("permission_denied", "Permission denied"), (error.Code, error.Message));
    }

    [Fact]
    public async Task An_operation_the_core_does_not_even_start_throws_at_once()
    {
        var (_, transport, files) = Connected();
        transport.Responses["sftp.mkdir"] = """{"ok":false,"error":"no_sftp","message":"1"}""";

        var error = await Assert.ThrowsAsync<RemoteFileException>(() => files.CreateDirectoryAsync("/x"));

        Assert.Equal("no_sftp", error.Code);
    }

    [Fact]
    public async Task A_new_empty_file_appears_in_the_list_and_never_replaces_one_that_exists()
    {
        var (browser, _, _) = await BrowserAsync();

        Assert.True(await browser.CreateFileAsync(" notes.txt "));

        Assert.Equal(["html", "nginx.conf", "notes.txt"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal((false, 0), (browser.Entries[2].IsDirectory, browser.Entries[2].Size));

        // Eine vorhandene Datei würde durch Anlegen geleert.
        Assert.False(await browser.CreateFileAsync("nginx.conf"));
        Assert.True(browser.HasError);
        Assert.Equal(200, browser.Entries.Single(entry => entry.Name == "nginx.conf").Size);

        Assert.False(await browser.CreateFileAsync("a/b"));
        Assert.Equal("Not a valid name.", browser.Error);
    }

    [Fact]
    public async Task A_link_to_an_entry_appears_beside_it_and_never_replaces_one_that_exists()
    {
        var (browser, _, _) = await BrowserAsync();
        var folder = browser.Entries.Single(entry => entry.Name == "html");

        Assert.True(await browser.CreateLinkAsync(folder, " current "));

        var link = browser.Entries.Single(entry => entry.Name == "current");
        Assert.True(link is { IsLink: true, IsDirectory: true });

        Assert.False(await browser.CreateLinkAsync(folder, "nginx.conf"));
        Assert.True(browser.HasError);
        Assert.False(browser.Entries.Single(entry => entry.Name == "nginx.conf").IsLink);
    }

    [Fact]
    public async Task A_link_windows_refuses_names_the_reason_there()
    {
        var (browser, files, _) = await BrowserAsync();
        var folder = browser.Entries.Single(entry => entry.Name == "html");
        files.LinksDenied = true;

        // Anderswo der gewöhnliche Satz …
        Assert.False(await browser.CreateLinkAsync(folder, "current"));
        Assert.Equal("Permission denied.", browser.Error);

        // … unter Windows mit dem Grund: Links anlegen dürfen dort meist nur Administratoren.
        browser.IsWindowsServer = true;
        Assert.False(await browser.CreateLinkAsync(folder, "current"));
        Assert.Equal("Permission denied – on Windows, usually only administrators may create links.", browser.Error);
        Assert.DoesNotContain(browser.Entries, entry => entry.Name == "current");

        // Andere Gründe bleiben, wie sie sind.
        files.LinksDenied = false;
        Assert.False(await browser.CreateLinkAsync(folder, "nginx.conf"));
        Assert.StartsWith("That name already exists.", browser.Error);
    }

    [Fact]
    public async Task A_search_lists_matches_below_the_folder_and_a_click_reveals_one_in_its_place()
    {
        var (browser, _, _) = await BrowserAsync();
        browser.OpenSearch();
        Assert.True(browser.IsSearchOpen);

        browser.SearchText = " CSS ";
        await browser.SearchAsync();

        // Der Ordner „css“ und die Datei „site.css“ darin – der nähere Treffer zuerst.
        Assert.Equal(["/var/www/html/css", "/var/www/html/css/site.css"], browser.Hits.Select(hit => hit.Path));
        Assert.Equal(("css", "/var/www/html", true), (browser.Hits[0].Name, browser.Hits[0].Location, browser.Hits[0].IsDirectory));
        Assert.Equal("2 matches", browser.SearchStatus);
        Assert.False(browser.IsSearching);

        Assert.True(await browser.RevealAsync(browser.Hits[1]));

        Assert.Equal("/var/www/html/css", browser.CurrentPath);
        Assert.Equal("site.css", browser.SelectedEntry?.Name);
        Assert.False(browser.IsSearchOpen);
        Assert.Empty(browser.Hits);

        // „Zurück“ führt dorthin, wo gesucht wurde.
        Assert.True(browser.CanGoBack);
    }

    [Fact]
    public async Task A_search_says_when_nothing_matches_and_shows_nothing_without_a_text()
    {
        var (browser, _, _) = await BrowserAsync();
        browser.OpenSearch();

        browser.SearchText = "gibt-es-nicht";
        await browser.SearchAsync();
        Assert.Empty(browser.Hits);
        Assert.Equal("Nothing found.", browser.SearchStatus);

        browser.SearchText = "nginx";
        await browser.SearchAsync();
        Assert.Equal("1 match", browser.SearchStatus);

        browser.SearchText = "   ";
        await browser.SearchAsync();
        Assert.Empty(browser.Hits);
        Assert.Equal(string.Empty, browser.SearchStatus);

        // Eine neue Verbindung beginnt ohne die Suche der alten.
        browser.SearchText = "nginx";
        await browser.SearchAsync();
        browser.Attach(null);
        Assert.False(browser.IsSearchOpen);
        Assert.Empty(browser.Hits);
    }

    [Fact]
    public async Task A_search_that_stopped_early_says_so_instead_of_looking_complete()
    {
        var (browser, files, _) = await BrowserAsync();
        browser.OpenSearch();
        files.FindStopsAfter = 200_000;

        // „Nichts gefunden“ hieße sonst „gibt es nicht“ – dabei wurde nur ein Teil angesehen.
        browser.SearchText = "gibt-es-nicht";
        await browser.SearchAsync();
        Assert.Equal("Nothing found – the search stopped after 200,000 entries. Search in a subfolder to cover the rest.", browser.SearchStatus);

        browser.SearchText = "nginx";
        await browser.SearchAsync();
        Assert.StartsWith("1 match – the search stopped after 200,000 entries.", browser.SearchStatus, StringComparison.Ordinal);

        files.FindStopsAfter = null;
        await browser.SearchAsync();
        Assert.Equal("1 match", browser.SearchStatus);
    }

    [Fact]
    public void Searching_sends_the_folder_and_the_text()
    {
        var (_, transport, files) = Connected();

        _ = files.FindAsync("/var/www", "nginx");

        var request = Assert.Single(transport.Sent("sftp.find"));
        Assert.Equal(("/var/www", "nginx"), (request.GetProperty("path").GetString(), request.GetProperty("name").GetString()));
    }

    [Fact]
    public void Creating_a_file_sends_its_path()
    {
        var (_, transport, files) = Connected();

        _ = files.CreateFileAsync("/var/www/notes.txt");

        Assert.Equal("/var/www/notes.txt", Assert.Single(transport.Sent("sftp.touch")).GetProperty("path").GetString());
    }

    [Fact]
    public void Creating_a_link_sends_where_it_lies_and_what_it_points_to()
    {
        var (_, transport, files) = Connected();

        _ = files.CreateLinkAsync("/var/www/current", "/var/www/html");

        var request = Assert.Single(transport.Sent("sftp.symlink"));
        Assert.Equal(("/var/www/current", "/var/www/html"), (request.GetProperty("path").GetString(), request.GetProperty("to").GetString()));
    }

    [Fact]
    public void Rename_sends_both_paths()
    {
        var (_, transport, files) = Connected();

        _ = files.RenameAsync("/a.txt", "/b.txt");

        var request = Assert.Single(transport.Sent("sftp.rename"));
        Assert.Equal(("/a.txt", "/b.txt"), (request.GetProperty("from").GetString(), request.GetProperty("to").GetString()));
    }

    [Fact]
    public void Transfers_are_queued_at_once_and_follow_the_cores_events()
    {
        var (backend, transport, files) = Connected();
        var changes = 0;
        backend.Transfers.Changed += () => changes++;

        files.Upload(LocalPath.Of("data", "backup.tar.gz"), "/var/backups");
        files.Download("/var/log/app.log", @"D:\downloads");

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal(("upload", LocalPath.Of("data", "backup.tar.gz"), "/var/backups"), (sent[0].GetProperty("direction").GetString(), sent[0].GetProperty("local").GetString(), sent[0].GetProperty("remote").GetString()));
        Assert.Equal(("download", @"D:\downloads", "/var/log/app.log"), (sent[1].GetProperty("direction").GetString(), sent[1].GetProperty("local").GetString(), sent[1].GetProperty("remote").GetString()));
        Assert.Equal(["backup.tar.gz", "app.log"], backend.Transfers.Items.Select(item => item.FileName));
        Assert.All(backend.Transfers.Items, item => Assert.Equal(("web01", TransferState.Queued), (item.Server, item.State)));

        var first = sent[0].GetProperty("transfer").GetUInt64();
        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"running","bytes":250,"total":1000}""");
        var running = backend.Transfers.Items[0];
        Assert.Equal((TransferState.Running, 0.25, 250L, 1000L), (running.State, running.Progress, running.Bytes, running.Total));

        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"failed","bytes":250,"total":1000,"message":"connection: lost"}""");
        Assert.Equal((TransferState.Failed, "connection: lost"), (backend.Transfers.Items[0].State, backend.Transfers.Items[0].Detail));
        Assert.True(changes >= 4);
    }

    [Fact]
    public void The_rule_for_what_exists_reaches_the_core_and_a_retry_keeps_it()
    {
        var (backend, transport, files) = Connected();

        files.Upload(LocalPath.Of("site"), "/srv", keep: TransferKeep.Same);
        files.Download("/srv/site", @"D:\downloads", keep: TransferKeep.Newer, localName: "site (2)");
        files.Upload(LocalPath.Of("a.txt"), "/srv");

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal("same", sent[0].GetProperty("keep").GetString());
        Assert.Equal(("newer", "site (2)"), (sent[1].GetProperty("keep").GetString(), sent[1].GetProperty("name").GetString()));
        Assert.False(sent[2].TryGetProperty("keep", out _));
        Assert.Equal("site (2)", backend.Transfers.Items[1].FileName);

        // Ein neuer Versuch überschreibt nicht, was der erste stehen lassen sollte.
        var first = sent[0].GetProperty("transfer").GetUInt64();
        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"failed","bytes":0,"total":1,"message":"connection: lost"}""");
        Assert.True(backend.Transfers.Retry(first.ToString()));
        Assert.Equal("same", transport.Sent("transfer.start").Last().GetProperty("keep").GetString());
    }

    [Fact]
    public void An_upload_can_name_the_file_it_becomes_on_the_server()
    {
        var (backend, transport, files) = Connected();

        files.Upload(LocalPath.Of("edit", "plan 12_30.txt"), "/srv", "plan 12:30.txt");
        files.Upload(LocalPath.Of("data", "a.txt"), "/srv");

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal("plan 12:30.txt", sent[0].GetProperty("name").GetString());
        Assert.False(sent[1].TryGetProperty("name", out _));
        Assert.Equal(["plan 12:30.txt", "a.txt"], backend.Transfers.Items.Select(item => item.FileName));

        // Auch ein neuer Versuch geht unter diesem Namen hinauf.
        var first = sent[0].GetProperty("transfer").GetUInt64();
        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"failed","bytes":0,"total":1,"message":"connection: lost"}""");
        Assert.True(backend.Transfers.Retry(first.ToString()));
        Assert.Equal("plan 12:30.txt", transport.Sent("transfer.start").Last().GetProperty("name").GetString());
    }

    [Fact]
    public void The_core_is_asked_where_a_download_would_write()
    {
        var (_, transport, files) = Connected();
        var folder = LocalPath.Of("downloads");
        var target = LocalPath.Of("downloads", "app-12_00.log");
        transport.Responses["transfer.target"] = $$"""{"ok":true,"path":{{System.Text.Json.JsonSerializer.Serialize(target)}}}""";

        Assert.Equal(target, files.LocalTarget("/var/log/app-12:00.log", folder));

        var request = Assert.Single(transport.Sent("transfer.target"));
        Assert.Equal((folder, "/var/log/app-12:00.log"), (request.GetProperty("local").GetString(), request.GetProperty("remote").GetString()));

        // Ein Name, aus dem sich kein lokaler machen lässt.
        transport.Responses["transfer.target"] = """{"ok":false,"error":"unsafe_name","message":"/"}""";
        Assert.Null(files.LocalTarget("/", folder));
    }

    [Fact]
    public async Task The_overwrite_question_names_what_already_lies_in_the_chosen_folder()
    {
        var (browser, files, _) = await BrowserAsync();
        files.File("/var/www/a\0b.txt", 1, hoursAgo: 1).Special("/var/www/queue");
        await browser.RefreshCommand.ExecuteAsync(null);
        using var local = new TempFolder();
        local.File("nginx.conf");
        local.File("a_b.txt");
        local.File("queue");
        Directory.CreateDirectory(Path.Combine(local.Path, "html"));

        // Gefragt wird nach dem lokalen Namen – auch dort, wo er vom Namen auf dem Server abweicht. Was gar nicht
        // übertragen wird, überschreibt auch nichts.
        Assert.Equal(["html", "a_b.txt", "nginx.conf"], browser.ExistingLocally(browser.Entries, local.Path));
        Assert.Empty(browser.ExistingLocally(browser.Entries, Path.Combine(local.Path, "html")));
    }

    [Fact]
    public void A_transfer_the_core_refuses_shows_as_failed()
    {
        var (backend, transport, files) = Connected();
        transport.Responses["transfer.start"] = """{"ok":false,"error":"no_sftp","message":"1"}""";

        files.Upload(LocalPath.Of("data", "a.txt"), "/");

        var item = Assert.Single(backend.Transfers.Items);
        Assert.Equal((TransferState.Failed, "no_sftp: 1"), (item.State, item.Detail));
    }

    [Fact]
    public void Cancel_names_the_transfer_and_clearing_keeps_what_still_runs()
    {
        var (backend, transport, files) = Connected();
        files.Upload(LocalPath.Of("data", "a.txt"), "/");
        files.Upload(LocalPath.Of("data", "b.txt"), "/");
        var ids = transport.Sent("transfer.start").Select(request => request.GetProperty("transfer").GetUInt64()).ToList();

        backend.Transfers.Cancel(backend.Transfers.Items[1].Id);
        Assert.Equal(ids[1], Assert.Single(transport.Sent("transfer.cancel")).GetProperty("transfer").GetUInt64());

        transport.Raise($$"""{"event":"transfer.state","transfer":{{ids[1]}},"state":"cancelled","bytes":0,"total":0}""");
        transport.Raise($$"""{"event":"transfer.state","transfer":{{ids[0]}},"state":"completed","bytes":5,"total":5}""");
        Assert.Equal(1, backend.Transfers.Items[0].Progress);

        files.Upload(LocalPath.Of("data", "c.txt"), "/");
        backend.Transfers.ClearFinished();
        Assert.Equal("c.txt", Assert.Single(backend.Transfers.Items).FileName);
    }

    [Fact]
    public async Task Exclusions_reach_the_core_with_the_transfer_and_survive_a_retry()
    {
        var (backend, transport, files) = Connected();

        files.Upload(LocalPath.Of("site"), "/srv", exclude: [".git", "*.tmp"]);
        files.Download("/srv/site", @"D:\downloads", exclude: ["node_modules"]);
        files.Upload(LocalPath.Of("a.txt"), "/srv");
        files.Upload(LocalPath.Of("b"), "/srv", exclude: []);

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal([".git", "*.tmp"], sent[0].GetProperty("exclude").EnumerateArray().Select(pattern => pattern.GetString()));
        Assert.Equal(["node_modules"], sent[1].GetProperty("exclude").EnumerateArray().Select(pattern => pattern.GetString()));
        Assert.False(sent[2].TryGetProperty("exclude", out _));
        Assert.False(sent[3].TryGetProperty("exclude", out _));

        // Ein neuer Versuch nimmt nicht mit, was der erste auslassen sollte.
        var first = sent[0].GetProperty("transfer").GetUInt64();
        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"failed","bytes":0,"total":1,"message":"connection: lost"}""");
        Assert.True(backend.Transfers.Retry(first.ToString()));
        Assert.Equal([".git", "*.tmp"], transport.Sent("transfer.start").Last().GetProperty("exclude").EnumerateArray().Select(pattern => pattern.GetString()));

        // Die Auskunft vorab: derselbe Baum, mit den Mustern – und auf Wunsch nur das, was fehlen würde.
        transport.Responses["sftp.tree"] = """{"ok":true}""";
        var asking = files.TreeAsync("/srv/site", [".git"], excluded: true);
        var tree = transport.Sent("sftp.tree").Last();
        Assert.Equal((".git", true), (tree.GetProperty("exclude")[0].GetString(), tree.GetProperty("excluded").GetBoolean()));
        transport.Raise($$"""{"event":"sftp.result","request":{{tree.GetProperty("request").GetUInt64()}},"ok":true,"tree":[{"remote":"/srv/site/.git","local":"site/.git","directory":true,"size":0}]}""");
        Assert.Equal(("/srv/site/.git", true), ((await asking)[0].RemotePath, (await asking)[0].IsDirectory));

        var plain = files.TreeAsync("/srv/site");
        var without = transport.Sent("sftp.tree").Last();
        Assert.False(without.TryGetProperty("exclude", out _) || without.TryGetProperty("excluded", out _));
        transport.Raise($$"""{"event":"sftp.result","request":{{without.GetProperty("request").GetUInt64()}},"ok":true,"tree":[]}""");
        await plain;
    }
}
