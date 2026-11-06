using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Dateien und ganze Ordner für ein anderes Programm – beim Ziehen aus der Liste und nach „Kopieren“: Der
/// Empfänger bekommt vorab die Liste samt Unterordnern, geladen wird erst, wenn er den Inhalt liest.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VirtualDropTests : IDisposable
{
    private static readonly DateTimeOffset Then = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DropItem Readme = new("/srv/readme.txt", "readme.txt", IsDirectory: false, 3, Then);
    private static readonly DropItem Www = new("/srv/www", "www", IsDirectory: true);

    private readonly TempFolder _temp = new();
    private readonly MockTransferQueue _queue = new();
    private readonly MockRemoteFileSystem _files;
    private readonly DropFetcher _fetcher;
    private readonly List<(DropNotice Notice, string? Detail)> _notices = [];

    public VirtualDropTests()
    {
        L.Init(AppLanguage.English);
        _fetcher = new DropFetcher(Path.Combine(_temp.Path, "drag"));
        _files = new MockRemoteFileSystem("/srv", _queue, "web01")
            .File("/srv/readme.txt", 3, hoursAgo: 1)
            .File("/srv/www/index.html", 10, hoursAgo: 2)
            .File("/srv/www/css/site.css", 7, hoursAgo: 3)
            .File("/srv/www/a\0b.txt", 4, hoursAgo: 4)
            .Special("/srv/www/events.fifo");
    }

    public void Dispose()
    {
        _fetcher.Dispose();
        _temp.Dispose();
    }

    private VirtualDrop Drop(params DropItem[] items)
        => new(_fetcher, _queue, () => _files, items, (notice, detail) => _notices.Add((notice, detail)));

    // Der Mock überträgt nichts; die Datei entsteht hier, als hätte der Download sie geschrieben.
    private string Arrive(int download, string relative, string content)
    {
        var local = Path.Combine(_files.Downloads[download].LocalDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, content);
        return local;
    }

    private string TransferOf(int download) => _queue.Items[^(_files.Downloads.Count - download)].Id;

    // Lesen wartet, bis der Inhalt da ist – deshalb in einem eigenen Thread, wie beim Empfänger.
    private static async Task<string> ReadAsync(Task<Stream?> opening)
    {
        await using var stream = Assert.IsAssignableFrom<Stream>(await opening);
        return await Task.Run(() => Rest(stream), TestContext.Current.CancellationToken);
    }

    private static string Rest(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }

    // Wie der Download: erst unter dem vorläufigen Namen schreiben …
    private string Partial(int download, string relative, string content)
    {
        var partial = Path.Combine(_files.Downloads[download].LocalDirectory, relative.Replace('/', Path.DirectorySeparatorChar)) + VirtualDrop.PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        using var file = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Write(System.Text.Encoding.UTF8.GetBytes(content));
        return partial;
    }

    // … und zum Schluss umbenennen.
    private static void Finish(string partial) => File.Move(partial, partial[..^VirtualDrop.PartialSuffix.Length]);

    // Wartet, bis so viele Downloads begonnen haben – der Aufruf läuft in einem anderen Thread weiter.
    private async Task StartedAsync(int downloads)
    {
        for (var i = 0; i < 500 && _files.Downloads.Count < downloads; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(downloads, _files.Downloads.Count);
    }

    private static async Task GoneAsync(string directory)
    {
        for (var i = 0; i < 300 && Directory.Exists(directory); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task The_receiver_gets_the_whole_list_before_anything_is_loaded()
    {
        var drop = Drop(Readme, Www);

        var entries = Assert.IsAssignableFrom<IReadOnlyList<VirtualEntry>>(await drop.EntriesAsync());

        // Die Datei, wie sie ausgewählt wurde; der Ordner mit allem darin – Verzeichnisse vor ihrem Inhalt, mit
        // den Namen dieses Rechners, ohne die Pipe.
        Assert.Equal(
            ["readme.txt", "www", "www/css", "www/a_b.txt", "www/index.html", "www/css/site.css"],
            entries.Select(entry => entry.Path));
        Assert.Equal([false, true, true, false, false, false], entries.Select(entry => entry.IsDirectory));
        Assert.Equal([3, 0, 0, 4, 10, 7], entries.Select(entry => entry.Size));
        Assert.Equal(Then, entries[0].Modified);
        Assert.Empty(_files.Downloads);
        Assert.Empty(_notices);
        Assert.Same(entries, await drop.EntriesAsync());
        Assert.Equal(2, drop.Count);
    }

    [Fact]
    public async Task Reading_a_file_starts_one_download_for_what_was_selected_and_waits_for_the_file()
    {
        var drop = Drop(Readme, Www);

        var index = drop.OpenAsync(4);
        await StartedAsync(1);

        // Der ganze Ordner wird geholt, in einen eigenen Zwischenordner – nicht die Datei allein.
        Assert.Equal("/srv/www", Assert.Single(_files.Downloads).RemotePath);
        Assert.False(index.IsCompleted);

        // Eine zweite Datei desselben Ordners wartet auf denselben Download.
        var css = drop.OpenAsync(5);
        Arrive(0, "www/css/site.css", "body {}");
        Assert.Equal("body {}", await ReadAsync(css));
        Assert.Single(_files.Downloads);
        Assert.False(index.IsCompleted);

        Arrive(0, "www/index.html", "<h1>hi</h1>");
        Assert.Equal("<h1>hi</h1>", await ReadAsync(index));

        // Die einzelne Datei ist ein eigener Download in einen eigenen Ordner.
        var readme = drop.OpenAsync(0);
        await StartedAsync(2);
        Assert.Equal(2, _files.Downloads.Count);
        Assert.NotEqual(_files.Downloads[0].LocalDirectory, _files.Downloads[1].LocalDirectory);
        Arrive(1, "readme.txt", "abc");
        Assert.Equal("abc", await ReadAsync(readme));
        Assert.Equal([(DropNotice.Fetching, null)], _notices);
    }

    [Fact]
    public async Task The_content_is_handed_on_while_it_is_still_loading()
    {
        var drop = Drop(Readme);
        var opening = drop.OpenAsync(0);
        await StartedAsync(1);
        Assert.False(opening.IsCompleted);

        // Der Download hat begonnen: Was schon da ist, lässt sich lesen.
        var partial = Partial(0, "readme.txt", "abc");
        await using var stream = Assert.IsAssignableFrom<Stream>(await opening);
        var start = new byte[2];
        Assert.Equal(2, await Task.Run(() => stream.Read(start, 0, 2), TestContext.Current.CancellationToken));
        Assert.Equal("ab"u8.ToArray(), start);

        // Der Rest wartet auf den Download – bis die Datei unter ihrem Namen liegt, ist sie nicht zu Ende.
        var rest = Task.Run(() => Rest(stream), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(rest.IsCompleted);

        Partial(0, "readme.txt", "def");
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Assert.False(rest.IsCompleted);

        Finish(partial);
        Assert.Equal("cdef", await rest);
    }

    [Fact]
    public async Task A_download_that_breaks_off_fails_the_reader_instead_of_ending_the_file()
    {
        var drop = Drop(Readme);
        var opening = drop.OpenAsync(0);
        await StartedAsync(1);
        var partial = Partial(0, "readme.txt", "abc");
        await using var stream = Assert.IsAssignableFrom<Stream>(await opening);
        var rest = Task.Run(() => Rest(stream), TestContext.Current.CancellationToken);
        await Task.Delay(120, TestContext.Current.CancellationToken);

        // Der Transfer scheitert und räumt seine Teil-Datei weg: Ein halber Inhalt darf nicht als ganzer gelten.
        _queue.Fail(TransferOf(0), "connection: lost");
        File.Delete(partial);

        await Assert.ThrowsAsync<IOException>(() => rest);

        // Hat jemand den Transfer abgebrochen, ist das kein Fehler, sondern ein Abbruch.
        var again = Drop(Readme);
        var second = again.OpenAsync(0);
        await StartedAsync(2);
        var other = Partial(1, "readme.txt", "abc");
        await using var cancelled = Assert.IsAssignableFrom<Stream>(await second);
        var waiting = Task.Run(() => Rest(cancelled), TestContext.Current.CancellationToken);
        _queue.Cancel(TransferOf(1));
        File.Delete(other);
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task A_file_that_never_came_is_missing_and_a_folder_has_no_content()
    {
        var drop = Drop(Www);

        // Der Transfer endet, ohne dass die Datei da ist – gescheitert, oder es gibt sie nicht mehr.
        var index = drop.OpenAsync(3);
        await StartedAsync(1);
        Arrive(0, "www/css/site.css", "body {}");
        _queue.Fail(TransferOf(0), "permission_denied: Permission denied");
        Assert.Null(await index);

        // Was schon da war, gibt es trotzdem.
        Assert.Equal("body {}", await ReadAsync(drop.OpenAsync(4)));

        Assert.Null(await drop.OpenAsync(0));
        Assert.Null(await drop.OpenAsync(99));
        Assert.Null(await drop.OpenAsync(-1));
    }

    [Fact]
    public async Task When_the_receiver_is_done_what_still_loads_stops_and_the_copies_go()
    {
        var drop = Drop(Readme, Www);
        var index = drop.OpenAsync(4);
        await StartedAsync(1);
        var folder = _files.Downloads[0].LocalDirectory;
        Arrive(0, "www/css/site.css", "body {}");

        drop.Completed(succeeded: false);

        Assert.Null(await index);
        Assert.Equal(TransferState.Cancelled, _queue.Items[^1].State);
        await GoneAsync(folder);

        // Ein zweites Mal schadet nicht.
        drop.Completed(succeeded: true);
        Assert.Single(_files.Downloads);
    }

    [Fact]
    public async Task The_same_offer_can_be_collected_again_and_starts_afresh()
    {
        // Aus der Zwischenablage lässt sich mehrmals einfügen: Jedes Mal wird neu geholt – und neu nachgesehen,
        // was in den Ordnern liegt.
        var drop = Drop(Readme, Www);
        var first = (await drop.EntriesAsync())!;
        var readme = drop.OpenAsync(0);
        await StartedAsync(1);
        Arrive(0, "readme.txt", "abc");
        _queue.Complete(TransferOf(0));
        Assert.Equal("abc", await ReadAsync(readme));
        drop.Completed(succeeded: true);
        await GoneAsync(_files.Downloads[0].LocalDirectory);

        _files.File("/srv/www/new.txt", 1, hoursAgo: 0);
        var second = (await drop.EntriesAsync())!;
        Assert.NotSame(first, second);
        Assert.Contains(second, entry => entry.Path == "www/new.txt");

        var again = drop.OpenAsync(0);
        await StartedAsync(2);
        Assert.Equal(2, _files.Downloads.Count);
        Arrive(1, "readme.txt", "abcd");
        _queue.Complete(TransferOf(1));
        Assert.Equal("abcd", await ReadAsync(again));
        Assert.Equal([(DropNotice.Fetching, null)], _notices);
    }

    [Fact]
    public async Task After_a_successful_drop_the_copies_go_as_well()
    {
        var drop = Drop(Readme);
        var readme = drop.OpenAsync(0);
        await StartedAsync(1);
        var folder = _files.Downloads[0].LocalDirectory;
        Arrive(0, "readme.txt", "abc");
        _queue.Complete(TransferOf(0));
        Assert.Equal("abc", await ReadAsync(readme));

        drop.Completed(succeeded: true);

        await GoneAsync(folder);
        Assert.Equal(TransferState.Completed, _queue.Items[^1].State);
    }

    [Fact]
    public async Task What_cannot_be_offered_is_reported_and_nothing_is_handed_over()
    {
        // Ein Pfad, den die Liste für den Empfänger nicht fasst.
        _files.File("/srv/deep/" + new string('x', 200) + "/" + new string('y', 80) + ".txt", 1, hoursAgo: 1);
        Assert.Null(await Drop(new DropItem("/srv/deep", "deep", IsDirectory: true)).EntriesAsync());
        Assert.Equal([(DropNotice.TooLong, null)], _notices);

        // Das Backend nennt nicht beliebig viele Einträge.
        _notices.Clear();
        _files.FailNextTree = "too_many";
        Assert.Null(await Drop(Www).EntriesAsync());
        Assert.Equal([(DropNotice.TooMany, null)], _notices);

        // Der Server verweigert einen Unterordner.
        _notices.Clear();
        _files.FailNextTree = "permission_denied";
        var refused = Drop(Readme, Www);
        Assert.Null(await refused.EntriesAsync());
        Assert.Null(await refused.OpenAsync(0));
        Assert.Equal([(DropNotice.ListFailed, "refused")], _notices);

        // Keine Verbindung mehr.
        _notices.Clear();
        var offline = new VirtualDrop(_fetcher, _queue, () => null, [Readme], (notice, detail) => _notices.Add((notice, detail)));
        Assert.Null(await offline.EntriesAsync());
        Assert.Equal([(DropNotice.NotConnected, null)], _notices);
        Assert.Empty(_files.Downloads);
    }

    [Fact]
    public async Task The_shell_offers_the_selection_with_sizes_and_reports_what_goes_wrong()
    {
        var backend = new MockBackend();
        var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), new FakePrompts(), dropDirectory: Path.Combine(_temp.Path, "shell"));
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        await session.Files.LoadAsync();
        var selected = session.Files.Entries.ToList();
        var file = selected.First(entry => !entry.IsDirectory);

        var drop = shell.VirtualFilesForDrop(session, selected)!;
        var entries = (await drop.EntriesAsync())!;

        // Jede ausgewählte Datei steht mit ihrer Größe da, jeder Ordner mit seinem Inhalt.
        Assert.Contains(entries, entry => entry.Path == file.Name && entry.Size == file.Size && !entry.IsDirectory);
        Assert.All(selected.Where(entry => entry.IsDirectory), folder => Assert.Contains(entries, entry => entry.Path == folder.Name && entry.IsDirectory));
        Assert.Contains(entries, entry => entry.Path.Contains('/'));

        // Der Browser wechselt das Verzeichnis – angeboten bleibt, was ausgewählt war.
        await session.Files.NavigateToAsync("/");
        Assert.Same(entries, await drop.EntriesAsync());

        Assert.Null(shell.VirtualFilesForDrop(session, []));
        shell.Dispose();
    }
}
