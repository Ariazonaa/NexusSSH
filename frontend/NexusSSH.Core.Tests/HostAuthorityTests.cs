using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>Zertifizierungsstellen für Host-Zertifikate: eintragen, ansehen, entfernen – und was der Verbindungstest dazu sagt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class HostAuthorityTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtleWtleWtleWtleWtleWtleWtleWtleWtleWtleWtleWtl";

    [Theory]
    [InlineData("*")]
    [InlineData("web01")]
    [InlineData(" *.example.org,10.0.?.1 ")]
    [InlineData("[web.example.org]:2222")]
    [InlineData("a,!b")]
    [InlineData("fe80::1")]
    public void Host_name_patterns_are_accepted(string patterns) => Assert.True(HostAuthority.IsPatterns(patterns));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a,")]
    [InlineData(",a")]
    [InlineData("!a")]
    [InlineData("a,!")]
    [InlineData("a\n@cert-authority *")]
    [InlineData("a#b")]
    [InlineData("ä.example.org")]
    public void Anything_that_would_become_another_line_of_the_file_is_not_a_pattern(string? patterns)
        => Assert.False(HostAuthority.IsPatterns(patterns));

    [Fact]
    public void A_pattern_for_every_server_is_told_apart()
    {
        Assert.True(HostAuthority.CoversEverything("*"));
        Assert.True(HostAuthority.CoversEverything("web01,**"));
        Assert.False(HostAuthority.CoversEverything("*.example.org"));
        Assert.False(HostAuthority.CoversEverything("a b,*"));
    }

    [Theory]
    [InlineData(Key, true)]
    [InlineData(Key + " ca@firma mit kommentar", true)]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTY=", true)]
    [InlineData("", false)]
    [InlineData("ssh-ed25519", false)]
    [InlineData("ssh-ed25519 kurz", false)]
    [InlineData("ssh-ed25519-cert-v01@openssh.com AAAAC3NzaC1lZDI1NTE5AAAAIGtleWtleQ==", false)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", false)]
    [InlineData(Key + "\n" + Key, false)]
    [InlineData("ssh-ed25519 AAAA$(rm)AAAAAAAAAAAAAAAAAAAA", false)]
    public void Only_one_line_with_a_public_key_looks_like_a_key(string text, bool expected)
        => Assert.Equal(expected, HostAuthority.LooksLikeKey(text));

    [Fact]
    public void An_authority_is_added_shown_and_removed_through_the_shell()
    {
        var shell = TestShell.Create(out _);
        shell.LoadKnownHosts();
        Assert.Equal(2, shell.KnownHosts.Count);

        // Was der Kern ablehnt, wird ein Satz für den Dialog – und nichts ändert sich.
        Assert.Equal("These are not names or patterns for servers.", shell.TrustAuthority("a b", Key));
        Assert.StartsWith("This is not a public key.", shell.TrustAuthority("*", "nonsense"), StringComparison.Ordinal);
        Assert.Equal(2, shell.KnownHosts.Count);

        Assert.Null(shell.TrustAuthority(" *.example.org ", Key + " ca@firma"));
        var authority = Assert.Single(shell.KnownHosts, host => host.IsAuthority);
        Assert.Equal(("*.example.org", "ssh-ed25519"), (authority.Name, authority.Algorithm));
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith("Added the certificate authority for *.example.org", StringComparison.Ordinal));

        // Ein Server mit demselben Namen wie die Muster wäre ein anderer Eintrag: Entfernt wird nur die Stelle.
        shell.ForgetHost(authority);
        Assert.DoesNotContain(shell.KnownHosts, host => host.IsAuthority);
        Assert.Equal(2, shell.KnownHosts.Count);
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith("Removed the certificate authority for *.example.org", StringComparison.Ordinal));
    }

    [Fact]
    public void The_core_is_told_patterns_and_key_and_which_authority_to_remove()
    {
        var transport = new FakeTransport();
        transport.Responses["hostkeys.authority"] =
            """{"ok":true,"host_keys":[{"name":"*.example.org","algorithm":"ssh-ed25519","fingerprint":"SHA256:ca","authority":true}]}""";
        transport.Responses["hostkeys.list"] =
            """{"ok":true,"host_keys":[{"name":"web01","algorithm":"ssh-ed25519","fingerprint":"SHA256:abc"},{"name":"*.example.org","algorithm":"ssh-ed25519","fingerprint":"SHA256:ca","authority":true}]}""";
        using var backend = new CoreBackend(transport);

        var added = backend.TrustAuthority("*.example.org", Key);
        Assert.Equal(new KnownHost("*.example.org", "ssh-ed25519", "SHA256:ca", IsAuthority: true), added);
        var sent = Assert.Single(transport.Sent("hostkeys.authority"));
        Assert.Equal(("*.example.org", Key), (sent.GetProperty("patterns").GetString(), sent.GetProperty("key").GetString()));

        Assert.Equal([false, true], backend.KnownHosts().Select(host => host.IsAuthority));

        Assert.True(backend.ForgetHost("*.example.org", "SHA256:ca"));
        var forget = transport.Sent("hostkeys.forget").Last();
        Assert.Equal(("*.example.org", "SHA256:ca"), (forget.GetProperty("name").GetString(), forget.GetProperty("authority").GetString()));

        // Ein Server wird wie bisher ohne das Feld vergessen.
        Assert.True(backend.ForgetHost("web01"));
        Assert.False(transport.Sent("hostkeys.forget").Last().TryGetProperty("authority", out _));

        transport.Responses["hostkeys.authority"] = """{"ok":false,"error":"invalid_key","message":"this is not a public key"}""";
        Assert.Equal("invalid_key", Assert.Throws<BackendException>(() => backend.TrustAuthority("*", "x")).Code);
    }

    [Fact]
    public void The_connection_test_names_a_server_trusted_through_its_certificate()
    {
        TestShell.Create(out _);
        var report = new ProbeReport("methods", ["192.0.2.1"], KeyAlgorithm: "ssh-ed25519", Fingerprint: "SHA256:abc", KeyState: ProbeReport.Certified);

        Assert.Contains(ProbeWording.Lines(report), line => line.Contains("trusted through a certificate from a known authority (ssh-ed25519, SHA256:abc)", StringComparison.Ordinal));
    }
}
