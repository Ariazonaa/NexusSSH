using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>„Hinterlegte Schlüssel“: authorized_keys des Servers ansehen, einzeln entfernen, eigene dazulegen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class AuthorizedKeysTests
{
    public AuthorizedKeysTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, SessionViewModel Session, MockServerMonitor Server) Connected(string? keyPath = null)
    {
        var shell = TestShell.Create(out _);
        if (keyPath is not null)
        {
            shell.SaveProfile(shell.Profiles[0] with { KeyPath = keyPath, IdentityId = null });
        }

        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        return (shell, session, Assert.IsType<MockServerMonitor>(session.Connection.Monitor));
    }

    [Fact]
    public async Task The_list_names_each_key_recognises_the_own_one_and_offers_only_keys_not_there_yet()
    {
        var (shell, session, _) = Connected("~/.ssh/id_ed25519");
        var keys = shell.AuthorizedKeysFor(session)!;

        await keys.LoadAsync();

        var file = Assert.Single(keys.Files);
        Assert.Equal("/home/demo/.ssh/authorized_keys", file.Title);
        Assert.Equal(2, keys.Count);
        var own = file.Keys[0];
        Assert.Equal("ssh-ed25519 · me@workstation", own.Title);
        Assert.Equal(("id_ed25519", true), (own.LocalName, own.IsProfileKey));
        var other = file.Keys[1];
        Assert.Equal("ssh-rsa (4096 Bit) · backup@nas", other.Title);
        Assert.Contains("Options: from=\"10.0.0.0/8\",no-pty", other.Details, StringComparison.Ordinal);
        Assert.Equal((string.Empty, false), (other.LocalName, other.IsProfileKey));

        // Dazulegen lassen sich nur die eigenen, die dort noch nicht stehen.
        Assert.Equal(["id_rsa_work", "legacy.ppk"], file.Addable.Select(key => key.Name));
        Assert.Empty(keys.Error);
    }

    [Fact]
    public async Task Removing_asks_more_clearly_for_the_own_and_the_last_key_and_reads_the_list_again()
    {
        var (shell, session, server) = Connected("~/.ssh/id_ed25519");
        var keys = shell.AuthorizedKeysFor(session)!;
        await keys.LoadAsync();
        var (own, other) = (keys.Files[0].Keys[0], keys.Files[0].Keys[1]);

        Assert.Equal(L.Get("AuthorizedRemoveOwn"), keys.RemoveWarning(own));
        Assert.Equal(L.Get("AuthorizedRemoveConfirm"), keys.RemoveWarning(other));

        Assert.Null(await keys.RemoveAsync(other));
        Assert.Single(server.AuthorizedSamples);
        Assert.Equal("me@workstation", Assert.Single(keys.Files[0].Keys).Key.Comment);
        Assert.Contains(shell.Output, entry => entry.Text.Contains(other.Key.Fingerprint, StringComparison.Ordinal));

        // Jetzt ist der eigene auch der letzte: Die eigene Warnung geht vor.
        Assert.Equal(L.Get("AuthorizedRemoveOwn"), keys.RemoveWarning(keys.Files[0].Keys[0]));
    }

    [Fact]
    public async Task The_last_key_gets_its_own_warning()
    {
        var (shell, session, server) = Connected();
        server.AuthorizedSamples.RemoveAt(0);
        var keys = shell.AuthorizedKeysFor(session)!;
        await keys.LoadAsync();

        Assert.Equal(L.Get("AuthorizedRemoveLast"), keys.RemoveWarning(keys.Files[0].Keys[0]));
    }

    [Fact]
    public async Task A_file_that_changed_meanwhile_keeps_its_keys_and_the_dialog_says_so()
    {
        var (shell, session, server) = Connected();
        var keys = shell.AuthorizedKeysFor(session)!;
        await keys.LoadAsync();
        var first = keys.Files[0].Keys[0];

        server.ChangeAuthorizedKeysElsewhere();
        Assert.Equal(L.Get("AuthorizedChanged"), await keys.RemoveAsync(first));

        Assert.Equal(2, server.AuthorizedSamples.Count);
        Assert.Equal(2, keys.Count);
        Assert.Contains(shell.Problems, entry => entry.Text.Contains(first.Key.Fingerprint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_own_key_is_added_and_then_listed_as_the_one_on_this_computer()
    {
        var (shell, session, server) = Connected();
        var keys = shell.AuthorizedKeysFor(session)!;
        await keys.LoadAsync();
        var work = keys.Files[0].Addable.Single(key => key.Name == "id_rsa_work");

        Assert.Null(await keys.AddAsync(keys.Files[0], work));

        Assert.Contains(work.PublicKey, server.InstalledKeys);
        Assert.Contains(keys.Files[0].Keys, item => item.LocalName == "id_rsa_work");
        Assert.DoesNotContain(keys.Files[0].Addable, key => key.Name == "id_rsa_work");

        // Über SFTP: Auch ein Konto ohne Befehle bekommt ihn.
        server.RefusesCommands = true;
        var legacy = keys.Files[0].Addable.Single(key => key.Name == "legacy.ppk");
        Assert.Null(await keys.AddAsync(keys.Files[0], legacy));
        Assert.Contains(legacy.PublicKey, server.InstalledKeys);

        // Hat sich die Datei inzwischen geändert, kommt nichts dazu.
        var stale = keys.Files[0];
        server.ChangeAuthorizedKeysElsewhere();
        server.InstalledKeys.Clear();
        Assert.Equal(L.Get("AuthorizedChanged"), await keys.AddAsync(stale, work));
        Assert.Empty(server.InstalledKeys);
    }

    [Fact]
    public async Task A_line_that_is_no_key_is_shown_but_cannot_be_removed()
    {
        var (shell, session, server) = Connected();
        server.AuthorizedSamples.Add(new ServerKey(3, string.Empty, null, string.Empty, string.Empty, string.Empty, "garbage line"));
        var keys = shell.AuthorizedKeysFor(session)!;
        await keys.LoadAsync();

        var broken = keys.Files[0].Keys[2];
        Assert.False(broken.CanRemove);
        Assert.Equal((L.Get("AuthorizedUnreadableLine", 3), "garbage line"), (broken.Title, broken.Details));
        Assert.Null(await keys.RemoveAsync(broken));
        Assert.Equal(3, server.AuthorizedSamples.Count);
    }

    [Fact]
    public async Task The_core_reads_and_removes_over_sftp_and_what_the_server_wrote_arrives_cleaned()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");

        var listing = connection.Monitor!.AuthorizedKeysAsync();
        var list = Assert.Single(transport.Sent("authorized.list"));
        transport.Raise($$"""
            {"event":"sftp.result","request":{{list.GetProperty("request").GetUInt64()}},"ok":true,"server_keys":[
              {"path":"/home/u/.ssh/authorized_keys","exists":true,"version":"abc","keys":[
                {"line":2,"algorithm":"ssh-ed25519","bits":256,"fingerprint":"SHA256:aaa","comment":"evil\u001b[31m","options":"no-pty\u0007"},
                {"line":5,"algorithm":"","fingerprint":"","comment":"","options":"","unreadable":"junk\u202e"}]},
              {"path":"/C:/ProgramData/ssh/administrators_authorized_keys","exists":true,"shared":true,"version":"def","keys":[]},
              {"exists":false,"version":"","keys":[]}]}
            """);
        var files = await listing;

        Assert.Equal(2, files.Count);
        var key = files[0].Keys[0];
        Assert.Equal((2u, "ssh-ed25519", 256, "SHA256:aaa"), (key.Line, key.Algorithm, key.Bits, key.Fingerprint));
        Assert.DoesNotContain(key.Comment + key.Options + files[0].Keys[1].Unreadable, character => char.IsControl(character) || character == '\u202e');
        Assert.False(files[0].Keys[1].IsReadable);
        Assert.True(files[1].Shared);

        var removing = connection.Monitor.RemoveAuthorizedKeyAsync(files[0], key);
        var remove = Assert.Single(transport.Sent("authorized.remove"));
        Assert.Equal(
            ("/home/u/.ssh/authorized_keys", "abc", 2u, "SHA256:aaa"),
            (remove.GetProperty("path").GetString(), remove.GetProperty("version").GetString(), remove.GetProperty("line").GetUInt32(), remove.GetProperty("fingerprint").GetString()));
        transport.Raise($$"""{"event":"sftp.result","request":{{remove.GetProperty("request").GetUInt64()}},"ok":false,"error":"changed","message":"the file changed since it was read"}""");
        Assert.Equal("changed", (await Assert.ThrowsAsync<RemoteFileException>(() => removing)).Code);

        var adding = connection.Monitor.AddAuthorizedKeyAsync(files[0], "ssh-ed25519 AAAA me@pc");
        var add = Assert.Single(transport.Sent("authorized.add"));
        Assert.Equal(
            ("/home/u/.ssh/authorized_keys", "abc", "ssh-ed25519 AAAA me@pc"),
            (add.GetProperty("path").GetString(), add.GetProperty("version").GetString(), add.GetProperty("public_key").GetString()));
        transport.Raise($$"""{"event":"sftp.result","request":{{add.GetProperty("request").GetUInt64()}},"ok":true}""");
        await adding;
    }

    [Fact]
    public void The_command_is_there_only_for_a_connected_ssh_session()
    {
        var shell = TestShell.Create(out _);
        var command = shell.Commands.Single(candidate => candidate.Id == "session.authorizedKeys");
        Assert.False(command.IsAvailable);

        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        Assert.True(command.IsAvailable);
        Assert.NotNull(shell.AuthorizedKeysFor(session));
    }
}
