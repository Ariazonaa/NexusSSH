using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ordner abgleichen: nur übertragen, was am Ziel fehlt, anders groß oder älter ist.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FolderSyncTests
{
    public FolderSyncTests() => L.Init(AppLanguage.English);

    // Die Zeiten des Mock-Dateisystems liegen fest in der Vergangenheit; „älter“ und „neuer“ beziehen sich darauf.
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Recent = DateTime.UtcNow;

    private static MockRemoteFileSystem Server()
        => new MockRemoteFileSystem("/srv/site")
            .File("/srv/site/index.html", size: 5, hoursAgo: 10)
            .File("/srv/site/css/site.css", size: 7, hoursAgo: 10)
            .File("/srv/site/only-remote.txt", size: 3, hoursAgo: 10);

    private static void Write(TempFolder folder, string relative, int size, DateTime modified)
    {
        var path = Path.Combine(folder.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, modified);
    }

    [Fact]
    public async Task What_the_patterns_leave_out_is_neither_transferred_nor_counted_as_extra()
    {
        using var folder = new TempFolder();
        var server = Server()
            .File("/srv/site/.git/config", size: 3, hoursAgo: 10)
            .File("/srv/site/cache/old.tmp", size: 3, hoursAgo: 10);
        Write(folder, "index.html", 5, Old);
        Write(folder, "css/site.css", 9, Old);
        Write(folder, "node_modules/left-pad/index.js", 4, Old);
        Write(folder, "notes.tmp", 1, Old);
        Write(folder, "js/app.js", 2, Old);
        string[] exclude = [".git", "node_modules", "*.tmp"];

        var up = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload, exclude: exclude);

        Assert.Equal(["css/site.css", "js/app.js"], up.Items.Select(item => item.RelativePath));

        // Nur am Ziel liegt, was nicht ausgelassen wird: `.git` und die .tmp-Datei dort bleiben unberührt, auch wenn
        // der Benutzer „Überzähliges löschen“ wählt.
        Assert.Equal(["only-remote.txt"], up.Extra.Select(extra => extra.RelativePath));

        var down = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Download, exclude: exclude);
        Assert.Contains("only-remote.txt", down.Items.Select(item => item.RelativePath));
        Assert.DoesNotContain(down.Items, item => item.RelativePath.StartsWith(".git/", StringComparison.Ordinal) || item.RelativePath.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(["js/app.js"], down.Extra.Select(extra => extra.RelativePath));

        // Ohne Muster ist alles dabei, wie immer.
        var all = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload);
        Assert.Contains("node_modules/left-pad/index.js", all.Items.Select(item => item.RelativePath));
        Assert.Contains(".git/config", all.Extra.Select(extra => extra.RelativePath));
    }

    [Fact]
    public async Task Uploading_takes_what_is_missing_larger_or_newer_and_nothing_else()
    {
        using var folder = new TempFolder();
        var server = Server();
        Write(folder, "index.html", 5, Old);
        Write(folder, "css/site.css", 9, Old);
        Write(folder, "js/new/app.js", 4, Old);
        Write(folder, "notes.txt", 1, Old);

        var plan = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload);

        Assert.Equal(["css/site.css", "js/new/app.js", "notes.txt"], plan.Items.Select(item => item.RelativePath));
        Assert.Equal([false, true, true], plan.Items.Select(item => item.IsNew));
        Assert.Equal((2, 1, 1, 14L), (plan.New, plan.Changed, plan.Unchanged, plan.Bytes));
        Assert.Equal("/srv/site/js/new/app.js", plan.Items[1].RemotePath);
        Assert.Empty(server.Uploads);

        await FolderSync.StartAsync(server, plan);

        // Jede Datei landet in ihrem Ordner; die fehlenden Ordner sind vorher entstanden.
        Assert.Equal(
            [("css/site.css", "/srv/site/css"), ("js/new/app.js", "/srv/site/js/new"), ("notes.txt", "/srv/site")],
            server.Uploads.Select(upload => (Path.GetRelativePath(folder.Path, upload.LocalPath).Replace('\\', '/'), upload.RemoteDirectory)));
        Assert.Contains(server.List("/srv/site/js"), entry => entry is { Name: "new", IsDirectory: true });

        // Was nur auf dem Server liegt, bleibt dort.
        Assert.Contains(server.List("/srv/site"), entry => entry.Name == "only-remote.txt");
    }

    [Fact]
    public async Task A_newer_local_file_of_the_same_size_counts_as_changed()
    {
        using var folder = new TempFolder();
        var server = Server();
        Write(folder, "index.html", 5, Recent);

        var plan = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload);

        Assert.Equal(("index.html", false), (Assert.Single(plan.Items).RelativePath, plan.Items[0].IsNew));
    }

    [Fact]
    public async Task Downloading_takes_what_is_missing_or_older_here()
    {
        using var folder = new TempFolder();
        var server = Server();
        Write(folder, "index.html", 5, Recent);
        Write(folder, "css/site.css", 7, Old);
        Write(folder, "only-local.txt", 1, Old);

        var plan = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Download);

        Assert.Equal(["css/site.css", "only-remote.txt"], plan.Items.Select(item => item.RelativePath));
        Assert.Equal([false, true], plan.Items.Select(item => item.IsNew));
        Assert.Equal(1, plan.Unchanged);

        await FolderSync.StartAsync(server, plan);

        Assert.Equal(
            [("/srv/site/css/site.css", Path.Combine(folder.Path, "css")), ("/srv/site/only-remote.txt", folder.Path)],
            server.Downloads);
        Assert.True(File.Exists(Path.Combine(folder.Path, "only-local.txt")));
    }

    [Fact]
    public async Task The_browser_plans_for_the_folder_it_shows_and_says_what_would_happen()
    {
        using var folder = new TempFolder();
        var server = Server();
        var browser = new SftpBrowserViewModel(server);
        await browser.LoadAsync();
        Write(folder, "index.html", 5, Old);
        Write(folder, "new.txt", 2048, Old);

        var plan = await browser.PlanSyncAsync(folder.Path, SyncDirection.Upload);

        Assert.NotNull(plan);
        Assert.Equal("To the server: 1 new, 0 changed (2.0 KB). Already the same: 1.", SftpBrowserViewModel.Describe(plan));
        Assert.True(await browser.StartSyncAsync(plan));
        Assert.Contains(browser.Entries, entry => entry.Name == "new.txt");

        // Danach gibt es nichts mehr zu tun.
        var again = await browser.PlanSyncAsync(folder.Path, SyncDirection.Upload);
        Assert.Empty(again!.Items);
        Assert.Equal("Nothing to transfer – all 2 files are already the same.", SftpBrowserViewModel.Describe(again));

        // Ein lokaler Ordner, den es nicht gibt, ist ein Fehler in der Leiste, keine Ausnahme.
        Assert.Null(await browser.PlanSyncAsync(Path.Combine(folder.Path, "missing"), SyncDirection.Upload));
        Assert.StartsWith("The local folder could not be read: ", browser.Error);
    }

    [Fact]
    public async Task Compared_by_content_the_time_does_not_count_and_equal_sizes_are_not_enough()
    {
        using var folder = new TempFolder();
        var server = Server();
        Write(folder, "index.html", 5, Recent);
        Write(folder, "css/site.css", 7, Old);

        // Nach Größe und Zeit: index.html ist hier neuer, also „geändert“; site.css gilt als gleich.
        var byTime = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload);
        Assert.Equal(["index.html"], byTime.Items.Select(item => item.RelativePath));

        // Nach Inhalt ist es umgekehrt: index.html ist dieselbe Datei, site.css nur gleich groß.
        var zeros = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[5]));
        server.Checksum = path => path.EndsWith("index.html", StringComparison.Ordinal) ? zeros : new string('f', 64);
        var byContent = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload, byContent: true);
        Assert.Equal(["css/site.css"], byContent.Items.Select(item => item.RelativePath));
        Assert.Equal(1, byContent.Unchanged);

        // In die andere Richtung gilt dasselbe; was hier fehlt, kommt ohnehin.
        var down = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Download, byContent: true);
        Assert.Equal(["css/site.css", "only-remote.txt"], down.Items.Select(item => item.RelativePath));

        // Nennt der Server gar keine Summen, scheitert der Vergleich – statt alles zu übertragen.
        server.Checksum = _ => null;
        var error = await Assert.ThrowsAsync<RemoteFileException>(() => FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload, byContent: true));
        Assert.Equal("command_failed", error.Code);
    }
}

