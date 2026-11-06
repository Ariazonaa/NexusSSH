using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Gespeicherte Sitzungen von PuTTY, WinSCP und MobaXterm als Profile übernehmen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ForeignSessionTests
{
    public ForeignSessionTests() => L.Init(AppLanguage.English);

    private static StoredSession Key(string name, params (string Name, string Value)[] values)
        => new(name, values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Putty_sessions_become_ssh_profiles_and_everything_else_stays_behind()
    {
        var sessions = ForeignSessions.FromPutty(
            [
                Key("Default%20Settings", ("HostName", ""), ("Protocol", "ssh"), ("PortNumber", "22")),
                Key("web%2001", ("HostName", "10.0.1.10"), ("Protocol", "ssh"), ("PortNumber", "2222"), ("UserName", "deploy"), ("PublicKeyFile", @"C:\keys\web.ppk")),
                Key("router", ("HostName", "admin@192.168.1.1"), ("Protocol", "ssh"), ("PortNumber", "22")),
                Key("nas", ("HostName", "nas.local"), ("PortNumber", "0"), ("PublicKeyFile", @"C:\keys\id_ed25519")),
                Key("switch", ("HostName", "10.0.0.2"), ("Protocol", "telnet"), ("PortNumber", "23")),
                Key("com", ("HostName", ""), ("Protocol", "serial")),
            ],
            "me");

        Assert.Equal(
            [
                new ForeignSession("PuTTY", "nas", "nas.local", 22, "me", string.Empty, @"C:\keys\id_ed25519"),
                new ForeignSession("PuTTY", "router", "192.168.1.1", 22, "admin", string.Empty),
                // Telnet kommt mit – ohne Benutzer und Schlüssel, die es dort nicht gibt.
                new ForeignSession("PuTTY", "switch", "10.0.0.2", 23, string.Empty, string.Empty, Protocol: ServerProfile.Telnet),
                // Auch der Schlüssel im Format von PuTTY kommt mit: Den liest der Kern.
                new ForeignSession("PuTTY", "web 01", "10.0.1.10", 2222, "deploy", string.Empty, @"C:\keys\web.ppk"),
            ],
            sessions);

        // Aus der Telnet-Sitzung wird ein Telnet-Profil; ein SSH-Profil zum selben Gerät gilt nicht als „schon da“.
        var telnet = sessions.Single(session => session.IsTelnet);
        Assert.Equal("telnet://10.0.0.2", telnet.Address);
        var profile = ForeignSessions.ToProfile(telnet);
        Assert.Equal((true, string.Empty, 23, null), (profile.IsTelnet, profile.User, profile.Port, profile.KeyPath));
        Assert.False(ForeignSessions.Exists(telnet, [profile with { Name = "other", Protocol = null }]));
        Assert.True(ForeignSessions.Exists(telnet, [profile with { Name = "other" }]));

        // Eine Telnet-Sitzung mit eigenem Port und „benutzer@“ im Host-Feld: Der Benutzer fällt weg.
        var custom = Assert.Single(ForeignSessions.FromPutty([Key("lab", ("HostName", "admin@lab.local"), ("Protocol", "telnet"), ("PortNumber", "2323"))], "me"));
        Assert.Equal(("lab.local", 2323, string.Empty), (custom.Host, custom.Port, custom.User));
    }

    [Fact]
    public void Winscp_sessions_bring_their_folder_and_only_what_runs_over_ssh()
    {
        var sessions = ForeignSessions.FromWinScp(
            [
                Key("Default%20Settings"),
                Key("Kunden/Acme/web%2001", ("HostName", "web01.example.org"), ("UserName", "deploy%40acme"), ("PortNumber", "2200"), ("FSProtocol", "2")),
                Key("backup", ("HostName", "backup.example.org"), ("FSProtocol", "0")),
                Key("ftp", ("HostName", "ftp.example.org"), ("UserName", "u"), ("FSProtocol", "5")),
                Key("broken%zz", ("HostName", "h.example.org"), ("UserName", "u")),
            ],
            "me");

        Assert.Equal(
            [
                new ForeignSession("WinSCP", "backup", "backup.example.org", 22, "me", string.Empty),
                new ForeignSession("WinSCP", "broken%zz", "h.example.org", 22, "u", string.Empty),
                new ForeignSession("WinSCP", "web 01", "web01.example.org", 2200, "deploy@acme", "Kunden/Acme"),
            ],
            sessions);
    }

    [Fact]
    public void Mobaxterm_bookmarks_are_read_from_its_file()
    {
        const string File = """
            [Misc]
            Something=#109#0%not%a%bookmark

            [Bookmarks]
            SubRep=
            ImgNum=42
            web01=#109#0%10.0.1.10%22%ubuntu%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%1%-1#0# #-1
            rdp=#91#4%10.0.1.20%3389%Administrator%0%0

            [Bookmarks_1]
            SubRep=Kunden\Acme
            ImgNum=41
            db=#109#0%db.acme.example%2222%[loginuser]%%-1
            nouser=#109#0%plain.example%22%%
            switch=#98#1%10.0.0.2%23%%%2%%%%%0%0%%1080%%
            """;

        Assert.Equal(
            [
                new ForeignSession("MobaXterm", "web01", "10.0.1.10", 22, "ubuntu", string.Empty),
                new ForeignSession("MobaXterm", "db", "db.acme.example", 2222, "me", "Kunden / Acme"),
                new ForeignSession("MobaXterm", "nouser", "plain.example", 22, "me", "Kunden / Acme"),
                new ForeignSession("MobaXterm", "switch", "10.0.0.2", 23, string.Empty, "Kunden / Acme", Protocol: ServerProfile.Telnet),
            ],
            ForeignSessions.FromMobaXterm(File, "me"));

        Assert.Empty(ForeignSessions.FromMobaXterm("no ini at all\0\u0001", "me"));

        // Die Datei ist meist nicht UTF-8: Dann gilt die westeuropäische Kodierung von Windows.
        Assert.Equal("Büro", ForeignSessions.ReadText([0x42, 0xFC, 0x72, 0x6F]));
        Assert.Equal("Büro", ForeignSessions.ReadText("Büro"u8.ToArray()));
    }

    [Fact]
    public void Chosen_sessions_are_saved_as_profiles_and_known_ones_are_not_preselected()
    {
        var shell = TestShell.Create(out _);
        var known = shell.Profiles[0];
        ForeignSession[] sessions =
        [
            new("PuTTY", "new one", "new.example.org", 2222, "deploy", "Kunden"),
            new("WinSCP", "same target", known.Host, known.Port, known.User, string.Empty),
            new("MobaXterm", known.Name.ToUpperInvariant(), "other.example.org", 22, "u", string.Empty),
        ];

        var import = new SshConfigImportViewModel(sessions, shell.Profiles);

        Assert.Equal("Take over sessions", import.Title);
        Assert.Equal([true, false, false], import.Candidates.Select(candidate => candidate.IsSelected));
        Assert.Equal("PuTTY  ·  deploy@new.example.org:2222  ·  Kunden", import.Candidates[0].Summary);
        Assert.EndsWith("already present", import.Candidates[1].Summary);
        Assert.Equal([sessions[0]], import.SelectedSessions);

        var before = shell.Profiles.Count;
        Assert.Equal(1, shell.ImportSessions(import.SelectedSessions));

        var profile = Assert.Single(shell.Profiles, profile => profile.Name == "new one");
        Assert.Equal(("new.example.org", 2222, "deploy", "Kunden"), (profile.Host, profile.Port, profile.User, profile.Group));
        Assert.Equal(before + 1, shell.Profiles.Count);
        Assert.Contains(shell.Output, entry => entry.Text == "1 session(s) taken over as profiles.");

        // Ohne Fund sagt der Dialog das, statt eine leere Liste zu zeigen.
        var empty = new SshConfigImportViewModel(Array.Empty<ForeignSession>(), shell.Profiles);
        Assert.False(empty.HasCandidates);
        Assert.Equal("No saved SSH or Telnet sessions were found.", empty.Intro);
    }

    [Fact]
    public void Importing_sessions_asks_the_window()
    {
        var shell = TestShell.Create(out _);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        Assert.True(shell.RunCommand("server.importSessions"));
        Assert.True(shell.RunCommand("server.importSessionFile"));

        Assert.Equal([UiRequest.ImportSessions, UiRequest.ImportSessionFile], requests);
    }
}
