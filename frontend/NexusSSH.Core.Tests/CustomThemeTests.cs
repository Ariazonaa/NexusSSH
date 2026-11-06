using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Importierte Farbschemata stehen neben den mitgelieferten, überstehen einen Neustart und lassen sich wieder entfernen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class CustomThemeTests : IDisposable
{
    private const string Scheme = """
        {
          "name": "Night Shift",
          "background": "#101418", "foreground": "#d0d4d8",
          "black": "#000000", "red": "#cc3333", "green": "#33cc33", "yellow": "#cccc33",
          "blue": "#3333cc", "purple": "#cc33cc", "cyan": "#33cccc", "white": "#cccccc",
          "brightBlack": "#555555", "brightRed": "#ff5555", "brightGreen": "#55ff55", "brightYellow": "#ffff55",
          "brightBlue": "#5555ff", "brightPurple": "#ff55ff", "brightCyan": "#55ffff", "brightWhite": "#ffffff"
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusssh-themes-" + Guid.NewGuid().ToString("N"));

    public CustomThemeTests() => L.Init(AppLanguage.English);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void An_imported_scheme_is_selected_listed_and_offered_in_the_palette()
    {
        var shell = TestShell.Create(out _);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(shell.CanRemoveTheme);

        var theme = shell.ImportTheme(Scheme, "night.json");

        Assert.NotNull(theme);
        Assert.Equal("custom-night-shift", theme.Id);
        Assert.Same(theme, shell.TerminalTheme);
        Assert.Equal(theme.Id, shell.Settings.TerminalTheme);
        Assert.Equal(TerminalThemes.All.Count + 1, shell.Themes.Count);
        Assert.Equal("Night Shift", shell.Themes[^1].Name);
        Assert.True(shell.CanRemoveTheme);
        Assert.Contains(nameof(shell.Themes), changed);
        Assert.Contains(shell.Commands, command => command.Id == "theme.custom-night-shift");
    }

    [Fact]
    public void Importing_the_same_name_again_replaces_it_and_a_broken_file_changes_nothing()
    {
        var shell = TestShell.Create(out _);
        shell.ImportTheme(Scheme, null);

        var again = shell.ImportTheme(Scheme.Replace("#101418", "#202428", StringComparison.Ordinal), null);

        Assert.Equal("#202428", again?.Background);
        Assert.Single(shell.Settings.CustomThemes);
        Assert.Equal("#202428", shell.TerminalTheme.Background);

        Assert.Null(shell.ImportTheme("{ \"name\": \"half\" }", null));
        Assert.Null(shell.ImportTheme(null, null));
        Assert.Single(shell.Settings.CustomThemes);
    }

    [Fact]
    public void Removing_the_selected_scheme_falls_back_to_the_default_and_built_in_ones_stay()
    {
        var shell = TestShell.Create(out _);
        var theme = shell.ImportTheme(Scheme, null)!;

        Assert.False(shell.RemoveTheme(TerminalThemes.All[1]));
        Assert.True(shell.RemoveTheme(theme));

        Assert.Equal(TerminalThemes.DefaultId, shell.TerminalTheme.Id);
        Assert.Empty(shell.Settings.CustomThemes);
        Assert.DoesNotContain(shell.Commands, command => command.Id == "theme.custom-night-shift");
        Assert.False(shell.RemoveTheme(theme));
    }

    [Fact]
    public void A_scheme_survives_a_restart_and_a_file_edited_by_hand_is_checked()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = store.Load();
        var shell = new ViewModels.ShellViewModel(new Mock.MockBackend(), new ImmediateDispatcher(), settings, new FakePrompts());
        shell.ImportTheme(Scheme, null);
        store.Save(settings);

        var loaded = new SettingsStore(path).Load();
        Assert.Equal("custom-night-shift", loaded.TerminalTheme);
        Assert.Equal("#101418", Assert.Single(loaded.CustomThemes).Background);
        Assert.Equal(16, loaded.CustomThemes[0].Ansi.Count);

        // Von Hand verdorben: Eine „Farbe“, die keine ist, wirft das Schema hinaus – und die Auswahl fällt zurück.
        File.WriteAllText(path, File.ReadAllText(path).Replace("#101418", "url(evil)", StringComparison.Ordinal));
        var tidied = new SettingsStore(path).Load();
        Assert.Empty(tidied.CustomThemes);
        Assert.Equal(TerminalThemes.DefaultId, tidied.TerminalTheme);
    }

    [Fact]
    public void There_is_room_for_fifty_schemes_and_no_more()
    {
        var shell = TestShell.Create(out _);
        for (var index = 0; index < TerminalThemes.MaxCustom; index++)
        {
            Assert.NotNull(shell.ImportTheme(Scheme.Replace("Night Shift", $"Scheme {index}", StringComparison.Ordinal), null));
        }

        Assert.Null(shell.ImportTheme(Scheme.Replace("Night Shift", "One too many", StringComparison.Ordinal), null));
        Assert.Equal(TerminalThemes.MaxCustom, shell.Settings.CustomThemes.Count);
    }
}
