using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Die Pille „Transfers“ in der Zeile unten zeigt, was läuft und wie es ausging (Wunsch des Users 2026-11-18: Vorher
/// stand dort nur „Transfers n“ in Grau, und ein kleiner Upload war vorbei, bevor man hinsah).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferBadgeTests
{
    private static (MockTransferQueue Queue, TransferCenterViewModel Center, Clock Clock) Empty()
    {
        L.Init(AppLanguage.English);
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
    public void Without_transfers_it_reads_as_before()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);
            Assert.Equal("Transfers 0", center.Badge.Name);
            Assert.False(center.Badge.HasIcon);
            Assert.False(center.Badge.ShowsProgress);
        }
    }

    [Fact]
    public void A_single_upload_shows_its_name_its_share_and_then_that_it_is_done_for_a_while()
    {
        var (queue, center, clock) = Empty();
        using (queue)
        using (center)
        {
            var id = queue.Add("index.php", "web01", TransferDirection.Upload, 1000);
            queue.Advance();
            queue.Advance();
            var badge = center.Badge;

            Assert.Equal(TransferBadgeKind.Running, badge.Kind);
            Assert.Equal(TransferBadgeDirection.Up, badge.Direction);
            Assert.Equal("index.php", badge.Text);
            Assert.True(badge.ShowsProgress);
            Assert.False(badge.IsIndeterminate);
            Assert.Equal(queue.Items.Single().Progress * 100, badge.Percent, 3);
            Assert.EndsWith("%", badge.Number, StringComparison.Ordinal);
            Assert.StartsWith("web01", badge.ToolTip, StringComparison.Ordinal);

            queue.Complete(id);
            Assert.Equal(TransferBadgeKind.Done, badge.Kind);
            Assert.Equal("index.php uploaded", badge.Name);
            Assert.False(badge.ShowsProgress);

            // Erst nach der Zeit wieder ruhig – so sieht man auch einen Upload, der nur einen Augenblick dauerte.
            Assert.Equal(TransferBadge.DoneFor, clock.Due);
            clock.Fire();
            Assert.Equal(TransferBadgeKind.Idle, badge.Kind);
            Assert.Equal("Transfers 0", badge.Name);
        }
    }

    [Fact]
    public void Several_count_together_and_mixed_directions_show_both()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            var small = queue.Add("small.bin", "web01", TransferDirection.Upload, 100);
            var big = queue.Add("big.bin", "web01", TransferDirection.Download, 300);
            var badge = center.Badge;

            Assert.Equal("2 transfers", badge.Text);
            Assert.Equal(TransferBadgeDirection.Both, badge.Direction);

            // Der Stand aller zusammen: ein Viertel, wenn der kleine fertig ist – und dann der Name des letzten.
            queue.Complete(small);
            Assert.Equal("big.bin", badge.Text);
            Assert.Equal(TransferBadgeDirection.Down, badge.Direction);

            queue.Complete(big);
            Assert.Equal(TransferBadgeKind.Done, badge.Kind);
            Assert.Equal("2 transfers done", badge.Text);
        }
    }

    [Fact]
    public void A_failure_stays_until_it_is_seen_and_outranks_what_went_well()
    {
        var (queue, center, clock) = Empty();
        using (queue)
        using (center)
        {
            var good = queue.Add("good.bin", "web01", TransferDirection.Upload, 100);
            var bad = queue.Add("bad.bin", "web01", TransferDirection.Upload, 100);
            queue.Complete(good);
            queue.Fail(bad, "connection: lost");
            var badge = center.Badge;

            Assert.Equal(TransferBadgeKind.Failed, badge.Kind);
            Assert.Equal("bad.bin failed", badge.Text);
            Assert.Null(clock.Due);

            badge.Acknowledge();
            Assert.Equal(TransferBadgeKind.Idle, badge.Kind);
        }
    }

    [Fact]
    public void A_failure_goes_when_its_transfer_is_cleared_or_retried()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            var bad = queue.Add("bad.bin", "web01", TransferDirection.Download, 100);
            queue.Fail(bad, "connection: lost");
            Assert.Equal(TransferBadgeKind.Failed, center.Badge.Kind);

            queue.Retry(bad);
            Assert.Equal(TransferBadgeKind.Running, center.Badge.Kind);
            queue.Fail(bad, "connection: lost");
            Assert.Equal(TransferBadgeKind.Failed, center.Badge.Kind);

            queue.ClearFinished();
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);
        }
    }

    [Fact]
    public void Pausing_shows_what_waits_and_resuming_runs_again()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            queue.Add("a.bin", "web01", TransferDirection.Upload, 100);
            queue.Add("b.bin", "web01", TransferDirection.Upload, 100);
            center.PauseAll();
            Assert.Equal(TransferBadgeKind.Paused, center.Badge.Kind);
            Assert.Equal("2 paused", center.Badge.Text);

            center.ResumeAll();
            Assert.Equal(TransferBadgeKind.Running, center.Badge.Kind);
        }
    }

    [Fact]
    public void Cancelling_everything_needs_no_message_and_a_new_run_ends_the_old_one()
    {
        var (queue, center, clock) = Empty();
        using (queue)
        using (center)
        {
            queue.Cancel(queue.Add("a.bin", "web01", TransferDirection.Upload, 100));
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);

            queue.Complete(queue.Add("b.bin", "web01", TransferDirection.Upload, 100));
            Assert.Equal(TransferBadgeKind.Done, center.Badge.Kind);

            // Beginnt gleich der nächste, zeigt die Pille ihn – und das alte Ende von „fertig“ wirft ihn nicht hinaus.
            queue.Add("c.bin", "web01", TransferDirection.Upload, 100);
            clock.Fire();
            Assert.Equal(TransferBadgeKind.Running, center.Badge.Kind);
        }
    }

    [Fact]
    public void Until_the_size_is_known_the_bar_runs_without_a_share()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            queue.Add("folder", "web01", TransferDirection.Download, 0);
            Assert.True(center.Badge.IsIndeterminate);
            Assert.False(center.Badge.HasNumber);
            Assert.Equal("folder", center.Badge.Name);
        }
    }

    [Fact]
    public void Clicking_the_pill_marks_a_done_message_as_seen_but_leaves_a_running_one()
    {
        var (queue, center, _) = Empty();
        using (queue)
        using (center)
        {
            queue.Complete(queue.Add("b.bin", "web01", TransferDirection.Upload, 100));
            Assert.Equal(TransferBadgeKind.Done, center.Badge.Kind);
            center.Badge.Acknowledge();
            Assert.Equal(TransferBadgeKind.Idle, center.Badge.Kind);

            queue.Add("c.bin", "web01", TransferDirection.Upload, 100);
            center.Badge.Acknowledge();
            Assert.Equal(TransferBadgeKind.Running, center.Badge.Kind);
        }
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
}
