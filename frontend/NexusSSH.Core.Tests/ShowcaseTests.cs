using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Das Bild der Beispieldaten (<c>--demo</c>): dieselben Inhalte wie Entwurf A – sonst ließe sich ein Bild der App
/// nicht neben den Entwurf legen.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ShowcaseTests
{
    public ShowcaseTests() => L.Init(AppLanguage.German);

    private static (ShellViewModel Shell, MockBackend Backend) Open()
    {
        var backend = new MockBackend(showcase: true);
        var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
        var tabs = MockBackend.ShowcaseTabs.Select(id => shell.OpenSession(shell.Profiles.Single(profile => profile.Id == id))).ToList();
        foreach (var tab in tabs)
        {
            tab.Start();
        }

        shell.ActiveSession = tabs[0];
        shell.SetSnippets(MockBackend.ShowcaseSnippets);
        return (shell, backend);
    }

    [Fact]
    public void Tabs_files_and_values_are_those_of_the_picture()
    {
        try
        {
            var (shell, _) = Open();
            Assert.Equal(["web01", "db02", "nas"], shell.Sessions.Select(session => session.Title));
            // Die Punkte der Reiter: web01 und db02 grün, nas gelb (verbindet noch).
            Assert.Equal((true, true, false), (shell.Sessions[0].Inspector.IsOnline, shell.Sessions[1].Inspector.IsOnline, shell.Sessions[2].Inspector.IsOnline));
            Assert.True(shell.Sessions[2].IsBusy);
            Assert.Equal(L.Get("StateConnecting"), shell.Sessions[2].StateLabel);

            var web = shell.ActiveSession!;
            Assert.Equal("/var/www/html", web.Files.CurrentPath);
            Assert.Equal(["assets", "config", "uploads", "index.php", "robots.txt", "wp-config.php"], web.Files.Entries.Select(entry => entry.Name));
            Assert.Equal("4,2 KB", Format.FileSize(web.Files.Entries.Single(entry => entry.Name == "index.php").Size, isDirectory: false));

            var values = web.Inspector;
            Assert.Equal(("12 %", "2,1 / 8 GB", "↓ 1,2 ↑ 0,3 MB/s", "64 %", "läuft seit 12 Tagen"), (values.CpuText, values.MemoryShortText, values.NetworkShortText, values.DiskShortText, values.UptimeSinceText));
            Assert.Equal("deploy", web.Profile.User);

            Assert.Equal((2, 1), (shell.TransferCount, shell.ProblemCount));

            // Die Kacheln der Startseite: zwei Gruppen, ein lokales Terminal, die Farben der Quadrate.
            Assert.Equal(["Kunde A: web01 db02 mail", "Zuhause: nas pi-hole PowerShell"], shell.HomeGroups.Select(group => $"{group.Name}: {string.Join(' ', group.Select(profile => profile.Name))}"));
            Assert.Equal(["green", "blue", null, "yellow", null, null], shell.Profiles.Select(profile => profile.ShownColor));
            Assert.True(shell.Profiles[^1].IsLocal);
            Assert.Equal(6, shell.FilteredSnippets.Count);
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }

    [Fact]
    public void The_terminal_of_the_picture_clears_the_connecting_line_before_its_commands()
    {
        try
        {
            var backend = new MockBackend(showcase: true);
            var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new AppSettings(), new FakePrompts());
            var web = shell.OpenSession(shell.Profiles.Single(profile => profile.Id == "web01"));
            var written = new System.Text.StringBuilder();
            web.Output += data => written.Append(System.Text.Encoding.UTF8.GetString(data.Span));
            web.Start();

            // Erst die Zeile der App, dann das Leeren samt Verlauf, dann die Befehle aus dem Bild.
            var text = written.ToString();
            var announced = text.IndexOf(L.Get("TerminalConnecting", "deploy@10.0.0.11"), StringComparison.Ordinal);
            var cleared = text.IndexOf("\e[H\e[2J\e[3J", StringComparison.Ordinal);
            Assert.True(announced >= 0 && cleared > announced && text.IndexOf("ls -la", StringComparison.Ordinal) > cleared, text);
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }

    [Fact]
    public void In_any_other_language_the_picture_is_english_and_neither_set_of_snippets_is_kept()
    {
        try
        {
            L.Init(AppLanguage.French);
            var backend = new MockBackend(showcase: true);
            Assert.Equal(["Customer A", "Customer A", "Customer A", "Home", "Home", "Home"], backend.GetProfiles().Select(profile => profile.Group));
            Assert.Equal("Check disk space", MockBackend.ShowcaseSnippets[1].Name);
            var english = MockBackend.ShowcaseSnippets.ToList();

            L.Init(AppLanguage.German);
            Assert.Equal("Platz prüfen", MockBackend.ShowcaseSnippets[1].Name);
            Assert.True(MockBackend.IsShowcase(english));
            Assert.True(MockBackend.IsShowcase(MockBackend.ShowcaseSnippets));
            Assert.False(MockBackend.IsShowcase(english.Take(5)));
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }

    [Fact]
    public void The_full_demo_data_stay_as_the_tests_know_them()
    {
        var backend = new MockBackend();
        Assert.Contains(backend.GetProfiles(), profile => profile.Name == "database01");
        Assert.DoesNotContain(backend.GetProfiles(), profile => profile.Name == "db02");
        Assert.Equal(["web01", "db02", "mail", "nas", "pi-hole", "PowerShell"], new MockBackend(showcase: true).GetProfiles().Select(profile => profile.Name));
    }
}
