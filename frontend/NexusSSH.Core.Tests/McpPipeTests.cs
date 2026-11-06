using System.Text.Json;
using NexusSSH.Core.Mcp;

namespace NexusSSH.Core.Tests;

/// <summary>Der Weg vom Programm für MCP-Programme zur laufenden App: eine Verbindung je Aufruf.</summary>
public sealed class McpPipeTests
{
    private static string NewName() => "NexusSSH-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Each_data_folder_has_its_own_pipe()
    {
        Assert.Equal(McpPipe.Name(@"C:\data"), McpPipe.Name(@"c:\DATA"));
        Assert.NotEqual(McpPipe.Name(@"C:\data"), McpPipe.Name(@"C:\other"));
    }

    [Fact]
    public async Task A_call_travels_to_the_app_and_back()
    {
        var name = NewName();
        using var stop = new CancellationTokenSource();
        var listening = McpPipe.ListenAsync(name, (call, _) => Task.FromResult(new McpResult($"{call.Tool}:{JsonDocument.Parse(call.Arguments).RootElement.GetProperty("a").GetInt32()}", IsError: true)), stop.Token);

        var result = await McpPipe.CallAsync(name, new McpCall("servers", """{"a":5}"""), TestContext.Current.CancellationToken);

        Assert.Equal(new McpResult("servers:5", IsError: true), result);
        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task Without_an_app_the_call_says_so_instead_of_waiting()
    {
        var result = await McpPipe.CallAsync(NewName(), new McpCall("servers", "{}"), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Several_calls_run_at_once()
    {
        var name = NewName();
        using var stop = new CancellationTokenSource();
        using var two = new Barrier(2);
        var listening = McpPipe.ListenAsync(
            name,
            (call, cancellation) => Task.Run(
                () =>
                {
                    two.SignalAndWait(cancellation);
                    return new McpResult(call.Tool);
                },
                cancellation),
            stop.Token);

        var answers = await Task.WhenAll(
            McpPipe.CallAsync(name, new McpCall("a", "{}"), TestContext.Current.CancellationToken),
            McpPipe.CallAsync(name, new McpCall("b", "{}"), TestContext.Current.CancellationToken));

        Assert.Equal(["a", "b"], answers.Select(answer => answer!.Text).Order());
        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task Giving_up_on_a_call_cancels_it_in_the_app()
    {
        var name = NewName();
        using var stop = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var listening = McpPipe.ListenAsync(
            name,
            async (_, cancellation) =>
            {
                await using (cancellation.Register(cancelled.SetResult))
                {
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                }

                return new McpResult("never");
            },
            stop.Token);

        using var giveUp = new CancellationTokenSource();
        var call = McpPipe.CallAsync(name, new McpCall("x", "{}"), giveUp.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await giveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task Stopping_ends_the_listener_and_later_calls_find_nothing()
    {
        var name = NewName();
        using var stop = new CancellationTokenSource();
        var listening = McpPipe.ListenAsync(name, (_, _) => Task.FromResult(new McpResult("ok")), stop.Token);
        Assert.NotNull(await McpPipe.CallAsync(name, new McpCall("x", "{}"), TestContext.Current.CancellationToken));

        await stop.CancelAsync();
        await listening;

        Assert.Null(await McpPipe.CallAsync(name, new McpCall("x", "{}"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_broken_message_is_no_call()
    {
        Assert.Null(McpPipe.ParseCall("not json"u8));
        Assert.Null(McpPipe.ParseCall("[]"u8));
        Assert.Null(McpPipe.ParseCall("""{"arguments":{}}"""u8));
        Assert.Null(McpPipe.ParseCall("""{"tool":"x","arguments":3}"""u8));
        Assert.Equal(new McpCall("x", """{"a":1}"""), McpPipe.ParseCall("""{"tool":"x","arguments":{"a":1}}"""u8));
    }
}
