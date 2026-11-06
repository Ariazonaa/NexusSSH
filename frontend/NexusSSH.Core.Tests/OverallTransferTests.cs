using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Der Stand aller Transfers zusammen – was das Symbol in der Taskleiste zeigt.</summary>
public sealed class OverallTransferTests
{
    private static (MockTransferQueue Queue, TransferCenterViewModel Center) Empty()
    {
        var queue = new MockTransferQueue();
        foreach (var item in queue.Items.Where(item => !item.IsFinished).ToList())
        {
            queue.Cancel(item.Id);
        }

        queue.ClearFinished();
        return (queue, new TransferCenterViewModel(queue, new ImmediateDispatcher()));
    }

    [Fact]
    public void Without_transfers_there_is_nothing_to_show()
    {
        var (queue, center) = Empty();
        using (queue)
        using (center)
        {
            Assert.Equal(OverallTransfer.Idle, center.Overall);
        }
    }

    [Fact]
    public void The_share_counts_everything_since_the_last_idle_so_it_never_jumps_back()
    {
        var (queue, center) = Empty();
        using (queue)
        using (center)
        {
            var small = queue.Add("small.bin", "web01", TransferDirection.Upload, 100);
            var big = queue.Add("big.bin", "web01", TransferDirection.Upload, 300);
            Assert.Equal(new OverallTransfer(OverallTransferState.Running, 0), center.Overall);

            // Der kleine wird fertig: ein Viertel von allem – nicht „0 von dem, was noch läuft“.
            queue.Complete(small);
            Assert.Equal(new OverallTransfer(OverallTransferState.Running, 0.25), center.Overall);

            // Scheitert einer, zählt er nicht mehr mit; ist nichts mehr unterwegs, ist auch der Balken weg.
            var broken = queue.Add("broken.bin", "web01", TransferDirection.Upload, 600);
            Assert.Equal(0.1, center.Overall.Fraction, 3);
            queue.Fail(broken, "connection: lost");
            Assert.Equal(0.25, center.Overall.Fraction, 3);

            queue.Complete(big);
            Assert.Equal(OverallTransfer.Idle, center.Overall);

            // Danach beginnt die Rechnung neu.
            queue.Add("next.bin", "web01", TransferDirection.Download, 50);
            Assert.Equal(new OverallTransfer(OverallTransferState.Running, 0), center.Overall);
        }
    }

    [Fact]
    public void An_unknown_amount_is_shown_as_such_and_paused_only_when_paused_here()
    {
        var (queue, center) = Empty();
        using (queue)
        using (center)
        {
            // Ein Ordner, der noch erfasst wird, kennt seine Menge nicht.
            queue.Add("folder", "web01", TransferDirection.Upload, 0);
            Assert.Equal(OverallTransferState.Unknown, center.Overall.State);

            queue.PauseAll();
            Assert.Equal(OverallTransferState.Paused, center.Overall.State);

            Assert.True(queue.Retry(queue.Items[0].Id));
            Assert.Equal(OverallTransferState.Unknown, center.Overall.State);
        }

        // Was beim Start schon angehalten dasteht, färbt das Symbol nicht.
        using var old = new MockTransferQueue();
        old.PauseAll();
        using var later = new TransferCenterViewModel(old, new ImmediateDispatcher());
        Assert.Equal(OverallTransfer.Idle, later.Overall);
    }
}
