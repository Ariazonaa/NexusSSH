using System.Text;
using NexusSSH.Core.Exchange;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Sitzungen aus Dateien anderer Programme. Die Beispiele sind nach den Formaten dieser Programme geschrieben,
/// nicht aus ihnen exportiert – die Programme standen beim Bauen nicht zur Verfügung.
/// </summary>
public sealed class ForeignFileTests
{
    private static IReadOnlyList<ForeignSession> Read(string fileName, string text) => ForeignFiles.Read(fileName, Encoding.UTF8.GetBytes(text), "me");

    private static (string Name, string Host, int Port, string User, string Group) Row(ForeignSession session)
        => (session.Name, session.Host, session.Port, session.User, session.Group);

    [Fact]
    public void An_ssh_config_file_yields_one_session_per_named_host()
    {
        var sessions = Read("sshconfig", """
            # exported
            Host web
                HostName web01.example.org
                User deploy
                Port 2222
                IdentityFile /keys/id_web

            Host db
              HostName=10.0.0.5

            Host *.internal jump?
                User ops

            Host *
                User fallback
                Port 22
            """);

        Assert.Equal(
            [("db", "10.0.0.5", 22, "fallback", ""), ("web", "web01.example.org", 2222, "deploy", "")],
            sessions.Select(Row));
        Assert.Equal("/keys/id_web", sessions[1].KeyPath);
        Assert.All(sessions, session => Assert.Equal(ForeignFiles.SshConfig, session.Source));
    }

    [Fact]
    public void Patterns_hand_their_values_to_the_hosts_they_match_and_the_first_value_wins()
    {
        var sessions = Read("config", """
            Host app.internal
                HostName 10.1.1.1
            Host !app.internal *.internal
                User nobody
            Host *.internal
                User ops
                Port 2200
            Match host x
                User ignored
            """);

        Assert.Equal([("app.internal", "10.1.1.1", 2200, "ops", "")], sessions.Select(Row));
    }

    [Fact]
    public void A_host_without_a_user_gets_the_one_of_this_computer_and_its_alias_as_address()
        => Assert.Equal([("box", "box", 22, "me", "")], Read("x", "Host box\n").Select(Row));

