using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>Von Server zu Server: holen, weiterschicken, den Zwischenordner wegräumen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerRelayTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly MockTransferQueue _queue = new();
    private readonly MockRemoteFileSystem _source;
    private readonly MockRemoteFileSystem _target;
    private readonly DropFetcher _folders;
    private readonly ServerRelay _relay;
    private readonly List<string> _failures = [];

    public ServerRelayTests()
    {
        L.Init(AppLanguage.English);
        _source = new MockRemoteFileSystem("/var/www", _queue, "web01").File("/var/www/site.tar.gz", 700, hoursAgo: 1);
        _target = new MockRemoteFileSystem("/srv/backup", _queue, "backup01");
        _folders = new DropFetcher(Path.Combine(_temp.Path, "relay"));
        _relay = new ServerRelay(_queue, _folders);
        _relay.Failed += (file, server, detail) => _failures.Add($"{file} to {server}: {detail}");
    }

    public void Dispose()
    {
        _relay.Dispose();
        _folders.Dispose();
        _temp.Dispose();
    }

    private TransferItem Last => _queue.Items[^1];

    [Fact]
    public void An_entry_is_fetched_into_its_own_folder_and_sent_on_when_it_has_arrived()
    {
        Assert.True(_relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01"));

        var (remote, folder) = Assert.Single(_source.Downloads);
        Assert.Equal("/var/www/site.tar.gz", remote);
        Assert.StartsWith(Path.Combine(_temp.Path, "relay"), folder);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(_target.Uploads);

        _queue.Complete(Last.Id);

        Assert.Equal([(Path.Combine(folder, "site.tar.gz"), "/srv/backup")], _target.Uploads);
        Assert.Equal((TransferDirection.Upload, "backup01"), (Last.Direction, Last.Server));
        Assert.True(Directory.Exists(folder));

        // Ist der Upload fertig, liegt hier nichts mehr.
        _queue.Complete(Last.Id);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(_failures);
    }

    [Fact]
    public void The_rule_for_what_exists_travels_with_the_second_half()
    {
        Assert.True(_relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01", "site (2).tar.gz", TransferKeep.Newer));
        var folder = _source.Downloads[0].LocalDirectory;

        // Geholt wird unter dem Namen der Quelle und ohne Regel – der Zwischenordner ist leer.
        Assert.Equal([("/var/www/site.tar.gz", TransferKeep.None, null)], _source.Rules);

        _queue.Complete(Last.Id);

        Assert.Equal([(Path.Combine(folder, "site.tar.gz"), TransferKeep.Newer, "site (2).tar.gz")], _target.Rules);
        Assert.Equal("site (2).tar.gz", Last.FileName);
    }

    [Fact]
    public void Sending_asks_once_what_happens_to_what_the_target_already_has()
    {
        using var backend = new MockBackend();
        var shell = new ViewModels.ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), new FakePrompts(), dropDirectory: Path.Combine(_temp.Path, "rules"));
        var from = shell.OpenSession(shell.Profiles[0]);
        from.Start();
        var to = shell.OpenSession(shell.Profiles[0]);
        to.Start();

        // Beide zeigen denselben Ordner: Alles, was geschickt wird, gibt es am Ziel schon.
        var entries = from.Files.Entries.Where(entry => !entry.IsDirectory).Take(2).ToList();
        Assert.Equal(entries.Select(entry => entry.Name), ViewModels.ShellViewModel.ExistingAt(to, entries));
        var target = Assert.IsType<MockRemoteFileSystem>(to.Files.FileSystem);
        target.Rules.Clear();

        Assert.Equal(2, shell.SendToServer(from, entries, to, ExistingRule.Rename));
        foreach (var item in backend.Transfers.Items.Where(item => item.Direction == TransferDirection.Download).ToList())
        {
            ((MockTransferQueue)backend.Transfers).Complete(item.Id);
        }

        var uploads = target.Rules.Where(rule => rule.Name is not null).ToList();
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, upload => Assert.Equal(TransferKeep.None, upload.Keep));
        Assert.Equal(2, uploads.Select(upload => upload.Name).Distinct().Count());
        Assert.DoesNotContain(uploads, upload => entries.Exists(entry => entry.Name == upload.Name));
    }

    [Fact]
    public void A_failed_or_cancelled_download_leaves_nothing_behind_and_sends_nothing()
    {
        _relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01");
        var first = _source.Downloads[0].LocalDirectory;
        _queue.Fail(Last.Id, "connection");

        _relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01");
        var second = _source.Downloads[1].LocalDirectory;
        _queue.Cancel(Last.Id);

        Assert.Empty(_target.Uploads);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public void A_failed_upload_keeps_the_copy_so_that_retrying_can_send_it()
    {
        _relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01");
        var folder = _source.Downloads[0].LocalDirectory;
        _queue.Complete(Last.Id);

        _queue.Fail(Last.Id, "connection");

        Assert.True(Directory.Exists(folder));

        // Ein abgebrochener Upload dagegen räumt auf: Den will niemand wiederholen.
        _relay.Send(_source, "/var/www/site.tar.gz", _target, "/srv/backup", "backup01");
        var other = _source.Downloads[1].LocalDirectory;
        _queue.Complete(Last.Id);
        _queue.Cancel(Last.Id);

        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public void The_shell_sends_the_selection_to_the_folder_another_session_shows()
    {
        using var backend = new MockBackend();
        var shell = new ViewModels.ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), new FakePrompts(), dropDirectory: Path.Combine(_temp.Path, "shell"));
        var from = shell.OpenSession(shell.Profiles[0]);
        from.Start();

        // Ohne zweite Sitzung gibt es kein Ziel; die eigene zählt nicht.
        Assert.Empty(shell.RelayTargets(from));

        var to = shell.OpenSession(shell.Profiles[1]);
        to.Start();
        Assert.Equal([to], shell.RelayTargets(from));
        Assert.Equal([from], shell.RelayTargets(to));

        var entries = from.Files.Entries.Where(entry => !entry.IsDirectory).Take(2).ToList();
        Assert.Equal(2, entries.Count);

        Assert.Equal(2, shell.SendToServer(from, entries, to));

        Assert.Contains(shell.Output, entry => entry.Text == $"2 item(s) on their way from {from.Profile.Name} to {to.Profile.Name}:{to.Files.CurrentPath} (through this computer).");
        Assert.Equal(2, backend.Transfers.Items.Count(item => item.Direction == TransferDirection.Download && entries.Exists(entry => entry.Name == item.FileName)));

        // Eine getrennte Sitzung ist kein Ziel mehr.
        to.Disconnect();
        Assert.Empty(shell.RelayTargets(from));
    }
}

