using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Seite „Schlüssel“: Schlüsseldateien ansehen, die Passphrase ändern, zwischen den Formaten umwandeln.</summary>
[Collection(LanguageCollection.Name)]
public sealed class KeyFilesTests
{
    public KeyFilesTests() => L.Init(AppLanguage.English);

    [Fact]
    public void The_list_is_read_when_its_page_opens_and_names_who_uses_a_key()
    {
        var settings = new AppSettings();
        settings.Identities.Add(new Identity("i1", "ops", "deploy", "~/.ssh/id_rsa_work"));
        var shell = TestShell.Create(out _, settings);
        Assert.NotNull(shell.SaveProfile(shell.Profiles[0] with { KeyPath = "~\\.ssh\\ID_ED25519" }));
        Assert.Empty(shell.KeyFiles);

        Assert.True(shell.RunCommand("home.keys"));

        Assert.Equal(HomePage.Keys, shell.HomePage);
        Assert.Equal(["id_ed25519", "id_rsa_work", "legacy.ppk"], shell.KeyFiles.Select(key => key.Name));
        Assert.Equal(["Ed25519", "RSA 4096", "ECDSA 256"], shell.KeyFiles.Select(key => key.Kind));

        // Pfade aus Profilen stehen mal so, mal so da; eine Identität steht als solche dabei.
        Assert.Equal(OperatingSystem.IsWindows() ? shell.Profiles[0].Name : string.Empty, shell.KeyFiles[0].UsedBy);
        Assert.Equal("identity ops", shell.KeyFiles[1].UsedBy);
        Assert.Equal(string.Empty, shell.KeyFiles[2].UsedBy);
        Assert.Equal("id_rsa_work, RSA 4096", shell.KeyFiles[1].ToString());
    }

