using System.Text.Json;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Was sich noch einmal versuchen lässt, ist nach einem Neustart der App wieder in der Liste – und „Wiederholen“
/// setzt dann fort wie vorher. Dass der Kern seinerseits behält, wie weit jede Datei kam, prüfen seine eigenen
/// Tests (`backend/nexus-core/tests/sftp.rs`).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferRestartTests : IDisposable
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true,"saved_secret":true}""";

    private readonly TempFolder _folder = new();
    private FakeTransport _transport = null!;
    private CoreBackend _backend = null!;

    public TransferRestartTests()
    {
        L.Init(AppLanguage.English);
        Open();
    }

    private string TransfersFile => Path.Combine(_folder.Path, "data", "transfers.json");

    public void Dispose() => _folder.Dispose();

    /// <summary>Startet die App (wieder) – mit einem frischen Kern über demselben Datenordner.</summary>
    private void Open()
    {
        _transport = new FakeTransport();
        _transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        _backend = new CoreBackend(_transport, TransfersFile);
    }

    /// <summary>Beendet die App, wie sie es beim Schließen tut, und startet sie neu.</summary>
    private void Restart()
    {
        _backend.Transfers.Suspend();
        _backend.Dispose();
        Open();
    }

    private IRemoteFileSystem Connect()
    {
        var connection = _backend.Connect(_backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var session = _transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        _transport.Raise($$"""{"event":"session.state","session":{{session}},"state":"connected","home":"/home/ubuntu"}""");
        return Assert.IsAssignableFrom<IRemoteFileSystem>(connection.Files);
    }

    private void Report(string id, string state, long bytes, string? message = null)
        => _transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"{{state}}","bytes":{{bytes}},"total":1000{{(message is null ? "" : $",\"message\":\"{message}\"")}}}""");

    private JsonElement LastStart() => _transport.Sent("transfer.start").Last();

    private long? Resume() => LastStart().TryGetProperty("resume", out var resume) ? resume.GetInt64() : null;

    /// <summary>Die Transfers, die die App dem Kern beim Start als „noch in der Liste“ genannt hat.</summary>
    private List<string> Retained()
        => [.. _transport.Sent("transfer.retain").Last().GetProperty("jobs").EnumerateArray()
            .Select(job => $"{job.GetProperty("direction").GetString()} {job.GetProperty("local").GetString()} {job.GetProperty("remote").GetString()}"
                + (job.TryGetProperty("name", out var name) ? $" {name.GetString()}" : string.Empty))];

    [Fact]
    public void A_failed_download_is_still_listed_after_a_restart_and_continues()
    {
        var files = Connect();
        var id = files.Download("/srv/backup.tar", _folder.Path);
        Report(id, "failed", 250, "connection: connection lost");

        Restart();

        var row = Assert.Single(_backend.Transfers.Items);
        Assert.Equal(("backup.tar", "web01", TransferDirection.Download, TransferState.Failed), (row.FileName, row.Server, row.Direction, row.State));
        Assert.Equal((250, 1000, 0.25), (row.Bytes, row.Total, row.Progress));
        Assert.Equal("connection: connection lost", row.Detail);

        // Der Kern erfährt gleich beim Start, dass er behalten soll, was er von diesem Transfer noch weiß.
        Assert.Equal([$"download {_folder.Path} /srv/backup.tar"], Retained());

        // Die Sitzung von damals gibt es nicht mehr; der neue Versuch nimmt die neue Verbindung zum selben Server.
        Connect();
        Assert.True(_backend.Transfers.Retry(row.Id));
        Assert.Equal(1UL, LastStart().GetProperty("session").GetUInt64());
        Assert.Equal("download", LastStart().GetProperty("direction").GetString());
        Assert.Equal(_folder.Path, LastStart().GetProperty("local").GetString());
        Assert.Equal("/srv/backup.tar", LastStart().GetProperty("remote").GetString());
        Assert.Equal(250, Resume());
    }

    [Fact]
    public void A_restored_upload_continues_only_while_the_file_is_unchanged()
    {
        var files = Connect();
        var local = _folder.File("backup.tar", "inhalt");
        var same = files.Upload(local, "/srv", "kopie.tar");
        Report(same, "cancelled", 250);
        Restart();

        Assert.Equal([$"upload {local} /srv kopie.tar"], Retained());
        Connect();
        Assert.True(_backend.Transfers.Retry(_backend.Transfers.Items.Single().Id));
        Assert.Equal("kopie.tar", LastStart().GetProperty("name").GetString());
        Assert.Equal(250, Resume());

        // Größe und Zeit vom ersten Versuch überstehen den Neustart auf den Tick genau – und eine Änderung danach
        // fällt auf.
        Report(_backend.Transfers.Items.Single().Id, "failed", 400);
        Restart();
        File.SetLastWriteTimeUtc(local, File.GetLastWriteTimeUtc(local).AddTicks(1));
        Connect();
        Assert.True(_backend.Transfers.Retry(_backend.Transfers.Items.Single().Id));
        Assert.Null(Resume());
    }

    [Fact]
    public void What_was_running_when_the_app_closed_is_listed_as_interrupted()
    {
        var files = Connect();
        var running = files.Download("/srv/backup.tar", _folder.Path);
        var waiting = files.Upload(_folder.File("notizen.txt", "inhalt"), "/srv");
        Report(running, "running", 100);

        _backend.Transfers.Suspend();
        Assert.Single(_transport.Sent("transfer.suspend"));
        Assert.All(_backend.Transfers.Items, item => Assert.Equal((TransferState.Failed, SavedTransfer.InterruptedDetail), (item.State, item.Detail)));

        // Was der Kern danach noch meldet, ändert daran nichts – es nennt nur, wie weit der Transfer kam.
        Report(running, "running", 300);
        Report(running, "failed", 400, "connection: core ended");
        Report(waiting, "failed", 0, "connection: core ended");
        Assert.All(_backend.Transfers.Items, item => Assert.Equal((TransferState.Failed, SavedTransfer.InterruptedDetail), (item.State, item.Detail)));

        _backend.Dispose();
        Open();
        Assert.Equal([400, 0], _backend.Transfers.Items.Select(item => item.Bytes));
        using var center = new TransferCenterViewModel(_backend.Transfers, new ImmediateDispatcher());
        Assert.All(center.Items, row => Assert.Equal(L.Get("TransferInterrupted"), row.DetailText));
        Assert.All(center.Items, row => Assert.True(row.CanRetry));
    }

    [Fact]
    public void A_transfer_that_completes_while_the_app_closes_is_not_listed_again()
    {
        var files = Connect();
        var id = files.Download("/srv/backup.tar", _folder.Path);
        Report(id, "running", 900);

        _backend.Transfers.Suspend();
        Report(id, "completed", 1000);
        Assert.Equal(TransferState.Completed, _backend.Transfers.Items.Single().State);

        _backend.Dispose();
        Open();
        Assert.Empty(_backend.Transfers.Items);
    }

    [Fact]
    public void A_transfer_that_was_still_running_when_the_app_crashed_can_be_retried()
    {
        var files = Connect();
        var id = files.Download("/srv/backup.tar", _folder.Path);
        Report(id, "running", 600);

        // Kein Suspend: Die App ist einfach weg.
        Open();

        var row = Assert.Single(_backend.Transfers.Items);
        Assert.Equal((TransferState.Failed, SavedTransfer.InterruptedDetail), (row.State, row.Detail));
        Connect();
        Assert.True(_backend.Transfers.Retry(row.Id));

        // Gespeichert hat die App den Stand, als er zu laufen begann; wie weit er danach kam, weiß der Kern.
        Assert.Equal(600, Resume());
    }

    [Fact]
    public void After_a_crash_a_retry_tells_the_core_to_build_on_what_it_wrote_down()
    {
        var files = Connect();
        files.Upload(_folder.Path, "/srv");
        files.Upload(_folder.File("notizen.txt", "inhalt"), "/srv");

        // Abgestürzt: kein Suspend, kein Ende.
        Open();
        Connect();
        var (afterFolder, afterSingle) = (_backend.Transfers.Items[0], _backend.Transfers.Items[1]);

        Assert.True(_backend.Transfers.Retry(afterFolder.Id));
        Assert.Equal(_folder.Path, LastStart().GetProperty("local").GetString());
        Assert.Equal(1, Resume());

        // Beim Upload einer Datei nimmt der Kern die Stelle aus seiner Spur; die 1 heißt nur „es gibt eine“.
        Assert.True(_backend.Transfers.Retry(afterSingle.Id));
        Assert.EndsWith("notizen.txt", LastStart().GetProperty("local").GetString());
        Assert.Equal(1, Resume());
    }

    [Fact]
    public void After_a_crash_a_changed_file_is_still_uploaded_from_the_beginning()
    {
        var files = Connect();
        var local = _folder.File("notizen.txt", "inhalt");
        files.Upload(local, "/srv");
        Open();
        Connect();

        File.WriteAllText(local, "anderer inhalt");
        Assert.True(_backend.Transfers.Retry(_backend.Transfers.Items.Single().Id));

        Assert.Null(Resume());
    }

    [Fact]
    public void Completed_and_cleared_transfers_are_gone_after_a_restart_and_the_core_forgets_them()
    {
        var files = Connect();
        var done = files.Download("/srv/fertig.tar", _folder.Path);
        var failed = files.Download("/srv/backup.tar", _folder.Path);
        Report(done, "completed", 1000);
        Report(failed, "failed", 250);
        Restart();
        Assert.Equal("backup.tar", _backend.Transfers.Items.Single().FileName);

        // Nimmt der Benutzer die Zeile weg, setzt niemand mehr fort: Der Kern darf die Teil-Datei wegräumen.
        _backend.Transfers.ClearFinished();
        Assert.Empty(Retained());
        Restart();
        Assert.Empty(_backend.Transfers.Items);
        Assert.Empty(Retained());
    }

    [Fact]
    public void Downloads_into_the_work_folder_for_editing_are_not_kept()
    {
        var files = Connect();
        var id = files.Download("/srv/nginx.conf", Path.Combine(AppDataLocation.EditRoot, "1234", "abc"));
        Report(id, "failed", 10);
        var kept = files.Download("/srv/backup.tar", _folder.Path);
        Report(kept, "failed", 10);

        Restart();

        Assert.Equal("backup.tar", _backend.Transfers.Items.Single().FileName);
    }

    [Fact]
    public void An_unreadable_or_edited_file_of_transfers_means_there_are_none()
    {
        var valid = $$"""{"Direction":"Download","Name":"a.tar","Server":"web01","Local":{{JsonSerializer.Serialize(_folder.Path)}},"Remote":"/srv/a.tar","State":"Failed","Bytes":5,"Total":10}""";
        foreach (var (content, expected) in new[]
        {
            ("{ kein JSON", 0),
            ($$"""{"Version":99,"Transfers":[{{valid}}]}""", 0),
            ($$"""{"Version":1,"Transfers":null}""", 0),
            ($$"""{"Version":1,"Transfers":[null,{{valid}}]}""", 1),
            ($$"""{"Version":1,"Transfers":[{{valid.Replace("\"Failed\"", "\"Running\"")}},{{valid.Replace("\"Remote\":\"/srv/a.tar\"", "\"Remote\":\"\"")}},{{valid.Replace("\"Bytes\":5", "\"Bytes\":-5")}},{{valid.Replace("\"Direction\":\"Download\"", "\"Direction\":7")}},{{valid}}]}""", 1),
        })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TransfersFile)!);
            File.WriteAllText(TransfersFile, content);
            _backend.Dispose();
            Open();

            Assert.Equal(expected, _backend.Transfers.Items.Count);

            // Auch dann erfährt der Kern, was die App zeigt – und räumt weg, was nur noch er kennt.
            Assert.Equal(expected, Retained().Count);
        }
    }

    [Fact]
    public void Without_a_file_the_list_lives_only_while_the_app_runs()
    {
        var transport = new FakeTransport();
        using var backend = new CoreBackend(transport);

        backend.Transfers.ClearFinished();

        Assert.Empty(transport.Sent("transfer.retain"));
    }
}