/// <summary>Von Server zu Server im Kern: ein Transfer, der beim Ziel in der Liste steht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class DirectRelayTests
{
    public DirectRelayTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Between_two_sessions_of_the_core_the_entry_travels_directly()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h1","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true},{"id":"p2","name":"backup01","host":"h2","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var from = backend.Connect(backend.GetProfiles()[0]);
        var to = backend.Connect(backend.GetProfiles()[1]);
        from.Terminal.Start();
        to.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        transport.Raise("""{"event":"session.state","session":2,"state":"connected","home":"/"}""");

        var id = from.Files!.SendTo("/var/www/site.tar.gz", to.Files!, "/srv/backup");

        Assert.NotNull(id);
        var request = Assert.Single(transport.Sent("transfer.start"));
        Assert.Equal(
            ("relay", 1UL, 2UL, "/var/www/site.tar.gz", "/srv/backup"),
            (request.GetProperty("direction").GetString(), request.GetProperty("session").GetUInt64(), request.GetProperty("target_session").GetUInt64(), request.GetProperty("remote").GetString(), request.GetProperty("local").GetString()));

        // In der Liste steht er beim Ziel, wie ein Upload dorthin – so frischt dessen Dateibrowser danach auf.
        var item = Assert.Single(backend.Transfers.Items);
        Assert.Equal(("site.tar.gz", "backup01", TransferDirection.Upload), (item.FileName, item.Server, item.Direction));

        // Ein neuer Versuch geht wieder von Server zu Server – und nennt, wie weit der abgebrochene kam: Der Kern
        // macht dann in dem weiter, was am Ziel schon liegt.
        transport.Raise($$"""{"event":"transfer.state","transfer":{{id}},"state":"failed","bytes":100,"total":700,"message":"connection: lost"}""");
        Assert.True(backend.Transfers.Retry(id!));
        var again = transport.Sent("transfer.start").Last();
        Assert.Equal(("relay", 2UL), (again.GetProperty("direction").GetString(), again.GetProperty("target_session").GetUInt64()));
        Assert.Equal(100, again.GetProperty("resume").GetInt64());

        // Ein gewöhnlicher Upload nennt kein Ziel; und ein fremdes Dateisystem ist keines, zu dem der Kern schicken kann.
        to.Files!.Upload(Path.Combine(Path.GetTempPath(), "x.bin"), "/srv");
        Assert.False(transport.Sent("transfer.start").Last().TryGetProperty("target_session", out _));
        Assert.Null(from.Files.SendTo("/var/www/site.tar.gz", new MockRemoteFileSystem("/"), "/"));

        // Die Regel für Vorhandenes und ein eigener Name am Ziel gehen mit; ohne beides steht davon nichts da.
        Assert.False(request.TryGetProperty("keep", out _));
        Assert.False(request.TryGetProperty("name", out _));
        var ruled = from.Files.SendTo("/var/www/site.tar.gz", to.Files, "/srv/backup", "site (2).tar.gz", TransferKeep.Same);
        var sent = transport.Sent("transfer.start").Last();
        Assert.Equal(("relay", "same", "site (2).tar.gz"), (sent.GetProperty("direction").GetString(), sent.GetProperty("keep").GetString(), sent.GetProperty("name").GetString()));
        Assert.Equal("site (2).tar.gz", backend.Transfers.Items.Single(item => item.Id == ruled).FileName);
    }
}
