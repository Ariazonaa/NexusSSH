using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Dateien des Servers im lokalen Editor bearbeiten: holen, öffnen, nach dem Speichern zurückschicken.</summary>
[Collection(LanguageCollection.Name)]
public sealed class RemoteEditorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly MockTransferQueue _queue = new();
    private readonly MockRemoteFileSystem _files;
    private readonly FakeEditorHost _host = new();
    private readonly RemoteEditor _editor;
    private readonly List<string> _events = [];
    private readonly string _work;

    public RemoteEditorTests()
    {
        L.Init(AppLanguage.English);
        _work = Path.Combine(_temp.Path, "edit");
        _files = new MockRemoteFileSystem("/etc", _queue, "web01")
            .File("/etc/nginx.conf", 1_834, hoursAgo: 1)
            .File("/etc/conf.d/nginx.conf", 20, hoursAgo: 1);
        _editor = new RemoteEditor(_queue, _host, _work);
        _editor.Opened += (file, server) => _events.Add($"opened {file} on {server}");
        _editor.Uploading += (file, server) => _events.Add($"uploading {file} to {server}");
        _editor.Failed += (file, server, detail) => _events.Add($"failed {file} on {server}: {detail}");
    }

    public void Dispose()
    {
        _editor.Dispose();
        _temp.Dispose();
    }

    private string LastTransfer => _queue.Items[^1].Id;

    [Fact]
    public void A_file_is_fetched_into_its_own_folder_and_opened_when_it_has_arrived()
    {
        Assert.Equal(EditOutcome.Started, _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834));

        var (remote, folder) = Assert.Single(_files.Downloads);
        Assert.Equal("/etc/nginx.conf", remote);
        Assert.StartsWith(_work, folder);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(_host.Launched);

        _queue.Complete(LastTransfer);

        Assert.Equal([Path.Combine(folder, "nginx.conf")], _host.Launched);
        Assert.Equal(["opened nginx.conf on web01"], _events);
    }

    [Fact]
    public void Every_save_sends_the_file_back_to_where_it_came_from()
    {
        _editor.Open(_files, "web01", "/etc/conf.d/nginx.conf", 20);
        _queue.Complete(LastTransfer);
        var local = Assert.Single(_host.Launched);

        _host.Save(local);
        _host.Save(local);

        Assert.Equal([(local, "/etc/conf.d"), (local, "/etc/conf.d")], _files.Uploads);
        Assert.Equal(2, _events.Count(text => text == "uploading nginx.conf to web01"));
    }

    private async Task<string> OpenKnownAsync(string path)
    {
        var entry = (await _files.ListAsync(RemotePath.Parent(path)!, TestContext.Current.CancellationToken)).Single(entry => entry.Name == RemotePath.Name(path));
        _editor.Open(_files, "web01", path, entry.Size, entry.Modified);
        _queue.Complete(LastTransfer);
        return _host.Launched[^1];
    }

    [Fact]
    public async Task A_file_that_changed_on_the_server_is_only_overwritten_when_the_user_says_so()
    {
        var asked = new List<string>();
        var overwrite = false;
        _editor.OverwriteChanged = (file, server) =>
        {
            asked.Add($"{file} on {server}");
            return Task.FromResult(overwrite);
        };
        _editor.Kept += (file, server) => _events.Add($"kept {file} on {server}");
        var local = await OpenKnownAsync("/etc/nginx.conf");

        // Unverändert am Server: Es wird nicht gefragt, die Datei geht zurück.
        _host.Save(local);
        Assert.Empty(asked);
        Assert.Single(_files.Uploads);
        _queue.Complete(LastTransfer);

        // Jemand anders ändert sie dort. Ohne Zustimmung bleibt der Server, wie er ist.
        _files.File("/etc/nginx.conf", 2_000, hoursAgo: 0);
        _host.Save(local);
        Assert.Equal(["nginx.conf on web01"], asked);
        Assert.Single(_files.Uploads);
        Assert.Contains("kept nginx.conf on web01", _events);

        // Beim nächsten Speichern wird wieder gefragt – diesmal mit Zustimmung.
        overwrite = true;
        _host.Save(local);
        Assert.Equal(2, asked.Count);
        Assert.Equal(2, _files.Uploads.Count);
    }

    [Fact]
    public async Task A_file_that_is_gone_from_the_server_counts_as_changed()
    {
        var asked = 0;
        _editor.OverwriteChanged = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var local = await OpenKnownAsync("/etc/nginx.conf");

        await _files.DeleteAsync("/etc/nginx.conf");
        _host.Save(local);

        Assert.Equal(1, asked);
        Assert.Empty(_files.Uploads);
    }

    [Fact]
    public async Task While_the_own_upload_is_running_a_save_is_not_taken_for_a_conflict()
    {
        var asked = 0;
        _editor.OverwriteChanged = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var local = await OpenKnownAsync("/etc/nginx.conf");

        // Zwei Speicherungen kurz nacheinander: Der erste Upload läuft noch, wenn die zweite kommt.
        _host.Save(local);
        _host.Save(local);

        Assert.Equal(0, asked);
        Assert.Equal(2, _files.Uploads.Count);
    }

    [Fact]
    public void Files_of_the_same_name_do_not_get_in_each_others_way()
    {
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);
        _editor.Open(_files, "web01", "/etc/conf.d/nginx.conf", 20);

        Assert.Equal(2, _files.Downloads.Select(download => download.LocalDirectory).Distinct().Count());
    }

    [Fact]
    public void Opening_again_fetches_nothing_twice_and_brings_the_editor_back()
    {
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);

        // Noch unterwegs: einfach abwarten.
        Assert.Equal(EditOutcome.AlreadyOpen, _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834));
        Assert.Single(_files.Downloads);
        Assert.Empty(_host.Launched);

        _queue.Complete(LastTransfer);
        Assert.Equal(EditOutcome.AlreadyOpen, _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834));
        Assert.Single(_files.Downloads);
        Assert.Equal(2, _host.Launched.Count);
        Assert.Single(_host.Launched.Distinct());

        // Dieselbe Datei auf einem anderen Server ist eine andere.
        Assert.Equal(EditOutcome.Started, _editor.Open(_files, "web02", "/etc/nginx.conf", 1_834));
    }

    [Fact]
    public void A_download_that_fails_is_reported_and_can_be_tried_again()
    {
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);
        _queue.Fail(LastTransfer, "permission denied");

        Assert.Equal(["failed nginx.conf on web01: permission denied"], _events);
        Assert.Empty(_host.Launched);
        Assert.Equal(EditOutcome.Started, _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834));
    }

    [Fact]
    public void A_cancelled_download_opens_nothing()
    {
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);
        _queue.Cancel(LastTransfer);

        Assert.Equal(["failed nginx.conf on web01: Cancelled"], _events);
        Assert.Empty(_host.Launched);
    }

    [Fact]
    public void An_editor_that_does_not_start_is_reported_and_nothing_is_watched()
    {
        _host.Fails = true;
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);
        _queue.Complete(LastTransfer);

        Assert.Equal(["failed nginx.conf on web01: the editor could not be started"], _events);
        Assert.Empty(_host.Watched);
    }

    [Fact]
    public void Large_files_are_not_fetched_for_editing()
    {
        Assert.Equal(EditOutcome.TooLarge, _editor.Open(_files, "web01", "/etc/nginx.conf", RemoteEditor.MaxSize + 1));
        Assert.Equal(EditOutcome.Started, _editor.Open(_files, "web01", "/etc/nginx.conf", RemoteEditor.MaxSize));
        Assert.Single(_files.Downloads);
    }

    [Fact]
    public void Something_without_a_name_is_refused()
    {
        Assert.Equal(EditOutcome.BadName, _editor.Open(_files, "web01", "/", 10));

        Assert.Empty(_files.Downloads);
        Assert.False(Directory.Exists(_work), "ohne Download entsteht auch kein Ordner");
    }

    [Fact]
    public void A_name_this_computer_cannot_hold_is_edited_under_the_one_the_backend_gives_it()
    {
        // Namen kommen vom Server: Ein Nullzeichen ist nirgends ein Dateiname, ein Doppelpunkt unter Windows nicht.
        Assert.Equal(EditOutcome.Started, _editor.Open(_files, "web01", "/etc/a\0b.txt", 10));

        var (remote, folder) = Assert.Single(_files.Downloads);
        Assert.Equal("/etc/a\0b.txt", remote);
        _queue.Complete(_queue.Items[^1].Id);

        // Geöffnet wird die Datei in ihrem eigenen Ordner – unter dem Namen, den das Backend genannt hat.
        var local = Assert.Single(_host.Launched);
        Assert.Equal(Path.Combine(folder, "a_b.txt"), local);

        // Zurück geht sie unter dem Namen, den sie auf dem Server hat – nicht als zweite Datei daneben.
        _host.Save(local);
        Assert.Equal((local, "/etc"), Assert.Single(_files.Uploads));
        Assert.Equal("a\0b.txt", _queue.Items[^1].FileName);
        Assert.DoesNotContain(_files.List("/etc"), entry => entry.Name == "a_b.txt");
    }

    [Fact]
    public void Closing_stops_watching_and_removes_the_fetched_copies()
    {
        _editor.Open(_files, "web01", "/etc/nginx.conf", 1_834);
        _queue.Complete(LastTransfer);
        File.WriteAllText(_host.Launched[0], "server { }");
        Assert.Single(_host.Watched);

        _editor.Dispose();

        Assert.Empty(_host.Watched);
        Assert.False(Directory.Exists(_work));

        // Was danach in der Warteschlange passiert, geht den Editor nichts mehr an.
        _queue.Add("late", "web01", TransferDirection.Download, 1);
        _queue.Complete(LastTransfer);
        Assert.Single(_host.Launched);
    }

    [Fact]
    public void Folders_left_behind_by_a_crashed_process_are_removed_at_the_next_start()
    {
        var root = Path.Combine(_temp.Path, "root");
        Directory.CreateDirectory(Path.Combine(root, "edit-111", "abc"));
        File.WriteAllText(Path.Combine(root, "edit-111", "abc", "secret.conf"), "password");
        Directory.CreateDirectory(Path.Combine(root, "edit-222"));
        Directory.CreateDirectory(Path.Combine(root, "edit-notanumber"));
        Directory.CreateDirectory(Path.Combine(root, "other"));

        RemoteEditor.RemoveStale(root, process => process == 222);

        Assert.Equal(
            ["edit-222", "edit-notanumber", "other"],
            Directory.GetDirectories(root).Select(Path.GetFileName).Order());

        // Ein Ordner, den es nicht gibt, ist kein Fehler.
        RemoteEditor.RemoveStale(Path.Combine(_temp.Path, "missing"), _ => false);
        Assert.EndsWith($"edit-{Environment.ProcessId}", RemoteEditor.WorkDirectory(root));
    }

    [Fact]
    public async Task A_double_click_on_a_file_edits_it_and_the_log_tells_what_happens()
    {
        var backend = new MockBackend();
        using var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), new FakePrompts(), _host, _work);
        var session = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        session.Start();
        await session.Files.LoadAsync();
        var file = session.Files.Entries.First(entry => !entry.IsDirectory);
        var queue = (MockTransferQueue)backend.Transfers;

        session.Files.OpenCommand.Execute(file);

        Assert.Contains(shell.Output, entry => entry.Text == $"Fetching {file.Name} from web01 for editing…");
        queue.Complete(queue.Items[^1].Id);
        Assert.EndsWith(file.Name, Assert.Single(_host.Launched));
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith($"{file.Name} (web01) opened in the editor", StringComparison.Ordinal));

        _host.Save(_host.Launched[0]);
        Assert.Contains(shell.Output, entry => entry.Text == $"{file.Name} saved – uploading to web01");

        // Ein Ordner wird geöffnet, nicht bearbeitet; zu Großes landet als Hinweis unter „Probleme“.
        shell.EditRemoteFile(session, new RemoteEntry("big.iso", false, RemoteEditor.MaxSize + 1, DateTimeOffset.MinValue), "/big.iso");
        Assert.Contains(shell.Problems, entry => entry.Text.StartsWith("big.iso is too large to edit", StringComparison.Ordinal));
        shell.EditRemoteFile(session, new RemoteEntry("dir", true, 0, DateTimeOffset.MinValue), "/dir");
        Assert.Single(_host.Launched);
    }

    [Fact]
    public async Task The_file_comes_from_the_server_whose_browser_was_used()
    {
        var backend = new MockBackend();
        using var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), new FakePrompts(), _host, _work);
        var web01 = backend.GetProfiles().Single(profile => profile.Id == "web01");

        // Zwei Server mit denselben Dateien unter demselben Pfad – die Einträge sehen völlig gleich aus.
        var first = shell.OpenSession(web01);
        first.Start();
        await first.Files.LoadAsync();
        var second = shell.OpenSession(web01 with { Id = "twin", Name = "web02" });
        second.Start();
        await second.Files.LoadAsync();
        var file = second.Files.Entries.First(entry => !entry.IsDirectory);
        Assert.Contains(file, first.Files.Entries);

        second.Files.OpenCommand.Execute(file);

        Assert.Contains(shell.Output, entry => entry.Text == $"Fetching {file.Name} from web02 for editing…");
        Assert.DoesNotContain(shell.Output, entry => entry.Text == $"Fetching {file.Name} from web01 for editing…");
    }

    [Fact]
    public async Task Without_an_editor_host_a_double_click_on_a_file_does_nothing()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        session.Start();
        await session.Files.LoadAsync();
        var before = backend.Transfers.Items.Count;

        session.Files.OpenCommand.Execute(session.Files.Entries.First(entry => !entry.IsDirectory));

        Assert.Equal(before, backend.Transfers.Items.Count);
    }
}
