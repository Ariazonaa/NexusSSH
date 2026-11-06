using System.Text.Json;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Die Warteschlange ordnen: wartende Transfers umstellen, einzelne anhalten.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class QueueOrderTests : IDisposable
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true,"saved_secret":true}""";

    private readonly TempFolder _folder = new();

    public QueueOrderTests() => L.Init(AppLanguage.English);

    public void Dispose() => _folder.Dispose();

    // Eine leere Liste der Beispieldaten mit einem laufenden und drei wartenden Transfers.
    private static (MockTransferQueue Queue, TransferCenterViewModel Center, List<string> Ids) Waiting()
    {
        var queue = new MockTransferQueue();
        foreach (var item in queue.Items.Where(item => !item.IsFinished).ToList())
        {
            queue.Cancel(item.Id);
        }

        queue.ClearFinished();
        var ids = new[] { "run.bin", "a.bin", "b.bin", "c.bin" }.Select(name => queue.Add(name, "web01", TransferDirection.Upload, 1000)).ToList();
        queue.Advance();
        return (queue, new TransferCenterViewModel(queue, new ImmediateDispatcher()), ids);
    }

    private static List<string> Names(TransferCenterViewModel center) => [.. center.Items.Select(item => item.FileName)];

    [Fact]
    public void A_waiting_transfer_moves_earlier_later_or_to_the_front_and_the_rows_follow()
    {
        var (queue, center, _) = Waiting();
        using (queue)
        using (center)
        {
            TransferItemViewModel Row(string name) => center.Items.Single(item => item.FileName == name);
            Assert.Equal(["run.bin", "a.bin", "b.bin", "c.bin"], Names(center));
            Assert.Equal([false, true, true, true], center.Items.Select(item => item.IsWaiting));
            Assert.Equal((false, true), (Row("a.bin").CanMoveUp, Row("a.bin").CanMoveDown));
            Assert.Equal((true, false), (Row("c.bin").CanMoveUp, Row("c.bin").CanMoveDown));

            Assert.True(center.Move(Row("c.bin"), QueueMove.First));
            Assert.Equal(["run.bin", "c.bin", "a.bin", "b.bin"], Names(center));
            Assert.True(center.Move(Row("c.bin"), QueueMove.Down));
            Assert.Equal(["run.bin", "a.bin", "c.bin", "b.bin"], Names(center));
            Assert.True(center.Move(Row("b.bin"), QueueMove.Up));
            Assert.Equal(["run.bin", "a.bin", "b.bin", "c.bin"], Names(center));
            Assert.Equal((true, false), (Row("c.bin").CanMoveUp, Row("c.bin").CanMoveDown));

            // Am Rand geht es nicht weiter, und wer läuft, wartet nicht.
            Assert.False(center.Move(Row("a.bin"), QueueMove.Up));
            Assert.False(center.Move(Row("a.bin"), QueueMove.First));
            Assert.False(center.Move(Row("c.bin"), QueueMove.Down));
            Assert.False(center.Move(Row("run.bin"), QueueMove.Down));
            Assert.Equal(["run.bin", "a.bin", "b.bin", "c.bin"], Names(center));
        }
    }

    [Fact]
    public void One_transfer_is_paused_alone_and_continues_with_retry()
    {
        var (queue, center, _) = Waiting();
        using (queue)
        using (center)
        {
            var failed = new List<string>();
            center.TransferFailed += (file, _, _) => failed.Add(file);
            var row = center.Items.Single(item => item.FileName == "b.bin");

            center.Pause(row);

            Assert.True(row.IsPaused);
            Assert.Equal("Paused", row.StatusText);
            Assert.Equal([false, false, true, false], center.Items.Select(item => item.IsPaused));
            Assert.Equal([true, true, false, true], center.Items.Select(item => item.CanCancel));

            // Angehalten ist nicht gescheitert, und die anderen beiden Wartenden stehen jetzt nebeneinander.
            Assert.Empty(failed);
            Assert.Equal((false, true), (center.Items[1].CanMoveUp, center.Items[1].CanMoveDown));
            Assert.Equal((true, false), (center.Items[3].CanMoveUp, center.Items[3].CanMoveDown));

            Assert.True(center.Retry(row));
            Assert.False(row.IsPaused);
            Assert.True(row.IsWaiting);
        }
    }

    [Fact]
    public void Moving_asks_the_core_first_and_the_list_follows_only_when_it_agrees()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        using var backend = new CoreBackend(transport, Path.Combine(_folder.Path, "data", "transfers.json"));
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var session = transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        transport.Raise($$"""{"event":"session.state","session":{{session}},"state":"connected","home":"/home/ubuntu"}""");
        var files = Assert.IsAssignableFrom<IRemoteFileSystem>(connection.Files);
        var ids = new[] { "/srv/a", "/srv/b", "/srv/c" }.Select(path => files.Download(path, _folder.Path)).ToList();
        List<string> Order() => [.. backend.Transfers.Items.Select(item => item.FileName)];
        (ulong Transfer, ulong Before) LastMove()
            => (transport.Sent("transfer.move").Last().GetProperty("transfer").GetUInt64(), transport.Sent("transfer.move").Last().GetProperty("before").GetUInt64());

        // „Als Nächstes“: der dritte vor den ersten.
        Assert.True(backend.Transfers.Move(ids[2], QueueMove.First));
        Assert.Equal((ulong.Parse(ids[2]), ulong.Parse(ids[0])), LastMove());
        Assert.Equal(["c", "a", "b"], Order());

        // „Später“ heißt für den Kern: Der Nächste rückt vor diesen.
        Assert.True(backend.Transfers.Move(ids[2], QueueMove.Down));
        Assert.Equal((ulong.Parse(ids[0]), ulong.Parse(ids[2])), LastMove());
        Assert.Equal(["a", "c", "b"], Order());

        // Lehnt der Kern ab (einer der beiden läuft inzwischen), bleibt die Liste, wie sie ist.
        transport.Responses["transfer.move"] = """{"ok":false,"error":"not_waiting","message":"3"}""";
        Assert.False(backend.Transfers.Move(ids[1], QueueMove.Up));
        Assert.Equal(["a", "c", "b"], Order());

        // Am Rand wird gar nicht erst gefragt.
        var asked = transport.Sent("transfer.move").Count();
        Assert.False(backend.Transfers.Move(ids[0], QueueMove.Up));
        Assert.False(backend.Transfers.Move(ids[1], QueueMove.Down));
        Assert.Equal(asked, transport.Sent("transfer.move").Count());
    }

    [Fact]
    public void Pausing_one_transfer_asks_the_core_for_this_one_and_its_end_reads_as_paused()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        using var backend = new CoreBackend(transport, Path.Combine(_folder.Path, "data", "transfers.json"));
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        var session = transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        transport.Raise($$"""{"event":"session.state","session":{{session}},"state":"connected","home":"/home/ubuntu"}""");
        var files = Assert.IsAssignableFrom<IRemoteFileSystem>(connection.Files);
        var first = files.Download("/srv/a", _folder.Path);
        var second = files.Download("/srv/b", _folder.Path);
        void Report(string id, string state, string? message = null)
            => transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"{{state}}","bytes":100,"total":1000{{(message is null ? "" : $",\"message\":\"{message}\"")}}}""");
        Report(first, "running");
        Report(second, "running");

        backend.Transfers.Pause(first);

        Assert.Equal(ulong.Parse(first), Assert.Single(transport.Sent("transfer.pause")).GetProperty("transfer").GetUInt64());
        Assert.Empty(transport.Sent("transfer.suspend"));

        // Der Kern meldet das Ende wie einen Abriss – in der Liste steht „angehalten“. Scheitert der andere wirklich,
        // bleibt das ein Fehler.
        Report(first, "failed", "connection: connection lost");
        Report(second, "failed", "connection: connection lost");
        var items = backend.Transfers.Items;
        Assert.Equal(ITransferQueue.PausedDetail, items[0].Detail);
        Assert.Equal("connection: connection lost", items[1].Detail);

        // Was schon zu Ende ist, wird nicht mehr angehalten.
        backend.Transfers.Pause(second);
        Assert.Single(transport.Sent("transfer.pause"));
    }
}
