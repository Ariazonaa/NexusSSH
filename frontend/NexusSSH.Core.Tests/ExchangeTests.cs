using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Export und Import von Profilen, gespeicherten Befehlen und Einstellungen: was in der Datei steht (und was nie),
/// wie sie mit dem abgeglichen wird, was es auf dem anderen Rechner schon gibt, und der Weg durch die App.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ExchangeTests : IDisposable
{
    private static readonly string HomeA = LocalPath.Of("Users", "anna");
    private static readonly string HomeB = LocalPath.Of("Users", "ben");

    private static readonly ServerProfile Bastion = new("b1", "bastion", "bastion.example.org", 2222, "jump", "Production", ["edge"], IsFavorite: true, UseAgent: false);

    private static readonly ServerProfile Web = new(
        "w1",
        "web01",
        "10.0.1.10",
        22,
        "deploy",
        "Production",
        ["nginx", "eu"],
        KeyPath: Path.Combine(HomeA, ".ssh", "id_web"),
        LastConnected: DateTimeOffset.FromUnixTimeSeconds(1_790_000_000),
        JumpId: "b1",
        ForwardAgent: true,
        HasSavedSecret: true);

    private readonly TempFolder _temp = new();

    public ExchangeTests() => L.Init(AppLanguage.English);

    public void Dispose() => _temp.Dispose();

    private static AppSettings SettingsA() => new()
    {
        Language = AppLanguage.German,
        TerminalTheme = TerminalThemes.All[^1].Id,
        TerminalFontSize = 17,
        ShowHiddenFiles = false,
        ProfileSort = ProfileSort.Recent,
        RestoreTabs = true,
        EditorPath = @"C:\Tools\editor.exe",
        LastDownloadFolder = @"D:\Downloads",
        SftpWidth = 400,
        OpenTabs = ["w1"],
        Snippets = [new Snippet("logs", "journalctl -fu nginx", Run: true), new Snippet("edit", "sudo nano ", Run: false)],
    };

    private static ExportFile Exported() => DataExchange.Create([Bastion, Web], SettingsA(), "0.8.0", DateTimeOffset.FromUnixTimeSeconds(1_791_000_000), HomeA);

    private static ImportPlan Plan(ExportFile file, IReadOnlyList<ServerProfile>? existing = null, AppSettings? settings = null)
        => DataExchange.Plan(file, existing ?? [], settings ?? new AppSettings(), HomeB, keyExists: _ => true);

    [Fact]
    public void The_file_holds_profiles_commands_and_portable_settings_but_nothing_secret_or_local()
    {
        var json = DataExchange.Serialize(Exported());
        var file = DataExchange.Parse(json);

        Assert.Equal((ExportFile.FormatName, ExportFile.CurrentVersion, "0.8.0"), (file.Format, file.Version, file.App));
        Assert.Equal(["bastion", "web01"], file.Profiles!.Select(profile => profile.Name));
        var web = file.Profiles![1];
        // Agent Forwarding steht nicht in der Datei, auch wenn es am Profil eingeschaltet ist.
        Assert.Equal(("w1", "10.0.1.10", 22, "deploy", "Production", "b1", false), (web.Id, web.Host, web.Port, web.User, web.Group, web.Jump, web.ForwardAgent));
        Assert.Equal(["nginx", "eu"], web.Tags);
        Assert.Equal(["logs", "edit"], file.Snippets!.Select(snippet => snippet.Name));
        Assert.Equal(new ExportedSettings("German", TerminalThemes.All[^1].Id, 17, false, "Recent", true), file.Settings);

        // Der Schlüssel liegt im Benutzerordner: In der Datei steht er so, dass er auch woanders stimmt.
        Assert.Equal("~/.ssh/id_web", web.KeyPath);

        // Was zum Rechner gehört oder nur eine Auskunft ist, wandert nicht mit – Geheimnisse gibt es hier ohnehin nicht.
        foreach (var absent in new[] { "password", "passphrase", "secret", "lastConnected", "editor", "download", "sftpWidth", "openTabs", "anna" })
        {
            Assert.DoesNotContain(absent, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("not json at all", ExchangeError.Malformed)]
    [InlineData("""{"format":"nexusssh-export","version":1,"profiles":"oops"}""", ExchangeError.Malformed)]
    [InlineData("{}", ExchangeError.NotAnExport)]
    [InlineData("null", ExchangeError.NotAnExport)]
    [InlineData("""{"format":"something-else","version":1}""", ExchangeError.NotAnExport)]
    [InlineData("""{"format":"nexusssh-export","version":99,"app":"9.0.0"}""", ExchangeError.TooNew)]
    public void A_file_that_is_no_export_is_refused_with_the_reason(string json, ExchangeError expected)
    {
        Assert.Equal(expected, Assert.Throws<ExchangeException>(() => DataExchange.Parse(json)).Error);
    }

    [Fact]
    public void A_file_with_only_the_header_imports_nothing()
    {
        var plan = Plan(DataExchange.Parse("""{"format":"nexusssh-export","version":1}"""));

        Assert.Empty(plan.Profiles);
        Assert.Null(plan.Settings);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void On_an_empty_installation_everything_is_new_and_keeps_its_identity()
    {
        var plan = Plan(Exported());

        Assert.Equal((2, 0, 0), (plan.NewProfiles, plan.ChangedProfiles, plan.UnchangedProfiles));
        var web = plan.Profiles[1].Profile;

        // Dieselbe Kennung wie auf dem anderen Rechner: So bleibt der Verweis auf den Sprung-Server gültig, und ein
        // späterer Import erkennt das Profil wieder.
        Assert.Equal(("w1", "b1"), (web.Id, web.JumpId));
        Assert.Equal(Path.Combine(HomeB, ".ssh", "id_web"), web.KeyPath);
        Assert.False(web.ForwardAgent, "was eine Verbindung schwächt, kommt nie aus einer Datei");

        // Wann zuletzt verbunden wurde und ob ein Passwort gespeichert ist, weiß nur der andere Rechner.
        Assert.Null(web.LastConnected);
        Assert.False(web.HasSavedSecret);
        Assert.Equal((2, 0), (plan.NewSnippets, plan.ChangedSnippets));
    }

    [Fact]
    public void What_is_already_here_is_recognised_and_only_differences_count()
    {
        var mine = Web with { KeyPath = Path.Combine(HomeB, ".ssh", "id_web") };
        var plan = Plan(Exported(), [Bastion with { Port = 22 }, mine], new AppSettings { Snippets = [new Snippet("logs", "tail -f app.log", Run: false), new Snippet("mine", "htop", Run: true)] });

        Assert.Equal((0, 1, 1), (plan.NewProfiles, plan.ChangedProfiles, plan.UnchangedProfiles));
        Assert.Equal(ProfileChange.Changed, plan.Profiles[0].Change);
        Assert.Equal(2222, plan.Profiles[0].Profile.Port);

        // Ein unverändertes Profil behält, was nur dieser Rechner weiß.
        Assert.Equal(mine.LastConnected, plan.Profiles[1].Profile.LastConnected);
        Assert.True(plan.Profiles[1].Profile.HasSavedSecret);

        // Befehle gleichen Namens werden ersetzt, eigene bleiben, neue kommen dazu.
        Assert.Equal((1, 1), (plan.NewSnippets, plan.ChangedSnippets));
        Assert.Equal(["logs", "mine", "edit"], plan.Snippets.Select(snippet => snippet.Name));
        Assert.Equal("journalctl -fu nginx", plan.Snippets[0].Text);
    }

    [Fact]
    public void The_same_server_set_up_by_hand_on_both_computers_is_replaced_not_duplicated()
    {
        // Hier hat der Sprung-Server eine andere Kennung – er wurde auf diesem Rechner eigens angelegt.
        var local = Bastion with { Id = "local-7", Name = "Bastion", Host = "BASTION.example.org", Group = "old" };
        var plan = Plan(Exported(), [local]);

        Assert.Equal((1, 1, 0), (plan.NewProfiles, plan.ChangedProfiles, plan.UnchangedProfiles));
        Assert.Equal(("local-7", "Production"), (plan.Profiles[0].Profile.Id, plan.Profiles[0].Profile.Group));

        // Und das Profil, das über ihn läuft, verweist auf die Kennung von hier.
        Assert.Equal("local-7", plan.Profiles[1].Profile.JumpId);
    }

    [Fact]
    public void A_jump_host_that_exists_nowhere_is_dropped_and_unusable_entries_are_skipped()
    {
        var file = DataExchange.Parse(
            """
            {
              "format": "nexusssh-export", "version": 1,
              "profiles": [
                { "id": "a", "name": "alpha", "host": "a.example.org", "port": 22, "user": "root", "jump": "gone" },
                { "id": "a", "name": "again", "host": "other.example.org", "port": 22, "user": "root" },
                { "id": "b", "name": "self", "host": "b.example.org", "port": 22, "user": "root", "jump": "b" },
                { "id": "c", "name": "known", "host": "c.example.org", "port": 22, "user": "root", "jump": "here" },
                { "id": "../../x", "host": "d.example.org", "port": 22, "user": "root" },
                { "id": "e", "name": "no host", "port": 22, "user": "root" },
                { "id": "f", "name": "no user", "host": "f.example.org", "port": 22 },
                { "id": "g", "name": "bad port", "host": "g.example.org", "port": 70000, "user": "root" }
              ]
            }
            """);
        var here = new ServerProfile("here", "here", "here.example.org", 22, "root", string.Empty, []);

        var plan = Plan(file, [here]);

        Assert.Equal(["alpha", "self", "known", "d.example.org"], plan.Profiles.Select(profile => profile.Profile.Name));
        Assert.Equal([null, null, "here", null], plan.Profiles.Select(profile => profile.Profile.JumpId));

        // Eine Kennung mit anderen Zeichen als Buchstaben, Ziffern, - und _ wird nicht übernommen; das Profil
        // bekommt beim Speichern eine neue.
        Assert.Equal(string.Empty, plan.Profiles[3].Profile.Id);
        Assert.Equal(4, plan.Skipped);
    }

    [Fact]
    public void A_key_file_missing_on_this_computer_is_named()
    {
        var plan = DataExchange.Plan(Exported(), [], new AppSettings(), HomeB, keyExists: _ => false);

        Assert.Equal(["web01"], plan.MissingKeys);
        Assert.Contains("web01", string.Join('\n', ShellViewModel.DescribeImport(plan)));
    }

    [Theory]
    [InlineData("id_ed25519", true, "~/.ssh/id_ed25519")]
    [InlineData(null, false, null)]
    [InlineData("  ", false, null)]
    public void Key_paths_below_the_home_folder_travel_as_tilde_paths(string? name, bool below, string? expected)
    {
        var path = name is null || !below ? name : Path.Combine(HomeA, ".ssh", name);

        Assert.Equal(expected, DataExchange.PortableKeyPath(path, HomeA));
        if (expected is not null)
        {
            Assert.Equal(Path.Combine(HomeB, ".ssh", name!), DataExchange.LocalKeyPath(expected, HomeB));
        }
    }

    [Fact]
    public void Key_paths_elsewhere_stay_as_they_are()
    {
        var elsewhere = LocalPath.Of("keys", "id_web");
        var sibling = HomeA + "-backup" + Path.DirectorySeparatorChar + "id";

        Assert.Equal(elsewhere, DataExchange.PortableKeyPath(elsewhere, HomeA));
        Assert.Equal(sibling, DataExchange.PortableKeyPath(sibling, HomeA));
        Assert.Equal(elsewhere, DataExchange.LocalKeyPath(elsewhere, HomeB));
        Assert.Equal("~/.ssh/id", DataExchange.LocalKeyPath("~/.ssh/id", home: null));
    }

    [Fact]
    public void Settings_this_version_does_not_know_are_passed_over()
    {
        var (language, theme, fontSize, showHidden, sort, restoreTabs) = DataExchange.Resolve(
            new ExportedSettings("Klingon", "neon-pink", 400, null, "ByMood", null));

        Assert.Null(language);
        Assert.Null(theme);
        Assert.Equal(TerminalThemes.MaxFontSize, fontSize);
        Assert.Null(showHidden);
        Assert.Null(sort);
        Assert.Null(restoreTabs);
    }

    // --- Der Weg durch die App: auf einem Rechner exportieren, auf einem anderen importieren ---

    private static ShellViewModel Machine(string home, AppSettings settings, params ServerProfile[] profiles)
    {
        var shell = TestShell.Create(out var backend, settings);
        foreach (var demo in backend.GetProfiles())
        {
            backend.Delete(demo.Id);
        }

        foreach (var profile in profiles)
        {
            backend.Save(profile);
        }

        shell.HomeDirectory = home;
        return shell;
    }

    [Fact]
    public void An_export_from_one_computer_arrives_on_another()
    {
        var path = Path.Combine(_temp.Path, "export.json");
        var first = Machine(HomeA, SettingsA(), Bastion, Web);
        Assert.True(first.ExportData(path));
        Assert.Contains("Exported 2 profiles, 2 saved commands", first.Output[^1].Text);

        var second = Machine(HomeB, new AppSettings());
        var plan = second.PrepareImport(path, out var error);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Empty(second.Profiles);

        second.ImportData(plan, new ImportChoice());

        Assert.Equal(["bastion", "web01"], second.Profiles.Select(profile => profile.Name));
        var web = second.Profiles.Single(profile => profile.Name == "web01");
        Assert.Equal(("w1", "b1", Path.Combine(HomeB, ".ssh", "id_web")), (web.Id, web.JumpId, web.KeyPath));
        Assert.Equal(["logs", "edit"], second.Snippets.Select(snippet => snippet.Name));
        Assert.Equal((TerminalThemes.All[^1].Id, 17, false, ProfileSort.Recent, true), (second.TerminalTheme.Id, second.TerminalFontSize, second.ShowHiddenFiles, second.ProfileSort, second.RestoreTabs));
        Assert.Equal(AppLanguage.German, second.Settings.Language);
        Assert.Contains("Imported 2 new and 0 changed profiles, 2 saved commands.", second.Output.Select(entry => entry.Text));

        // Was zum ersten Rechner gehört, ist nicht mitgekommen.
        Assert.Equal((string.Empty, string.Empty, AppSettings.DefaultSftpWidth), (second.Settings.EditorPath, second.Settings.LastDownloadFolder, second.Settings.SftpWidth));

        // Noch einmal dieselbe Datei: nichts Neues.
        var again = second.PrepareImport(path, out _);
        Assert.Equal((0, 0, 2, 0, 0), (again!.NewProfiles, again.ChangedProfiles, again.UnchangedProfiles, again.NewSnippets, again.ChangedSnippets));
    }

    [Fact]
    public void Shortcuts_of_ones_own_travel_with_the_settings_and_a_foreign_file_cannot_bind_anything()
    {
        var path = Path.Combine(_temp.Path, "shortcuts.json");
        var first = Machine(HomeA, new AppSettings());
        Assert.Null(first.SetShortcut("view.settings", new KeyChord("F9", Shift: false)));
        Assert.Null(first.SetShortcut("session.close", null));
        Assert.True(first.ExportData(path));
        Assert.Contains("\"view.settings\": \"F9\"", File.ReadAllText(path));

        var second = Machine(HomeB, new AppSettings());
        Assert.Null(second.SetShortcut("view.home", new KeyChord("F10", Shift: false)));
        second.ImportData(second.PrepareImport(path, out _)!, new ImportChoice());

        // Die Kürzel der Datei ersetzen die eigenen: F9 öffnet die Einstellungen, „Tab schließen“ hat keines mehr.
        Assert.Equal(new KeyChord("F9", Shift: false), second.Commands.Single(command => command.Id == "view.settings").Shortcut);
        Assert.Null(second.Commands.Single(command => command.Id == "session.close").Shortcut);
        Assert.Null(second.Commands.Single(command => command.Id == "view.home").Shortcut);
        Assert.Equal(["session.close", "view.settings"], second.Settings.Shortcuts.Keys.Order(StringComparer.Ordinal));

        // Ohne die Einstellungen bleiben die Kürzel, wie sie sind.
        var third = Machine(HomeB, new AppSettings());
        third.ImportData(third.PrepareImport(path, out _)!, new ImportChoice(Settings: false));
        Assert.Empty(third.Settings.Shortcuts);

        // Eine Datei von irgendwoher: unbekannte Befehle und Tasten, die der Shell gehören, zählen nicht.
        var fourth = Machine(HomeB, new AppSettings());
        fourth.ImportShortcuts(new Dictionary<string, string>
        {
            ["no.such.command"] = "F9",
            ["view.settings"] = "KeyC",
            ["view.home"] = "Shift+F8",
            ["session.close"] = "not a key",
        });
        Assert.Equal(["view.home"], fourth.Settings.Shortcuts.Keys);
        Assert.Equal(new KeyChord("F8"), fourth.Commands.Single(command => command.Id == "view.home").Shortcut);
    }

    [Fact]
    public void Folder_bookmarks_travel_with_their_profile_and_join_the_ones_already_there()
    {
        var path = Path.Combine(_temp.Path, "bookmarks.json");
        var first = Machine(HomeA, new AppSettings(), Bastion, Web);
        FolderBookmarks.Add(first.Settings, "w1", "/var/www");
        FolderBookmarks.Add(first.Settings, "w1", "/etc/nginx");
        Assert.True(first.ExportData(path));

        // Ein Rechner, der das Profil noch nicht hat: Die Lesezeichen kommen mit ihm an.
        var second = Machine(HomeB, new AppSettings());
        var plan = second.PrepareImport(path, out _)!;
        Assert.Equal(2, plan.NewBookmarks);
        Assert.Contains("Folder bookmarks: 2 new (they come with the profiles)", ShellViewModel.DescribeImport(plan));
        second.ImportData(plan, new ImportChoice());
        Assert.Equal(["/var/www", "/etc/nginx"], second.Settings.Bookmarks["w1"]);

        // Einer, der es schon hat – mit einem eigenen Lesezeichen und einem gleichen: Nur das Fehlende kommt dazu,
        // und allein dafür lohnt der Import.
        var third = Machine(HomeB, new AppSettings(), Bastion, Web with { KeyPath = Path.Combine(HomeB, ".ssh", "id_web") });
        FolderBookmarks.Add(third.Settings, "w1", "/srv/own");
        FolderBookmarks.Add(third.Settings, "w1", "/etc/nginx");
        plan = third.PrepareImport(path, out _)!;
        Assert.Equal((0, 0, 1, true), (plan.NewProfiles, plan.ChangedProfiles, plan.NewBookmarks, plan.HasChanges));
        third.ImportData(plan, new ImportChoice());
        Assert.Equal(["/srv/own", "/etc/nginx", "/var/www"], third.Settings.Bookmarks["w1"]);

        // Ohne die Profile bleiben auch ihre Lesezeichen draußen; und ein Pfad, der keiner ist, zählt nicht.
        var fourth = Machine(HomeB, new AppSettings());
        fourth.ImportData(fourth.PrepareImport(path, out _)!, new ImportChoice(Profiles: false));
        Assert.Empty(fourth.Settings.Bookmarks);
        var odd = DataExchange.Plan(
            DataExchange.Parse("""{"format":"nexusssh-export","version":1,"profiles":[{"id":"x1","name":"x","host":"h","port":22,"user":"u","bookmarks":["relative","/ok/../fine",""]}]}"""),
            [],
            new AppSettings(),
            HomeB,
            keyExists: _ => true);
        Assert.Equal(["/fine"], odd.Bookmarks["x1"]);
    }

    [Fact]
    public void Only_the_chosen_parts_are_taken_over()
    {
        var path = Path.Combine(_temp.Path, "export.json");
        Machine(HomeA, SettingsA(), Bastion, Web).ExportData(path);
        var second = Machine(HomeB, new AppSettings());
        var plan = second.PrepareImport(path, out _);

        second.ImportData(plan!, new ImportChoice(Profiles: false, Snippets: true, Settings: false));

        Assert.Empty(second.Profiles);
        Assert.Equal(2, second.Snippets.Count);
        Assert.Equal((TerminalThemes.DefaultId, TerminalThemes.DefaultFontSize, AppLanguage.Auto), (second.TerminalTheme.Id, second.TerminalFontSize, second.Settings.Language));
        Assert.Contains("Imported 0 new and 0 changed profiles, 2 saved commands.", second.Output.Select(entry => entry.Text));
    }

    [Fact]
    public void A_file_that_is_no_export_says_so_and_changes_nothing()
    {
        var shell = Machine(HomeB, new AppSettings(), Bastion);
        var path = _temp.File("holiday.json", """{"photos":[1,2,3]}""");

        Assert.Null(shell.PrepareImport(path, out var error));
        Assert.Equal("This is not a NexusSSH export file.", error);

        Assert.Null(shell.PrepareImport(Path.Combine(_temp.Path, "missing.json"), out error));
        Assert.Equal("The file cannot be read.", error);
        Assert.Single(shell.Profiles);
    }

    [Fact]
    public void A_profile_the_backend_refuses_is_reported_and_the_rest_arrives()
    {
        var store = new PickyStore("bastion");
        var plan = Plan(Exported());

        var refused = DataExchange.SaveProfiles(plan, store);

        Assert.Equal(["web01"], store.Saved.Select(profile => profile.Name));
        var (profile, reason) = Assert.Single(refused);
        Assert.Equal(("bastion", "invalid_profile: host"), (profile.Profile.Name, reason));
    }

    [Fact]
    public void The_export_is_offered_as_a_command()
    {
        var shell = Machine(HomeA, new AppSettings());
        List<UiRequest> asked = [];
        shell.UiRequested += asked.Add;

        Assert.True(shell.RunCommand("data.export"));
        Assert.True(shell.RunCommand("data.import"));

        Assert.Equal([UiRequest.ExportData, UiRequest.ImportData], asked);
        Assert.Equal("nexusssh-export-2026-10-02", ShellViewModel.ExportFileName(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 2, 12, 0, 0)))));
    }

    /// <summary>Ein Profilspeicher, der ein bestimmtes Profil ablehnt – wie der Kern einen unzulässigen Hostnamen.</summary>
    private sealed class PickyStore(string refusedName) : IProfileStore
    {
        public List<ServerProfile> Saved { get; } = [];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ServerProfile> GetProfiles() => Saved;

        public ServerProfile Save(ServerProfile profile)
        {
            if (profile.Name == refusedName)
            {
                throw new BackendException("invalid_profile", "host");
            }

            Saved.Add(profile);
            return profile;
        }

        public void Delete(string id)
        {
        }

        public SshConfigListing ReadSshConfig() => new(null, []);

        public IReadOnlyList<KnownHost> KnownHosts() => [];

        public bool ForgetHost(string name, string? authority = null) => false;

        public KnownHost TrustAuthority(string patterns, string key) => new(patterns, "ssh-ed25519", "SHA256:test", true);

        public int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false) => 0;

        public GeneratedKey GenerateKey(string name, string? passphrase, string comment) => new(name, "ssh-ed25519 AAAA test");

        public void ForgetSecrets(string id)
        {
        }
    }
}
