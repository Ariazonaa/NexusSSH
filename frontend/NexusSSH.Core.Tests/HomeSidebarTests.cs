using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Seitenleiste der Startseite: fünf Einträge statt acht, keine Seite fällt weg.</summary>
public sealed class HomeSidebarTests
{
    [Fact]
    public void Every_page_is_reached_through_exactly_one_entry()
    {
        foreach (var page in Enum.GetValues<HomePage>())
        {
            var entry = HomeSidebar.EntryOf(page);
            Assert.NotEqual(HomeEntry.More, entry);
            Assert.Equal(page, HomeSidebar.PageOf(entry, page));
        }

        // Oben fünf Einträge, unter „Mehr“ drei; „Mehr“ selbst führt nirgends hin.
        Assert.Equal(
            [HomeEntry.Servers, HomeEntry.Snippets, HomeEntry.KeysAndIdentities, HomeEntry.Tunnels, HomeEntry.More],
            HomeSidebar.Entries.Except(HomeSidebar.UnderMore));
        Assert.Equal([HomeEntry.Syncs, HomeEntry.KnownHosts, HomeEntry.Overview], HomeSidebar.UnderMore);
        Assert.Null(HomeSidebar.PageOf(HomeEntry.More, HomePage.Servers));

        // Schlüssel & Identitäten bleibt bei der offenen Hälfte, sonst zuerst die Schlüssel.
        Assert.Equal(HomePage.Identities, HomeSidebar.PageOf(HomeEntry.KeysAndIdentities, HomePage.Identities));
        Assert.Equal(HomePage.Keys, HomeSidebar.PageOf(HomeEntry.KeysAndIdentities, HomePage.Tunnels));
    }

    [Fact]
    public void More_opens_by_itself_for_a_page_below_it_and_stands_in_for_it_when_closed()
    {
        var shell = TestShell.Create(out _);
        Assert.False(shell.IsHomeMoreOpen);

        shell.ChooseHomeEntry(HomeEntry.KeysAndIdentities);
        Assert.Equal(HomePage.Keys, shell.HomePage);
        shell.HomePage = HomePage.Identities;
        shell.ChooseHomeEntry(HomeEntry.KeysAndIdentities);
        Assert.Equal(HomePage.Identities, shell.HomePage);

        // „Mehr“ klappt nur auf und zu.
        shell.ChooseHomeEntry(HomeEntry.More);
        Assert.True(shell.IsHomeMoreOpen);
        Assert.Equal(HomePage.Identities, shell.HomePage);
        shell.ChooseHomeEntry(HomeEntry.More);
        Assert.False(shell.IsHomeMoreOpen);

        // Eine Seite darunter (auch über die Palette) klappt es auf; zugeklappt ist „Mehr“ hervorgehoben.
        shell.ShowHome(HomePage.KnownHosts);
        Assert.True(shell.IsHomeMoreOpen);
        Assert.Equal(HomeEntry.KnownHosts, HomeSidebar.Highlighted(shell.HomePage, shell.IsHomeMoreOpen));
        shell.ChooseHomeEntry(HomeEntry.More);
        Assert.Equal(HomeEntry.More, HomeSidebar.Highlighted(shell.HomePage, shell.IsHomeMoreOpen));
        Assert.Equal(HomeEntry.Tunnels, HomeSidebar.Highlighted(HomePage.Tunnels, moreOpen: false));
    }
}
