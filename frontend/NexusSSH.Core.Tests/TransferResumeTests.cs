using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Ein Transfer, der noch einmal versucht wird, setzt fort, statt von vorn zu beginnen: Die App nennt dem Kern, wie
/// viel der abgebrochene Versuch übertragen hat – beim Upload einer einzelnen Datei aber nur, wenn die Datei hier
/// noch dieselbe ist. Was der Kern damit macht, prüfen seine eigenen Tests (`backend/nexus-core/tests/sftp.rs`).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferResumeTests : IDisposable
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true,"saved_secret":true}""";

    private readonly TempFolder _folder = new();
    private readonly FakeTransport _transport = new();
    private readonly CoreBackend _backend;

    public TransferResumeTests()
    {
        L.Init(AppLanguage.English);
        _transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        _backend = new CoreBackend(_transport);
    }

    public void Dispose() => _folder.Dispose();

    private IRemoteFileSystem Connect()
    {
        var connection = _backend.Connect(_backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var session = _transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        _transport.Raise($$"""{"event":"session.state","session":{{session}},"state":"connected","home":"/home/ubuntu"}""");
        return Assert.IsAssignableFrom<IRemoteFileSystem>(connection.Files);
    }

    private void Stop(string id, string state, long bytes)
        => _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"{{state}}","bytes":{{bytes}},"total":1000}""");

    private long? Resume()
        => _transport.Sent("transfer.start").Last().TryGetProperty("resume", out var resume) ? resume.GetInt64() : null;

    private string Retried(string id)
    {
        Assert.True(_backend.Transfers.Retry(id));
        return _backend.Transfers.Items.Single().Id;
    }

    [Fact]
    public void A_retried_upload_tells_the_core_how_much_the_stopped_attempt_had_sent()
    {
        var files = Connect();
        var id = files.Upload(_folder.File("backup.tar", "inhalt"), "/srv");
        Assert.Null(Resume());

        Stop(id, "failed", 250);
        var second = Retried(id);
        Assert.Equal(250, Resume());

        // Auch nach einem Abbruch von Hand, und immer mit dem Stand des letzten Versuchs.
        Stop(second, "cancelled", 700);
        Retried(second);
        Assert.Equal(700, Resume());
    }

    [Fact]
    public void A_file_that_changed_since_is_uploaded_from_the_beginning()
    {
        var files = Connect();
        var local = _folder.File("backup.tar", "inhalt");
        var id = files.Upload(local, "/srv");
        Stop(id, "failed", 250);

        // Gleiche Größe, andere Zeit: Fortsetzen ergäbe den Anfang der alten vor dem Rest der neuen Fassung.
        File.SetLastWriteTimeUtc(local, File.GetLastWriteTimeUtc(local).AddSeconds(5));
        var second = Retried(id);
        Assert.Null(Resume());

        // Vom neuen Stand an gilt wieder: Bleibt die Datei, wie sie ist, geht es beim nächsten Mal weiter.
        Stop(second, "failed", 100);
        var third = Retried(second);
        Assert.Equal(100, Resume());

        File.WriteAllText(local, "inhalt und mehr");
        Stop(third, "failed", 400);
        var fourth = Retried(third);
        Assert.Null(Resume());

        File.Delete(local);
        Stop(fourth, "failed", 3);
        Retried(fourth);
        Assert.Null(Resume());
    }

    [Fact]
    public void A_retried_folder_or_download_tells_the_core_that_it_is_a_new_attempt()
    {
        var files = Connect();
        _folder.File("backup.tar", "inhalt");

        // Wie weit die einzelnen Dateien kamen und ob sie noch dieselben sind, weiß bei einem Ordner nur der Kern.
        // Die Zahl sagt ihm, dass er darauf aufbauen darf.
        var folder = files.Upload(_folder.Path, "/srv");
        Stop(folder, "cancelled", 250);
        Assert.True(_backend.Transfers.Retry(folder));
        Assert.Equal(250, Resume());

        // Beim Download liegt die Quelle auf dem Server: Ob sie noch dieselbe ist, prüft ebenfalls der Kern.
        var download = files.Download("/srv/backup.tar", _folder.Path);
        Stop(download, "failed", 300);
        Assert.True(_backend.Transfers.Retry(download));
        Assert.Equal(300, Resume());

        var tree = files.Download("/srv/www", _folder.Path);
        Stop(tree, "failed", 400);
        Assert.True(_backend.Transfers.Retry(tree));
        Assert.Equal(400, Resume());
    }

    [Fact]
    public void An_attempt_that_transferred_nothing_starts_from_the_beginning()
    {
        var files = Connect();
        var local = _folder.File("backup.tar", "inhalt");

        var upload = files.Upload(local, "/srv");
        Stop(upload, "failed", 0);
        Assert.True(_backend.Transfers.Retry(upload));
        Assert.Null(Resume());

        var folder = files.Upload(_folder.Path, "/srv");
        Stop(folder, "failed", 0);
        Assert.True(_backend.Transfers.Retry(folder));
        Assert.Null(Resume());

        var download = files.Download("/srv/backup.tar", _folder.Path);
        Stop(download, "failed", 0);
        Assert.True(_backend.Transfers.Retry(download));
        Assert.Null(Resume());
    }

    [Fact]
    public void An_attempt_that_found_no_connection_does_not_forget_how_far_a_download_was()
    {
        var files = Connect();
        var id = files.Download("/srv/backup.tar", _folder.Path);
        Stop(id, "failed", 250);
        _transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connection_lost"}""");

        var waiting = Retried(id);
        Assert.Equal("not_connected: ", _backend.Transfers.Items.Single().Detail);

        Connect();
        Retried(waiting);

        Assert.Equal(250, Resume());
    }

    [Fact]
    public void An_attempt_that_found_no_connection_does_not_forget_how_far_the_upload_was()
    {
        var files = Connect();
        var id = files.Upload(_folder.File("backup.tar", "inhalt"), "/srv");
        Stop(id, "failed", 250);
        _transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connection_lost"}""");

        // Niemand ist verbunden: Der Versuch scheitert, ohne den Kern zu fragen – und ohne ein Byte.
        var waiting = Retried(id);
        Assert.Equal("not_connected: ", _backend.Transfers.Items.Single().Detail);
        Assert.Single(_transport.Sent("transfer.start"));

        Connect();
        Retried(waiting);

        Assert.Equal(2, _transport.Sent("transfer.start").Count());
        Assert.Equal(250, Resume());
    }

    [Fact]
    public void The_row_says_where_a_resumed_upload_went_on()
    {
        var files = Connect();
        var id = files.Upload(_folder.File("backup.tar", "inhalt"), "/srv");
        using var center = new TransferCenterViewModel(_backend.Transfers, new ImmediateDispatcher());
        var row = Assert.Single(center.Items);

        _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"running","bytes":1048576,"total":4194304}""");
        Assert.Equal("1.0 MB / 4.0 MB", row.DetailText);

        // Bei einem Ordner wächst die Zahl, während der neue Versuch findet, was schon vollständig drüben ist –
        // deshalb nennt die Zeile eine Menge und keine Stelle.
        _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"running","bytes":3145728,"total":4194304,"resumed":2097152}""");
        Assert.Equal(2097152, _backend.Transfers.Items.Single().ResumedAt);
        Assert.Equal("3.0 MB / 4.0 MB – 2.0 MB from the previous attempt", row.DetailText);
        Assert.Equal(75, row.ProgressPercent);

        _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"completed","bytes":4194304,"total":4194304,"resumed":2097152}""");
        Assert.Equal("4.0 MB", row.DetailText);
    }

    [Fact]
    public void A_folder_of_empty_files_counts_its_progress_in_files()
    {
        var files = Connect();
        var id = files.Download("/srv/leer", _folder.Path);
        using var center = new TransferCenterViewModel(_backend.Transfers, new ImmediateDispatcher());
        var row = Assert.Single(center.Items);

        _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"running","bytes":0,"total":0,"files_done":12,"files":30}""");
        Assert.Equal(40, row.ProgressPercent);
        Assert.Equal("12 of 30 files", row.DetailText);

        _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"completed","bytes":0,"total":0,"files_done":30,"files":30}""");
        Assert.Equal(100, row.ProgressPercent);
        Assert.Equal("30 files", row.DetailText);

        // Gibt es Bytes, zählen die – auch wenn die meisten Dateien leer sind.
        var mixed = files.Download("/srv/gemischt", _folder.Path);
        _transport.Raise($$"""{"event":"transfer.state","transfer":{{mixed}},"state":"running","bytes":10,"total":1000,"files_done":29,"files":30}""");
        Assert.Equal(1, center.Items.Single(item => item.Id == mixed).ProgressPercent);
    }
}
