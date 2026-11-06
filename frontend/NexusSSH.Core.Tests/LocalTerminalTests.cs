using NexusSSH.Core.Backend;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Lokale Terminals: eine Shell dieses Rechners als Profil (<c>protocol: local</c>). Das Profil nennt nur die
/// Kennung der Shell und einen Startordner; es bleibt auf diesem Rechner, und der Tab hat weder Dateien noch
/// Server-Info.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class LocalTerminalTests
{
    private static readonly LocalShell[] Found = [new("pwsh", "PowerShell 7"), new("cmd", "Command Prompt"), new("wsl:Ubuntu", "WSL: Ubuntu")];

    public LocalTerminalTests() => L.Init(AppLanguage.English);

    private static ServerProfile LocalProfile(string shell = "cmd", string id = "") => new(id, "Command Prompt", shell, 22, string.Empty, string.Empty, [], Protocol: ServerProfile.Local);

    [Fact]
    public void A_local_profile_needs_no_user_or_port_and_offers_the_shells_found()
    {
        var editor = new ProfileEditorViewModel(localShells: Found);
        Assert.False(editor.IsLocal);
        Assert.Equal(["PowerShell 7", "Command Prompt", "WSL: Ubuntu"], editor.LocalShells.Select(shell => shell.DisplayName));

        // Weder Benutzer noch Port noch Host: Die Shell und der Ordner genügen.
        editor.IsLocal = true;
        (editor.Host, editor.User, editor.Port) = (string.Empty, string.Empty, double.NaN);
        Assert.True(editor.IsValid);
        Assert.False(editor.IsNetwork);
        Assert.False(editor.IsRemote);
        Assert.False(editor.CanEditPort);
        Assert.Equal("pwsh", editor.LocalShellId);

        editor.LocalShellId = "cmd";
        var profile = editor.ToProfile();
        Assert.Equal((ServerProfile.Local, "cmd", string.Empty, 22, "Command Prompt", true), (profile.Protocol, profile.Host, profile.User, profile.Port, profile.Name, profile.IsLocal));
        Assert.False(profile.IsSsh);
        Assert.Null(profile.LocalDirectory);
        Assert.Equal("Command Prompt", profile.Address);

        // Der Startordner ist absolut; ob es ihn gibt, sieht erst der Start nach.
        editor.LocalDirectory = "relative/folder";
        Assert.False(editor.IsValid);
        Assert.NotEmpty(editor.LocalFolderError);
        var folder = LocalPath.Of("work", "project");
        editor.LocalDirectory = $"  {folder}  ";
        Assert.True(editor.IsValid);
        Assert.Empty(editor.LocalFolderError);
        Assert.Equal(folder, editor.ToProfile().LocalDirectory);

        // Alles, was eine Verbindung beschreibt, entfällt – auch was vorher eingetippt war.
        editor.Host = "example.org";
        editor.User = "root";
        editor.ForwardX11 = true;
        editor.Compression = true;
        var shed = editor.ToProfile();
        Assert.Equal(("cmd", string.Empty, false, false, (string?)null), (shed.Host, shed.User, shed.ForwardX11, shed.Compression, shed.JumpId));

        // Eine andere Art schaltet es wieder aus – und umgekehrt.
        editor.IsTelnet = true;
        Assert.False(editor.IsLocal);
        editor.IsLocal = true;
        Assert.False(editor.IsTelnet);
    }

    [Fact]
    public void A_shell_that_is_gone_stays_in_the_list_and_without_any_shell_the_form_is_not_valid()
    {
        var gone = new ProfileEditorViewModel(LocalProfile("wsl:Gone"), localShells: Found);
        Assert.True(gone.IsLocal);
        Assert.Equal("wsl:Gone", gone.LocalShellId);
        Assert.Equal("WSL: Gone", gone.LocalShells[gone.LocalShellIndex].DisplayName);
        Assert.Equal(4, gone.LocalShells.Count);
        Assert.Equal("wsl:Gone", gone.ToProfile().Host);

        // Der Kern fand nichts (oder hat noch nicht geantwortet): Es gibt nichts zu wählen.
        var none = new ProfileEditorViewModel();
        none.IsLocal = true;
        Assert.False(none.IsValid);
        Assert.Equal(string.Empty, none.LocalShellId);

        // Ein vorhandenes Profil zeigt die Shell, nicht das Feld „Host“.
        Assert.Equal(string.Empty, gone.Host);
    }

    [Fact]
    public void The_names_of_shells_are_translated_only_where_windows_and_unix_have_a_word_for_them()
    {
        Assert.Equal("Command Prompt", LocalShellNames.Of("cmd", "Command Prompt"));
        Assert.Equal("Login shell (bash)", LocalShellNames.Of("login", "bash"));
        Assert.Equal("WSL: Ubuntu-24.04", LocalShellNames.Of("wsl:Ubuntu-24.04"));
        Assert.Equal("zsh", LocalShellNames.Of("/usr/bin/zsh"));
        Assert.Equal("PowerShell 7", LocalShellNames.Of("pwsh"));

        // Der Name kommt vom Kern, der ihn aus einem fremden Verzeichnis bildet: nur sichtbare Zeichen.
        Assert.DoesNotContain('\u001b', LocalShellNames.Of("wsl:x", "WSL: a\u001b[31mb"));

        try
        {
            L.Init(AppLanguage.German);
            Assert.Equal("Eingabeaufforderung", LocalShellNames.Of("cmd", "Command Prompt"));
            Assert.Equal("Anmelde-Shell (bash)", LocalShellNames.Of("login", "bash"));
            L.Init(AppLanguage.French);
            Assert.Equal("Invite de commandes", LocalShellNames.Of("cmd"));
            L.Init(AppLanguage.Spanish);
            Assert.Equal("Símbolo del sistema", LocalShellNames.Of("cmd"));
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }

    [Theory]
    [InlineData("pwsh", true)]
    [InlineData("powershell", true)]
    [InlineData("cmd", true)]
    [InlineData("wsl", true)]
    [InlineData("wsl:Ubuntu-24.04", true)]
    [InlineData("gitbash", true)]
    [InlineData("login", true)]
    [InlineData("", false)]
    [InlineData("wsl:", false)]
    [InlineData("wsl:../x", false)]
    [InlineData("wsl:a b", false)]
    [InlineData("wsl:-d", false)]
    [InlineData("C:\\evil.exe", false)]
    [InlineData("pwsh -c calc", false)]
    [InlineData("wsl:a;b", false)]
    [InlineData("/bin/../etc/x", false)]
    [InlineData("bin/sh", false)]
    public void Only_a_kind_of_shell_or_a_plain_distribution_name_is_an_id(string id, bool expected) => Assert.Equal(expected, LocalShellNames.IsId(id));

    [Fact]
    public void A_plain_absolute_path_is_an_id_only_outside_windows()
        => Assert.Equal(!OperatingSystem.IsWindows(), LocalShellNames.IsId("/usr/bin/fish"));

    [Fact]
    public void The_first_shell_found_is_the_one_wanted_with_power_shell_7_before_windows_power_shell()
    {
        Assert.Equal("pwsh", LocalShellNames.Preferred([new("cmd", "c"), new("powershell", "w"), new("pwsh", "p")])!.Id);
        Assert.Equal("powershell", LocalShellNames.Preferred([new("cmd", "c"), new("powershell", "w")])!.Id);
        Assert.Equal("login", LocalShellNames.Preferred([new("/bin/sh", "sh"), new("login", "bash")])!.Id);
        Assert.Equal("cmd", LocalShellNames.Preferred([new("cmd", "c"), new("wsl", "WSL")])!.Id);
        Assert.Null(LocalShellNames.Preferred([]));
    }

    [Fact]
    public void A_local_profile_never_leaves_this_computer_in_an_export()
    {
        var ssh = new ServerProfile("s1", "web01", "10.0.1.10", 22, "deploy", string.Empty, []);
        var local = LocalProfile(id: "l1") with { LocalDirectory = LocalPath.Of("work") };

        var file = DataExchange.Create([ssh, local], new AppSettings { LocalShell = "cmd" }, "1.0.0", DateTimeOffset.UnixEpoch, home: null);
        var json = DataExchange.Serialize(file);

        Assert.Equal(["web01"], file.Profiles!.Select(profile => profile.Name));
        Assert.DoesNotContain("local", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Command Prompt", json, StringComparison.Ordinal);

        // Auch die gewählte Shell gehört zu diesem Rechner: nicht im Export.
        Assert.Null(typeof(ExportedSettings).GetProperty(nameof(AppSettings.LocalShell)));
    }

    [Fact]
    public void An_import_never_creates_a_local_profile_and_says_it_skipped_one()
    {
        var backend = new MockBackend();
        var local = backend.Save(LocalProfile());
        var file = new ExportFile
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            Profiles =
            [
                // Zwei Schreibweisen von „lokal“: beide werden übergangen.
                new ExportedProfile("x1", "Evil local", "cmd", 22, string.Empty, Protocol: "local"),
                new ExportedProfile("x2", "Evil LOCAL", "pwsh", 22, string.Empty, Protocol: " Local "),

                // Ein Server mit der Kennung eines lokalen Profils hier ersetzt es nicht – er wird ein neuer Server.
                new ExportedProfile(local.Id, "web01", "10.9.9.9", 22, "deploy"),

                // Und ein Server, der heißt und zeigt wie das lokale Profil, ist nicht dasselbe wie dieses.
                new ExportedProfile(null, "Command Prompt", "cmd", 22, "someone"),
            ],
        };

        var plan = DataExchange.Plan(file, backend.GetProfiles(), new AppSettings(), home: null, keyExists: _ => true);

        Assert.Equal(2, plan.SkippedLocal);
        Assert.Equal(0, plan.Skipped);
        Assert.Equal(2, plan.Profiles.Count);
        Assert.All(plan.Profiles, planned =>
        {
            Assert.Equal(ProfileChange.New, planned.Change);
            Assert.False(planned.Profile.IsLocal);
            Assert.NotEqual(local.Id, planned.Profile.Id);
        });
        Assert.Contains(ShellViewModel.DescribeImport(plan), line => line.Contains("2 local terminals", StringComparison.Ordinal));

        Assert.Empty(DataExchange.SaveProfiles(plan, backend));
        Assert.Equal(local, backend.AllProfiles.Single(profile => profile.Id == local.Id));
        Assert.Equal(1, backend.AllProfiles.Count(profile => profile.IsLocal));
        Assert.Equal(2, backend.AllProfiles.Count(profile => profile.Host is "10.9.9.9" or "cmd" && !profile.IsLocal));
    }

    [Fact]
    public void A_local_tab_shows_neither_files_nor_server_info_and_is_not_measured()
    {
        var shell = TestShell.Create(out var backend);
        var server = shell.OpenSession(backend.GetProfiles()[0]);
        server.Start();
        var local = shell.OpenLocalTerminal("cmd")!;
        local.Start();
        shell.IsSftpVisible = shell.IsSnippetsVisible = true;

        // Der lokale Tab ist aktiv: kein Dateibrowser, keine Server-Details, keine Messung, kein „Befehle am Server“ –
        // die Snippets aber schon: Sie laufen auch in einer Shell dieses Rechners.
        Assert.Same(local, shell.ActiveSession);
        Assert.True(shell.IsLocalActive);
        Assert.False(shell.ShowsFiles);
        Assert.True(shell.ShowsSnippets);
        Assert.False(shell.CanShowDetails);
        Assert.True(shell.CanRunSnippets);
        Assert.False(local.CanMeasure);
        Assert.False(local.Files.HasCommands);
        Assert.False(((MockServerMonitor)local.Connection.Monitor!).IsActive);

        // Der Server daneben behält alles.
        shell.ActiveSession = server;
        Assert.True(shell.ShowsFiles);
        Assert.True(shell.ShowsSnippets);
        Assert.True(((MockServerMonitor)server.Connection.Monitor!).IsActive);
        Assert.False(((MockServerMonitor)local.Connection.Monitor!).IsActive);

        // „Zwei Spalten“ liegt an der Stelle des Terminals – bei einem lokalen Tab bleibt das Terminal stehen.
        shell.IsDualFiles = true;
        Assert.True(shell.ShowsDualFiles);
        Assert.False(shell.ShowsTerminal);
        shell.ActiveSession = local;
        Assert.False(shell.ShowsDualFiles);
        Assert.True(shell.ShowsTerminal);
        Assert.False(shell.Commands.Single(command => command.Id == "session.attachMux").IsAvailable);
        Assert.False(shell.Commands.Single(command => command.Id == "session.createKey").IsAvailable);
    }

    [Fact]
    public void The_key_opens_the_chosen_shell_as_a_passing_profile_that_can_be_kept()
    {
        var shell = TestShell.Create(out var backend, new AppSettings { LocalShell = "cmd" });
        var open = shell.Commands.Single(command => command.Id == "session.newLocal");
        Assert.Equal(new KeyChord("KeyT"), open.Shortcut);

        Assert.True(shell.RunCommand("session.newLocal"));
        var session = shell.ActiveSession!;
        var profile = session.Profile;
        Assert.Equal((ServerProfile.Local, "cmd", true, true, "Command Prompt"), (profile.Protocol, profile.Host, profile.IsTransient, session.IsQuick, profile.Name));

        // Vorläufig wie beim Schnellverbinden: in keiner Liste, und mit dem Tab weg.
        Assert.DoesNotContain(backend.GetProfiles(), listed => listed.Id == profile.Id);
        Assert.DoesNotContain(shell.Profiles, listed => listed.Id == profile.Id);
        shell.CloseSession(session);
        Assert.DoesNotContain(backend.AllProfiles, listed => listed.Id == profile.Id);

        // „Behalten“ macht daraus ein gewöhnliches Profil.
        var again = shell.OpenLocalTerminal()!;
        Assert.True(shell.RunCommand("session.keep"));
        var kept = Assert.Single(backend.GetProfiles(), listed => listed.IsLocal);
        Assert.Equal((again.Profile.Id, false), (kept.Id, kept.IsTransient));
        shell.CloseSession(again);
        Assert.Contains(backend.GetProfiles(), listed => listed.Id == kept.Id);

        // Zwei Tabs zum selben gespeicherten lokalen Profil: Keiner wartet auf den anderen, keiner teilt eine Verbindung.
        var first = shell.OpenSession(kept);
        var second = shell.OpenSession(kept);
        first.Start();
        second.Start();
        Assert.NotNull(first.Connection);
        Assert.NotSame(first.Connection, second.Connection);
    }

    [Fact]
    public void Without_a_choice_the_first_shell_found_opens_and_a_setting_that_does_not_fit_is_ignored()
    {
        var shell = TestShell.Create(out _);
        Assert.Equal("pwsh", shell.OpenLocalTerminal()!.Profile.Host);

        // Gewählt, aber hier nicht (mehr) da: die bevorzugte.
        shell.DefaultLocalShell = "wsl:Gone";
        Assert.Equal("pwsh", shell.OpenLocalTerminal()!.Profile.Host);

        shell.DefaultLocalShell = "wsl:Ubuntu";
        Assert.Equal("wsl:Ubuntu", shell.OpenLocalTerminal()!.Profile.Host);

        // Was keine Kennung sein kann, wird nicht gemerkt.
        shell.DefaultLocalShell = "C:\\evil.exe";
        Assert.Equal(string.Empty, shell.DefaultLocalShell);
        Assert.Null(shell.OpenLocalTerminal("C:\\evil.exe"));
        Assert.Null(shell.OpenLocalTerminal("pwsh -c calc"));

        // Ohne Antwort des Kerns die Shell, die es auf jedem System gibt.
        var scripted = new ScriptedBackend();
        var bare = TestShell.Create(scripted);
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell" : "login", bare.OpenLocalTerminal()!.Profile.Host);
    }

    [Fact]
    public void The_choice_in_the_settings_is_a_row_that_goes_back_to_automatic()
    {
        var shell = TestShell.Create(out _);
        Assert.Equal("Automatic (the first one found)", shell.LocalShellChoices[0].DisplayName);
        Assert.Equal(["Automatic (the first one found)", "PowerShell 7", "Command Prompt", "WSL: Ubuntu"], shell.LocalShellChoices.Select(choice => choice.DisplayName));
        Assert.True(shell.IsDefaultSetting("LocalShell"));
        Assert.Contains("LocalShell", shell.ResettableSettings);

        shell.DefaultLocalShellIndex = 2;
        Assert.Equal("cmd", shell.Settings.LocalShell);
        Assert.False(shell.IsDefaultSetting("LocalShell"));

        Assert.True(shell.ResetSetting("LocalShell"));
        Assert.Equal((string.Empty, 0), (shell.Settings.LocalShell, shell.DefaultLocalShellIndex));

        // Eine Shell, die gewählt war und fehlt, bleibt sichtbar – die Wahl verschwindet nicht still.
        shell.Settings.LocalShell = "wsl:Gone";
        Assert.Equal("WSL: Gone", shell.LocalShellChoices[shell.DefaultLocalShellIndex].DisplayName);

        // Aus einer fremden oder kaputten Datei der Einstellungen kommt keine Kennung, die der Kern ablehnt.
        Assert.Equal(string.Empty, new AppSettings { LocalShell = "evil --flag" }.Sanitize().LocalShell);
        Assert.Equal(string.Empty, new AppSettings { LocalShell = null! }.Sanitize().LocalShell);
        Assert.Equal("wsl:Ubuntu", new AppSettings { LocalShell = "wsl:Ubuntu" }.Sanitize().LocalShell);
    }

    [Fact]
    public void Each_shell_found_has_its_own_entry_in_the_palette()
    {
        var shell = TestShell.Create(out _);

        var titles = shell.SearchPalette("local").Select(item => item.Title).ToList();
        Assert.Contains("New local terminal", titles);
        Assert.Contains("Local terminal: PowerShell 7", titles);
        Assert.Contains("Local terminal: Command Prompt", titles);
        Assert.Contains("Local terminal: WSL: Ubuntu", titles);

        // Auch nur unter den Befehlen, und ein Eintrag öffnet genau seine Shell.
        var entry = Assert.Single(shell.SearchPalette(">Command Prompt"), item => item.Kind == PaletteKind.Command);
        entry.Run();
        Assert.Equal("cmd", shell.ActiveSession!.Profile.Host);
        Assert.True(shell.ActiveSession.Profile.IsLocal);

        // Findet der Kern keine, gibt es nur den Befehl für die eine, die es überall gibt.
        var bare = TestShell.Create(new ScriptedBackend());
        Assert.DoesNotContain(bare.SearchPalette("local"), item => item.Title.StartsWith("Local terminal: ", StringComparison.Ordinal));
        Assert.Contains(bare.SearchPalette("local"), item => item.Title == "New local terminal");
    }

    [Fact]
    public async Task The_core_is_told_protocol_local_the_shell_and_the_folder()
    {
        var transport = new FakeTransport();
        var folder = LocalPath.Of("work", "project");
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"PowerShell 7","host":"pwsh","port":22,"user":"","group":"","tags":[],"favorite":false,"use_agent":false,"protocol":"local","local_dir":""" + System.Text.Json.JsonSerializer.Serialize(folder) + "}}";
        transport.Responses["local.shells"] = """{"ok":true,"shells":[{"id":"pwsh","name":"PowerShell 7"},{"id":"C:\\evil.exe","name":"evil"},{"id":"wsl:Ubuntu","name":"WSL: Ubuntu"},{"id":"cmd"}]}""";
        using var backend = new CoreBackend(transport);

        var saved = backend.Save(LocalProfile("pwsh") with { Name = "PowerShell 7", LocalDirectory = folder });

        var sent = Assert.Single(transport.Sent("profiles.save")).GetProperty("profile");
        Assert.Equal(("local", "pwsh", folder), (sent.GetProperty("protocol").GetString(), sent.GetProperty("host").GetString(), sent.GetProperty("local_dir").GetString()));
        Assert.Equal((true, folder), (saved.IsLocal, saved.LocalDirectory));

        // Der Startordner gehört nur zu einem lokalen Terminal.
        backend.Save(new ServerProfile(string.Empty, "web", "h", 22, "u", string.Empty, [], LocalDirectory: folder));
        Assert.False(transport.Sent("profiles.save").Last().GetProperty("profile").TryGetProperty("local_dir", out _));

        // Die Liste der Shells kommt asynchron; was keine Kennung sein kann, bleibt draußen, und ohne Namen gilt die Kennung.
        var shells = await backend.LocalShellsAsync(TestContext.Current.CancellationToken);
        Assert.Single(transport.Sent("local.shells"));
        Assert.Equal([("pwsh", "PowerShell 7"), ("wsl:Ubuntu", "WSL: Ubuntu"), ("cmd", "cmd")], shells.Select(shell => (shell.Id, shell.Name)));

        // Eine Antwort mit Fehler ergibt keine Shells und eine Zeile im Protokoll.
        List<string> warnings = [];
        backend.Warning += warnings.Add;
        transport.Responses["local.shells"] = """{"ok":false,"error":"internal"}""";
        Assert.Empty(await backend.LocalShellsAsync(TestContext.Current.CancellationToken));
        Assert.Contains(warnings, warning => warning.StartsWith("local.shells", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_start_folder_is_explained_in_the_users_language_with_the_profile_named_as_plain_text()
    {
        var transport = new FakeTransport();
        using var backend = new CoreBackend(transport);
        List<string> warnings = [];
        backend.Warning += warnings.Add;

        transport.Raise("""{"event":"core.warning","code":"local_dir_missing","message":"dev\u001b[31m: the start folder is missing or did not answer in time; the shell starts in the home folder"}""");

        var warning = Assert.Single(warnings);
        Assert.StartsWith("The start folder of the local terminal is missing", warning);
        Assert.Contains("dev", warning);
        Assert.DoesNotContain('\u001b', warning);
    }

    [Fact]
    public void The_shells_come_from_the_backend_when_the_shell_starts()
    {
        var shell = TestShell.Create(out _);
        Assert.Equal(["pwsh", "cmd", "wsl:Ubuntu"], shell.LocalShells.Select(found => found.Id));
    }
}
