using NexusSSH.Core.Backend;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Das ausführliche Protokoll einer Verbindung: nur auf Wunsch, und dann unter „Ausgabe“.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ConnectionTraceTests
{
    private const string Profile = """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    private static (ShellViewModel Shell, SessionViewModel Session, FakeTransport Transport) Open()
    {
        Localization.L.Init(Localization.AppLanguage.English);
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Profile}}]}""";
        var shell = new ShellViewModel(new CoreBackend(transport), new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        return (shell, session, transport);
    }

    [Fact]
    public void A_connection_is_not_traced_unless_the_user_asks()
    {
        var (shell, _, transport) = Open();

        Assert.False(transport.Sent("session.open").Single().TryGetProperty("trace", out _));

        // Eine Zeile, um die niemand gebeten hat, erscheint auch nicht.
        transport.Raise("""{"event":"session.trace","session":1,"text":"key exchange curve25519"}""");
        Assert.DoesNotContain(shell.Output, entry => entry.Text.Contains("curve25519", StringComparison.Ordinal));
    }

    [Fact]
    public void Reconnecting_with_the_detailed_log_asks_the_core_and_shows_its_lines_in_the_output()
    {
        var (shell, session, transport) = Open();

        shell.RunCommand("session.reconnectVerbose");

        Assert.True(session.IsVerbose);
        var second = transport.Sent("session.open").Last();
        Assert.True(second.GetProperty("trace").GetBoolean());
        Assert.False(second.TryGetProperty("share", out _));
        var number = second.GetProperty("session").GetUInt64();

        transport.Raise($$"""{"event":"session.trace","session":{{number}},"text":"10.0.1.10: server is SSH-2.0-Open\u001b[31mSSH; key exchange curve25519"}""");

        var line = Assert.Single(shell.Output, entry => entry.Text.Contains("curve25519", StringComparison.Ordinal));
        Assert.StartsWith("web01: 10.0.1.10: server is SSH-2.0-Open", line.Text);
        Assert.DoesNotContain('\u001b', line.Text);
        Assert.True(shell.IsBottomPanelVisible);
    }
}
