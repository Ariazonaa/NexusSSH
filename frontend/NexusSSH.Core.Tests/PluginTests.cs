using System.Text;
using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Plugins;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Plugins (Stufe 1): Beschreibung, Antwort, Ablage, Rechte, Befehle in der Palette, Abschalten bei Versagen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class PluginTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nexus-plugins-{Guid.NewGuid():N}");

    public PluginTests() => L.Init(AppLanguage.English);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Manifest(string permissions = """["notify","files"]""", string id = "example.disks")
        => $$"""{"format":1,"id":"{{id}}","name":"Disk\u202e check","version":"1.0.0","author":"Me","permissions":{{permissions}},"commands":[{"id":"check","title":"Check disks"},{"id":"Bad Id","title":"x"},{"id":"empty","title":""}]}""";

    [Fact]
    public void The_description_is_read_and_checked()
    {
        var manifest = PluginManifest.Parse(Manifest(), out var problem)!;

        Assert.Null(problem);
        Assert.Equal(("example.disks", "Disk check", "1.0.0", "Me"), (manifest.Id, manifest.Name, manifest.Version, manifest.Author));
        Assert.Equal([PluginPermission.Notify, PluginPermission.Files], manifest.Permissions);
        Assert.Equal(new PluginCommand("check", "Check disks"), Assert.Single(manifest.Commands));
    }

    [Theory]
    [InlineData("""{"format":2,"id":"a.b","name":"x","version":"1","commands":[{"id":"c","title":"C"}]}""")]
    [InlineData("""{"format":1,"id":"../x","name":"x","version":"1","commands":[{"id":"c","title":"C"}]}""")]
    [InlineData("""{"format":1,"id":"a.b","name":"x","version":"1","permissions":["network"],"commands":[{"id":"c","title":"C"}]}""")]
    [InlineData("""{"format":1,"id":"a.b","name":"x","version":"1","commands":[]}""")]
    [InlineData("""[1,2]""")]
    [InlineData("")]
    public void A_description_that_does_not_fit_is_refused_with_a_reason(string json)
    {
        Assert.Null(PluginManifest.Parse(json, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void The_answer_is_cleaned_and_limited()
    {
        var rows = string.Join(',', Enumerable.Range(0, 600).Select(row => $"""["{row}","x\u001b[31m","extra"]"""));
        var output = PluginOutput.Parse($$$"""
            {"view":{"title":"Disks\u202e","text":"line 1\nline\u0007 2","table":{"columns":["Mount","Use"],"rows":[{{{rows}}}]}},
             "notify":"Done\u001b","log":"ran","storage":{"runs":2},"extra":1}
            """)!;

        var view = output.View!;
        Assert.Equal(("Disks", "line 1\nline 2"), (view.Title, view.Text));
        Assert.Equal(["Mount", "Use"], view.Columns);
        Assert.Equal(500, view.Rows.Count);
        Assert.Equal(["0", "x[31m"], view.Rows[0]);
        Assert.Equal("12 /a 2048 /b", PluginText.Clean("12\t/a\n2048\t/b\n", 100));
        Assert.Equal(("Done", "ran"), (output.Notify, output.Log));
        Assert.Equal("""{"runs":2}""", output.Storage);

        Assert.Null(PluginOutput.Parse("[]"));
        Assert.Null(PluginOutput.Parse("not json"));
        Assert.Null(PluginOutput.Parse("""{"storage":[1]}""")!.Storage);
    }

    [Fact]
    public void The_store_keeps_file_checksum_rights_switch_and_data_and_ignores_foreign_keys()
    {
        var source = Path.Combine(Path.GetTempPath(), $"plugin-{Guid.NewGuid():N}.nxplugin");
        File.WriteAllBytes(source, [0, 97, 115, 109]);
        try
        {
            var store = new PluginStore(_directory);
            var path = store.Install("example.disks", source, Sha, [PluginPermission.Notify]);
            store.SetDisabled("example.disks", true);
            store.WriteData("example.disks", """{"runs":1}""");
            File.WriteAllText(Path.Combine(_directory, "state.json"), File.ReadAllText(Path.Combine(_directory, "state.json")).Replace("{", $$"""{"..\\evil":{"Sha256":"{{Sha}}"},""", StringComparison.Ordinal));

            var fresh = new PluginStore(_directory);
            var entry = Assert.Single(fresh.Load());
            Assert.Equal(("example.disks", path, Sha, true), (entry.Id, entry.Path, entry.Sha256, entry.Disabled));
            Assert.Equal([PluginPermission.Notify], entry.Granted);
            Assert.Equal("""{"runs":1}""", fresh.ReadData("example.disks"));

            fresh.Remove("example.disks");
            Assert.Empty(new PluginStore(_directory).Load());
            Assert.Null(fresh.ReadData("example.disks"));
        }
        finally
        {
            File.Delete(source);
        }
    }

    private (ShellViewModel Shell, FakeRuntime Runtime) Shell(FakePrompts? prompts = null, string? manifest = null)
    {
        var shell = TestShell.Create(out var backend, prompts: prompts);
        var runtime = new FakeRuntime(manifest ?? Manifest("""["notify","commands","files"]"""));
        backend.Plugins = runtime;
        shell.UsePlugins(new PluginStore(_directory));
        shell.PluginWatchPause = TimeSpan.Zero;
        return (shell, runtime);
    }

    // Stufe 3: ein Plugin ohne Befehle, das auf „verbunden“ hört und zwei Muster beobachtet.
    private const string Watcher = """
        {"format":1,"id":"example.watch","name":"Watcher","version":"1.0","permissions":["notify","watch","events"],
         "events":["connected"],
         "watch":[{"id":"oom","pattern":"out of memory","ignoreCase":true},{"id":"err","pattern":"^E\\d{3}\\b","regex":true}]}
        """;

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public void Events_and_patterns_are_read_from_the_description_and_need_their_permission()
    {
        var manifest = PluginManifest.Parse(Watcher, out var problem)!;

        Assert.Null(problem);
        Assert.Empty(manifest.Commands);
        Assert.Equal([PluginEvent.Connected], manifest.Events);
        Assert.Equal(["oom", "err"], manifest.Watch.Select(pattern => pattern.Id));
        Assert.True(manifest.Watch[0].Matches("kernel: Out Of Memory: killed"));
        Assert.True(manifest.Watch[1].Matches("E404 not found"));
        Assert.False(manifest.Watch[1].Matches("xE404"));
        Assert.Equal("\"out of memory\", /^E\\d{3}\\b/", ShellViewModel.PatternTexts(manifest.Watch));
    }

    [Theory]
    [InlineData("""["watch"]""", """ "events":["connected"] """)]
    [InlineData("""["events"]""", """ "events":["reboot"] """)]
    [InlineData("""["events"]""", """ "watch":[{"id":"a","pattern":"x"}] """)]
    [InlineData("""["watch"]""", """ "watch":[{"id":"a","pattern":".*","regex":true}] """)]
    [InlineData("""["watch"]""", """ "watch":[{"id":"a","pattern":"(a)\\1","regex":true}] """)]
    [InlineData("""["watch"]""", """ "watch":[{"id":"a","pattern":"x\u001b"}] """)]
    [InlineData("""["watch"]""", """ "watch":[{"id":"a","pattern":"x"},{"id":"a","pattern":"y"}] """)]
    [InlineData("""["watch"]""", """ "watch":[{"id":"A b","pattern":"x"}] """)]
    public void Events_and_patterns_that_do_not_fit_are_refused(string permissions, string rest)
    {
        Assert.Null(PluginManifest.Parse($$"""{"format":1,"id":"a.b","name":"x","version":"1","permissions":{{permissions}},{{rest}}}""", out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Output_becomes_lines_of_plain_text_even_when_cut_anywhere()
    {
        var lines = new List<string>();
        var output = new OutputLines();
        output.Append("\u001b[31mfir"u8, lines.Add);
        output.Append("st\u001b[0m\r\n\r\n  sec"u8, lines.Add);
        output.Append("ond\r\n"u8, lines.Add);
        output.Append(Encoding.UTF8.GetBytes(new string('x', 5000) + "\n"), lines.Add);
        output.Append("open"u8, lines.Add);

        Assert.Equal(["first", "second", new string('x', OutputLines.MaxLine)], lines);
    }

    [Fact]
    public async Task A_plugin_hears_a_connect_only_with_the_events_permission()
    {
        var (shell, runtime) = Shell(manifest: Watcher);
        var plugin = Install(shell, PluginPermission.Events);

        var session = shell.OpenSession(shell.Profiles.Single(profile => profile.Name == "web01"));
        session.Start();

        await WaitFor(() => runtime.Inputs.Count == 1);
        using (var input = JsonDocument.Parse(runtime.Inputs[0]))
        {
            Assert.Equal(("event", "connected", "web01"), (input.RootElement.GetProperty("kind").GetString(), input.RootElement.GetProperty("event").GetString(), input.RootElement.GetProperty("session").GetProperty("server").GetString()));
        }

        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Events, granted: false);
        shell.OpenSession(shell.Profiles.Single(profile => profile.Name == "web01")).Start();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(runtime.Inputs);
        Assert.Null(session.LineWatcher);
        _ = plugin;
    }

    [Fact]
    public async Task Watched_lines_reach_the_plugin_only_when_they_match_and_its_answer_may_not_open_dialogs_or_run_anything()
    {
        var prompts = new FakePrompts { AnswerCommand = true };
        var (shell, runtime) = Shell(prompts, Watcher);
        Install(shell, PluginPermission.Watch, PluginPermission.Notify);
        var session = shell.OpenSession(shell.Profiles.Single(profile => profile.Name == "web01"));
        session.Start();
        var views = 0;
        shell.PluginViewRequested += (_, _) => views++;
        runtime.Answer = """{"view":{"text":"x"},"run":{"command":"reboot"},"log":"saw it"}""";
        shell.PluginWatchPause = TimeSpan.FromMilliseconds(300);

        var watcher = Assert.IsType<Action<string>>(session.LineWatcher);
        watcher("all good");
        watcher("kernel: Out of memory: Killed process 1");
        watcher("E500 internal\u001b[31m");

        await WaitFor(() => runtime.Inputs.Count == 1);
        using var input = JsonDocument.Parse(runtime.Inputs[0]);
        Assert.Equal("watch", input.RootElement.GetProperty("kind").GetString());
        var lines = input.RootElement.GetProperty("lines").EnumerateArray().Select(line => (line.GetProperty("pattern").GetString(), line.GetProperty("server").GetString(), line.GetProperty("text").GetString())).ToList();
        Assert.Equal([("oom", "web01", "kernel: Out of memory: Killed process 1"), ("err", "web01", "E500 internal␛[31m")], lines);
        Assert.Equal(0, views);
        Assert.Empty(prompts.Commands);
        Assert.Contains(shell.Output, entry => entry.Text == "Watcher: saw it");

        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Watch, granted: false);
        Assert.Null(session.LineWatcher);
    }

    [Fact]
    public async Task Lines_beyond_the_basket_are_only_counted()
    {
        var (shell, runtime) = Shell(manifest: Watcher);
        Install(shell, PluginPermission.Watch);
        var session = shell.OpenSession(shell.Profiles.Single(profile => profile.Name == "web01"));
        session.Start();
        shell.PluginWatchPause = TimeSpan.FromMilliseconds(300);

        var watcher = session.LineWatcher!;
        for (var i = 0; i < ShellViewModel.MaxPluginLines + 7; i++)
        {
            watcher($"out of memory {i}");
        }

        await WaitFor(() => runtime.Inputs.Count == 1);
        using var input = JsonDocument.Parse(runtime.Inputs[0]);
        Assert.Equal(ShellViewModel.MaxPluginLines, input.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal(7, input.RootElement.GetProperty("dropped").GetInt32());
    }

    // Stufe 2: ein Plugin mit einer verbundenen Sitzung (web01), die aktiv ist.
    private (ShellViewModel Shell, FakeRuntime Runtime, FakePrompts Prompts, SessionViewModel Session, MockServerMonitor Monitor) Connected()
    {
        var prompts = new FakePrompts { AnswerCommand = true };
        var (shell, runtime) = Shell(prompts);
        var session = shell.OpenSession(shell.Profiles.Single(profile => profile.Name == "web01"));
        session.Start();
        shell.ActiveSession = session;
        return (shell, runtime, prompts, session, Assert.IsType<MockServerMonitor>(session.Connection.Monitor));
    }

    private static JsonElement Result(FakeRuntime runtime, int call)
    {
        using var input = JsonDocument.Parse(runtime.Inputs[call]);
        return input.RootElement.Clone();
    }

    [Fact]
    public async Task A_command_on_the_server_runs_only_after_asking_and_its_output_goes_back_with_the_state()
    {
        var (shell, runtime, prompts, _, monitor) = Connected();
        var plugin = Install(shell, PluginPermission.Commands);
        runtime.Answers.Enqueue("""{"run":{"command":"df -h","timeout_seconds":9},"state":{"step":1}}""");
        runtime.Answers.Enqueue("""{"view":{"text":"done"}}""");
        var views = new List<PluginView>();
        shell.PluginViewRequested += (_, view) => views.Add(view);

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        Assert.Equal([("web01", "df -h")], prompts.Commands);
        Assert.Equal("Disk check", prompts.Plugin);
        Assert.Equal([("df -h", 9)], monitor.Commands);
        var first = Result(runtime, 0);
        Assert.Equal(("command", "web01"), (first.GetProperty("kind").GetString(), first.GetProperty("session").GetProperty("server").GetString()));
        var second = Result(runtime, 1);
        Assert.Equal("result", second.GetProperty("kind").GetString());
        Assert.Equal(1, second.GetProperty("state").GetProperty("step").GetInt32());
        var result = second.GetProperty("result");
        Assert.Equal(("df -h", 0, "ok\n"), (result.GetProperty("command").GetString(), result.GetProperty("status").GetInt32(), result.GetProperty("stdout").GetString()));
        Assert.Equal("done", Assert.Single(views).Text);
    }

    [Fact]
    public async Task A_declined_command_or_one_without_the_right_does_not_run_and_the_plugin_learns_why()
    {
        var (shell, runtime, prompts, _, monitor) = Connected();
        var plugin = Install(shell, PluginPermission.Commands);
        prompts.AnswerCommand = false;
        runtime.Answers.Enqueue("""{"run":{"command":"reboot"}}""");

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        Assert.Empty(monitor.Commands);
        Assert.Equal("declined", Result(runtime, 1).GetProperty("result").GetProperty("error").GetString());

        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Commands, granted: false);
        prompts.Commands.Clear();
        runtime.Answers.Enqueue("""{"run":{"command":"reboot"}}""");
        await shell.RunPluginCommandAsync(shell.Plugins[0], plugin.Manifest.Commands[0]);

        Assert.Empty(prompts.Commands);
        Assert.Empty(monitor.Commands);
        var last = Result(runtime, runtime.Inputs.Count - 1);
        Assert.Equal("permission", last.GetProperty("result").GetProperty("error").GetString());
        Assert.False(last.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task A_file_of_the_session_is_read_with_the_right_and_without_it_the_plugin_gets_nothing()
    {
        var (shell, runtime, prompts, session, _) = Connected();
        await session.Connection.Files!.CreateDirectoryAsync("/p");
        await session.Connection.Files.WriteAsync("/p/a.txt", "one\ntwo"u8.ToArray());
        var plugin = Install(shell, PluginPermission.Files);
        runtime.Answers.Enqueue("""{"read":{"path":"/p/a.txt"}}""");

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        var read = Result(runtime, 1).GetProperty("result");
        Assert.Equal(("/p/a.txt", "one\ntwo", 7L), (read.GetProperty("path").GetString(), read.GetProperty("text").GetString(), read.GetProperty("size").GetInt64()));
        Assert.Empty(prompts.Commands);

        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Files, granted: false);
        runtime.Answers.Enqueue("""{"read":{"path":"/p/a.txt"}}""");
        await shell.RunPluginCommandAsync(shell.Plugins[0], plugin.Manifest.Commands[0]);

        var refused = Result(runtime, runtime.Inputs.Count - 1).GetProperty("result");
        Assert.Equal("permission", refused.GetProperty("error").GetString());
        Assert.False(refused.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task A_plugin_that_keeps_asking_is_stopped_after_the_last_step()
    {
        var (shell, runtime, _, _, _) = Connected();
        var plugin = Install(shell);
        runtime.Answer = """{"read":{"path":"/etc/hostname"}}""";

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        Assert.Equal(ShellViewModel.MaxPluginSteps, runtime.Inputs.Count);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("steps", StringComparison.Ordinal));
        Assert.False(shell.Plugins[0].IsDisabled);
    }

    [Theory]
    [InlineData("""{"run":{"command":"ls"},"read":{"path":"/"}}""")]
    [InlineData("""{"run":{"command":"ls\u001b[2J"}}""")]
    [InlineData("""{"run":{"command":"ls ‮"}}""")]
    [InlineData("""{"run":{"command":"  "}}""")]
    [InlineData("""{"run":"ls"}""")]
    [InlineData("""{"read":{"path":"/a\nb"}}""")]
    [InlineData("""{"read":{"path":""}}""")]
    public void A_request_that_does_not_fit_makes_the_answer_unusable(string json)
        => Assert.Null(PluginOutput.Parse(json));

    [Fact]
    public void A_request_is_read_with_its_limits()
    {
        var run = PluginOutput.Parse("""{"run":{"command":"ls -la\n\tpwd","timeout_seconds":100000},"state":[1,"x"]}""")!;
        Assert.Equal(new PluginRequest("ls -la\n\tpwd", PluginRequest.MaxSeconds, null, 0), run.Request);
        Assert.Equal("""[1,"x"]""", run.State);

        var read = PluginOutput.Parse("""{"read":{"path":"/var/log/syslog","offset":-5}}""")!;
        Assert.Equal(new PluginRequest(null, 0, "/var/log/syslog", 0), read.Request);

        Assert.Null(PluginOutput.Parse($$"""{"state":"{{new string('x', PluginOutput.MaxState)}}"}"""));
    }

    private static string SourceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plugin-{Guid.NewGuid():N}.nxplugin");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static InstalledPlugin Install(ShellViewModel shell, params string[] granted)
    {
        var source = SourceFile();
        var found = shell.InspectPlugin(source, out _)!.Value;
        Assert.True(shell.InstallPlugin(source, found.Manifest, found.Inspection, granted));
        File.Delete(source);
        return Assert.Single(shell.Plugins);
    }

    [Fact]
    public void An_installed_plugin_gets_only_the_rights_it_asked_for_and_puts_its_command_into_the_palette()
    {
        var (shell, _) = Shell();

        var plugin = Install(shell, PluginPermission.Notify, PluginPermission.Watch);

        Assert.Equal([PluginPermission.Notify], plugin.Granted);
        Assert.Contains(shell.Commands, command => command.Id == "plugin.example.disks.check" && command.Title == "Disk check: Check disks");

        shell.SetPluginPermission(plugin, PluginPermission.Events, granted: true);
        Assert.Equal([PluginPermission.Notify], shell.Plugins[0].Granted);
        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Files, granted: true);
        Assert.Equal([PluginPermission.Notify, PluginPermission.Files], shell.Plugins[0].Granted);

        shell.SetPluginEnabled(shell.Plugins[0], enabled: false);
        Assert.DoesNotContain(shell.Commands, command => command.Id.StartsWith("plugin.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_shows_the_view_keeps_the_data_and_notifies_only_with_the_right()
    {
        var (shell, runtime) = Shell();
        var plugin = Install(shell);
        var views = new List<(string Plugin, PluginView View)>();
        shell.PluginViewRequested += (name, view) => views.Add((name, view));
        var notices = new List<string>();
        shell.AttentionRequested += (_, text) => notices.Add(text);
        runtime.Answer = """{"view":{"title":"Result","text":"all fine"},"notify":"done","log":"logged","storage":{"runs":1}}""";

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        var (name, view) = Assert.Single(views);
        Assert.Equal(("Disk check", "Result", "all fine"), (name, view.Title, view.Text));
        Assert.Empty(notices);
        Assert.DoesNotContain(shell.Output, entry => entry.Text.Contains("logged", StringComparison.Ordinal));
        using (var input = JsonDocument.Parse(runtime.Inputs[^1]))
        {
            Assert.Equal("check", input.RootElement.GetProperty("command").GetString());
            Assert.Equal(JsonValueKind.Object, input.RootElement.GetProperty("storage").ValueKind);
            Assert.Equal(0, input.RootElement.GetProperty("permissions").GetArrayLength());
        }

        // Mit dem Recht: Hinweis und Eintrag; die Daten vom letzten Mal kommen mit.
        shell.SetPluginPermission(shell.Plugins[0], PluginPermission.Notify, granted: true);
        await shell.RunPluginCommandAsync(shell.Plugins[0], plugin.Manifest.Commands[0]);
        Assert.Contains(shell.Output, entry => entry.Text == "Disk check: logged");
        using var second = JsonDocument.Parse(runtime.Inputs[^1]);
        Assert.Equal(1, second.RootElement.GetProperty("storage").GetProperty("runs").GetInt32());
    }

    [Theory]
    [InlineData("plugin_fuel")]
    [InlineData("plugin_trap")]
    [InlineData("plugin_changed")]
    [InlineData("bad answer")]
    public async Task A_plugin_that_fails_is_switched_off(string failure)
    {
        var (shell, runtime) = Shell();
        var plugin = Install(shell);
        if (failure == "bad answer")
        {
            runtime.Answer = "[]";
        }
        else
        {
            runtime.Failure = failure;
        }

        await shell.RunPluginCommandAsync(plugin, plugin.Manifest.Commands[0]);

        Assert.True(shell.Plugins[0].IsDisabled);
        Assert.Contains(shell.Problems, entry => entry.Text.Contains("Disk check", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plugin_file_changed_behind_the_apps_back_is_not_loaded()
    {
        var (shell, runtime) = Shell();
        Install(shell);

        runtime.Sha256 = new string('f', 64);
        shell.UsePlugins(new PluginStore(_directory));

        Assert.Empty(shell.Plugins);
    }

    [Fact]
    public async Task The_core_is_asked_with_path_checksum_and_input_and_its_failures_arrive_with_their_code()
    {
        var transport = new FakeTransport();
        transport.Responses["plugin.inspect"] = $$"""{"ok":true,"manifest":"{\"format\":1}","sha256":"{{Sha}}"}""";
        using var backend = new Backend.CoreBackend(transport);
        var runtime = backend.Plugins;

        var inspection = runtime.Inspect(@"C:\p\a.nxplugin");
        Assert.Equal(("""{"format":1}""", Sha), (inspection.Manifest, inspection.Sha256));
        Assert.Equal(@"C:\p\a.nxplugin", Assert.Single(transport.Sent("plugin.inspect")).GetProperty("path").GetString());

        var calling = runtime.CallAsync(@"C:\p\a.nxplugin", Sha, """{"kind":"command"}""", TestContext.Current.CancellationToken);
        var call = Assert.Single(transport.Sent("plugin.call"));
        Assert.Equal((Sha, """{"kind":"command"}"""), (call.GetProperty("sha256").GetString(), call.GetProperty("input").GetString()));
        transport.Raise($$$"""{"event":"sftp.result","request":{{{call.GetProperty("request").GetUInt64()}}},"ok":true,"output":"{\"view\":{}}"}""");
        Assert.Equal("""{"view":{}}""", await calling);

        var failing = runtime.CallAsync(@"C:\p\a.nxplugin", Sha, "{}", TestContext.Current.CancellationToken);
        var second = transport.Sent("plugin.call").Last();
        transport.Raise($$"""{"event":"sftp.result","request":{{second.GetProperty("request").GetUInt64()}},"ok":false,"error":"plugin_fuel","message":"too long"}""");
        var failure = await Assert.ThrowsAsync<PluginException>(() => failing);
        Assert.Equal("plugin_fuel", failure.Code);
        Assert.True(failure.IsMisbehaving);

        transport.Responses["plugin.inspect"] = """{"ok":false,"error":"plugin_invalid","message":"imports"}""";
        Assert.Equal("plugin_invalid", Assert.Throws<PluginException>(() => runtime.Inspect("x")).Code);
    }

    private sealed class FakeRuntime(string manifest) : IPluginRuntime
    {
        public string Sha256 { get; set; } = Sha;

        public string Answer { get; set; } = "{}";

        /// <summary>Antworten der Reihe nach; sind sie aufgebraucht, gilt <see cref="Answer"/>.</summary>
        public Queue<string> Answers { get; } = new();

        public string? Failure { get; set; }

        public List<string> Inputs { get; } = [];

        public PluginInspection Inspect(string path) => new(manifest, Sha256);

        public Task<string> CallAsync(string path, string sha256, string input, CancellationToken cancellation = default)
        {
            Inputs.Add(input);
            return Failure is { } code ? Task.FromException<string>(new PluginException(code, "failed")) : Task.FromResult(Answers.TryDequeue(out var next) ? next : Answer);
        }
    }
}
