using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Der Selbsttest mit echter Verbindung: Wem er vertraut, und woran er das Tempo des Terminals misst.</summary>
public sealed class LiveSelfTestTests
{
    private static ConnectionPrompt.HostKey Key(string host, int port, bool changed = false)
        => new(host, port, "ssh-ed25519", "SHA256:x", changed, null, null, _ => { });

    private static ConnectionPrompt.Secret Secret(string host, string user = "demo", int attempt = 1, SecretKind kind = SecretKind.Password)
        => new(kind, user, host, null, null, null, [], CanSave: true, attempt, _ => { });

    [Fact]
    public async Task The_self_test_trusts_only_the_test_server_on_this_machine()
    {
        var prompts = new SelfTestPrompts(2222);
        var none = TestContext.Current.CancellationToken;

        Assert.True(await prompts.ConfirmHostKeyAsync(Key("127.0.0.1", 2222), none));
        Assert.False(await prompts.ConfirmHostKeyAsync(Key("127.0.0.1", 22), none));
        Assert.False(await prompts.ConfirmHostKeyAsync(Key("example.org", 2222), none));
        Assert.False(await prompts.ConfirmHostKeyAsync(Key("localhost", 2222), none));

        // Ein geänderter Schlüssel ist auch hier kein Fall für ein stilles Ja.
        Assert.False(await prompts.ConfirmHostKeyAsync(Key("127.0.0.1", 2222, changed: true), none));
    }

    [Fact]
    public async Task The_test_password_goes_only_to_the_test_server_and_only_once()
    {
        var prompts = new SelfTestPrompts(2222);
        var none = TestContext.Current.CancellationToken;

        Assert.Equal(["demo"], (await prompts.AskSecretAsync(Secret("127.0.0.1:2222"), none))?.Values);
        Assert.Equal(false, (await prompts.AskSecretAsync(Secret("127.0.0.1"), none))?.Save);

        Assert.Null(await prompts.AskSecretAsync(Secret("example.org:2222"), none));
        Assert.Null(await prompts.AskSecretAsync(Secret("127.0.0.1:22"), none));
        Assert.Null(await prompts.AskSecretAsync(Secret("127.0.0.1:2222", user: "root"), none));
        Assert.Null(await prompts.AskSecretAsync(Secret("127.0.0.1:2222", attempt: 2), none));
        Assert.Null(await prompts.AskSecretAsync(Secret("127.0.0.1:2222", kind: SecretKind.Passphrase), none));

        // Andere Fragen beantwortet er mit Nein – nicht gefragt heißt nie zugestimmt.
        Assert.False(await ((IUserPrompts)prompts).ConfirmAsync("t", "x", "ok"));
    }

    [Fact]
    public void The_backlog_is_what_the_page_has_not_confirmed_yet()
    {
        var posted = new List<int>();
        Action? pending = null;
        var pump = new TerminalOutputPump((_, data) => posted.Add(data.Length), action => pending = action, (_, _) => { });
        Assert.Equal(0, pump.Backlog("s1"));

        // Wartend zählt, unterwegs zählt, bestätigt nicht mehr.
        pump.Add("s1", new byte[300]);
        Assert.Equal(300, pump.Backlog("s1"));
        pending!();
        Assert.Equal([300], posted);
        Assert.Equal(300, pump.Backlog("s1"));
        pump.Acknowledge("s1", 100);
        Assert.Equal(200, pump.Backlog("s1"));
        pump.Acknowledge("s1", 200);
        Assert.Equal(0, pump.Backlog("s1"));
        Assert.Equal(0, pump.Backlog("other"));
    }
}
