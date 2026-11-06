using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Mehrere Server der Startseite auf einmal: verbinden, verschieben, Schlagworte, löschen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class BulkProfileTests
{
    public BulkProfileTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Several_servers_connect_at_once_each_only_once()
    {
        var shell = TestShell.Create(out var backend);
        var profiles = backend.GetProfiles();

        // Ein Favorit steht auf der Startseite zweimal – verbunden wird er einmal.
        Assert.Equal(2, shell.ConnectAll([profiles[0], profiles[1], profiles[0]]));
        Assert.Equal([profiles[0].Id, profiles[1].Id], shell.Sessions.Select(session => session.Profile.Id));

        // Nichts gewählt, oder ein Profil, das es nicht mehr gibt: nichts geschieht.
        Assert.Equal(0, shell.ConnectAll([]));
        Assert.Equal(0, shell.ConnectAll([profiles[0] with { Id = "gone" }]));
        Assert.Equal(2, shell.Sessions.Count);
    }

    [Fact]
    public void A_click_does_not_open_more_sessions_than_the_limit()
    {
        var shell = TestShell.Create(out _);
        for (var number = 0; number < ShellViewModel.MaxConnectAtOnce + 3; number++)
        {
            shell.SaveProfile(new ServerProfile(string.Empty, $"node{number}", $"node{number}.example", 22, "u", "cluster", []));
        }

        var cluster = shell.Profiles.Where(profile => profile.Group == "cluster").ToList();
        Assert.Equal(ShellViewModel.MaxConnectAtOnce, shell.ConnectAll(cluster));
        Assert.Equal(ShellViewModel.MaxConnectAtOnce, shell.Sessions.Count);
        Assert.Contains(shell.Problems, entry => entry.Text == $"Only the first {ShellViewModel.MaxConnectAtOnce} servers were connected.");
    }

    [Fact]
    public void Servers_move_into_a_group_or_out_of_it()
    {
        var shell = TestShell.Create(out var backend);
        var chosen = backend.GetProfiles().Take(3).ToList();

        Assert.Equal(3, shell.MoveToGroup(chosen, "  Kunde/Projekt "));
        Assert.All(chosen, profile => Assert.Equal("Kunde/Projekt", Stored(shell, profile).Group));

        // Wer schon dort liegt, ändert sich nicht – die Auswahl ist dabei älter als die Änderung.
        Assert.Equal(0, shell.MoveToGroup(chosen, "Kunde/Projekt"));
        Assert.Equal(3, shell.MoveToGroup(chosen, null));
        Assert.All(chosen, profile => Assert.Equal(string.Empty, Stored(shell, profile).Group));
    }

    [Fact]
    public void Tags_are_added_and_removed_without_touching_the_others()
    {
        var shell = TestShell.Create(out _);
        var first = shell.SaveProfile(new ServerProfile(string.Empty, "a", "a.example", 22, "u", "", ["web", "Prod"]))!;
        var second = shell.SaveProfile(new ServerProfile(string.Empty, "b", "b.example", 22, "u", "", ["db"]))!;

        Assert.Equal(2, shell.ChangeTags([first, second], "nginx; web, NGINX", "prod"));
        Assert.Equal(["web", "nginx"], Stored(shell, first).Tags);
        Assert.Equal(["db", "nginx", "web"], Stored(shell, second).Tags);

        // Noch einmal dasselbe ändert nichts mehr; nur entfernen geht auch.
        Assert.Equal(0, shell.ChangeTags([first, second], "nginx", "prod"));
        Assert.Equal(1, shell.ChangeTags([first, second], null, "DB"));
        Assert.Equal(["nginx", "web"], Stored(shell, second).Tags);

        Assert.Equal(["a", "b c"], ShellViewModel.ParseTags(" a ,, b c ; A "));
        Assert.Empty(ShellViewModel.ParseTags(null));
    }

    [Fact]
    public void Several_servers_are_deleted_together()
    {
        var shell = TestShell.Create(out var backend);
        var all = backend.GetProfiles();
        var chosen = all.Take(2).ToList();

        Assert.Equal(2, shell.DeleteProfiles([.. chosen, chosen[0]]));
        Assert.Equal(all.Count - 2, shell.Profiles.Count);
        Assert.DoesNotContain(shell.Profiles, profile => chosen.Any(gone => gone.Id == profile.Id));

        // Schon gelöscht: nichts mehr zu tun.
        Assert.Equal(0, shell.DeleteProfiles(chosen));
    }

    private static ServerProfile Stored(ShellViewModel shell, ServerProfile profile) => shell.Profiles.Single(stored => stored.Id == profile.Id);
}
