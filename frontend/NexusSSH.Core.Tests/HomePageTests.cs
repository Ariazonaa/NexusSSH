using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Die Startseite mit den Server-Kacheln – und die Akzentfarbe, die alles Hervorgehobene färbt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class HomePageTests
{
    [Fact]
    public void Favorites_get_their_own_section_before_the_groups()
    {
        // Beispieldaten: web01 (Favorit) und database01 in „Production“, staging und test01 in „Development“.
        var shell = TestShell.Create(out _);

        Assert.Equal(["Favorites", "Production", "Development"], shell.HomeGroups.Select(group => group.Name));
        Assert.Equal(["web01"], shell.HomeGroups[0].Select(profile => profile.Name));
        Assert.Equal(["database01"], shell.HomeGroups[1].Select(profile => profile.Name));

        // Jedes Profil steht genau einmal da.
        Assert.Equal(shell.Profiles.Count, shell.HomeGroups.Sum(group => group.Count));
        Assert.False(shell.HasNoHomeMatches);
    }

    [Fact]
    public void The_filter_keeps_what_carries_every_word_in_name_host_user_group_or_tag()
    {
        var shell = TestShell.Create(out _);

        shell.HomeFilter = "development";
        Assert.Equal(["Development"], shell.HomeGroups.Select(group => group.Name));
        Assert.Equal(2, shell.HomeGroups[0].Count);

        // Zwei Wörter: beide müssen vorkommen, egal wo und in welcher Schreibweise.
        shell.HomeFilter = "  WEB01   ubuntu ";
        Assert.Equal(["web01"], shell.HomeGroups.SelectMany(group => group).Select(profile => profile.Name));

        shell.HomeFilter = "web01 development";
        Assert.Empty(shell.HomeGroups);
        Assert.True(shell.HasNoHomeMatches);

        shell.HomeFilter = string.Empty;
        Assert.Equal(shell.Profiles.Count, shell.HomeGroups.Sum(group => group.Count));

        // Die Liste im Aufklapper der Titelleiste bleibt vom Filter unberührt.
        shell.HomeFilter = "nichts";
        Assert.Equal(shell.Profiles.Count, shell.ProfileGroups.Sum(group => group.Count));
    }

    [Fact]
    public void The_third_line_of_a_card_names_the_tags()
    {
        Assert.Equal(string.Empty, Format.ProfileExtras([]));
        Assert.Equal("web, nginx", Format.ProfileExtras(["web", "nginx"]));
    }

    [Fact]
    public void The_terminal_font_reaches_the_page_only_as_a_plain_name()
    {
        Assert.Equal("Cascadia Mono", TerminalThemes.Fonts[0]);
        Assert.Equal("JetBrains Mono", TerminalThemes.Font("  JetBrains Mono "));
        Assert.Equal("Fira-Code.Retina", TerminalThemes.Font("Fira-Code.Retina"));

        // Der Name landet in einer CSS-Angabe der Terminal-Seite: nichts, was dort mehr wäre als ein Name.
        Assert.Equal("Cascadia Mono", TerminalThemes.Font("x\"; } body { display:none"));
        Assert.Equal("Cascadia Mono", TerminalThemes.Font("a, url(http://example.org)"));
        Assert.Equal("Cascadia Mono", TerminalThemes.Font(new string('a', 65)));
        Assert.Equal("Cascadia Mono", TerminalThemes.Font(null));

        var settings = new AppSettings { TerminalFont = "bad;name" };
        Assert.Equal("Cascadia Mono", settings.Sanitize().TerminalFont);

        var shell = TestShell.Create(out _, settings);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.TerminalFont = "Consolas";
        shell.TerminalFont = "Consolas";
        shell.MouseCopyPaste = false;
        Assert.Equal(("Consolas", false), (settings.TerminalFont, settings.MouseCopyPaste));
        Assert.Equal([nameof(shell.TerminalFont), nameof(shell.MouseCopyPaste)], changed);

        var message = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], "evil\"font", mouseCopyPaste: true)).RootElement;
        Assert.Equal("Cascadia Mono", message.GetProperty("fontFamily").GetString());
        Assert.True(message.GetProperty("mouseCopyPaste").GetBoolean());
    }

    [Fact]
    public void Highlight_rules_are_typed_one_per_line_and_what_is_no_rule_never_reaches_the_page()
    {
        const string Text = "red: ERROR\n\n  gelb : WARN \ncyan: /\\b\\d+\\.\\d+\\b/\nGREEN: /ok/i\npink: nope\nno color\nblue: /(/\nred:\nblue: a/b";

        Assert.Equal(
            [
                new HighlightRule("ERROR", Regex: false, IgnoreCase: false, "red"),
                new HighlightRule("WARN", Regex: false, IgnoreCase: false, "yellow"),
                new HighlightRule(@"\b\d+\.\d+\b",Regex: true, IgnoreCase: false, "cyan"),
                new HighlightRule("ok", Regex: true, IgnoreCase: true, "green"),
                new HighlightRule("a/b", Regex: false, IgnoreCase: false, "blue"),
            ],
            TerminalHighlights.Parse(Text));
        Assert.Equal(4, TerminalHighlights.Ignored(Text));
        Assert.Empty(TerminalHighlights.Parse(null));

        // Mehr als die Höchstzahl an Regeln und ein überlanger Text werden gekappt.
        var many = string.Join('\n', Enumerable.Range(0, TerminalHighlights.MaxRules + 3).Select(index => $"red: word{index}"));
        Assert.Equal(TerminalHighlights.MaxRules, TerminalHighlights.Parse(many).Count);
        Assert.Equal(3, TerminalHighlights.Ignored(many));
        Assert.Equal(TerminalHighlights.MaxTextLength, new AppSettings { Highlights = new string('x', 5000) }.Sanitize().Highlights.Length);

        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        L.Init(AppLanguage.English);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.Highlights = "red: ERROR\nnonsense";
        shell.Highlights = "red: ERROR\nnonsense";

        Assert.Equal("red: ERROR\nnonsense", settings.Highlights);
        Assert.Equal([nameof(shell.Highlights), nameof(shell.HighlightHint)], changed);
        Assert.Equal("1 line(s) are no rule and are skipped.", shell.HighlightHint);

        var message = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], highlights: shell.HighlightRules)).RootElement;
        var rule = Assert.Single(message.GetProperty("highlights").EnumerateArray());
        Assert.Equal(("ERROR", false, false, "red"), (rule.GetProperty("pattern").GetString(), rule.GetProperty("regex").GetBoolean(), rule.GetProperty("ignoreCase").GetBoolean(), rule.GetProperty("color").GetString()));

        // Ohne Regeln kommt eine leere Liste – die Seite räumt ihre damit ab.
        Assert.Equal(0, JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [])).RootElement.GetProperty("highlights").GetArrayLength());
    }

    [Fact]
    public void The_terminal_page_gets_the_saved_commands_to_suggest_unless_that_is_switched_off()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        shell.SetSnippets([new Snippet("Logs", "docker compose logs -f", Run: true), new Snippet("Huge", new string('x', TerminalMessages.MaxSuggestionLength + 1), Run: false)]);

        // Ein Befehl, der zu lang ist, um ihn sinnvoll vorzuschlagen, bleibt hier.
        var message = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], suggestions: shell.TerminalSuggestions)).RootElement;
        var suggestion = Assert.Single(message.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(("Logs", "docker compose logs -f"), (suggestion.GetProperty("name").GetString(), suggestion.GetProperty("text").GetString()));

        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.SnippetSuggestions = false;
        shell.SnippetSuggestions = false;

        Assert.False(settings.SnippetSuggestions);
        Assert.Equal([nameof(shell.SnippetSuggestions)], changed);
        Assert.Empty(shell.TerminalSuggestions);

        // Abgeschaltet kommt eine leere Liste – die Seite vergisst damit, was sie hatte.
        Assert.Equal(0, JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], suggestions: shell.TerminalSuggestions)).RootElement.GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public void The_file_list_sorts_by_name_size_or_date_with_folders_always_first()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        var files = session.Files;
        var byName = files.Entries.Select(entry => entry.Name).ToList();
        Assert.Contains(files.Entries, entry => entry.IsDirectory);
        Assert.Contains(files.Entries, entry => !entry.IsDirectory);

        void FoldersFirst() => Assert.Equal(
            files.Entries.Select(entry => entry.IsDirectory),
            files.Entries.Select(entry => entry.IsDirectory).OrderByDescending(isDirectory => isDirectory));

        shell.FileSort = FileSort.Size;
        FoldersFirst();
        var sizes = files.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Size).ToList();
        Assert.Equal(sizes.Order(), sizes);

        shell.FileSortDescending = true;
        FoldersFirst();
        sizes = [.. files.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Size)];
        Assert.Equal(sizes.OrderDescending(), sizes);

        shell.FileSort = FileSort.Modified;
        var dates = files.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Modified).ToList();
        Assert.Equal(dates.OrderDescending(), dates);

        // Nach Name ist es die Reihenfolge des Servers – rückwärts innerhalb der Ordner und der Dateien.
        shell.FileSort = FileSort.Name;
        FoldersFirst();
        Assert.Equal(byName.Order(StringComparer.Ordinal), files.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        shell.FileSortDescending = false;
        Assert.Equal(byName, files.Entries.Select(entry => entry.Name));

        // Die Wahl gilt für jede Sitzung und steht in den Einstellungen.
        shell.FileSort = FileSort.Size;
        Assert.Equal(FileSort.Size, shell.Settings.FileSort);
        Assert.Equal(FileSort.Size, shell.OpenSession(shell.Profiles[1]).Files.Sort);
        Assert.Equal(FileSort.Name, new AppSettings { FileSort = (FileSort)99 }.Sanitize().FileSort);
    }

    [Fact]
    public void Opening_a_folder_in_the_terminal_types_a_cd_with_the_path_as_one_argument()
    {
        L.Init(AppLanguage.English);
        Assert.Equal("'/var/www'", Format.ShellQuote("/var/www"));
        Assert.Equal("'/srv/it'\\''s here; rm -rf $HOME'", Format.ShellQuote("/srv/it's here; rm -rf $HOME"));
        Assert.Null(Format.ShellQuote("/tmp/a\nrm -rf ~"));
        Assert.Null(Format.ShellQuote("/tmp/\u001b[2J"));
        Assert.Null(Format.ShellQuote(string.Empty));
        Assert.Equal("drwxr-xr-x", Format.ModeText(0x1ED, isDirectory: true, isLink: false));
        Assert.Equal(string.Empty, Format.ModeText(null, isDirectory: false, isLink: false));

        var profile = new Models.ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);
        var session = shell.OpenSession(profile);
        session.Start();

        // Ohne stehende Shell wird nichts getippt.
        Assert.False(shell.OpenInTerminal(session, "/var/www"));

        backend.Last.Set(new Services.ConnectionStatus(Services.ConnectionState.Connected));
        Assert.True(shell.OpenInTerminal(session, "/var/my site"));
        Assert.Equal(["cd '/var/my site'\r"], backend.Last.Input);

        Assert.False(shell.OpenInTerminal(session, "/tmp/a\nreboot"));
        Assert.Single(backend.Last.Input);
    }

    [Fact]
    public void The_settings_page_opens_and_closes_through_its_command()
    {
        var shell = TestShell.Create(out _);
        Assert.False(shell.IsSettingsOpen);

        Assert.True(shell.RunCommand("view.settings"));
        Assert.True(shell.IsSettingsOpen);

        Assert.True(shell.RunCommand("view.settings"));
        Assert.False(shell.IsSettingsOpen);
    }

    [Fact]
    public void Start_closes_the_settings_and_the_help_even_when_the_start_page_is_already_behind_them()
    {
        var shell = TestShell.Create(out _);
        shell.HomePage = ViewModels.HomePage.Snippets;

        shell.IsSettingsOpen = true;
        shell.GoHome();
        Assert.False(shell.IsSettingsOpen);
        Assert.Null(shell.ActiveSession);
        Assert.Equal(ViewModels.HomePage.Snippets, shell.HomePage);

        shell.IsHelpOpen = true;
        shell.GoHome();
        Assert.False(shell.IsHelpOpen);
    }

    [Fact]
    public void A_click_on_a_tab_closes_the_settings_even_on_the_active_one_but_a_tab_that_closes_does_not()
    {
        var shell = TestShell.Create(out _);
        var first = shell.OpenSession(shell.Profiles[0]);
        var second = shell.OpenSession(shell.Profiles[1]);
        shell.ActiveSession = first;

        shell.IsSettingsOpen = true;
        shell.ShowSession(first);
        Assert.False(shell.IsSettingsOpen);
        Assert.Same(first, shell.ActiveSession);

        shell.IsSettingsOpen = true;
        shell.ShowSession(second);
        Assert.False(shell.IsSettingsOpen);
        Assert.Same(second, shell.ActiveSession);

        // Wechselt der Tab von selbst, bleibt die Seite, auf der der Benutzer gerade ist.
        shell.IsSettingsOpen = true;
        shell.ActiveSession = first;
        Assert.True(shell.IsSettingsOpen);
    }

    [Fact]
    public void A_profile_can_carry_one_of_the_known_colors_through_editor_core_and_export()
    {
        L.Init(AppLanguage.English);
        Assert.Equal(["", "red", "orange", "yellow", "green", "blue", "violet"], ProfileColors.All.Select(color => color.Id));
        Assert.Null(ProfileColors.All[0].Hex);
        Assert.Equal("red", ProfileColors.Find("red").Id);
        Assert.Equal(string.Empty, ProfileColors.Find("#ff0000").Id);
        Assert.Equal(string.Empty, ProfileColors.Find(null).Id);

        // Der Editor zeigt die Farbe des Profils und gibt sie beim Speichern zurück – „keine“ als null.
        var profile = new Models.ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", [], Color: "green");
        var editor = new ViewModels.ProfileEditorViewModel(profile);
        Assert.Equal("green", editor.Color.Id);
        editor.Color = editor.Colors.Single(color => color.Id == "red");
        Assert.Equal("red", editor.ToProfile().Color);
        editor.Color = editor.Colors[0];
        Assert.Null(editor.ToProfile().Color);
        Assert.Equal(string.Empty, new ViewModels.ProfileEditorViewModel(profile with { Color = "neon" }).Color.Id);

        // Zum Kern und zurück.
        var dto = Backend.ProfileDto.From(profile);
        Assert.Equal("green", dto.Color);
        Assert.Equal("green", dto.ToProfile().Color);
        Assert.Contains("\"color\":\"green\"", JsonSerializer.Serialize(new Backend.RequestDto("profiles.save") { Profile = dto }, Backend.CoreJsonContext.Default.RequestDto));
    }

    private sealed class FakeReachability(Func<string, bool> answers) : ViewModels.IReachability
    {
        public List<string> Asked { get; } = [];

        public Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellation)
        {
            Asked.Add($"{host}:{port}");
            return Task.FromResult(answers(host));
        }
    }

    [Fact]
    public async Task Reachability_is_probed_only_when_switched_on_on_the_start_page_and_not_too_often()
    {
        var shell = TestShell.Create(out _);
        var probe = new FakeReachability(host => host != "10.0.1.20");
        shell.UseReachability(probe);
        var clock = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        shell.Now = () => clock;
        var changes = 0;
        shell.ReachabilityChanged += () => changes++;

        // Ausgeschaltet klopft die App nirgends an.
        await shell.ProbeReachabilityAsync();
        Assert.Empty(probe.Asked);
        Assert.Null(shell.IsReachable("web01"));

        shell.CheckReachability = true;
        await shell.ProbeReachabilityAsync();
        Assert.Equal(["10.0.1.10:22", "10.0.1.20:22", "10.0.2.10:2222", "10.0.2.21:22"], probe.Asked);
        Assert.Equal((true, false), (shell.IsReachable("web01"), shell.IsReachable("database01")));
        Assert.Equal(1, changes);

        // Nicht noch einmal, bevor die Frist um ist – und nichts Neues heißt: keine Meldung.
        await shell.ProbeReachabilityAsync();
        Assert.Equal(4, probe.Asked.Count);
        clock += ViewModels.ShellViewModel.ReachabilityInterval;
        await shell.ProbeReachabilityAsync();
        Assert.Equal(8, probe.Asked.Count);
        Assert.Equal(1, changes);

        // Mit einer aktiven Sitzung ist die Startseite nicht zu sehen.
        shell.OpenSession(shell.Profiles[0]);
        clock += ViewModels.ShellViewModel.ReachabilityInterval;
        await shell.ProbeReachabilityAsync();
        Assert.Equal(8, probe.Asked.Count);

        // Ausschalten nimmt die Punkte weg.
        shell.CheckReachability = false;
        Assert.Null(shell.IsReachable("web01"));
        Assert.Equal(2, changes);
        Assert.False(shell.Settings.CheckReachability);
    }

    [Fact]
    public void Accent_colors_are_complete_and_an_unknown_one_falls_back_to_the_draft_blue()
    {
        L.Init(AppLanguage.English);
        var all = AccentColors.All;

        Assert.Equal(all.Count, all.Select(color => color.Id).Distinct().Count());
        Assert.Equal((AccentColors.WindowsId, null), (all[0].Id, all[0].Hex));
        Assert.Equal("#5fb3ff", AccentColors.Find(AccentColors.DefaultId).Hex);
        Assert.All(all.Skip(1), color => Assert.Matches("^#[0-9a-f]{6}$", color.Hex));
        Assert.All(all, color => Assert.False(string.IsNullOrWhiteSpace(color.Name)));

        Assert.Equal("violet", AccentColors.Find("violet").Id);
        Assert.Equal(AccentColors.DefaultId, AccentColors.Find("neon").Id);
        Assert.Equal(AccentColors.DefaultId, AccentColors.Find(null).Id);

        Assert.Equal(AccentColors.DefaultId, new AppSettings().Accent);
        Assert.Equal("green", new AppSettings { Accent = "green" }.Sanitize().Accent);
        Assert.Equal(AccentColors.DefaultId, new AppSettings { Accent = "neon" }.Sanitize().Accent);
    }
}
