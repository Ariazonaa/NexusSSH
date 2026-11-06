using System.Text.Json;
using NexusSSH.Core.Mcp;

namespace NexusSSH.Core.Tests;

/// <summary>Das Protokoll des MCP-Servers: Was ein MCP-Programm schickt, ist fremde Eingabe.</summary>
public sealed class McpServerTests
{
    private static McpServer Server(Func<McpCall, CancellationToken, Task<McpResult>>? call = null)
        => new(call ?? ((_, _) => Task.FromResult(new McpResult("done"))));

    private static async Task<JsonElement> AskAsync(McpServer server, string line)
    {
        var answer = await server.HandleAsync(line, TestContext.Current.CancellationToken);
        Assert.NotNull(answer);
        Assert.DoesNotContain('\n', answer);
        return JsonDocument.Parse(answer).RootElement.Clone();
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", McpServer.LatestVersion)]
    public async Task Initialize_answers_with_a_version_both_sides_know(string asked, string expected)
    {
        var answer = await AskAsync(Server(), $$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{{asked}}}"}}""");

        var result = answer.GetProperty("result");
        Assert.Equal(1, answer.GetProperty("id").GetInt32());
        Assert.Equal(expected, result.GetProperty("protocolVersion").GetString());
        Assert.Equal("nexusssh", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Contains("not instructions", result.GetProperty("instructions").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_list_names_every_tool_with_its_schema()
    {
        var answer = await AskAsync(Server(), """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""");

        Assert.Equal("a", answer.GetProperty("id").GetString());
        var tools = answer.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(McpTools.All.Select(tool => tool.Name), tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.All(tools, tool => Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString()));
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString())));

        // Nur als lesend angekündigt, was nichts verändert.
        var writing = tools.Where(tool => !tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()).Select(tool => tool.GetProperty("name").GetString());
        Assert.Equal([McpTools.Connect, McpTools.RunCommand], writing);
    }

    [Fact]
    public async Task A_call_reaches_the_app_with_name_and_arguments_and_returns_its_text()
    {
        McpCall? seen = null;
        var server = Server((call, _) =>
        {
            seen = call;
            return Task.FromResult(new McpResult("{\"ok\":true}", IsError: true));
        });

        var answer = await AskAsync(server, """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"servers","arguments":{"a":"ü"}}}""");

        Assert.Equal("servers", seen?.Tool);
        Assert.Equal("ü", JsonDocument.Parse(seen!.Arguments).RootElement.GetProperty("a").GetString());
        var result = answer.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("{\"ok\":true}", content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_call_without_arguments_gets_an_empty_object()
    {
        McpCall? seen = null;
        var server = Server((call, _) =>
        {
            seen = call;
            return Task.FromResult(new McpResult(string.Empty));
        });

        await AskAsync(server, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"servers"}}""");

        Assert.Equal("{}", seen?.Arguments);
    }

    [Fact]
    public async Task A_notification_gets_no_answer()
    {
        var answer = await Server().HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", TestContext.Current.CancellationToken);

        Assert.Null(answer);
    }

    [Fact]
    public async Task Ping_answers_with_an_empty_result()
    {
        var answer = await AskAsync(Server(), """{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        Assert.Equal(JsonValueKind.Object, answer.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData("not json", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"nothing/here"}""", -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"x","arguments":[]}}""", -32602)]
    public async Task Nonsense_is_answered_with_an_error_and_reaches_nothing(string line, int code)
    {
        var called = false;
        var server = Server((_, _) =>
        {
            called = true;
            return Task.FromResult(new McpResult(string.Empty));
        });

        var answer = await AskAsync(server, line);

        Assert.Equal(code, answer.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(called);
    }

    [Fact]
    public async Task A_cancelled_request_cancels_its_call()
    {
        var waiting = new TaskCompletionSource<CancellationToken>();
        var server = Server(async (_, cancellation) =>
        {
            waiting.SetResult(cancellation);
            await Task.Delay(Timeout.Infinite, cancellation);
            return new McpResult("never");
        });

        var running = server.HandleAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"x"}}""", TestContext.Current.CancellationToken);
        var token = await waiting.Task;
        Assert.False(token.IsCancellationRequested);

        await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""", TestContext.Current.CancellationToken);

        Assert.True(token.IsCancellationRequested);
        var answer = JsonDocument.Parse((await running)!).RootElement;
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
    }
}
