using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public class CloseQuestionTests
{
    [Fact]
    public void Running_transfers_are_worth_a_question()
    {
        // Die Beispieldaten bringen laufende Transfers mit.
        var shell = TestShell.Create(out _);

        Assert.True(shell.ConfirmClose);
        Assert.Equal(shell.Transfers.ActiveCount > 1, shell.CloseQuestion()!.Contains($"{shell.Transfers.ActiveCount} transfers are running", StringComparison.Ordinal));
        Assert.DoesNotContain("session", shell.CloseQuestion(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_connected_session_is_worth_a_question_until_the_user_switches_it_off()
    {
        var shell = TestShell.Create(out var backend);
        shell.OpenSession(backend.Profiles.GetProfiles()[0]).Start();

        Assert.StartsWith("One session is connected and will be disconnected.", shell.CloseQuestion());

        shell.OpenSession(backend.Profiles.GetProfiles()[1]).Start();
        Assert.StartsWith("2 sessions are connected", shell.CloseQuestion());

        shell.ConfirmClose = false;
        Assert.Null(shell.CloseQuestion());
        Assert.False(shell.Settings.ConfirmClose);
    }
}
