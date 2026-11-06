using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Gruppen auf der Startseite: verschachtelt, einklappbar, in eigener Reihenfolge.</summary>
[Collection(LanguageCollection.Name)]
public sealed class HomeGroupTests
{
    public HomeGroupTests() => L.Init(AppLanguage.English);

    // Beispieldaten (web01 als Favorit und database01 in „Production“, zwei in „Development“) und dazu ein Kunde
    // mit zwei Projekten, eines davon mit einer Ebene mehr.
    private static ShellViewModel Shell(AppSettings? settings = null)
    {
        var shell = TestShell.Create(out _, settings);
        Add(shell, "acme-web", "Acme/Shop");
        Add(shell, "acme-db", " Acme / Shop ");
        Add(shell, "acme-prod", "Acme/Shop/Prod");
        Add(shell, "acme-mail", "Acme/Mail");
        Add(shell, "acme-vpn", "Acme");
        Add(shell, "loose", string.Empty);
        return shell;
    }

    private static void Add(ShellViewModel shell, string name, string group)
        => Assert.NotNull(shell.SaveProfile(new ServerProfile(string.Empty, name, $"{name}.example.org", 22, "root", group, [])));

    private static string[] Names(ShellViewModel shell) => [.. shell.HomeGroups.Select(group => group.Name)];

    private static string[] In(ShellViewModel shell, string section)
        => [.. shell.HomeGroups.Single(group => group.Name == section).Select(profile => profile.Name)];

    [Fact]
    public void Paths_are_read_without_empty_parts_and_know_their_place()
    {
        Assert.Equal("a/b/c", GroupPath.Normalize(" a / b//c/ "));
        Assert.Equal(string.Empty, GroupPath.Normalize(null));
        Assert.Equal(("c", "a/b", string.Empty), (GroupPath.Name("a/b/c"), GroupPath.Parent("a/b/c"), GroupPath.Parent("a")));

        Assert.True(GroupPath.IsWithin("a/b", "a"));
        Assert.True(GroupPath.IsWithin("a", "a"));
        Assert.True(GroupPath.IsWithin("a", string.Empty));
        Assert.False(GroupPath.IsWithin("ab", "a"));

        Assert.Equal("a/b", GroupPath.ChildOf("a/b/c", "a"));
        Assert.Equal("a", GroupPath.ChildOf("a/b/c", string.Empty));
        Assert.Null(GroupPath.ChildOf("a", "a"));
        Assert.Null(GroupPath.ChildOf("ab/c", "a"));
        Assert.Null(GroupPath.ChildOf(string.Empty, string.Empty));

        Assert.Equal(["a", "a/b", "a/b/c"], GroupPath.WithAncestors("a/b/c"));
        Assert.Empty(GroupPath.WithAncestors(string.Empty));
    }

    [Fact]
    public void The_home_page_shows_one_level_and_a_click_leads_deeper()
    {
        var shell = Shell();

        // Ganz oben: jede Gruppe der ersten Ebene, mit dem, was direkt in ihr liegt.
        Assert.Equal(["Favorites", "Production", "Development", "Acme", "Ungrouped"], Names(shell));
        Assert.Equal(["acme-vpn"], In(shell, "Acme"));
        var acme = shell.HomeGroups.Single(group => group.Name == "Acme");
        Assert.Equal((5, 2, true, "Acme"), (acme.Total, acme.Subgroups, acme.HasSubgroups, acme.Path));
        Assert.Equal("5 server(s) · subgroups: 2", acme.Summary);
        Assert.False(shell.IsInHomeFolder);
        Assert.Equal(["All servers"], shell.HomeCrumbs.Select(crumb => crumb.Name));

        shell.OpenHomeFolder("Acme");

        Assert.True(shell.IsInHomeFolder);
        Assert.Equal(["Acme", "Shop", "Mail"], Names(shell));
        Assert.Equal(GroupKind.Here, shell.HomeGroups[0].Kind);
        Assert.Equal(["acme-vpn"], In(shell, "Acme"));
        // „ Acme / Shop “ ist dieselbe Gruppe wie „Acme/Shop“.
        Assert.Equal(["acme-web", "acme-db"], In(shell, "Shop"));
        Assert.Equal([("All servers", string.Empty), ("Acme", "Acme")], shell.HomeCrumbs.Select(crumb => (crumb.Name, crumb.Path)));

        shell.OpenHomeFolder("Acme/Shop");
        Assert.Equal(["Shop", "Prod"], Names(shell));
        Assert.Equal(["All servers", "Acme", "Shop"], shell.HomeCrumbs.Select(crumb => crumb.Name));

        // Eine Gruppe ohne eigene Profile zeigt nur, was unter ihr liegt.
        shell.DeleteProfile(shell.Profiles.Single(profile => profile.Name == "acme-vpn"));
        shell.OpenHomeFolder("Acme");
        Assert.Equal(["Shop", "Mail"], Names(shell));
        shell.OpenHomeFolder(string.Empty);
        Assert.Empty(In(shell, "Acme"));

        // Die Liste im Aufklapper nennt jede Gruppe mit ihrem ganzen Pfad, Untergruppen hinter ihrer Gruppe.
        Assert.Equal(
            ["Production", "Development", "Acme/Shop", "Acme/Shop/Prod", "Acme/Mail", "Ungrouped"],
            shell.ProfileGroups.Select(group => group.Name));
        Assert.Equal(["Production", "Development", "Acme", "Acme/Shop", "Acme/Shop/Prod", "Acme/Mail"], shell.GroupPaths);
    }

