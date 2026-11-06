using System.Text.Json;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Eigene Farbschemata anlegen und ändern, je Profil eines wählen, und sie im Export mitnehmen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ThemeEditorTests
{
    public ThemeEditorTests() => L.Init(AppLanguage.English);

    private static TerminalTheme Dracula => TerminalThemes.Find("dracula");

    [Fact]
    public void A_new_scheme_starts_from_the_chosen_one_and_gets_its_own_id()
    {
        var editor = new ThemeEditorViewModel(Dracula with { Name = "Prod" }, isNew: true, TerminalThemes.All);
        Assert.Equal((true, "New colour scheme", 20), (editor.IsValid, editor.Title, editor.Slots.Count));
        Assert.Equal(("Background", "#282a36", true), (editor.Selected.Label, editor.Selected.Hex, editor.Selected.IsSelected));
        Assert.Equal(["Background", "Text", "Cursor", "Selection", "Black", "Red"], editor.Slots.Take(6).Select(slot => slot.Label));
        Assert.Equal("Red, bright", editor.Slots[13].Label);

        // Der Hintergrund wird dunkelrot; die Vorschau erfährt davon.
        var changes = 0;
        editor.Changed += () => changes++;
        Assert.True(editor.SetColor(" #3A0000 "));
        Assert.Equal(("#3a0000", 1), (editor.Selected.Hex, changes));

        // Dieselbe Farbe noch einmal ist keine Änderung; was keine Farbe ist, ändert nichts.
        Assert.True(editor.SetColor("#3a0000"));
        foreach (var wrong in new[] { null, "", "3a0000", "#3a00", "#3a000g", "red", "#3a0000ff", "url(x)" })
        {
            Assert.False(editor.SetColor(wrong), wrong);
        }

        Assert.Equal(("#3a0000", 1), (editor.Selected.Hex, changes));

        // Ein anderes Feld wählen und ändern.
        editor.Select(editor.Slots[5]);
        Assert.Equal((false, true, "Red"), (editor.Slots[0].IsSelected, editor.Slots[5].IsSelected, editor.Selected.Label));
        editor.SetColor("#ff0000");

        var theme = editor.ToTheme();
        Assert.Equal(("custom-prod", "Prod", "#3a0000", "#ff0000"), (theme.Id, theme.Name, theme.Background, theme.Ansi[1]));
        Assert.Equal(Dracula.Ansi[9], theme.Ansi[9]);
        Assert.True(ThemeImport.IsValid(theme));
    }

    [Fact]
    public void A_scheme_needs_a_name_that_is_not_taken()
    {
        var own = Dracula with { Id = "custom-prod", Name = "Prod" };
        var editor = new ThemeEditorViewModel(Dracula, isNew: true, [.. TerminalThemes.All, own]);

        // Der Name eines vorhandenen – auch eines mitgelieferten – würde verwechselt.
        Assert.Equal((false, "A scheme with this name already exists."), (editor.IsValid, editor.NameError));
        editor.Name = "prod";
        Assert.False(editor.IsValid);
        editor.Name = "  \u0007 ";
        Assert.Equal((false, "The scheme needs a name."), (editor.IsValid, editor.NameError));

        // Was nicht in einen Namen gehört, fällt weg.
        editor.Name = " Prod <rot> \"2\" ";
        Assert.True(editor.IsValid);
        Assert.Equal(("Prod rot 2", "custom-prod-rot-2"), (editor.ToTheme().Name, editor.ToTheme().Id));

        // Beim Ändern bleibt die Kennung – Profile verweisen auf sie –, und der eigene Name ist nicht „vergeben“.
        var edit = new ThemeEditorViewModel(own, isNew: false, [.. TerminalThemes.All, own]);
        Assert.Equal((true, "Edit colour scheme"), (edit.IsValid, edit.Title));
        edit.Name = "Production";
        Assert.Equal(("custom-prod", "Production"), (edit.ToTheme().Id, edit.ToTheme().Name));
    }

    [Fact]
    public void The_shell_keeps_own_schemes_and_only_those_can_be_edited()
    {
        var shell = TestShell.Create(out _);
        shell.TerminalTheme = Dracula;
        Assert.Null(shell.EditTheme());

        var editor = shell.NewTheme();
        Assert.Equal("Dracula (own)", editor.Name);
        editor.SetColor("#3a0000");
        Assert.True(shell.SaveTheme(editor.ToTheme()));
        Assert.Equal(("custom-dracula-own", "#3a0000"), (shell.TerminalTheme.Id, shell.TerminalTheme.Background));
        Assert.Contains(shell.Themes, theme => theme.Id == "custom-dracula-own");
        Assert.Contains(shell.Commands, command => command.Id == "theme.custom-dracula-own");

        // Jetzt lässt es sich ändern; gespeichert ersetzt es sich selbst.
        var again = Assert.IsType<ThemeEditorViewModel>(shell.EditTheme());
        again.SetColor("#000033");
        Assert.True(shell.SaveTheme(again.ToTheme()));
        Assert.Equal("#000033", shell.TerminalTheme.Background);
        Assert.Single(shell.Themes, theme => theme.Id == "custom-dracula-own");

        // Was die Terminal-Seite nicht bekommen darf, wird nicht gespeichert – auch kein mitgeliefertes unter seiner Kennung.
        Assert.False(shell.SaveTheme(Dracula));
        Assert.False(shell.SaveTheme(again.ToTheme() with { Background = "red" }));
        Assert.False(shell.SaveTheme(null));
    }

    [Fact]
    public void A_profile_can_have_its_own_scheme()
    {
        var shell = TestShell.Create(out var backend);
        var plain = backend.GetProfiles()[0];
        var production = backend.GetProfiles()[1] with { ThemeId = "dracula" };

        Assert.Null(shell.ThemeOverride(plain));
        Assert.Equal(shell.TerminalTheme, shell.ThemeFor(plain));
        Assert.Equal("dracula", shell.ThemeOverride(production)?.Id);

        // Ein Schema, das es nicht (mehr) gibt, ist keines: Dann gilt das allgemeine.
        Assert.Null(shell.ThemeOverride(plain with { ThemeId = "custom-gone" }));

        // Die Karte um das Terminal folgt dem aktiven Tab.
        var first = shell.OpenSession(plain);
        var second = shell.OpenSession(production);
        Assert.Equal("dracula", shell.ShownTheme.Id);
        shell.ActiveSession = first;
        Assert.Equal(shell.TerminalTheme.Id, shell.ShownTheme.Id);
        shell.ShowHome();
        Assert.Equal(shell.TerminalTheme.Id, shell.ShownTheme.Id);
        Assert.NotNull(second);
    }

    [Fact]
    public void The_profile_editor_offers_the_schemes_and_the_core_gets_the_id()
    {
        var editor = new ProfileEditorViewModel(new ServerProfile("p1", "prod", "h", 22, "u", "", [], ThemeId: "nord"), themes: TerminalThemes.All) { };
        Assert.Equal(("As set in the settings", null), (editor.ThemeChoices[0].Name, editor.ThemeChoices[0].Id));
        Assert.Equal(TerminalThemes.All.Count + 1, editor.ThemeChoices.Count);
        Assert.Equal("nord", editor.Theme.Id);

        editor.Theme = editor.ThemeChoices.First(choice => choice.Id == "dracula");
        Assert.Equal("dracula", editor.ToProfile().ThemeId);
        editor.Theme = editor.ThemeChoices[0];
        Assert.Null(editor.ToProfile().ThemeId);

        // Ein Schema, das es nicht mehr gibt, zeigt „wie eingestellt“; ohne Terminal (FTP) gibt es keines.
        Assert.Null(new ProfileEditorViewModel(new ServerProfile("p1", "prod", "h", 22, "u", "", [], ThemeId: "custom-gone"), themes: TerminalThemes.All).Theme.Id);
        editor.Theme = editor.ThemeChoices[1];
        editor.IsFtp = true;
        Assert.Null(editor.ToProfile().ThemeId);

        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"prod","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true,"theme":"dracula"}}""";
        using var core = new Backend.CoreBackend(transport);
        var saved = core.Save(new ServerProfile(string.Empty, "prod", "h", 22, "u", string.Empty, [], ThemeId: "dracula"));
        Assert.Equal("dracula", saved.ThemeId);
        Assert.Equal("dracula", Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("theme").GetString());
    }

    [Fact]
    public void A_terminal_is_opened_with_its_own_scheme_or_none()
    {
        using var plain = JsonDocument.Parse(TerminalMessages.Open("s1", "#ff0000"));
        Assert.False(plain.RootElement.TryGetProperty("theme", out _));

        using var themed = JsonDocument.Parse(TerminalMessages.Open("s1", null, Dracula));
        var theme = themed.RootElement.GetProperty("theme");
        Assert.Equal(("#282a36", "#f8f8f2", "#ff5555", "#ffffff"), (theme.GetProperty("background").GetString(), theme.GetProperty("foreground").GetString(), theme.GetProperty("red").GetString(), theme.GetProperty("brightWhite").GetString()));
        Assert.Equal(21, theme.EnumerateObject().Count());

        // Später ein Schema geben – oder es wieder nehmen.
        using var given = JsonDocument.Parse(TerminalMessages.Theme("s1", Dracula));
        Assert.Equal(("theme", "s1", "#282a36"), (given.RootElement.GetProperty("type").GetString(), given.RootElement.GetProperty("id").GetString(), given.RootElement.GetProperty("theme").GetProperty("background").GetString()));
        using var taken = JsonDocument.Parse(TerminalMessages.Theme("s1", null));
        Assert.False(taken.RootElement.TryGetProperty("theme", out _));
    }

    [Fact]
    public void Own_schemes_travel_in_the_export_and_arrive_checked()
    {
        var own = Dracula with { Id = "custom-prod", Name = "Prod", Background = "#3a0000" };
        var settings = new AppSettings { CustomThemes = [own], TerminalTheme = own.Id };
        var profile = new ServerProfile("p1", "prod", "h", 22, "u", "", [], ThemeId: own.Id);
        var file = DataExchange.Create([profile, profile with { Id = "p2", Name = "test", Host = "t", ThemeId = "nord" }], settings, "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");

        Assert.Equal("custom-prod", Assert.Single(file.Themes!).Id);
        Assert.Equal(["custom-prod", "nord"], file.Profiles!.Select(exported => exported.Theme));

        // Auf einem anderen Rechner: Das Schema kommt mit, die Profile behalten es, und es kann das gewählte sein.
        var plan = DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true);
        Assert.Equal((1, 0, true), (plan.NewThemes, plan.ChangedThemes, plan.HasChanges));
        Assert.Equal(["custom-prod", "nord"], plan.Profiles.Select(planned => planned.Profile.ThemeId));
        Assert.Equal("custom-prod", DataExchange.Resolve(file.Settings!, plan.Themes).Theme?.Id);

        // Dasselbe noch einmal ändert nichts; ein geändertes ersetzt das vorhandene.
        var same = DataExchange.Plan(file, [], settings, "/home/a", keyExists: _ => true);
        Assert.Equal((0, 0), (same.NewThemes, same.ChangedThemes));
        var older = new AppSettings { CustomThemes = [own with { Background = "#000000" }] };
        Assert.Equal((0, 1), (DataExchange.Plan(file, [], older, "/home/a", keyExists: _ => true) is var changed ? (changed.NewThemes, changed.ChangedThemes) : default));

        // Die Datei ist fremd: Ein Schema mit etwas anderem als Farben kommt nicht an, und ein Profil, das darauf
        // verweist, folgt dann dem allgemeinen. Ohne mitgeliefertes Schema bleibt auch die Wahl, wie sie ist.
        file.Themes = [own with { Background = "url(javascript:1)" }, own with { Id = "dracula" }, own with { Id = "custom-ok", Name = "Ok", Ansi = [.. own.Ansi.Take(15)] }];
        var hostile = DataExchange.Plan(file, [], new AppSettings(), "/home/b", keyExists: _ => true);
        Assert.Equal((0, 0), (hostile.NewThemes, hostile.ChangedThemes));
        Assert.Equal([null, "nord"], hostile.Profiles.Select(planned => planned.Profile.ThemeId));
        Assert.Null(DataExchange.Resolve(file.Settings!, hostile.Themes).Theme);
    }

    [Fact]
    public void An_import_brings_the_schemes_with_the_settings()
    {
        var own = Dracula with { Id = "custom-prod", Name = "Prod", Background = "#3a0000" };
        var file = DataExchange.Create([], new AppSettings { CustomThemes = [own], TerminalTheme = own.Id }, "0.21.0", DateTimeOffset.UnixEpoch, "/home/a");

        // Ohne die Einstellungen bleiben auch die Schemata draußen.
        var shell = TestShell.Create(out _);
        shell.ImportData(DataExchange.Plan(file, [], shell.Settings, "/home/b", keyExists: _ => true), new ImportChoice(Settings: false));
        Assert.DoesNotContain(shell.Themes, theme => theme.Id == own.Id);

        shell.ImportData(DataExchange.Plan(file, [], shell.Settings, "/home/b", keyExists: _ => true), new ImportChoice());
        Assert.Contains(shell.Themes, theme => theme.Id == own.Id);
        Assert.Equal(own.Id, shell.TerminalTheme.Id);
    }
}
