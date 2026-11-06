using System.IO.Compression;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

public class DiagnosticBundleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 30, 0, TimeSpan.FromHours(2));

    private static DiagnosticInput Input(string? settings = null, string? log = null, params string[] output)
        => new(
            "0.21.0",
            "0.21.0 (ABI 30, expected 30)",
            IsDemo: false,
            [
                new ServerProfile("p1", "Kundenportal", "portal.kunde-mueller.example", 22, "deploy", "Kunde Müller/Produktion", ["prod", "bezahlt"], KeyPath: @"C:\Users\anna\.ssh\id_kunde", HasSavedSecret: true),
                new ServerProfile("p2", "gate", "203.0.113.7", 2222, "ops", string.Empty, [], JumpId: "p1", ForwardAgent: true, Protocol: "ssh"),
            ],
            [new Identity("i1", "Müller Admin", "mueller-admin", @"C:\Users\anna\.ssh\id_admin")],
            settings,
            log,
            [.. output.Select(text => new LogEntry(Now, text))],
            OpenSessions: 1,
            UserName: "anna",
            HomeDirectory: @"C:\Users\anna");

    private static Dictionary<string, string> Unpack(DiagnosticInput input)
    {
        using var buffer = new MemoryStream();
        DiagnosticBundle.Write(buffer, input, Now);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry => new StreamReader(entry.Open()).ReadToEnd());
    }

    [Fact]
    public void The_package_holds_versions_log_output_settings_and_an_anonymous_list_of_profiles()
    {
        var files = Unpack(Input("""{ "Language": "German" }""", "2026-10-10 boom", "started"));

        Assert.Equal(["app.log", "info.txt", "output.txt", "profiles.txt", "settings.json"], files.Keys.Order());
        Assert.Contains("app: 0.21.0", files["info.txt"]);
        Assert.Contains("core: 0.21.0 (ABI 30, expected 30)", files["info.txt"]);
        Assert.Contains("profiles: 2, identities: 1, open sessions: 1", files["info.txt"]);
        Assert.Contains("boom", files["app.log"]);
        Assert.Contains("started", files["output.txt"]);
        Assert.Equal(
            "server-1: ssh, port 22, key file, agent, saved password, in a group, 2 tags\nserver-2: ssh, port 2222, agent, agent forwarding, jump via server-1\n",
            files["profiles.txt"]);
    }

    [Fact]
    public void Nothing_in_the_package_names_a_server_a_user_a_group_or_a_path()
    {
        var files = Unpack(Input(
            """{ "EditorPath": "C:\\Users\\anna\\tools\\edit.exe", "Bookmarks": { "p1": ["/srv/kunde-mueller"] }, "GroupOrder": ["Kunde Müller"], "Snippets": [{ "Name": "deploy", "Text": "ssh deploy@portal.kunde-mueller.example" }] }""",
            "2026-10-10T12:30:05+02:00 System.IO.IOException: C:\\Users\\anna\\AppData\\Local\\NexusSSH\\trails.json\n   connect to portal.kunde-mueller.example:22 as deploy failed (203.0.113.7, 2001:db8::17, fe80::1)\n",
            "Verbunden mit Kundenportal (deploy@portal.kunde-mueller.example)",
            "KUNDENPORTAL: Verbindung getrennt – ops@gate über 203.0.113.7:2222",
            "Identität Müller Admin benutzt C:\\Users\\anna\\.ssh\\id_admin; Gruppe Produktion; unbekannt: backup@other.example.org und db.intern"));

        var everything = string.Join('\n', files.Values);
        foreach (var secret in new[] { "kunde", "mueller", "Müller", "Kundenportal", "portal", "deploy", "203.0.113.7", "2001:db8", "fe80", "anna", "id_admin", "Produktion", "other.example", "backup@", "db.intern", "edit.exe", "/srv" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }

        // Was bleibt, lässt sich noch lesen: derselbe Server ist derselbe Platzhalter, die Uhrzeit ist keine Adresse.
        Assert.Contains("Verbunden mit <server-1> (<user>@<server-1>)", files["output.txt"]);
        Assert.Contains("12:30:05", files["app.log"]);
        Assert.Contains("System.IO.IOException", files["app.log"]);
        Assert.Contains("trails.json", files["app.log"]);
    }

    [Fact]
    public void Settings_keep_switches_numbers_and_a_few_plain_words_only()
    {
        var clean = DiagnosticBundle.Settings(
            """{ "Language": "German", "TerminalFontSize": 14, "ShowHiddenFiles": true, "TerminalFont": "Cascadia Mono", "EditorPath": "C:\\x\\edit.exe", "LocalFolder": "", "Highlights": "error=red", "Bookmarks": { "a": ["/x"], "b": [] }, "OpenTabs": ["p1", "p2", "p3"], "Shortcuts": { "view.nextPane": "Ctrl+F6", "bad key!": "x" }, "TerminalTheme": "C:\\not\\an\\id", "FileSort": "Owner", "LocalSort": "Type", "RemoteColumns": { "Visible": ["Size"], "Widths": { } }, "Future": { "Host": "h" } }""");

        using var document = System.Text.Json.JsonDocument.Parse(clean);
        var root = document.RootElement;
        Assert.Equal("German", root.GetProperty("Language").GetString());
        Assert.Equal(14, root.GetProperty("TerminalFontSize").GetInt32());
        Assert.True(root.GetProperty("ShowHiddenFiles").GetBoolean());
        Assert.Equal("Cascadia Mono", root.GetProperty("TerminalFont").GetString());
        Assert.Equal("(set)", root.GetProperty("EditorPath").GetString());
        Assert.Equal("(empty)", root.GetProperty("LocalFolder").GetString());
        Assert.Equal("(set)", root.GetProperty("Highlights").GetString());
        Assert.Equal("(2 entries)", root.GetProperty("Bookmarks").GetString());
        Assert.Equal("(3 entries)", root.GetProperty("OpenTabs").GetString());
        Assert.Equal("Ctrl+F6", root.GetProperty("Shortcuts").GetProperty("view.nextPane").GetString());
        Assert.Single(root.GetProperty("Shortcuts").EnumerateObject());
        Assert.Equal("(set)", root.GetProperty("TerminalTheme").GetString());
        Assert.Equal(("Owner", "Type"), (root.GetProperty("FileSort").GetString(), root.GetProperty("LocalSort").GetString()));
        Assert.Equal("(2 entries)", root.GetProperty("RemoteColumns").GetString());
        Assert.Equal("(1 entries)", root.GetProperty("Future").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void Unreadable_settings_become_an_empty_object(string json)
        => Assert.Equal("{}", DiagnosticBundle.Settings(json));

    [Fact]
    public void A_package_without_log_and_settings_still_holds_every_file()
    {
        var files = Unpack(Input());

        Assert.Equal("(leer)", files["app.log"]);
        Assert.Equal("{}", files["settings.json"]);
        Assert.Equal(string.Empty, files["output.txt"]);
    }

    [Fact]
    public void The_shell_writes_the_package_and_says_where()
    {
        var shell = TestShell.Create(out _);
        var path = Path.Combine(Path.GetTempPath(), $"nexusssh-diag-{Guid.NewGuid():N}.zip");
        try
        {
            Assert.True(shell.SaveDiagnostics(path, "1.2.3", isDemo: true));

            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, entry => entry.FullName == "info.txt");
            Assert.Contains(shell.Output, entry => entry.Text.Contains(path, StringComparison.Ordinal));
            Assert.False(shell.SaveDiagnostics(Path.Combine(path, "below-a-file.zip"), "1.2.3", isDemo: true));
            Assert.Contains(shell.Output, entry => entry.IsError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_file_name_carries_the_date_and_time()
        => Assert.StartsWith("nexusssh-diagnose-2026-10-10-", DiagnosticBundle.FileName(Now));
}
