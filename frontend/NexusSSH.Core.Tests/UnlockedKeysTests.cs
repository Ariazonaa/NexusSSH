using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

/// <summary>Entsperrte Schlüssel bleiben im Speicher des Kerns – die App sagt nur, ob und wann nicht mehr.</summary>
[Collection(LanguageCollection.Name)]
public sealed class UnlockedKeysTests
{
    public UnlockedKeysTests() => L.Init(AppLanguage.English);

    [Fact]
    public void The_core_is_only_told_when_it_should_not_remember()
    {
        var shell = TestShell.Create(out var backend);
        Assert.True(shell.RememberUnlockedKeys);
        Assert.True(backend.RemembersUnlockedKeys);

        var quiet = TestShell.Create(out var other, new AppSettings { RememberUnlockedKeys = false });
        Assert.False(quiet.RememberUnlockedKeys);
        Assert.False(other.RemembersUnlockedKeys);
    }

    [Fact]
    public void Switching_it_off_and_on_reaches_the_core_and_the_settings()
    {
        var shell = TestShell.Create(out var backend);

        shell.RememberUnlockedKeys = false;
        Assert.False(backend.RemembersUnlockedKeys);
        Assert.False(shell.Settings.RememberUnlockedKeys);

        shell.RememberUnlockedKeys = true;
        Assert.True(backend.RemembersUnlockedKeys);
    }

    [Fact]
    public void Forgetting_is_a_command_and_says_so_in_the_log()
    {
        var shell = TestShell.Create(out var backend);

        Assert.True(shell.RunCommand("keys.forget"));

        Assert.Equal(1, backend.ForgotUnlockedKeys);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("Unlocked keys forgotten", StringComparison.Ordinal));
    }

    [Fact]
    public void The_requests_carry_what_the_core_reads()
    {
        var transport = new FakeTransport();
        using var backend = new Backend.CoreBackend(transport);

        backend.RememberUnlockedKeys(false);
        backend.ForgetUnlockedKeys();

        var sent = transport.Sent("keys.unlocked").ToList();
        Assert.Equal(2, sent.Count);
        Assert.False(sent[0].GetProperty("remember").GetBoolean());
        Assert.False(sent[1].TryGetProperty("remember", out _), "ohne Angabe: nur vergessen");
    }
}
