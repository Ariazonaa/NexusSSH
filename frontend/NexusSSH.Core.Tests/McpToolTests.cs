using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mcp;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Die Werkzeuge des MCP-Servers. Text vom Server erreicht die KI und kann sie zu etwas verleiten – deshalb sitzen die
/// Sperren hier und nicht beim MCP-Programm.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class McpToolTests
{
    public McpToolTests() => L.Init(AppLanguage.English);

    private sealed record Setup(ShellViewModel Shell, FakePrompts Prompts, SessionViewModel Session, MockServerMonitor Monitor);

    // web01 ist freigegeben und verbunden, die übrigen Server des Mock nicht.
    private static Setup Released()
    {
        var prompts = new FakePrompts();
        var shell = TestShell.Create(out _, prompts: prompts);
        shell.McpEnabled = true;
        var profile = shell.Profiles.Single(candidate => candidate.Name == "web01");
        shell.SetMcpServer(profile.Id, true);
        var session = shell.OpenSession(profile);
        session.Start();
        return new Setup(shell, prompts, session, Assert.IsType<MockServerMonitor>(session.Connection.Monitor));
    }

    private static Task<McpResult> CallAsync(ShellViewModel shell, string tool, string arguments = "{}")
        => shell.CallMcpAsync(new McpCall(tool, arguments), TestContext.Current.CancellationToken);

    private static JsonElement Json(McpResult result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonDocument.Parse(result.Text).RootElement.Clone();
    }

    [Theory]
    [InlineData(McpTools.Servers)]
    [InlineData(McpTools.AppLog)]
    [InlineData(McpTools.RunCommand)]
    public async Task Switched_off_every_tool_refuses(string tool)
    {
        var setup = Released();
        setup.Shell.McpEnabled = false;

        var result = await CallAsync(setup.Shell, tool, """{"server":"web01","command":"id"}""");

        Assert.True(result.IsError);
        Assert.Contains("switched off", result.Text, StringComparison.Ordinal);
        Assert.Empty(setup.Prompts.Commands);
    }

    [Fact]
    public async Task Off_is_the_default_and_nothing_is_released()
    {
        var shell = TestShell.Create(out _);

        Assert.False(shell.McpEnabled);
        Assert.Empty(shell.Settings.McpServers);
        Assert.True((await CallAsync(shell, McpTools.Servers)).IsError);
    }

    [Fact]
    public async Task The_list_names_only_released_servers_and_no_address()
    {
        var setup = Released();

        var result = await CallAsync(setup.Shell, McpTools.Servers);

        var server = Assert.Single(Json(result).GetProperty("servers").EnumerateArray());
        Assert.Equal("web01", server.GetProperty("name").GetString());
        Assert.Equal("connected", server.GetProperty("state").GetString());
        Assert.DoesNotContain("10.0.1", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("database01", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_is_not_released_looks_like_one_that_does_not_exist()
    {
        var setup = Released();
        var other = setup.Shell.OpenSession(setup.Shell.Profiles.Single(candidate => candidate.Name == "database01"));
        other.Start();

        var refused = await CallAsync(setup.Shell, McpTools.ListFiles, """{"server":"database01","path":"/"}""");
        var unknown = await CallAsync(setup.Shell, McpTools.ListFiles, """{"server":"nothing","path":"/"}""");

        Assert.True(refused.IsError);
        Assert.Equal(unknown.Text.Replace("nothing", "database01", StringComparison.Ordinal), refused.Text);
    }

    [Fact]
    public async Task Local_terminals_stay_out_of_reach_even_if_released()
    {
        var setup = Released();
        var local = new ServerProfile("pc", "pc", "pwsh", 0, string.Empty, string.Empty, [], Protocol: ServerProfile.Local);
        setup.Shell.SaveProfile(local);
        setup.Shell.SetMcpServer(setup.Shell.Profiles.Single(candidate => candidate.Name == "pc").Id, true);

        var result = await CallAsync(setup.Shell, McpTools.Servers);

        Assert.DoesNotContain("\"pc\"", result.Text, StringComparison.Ordinal);
        Assert.True((await CallAsync(setup.Shell, McpTools.Connect, """{"server":"pc"}""")).IsError);
    }

    [Fact]
    public async Task A_release_ends_when_the_profile_points_elsewhere()
    {
        var setup = Released();
        var profile = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");
        Assert.True(setup.Shell.IsMcpServer(profile));

        setup.Shell.SaveProfile(profile with { Host = "10.9.9.9" });

        var moved = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");
        Assert.False(setup.Shell.IsMcpServer(moved));
        Assert.Empty(Json(await CallAsync(setup.Shell, McpTools.Servers)).GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task Connect_opens_a_tab_without_taking_the_active_one()
    {
        var setup = Released();
        var staging = setup.Shell.Profiles.Single(candidate => candidate.Name == "staging");
        setup.Shell.SetMcpServer(staging.Id, true);
        var count = setup.Shell.Sessions.Count;

        var result = await CallAsync(setup.Shell, McpTools.Connect, """{"server":"staging"}""");

        Assert.Equal("connected", Json(result).GetProperty("state").GetString());
        Assert.Equal(count + 1, setup.Shell.Sessions.Count);
        Assert.Same(setup.Session, setup.Shell.ActiveSession);
    }

    [Fact]
    public async Task A_connection_that_fails_is_an_error_with_its_reason()
    {
        var backend = new ScriptedBackend(new ServerProfile("p1", "web", "10.0.0.1", 22, "u", string.Empty, []));
        var shell = TestShell.Create(backend);
        shell.McpEnabled = true;
        shell.SetMcpServer("p1", true);

        var call = shell.CallMcpAsync(new McpCall(McpTools.Connect, """{"server":"web"}"""), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed));
        var result = await call;

        Assert.True(result.IsError);
        Assert.Contains(L.Get("ClosedAuthFailed"), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_uses_a_connection_that_stands()
    {
        var setup = Released();
        var count = setup.Shell.Sessions.Count;

        Json(await CallAsync(setup.Shell, McpTools.Connect, """{"server":"web01"}"""));

        Assert.Equal(count, setup.Shell.Sessions.Count);
    }

    [Fact]
    public async Task A_tool_on_a_server_without_a_connection_says_what_to_do()
    {
        var setup = Released();
        setup.Shell.SetMcpServer("staging", true);

        var result = await CallAsync(setup.Shell, McpTools.ListFiles, """{"server":"staging","path":"/"}""");

        Assert.True(result.IsError);
        Assert.Contains(McpTools.Connect, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_are_listed_and_read_as_plain_text()
    {
        var setup = Released();
        var files = setup.Session.Connection.Files!;
        await files.CreateDirectoryAsync("/mcp");
        await files.WriteAsync("/mcp/a.txt", "one\n\u001b[31mred\u001b[0m\ntwo‮"u8.ToArray());

        var listed = Json(await CallAsync(setup.Shell, McpTools.ListFiles, """{"server":"web01","path":"/mcp"}"""));
        var read = Json(await CallAsync(setup.Shell, McpTools.ReadFile, """{"server":"web01","path":"/mcp/a.txt"}"""));

        Assert.Equal("a.txt", Assert.Single(listed.GetProperty("entries").EnumerateArray()).GetProperty("name").GetString());
        var text = read.GetProperty("text").GetString();
        Assert.StartsWith("one\nred\ntwo", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text!);
        Assert.DoesNotContain('‮', text!);
    }

    [Fact]
    public async Task A_part_of_a_file_is_read_within_the_limit()
    {
        var setup = Released();
        await setup.Session.Connection.Files!.WriteAsync("/big.txt", Enumerable.Repeat((byte)'x', McpTools.MaxReadLength + 500).ToArray());

        var read = Json(await CallAsync(setup.Shell, McpTools.ReadFile, """{"server":"web01","path":"/big.txt","offset":100,"length":100000000}"""));

        Assert.Equal(McpTools.MaxReadLength + 500, read.GetProperty("size").GetInt64());
        Assert.Equal(McpTools.MaxReadLength, read.GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task Answers_stay_small_enough_for_an_ai_program()
    {
        var setup = Released();
        var files = setup.Session.Connection.Files!;
        await files.CreateDirectoryAsync("/many");
        foreach (var number in Enumerable.Range(0, 600))
        {
            await files.WriteAsync($"/many/file-{number}.txt", "x"u8.ToArray());
        }

        var listed = Json(await CallAsync(setup.Shell, McpTools.ListFiles, """{"server":"web01","path":"/many"}"""));

        Assert.True(listed.GetProperty("cut").GetBoolean());
        Assert.True(listed.GetProperty("entries").GetArrayLength() <= 500);
        Assert.True(McpTools.MaxReadLength <= 64 * 1024);
    }

    [Fact]
    public async Task Binary_content_is_named_not_shown()
    {
        var setup = Released();
        await setup.Session.Connection.Files!.WriteAsync("/data.bin", new byte[] { 1, 0, 2 });

        var read = Json(await CallAsync(setup.Shell, McpTools.ReadFile, """{"server":"web01","path":"/data.bin"}"""));

        Assert.True(read.GetProperty("binary").GetBoolean());
        Assert.False(read.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task A_command_runs_only_after_the_user_saw_it_in_full()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Monitor.NextRun = new CommandRun(3, "line\n\u001b[1mbold\u001b[0m", "failed\n", Cut: true, TimedOut: false);
        var body = new string('x', 5000);
        var command = "cd /tmp &&\n" + body;
        var arguments = $$"""{"server":"web01","command":"cd /tmp &&\n{{body}}","timeout_seconds":9}""";

        var result = Json(await CallAsync(setup.Shell, McpTools.RunCommand, arguments));

        Assert.Equal([("web01", command)], setup.Prompts.Commands);
        Assert.Equal([(command, 9)], setup.Monitor.Commands);
        Assert.Equal(3, result.GetProperty("status").GetInt32());
        Assert.Equal("line\nbold", result.GetProperty("stdout").GetString());
        Assert.Equal("failed\n", result.GetProperty("stderr").GetString());
        Assert.True(result.GetProperty("cut").GetBoolean());
        Assert.False(result.GetProperty("timed_out").GetBoolean());
    }

    // Eine Sitzung mit echter Shell: Der Test spielt, was der Server antwortet.
    private sealed record Typed(ShellViewModel Shell, FakePrompts Prompts, ScriptedBackend Backend, MockServerMonitor Monitor, SessionViewModel Session);

    private static Typed WithShell(string loginShell = "/bin/bash", string? charset = null)
    {
        var prompts = new FakePrompts { AnswerCommand = true };
        var backend = new ScriptedBackend(new ServerProfile("p1", "web", "10.0.0.1", 22, "u", string.Empty, [], Charset: charset));
        var shell = TestShell.Create(backend, prompts);
        shell.McpEnabled = true;
        shell.SetMcpServer("p1", true);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        var monitor = new MockServerMonitor(new ServerStats(true, 1, 2, 3, "Debian", "6", TimeSpan.Zero), 1) { LoginShell = loginShell };
        backend.Last.Monitor = monitor;
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        return new Typed(shell, prompts, backend, monitor, session);
    }

    // Die Shell zeigt, was getippt wurde: Erst dann drückt die App Enter.
    private static async Task EchoAsync(Typed setup)
    {
        setup.Session.EchoQuiet = TimeSpan.Zero;
        var typed = setup.Backend.Last.Input[0];
        setup.Backend.Last.Emit(typed[(typed.IndexOf('\u0015', StringComparison.Ordinal) + 1)..]);
        Assert.Equal("\r", await EventuallyAsync(() => Task.FromResult(setup.Backend.Last.Input[^1]), last => last == "\r"));
    }

    private static string NumberIn(string typed)
        => System.Text.RegularExpressions.Regex.Match(typed, @"nexus;([0-9A-F]+);").Groups[1].Value;

    [Fact]
    public async Task A_command_is_typed_into_the_terminal_and_its_output_read_back()
    {
        var setup = WithShell();

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"df -h"}"""), TestContext.Current.CancellationToken);

        Assert.False(call.IsCompleted);
        var typed = Assert.Single(setup.Backend.Last.Input);
        Assert.StartsWith("\u001b[F\u0005\u0015df -h; printf", typed, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', typed);
        Assert.Equal([("web", "df -h")], setup.Prompts.Commands);
        Assert.True(setup.Prompts.InTerminal);

        await EchoAsync(setup);
        setup.Backend.Last.Emit($"\r\n/dev/sda1  50G\r\n\u001b]7777;nexus;{NumberIn(typed)};0\u0007$ ");
        var result = Json(await call);

        Assert.Equal(0, result.GetProperty("status").GetInt32());
        Assert.Equal("/dev/sda1  50G\n", result.GetProperty("stdout").GetString());
        Assert.True(result.GetProperty("terminal").GetBoolean());
        Assert.Empty(setup.Monitor.Commands);
    }

    [Fact]
    public async Task A_line_the_shell_does_not_show_as_approved_is_never_entered()
    {
        var setup = WithShell();
        setup.Session.TypingLimit = TimeSpan.FromMilliseconds(30);

        // Eine Shell im vi-Befehlsmodus, ein Passwortfeld: Es kommt etwas anderes zurück als das Getippte.
        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);
        setup.Backend.Last.Emit("[sudo] password for u: ");
        var result = await call;

        Assert.True(result.IsError);
        Assert.Contains("nothing ran", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Backend.Last.Input, input => input.Contains('\r', StringComparison.Ordinal));
        Assert.Empty(setup.Monitor.Commands);
        Assert.Null(setup.Session.OutputTap);
    }

    [Fact]
    public async Task In_zsh_no_escape_is_typed()
    {
        var setup = WithShell("/usr/bin/zsh");

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);
        var typed = Assert.Single(setup.Backend.Last.Input);
        await EchoAsync(setup);
        setup.Backend.Last.Emit($"\r\n\u001b]7777;nexus;{NumberIn(typed)};0\u0007");
        await call;

        Assert.DoesNotContain('\u001b', typed);
    }

    [Fact]
    public async Task What_the_terminal_shows_stays_as_it_is()
    {
        var setup = WithShell();
        var seen = new List<byte>();
        setup.Session.Output += data => seen.AddRange(data.ToArray());

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);
        await EchoAsync(setup);
        seen.Clear();
        var output = $"\r\na.txt\r\n\u001b]7777;nexus;{NumberIn(setup.Backend.Last.Input[0])};0\u0007$ ";
        setup.Backend.Last.Emit(output);
        await call;

        Assert.Equal(output, System.Text.Encoding.UTF8.GetString([.. seen]));
    }

    [Fact]
    public async Task Nothing_is_typed_before_the_user_agreed()
    {
        var setup = WithShell();
        setup.Prompts.AnswerCommand = false;

        var result = await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"rm -rf /"}"""), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(setup.Backend.Last.Input);
    }

    [Fact]
    public async Task A_program_on_the_full_screen_gets_nothing_typed_into_it()
    {
        var setup = WithShell();
        setup.Backend.Last.Emit("\u001b[?1049h");

        var result = await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("full-screen", result.Text, StringComparison.Ordinal);
        Assert.Empty(setup.Prompts.Commands);
        Assert.Empty(setup.Backend.Last.Input);
    }

    [Fact]
    public async Task A_program_that_opens_the_full_screen_during_the_question_gets_nothing_typed_either()
    {
        var setup = WithShell();
        setup.Prompts.Asking = () => setup.Backend.Last.Emit("\u001b[?1049h");

        var result = await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(setup.Backend.Last.Input);
    }

    [Fact]
    public async Task Past_its_time_a_typed_command_returns_what_came_and_lets_go_of_the_terminal()
    {
        var setup = WithShell();

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"tail -f log","timeout_seconds":1}"""), TestContext.Current.CancellationToken);
        await EchoAsync(setup);
        setup.Backend.Last.Emit("\r\nfirst line\r\n");
        var result = Json(await call);

        Assert.True(result.GetProperty("timed_out").GetBoolean());
        Assert.Equal("first line\n", result.GetProperty("stdout").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("status").ValueKind);
        Assert.Null(setup.Session.OutputTap);
    }

    [Fact]
    public async Task A_connection_that_ends_ends_the_command()
    {
        var setup = WithShell();

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"reboot"}"""), TestContext.Current.CancellationToken);
        setup.Backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        var result = await call;

        Assert.True(result.IsError);
        Assert.Null(setup.Session.OutputTap);
    }

    [Fact]
    public async Task Only_one_command_runs_in_a_terminal_at_a_time()
    {
        var setup = WithShell();
        var cancellation = TestContext.Current.CancellationToken;

        var first = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"one"}"""), cancellation);
        var second = await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"two"}"""), cancellation);

        Assert.True(second.IsError);
        Assert.Contains("busy", second.Text, StringComparison.Ordinal);
        Assert.Single(setup.Backend.Last.Input);
        await EchoAsync(setup);
        setup.Backend.Last.Emit($"\r\n\u001b]7777;nexus;{NumberIn(setup.Backend.Last.Input[0])};0\u0007");
        await first;
    }

    [Theory]
    [InlineData("/usr/bin/fish", """{"server":"web","command":"ls"}""")]
    [InlineData("/bin/bash", """{"server":"web","command":"a\tb"}""")]
    public async Task What_cannot_be_typed_runs_in_its_own_channel(string loginShell, string arguments)
    {
        var setup = WithShell(loginShell);

        var result = Json(await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, arguments), TestContext.Current.CancellationToken));

        Assert.False(setup.Prompts.InTerminal);
        Assert.False(result.GetProperty("terminal").GetBoolean());
        Assert.Single(setup.Monitor.Commands);
        Assert.Empty(setup.Backend.Last.Input);
    }

    [Fact]
    public async Task Another_character_set_runs_in_its_own_channel()
    {
        var setup = WithShell(charset: Terminals.TerminalCharset.Latin1);

        await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);

        Assert.Single(setup.Monitor.Commands);
        Assert.Empty(setup.Backend.Last.Input);
    }

    [Fact]
    public async Task If_the_shell_cannot_be_asked_the_terminal_is_used()
    {
        var setup = WithShell();
        setup.Monitor.LoginShellFailure = new RemoteFileException("connect failed", "no_channel");

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web","command":"ls"}"""), TestContext.Current.CancellationToken);
        Assert.Single(setup.Backend.Last.Input);
        await EchoAsync(setup);
        setup.Backend.Last.Emit($"\r\n\u001b]7777;nexus;{NumberIn(setup.Backend.Last.Input[0])};0\u0007");

        Assert.False((await call).IsError);
    }

    [Fact]
    public async Task A_declined_command_never_reaches_the_server()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = false;

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"rm -rf /"}""");

        Assert.True(result.IsError);
        Assert.Contains("declined", result.Text, StringComparison.Ordinal);
        Assert.Empty(setup.Monitor.Commands);
    }

    [Fact]
    public async Task Without_an_answer_a_command_counts_as_declined()
    {
        var setup = Released();
        setup.Shell.McpPromptLimit = TimeSpan.FromMilliseconds(20);
        setup.Prompts.NeverAnswer = true;

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.True(result.IsError);
        Assert.Empty(setup.Monitor.Commands);
        Assert.Equal(1, setup.Prompts.Cancelled);
    }

    [Fact]
    public async Task A_program_that_gives_up_closes_the_question_and_runs_nothing()
    {
        var setup = Released();
        setup.Prompts.NeverAnswer = true;
        using var giveUp = new CancellationTokenSource();

        var call = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web01","command":"id"}"""), giveUp.Token);
        await giveUp.CancelAsync();

        Assert.True((await call).IsError);
        Assert.Empty(setup.Monitor.Commands);
    }

    [Fact]
    public async Task Only_one_question_is_open_at_a_time()
    {
        var setup = Released();
        setup.Prompts.NeverAnswer = true;
        using var giveUp = new CancellationTokenSource();
        var first = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, """{"server":"web01","command":"one"}"""), giveUp.Token);

        var second = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"two"}""");

        Assert.True(second.IsError);
        Assert.Contains("busy", second.Text, StringComparison.Ordinal);
        Assert.Equal([("web01", "one")], setup.Prompts.Commands);
        await giveUp.CancelAsync();
        await first;
    }

    [Fact]
    public async Task Switched_off_during_the_question_the_command_stays_home()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.Asking = () => setup.Shell.McpEnabled = false;

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.True(result.IsError);
        Assert.Empty(setup.Monitor.Commands);
    }

    [Theory]
    [InlineData("""{"server":"web01"}""")]
    [InlineData("""{"server":"web01","command":""}""")]
    [InlineData("""{"server":"web01","command":"   "}""")]
    [InlineData("""{"server":"web01","command":5}""")]
    [InlineData("""{"server":"web01","command":"a\u0000b"}""")]
    [InlineData("""{"server":"web01","command":"id","timeout_seconds":0}""")]
    [InlineData("""{"server":"web01","command":"id","timeout_seconds":301}""")]
    [InlineData("""{"server":"web01","command":"id","timeout_seconds":"5"}""")]
    public async Task A_broken_command_is_refused_before_anyone_is_asked(string arguments)
    {
        var setup = Released();

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, arguments);

        Assert.True(result.IsError);
        Assert.Empty(setup.Prompts.Commands);
    }

    [Fact]
    public async Task No_channel_names_max_sessions()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Monitor.RunFailure = new RemoteFileException("connect failed", "no_channel");

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.True(result.IsError);
        Assert.Contains("MaxSessions", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_call_is_written_to_the_output_without_what_was_read()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        await setup.Session.Connection.Files!.WriteAsync("/secret.txt", "topsecret"u8.ToArray());

        await CallAsync(setup.Shell, McpTools.ReadFile, """{"server":"web01","path":"/secret.txt"}""");
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"uptime"}""");

        var text = string.Join('\n', setup.Shell.Output.Select(entry => entry.Text));
        Assert.Contains(McpTools.ReadFile, text, StringComparison.Ordinal);
        Assert.Contains("uptime", text, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_app_log_hides_servers_that_are_not_released()
    {
        var setup = Released();
        setup.Shell.Log("database01 failed at 10.0.1.20");
        setup.Shell.Log("web01 is fine", LogSeverity.Warning);

        var result = await CallAsync(setup.Shell, McpTools.AppLog, """{"limit":50}""");

        Assert.DoesNotContain("database01", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.1.20", result.Text, StringComparison.Ordinal);
        Assert.Contains("web01 is fine", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_nobody_knows_is_refused()
    {
        var setup = Released();

        Assert.True((await CallAsync(setup.Shell, "delete_everything")).IsError);
        Assert.True((await CallAsync(setup.Shell, McpTools.Servers, "not json")).IsError);
    }

    [Fact]
    public async Task Server_logs_go_through_the_same_reader_as_the_page()
    {
        var setup = Released();

        var result = Json(await CallAsync(setup.Shell, McpTools.ServerLogs, """{"server":"web01","since":"1h","priority":3,"lines":10}"""));

        var line = Assert.Single(result.GetProperty("lines").EnumerateArray());
        Assert.Contains("base/16384", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_marker_shows_on_the_tab_while_a_call_runs()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        var during = false;
        setup.Monitor.Running = () => during = setup.Session.IsMcpActive;

        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.True(during);
        Assert.False(setup.Session.IsMcpActive);
    }

    [Fact]
    public async Task The_pipe_is_open_only_while_the_setting_is_on()
    {
        var pipe = "NexusSSH-test-" + Guid.NewGuid().ToString("N");
        var shell = TestShell.Create(out _);
        var call = new McpCall(McpTools.Servers, "{}");
        var cancellation = TestContext.Current.CancellationToken;

        shell.WatchMcp(pipe);
        Assert.Null(await McpPipe.CallAsync(pipe, call, cancellation));

        shell.McpEnabled = true;
        var answer = await EventuallyAsync(() => McpPipe.CallAsync(pipe, call, cancellation), result => result is not null);
        Assert.False(answer!.IsError, answer.Text);

        shell.McpEnabled = false;
        Assert.Null(await EventuallyAsync(() => McpPipe.CallAsync(pipe, call, cancellation), result => result is null));

        // Gleich wieder an: Das Lauschen von vorhin steht dem neuen nicht im Weg.
        shell.McpEnabled = true;
        Assert.NotNull(await EventuallyAsync(() => McpPipe.CallAsync(pipe, call, cancellation), result => result is not null));
        shell.StopMcp();
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> ask, Func<T, bool> wanted)
    {
        var answer = await ask();
        for (var attempt = 0; attempt < 100 && !wanted(answer); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            answer = await ask();
        }

        return answer;
    }

    [Fact]
    public async Task An_always_allowed_command_runs_without_a_question_in_its_own_channel()
    {
        var setup = WithShell();
        setup.Prompts.AlwaysAllow = true;
        var cancellation = TestContext.Current.CancellationToken;
        const string Arguments = """{"server":"web","command":"df -h"}""";

        // Beim ersten Mal so, wie der Dialog es sagte: getippt ins Terminal.
        var first = setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, Arguments), cancellation);
        await EchoAsync(setup);
        setup.Backend.Last.Emit($"\r\n\u001b]7777;nexus;{NumberIn(setup.Backend.Last.Input[0])};0\u0007");
        var asked = Json(await first);
        Assert.Equal("asked", asked.GetProperty("approved").GetString());
        Assert.True(asked.GetProperty("terminal").GetBoolean());
        var typed = setup.Backend.Last.Input.Count;

        // Danach ohne Frage und im eigenen Kanal: Getippt würde es eine Zeile leeren, die der Benutzer gerade schreibt.
        var again = Json(await setup.Shell.CallMcpAsync(new McpCall(McpTools.RunCommand, Arguments), cancellation));

        Assert.Single(setup.Prompts.Commands);
        Assert.Equal("always", again.GetProperty("approved").GetString());
        Assert.False(again.GetProperty("terminal").GetBoolean());
        Assert.Equal([("df -h", McpTools.DefaultCommandSeconds)], setup.Monitor.Commands);
        Assert.Equal(typed, setup.Backend.Last.Input.Count);
        Assert.Contains(setup.Shell.Output, entry => entry.Text.Contains("always allowed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("id; rm -rf ~")]
    [InlineData("id ")]
    [InlineData(" id")]
    [InlineData("ID")]
    [InlineData("id\n")]
    [InlineData("i")]
    public async Task Only_exactly_the_allowed_text_runs_without_a_question(string other)
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.AlwaysAllow = true;
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");
        setup.Prompts.AnswerCommand = false;

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, JsonSerializer.Serialize(new Dictionary<string, string> { ["server"] = "web01", ["command"] = other }));

        Assert.True(result.IsError);
        Assert.Equal(["id", other], setup.Prompts.Commands.Select(asked => asked.Command));
        Assert.Equal(["id"], setup.Monitor.Commands.Select(run => run.Item1));
        Assert.False((await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""")).IsError);
        Assert.Equal(2, setup.Prompts.Commands.Count);
    }

    [Fact]
    public async Task Run_once_allows_nothing_for_later()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;

        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.Equal(2, setup.Prompts.Commands.Count);
        Assert.Empty(setup.Shell.Settings.McpAllowedCommands);
    }

    [Fact]
    public async Task An_allowed_command_belongs_to_its_server_and_its_target()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.AlwaysAllow = true;
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");
        var web = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");
        var other = setup.Shell.Profiles.First(candidate => candidate.Name != "web01" && !candidate.IsLocal);
        setup.Shell.SetMcpServer(other.Id, true);

        Assert.True(setup.Shell.IsMcpCommandAllowed(web, "id"));
        Assert.False(setup.Shell.IsMcpCommandAllowed(other, "id"));

        setup.Shell.SaveProfile(web with { Host = "10.9.9.9" });
        var moved = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");
        setup.Shell.SetMcpServer(moved.Id, true);
        Assert.False(setup.Shell.IsMcpCommandAllowed(moved, "id"));
    }

    [Fact]
    public async Task Ending_the_release_forgets_the_allowed_commands()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.AlwaysAllow = true;
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");
        var web = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");
        Assert.Single(setup.Shell.McpAllowedCommandList);

        setup.Shell.SetMcpServer(web.Id, false);
        setup.Shell.SetMcpServer(web.Id, true);

        Assert.False(setup.Shell.IsMcpCommandAllowed(web, "id"));
        Assert.Empty(setup.Shell.McpAllowedCommandList);
    }

    [Fact]
    public async Task Allowed_commands_can_be_taken_back()
    {
        var setup = Released();
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.AlwaysAllow = true;
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");
        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"uptime"}""");
        var web = setup.Shell.Profiles.Single(candidate => candidate.Name == "web01");

        var listed = setup.Shell.McpAllowedCommandList;
        Assert.Equal(["id", "uptime"], listed.Select(pair => pair.Entry.Command));
        setup.Shell.ForgetMcpCommand(listed[0].Entry);
        Assert.False(setup.Shell.IsMcpCommandAllowed(web, "id"));
        Assert.True(setup.Shell.IsMcpCommandAllowed(web, "uptime"));

        setup.Shell.ForgetMcpCommands();
        Assert.False(setup.Shell.IsMcpCommandAllowed(web, "uptime"));
    }

    [Fact]
    public async Task A_question_that_runs_out_allows_nothing()
    {
        var setup = Released();
        setup.Shell.McpPromptLimit = TimeSpan.FromMilliseconds(20);
        setup.Prompts.NeverAnswer = true;
        setup.Prompts.AnswerCommand = true;
        setup.Prompts.AlwaysAllow = true;

        var result = await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.True(result.IsError);
        Assert.Empty(setup.Monitor.Commands);
        Assert.Empty(setup.Shell.Settings.McpAllowedCommands);
    }

    [Fact]
    public async Task The_question_knows_until_when_it_stands()
    {
        var setup = Released();
        setup.Shell.McpPromptSeconds = 30;
        var before = DateTimeOffset.UtcNow;

        await CallAsync(setup.Shell, McpTools.RunCommand, """{"server":"web01","command":"id"}""");

        Assert.InRange(setup.Prompts.Until, before.AddSeconds(29), DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.Equal(30, setup.Shell.Settings.McpPromptSeconds);
    }

    [Fact]
    public void Odd_values_in_the_settings_file_are_put_right()
    {
        var settings = new AppSettings
        {
            McpPromptSeconds = 7,
            McpAllowedCommands =
            [
                new("t", "id"), new("t", "id"), new(string.Empty, "id"), new("t", string.Empty), new("t", "a\0b"),
                new("t", new string('x', McpTools.MaxCommandLength + 1)), new(null!, "id"), new("t", null!),
            ],
        }.Sanitize();

        Assert.Equal(AppSettings.DefaultMcpPromptSeconds, settings.McpPromptSeconds);
        Assert.Equal([new McpAllowedCommand("t", "id")], settings.McpAllowedCommands);
    }

    [Fact]
    public void The_release_stays_on_this_computer()
    {
        var setup = Released();

        var file = Exchange.DataExchange.Create([.. setup.Shell.Profiles], setup.Shell.Settings, "1.0", DateTimeOffset.UnixEpoch, null);

        Assert.DoesNotContain(typeof(Exchange.ExportedSettings).GetProperties(), property => property.Name.Contains("Mcp", StringComparison.Ordinal));
        Assert.NotNull(file.Settings);
    }
}