/// <summary>Der Abgleich als ein Auftrag – und das Löschen am Ziel, das nur auf Wunsch geschieht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FolderSyncRestTests
{
    public FolderSyncRestTests() => L.Init(AppLanguage.English);

    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Write(TempFolder folder, string relative, int size)
    {
        var path = Path.Combine(folder.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, Old);
    }

    [Fact]
    public async Task What_lies_only_at_the_destination_is_listed_and_deleted_only_on_request()
    {
        using var folder = new TempFolder();
        var server = new MockRemoteFileSystem("/srv/site")
            .File("/srv/site/index.html", size: 5, hoursAgo: 10)
            .File("/srv/site/old/gone.txt", size: 3, hoursAgo: 10);
        Write(folder, "index.html", 5);
        Write(folder, "only-local.txt", 1);

        var up = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Upload);
        Assert.Equal([("old/gone.txt", "/srv/site/old/gone.txt")], up.Extra.Select(extra => (extra.RelativePath, extra.Path)));

        // Ohne die Wahl bleibt es liegen.
        await FolderSync.StartAsync(server, up);
        Assert.Contains(server.List("/srv/site/old"), entry => entry.Name == "gone.txt");

        await FolderSync.StartAsync(server, up, deleteExtra: true);
        Assert.Empty(server.List("/srv/site/old"));
        Assert.Contains(server.List("/srv/site"), entry => entry.Name == "index.html");

        // In die andere Richtung liegt das Überzählige hier („only-local.txt“ ist inzwischen hochgeladen).
        Write(folder, "draft.txt", 1);
        var down = await FolderSync.PlanAsync(server, folder.Path, "/srv/site", SyncDirection.Download);
        var extra = Assert.Single(down.Extra);
        Assert.Equal(("draft.txt", Path.Combine(folder.Path, "draft.txt")), (extra.RelativePath, extra.Path));

        await FolderSync.StartAsync(server, down);
        Assert.True(File.Exists(extra.Path));
        await FolderSync.StartAsync(server, down, deleteExtra: true);
        Assert.False(File.Exists(extra.Path));
        Assert.True(File.Exists(Path.Combine(folder.Path, "index.html")));
    }

    [Fact]
    public void The_core_gets_a_sync_as_one_folder_transfer_limited_to_the_files()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        var local = Path.Combine(Path.GetTempPath(), "nexus-sync-test", "mine");

        Assert.NotNull(connection.Files!.UploadSome(local, "/srv/site", ["css/site.css", "new.txt"]));

        var up = transport.Sent("transfer.start").Last();
        Assert.Equal(("upload", local, "/srv", "site"), (up.GetProperty("direction").GetString(), up.GetProperty("local").GetString(), up.GetProperty("remote").GetString(), up.GetProperty("name").GetString()));
        Assert.Equal(["css/site.css", "new.txt"], up.GetProperty("only").EnumerateArray().Select(entry => entry.GetString()));

        Assert.NotNull(connection.Files.DownloadSome("/srv/site", local, ["a.txt"]));

        var down = transport.Sent("transfer.start").Last();
        Assert.Equal(("download", Path.GetDirectoryName(local), "/srv/site", "mine"), (down.GetProperty("direction").GetString(), down.GetProperty("local").GetString(), down.GetProperty("remote").GetString(), down.GetProperty("name").GetString()));
        Assert.Equal(["a.txt"], down.GetProperty("only").EnumerateArray().Select(entry => entry.GetString()));

        // Die Wurzel des Servers hat keinen Namen: Dann geht es nur Datei für Datei.
        Assert.Null(connection.Files.UploadSome(local, "/", ["a.txt"]));

        // Ein gewöhnlicher Transfer nennt keine Auswahl.
        connection.Files.Download("/srv/site/a.txt", local);
        Assert.False(transport.Sent("transfer.start").Last().TryGetProperty("only", out _));
    }
}
