using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>„Pfad kopieren als …“: sftp-Link, scp-Ziel, FTP-Link.</summary>
public sealed class PathLinkTests
{
    private static readonly ServerProfile P = new("p1", "web01", "web01.example.org", 22, "anna", string.Empty, []);

    private static readonly ServerProfile Ftp = new("p2", "files", "files.example.org", 21, "anna", string.Empty, [], Protocol: ServerProfile.Ftp);

    [Fact]
    public void An_sftp_link_encodes_user_and_path_and_names_the_port_only_when_unusual()
    {
        Assert.Equal("sftp://anna@web01.example.org/var/log/syslog", PathLink.Format(P, "/var/log/syslog", PathLinkKind.Sftp));
        Assert.Equal("sftp://anna@web01.example.org:2222/srv/a%20b/%23x", PathLink.Format(P with { Port = 2222 }, "/srv/a b/#x", PathLinkKind.Sftp));
        Assert.Equal("sftp://anna%40corp@[2001:db8::1]/home", PathLink.Format(P with { User = "anna@corp", Host = "2001:db8::1" }, "/home", PathLinkKind.Sftp));
    }

    [Fact]
    public void An_scp_target_is_quoted_only_when_a_shell_could_read_it()
    {
        Assert.Equal("anna@web01.example.org:/var/log/syslog", PathLink.Format(P, "/var/log/syslog", PathLinkKind.Scp));
        Assert.Equal("'anna@web01.example.org:/srv/a b'", PathLink.Format(P, "/srv/a b", PathLinkKind.Scp));
        Assert.Equal("scp -P 2222 anna@web01.example.org:/x", PathLink.Format(P with { Port = 2222 }, "/x", PathLinkKind.Scp));
        Assert.Equal("anna@[2001:db8::1]:/x", PathLink.Format(P with { Host = "2001:db8::1" }, "/x", PathLinkKind.Scp));
        Assert.Equal("'anna@h:/it'\\''s'", PathLink.Format(P with { Host = "h" }, "/it's", PathLinkKind.Scp));
    }

    [Fact]
    public void An_ftp_link_follows_the_encryption_and_the_port()
    {
        Assert.Equal("ftps://anna@files.example.org/pub/a.txt", PathLink.Format(Ftp, "/pub/a.txt", PathLinkKind.Ftp));
        Assert.Equal("ftp://anna@files.example.org/pub", PathLink.Format(Ftp with { FtpTls = ServerProfile.FtpPlain }, "/pub", PathLinkKind.Ftp));
        Assert.Equal("ftps://anna@files.example.org/pub", PathLink.Format(Ftp with { FtpTls = ServerProfile.FtpImplicit, Port = 990 }, "/pub", PathLinkKind.Ftp));
        Assert.Equal("ftps://anna@files.example.org:2121/pub", PathLink.Format(Ftp with { Port = 2121 }, "/pub", PathLinkKind.Ftp));
    }

    [Fact]
    public void The_kinds_fit_the_profile()
    {
        Assert.Equal([PathLinkKind.Plain, PathLinkKind.Sftp, PathLinkKind.Scp], PathLink.KindsFor(P));
        Assert.Equal([PathLinkKind.Plain, PathLinkKind.Ftp], PathLink.KindsFor(Ftp));
        Assert.Equal([PathLinkKind.Plain], PathLink.KindsFor(P with { Protocol = ServerProfile.Telnet }));
        Assert.Equal("/a b", PathLink.Format(P, "/a b", PathLinkKind.Plain));
    }

    [Fact]
    public void A_name_or_host_starting_with_a_dash_never_becomes_an_scp_option()
    {
        foreach (var profile in new[] { P with { User = "-oProxyCommand=x" }, P with { Host = "-oProxyCommand=x" } })
        {
            Assert.Equal([PathLinkKind.Plain, PathLinkKind.Sftp], PathLink.KindsFor(profile));
            var text = PathLink.Format(profile, "/x", PathLinkKind.Scp);
            Assert.StartsWith("sftp://", text);
            Assert.Equal(PathLink.Format(profile, "/x", PathLinkKind.Sftp), text);
        }
    }

    [Fact]
    public void Control_characters_never_reach_the_clipboard()
    {
        Assert.Equal("sftp://anna@web01.example.org/a%0Ab", PathLink.Format(P, "/a\nb", PathLinkKind.Sftp));
        Assert.Equal("'anna@web01.example.org:/a?b'", PathLink.Format(P, "/a\nb", PathLinkKind.Scp));
        Assert.Equal("/a?b", PathLink.Format(P, "/a\nb", PathLinkKind.Plain));
    }

    [Fact]
    public void Brackets_are_plain_only_around_an_ipv6_host()
    {
        Assert.Equal("'anna@h:/x/a[1].txt'", PathLink.Format(P with { Host = "h" }, "/x/a[1].txt", PathLinkKind.Scp));
        Assert.Equal("anna@[2001:db8::1]:/x", PathLink.Format(P with { Host = "2001:db8::1" }, "/x", PathLinkKind.Scp));
        Assert.Equal("'anna@[2001:db8::1]:/a[1]'", PathLink.Format(P with { Host = "2001:db8::1" }, "/a[1]", PathLinkKind.Scp));
    }

    [Fact]
    public void Characters_that_turn_the_writing_direction_never_reach_the_clipboard()
    {
        var path = "/a\u202Eb\u2028c\u2066d\u200Fe";
        Assert.Equal("sftp://anna@web01.example.org/a%E2%80%AEb%E2%80%A8c%E2%81%A6d%E2%80%8Fe", PathLink.Format(P, path, PathLinkKind.Sftp));
        Assert.Equal("'anna@web01.example.org:/a?b?c?d?e'", PathLink.Format(P, path, PathLinkKind.Scp));
        Assert.Equal("/a?b?c?d?e", PathLink.Format(P, path, PathLinkKind.Plain));
    }

    [Fact]
    public void Several_entries_give_one_line_each()
    {
        var lines = new[] { "/a", "/b c" }.Select(path => PathLink.Format(P, path, PathLinkKind.Scp));
        Assert.Equal("anna@web01.example.org:/a\n'anna@web01.example.org:/b c'", string.Join('\n', lines));
    }
}
