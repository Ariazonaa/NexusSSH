using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Was der Import aus <c>~/.ssh/config</c> zusätzlich versteht – und dass er sagt, was er nicht übernimmt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SshConfigMoreTests
{
    private static readonly ServerProfile Plain = new("p", "plain", "host.example.org", 22, "me", "", []);

    [Fact]
    public void The_list_names_tunnels_and_what_is_not_taken_over()
    {
        TestShell.Create(out _);
        var listing = new SshConfigListing(
            "C:\\Users\\me\\.ssh\\config",
            [
                new SshConfigHost("web01", "10.0.1.10", 22, "ubuntu", null, null, false, false, Tunnels: 2, Skipped: ["ProxyCommand", "Ciphers"]),
                new SshConfigHost("db01", "10.0.1.11", 22, "ubuntu", null, null, false, false),
            ],
            ["exec", "canonical"]);

        var import = new SshConfigImportViewModel(listing);

        Assert.Contains("Tunnels: 2", import.Candidates[0].Summary, StringComparison.Ordinal);
        Assert.Equal("Not taken over: ProxyCommand, Ciphers", import.Candidates[0].Skipped);
        Assert.DoesNotContain("Tunnels", import.Candidates[1].Summary, StringComparison.Ordinal);
        Assert.Equal(string.Empty, import.Candidates[1].Skipped);
        Assert.StartsWith("NexusSSH does not check blocks with “Match exec, canonical”", import.Unchecked, StringComparison.Ordinal);

        // Ohne solche Blöcke steht dort nichts.
        Assert.Equal(string.Empty, new SshConfigImportViewModel(listing with { Unchecked = null }).Unchecked);
    }

    [Fact]
    public void The_core_names_tunnels_skipped_keywords_and_unchecked_conditions()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.read"] =
            """{"ok":true,"path":"C:\\Users\\me\\.ssh\\config","unchecked":["exec"],"hosts":[{"alias":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","key_path":"C:\\Users\\me\\.ssh\\web","forward_agent":false,"exists":false,"identities_only":true,"tunnels":[{"id":"","kind":"local","name":"","listen_port":8080,"target_host":"localhost","target_port":80,"open":false,"auto":true},{"id":"","kind":"dynamic","name":"","listen_port":1080,"target_host":"","target_port":0,"open":false,"auto":true}],"skipped":["ProxyCommand","Evil\u202eWord","","Ciphers"]},{"alias":"db01","host":"10.0.1.11","port":22,"user":"ubuntu","forward_agent":false,"exists":true}]}""";
        using var backend = new CoreBackend(transport);

        var listing = backend.ReadSshConfig();

        Assert.Equal(["exec"], listing.Unchecked);
        var web = listing.Hosts[0];
        Assert.True(web.IdentitiesOnly);
        Assert.Equal(2, web.Tunnels);

        // Die Wörter stehen in einer fremden Datei: ohne Zeichen, die die Schreibrichtung drehen (das wird sichtbar
        // ersetzt), und ohne Leeres.
        Assert.Equal(["ProxyCommand", "Evil�Word", "Ciphers"], web.Skipped);
        Assert.Equal((false, 0, null), (listing.Hosts[1].IdentitiesOnly, listing.Hosts[1].Tunnels, listing.Hosts[1].Skipped));
    }

    [Fact]
    public void After_the_import_the_log_says_what_was_left_out()
    {
        var shell = TestShell.Create(out _);

        shell.ImportSshConfig(["build01", "web01"]);

        Assert.Contains(shell.Output, entry => entry.Text == "build01: not taken over from ~/.ssh/config: ProxyCommand, Ciphers");
        Assert.DoesNotContain(shell.Output, entry => entry.Text.StartsWith("web01: not taken over", StringComparison.Ordinal));
    }

    [Fact]
    public void Offering_only_the_named_key_needs_a_key_and_reaches_the_core()
    {
        TestShell.Create(out _);
        var editor = new ProfileEditorViewModel(null, []) { Host = "h", User = "u", IdentitiesOnly = true };
        Assert.False(editor.ToProfile().IdentitiesOnly);

        editor.KeyPath = "C:\\keys\\id";
        Assert.True(editor.ToProfile().IdentitiesOnly);

        var transport = new FakeTransport();
        transport.Responses["profiles.save"] =
            """{"ok":true,"profile":{"id":"p","name":"plain","host":"host.example.org","port":22,"user":"me","group":"","tags":[],"favorite":false,"use_agent":true,"key_path":"C:\\keys\\id","identities_only":true}}""";
        var backend = new CoreBackend(transport);

        var saved = backend.Save(Plain with { KeyPath = "C:\\keys\\id", IdentitiesOnly = true });
        Assert.True(transport.Sent("profiles.save").Last().GetProperty("profile").GetProperty("identities_only").GetBoolean());
        Assert.True(saved.IdentitiesOnly);

        // Ohne Schlüssel im Profil geht die Angabe nicht hinaus.
        backend.Save(Plain with { IdentitiesOnly = true });
        var sent = transport.Sent("profiles.save").Last().GetProperty("profile");
        Assert.False(sent.TryGetProperty("identities_only", out var only) && only.GetBoolean());
    }
}
