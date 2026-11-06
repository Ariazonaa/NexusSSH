using System.Text.Json;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Der Rest des Sicherheitsdurchgangs vom 2026-10-10, soweit er die App betrifft.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SecurityRestTests
{
    public SecurityRestTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Several_lines_for_a_shell_without_bracketed_paste_need_consent()
    {
        // Die Seite bittet, statt einzufügen; die App fragt und schickt den Text mit dem Vermerk zurück.
        var ask = Assert.IsType<WebMessage.PasteNeedsConsent>(TerminalMessages.Parse("""{"type":"confirmPaste","id":"s1","data":"ls\nrm -rf ~"}"""));
        Assert.Equal(("s1", "ls\nrm -rf ~"), (ask.Id, ask.Content));
        Assert.Null(TerminalMessages.Parse("""{"type":"confirmPaste","id":"s1","data":""}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"confirmPaste","data":"x"}"""));

        using var plain = JsonDocument.Parse(TerminalMessages.Paste("s1", "ls"));
        Assert.False(plain.RootElement.TryGetProperty("confirmed", out _));
        using var confirmed = JsonDocument.Parse(TerminalMessages.Paste("s1", "ls\npwd", confirmed: true));
        Assert.True(confirmed.RootElement.GetProperty("confirmed").GetBoolean());

        // Die Rückfrage nennt die Zahl der Zeilen und zeigt die ersten – gekürzt und als sichtbarer Text.
        var (lines, preview) = TerminalMessages.PastePreview("ls\nrm -rf ~\necho \u202edone");
        Assert.Equal(3, lines);
        Assert.StartsWith("ls\nrm -rf ~\necho ", preview, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202e', preview);

        var many = TerminalMessages.PastePreview(string.Join('\n', Enumerable.Range(1, 40).Select(number => new string('x', 300) + number)));
        Assert.Equal(40, many.Lines);
        Assert.EndsWith("\n…", many.Preview, StringComparison.Ordinal);
        Assert.True(many.Preview.Length < 800);
    }

    [Fact]
    public void A_download_for_editing_carries_the_limit_checked_against_the_listing()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");

        connection.Files!.Download("/etc/hosts", Path.GetTempPath(), RemoteEditor.MaxSize);
        connection.Files!.Download("/etc/motd", Path.GetTempPath());

        var sent = transport.Sent("transfer.start").ToList();
        Assert.Equal(RemoteEditor.MaxSize, sent[0].GetProperty("limit").GetInt64());
        Assert.False(sent[1].TryGetProperty("limit", out _));
        Assert.True(RemotePreview.FetchLimit > RemoteEditor.MaxSize);
    }

    [Fact]
    public void A_local_name_is_shown_like_one_from_a_server()
    {
        var entry = new LocalEntry("rechnung\u202efdp.exe", @"C:\x\rechnung.exe", false, 1, null);
        Assert.DoesNotContain('\u202e', entry.DisplayName);
        Assert.Equal(entry.DisplayName, entry.ToString());
        Assert.Equal("readme.txt", new LocalEntry("readme.txt", @"C:\x\readme.txt", false, 1, null).DisplayName);
    }

    [Fact]
    public void An_identity_changed_by_an_import_reaches_its_profiles_at_once()
    {
        var shell = TestShell.Create(out _);
        shell.Settings.Identities = [new Identity("i1", "Deploy", "old", null)];
        var profile = shell.SaveProfile(new ServerProfile(string.Empty, "web", "web.example", 22, "old", "", [], IdentityId: "i1"))!;
        Assert.Equal("old", shell.Profiles.Single(stored => stored.Id == profile.Id).User);

        // Dieselbe Identität (dieselbe Kennung) mit neuem Benutzer – etwa vom eigenen zweiten Rechner.
        var file = new ExportFile
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            Identities = [new ExportedIdentity("i1", "Deploy", "new")],
        };
        var plan = DataExchange.Plan(file, shell.Profiles, shell.Settings, "/home/a", keyExists: _ => true);
        Assert.Equal(1, plan.ChangedIdentities);
        shell.ImportData(plan, new ImportChoice());

        // Das Profil trägt den neuen Benutzer sofort – nicht erst, wenn es irgendwann wieder gespeichert wird.
        Assert.Equal("new", shell.Profiles.Single(stored => stored.Id == profile.Id).User);
    }
}
