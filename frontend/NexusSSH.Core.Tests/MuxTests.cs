using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Laufende Sitzungen von tmux und screen: finden, anbieten, anhängen – und nur Schlichtes tippen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class MuxTests
{
    private const string Profile = """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    [Theory]
    [InlineData("tmux", "work", "tmux attach-session -t =work", true)]
    [InlineData("screen", "1234.pts-0.web01", "screen -x 1234.pts-0.web01", true)]
    [InlineData("tmux", "work", "tmux attach-session -t =work; rm -rf ~", false)]
    [InlineData("tmux", "a b", "tmux attach-session -t =a b", false)]
    [InlineData("tmux", "$(id)", "tmux attach-session -t =$(id)", false)]
    [InlineData("tmux", "-x", "tmux attach-session -t =-x", false)]
    [InlineData("screen", "work", "tmux attach-session -t =work", false)]
    [InlineData("zellij", "work", "zellij attach work", false)]
    [InlineData("tmux", "", "tmux attach-session -t =", false)]
    public void Only_a_line_of_the_known_shape_with_a_plain_name_is_typed(string kind, string name, string command, bool safe)
        => Assert.Equal(safe, new MuxSession(kind, name, 1, false, command).IsSafe);

    [Fact]
    public void A_remembered_step_is_read_back_and_rubbish_in_the_settings_is_dropped()
    {
        var work = new MuxSession("tmux", "work", 2, false, "tmux attach-session -t =work");
        Assert.Equal(("tmux", "work"), MuxSteps.Target(MuxSteps.For(work)));
        Assert.Null(MuxSteps.Target(MuxSteps.Offer));
        Assert.Null(MuxSteps.Target("attach:zellij:x"));
        Assert.Null(MuxSteps.Target("attach:tmux:"));
        Assert.Null(MuxSteps.Target(null));

        var clean = MuxSteps.Sanitize(new() { ["p1"] = "offer", ["p2"] = "attach:screen:12.x", ["p3"] = "run:anything", [string.Empty] = "offer" });
        Assert.Equal(["p1", "p2"], clean.Keys.Order());
    }

    [Fact]
    public async Task On_request_the_running_sessions_are_offered_and_the_choice_is_typed_and_remembered()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.Profiles.GetProfiles()[0]);
        session.Start();
        (SessionViewModel Session, IReadOnlyList<MuxSession> Running)? offered = null;
        shell.MuxOffered += (where, running) => offered = (where, running);

        await shell.FindMuxAsync();

        Assert.NotNull(offered);
        Assert.Same(session, offered.Value.Session);
        Assert.Equal(["deploy", "logs", "4711.backup"], offered.Value.Running.Select(mux => mux.Name));
        Assert.Equal("deploy, tmux · 3 windows · detached", offered.Value.Running[0].ToString());

        shell.ChooseMux(session, offered.Value.Running[0], MuxStep.Attach);

        Assert.Equal("attach:tmux:deploy", shell.Settings.MuxSteps[session.Profile.Id]);
        Assert.Equal(MuxStep.Attach, shell.MuxStepOf(session.Profile));
        Assert.Contains(shell.Output, entry => entry.Text.Contains("attached to the tmux session “deploy”", StringComparison.Ordinal));

        // „Nicht anhängen“ und „nichts von selbst“ nimmt den gemerkten Schritt wieder weg.
        shell.ChooseMux(session, null, MuxStep.None);
        Assert.Empty(shell.Settings.MuxSteps);
    }

    [Fact]
    public async Task After_connecting_nothing_happens_by_itself_unless_a_step_is_remembered_for_the_server()
    {
        var shell = TestShell.Create(out var backend);
        var profile = backend.Profiles.GetProfiles()[0];
        var offers = 0;
        shell.MuxOffered += (_, _) => offers++;

        shell.OpenSession(profile).Start();
        await Task.Yield();
        Assert.Equal(0, offers);
        Assert.DoesNotContain(shell.Output, entry => entry.Text.Contains("attached to", StringComparison.Ordinal));

        // „Anbieten“: Die nächste Sitzung zu diesem Server fragt.
        shell.Settings.MuxSteps[profile.Id] = MuxSteps.Offer;
        shell.OpenSession(profile).Start();
        await Task.Yield();
        Assert.Equal(1, offers);

        // „Von selbst an diese“: ohne Frage – und an eine, die es nicht mehr gibt, wird nicht blind angehängt.
        shell.Settings.MuxSteps[profile.Id] = "attach:tmux:logs";
        shell.OpenSession(profile).Start();
        await Task.Yield();
        Assert.Equal(1, offers);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("attached to the tmux session “logs”", StringComparison.Ordinal));

        shell.Settings.MuxSteps[profile.Id] = "attach:tmux:gone";
        shell.OpenSession(profile).Start();
        await Task.Yield();
        Assert.Equal(2, offers);
    }

    [Fact]
    public async Task The_core_is_asked_with_mux_list_and_its_answer_is_read()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Profile}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");
        var monitor = Assert.IsAssignableFrom<IServerMonitor>(connection.Monitor);

        var listing = monitor.MuxSessionsAsync();
        var request = transport.Sent("mux.list").Single();
        Assert.Equal(1UL, request.GetProperty("session").GetUInt64());
        transport.Raise($$"""{"event":"sftp.result","request":{{request.GetProperty("request").GetUInt64()}},"ok":true,"mux":[{"kind":"tmux","name":"work","windows":3,"attached":true,"command":"tmux attach-session -t =work"},{"kind":"screen","name":"12.x","attached":false,"command":"screen -x 12.x"},{"kind":null,"name":"x","attached":false,"command":"x"}]}""");

        Assert.Equal(
            [new MuxSession("tmux", "work", 3, true, "tmux attach-session -t =work"), new MuxSession("screen", "12.x", null, false, "screen -x 12.x")],
            await listing);
    }
}