    [Fact]
    public void A_group_that_is_gone_leaves_the_home_page_at_the_nearest_one_left()
    {
        var shell = Shell();
        shell.OpenHomeFolder("Acme/Shop/Prod");
        Assert.Equal("Acme/Shop/Prod", shell.HomeFolder);

        shell.DeleteProfile(shell.Profiles.Single(profile => profile.Name == "acme-prod"));
        Assert.Equal("Acme/Shop", shell.HomeFolder);

        shell.OpenHomeFolder("Nowhere/At/All");
        Assert.Equal(string.Empty, shell.HomeFolder);
    }

    [Fact]
    public void The_filter_searches_every_level_and_ignores_what_is_collapsed()
    {
        var shell = Shell();
        shell.OpenHomeFolder("Acme/Mail");
        shell.ToggleGroup(shell.HomeGroups[0]);

        shell.HomeFilter = "acme";

        Assert.Equal(["Acme", "Acme/Shop", "Acme/Shop/Prod", "Acme/Mail"], Names(shell));
        Assert.Equal(5, shell.HomeGroups.Sum(group => group.Count));
        Assert.All(shell.HomeGroups, group => Assert.False(group.IsCollapsed));

        shell.HomeFilter = string.Empty;
        Assert.Equal("Acme/Mail", shell.HomeFolder);
    }

    [Fact]
    public void A_collapsed_section_keeps_its_heading_and_is_remembered()
    {
        var settings = new AppSettings();
        var shell = Shell(settings);

        shell.ToggleGroup(shell.HomeGroups.Single(group => group.Name == "Development"));
        shell.ToggleGroup(shell.HomeGroups.Single(group => group.Kind == GroupKind.Favorites));

        var development = shell.HomeGroups.Single(group => group.Name == "Development");
        Assert.True(development.IsCollapsed);
        Assert.Empty(development);
        Assert.Equal(2, development.Total);
        Assert.True(shell.HomeGroups[0].IsCollapsed);
        Assert.Equal(["*favorites", "Development"], settings.CollapsedGroups.Order(StringComparer.Ordinal));

        // Es gibt weiter Profile – nur eben eingeklappt.
        Assert.False(shell.HasNoHomeMatches);

        shell.ToggleGroup(shell.HomeGroups.Single(group => group.Name == "Development"));
        Assert.Equal(2, shell.HomeGroups.Single(group => group.Name == "Development").Count);
        Assert.Equal(["*favorites"], settings.CollapsedGroups);
    }

    [Fact]
    public void Groups_can_be_put_in_an_order_of_their_own_level_by_level()
    {
        var settings = new AppSettings();
        var shell = Shell(settings);
        ProfileGroup Section(string name) => shell.HomeGroups.Single(group => group.Name == name);

        Assert.False(shell.CanMoveGroup(Section("Production"), -1));
        Assert.False(shell.CanMoveGroup(Section("Favorites"), 1));
        Assert.False(shell.CanMoveGroup(Section("Ungrouped"), -1));
        Assert.False(shell.CanMoveGroup(Section("Acme"), 1));

        shell.MoveGroup(Section("Acme"), -1);
        shell.MoveGroup(Section("Acme"), -1);
        Assert.Equal(["Favorites", "Acme", "Production", "Development", "Ungrouped"], Names(shell));

        shell.OpenHomeFolder("Acme");
        shell.MoveGroup(Section("Mail"), -1);
        Assert.Equal(["Acme", "Mail", "Shop"], Names(shell));

        // Beide Ebenen stehen in den Einstellungen und gelten nach einem Neustart weiter – auch im Aufklapper.
        Assert.Equal(["Acme", "Production", "Development", "Acme/Mail", "Acme/Shop"], settings.GroupOrder);
        Assert.Equal(
            ["Acme", "Acme/Mail", "Acme/Shop", "Acme/Shop/Prod", "Production", "Development", "Ungrouped"],
            shell.ProfileGroups.Select(group => group.Name));

        // Eine neue Gruppe steht hinter den geordneten; eine, die es nicht mehr gibt, fällt aus der Liste.
        shell.OpenHomeFolder(string.Empty);
        Add(shell, "new", "Zeta");
        Assert.Equal(["Favorites", "Acme", "Production", "Development", "Zeta", "Ungrouped"], Names(shell));
        foreach (var profile in shell.Profiles.Where(profile => profile.Group == "Development").ToList())
        {
            shell.DeleteProfile(profile);
        }

        Assert.DoesNotContain("Development", settings.GroupOrder);
    }

    [Fact]
    public void A_profile_can_be_moved_to_another_group()
    {
        var shell = Shell();
        var profile = shell.Profiles.Single(profile => profile.Name == "loose");

        Assert.True(shell.MoveProfileToGroup(profile, " Acme / Mail "));

        Assert.Equal("Acme/Mail", shell.Profiles.Single(moved => moved.Name == "loose").Group);
        Assert.DoesNotContain("Ungrouped", Names(shell));
        shell.OpenHomeFolder("Acme");
        Assert.Equal(["acme-mail", "loose"], In(shell, "Mail"));

        // Dorthin, wo es schon liegt, ist nichts zu tun; und zurück nach „ohne Gruppe“ geht auch.
        var moved = shell.Profiles.Single(moved => moved.Name == "loose");
        Assert.True(shell.MoveProfileToGroup(moved, "Acme/Mail"));
        Assert.True(shell.MoveProfileToGroup(moved, string.Empty));
        Assert.Equal(string.Empty, shell.Profiles.Single(moved => moved.Name == "loose").Group);
    }

    [Fact]
    public void Damaged_lists_in_the_settings_are_put_right()
    {
        var settings = new AppSettings { GroupOrder = ["a", "a", string.Empty, null!], CollapsedGroups = null! };

        settings.Sanitize();

        Assert.Equal(["a"], settings.GroupOrder);
        Assert.Empty(settings.CollapsedGroups);
    }
}
