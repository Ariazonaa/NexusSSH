using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Startseite als Tabelle: alle Server in einer Liste, nach einer Spalte sortiert.</summary>
[Collection(LanguageCollection.Name)]
public sealed class HomeTableTests
{
    public HomeTableTests() => L.Init(AppLanguage.English);

    // Vier eigene Profile statt der Beispieldaten, damit die Reihenfolgen eindeutig sind.
    private static ShellViewModel Shell(AppSettings? settings = null)
    {
        var shell = TestShell.Create(out var backend, settings);
        foreach (var profile in shell.Profiles.ToList())
        {
            shell.DeleteProfile(profile);
        }

        void Add(string name, string host, int port, string group, long? last)
            => Assert.NotNull(backend.Profiles.Save(new ServerProfile(string.Empty, name, host, port, "root", group, [], LastConnected: last is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null)));

        Add("delta", "10.0.0.4", 22, "Web", 300);
        Add("Alpha", "10.0.0.9", 22, "db", null);
        Add("charlie", "10.0.0.4", 2222, "Web", 100);
        Add("Bravo", "10.0.0.1", 22, string.Empty, 200);
        shell.HomeFilter = " ";
        shell.HomeFilter = string.Empty;
        return shell;
    }

    private static string[] Names(ShellViewModel shell) => [.. shell.HomeTable.Select(profile => profile.Name)];

    [Fact]
    public void The_table_lists_every_server_by_name_whatever_its_group()
    {
        var shell = Shell();

        Assert.False(shell.HomeAsTable);
        Assert.Equal(["Alpha", "Bravo", "charlie", "delta"], Names(shell));
        Assert.Equal((HomeTableSort.Name, false), (shell.HomeTableSort, shell.HomeTableDescending));
    }

    [Fact]
    public void A_click_on_a_column_sorts_by_it_and_a_second_click_turns_it_round()
    {
        var shell = Shell();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.SortHomeTable(HomeTableSort.Address);
        Assert.Equal(["Bravo", "delta", "charlie", "Alpha"], Names(shell));
        Assert.Contains(nameof(shell.HomeTable), changed);

        shell.SortHomeTable(HomeTableSort.Address);
        Assert.Equal(["Alpha", "charlie", "delta", "Bravo"], Names(shell));
        Assert.True(shell.HomeTableDescending);

        // Gruppen: ohne Gruppe zuerst, bei Gleichstand nach Name.
        shell.SortHomeTable(HomeTableSort.Group);
        Assert.Equal(["Bravo", "Alpha", "charlie", "delta"], Names(shell));

        shell.SortHomeTable(HomeTableSort.Name);
        shell.SortHomeTable(HomeTableSort.Name);
        Assert.Equal(["delta", "charlie", "Bravo", "Alpha"], Names(shell));
    }

    [Fact]
    public void By_last_connection_the_newest_comes_first_and_never_connected_always_last()
    {
        var shell = Shell();

        shell.SortHomeTable(HomeTableSort.Recent);
        Assert.Equal(["delta", "Bravo", "charlie", "Alpha"], Names(shell));

        shell.SortHomeTable(HomeTableSort.Recent);
        Assert.Equal(["charlie", "Bravo", "delta", "Alpha"], Names(shell));
    }

    [Fact]
    public void The_filter_of_the_page_narrows_the_table_too()
    {
        var shell = Shell();
        shell.HomeAsTable = true;

        shell.HomeFilter = "web CHAR";
        Assert.Equal(["charlie"], Names(shell));
        Assert.False(shell.HasNoTableMatches);

        shell.HomeFilter = "nothing-like-this";
        Assert.Empty(shell.HomeTable);
        Assert.True(shell.HasNoTableMatches);

        // Als Kacheln ist das nicht die Sache der Tabelle.
        shell.HomeAsTable = false;
        Assert.False(shell.HasNoTableMatches);
    }

    [Fact]
    public void The_choice_survives_in_the_settings_and_a_new_profile_shows_up()
    {
        var settings = new AppSettings();
        var shell = Shell(settings);
        shell.HomeAsTable = true;
        shell.SortHomeTable(HomeTableSort.Group);
        shell.SortHomeTable(HomeTableSort.Group);

        Assert.Equal((true, HomeTableSort.Group, true), (settings.HomeAsTable, settings.HomeTableSort, settings.HomeTableDescending));

        Assert.NotNull(shell.SaveProfile(new ServerProfile(string.Empty, "echo", "10.0.0.7", 22, "root", "Zoo", [])));
        Assert.Equal("echo", Names(shell)[0]);

        var odd = new AppSettings { HomeTableSort = (HomeTableSort)99 };
        odd.Sanitize();
        Assert.Equal(HomeTableSort.Name, odd.HomeTableSort);
    }
}
