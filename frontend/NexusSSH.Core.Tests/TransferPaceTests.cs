using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Tempo und Restzeit eines Transfers, „alle anhalten“ und die Obergrenze für die Bandbreite.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferPaceTests
{
    private const long Mib = 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public TransferPaceTests() => L.Init(AppLanguage.English);

    // Eine Uhr, die der Test stellt.
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void The_rate_is_the_average_of_the_last_seconds()
    {
        var rate = new TransferRate();
        Assert.Null(rate.BytesPerSecond);

        rate.Add(Start, 0);
        rate.Add(Start.AddMilliseconds(500), Mib);
        Assert.Null(rate.BytesPerSecond);

        // Zwei MiB je Sekunde, zehn Sekunden lang.
        for (var second = 1; second <= 10; second++)
        {
            rate.Add(Start.AddSeconds(second), 2 * Mib * second);
        }

        Assert.Equal(2 * Mib, rate.BytesPerSecond!.Value, 1.0);
        Assert.Equal(TimeSpan.FromSeconds(40), rate.Remaining(100 * Mib));
        Assert.Null(rate.Remaining(20 * Mib));

        // Danach steht er: Der Schnitt über das Fenster fällt, bis nichts mehr kommt.
        rate.Add(Start.AddSeconds(20), 20 * Mib);
        Assert.Equal(0, rate.BytesPerSecond);
        Assert.Null(rate.Remaining(100 * Mib));

        // Ein kleinerer Stand ist ein neuer Versuch.
        rate.Add(Start.AddSeconds(21), Mib);
        Assert.Null(rate.BytesPerSecond);
    }

    [Fact]
    public void A_running_transfer_shows_its_speed_and_what_is_left()
    {
        var time = new Clock(Start);
        var item = new TransferItem("1", "big.iso", "web01", TransferDirection.Download, TransferState.Running, 0, 0, 100 * Mib);
        var row = new TransferItemViewModel(item, time);
        Assert.Equal("0 B / 100 MB", row.DetailText);

        for (var second = 1; second <= 4; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            row.Update(item with { Bytes = 5 * Mib * second, Progress = 0.05 * second });
        }

        Assert.Equal("20.0 MB / 100 MB  ·  5.0 MB/s  ·  16 s left", row.DetailText);

        // Setzt ein neuer Versuch fort, zählt der Sprung auf das schon Übertragene nicht als Tempo.
        var resumed = new TransferItemViewModel(item, time);
        time.Advance(TimeSpan.FromSeconds(2));
        resumed.Update(item with { Bytes = 60 * Mib, ResumedAt = 60 * Mib });
        Assert.DoesNotContain("/s", resumed.DetailText);
        time.Advance(TimeSpan.FromSeconds(2));
        resumed.Update(item with { Bytes = 62 * Mib, ResumedAt = 60 * Mib });
        Assert.Contains("1.0 MB/s", resumed.DetailText);

        // Fertig: kein Tempo mehr.
        row.Update(item with { State = TransferState.Completed, Bytes = 100 * Mib, Progress = 1 });
        Assert.Equal("100 MB", row.DetailText);
    }

    [Fact]
    public void Pausing_all_stops_what_runs_without_calling_it_a_failure_and_resuming_continues()
    {
        using var queue = new MockTransferQueue();
        var first = queue.Add("a.bin", "web01", TransferDirection.Upload, 10 * Mib);
        var second = queue.Add("b.bin", "web01", TransferDirection.Download, 10 * Mib);
        queue.Complete(first);
        var third = queue.Add("c.bin", "web01", TransferDirection.Upload, 10 * Mib);
        var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var failed = new List<string>();
        center.TransferFailed += (file, _, _) => failed.Add(file);
        Assert.True(center.CanPauseAll);
        Assert.False(center.CanResumeAll);

        center.PauseAll();

        Assert.False(center.CanPauseAll);
        Assert.True(center.CanResumeAll);
        Assert.Empty(failed);
        var paused = center.Items.Where(row => row.IsPaused).ToList();
        // Die Warteschlange bringt Beispiele mit; angehalten ist alles, was noch nicht fertig war – nicht das Fertige.
        Assert.Contains(second, paused.Select(row => row.Id));
        Assert.Contains(third, paused.Select(row => row.Id));
        Assert.DoesNotContain(first, paused.Select(row => row.Id));
        Assert.All(paused, row => Assert.Equal(("Paused", true, false), (row.StatusText, row.CanRetry, row.CanCancel)));
        Assert.Equal("a.bin, web01, Completed", center.Items.Single(row => row.Id == first).ToString());

        Assert.Equal(paused.Count, center.ResumeAll());
        Assert.False(center.CanResumeAll);
        Assert.Equal(paused.Count, center.Items.Count(row => row.CanCancel));
        Assert.Equal("Paused – “Resume” continues from there", FailureText.OfTransfer(ITransferQueue.PausedDetail));
    }

    [Fact]
    public void The_limit_is_told_to_the_queue_at_the_start_and_whenever_it_changes()
    {
        var settings = new AppSettings { TransferLimitKib = 512 };
        var shell = TestShell.Create(out var backend, settings);
        var queue = (MockTransferQueue)backend.Transfers;
        Assert.Equal(512 * 1024, queue.Limit);

        shell.TransferLimitKib = 2048;
        Assert.Equal((2048, 2048 * 1024L), (settings.TransferLimitKib, queue.Limit));

        shell.TransferLimitKib = -5;
        Assert.Equal((0, 0L), (settings.TransferLimitKib, queue.Limit));

        settings.TransferLimitKib = int.MaxValue;
        settings.Sanitize();
        Assert.Equal(AppSettings.MaxTransferLimitKib, settings.TransferLimitKib);
    }

    [Fact]
    public void The_display_of_the_x_server_is_told_to_the_backend_when_it_is_not_the_usual_one()
    {
        var shell = TestShell.Create(out var backend, new AppSettings { X11Display = 2 });
        Assert.Equal(2, backend.X11Display);

        shell.X11Display = 500;
        Assert.Equal((AppSettings.MaxX11Display, AppSettings.MaxX11Display), (shell.Settings.X11Display, backend.X11Display));

        shell.X11Display = 0;
        Assert.Equal(0, backend.X11Display);
    }
}