    [Fact]
    public void Mremoteng_connections_come_with_their_folders_and_without_passwords()
    {
        var sessions = Read("confCons.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
              <Node Name="Customers" Type="Container">
                <Node Name="Acme" Type="Container">
                  <Node Name="acme-web" Type="Connection" Hostname="web.acme.example" Protocol="SSH2" Port="22" Username="root" Password="AAAA" />
                  <Node Name="acme-rdp" Type="Connection" Hostname="win.acme.example" Protocol="RDP" Port="3389" Username="admin" />
                </Node>
                <Node Name="switch" Type="Connection" Hostname="10.0.0.2" Protocol="Telnet" Port="23" />
              </Node>
              <Node Name="lonely" Type="Connection" Hostname="h" Protocol="SSH1" Port="" Username="" />
            </mrng:Connections>
            """);

        Assert.Equal(
            [("lonely", "h", 22, "me", ""), ("switch", "10.0.0.2", 23, "", "Customers"), ("acme-web", "web.acme.example", 22, "root", "Customers / Acme")],
            sessions.Select(Row));
        Assert.True(sessions[1].IsTelnet);
    }

    [Fact]
    public void Royal_ts_connections_find_their_folder_through_the_parent_id()
    {
        var sessions = Read("servers.rtsz", """
            <RTSZDocument>
              <RoyalDocument><ID>doc</ID><Name>Servers</Name></RoyalDocument>
              <RoyalFolder><ID>root</ID><Name>Connections</Name></RoyalFolder>
              <RoyalFolder><ID>f1</ID><Name>Production</Name><ParentID>root</ParentID></RoyalFolder>
              <RoyalFolder><ID>f2</ID><Name>East</Name><ParentID>f1</ParentID></RoyalFolder>
              <RoyalSSHConnection><ID>c1</ID><Name>db-east</Name><URI>db.east.example</URI><Port>2222</Port><ParentID>f2</ParentID><CredentialUsername>postgres</CredentialUsername></RoyalSSHConnection>
              <RoyalTelnetConnection><Name>router</Name><URI>10.0.0.1</URI><Port>23</Port><ParentID>root</ParentID></RoyalTelnetConnection>
              <RoyalRDSConnection><Name>win</Name><URI>win</URI></RoyalRDSConnection>
            </RTSZDocument>
            """);

        Assert.Equal(
            [("router", "10.0.0.1", 23, "", ""), ("db-east", "db.east.example", 2222, "postgres", "Production / East")],
            sessions.Select(Row));
    }

    [Fact]
    public void A_securecrt_session_file_is_named_after_the_file_and_reads_its_port_as_hex()
    {
        var sessions = Read(Path.Combine("Sessions", "web01.ini"), "S:\"Protocol Name\"=SSH2\r\nS:\"Hostname\"=web01.example.org\r\nS:\"Username\"=deploy\r\nD:\"[SSH2] Port\"=000008ae\r\nS:\"Password V2\"=02:abc\r\n");

        Assert.Equal([("web01", "web01.example.org", 2222, "deploy", "")], sessions.Select(Row));
        Assert.Equal(ForeignFiles.SecureCrt, sessions[0].Source);
    }

    [Fact]
    public void A_securecrt_export_lists_sessions_with_their_folders()
    {
        var sessions = Read("export.xml", """
            <VanDyke version="3.0">
              <key name="Sessions">
                <key name="Lab">
                  <key name="lab-sw">
                    <string name="Hostname">10.9.9.9</string>
                    <string name="Protocol Name">Telnet</string>
                  </key>
                  <key name="lab-01">
                    <string name="Hostname">lab01</string>
                    <string name="Protocol Name">SSH2</string>
                    <string name="Username">lab</string>
                    <dword name="[SSH2] Port">22</dword>
                  </key>
                </key>
              </key>
              <key name="Global"><string name="Hostname">not-a-session</string></key>
            </VanDyke>
            """);

        Assert.Equal([("lab-01", "lab01", 22, "lab", "Lab"), ("lab-sw", "10.9.9.9", 23, "", "Lab")], sessions.Select(Row));
    }

    [Fact]
    public void A_remmina_file_reads_host_and_port_from_one_field()
    {
        var ssh = Read("group_ssh_web_host.remmina", "[remmina]\nname=web\nserver=web.example.org:2200\nprotocol=SSH\nusername=admin\ngroup=Kunden/Acme\nssh_privatekey=/home/a/.ssh/id_web\npassword=xyz\n");
        Assert.Equal([("web", "web.example.org", 2200, "admin", "Kunden / Acme")], ssh.Select(Row));
        Assert.Equal("/home/a/.ssh/id_web", ssh[0].KeyPath);

        Assert.Equal([("v6", "2001:db8::7", 22, "me", "")], Read("v6.remmina", "[remmina]\nname=v6\nserver=[2001:db8::7]:22\nprotocol=SFTP\n").Select(Row));
        Assert.Empty(Read("desk.remmina", "[remmina]\nname=desk\nserver=desk\nprotocol=RDP\n"));
    }

    [Fact]
    public void An_xshell_file_is_read_even_in_utf16()
    {
        const string Text = "[CONNECTION]\r\nHost=core.example.org\r\nPort=22\r\nProtocol=SSH\r\n[CONNECTION:AUTHENTICATION]\r\nUserName=netadmin\r\nPassword=abc\r\n";
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)];

        var sessions = ForeignFiles.Read("core.xsh", utf16, "me");

        Assert.Equal([("core", "core.example.org", 22, "netadmin", "")], sessions.Select(Row));
        Assert.Equal(ForeignFiles.Xshell, sessions[0].Source);
    }

    [Fact]
    public void A_mobaxterm_file_still_goes_to_its_own_reader()
        => Assert.Equal([("box", "10.0.0.9", 22, "root", "")], Read("x.mxtsessions", "[Bookmarks]\nSubRep=\nImgNum=42\nbox=#109#0%10.0.0.9%22%root%%-1%-1\n").Select(Row));

    [Theory]
    [InlineData("")]
    [InlineData("just some text\nwithout any meaning")]
    [InlineData("<html><body>no</body></html>")]
    [InlineData("<Connections><Node Name=\"x\" Type=\"Connection\" Hostname=\"\" Protocol=\"SSH2\" /></Connections>")]
    [InlineData("<broken")]
    [InlineData("[remmina]\nname=x\n")]
    public void A_file_that_holds_no_session_yields_nothing(string text)
        => Assert.Empty(Read("x", text));

    [Fact]
    public void Nothing_dangerous_comes_out_of_a_file()
    {
        // Ein Host mit Leerzeichen oder ohne Namen wird später vom Kern abgelehnt; hier kommt er gar nicht erst als
        // „Passwort“ oder Befehl an – eine Sitzung trägt nur Name, Host, Port, Benutzer, Ordner und Schlüsselpfad.
        var session = Assert.Single(Read("confCons.xml", "<Connections><Node Name=\"n\" Type=\"Connection\" Hostname=\"h\" Protocol=\"SSH2\" Port=\"99999\" Username=\"u\" Password=\"secret\" /></Connections>"));

        Assert.Equal(22, session.Port);
        Assert.DoesNotContain("secret", session.ToString(), StringComparison.Ordinal);
        Assert.Empty(ForeignFiles.Read("big", new byte[ForeignFiles.MaxLength + 1], "me"));
    }
}
