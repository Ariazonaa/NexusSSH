using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Löschen ist unten zu sehen wie ein Upload (Wunsch des Users 2026-11-19: Bei großen Ordnern geschah vorher
/// scheinbar minutenlang nichts): die Pille mit Papierkorb und Zähler, eine Zeile je Löschvorgang in den Transfers mit
/// „Abbrechen“, danach „gelöscht“ oder „fehlgeschlagen“ – nach denselben Regeln wie bei Transfers.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class DeletionTests
{
    private const string Web01 =
        """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    // Kehrt die Schreibrichtung um (U+202E) – darf aus Text des Servers nie in der Oberfläche ankommen.
    private static readonly string Reversed = ((char)0x202E).ToString();

    public DeletionTests() => L.Init(AppLanguage.English);

    private static (MockTransferQueue Queue, TransferCenterViewModel Center, Clock Clock) Empty()
    {
        var queue = new MockTransferQueue();
        foreach (var item in queue.Items.Where(item => !item.IsFinished).ToList())
        {
            queue.Cancel(item.Id);
        }

        queue.ClearFinished();
        var clock = new Clock();
        return (queue, new TransferCenterViewModel(queue, new ImmediateDispatcher(), clock), clock);
    }

    [Fact]
    public async Task A_running_deletion_shows_the_bin_its_name_and_how_many_are_gone()
    {
        var (queue, center, clock) = Empty();
        using (queue)
        using (center)
        {
            var run = new Manual();
            var row = center.StartDeletion("node_modules", "web01", run.Start);
            var badge = center.Badge;

            Assert.Equal(TransferBadgeKind.Running, badge.Kind);
            Assert.Equal(TransferBadgeDirection.Delete, badge.Direction);
            Assert.Equal("node_modules", badge.Text);
            Assert.Equal("· 0 deleted", badge.Number);
            Assert.True(badge.ShowsProgress);
            Assert.True(badge.IsIndeterminate);
            Assert.Equal("web01", badge.ToolTip);
            Assert.Same(row, Assert.Single(center.Deletions));
            Assert.True(row.CanCancel);

            run.Send(DeletionState.Running, 1234);
            Assert.Equal("node_modules · 1,234 deleted", badge.Name);
            Assert.Equal(("Deleting", "1,234 deleted"), (row.StatusText, row.DetailText));
            Assert.Equal("node_modules, web01, Deleting", row.ToString());

            run.Send(DeletionState.Done, 2000);
            Assert.Equal(TransferBadgeKind.Done, badge.Kind);
            Assert.Equal("node_modules deleted", badge.Text);
            Assert.False(badge.ShowsProgress);
            Assert.Equal(("Deleted", "2,000 deleted"), (row.StatusText, row.DetailText));
            Assert.False(row.CanCancel);
            Assert.True(row.Finished.IsCompletedSuccessfully);
            Assert.Equal(new DeletionStatus(DeletionState.Done, 2000), await row.Finished);

            // „gelöscht“ steht so lange wie „hochgeladen“.
            Assert.Equal(TransferBadge.DoneFor, clock.Due);
            clock.Fire();
            Assert.Equal(TransferBadgeKind.Idle, badge.Kind);
        }
    }

    [Fact]
    public async Task A_failed_deletion_stays_red_until_it_is_seen_and_names_the_reason()
    {
        var (queue, center, clock) = Empty();
        using (queue)
        using (center)
        {
            List<string> logged = [];
            center.DeletionFailed += (name, server, reason) => logged.Add($"{name}|{server}|{reason}");
            var run = new Manual();
            var row = center.StartDeletion("node_modules", "web01", run.Start);
            run.Send(DeletionState.Running, 12);
            run.Send(DeletionState.Failed, 13, "permission_denied", "/srv/node_modules/x: Permission denied" + Reversed);

            Assert.Equal(TransferBadgeKind.Failed, center.Badge.Kind);
            Assert.Equal(TransferBadgeDirection.Delete, center.Badge.Direction);
            Assert.Equal("node_modules: delete failed", center.Badge.Text);
            Assert.Null(clock.Due);
            Assert.Equal("Failed", row.StatusText);
            Assert.StartsWith("13 deleted  ·  ", row.DetailText, StringComparison.Ordinal);
            Assert.Contains("Permission denied", row.DetailText, StringComparison.Ordinal);

            // Der Text des Servers kommt gesäubert an: ohne Zeichen, die die Schreibrichtung umkehren.
            Assert.DoesNotContain(Reversed, row.DetailText, StringComparison.Ordinal);
            Assert.Equal(row.Failure, Assert.Single(logged).Split('|')[2]);
            Assert.Equal(DeletionState.Failed, (await row.Finished).State);

            center.Badge.Acknowledge();
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);

            // Ein zweites Scheitern bleibt, bis die Zeile geht.
            var again = new Manual();
            center.StartDeletion("logs", "web01", again.Start);
            again.Send(DeletionState.Failed, 0, "not_found", "gone");
            Assert.Equal(TransferBadgeKind.Failed, center.Badge.Kind);
            center.ClearFinished();
            Assert.Empty(center.Deletions);
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);
        }
    }

    [Fact]
    public void Cancelling_stops_the_run_needs_no_message_and_late_news_change_nothing()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            var run = new Manual();
            var row = center.StartDeletion("node_modules", "web01", run.Start);
            run.Send(DeletionState.Running, 40);

            center.Cancel(row);
            Assert.True(run.Cancelled);
            Assert.Equal(TransferBadgeKind.Running, center.Badge.Kind);

            run.Send(DeletionState.Cancelled, 41);
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);
            Assert.Equal(("Cancelled", "41 deleted"), (row.StatusText, row.DetailText));
            Assert.False(row.CanCancel);

            // Was nach dem Ende noch kommt, zählt nicht; ein zweites Abbrechen erreicht den Kern nicht mehr.
            run.Cancelled = false;
            run.Send(DeletionState.Running, 99);
            Assert.Equal(41, row.Removed);
            row.Cancel();
            Assert.False(run.Cancelled);

            // Wer aufräumt, nimmt beendete Löschvorgänge mit, laufende nicht.
            var running = center.StartDeletion("cache", "web01", new Manual().Start);
            center.ClearFinished();
            Assert.Same(running, Assert.Single(center.Deletions));
        }
    }

    [Fact]
    public void Transfers_and_deletions_together_are_counted_as_tasks_and_a_failure_outranks_the_rest()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            var upload = queue.Add("index.php", "web01", TransferDirection.Upload, 1000);
            var run = new Manual();
            center.StartDeletion("node_modules", "web01", run.Start);
            var badge = center.Badge;

            Assert.Equal("2 tasks", badge.Text);
            Assert.Equal(TransferBadgeDirection.Both, badge.Direction);
            Assert.True(badge.IsIndeterminate);
            Assert.False(badge.HasNumber);

            // Ist der Upload fertig, zeigt die Pille wieder nur das Löschen.
            queue.Complete(upload);
            Assert.Equal("node_modules", badge.Text);
            Assert.Equal(TransferBadgeDirection.Delete, badge.Direction);

            // Gescheitert vor fertig – auch wenn das eine ein Transfer war und das andere ein Löschen.
            run.Send(DeletionState.Failed, 3, "permission_denied", "denied");
            Assert.Equal(TransferBadgeKind.Failed, badge.Kind);
            Assert.Equal("node_modules: delete failed", badge.Text);

            badge.Acknowledge();
            queue.Complete(queue.Add("a.bin", "web01", TransferDirection.Download, 10));
            Assert.Equal("a.bin downloaded", badge.Text);

            // Beides fertig: die Zahl der Vorgänge.
            var download = queue.Add("b.bin", "web01", TransferDirection.Download, 10);
            var second = new Manual();
            center.StartDeletion("tmp", "web01", second.Start);
            queue.Complete(download);
            second.Send(DeletionState.Done, 5);
            Assert.Equal(TransferBadgeKind.Done, badge.Kind);
            Assert.Equal("2 tasks done", badge.Text);
            Assert.Equal(TransferBadgeDirection.Both, badge.Direction);
        }
    }

    [Fact]
    public void A_paused_transfer_outranks_a_finished_deletion_and_several_deletions_add_up()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            queue.Add("a.bin", "web01", TransferDirection.Upload, 100);
            var run = new Manual();
            center.StartDeletion("node_modules", "web01", run.Start);
            center.PauseAll();
            run.Send(DeletionState.Done, 7);
            Assert.Equal(TransferBadgeKind.Paused, center.Badge.Kind);

            queue.ClearFinished();
            center.ClearFinished();
            var one = new Manual();
            var two = new Manual();
            center.StartDeletion("a", "web01", one.Start);
            center.StartDeletion("b", "db01", two.Start);
            one.Send(DeletionState.Running, 1000);
            two.Send(DeletionState.Running, 500);
            Assert.Equal("2 deletions", center.Badge.Text);
            Assert.Equal("· 1,500 deleted", center.Badge.Number);

            one.Send(DeletionState.Done, 1000);
            two.Send(DeletionState.Done, 600);
            Assert.Equal("2 deletions done", center.Badge.Text);
        }
    }

    [Fact]
    public async Task The_file_browser_deletes_as_one_task_counts_every_entry_and_reloads()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            var files = new MockRemoteFileSystem("/var/www", queue, "web01")
                .File("/var/www/html/index.html", 100, hoursAgo: 1)
                .File("/var/www/html/css/site.css", 50, hoursAgo: 1)
                .File("/var/www/nginx.conf", 200, hoursAgo: 2)
                .File("/var/www/keep.txt", 1, hoursAgo: 2);
            var browser = new SftpBrowserViewModel(files) { Transfers = center, Profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", "", []) };
            await browser.LoadAsync();

            Assert.True(await browser.DeleteAsync([.. browser.Entries.Where(entry => entry.Name is "html" or "nginx.conf")]));

            // Zwei Einträge, ein Vorgang: index.html, site.css, css, html, nginx.conf.
            var row = Assert.Single(center.Deletions);
            Assert.Equal(("2 items", "web01", 5L), (row.Name, row.Server, row.Removed));
            Assert.Equal("keep.txt", Assert.Single(browser.Entries).Name);
            Assert.False(browser.HasError);

            // Was es nicht mehr gibt: Der Fehler steht in der Liste wie bisher, und in der Zeile.
            var stale = browser.Entries.Single();
            await files.DeleteAsync("/var/www/keep.txt");
            Assert.False(await browser.DeleteAsync([stale]));
            Assert.StartsWith("Not found.", browser.Error, StringComparison.Ordinal);
            Assert.Equal(DeletionState.Failed, center.Deletions[1].State);
            Assert.Equal("keep.txt", center.Deletions[1].Name);
        }
    }

    [Fact]
    public async Task The_demo_files_delete_step_by_step_and_stop_when_asked()
    {
        var files = new MockRemoteFileSystem("/srv") { DeleteStep = TimeSpan.FromMilliseconds(5) };
        for (var number = 0; number < 200; number++)
        {
            files.File($"/srv/node_modules/{number}.js", 1, hoursAgo: 1);
        }

        var seen = new List<DeletionStatus>();
        var finished = new TaskCompletionSource<DeletionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDeletion? run = null;
        run = files.StartDelete(["/srv/node_modules"], status =>
        {
            lock (seen)
            {
                seen.Add(status);
            }

            // Nach dem dritten anhalten – der Rest bleibt. (Die erste Meldung kommt im Aufruf selbst, mit 0.)
            if (status.Removed >= 3)
            {
                run?.Cancel();
            }

            if (status.IsFinished)
            {
                finished.TrySetResult(status);
            }
        });

        var end = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new DeletionStatus(DeletionState.Cancelled, 3), end);
        Assert.Equal(197, (await files.ListAsync("/srv/node_modules", TestContext.Current.CancellationToken)).Count);
        lock (seen)
        {
            Assert.Equal(new DeletionStatus(DeletionState.Running, 0), seen[0]);
            Assert.Contains(new DeletionStatus(DeletionState.Running, 2), seen);
        }
    }

    [Fact]
    public void Deleting_here_counts_every_entry_stops_when_asked_and_never_enters_a_link()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-deletion-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "big", "inner"));
            File.WriteAllText(Path.Combine(root, "big", "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "big", "inner", "b.txt"), "b");
            File.WriteAllText(Path.Combine(root, "single.txt"), "s");

            // Ein Verweis im Ordner auf etwas außerhalb: Er geht, das Ziel bleibt (wo das Anlegen erlaubt ist).
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "p");
            var linked = TryLink(Path.Combine(root, "big", "link"), outside);

            var done = Deletions.DeleteLocally([Path.Combine(root, "big"), Path.Combine(root, "single.txt")], () => false, _ => { });

            // a.txt, b.txt, inner, big, single.txt – und der Verweis, wenn es ihn gab.
            Assert.Equal(new DeletionStatus(DeletionState.Done, linked ? 6 : 5), done);
            Assert.False(Directory.Exists(Path.Combine(root, "big")));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));

            // Anhalten nach zwei: Was weg ist, ist weg, der Rest bleibt.
            for (var number = 0; number < 5; number++)
            {
                File.WriteAllText(Path.Combine(outside, $"{number}.tmp"), "x");
            }

            long removed = 0;
            var stopped = Deletions.DeleteLocally([outside], () => removed >= 2, count => removed = count);
            Assert.Equal(new DeletionStatus(DeletionState.Cancelled, 2), stopped);
            Assert.Equal(4, Directory.GetFiles(outside).Length);

            var missing = Deletions.DeleteLocally([Path.Combine(root, "nope")], () => false, _ => { });
            Assert.Equal((DeletionState.Failed, 0L, "not_found"), (missing.State, missing.Removed, missing.Code));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Deleting_forever_here_runs_as_a_task_of_this_computer()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-deletion-" + Guid.NewGuid().ToString("N"));
        var (queue, center, _) = Empty();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha", "inner"));
            File.WriteAllText(Path.Combine(root, "alpha", "inner", "x.txt"), "x");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "n");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "k");

            // Der Papierkorb wird nie gefragt, wenn endgültig gelöscht wird.
            var local = new LocalBrowserViewModel(root) { Transfers = center, Recycle = _ => throw new InvalidOperationException("recycled") };
            Assert.True(await local.DeleteForeverAsync(local.Entries.Where(entry => entry.Name is "alpha" or "notes.txt")));

            var row = Assert.Single(center.Deletions);
            Assert.Equal(("2 items", "This computer", DeletionState.Done, 4L), (row.Name, row.Server, row.State, row.Removed));
            Assert.Equal(["keep.txt"], local.Entries.Select(entry => entry.Name));
            Assert.False(local.HasError);
        }
        finally
        {
            center.Dispose();
            queue.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void The_core_hears_start_and_cancel_by_the_apps_number_and_reports_the_end_once()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Web01}}]}""";
        using var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        var files = connection.Files!;

        var seen = new List<DeletionStatus>();
        var run = files.StartDelete(["/srv/node_modules", "/srv/a b"], seen.Add);
        var start = Assert.Single(transport.Sent("delete.start"));
        Assert.Equal(
            """{"op":"delete.start","session":1,"paths":["/srv/node_modules","/srv/a b"],"delete":1}""",
            start.GetRawText());

        transport.Raise("""{"event":"delete.state","delete":1,"removed":0,"state":"running"}""");
        transport.Raise("""{"event":"delete.state","delete":2,"removed":5,"state":"running"}""");
        transport.Raise("""{"event":"delete.state","delete":1,"removed":250,"state":"running"}""");
        run.Cancel();
        Assert.Equal("""{"op":"delete.cancel","delete":1}""", Assert.Single(transport.Sent("delete.cancel")).GetRawText());

        transport.Raise("""{"event":"delete.state","delete":1,"removed":251,"state":"cancelled"}""");
        transport.Raise("""{"event":"delete.state","delete":1,"removed":251,"state":"cancelled"}""");
        Assert.Equal(
            [
                new DeletionStatus(DeletionState.Running, 0),
                new DeletionStatus(DeletionState.Running, 250),
                new DeletionStatus(DeletionState.Cancelled, 251),
            ],
            seen);

        // Nach dem Ende fragt die App den Kern nicht mehr.
        run.Cancel();
        Assert.Single(transport.Sent("delete.cancel"));

        // Ein Fehler kommt mit Art und Meldung; lehnt der Kern schon den Start ab, steht das gleich da.
        var failed = new List<DeletionStatus>();
        files.StartDelete(["/etc"], failed.Add);
        transport.Raise("""{"event":"delete.state","delete":2,"removed":3,"state":"failed","error":"permission_denied","message":"Permission denied"}""");
        Assert.Equal([new DeletionStatus(DeletionState.Failed, 3, "permission_denied", "Permission denied")], failed);

        transport.Responses["delete.start"] = """{"ok":false,"error":"no_sftp","message":"1"}""";
        var refused = new List<DeletionStatus>();
        files.StartDelete(["/etc"], refused.Add);
        Assert.Equal([new DeletionStatus(DeletionState.Failed, 0, "no_sftp", "1")], refused);
    }

    /// <summary>Ein Löschvorgang, dessen Meldungen der Test selbst schickt.</summary>
    private sealed class Manual : IDeletion
    {
        private Action<DeletionStatus>? _report;

        public bool Cancelled { get; set; }

        public IDeletion Start(Action<DeletionStatus> changed)
        {
            _report = changed;
            changed(new DeletionStatus(DeletionState.Running, 0));
            return this;
        }

        public void Send(DeletionState state, long removed, string? code = null, string? message = null)
            => _report!(new DeletionStatus(state, removed, code, message));

        public void Cancel() => Cancelled = true;
    }

    // Eine Uhr, deren einziger Timer auf Kommando auslöst.
    private sealed class Clock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan? Due { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            (_callback, _state, Due) = (callback, state, dueTime);
            return new Handle(this);
        }

        public void Fire() => _callback?.Invoke(_state);

        private sealed class Handle(Clock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => clock.Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