    [Fact]
    public async Task The_passphrase_changes_only_with_the_old_one_and_the_reason_is_a_sentence()
    {
        var shell = TestShell.Create(out _);
        shell.ShowHome(HomePage.Keys);
        var work = shell.KeyFiles[1];
        Assert.True(work.IsEncrypted);

        Assert.Equal("The passphrase is wrong.", await shell.ChangeKeyPassphraseAsync(work, "nope", "next"));
        Assert.Equal("The passphrase is wrong.", await shell.ChangeKeyPassphraseAsync(work, null, "next"));

        Assert.Null(await shell.ChangeKeyPassphraseAsync(work, "demo", "next"));
        Assert.True(shell.KeyFiles[1].IsEncrypted);
        Assert.Equal("The passphrase is wrong.", await shell.ChangeKeyPassphraseAsync(work, "demo", "again"));

        // Ohne neue Passphrase liegt der Schlüssel danach offen da – und das steht im Protokoll, ohne die Passphrase.
        Assert.Null(await shell.ChangeKeyPassphraseAsync(work, "next", string.Empty));
        Assert.False(shell.KeyFiles[1].IsEncrypted);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("removed", StringComparison.Ordinal) && entry.Text.Contains("id_rsa_work", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.Output, entry => entry.Text.Contains("next", StringComparison.Ordinal) || entry.Text.Contains("demo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Converting_adds_a_file_in_the_other_format_and_overwrites_nothing()
    {
        var shell = TestShell.Create(out _);
        shell.ShowHome(HomePage.Keys);
        var plain = shell.KeyFiles[0];
        Assert.Equal((KeyFormat.Ppk, "id_ed25519.ppk"), (plain.OtherFormat, plain.ConvertedName));

        Assert.Null(await shell.ConvertKeyAsync(plain, null, $" {plain.ConvertedName} "));

        var added = Assert.Single(shell.KeyFiles, key => key.Name == "id_ed25519.ppk");
        Assert.Equal((KeyFormat.Ppk, plain.Fingerprint), (added.Format, added.Fingerprint));
        Assert.Equal((KeyFormat.OpenSsh, "id_ed25519"), (added.OtherFormat, added.ConvertedName));

        Assert.Equal("A file with this name already exists there.", await shell.ConvertKeyAsync(plain, null, "id_ed25519.ppk"));
        Assert.StartsWith("This is not a name", await shell.ConvertKeyAsync(plain, null, "../x.ppk"));
        Assert.Equal("The passphrase is wrong.", await shell.ConvertKeyAsync(shell.KeyFiles.First(key => key.IsEncrypted), "nope", "work.ppk"));
        Assert.Equal(4, shell.KeyFiles.Count);
    }

    [Fact]
    public void A_file_from_putty_without_its_ending_gets_a_name_that_says_what_it_is()
    {
        var key = new KeyFile(@"C:\keys\work", "work", KeyFormat.Ppk, "ssh-rsa", null, "SHA256:x", string.Empty, true, string.Empty);

        Assert.Equal("work_openssh", key.ConvertedName);
        Assert.Equal("RSA", key.Kind);
        Assert.Equal("legacy", (key with { Name = "legacy.PPK" }).ConvertedName);
        Assert.Equal(".ppk_openssh", (key with { Name = ".ppk" }).ConvertedName);

        // Verschlüsseltes PEM ohne Datei daneben: Art und Fingerabdruck sind ohne Passphrase nicht zu erfahren.
        var locked = new KeyFile(@"C:\keys\old.pem", "old.pem", KeyFormat.Pem, string.Empty, null, string.Empty, string.Empty, true, string.Empty);
        Assert.False(locked.IsKnown);
        Assert.Equal((string.Empty, "old.pem", KeyFormat.Ppk), (locked.Kind, locked.ToString(), locked.OtherFormat));
    }

    [Fact]
    public void The_core_is_asked_and_what_it_names_is_shown_as_plain_text()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[]}""";
        transport.Responses["key.list"] = """{"ok":true,"keys":[{"path":"C:\\Users\\me\\.ssh\\id_a","name":"id_a\u202e","format":"ppk","algorithm":"ssh-ed25519","bits":256,"fingerprint":"SHA256:abc","comment":"me\u0007@pc","encrypted":true,"public_key":"ssh-ed25519 AAAA me@pc"},{"path":"","name":"x","format":"openssh"},{"path":"C:\\k\\old","name":"old","format":"pem","algorithm":"","fingerprint":"","comment":"","encrypted":true,"public_key":"","unchecked":true}]}""";
        transport.Responses["key.passphrase"] = """{"ok":true}""";
        transport.Responses["key.convert"] = """{"ok":true,"path":"C:\\Users\\me\\.ssh\\id_a.ppk"}""";
        using var backend = new Backend.CoreBackend(transport);

        var keys = backend.KeyFiles([@"C:\k\old"]);

        Assert.Equal([@"C:\k\old"], Assert.Single(transport.Sent("key.list")).GetProperty("paths").EnumerateArray().Select(path => path.GetString()));
        Assert.Equal(2, keys.Count);
        Assert.Equal(new KeyFile(@"C:\Users\me\.ssh\id_a", Format.Visible("id_a\u202e"), KeyFormat.Ppk, "ssh-ed25519", 256, "SHA256:abc", Format.Visible("me\u0007@pc"), true, "ssh-ed25519 AAAA me@pc"), keys[0]);
        Assert.DoesNotContain('\u202e', keys[0].Name);
        Assert.DoesNotContain('\u0007', keys[0].Comment);
        Assert.Equal((KeyFormat.Pem, false, (int?)null), (keys[1].Format, keys[1].IsKnown, keys[1].Bits));
        Assert.Equal((false, true), (keys[0].IsUnchecked, keys[1].IsUnchecked));

        backend.ChangeKeyPassphrase(keys[0].Path, "old", "new");
        var change = Assert.Single(transport.Sent("key.passphrase"));
        Assert.Equal((keys[0].Path, "old", "new"), (change.GetProperty("path").GetString(), change.GetProperty("passphrase").GetString(), change.GetProperty("new_passphrase").GetString()));

        // Ohne Passphrase steht keine in der Anfrage – auch keine leere.
        backend.ChangeKeyPassphrase(keys[0].Path, string.Empty, string.Empty);
        var bare = transport.Sent("key.passphrase").Last();
        Assert.False(bare.TryGetProperty("passphrase", out _) || bare.TryGetProperty("new_passphrase", out _));

        Assert.Equal(@"C:\Users\me\.ssh\id_a.ppk", backend.ConvertKey(keys[0].Path, "old", KeyFormat.OpenSsh, "id_a_openssh"));
        var convert = Assert.Single(transport.Sent("key.convert"));
        Assert.Equal(("openssh", "id_a_openssh", "old"), (convert.GetProperty("format").GetString(), convert.GetProperty("name").GetString(), convert.GetProperty("passphrase").GetString()));

        transport.Responses["key.passphrase"] = """{"ok":false,"error":"wrong_passphrase","message":"the passphrase does not open this key"}""";
        Assert.Equal("wrong_passphrase", Assert.Throws<BackendException>(() => backend.ChangeKeyPassphrase(keys[0].Path, "x", "y")).Code);
        transport.Responses["key.convert"] = """{"ok":false,"error":"exists","message":"exists"}""";
        Assert.Equal("exists", Assert.Throws<BackendException>(() => backend.ConvertKey(keys[0].Path, null, KeyFormat.Ppk, "a.ppk")).Code);
    }
}
