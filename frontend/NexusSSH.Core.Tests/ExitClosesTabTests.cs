using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>„exit“ in der Shell schließt den Tab – aber nur, wenn darin getippt wurde und die Shell von selbst endete.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ExitClosesTabTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);

    private readonly ScriptedBackend _backend = new(Profile);

    private ShellViewModel Shell()
    {
        L.Init(AppLanguage.English);
        return new ShellViewModel(_backend, new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
    }

    private (SessionViewModel Session, ScriptedConnection Connection) Open(ShellViewModel shell)
    {
        var session = shell.OpenSession(Profile);
        session.Start();
        var connection = _backend.Last;
        connection.Set(new ConnectionStatus(ConnectionState.Connected));
        return (session, connection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void Typing_exit_closes_the_tab(int? code)
    {
        var shell = Shell();
        var (session, connection) = Open(shell);

        session.Write(Encoding.UTF8.GetBytes("exit\r"));
        connection.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote, ExitStatus: code));

        Assert.DoesNotContain(session, shell.Sessions);
    }

    [Fact]
    public void A_shell_that_ends_without_input_stays_to_be_read()
    {
        var shell = Shell();
        var (session, connection) = Open(shell);

        connection.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote));

        Assert.Contains(session, shell.Sessions);
    }

    [Theory]
    [InlineData(CloseReason.ConnectionLost)]
    [InlineData(CloseReason.User)]
    public void Other_ends_keep_the_tab(CloseReason reason)
    {
        var shell = Shell();
        var (session, connection) = Open(shell);

        session.Write(Encoding.UTF8.GetBytes("ls\r"));
        connection.Set(new ConnectionStatus(ConnectionState.Closed, reason, "gone"));

        Assert.Contains(session, shell.Sessions);
    }

    [Fact]
    public void Input_before_a_reconnect_does_not_count()
    {
        var shell = Shell();
        var (session, connection) = Open(shell);
        session.Write(Encoding.UTF8.GetBytes("ls\r"));
        connection.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost, "gone"));

        session.Reconnect();
        var again = _backend.Last;
        again.Set(new ConnectionStatus(ConnectionState.Connected));
        again.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote));

        Assert.Contains(session, shell.Sessions);
    }
}
