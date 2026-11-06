using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Reiter „Probleme“ (Spec §7): Warnungen und Fehler aus dem Protokoll.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ProblemsTests
{
    private const string Profile = """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    private static (ShellViewModel Shell, FakeTransport Transport) Create()
    {
        TestShell.Create(out _);
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Profile}}]}""";
        var shell = new ShellViewModel(new CoreBackend(transport), new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
        return (shell, transport);
    }

    [Fact]
    public void Ordinary_log_lines_are_no_problems()
    {
        var (shell, _) = Create();

        shell.Log("hello");

        Assert.Single(shell.Output);
        Assert.Empty(shell.Problems);
        Assert.Equal("Problems", shell.ProblemsTitle);
    }

    [Fact]
    public void Warnings_of_the_core_are_problems_and_counted_in_the_tab()
    {
        var (shell, transport) = Create();

        transport.Raise("""{"event":"core.warning","code":"key_unreadable","message":"C:\\keys\\id: invalid format"}""");

        var problem = Assert.Single(shell.Problems);
        Assert.Equal(LogSeverity.Warning, problem.Severity);
        Assert.False(problem.IsError);
        Assert.Equal(@"The key file could not be read. (C:\keys\id: invalid format)", problem.Text);
        Assert.Equal("Problems (1)", shell.ProblemsTitle);
        Assert.Contains(problem, shell.Output);
    }

    [Fact]
    public void A_failed_connection_is_a_problem_but_exit_is_not()
    {
        var (shell, transport) = Create();
        var first = shell.OpenSession(shell.Profiles[0]);
        first.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"auth_failed","message":"no sign-in method was accepted"}""");

        var problem = Assert.Single(shell.Problems);
        Assert.True(problem.IsError);
        Assert.Equal("web01: Sign-in failed. (no sign-in method was accepted)", problem.Text);

        var second = shell.OpenSession(shell.Profiles[0]);
        second.Start();
        transport.Raise("""{"event":"session.state","session":2,"state":"closed","reason":"remote","exit_status":0}""");

        Assert.Single(shell.Problems);
        Assert.Contains(shell.Output, entry => entry.Text == "web01: Session ended.");
    }

    [Fact]
    public void Closing_a_tab_is_not_reported_by_the_tab_that_is_gone()
    {
        var (shell, _) = Create();
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();

        shell.CloseSession(session);

        Assert.Empty(shell.Problems);
    }

    [Fact]
    public void A_failed_transfer_is_a_problem_once()
    {
        var (shell, transport) = Create();
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/home/ubuntu"}""");
        session.Files.Upload([LocalPath.Of("data", "backup.tar.gz")]);
        var transfer = Assert.Single(transport.Sent("transfer.start")).GetProperty("transfer").GetUInt64();

        var failed = $$"""{"event":"transfer.state","transfer":{{transfer}},"state":"failed","bytes":10,"total":100,"message":"permission_denied: Permission denied"}""";
        transport.Raise(failed);
        transport.Raise(failed);

        var problem = Assert.Single(shell.Problems);
        Assert.Equal("Transfer backup.tar.gz (web01) failed: Permission denied.", problem.Text);
    }

    [Fact]
    public void A_refused_profile_is_a_problem_and_clearing_empties_only_the_problems()
    {
        var shell = TestShell.Create(out _);

        Assert.Null(shell.SaveProfile(new ServerProfile("", "broken", " ", 22, "root", "", [])));
        Assert.Equal("Problems (1)", shell.ProblemsTitle);

        var lines = shell.Output.Count;
        shell.ClearProblems();

        Assert.Empty(shell.Problems);
        Assert.Equal("Problems", shell.ProblemsTitle);
        Assert.Equal(lines, shell.Output.Count);
    }
}
