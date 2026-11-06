using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ein gescheiterter oder abgebrochener Transfer lässt sich aus dem Transfer Center noch einmal starten.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferRetryTests
{
    public TransferRetryTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Only_failed_and_cancelled_transfers_offer_a_retry()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var (running, done, waiting) = (center.Items[0], center.Items[1], center.Items[2]);
        Assert.All(center.Items, item => Assert.False(item.CanRetry));
        Assert.False(center.Retry(running));
        Assert.False(center.Retry(done));

        queue.Fail(running.Id, "connection: lost");
        center.Cancel(waiting);

        Assert.True(running.CanRetry);
        Assert.True(waiting.CanRetry);
        Assert.False(done.CanRetry);
        Assert.False(running.CanCancel);
    }

    [Fact]
    public void A_retried_transfer_waits_again_and_starts_from_the_beginning()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var row = center.Items[0];
        queue.Fail(row.Id, "connection: lost");

        Assert.True(center.Retry(row));

        var item = queue.Items.Single(item => item.Id == row.Id);
        Assert.Equal((TransferState.Queued, 0d, 0L, null), (item.State, item.Progress, item.Bytes, item.Detail));
        Assert.False(row.CanRetry);
        Assert.True(row.CanCancel);
        Assert.Equal("Upload queued", row.StatusText);
    }

    [Fact]
    public void A_transfer_that_fails_again_after_a_retry_is_reported_again()
    {
        var shell = TestShell.Create(out var backend);
        var queue = (MockTransferQueue)backend.Transfers;
        var id = queue.Items[0].Id;

        queue.Fail(id, "connection: lost");
        Assert.Single(shell.Problems);

        shell.Transfers.Retry(shell.Transfers.Items.Single(row => row.Id == id));
        queue.Fail(id, "no space left on device");

        Assert.Equal(2, shell.Problems.Count);
        Assert.EndsWith("no space left on device", shell.Problems[1].Text);
    }

    [Fact]
    public void A_transfer_that_is_gone_cannot_be_retried()
    {
        using var queue = new MockTransferQueue();
        using var center = new TransferCenterViewModel(queue, new ImmediateDispatcher());
        var row = center.Items[2];
        center.Cancel(row);
        center.ClearFinished();

        Assert.False(center.Retry(row));
    }
}
