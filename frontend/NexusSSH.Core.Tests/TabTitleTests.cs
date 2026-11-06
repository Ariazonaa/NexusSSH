using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Was am Reiter steht: der Name des Profils, ein eigener Name oder der Titel, den der Server setzt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TabTitleTests
{
    public TabTitleTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_tab_is_called_like_its_profile_until_the_user_names_it()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        var changed = new List<string?>();
        session.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(session.Profile.Name, session.Title);

        session.Rename("  deploy ‮evil\n ");
        // Eine Zeile, ohne Steuerzeichen und ohne das Zeichen, das die Schreibrichtung umkehrt.
        Assert.StartsWith("deploy ", session.Title);
        Assert.DoesNotContain(session.Title, character => char.IsControl(character) || character == '‮');
        Assert.Equal(Format.Visible("deploy ‮evil\n").Trim(), session.Title);
        Assert.Contains(nameof(SessionViewModel.Title), changed);

        session.Rename(new string('x', 200));
        Assert.Equal(SessionViewModel.MaxTitleLength + 1, session.Title.Length);
        Assert.EndsWith("…", session.Title);

        // Leer nimmt den eigenen Namen wieder weg.
        session.Rename("   ");
        Assert.Null(session.CustomTitle);
        Assert.Equal(session.Profile.Name, session.Title);
    }

    [Fact]
    public void The_servers_title_shows_only_when_switched_on_and_never_over_a_name_of_ones_own()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        var session = shell.OpenSession(shell.Profiles[0]);

        session.SetServerTitle("vim: nginx.conf");
        Assert.Equal(session.Profile.Name, session.Title);

        shell.ServerTabTitles = true;
        Assert.True(settings.ServerTabTitles);
        Assert.Equal("vim: nginx.conf", session.Title);

        // Ein neuer Tab kennt die Einstellung von Anfang an.
        var second = shell.OpenSession(shell.Profiles[1]);
        second.SetServerTitle("htop");
        Assert.Equal("htop", second.Title);

        // Der eigene Name geht vor; ein leerer Titel des Servers gibt den Namen des Profils zurück.
        session.Rename("production – careful");
        session.SetServerTitle("bash");
        Assert.Equal("production – careful", session.Title);
        session.Rename(null);
        Assert.Equal("bash", session.Title);
        session.SetServerTitle(string.Empty);
        Assert.Equal(session.Profile.Name, session.Title);

        shell.ServerTabTitles = false;
        Assert.Equal(second.Profile.Name, second.Title);
    }

    [Fact]
    public void Renaming_asks_the_window_and_the_page_reports_titles()
    {
        var shell = TestShell.Create(out _);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        Assert.False(shell.RunCommand("session.rename"));
        shell.OpenSession(shell.Profiles[0]);
        Assert.True(shell.RunCommand("session.rename"));
        Assert.Equal([UiRequest.RenameTab], requests);

        Assert.Equal(new WebMessage.TitleChanged("s1", "vim"), TerminalMessages.Parse("""{"type":"title","id":"s1","data":"vim"}"""));
        Assert.Null(TerminalMessages.Parse($$"""{"type":"title","id":"s1","data":"{{new string('x', TerminalMessages.MaxTitleLength + 1)}}"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"title","id":"","data":"vim"}"""));
    }
}
