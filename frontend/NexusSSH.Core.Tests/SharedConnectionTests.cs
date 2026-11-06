using System.Text;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Was mit ABI 5 zum Protokoll des Kerns dazukam, von der C#-Seite: mitbenutzte Verbindung, Banner,
/// Flusskontrolle, gespeicherte Geheimnisse, Rechte und Besitzer, Last und Netz, Transfer wiederholen.
/// Die JSON-Texte sind dieselben wie in <c>backend/nexus-core/src/protocol.rs</c>.
/// </summary>
public sealed class SharedConnectionTests
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true,"saved_secret":true}""";

    private const string App =
        """{"id":"p2","name":"app","host":"10.0.2.20","port":22,"user":"deploy","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"p1","saved_secret":false}""";

    private static (CoreBackend Backend, FakeTransport Transport) Create()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}},{{App}}]}""";
        return (new CoreBackend(transport), transport);
    }

    private static IServerConnection Connected(CoreBackend backend, FakeTransport transport, IServerConnection? share = null)
    {
        var connection = backend.Connect(backend.GetProfiles()[0], share);
        connection.Terminal.Start();
        var session = transport.Sent("session.open").Last().GetProperty("session").GetUInt64();
        transport.Raise($$"""{"event":"session.state","session":{{session}},"state":"connected","home":"/home/ubuntu"}""");
        return connection;
    }

    [Fact]
    public void A_second_session_names_the_standing_one_to_share_its_connection()
    {
        var (backend, transport) = Create();
        var first = Connected(backend, transport);
        Assert.False(transport.Sent("session.open").Single().TryGetProperty("share", out _), "die erste Sitzung meldet sich selbst an");

        var second = backend.Connect(backend.GetProfiles()[0], first);
        second.Terminal.Start();

        var open = transport.Sent("session.open").Last();
        Assert.Equal(2UL, open.GetProperty("session").GetUInt64());
        Assert.Equal(1UL, open.GetProperty("share").GetUInt64());
    }

    [Fact]
    public void A_connection_that_has_ended_by_the_time_of_the_start_is_not_named()
    {
        var (backend, transport) = Create();
        var first = Connected(backend, transport);
        var second = backend.Connect(backend.GetProfiles()[0], first);

        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"remote"}""");
        second.Terminal.Start();

        Assert.False(transport.Sent("session.open").Last().TryGetProperty("share", out _));
    }

    [Fact]
    public void The_banner_reaches_only_its_own_connection()
    {
        var (backend, transport) = Create();
        var first = Connected(backend, transport);
        var second = Connected(backend, transport);
        var banners = new List<string>();
        first.Banner += text => banners.Add($"1:{text}");
        second.Banner += text => banners.Add($"2:{text}");

        transport.Raise("""{"event":"session.banner","session":2,"text":"Authorized use only\n"}""");
        transport.Raise("""{"event":"session.banner","session":9,"text":"nobody"}""");

        Assert.Equal(["2:Authorized use only\n"], banners);
    }

    [Fact]
    public void Pausing_and_resuming_tell_the_core_which_session()
    {
        var (backend, transport) = Create();
        var connection = Connected(backend, transport);

        connection.Terminal.SetPaused(true);
        connection.Terminal.SetPaused(false);

        Assert.Equal(
            [(1UL, true), (1UL, false)],
            transport.Sent("session.flow").Select(request => (request.GetProperty("session").GetUInt64(), request.GetProperty("paused").GetBoolean())));
    }

    [Fact]
    public void Profiles_tell_whether_a_secret_is_saved_and_which_jump_server_they_use()
    {
        var (backend, _) = Create();

        var profiles = backend.GetProfiles();

        Assert.True(profiles[0].HasSavedSecret);
        Assert.Null(profiles[0].JumpId);
        Assert.False(profiles[1].HasSavedSecret);
        Assert.Equal("p1", profiles[1].JumpId);
    }

    [Fact]
    public void Saving_a_profile_does_not_send_what_only_the_core_knows()
    {
        var (backend, transport) = Create();
        transport.Responses["profiles.save"] = $$"""{"ok":true,"profile":{{App}}}""";

        backend.Save(backend.GetProfiles()[1]);

        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.False(sent.TryGetProperty("saved_secret", out _));
        Assert.False(sent.TryGetProperty("jump_name", out _));
        Assert.Equal("p1", sent.GetProperty("jump").GetString());
    }

    [Fact]
    public void Forgetting_secrets_names_the_profile_and_reloads_the_list()
    {
        var (backend, transport) = Create();
        var changes = 0;
        backend.Changed += () => changes++;
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01.Replace("\"saved_secret\":true", "\"saved_secret\":false")}}]}""";

        backend.ForgetSecrets("p1");

        Assert.Equal("p1", Assert.Single(transport.Sent("secrets.forget")).GetProperty("id").GetString());
        Assert.False(Assert.Single(backend.GetProfiles()).HasSavedSecret);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Entries_carry_permissions_owner_and_group_when_the_server_names_them()
    {
        var (backend, transport) = Create();
        var files = Connected(backend, transport).Files!;

        var listing = files.ListAsync("/etc", TestContext.Current.CancellationToken);
        var number = Assert.Single(transport.Sent("sftp.list")).GetProperty("request").GetUInt64();
        transport.Raise(
            $$"""{"event":"sftp.result","request":{{number}},"ok":true,"entries":[{"name":"shadow","kind":"file","link":false,"size":1200,"modified":1790000000,"permissions":416,"owner":"root","group":"shadow"},{"name":".cache","kind":"directory","link":false,"size":0,"permissions":493,"owner":"1000","group":"1000"},{"name":"plain","kind":"file","link":false,"size":1}]}""");
        var entries = await listing;

        Assert.Equal((416, "root", "shadow"), (entries[0].Permissions, entries[0].Owner, entries[0].Group));
        Assert.Equal("-rw-r----- (640)", Format.Permissions(entries[0].Permissions, entries[0].IsDirectory, entries[0].IsLink));
        Assert.Equal("1000", entries[1].Owner);
        Assert.True(entries[1].IsHidden);
        Assert.Equal((null, null, null), (entries[2].Permissions, entries[2].Owner, entries[2].Group));
        Assert.False(entries[2].IsHidden);
    }

    [Fact]
    public void A_measurement_carries_load_and_network_rates()
    {
        var (backend, transport) = Create();
        var monitor = Connected(backend, transport).Monitor!;

        transport.Raise(
            """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":1,"memory_total":2,"disk_used":1,"disk_total":2,"os":"Debian","kernel":"Linux 6","uptime":60,"load":"0.42 0.31 0.25","net_rx":1048576,"net_tx":2048}""");
        Assert.Equal(("0.42 0.31 0.25", 1048576L, 2048L), (monitor.Current!.Load, monitor.Current.NetReceived, monitor.Current.NetSent));

        // Ein älterer oder fremder Server nennt beides nicht.
        transport.Raise(
            """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":1,"memory_total":2,"disk_used":1,"disk_total":2,"os":"Debian","kernel":"Linux 6","uptime":60}""");
        Assert.Equal((string.Empty, 0L, 0L), (monitor.Current.Load, monitor.Current.NetReceived, monitor.Current.NetSent));
        Assert.Null(monitor.Current.Io);

        // Der Durchsatz der Datenträger: was der Kern nennt, in Grenzen – und nur, wenn er ihn nennt.
        transport.Raise(
            """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":1,"memory_total":2,"disk_used":1,"disk_total":2,"os":"Debian","kernel":"Linux 6","uptime":60,"io_read":2097152,"io_write":524288,"io_busy":250}""");
        Assert.Equal(new DiskIo(2097152, 524288, 100), monitor.Current.Io);
        Assert.Empty(monitor.Current.Interfaces!);
        Assert.Empty(monitor.Current.IoDevices!);

        // Je Platte: nur mit einem Namen, in Grenzen; nennt der Server Lesen und Schreiben nur zusammen, gilt das für jede.
        transport.Raise(
            """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":1,"memory_total":2,"disk_used":1,"disk_total":2,"os":"OpenBSD","kernel":"OpenBSD 7.9","uptime":60,"io_read":1048576,"io_write":0,"io_busy":25,"io_mixed":true,"io_disks":[{"name":"sd0","read":1048576,"write":0,"busy":25},null,{"name":"","read":1,"write":1,"busy":1},{"name":"wd0","read":-4,"write":0,"busy":900}]}""");
        Assert.Equal(
            [new DiskIoDevice("sd0", new DiskIo(1048576, 0, 25, Mixed: true)), new DiskIoDevice("wd0", new DiskIo(0, 0, 100, Mixed: true))],
            monitor.Current.IoDevices);
        Assert.Equal(NexusSSH.Core.Localization.L.Get("DiskIoMixedText", Format.Rate(1048576), Format.Percent(25)), monitor.Current.Io!.Text);

        // Das Netz je Karte: Als Adresse gilt nur, was eine ist; ohne Namen gibt es keine Karte.
        transport.Raise(
            """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":1,"memory_total":2,"disk_used":1,"disk_total":2,"os":"Debian","kernel":"Linux 6","uptime":60,"interfaces":[{"name":"eth0","rx":150000,"tx":-5,"addresses":["192.0.2.10","<b>evil</b>","2001:db8::10",null]},{"name":"","rx":1,"tx":1},null,{"name":"wg0","rx":0,"tx":0}]}""");
        Assert.Equal(
            [new NetInterface("eth0", 150000, 0, "192.0.2.10, 2001:db8::10"), new NetInterface("wg0", 0, 0, string.Empty)],
            monitor.Current.Interfaces);
        Assert.Equal("wg0, ↓ 0 B/s  ↑ 0 B/s", monitor.Current.Interfaces![1].ToString());
    }

    [Fact]
    public void A_failed_transfer_is_started_again_with_the_same_paths_and_replaces_its_row()
    {
        var (backend, transport) = Create();
        var files = Connected(backend, transport).Files!;
        var id = files.Upload(LocalPath.Of("data", "backup.tar.gz"), "/var/backups");
        Assert.False(backend.Transfers.Retry(id), "ein wartender Transfer wird nicht noch einmal gestartet");

        transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"failed","bytes":0,"total":10,"message":"connection: lost"}""");
        Assert.True(backend.Transfers.Retry(id));

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(
            ("upload", LocalPath.Of("data", "backup.tar.gz"), "/var/backups", 1UL),
            (sent[1].GetProperty("direction").GetString(), sent[1].GetProperty("local").GetString(), sent[1].GetProperty("remote").GetString(), sent[1].GetProperty("session").GetUInt64()));
        Assert.NotEqual(sent[0].GetProperty("transfer").GetUInt64(), sent[1].GetProperty("transfer").GetUInt64());

        var item = Assert.Single(backend.Transfers.Items);
        Assert.Equal((TransferState.Queued, "backup.tar.gz", "web01"), (item.State, item.FileName, item.Server));
        Assert.NotEqual(id, item.Id);
        Assert.False(backend.Transfers.Retry(id), "den alten gibt es nicht mehr");
        Assert.False(backend.Transfers.Retry("nonsense"));
    }

    [Fact]
    public void A_retry_after_the_connection_was_lost_uses_one_that_stands()
    {
        var (backend, transport) = Create();
        var lost = Connected(backend, transport);
        var id = lost.Files!.Download("/var/log/app.log", LocalPath.Folder("downloads"));

        // Die Verbindung reißt ab: Transfer gescheitert, Sitzung zu Ende.
        transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"failed","bytes":10,"total":100,"message":"connection: sender dropped"}""");
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"connection_lost"}""");

        // Solange niemand zum Server verbunden ist, sagt die Zeile das – der Kern wird gar nicht erst gefragt.
        Assert.True(backend.Transfers.Retry(id));
        var waiting = Assert.Single(backend.Transfers.Items);
        Assert.Equal((TransferState.Failed, "not_connected: "), (waiting.State, waiting.Detail));
        Assert.Single(transport.Sent("transfer.start"));

        // „Erneut verbinden“ ergibt eine neue Sitzung – auch in einem anderen Tab. Der nächste Versuch läuft darüber.
        var other = backend.Connect(backend.GetProfiles()[1]);
        other.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":2,"state":"connected","home":"/"}""");
        Connected(backend, transport);

        Assert.True(backend.Transfers.Retry(waiting.Id));

        var start = transport.Sent("transfer.start").Last();
        Assert.Equal(3UL, start.GetProperty("session").GetUInt64());
        Assert.Equal("/var/log/app.log", start.GetProperty("remote").GetString());
        Assert.Equal(TransferState.Queued, Assert.Single(backend.Transfers.Items).State);
    }

    [Fact]
    public void A_cancelled_transfer_can_be_retried_and_a_cleared_one_is_forgotten()
    {
        var (backend, transport) = Create();
        var files = Connected(backend, transport).Files!;
        var first = files.Download("/var/log/app.log", @"D:\downloads");
        var second = files.Download("/var/log/b.log", @"D:\downloads");
        transport.Raise($$"""{"event":"transfer.state","transfer":{{first}},"state":"cancelled","bytes":0,"total":0}""");
        transport.Raise($$"""{"event":"transfer.state","transfer":{{second}},"state":"cancelled","bytes":0,"total":0}""");

        Assert.True(backend.Transfers.Retry(first));
        backend.Transfers.ClearFinished();

        Assert.False(backend.Transfers.Retry(second));
        Assert.Equal("app.log", Assert.Single(backend.Transfers.Items).FileName);
        Assert.Equal(3, transport.Sent("transfer.start").Count());
    }

    [Fact]
    public void Output_keeps_flowing_to_the_right_session_while_another_is_paused()
    {
        var (backend, transport) = Create();
        var first = Connected(backend, transport);
        var second = Connected(backend, transport);
        var seen = new StringBuilder();
        second.Terminal.Output += data => seen.Append(Encoding.UTF8.GetString(data.Span));

        first.Terminal.SetPaused(true);
        transport.RaiseOutput(2, "still here");

        Assert.Equal("still here", seen.ToString());
    }
}
