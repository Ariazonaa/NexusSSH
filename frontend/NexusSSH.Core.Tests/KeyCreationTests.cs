using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ein Schlüsselpaar anlegen und auf dem Server der Sitzung hinterlegen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class KeyCreationTests
{
    public KeyCreationTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, MockBackend Backend, SessionViewModel Session, MockServerMonitor Server) Connected()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        return (shell, backend, session, Assert.IsType<MockServerMonitor>(session.Connection.Monitor));
    }

    [Fact]
    public async Task What_needs_commands_is_hidden_once_the_server_says_it_runs_none()
    {
        var (shell, _, session, server) = Connected();
        Assert.True(session.Files.HasCommands);
        var changes = new List<string?>();
        session.Files.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // Ein Konto ohne Befehle: Die Antwort auf die Frage nach dem Verbinden.
        server.RefusesCommands = true;
        await shell.AskForCommandsAsync(session);
        Assert.False(session.Files.HasCommands);
        Assert.Contains(nameof(SftpBrowserViewModel.HasCommands), changes);

        // Die nächste Verbindung kann es wieder können.
        server.RefusesCommands = false;
        await shell.AskForCommandsAsync(session);
        Assert.True(session.Files.HasCommands);
    }

    [Fact]
    public async Task A_windows_server_gets_what_powershell_can_do_and_nothing_that_needs_posix()
    {
        var (shell, _, session, server) = Connected();
        server.IsWindows = true;
        await shell.AskForCommandsAsync(session);

        // Befehle ja (Belegung, Packen) – aber keine Protokolle.
        Assert.True(session.Files.HasCommands);
        Assert.True(session.Files.IsWindowsServer);
        Assert.False(shell.CanShowLogs);

        server.IsWindows = false;
        await shell.AskForCommandsAsync(session);
        Assert.False(session.Files.IsWindowsServer);
        Assert.True(shell.CanShowLogs);
    }

    [Fact]
    public async Task A_server_that_cannot_say_yet_how_it_runs_commands_is_asked_again()
    {
        var (shell, _, session, server) = Connected();
        shell.CommandsRetryDelay = TimeSpan.Zero;

        // Die Probe des Kerns blieb zweimal stumm (ein Windows, dessen erstes PowerShell noch startet) – die App fragt
        // nach, statt den Server wie einen Linux-Server stehen zu lassen.
        server.IsWindows = true;
        server.UnknownShellAnswers = 2;
        var asked = server.ShellQuestions;
        await shell.AskForCommandsAsync(session);
        Assert.Equal(asked + 3, server.ShellQuestions);
        Assert.True(session.Files is { IsWindowsServer: true, HasCommands: true });

        // Sagt er es nie, hört sie nach einigen Versuchen auf, und es bleibt, was galt.
        server.IsWindows = false;
        server.UnknownShellAnswers = 99;
        asked = server.ShellQuestions;
        await shell.AskForCommandsAsync(session);
        Assert.Equal(asked + 5, server.ShellQuestions);
        Assert.True(session.Files is { IsWindowsServer: true, HasCommands: true });
    }

    [Fact]
    public async Task A_new_key_is_installed_on_the_server_and_the_profile_uses_it()
    {
        var (shell, backend, session, server) = Connected();

        Assert.True(await shell.CreateKeyAsync(session, new KeyRequest("id_ed25519_web01", "secret")));

        Assert.Equal(["id_ed25519_web01"], backend.GeneratedKeys);
        Assert.StartsWith("ssh-ed25519 ", Assert.Single(server.InstalledKeys));
        Assert.Equal("~/.ssh/id_ed25519_web01", shell.Profiles.Single(profile => profile.Id == session.Profile.Id).KeyPath);
        Assert.Contains(shell.Output, entry => entry.Text == $"Key ~/.ssh/id_ed25519_web01 created and installed on {session.Profile.Name}; the profile uses it from the next connection on.");

        // Der Name ist jetzt vergeben: Ein zweites Paar entsteht nicht, und am Server ändert sich nichts.
        Assert.False(await shell.CreateKeyAsync(session, new KeyRequest("id_ed25519_web01", null)));
        Assert.Single(server.InstalledKeys);
        Assert.Contains(shell.Problems, entry => entry.Text.StartsWith("The key id_ed25519_web01 could not be created: exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_runs_no_commands_leaves_the_profile_as_it_was()
    {
        var (shell, backend, session, server) = Connected();
        var before = shell.Profiles.Single(profile => profile.Id == session.Profile.Id).KeyPath;
        server.RefusesCommands = true;

        Assert.False(await shell.CreateKeyAsync(session, new KeyRequest("id_new", null)));

        // Das Paar gibt es – es zu löschen, stünde der App nicht zu –, aber das Profil zeigt nicht darauf.
        Assert.Equal(["id_new"], backend.GeneratedKeys);
        Assert.Equal(before, shell.Profiles.Single(profile => profile.Id == session.Profile.Id).KeyPath);
        Assert.Contains(shell.Problems, entry => entry.Text.StartsWith($"The key ~/.ssh/id_new was created but could not be installed on {session.Profile.Name}: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_connection_no_key_is_created()
    {
        var (shell, backend, session, _) = Connected();
        session.Disconnect();

        Assert.False(await shell.CreateKeyAsync(session, new KeyRequest("id_new", null)));
        Assert.Empty(backend.GeneratedKeys);
    }

    [Theory]
    [InlineData("id_ed25519", true)]
    [InlineData("id_ed25519_web-01.key", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("with space", false)]
    [InlineData("schlüssel", false)]
    [InlineData("id.pub", false)]
    [InlineData("config", false)]
    [InlineData("Known_Hosts", false)]
    [InlineData("authorized_keys", false)]
    public void A_key_file_has_a_plain_name(string name, bool valid)
    {
        Assert.Equal(valid, KeyRequest.IsValidName(name));
    }

    [Fact]
    public void The_suggested_name_carries_the_profile_as_far_as_a_file_name_can()
    {
        ServerProfile Named(string name) => new(string.Empty, name, "h", 22, "u", string.Empty, []);

        Assert.Equal("id_ed25519_web01", KeyRequest.SuggestedName(Named("web01")));
        Assert.Equal("id_ed25519_KundeAcme-prod", KeyRequest.SuggestedName(Named("Kunde Acme: /-prod/")));
        Assert.Equal("id_ed25519_nexus", KeyRequest.SuggestedName(Named("生産")));
        Assert.True(KeyRequest.IsValidName(KeyRequest.SuggestedName(Named(new string('x', 200)))));
    }

    [Fact]
    public async Task The_core_is_asked_to_generate_and_install()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        transport.Responses["key.generate"] = """{"ok":true,"path":"C:\\Users\\me\\.ssh\\id_new","public_key":"ssh-ed25519 AAAA me@pc"}""";
        using var backend = new Backend.CoreBackend(transport);

        var key = backend.GenerateKey("id_new", "secret", "me@pc");

        Assert.Equal(new GeneratedKey(@"C:\Users\me\.ssh\id_new", "ssh-ed25519 AAAA me@pc"), key);
        var generate = Assert.Single(transport.Sent("key.generate"));
        Assert.Equal(("id_new", "secret", "me@pc"), (generate.GetProperty("name").GetString(), generate.GetProperty("passphrase").GetString(), generate.GetProperty("comment").GetString()));

        // Ohne Passphrase steht keine in der Anfrage – auch keine leere.
        _ = backend.GenerateKey("id_plain", string.Empty, "me@pc");
        Assert.False(transport.Sent("key.generate").Last().TryGetProperty("passphrase", out _));

        transport.Responses["key.generate"] = """{"ok":false,"error":"exists","message":"id_new already exists"}""";
        Assert.Equal("exists", Assert.Throws<BackendException>(() => backend.GenerateKey("id_new", null, string.Empty)).Code);

        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");

        var installing = connection.Monitor!.InstallKeyAsync(key.PublicKey);
        var install = Assert.Single(transport.Sent("key.install"));
        Assert.Equal("ssh-ed25519 AAAA me@pc", install.GetProperty("public_key").GetString());
        transport.Raise($$"""{"event":"sftp.result","request":{{install.GetProperty("request").GetUInt64()}},"ok":true}""");
        await installing;
    }
}
