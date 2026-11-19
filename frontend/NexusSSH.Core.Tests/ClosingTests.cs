using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Beim Schließen der App wird jede Verbindung beendet. Am 2026-10-31 brach das Aufräumen ab, weil ein Hörer der
/// ersten Sitzung (die Terminal-Seite, schon abgebaut) beim Beenden warf – die Verbindungen und ein mosh-server liefen
/// danach weiter.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ClosingTests
{
    private static readonly ServerProfile Web = new("p1", "web", "10.0.0.1", 22, "u", string.Empty, []);
    private static readonly ServerProfile Db = new("p2", "db", "10.0.0.2", 22, "u", string.Empty, []);

    private static (SessionViewModel Session, ScriptedConnection Connection) Connected(
        ShellViewModel shell, ScriptedBackend backend, ServerProfile profile, IServerMonitor? monitor = null)
    {
        var session = shell.OpenSession(profile);
        session.Start();
        backend.Last.Monitor = monitor;
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        return (session, backend.Last);
    }

    [Fact]
    public void Every_session_is_disconnected_even_if_one_fails_on_the_way()
    {
        var backend = new ScriptedBackend(Web, Db);
        var shell = TestShell.Create(backend);
        var (first, firstConnection) = Connected(shell, backend, Web);
        var (_, secondConnection) = Connected(shell, backend, Db);

        // Wie die abgebaute Terminal-Seite: ein Hörer, der beim Beenden wirft.
        var closed = new InvalidOperationException("page closed");
        first.Settled += () => throw closed;

        var failures = shell.EndSessions();

        Assert.Same(closed, Assert.Single(failures));
        Assert.True(firstConnection.Disposed);
        Assert.True(secondConnection.Disposed);
    }

    [Fact]
    public void Without_failures_nothing_is_reported_and_every_connection_ends()
    {
        var backend = new ScriptedBackend(Web, Db);
        var shell = TestShell.Create(backend);
        var (_, firstConnection) = Connected(shell, backend, Web);
        var (_, secondConnection) = Connected(shell, backend, Db);

        Assert.Empty(shell.EndSessions());
        Assert.True(firstConnection.Disposed);
        Assert.True(secondConnection.Disposed);

        // Ein zweites Mal (das Fenster meldet sein Ende nur einmal, aber sicher ist sicher) tut nichts.
        Assert.Empty(shell.EndSessions());
    }

    /// <summary>
    /// Beim Beenden meldet sich eine Sitzung noch einmal (<c>Settled</c>, für die, die auf sie warten). Darauf fragte die
    /// App den Server noch nach Befehlen, Namen zum Ergänzen und tmux – für eine Sitzung, die gerade ging (2026-11-18).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ending_a_session_starts_nothing_new_for_it(bool appEnds)
    {
        var backend = new ScriptedBackend(Web);
        var shell = TestShell.Create(backend);
        var monitor = new AskedMonitor();
        var (session, connection) = Connected(shell, backend, Web, monitor);
        Assert.Contains(nameof(IServerMonitor.CommandShellAsync), monitor.Asked);
        Assert.Contains(nameof(IServerMonitor.CommandNamesAsync), monitor.Asked);

        // Inzwischen soll die App tmux anbieten – erst beim nächsten Verbinden, nicht beim Gehen.
        shell.Settings.MuxSteps[Web.Id] = MuxSteps.Offer;
        var waited = false;
        session.Settled += () => waited = true;
        monitor.Asked.Clear();

        if (appEnds)
        {
            Assert.Empty(shell.EndSessions());
        }
        else
        {
            shell.CloseSession(session);
        }

        Assert.True(connection.Disposed);
        Assert.True(waited, "wer auf die Sitzung wartet, erfährt weiter, dass sie endet");
        Assert.Empty(monitor.Asked);
    }

    /// <summary>
    /// Wer schon tmux angeboten bekam, merkt sich die App – ein geschlossener Tab blieb darin für immer stehen
    /// (2026-11-18).
    /// </summary>
    [Fact]
    public void A_closed_tab_is_not_kept_for_tmux()
    {
        var backend = new ScriptedBackend(Web);
        var shell = TestShell.Create(backend);
        shell.Settings.MuxSteps[Web.Id] = MuxSteps.Offer;
        var monitor = new AskedMonitor();
        var (session, _) = Connected(shell, backend, Web, monitor);
        Assert.Contains(nameof(IServerMonitor.MuxSessionsAsync), monitor.Asked);
        Assert.True(shell.IsMuxDone(session));

        shell.CloseSession(session);

        Assert.False(shell.IsMuxDone(session));
    }

    /// <summary>
    /// Der aktive Tab misst, und gleich nach dem Verbinden misst er ohnehin einmal (Probe, um das System zu erkennen).
    /// Schloss er, nahm der Wechsel zum Nachbarn ihm das Hinsehen – und weil die Probe noch lief, schaltete das die
    /// Messung der gehenden Sitzung noch einmal ein (2026-11-18).
    /// </summary>
    [Fact]
    public void Closing_the_active_tab_does_not_measure_it_again()
    {
        var backend = new ScriptedBackend(Web, Db);
        var shell = TestShell.Create(backend);
        shell.MonitorBar = true;
        Connected(shell, backend, Web);
        var monitor = new AskedMonitor();
        var (session, connection) = Connected(shell, backend, Db, monitor);
        Assert.Same(session, shell.ActiveSession);
        Assert.Contains(true, monitor.Activations);
        monitor.Activations.Clear();

        shell.CloseSession(session);

        Assert.True(connection.Disposed);
        Assert.Equal([false], monitor.Activations);
    }

    // Merkt sich, wonach die App den Server fragt und ob sie messen lässt.
    private sealed class AskedMonitor : IServerMonitor
    {
        public List<string> Asked { get; } = [];

        public List<bool> Activations { get; } = [];

        public ServerStats? Current => null;

        public bool IsUnavailable => false;

#pragma warning disable CS0067 // In diesem Test meldet die Überwachung nichts.
        public event Action? Changed;
#pragma warning restore CS0067

        public void SetActive(bool active) => Activations.Add(active);

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public Task<IReadOnlyList<MuxSession>> MuxSessionsAsync()
        {
            Asked.Add(nameof(MuxSessionsAsync));
            return Task.FromResult<IReadOnlyList<MuxSession>>([]);
        }

        public Task<IReadOnlyList<string>> CommandNamesAsync()
        {
            Asked.Add(nameof(CommandNamesAsync));
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<CommandShell> CommandShellAsync()
        {
            Asked.Add(nameof(CommandShellAsync));
            return Task.FromResult(CommandShell.Posix);
        }
    }
}
