using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Verbindungstest, Antwortzeit und Traceroute: was die App aus dem Bericht des Kerns macht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ProbeTests
{
    private static readonly ServerProfile Plain = new("p1", "web01", "web01.example.org", 22, "ubuntu", "Production", []);

    private static readonly ProbeReport Good = new(
        "methods",
        ["203.0.113.10", "2001:db8::10"],
        "203.0.113.10",
        ConnectMs: 23,
        Software: "SSH-2.0-OpenSSH_9.6",
        KeyAlgorithm: "ssh-ed25519",
        Fingerprint: "SHA256:abc",
        KeyState: ProbeReport.Unknown,
        Methods: ["publickey", "keyboard-interactive", "gssapi-with-mic"]);

    public ProbeTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_good_report_reads_step_by_step_and_ends_with_all_is_well()
    {
        Assert.Equal(
            [
                "✓ Name resolved: 203.0.113.10, 2001:db8::10",
                "✓ Port open at 203.0.113.10, answered after 23 ms",
                "✓ The service introduces itself as: SSH-2.0-OpenSSH_9.6",
                "• Host key not known yet – you will be asked on the first connection (ssh-ed25519, SHA256:abc)",
                "✓ Sign-in possible with: key, questions from the server (e.g. 2FA), gssapi-with-mic",
                "✓ Everything is fine up to the sign-in.",
            ],
            ProbeWording.Lines(Good));
    }

    [Fact]
    public void The_host_key_line_says_whether_the_key_is_known_or_another_one()
    {
        Assert.Contains("✓ Host key known (ssh-ed25519, SHA256:abc)", ProbeWording.Lines(Good with { KeyState = ProbeReport.Trusted }));
        Assert.Contains(ProbeWording.Lines(Good with { KeyState = ProbeReport.Changed }), line => line.StartsWith("✗ The host key differs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_that_stopped_shows_what_worked_and_then_why_it_stopped()
    {
        var refused = new ProbeReport("resolve", ["203.0.113.10"], Trouble: ConnectTrouble.Refused, Detail: "connection refused");

        var lines = ProbeWording.Lines(refused);

        Assert.Equal(2, lines.Count);
        Assert.Equal("✓ Name resolved: 203.0.113.10", lines[0]);
        Assert.StartsWith("✗ The computer answers, but no service listens on this port.", lines[1]);
        Assert.EndsWith("(connection refused)", lines[1]);
        Assert.False(refused.Succeeded);
    }

    [Fact]
    public void A_jump_host_and_a_service_that_is_not_ssh_have_their_own_sentences()
    {
        Assert.Equal(
            ["✗ The way leads through a jump host. That can only be checked by signing in there – that is, by connecting."],
            ProbeWording.Lines(new ProbeReport(string.Empty, [], Trouble: "jump", Detail: "…")));

        var mail = new ProbeReport("connect", ["192.0.2.1"], "192.0.2.1", 5, Software: "220 mail ESMTP", Trouble: "not_ssh", Detail: "…");
        var lines = ProbeWording.Lines(mail);
        Assert.Contains("• The service introduces itself as: 220 mail ESMTP", lines);
        Assert.Equal("✗ No SSH service answers on this port. Is the port right?", lines[^1]);
    }

    [Fact]
    public void Text_from_the_server_is_shown_without_what_one_cannot_see()
    {
        var odd = Good with { Software = "SSH-2.0-odd‮name", Methods = ["pass\u0007word"] };

        var text = ProbeWording.Text(odd);

        Assert.DoesNotContain('‮', text);
        Assert.DoesNotContain('\u0007', text);
    }

    [Fact]
    public async Task The_core_report_arrives_field_by_field()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);

        var pending = backend.ProbeAsync(Plain with { LegacyAlgorithms = true }, TestContext.Current.CancellationToken);
        var sent = Assert.Single(transport.Sent("probe"));
        Assert.Equal("web01.example.org", sent.GetProperty("profile").GetProperty("host").GetString());
        Assert.True(sent.GetProperty("profile").GetProperty("legacy_algorithms").GetBoolean());
        var request = sent.GetProperty("request").GetUInt64();

        transport.Raise($$"""{"event":"probe.result","request":{{request}},"step":"key","addresses":["203.0.113.10"],"address":"203.0.113.10","connect_ms":41,"software":"SSH-2.0-x","key_algorithm":"ssh-ed25519","fingerprint":"SHA256:abc","key_state":"changed","methods":[],"trouble":"timeout","message":"connection attempt timed out"}""");

        var report = await pending;
        Assert.Equal(("key", "203.0.113.10", 41, "SSH-2.0-x", "ssh-ed25519", "SHA256:abc", ProbeReport.Changed), (report.Step, report.Address, report.ConnectMs, report.Software, report.KeyAlgorithm, report.Fingerprint, report.KeyState));
        Assert.Equal((ConnectTrouble.Timeout, "connection attempt timed out"), (report.Trouble, report.Detail));
        Assert.Equal(["203.0.113.10"], report.Addresses);
    }

    [Fact]
    public async Task A_profile_the_core_refuses_is_an_error_not_a_report()
    {
        var transport = new FakeTransport();
        transport.Responses["probe"] = """{"ok":false,"error":"invalid_profile","message":"host"}""";
        using var backend = new Backend.CoreBackend(transport);

        var error = await Assert.ThrowsAsync<BackendException>(() => backend.ProbeAsync(Plain, TestContext.Current.CancellationToken));

        Assert.Equal(("invalid_profile", "host"), (error.Code, error.Message));
    }

    [Fact]
    public async Task The_dialog_gets_the_report_as_text_and_the_inspector_the_response_time()
    {
        var shell = TestShell.Create(out var backend);

        var text = await shell.ProbeAsync(shell.Profiles[0]);
        Assert.Contains("Port open at 203.0.113.10, answered after 12 ms", text);
        Assert.Single(backend.Probed);

        var session = shell.OpenSession(shell.Profiles[0]);
        Assert.Equal(string.Empty, session.LatencyText);
        await session.MeasureLatencyAsync();
        Assert.Equal("12 ms to 203.0.113.10", session.LatencyText);
        Assert.False(session.IsMeasuring);

        // Scheitert der Test, steht da, warum.
        backend.NextProbe = new ProbeReport(string.Empty, [], Trouble: "jump", Detail: "…");
        await session.MeasureLatencyAsync();
        Assert.StartsWith("✗ The way leads through a jump host.", session.LatencyText);
    }

    [Fact]
    public void A_route_is_traced_to_the_server_this_computer_really_connects_to()
    {
        var bastion = Plain with { Id = "b", Name = "bastion", Host = "bastion.example.org" };
        var inner = Plain with { Id = "i", Host = "10.0.0.5", JumpId = "b" };
        var lost = Plain with { Id = "l", JumpId = "gone" };
        var serial = Plain with { Id = "s", Host = "COM3", Protocol = ServerProfile.Serial };
        IReadOnlyList<ServerProfile> all = [bastion, inner, lost, serial];

        Assert.Equal("web01.example.org", RouteTrace.Target(Plain, all));
        Assert.Equal("bastion.example.org", RouteTrace.Target(inner, all));
        Assert.Null(RouteTrace.Target(lost, all));
        Assert.Null(RouteTrace.Target(serial, all));
    }

    [Theory]
    [InlineData("web01.example.org", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1%eth0", true)]
    [InlineData("bücher.example", true)]
    [InlineData("-h", false)]
    [InlineData("/?", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    [InlineData("a&b", false)]
    [InlineData("", false)]
    public void Only_a_plain_host_name_reaches_the_trace_program(string host, bool expected)
        => Assert.Equal(expected, RouteTrace.IsHostName(host));

    [Fact]
    public void The_host_is_one_argument_of_the_trace_program_and_never_a_command_line()
    {
        var (program, arguments) = RouteTrace.Command("web01.example.org");

        Assert.Contains(program, new[] { "tracert", "traceroute" });
        Assert.Equal("web01.example.org", arguments[^1]);
        Assert.Contains(RouteTrace.MaxHops.ToString(System.Globalization.CultureInfo.InvariantCulture), arguments);

        // Was kein Hostname ist, startet nichts und meldet das.
        string? failure = "unset";
        using var running = RouteTrace.Start("-h", _ => { }, reason => failure = reason);
        Assert.Equal("not a host name", failure);
    }
}
