using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public sealed class ShellViewModelTests
{
    [Fact]
    public void Starts_without_a_session()
    {
        var shell = TestShell.Create(out _);

        Assert.Empty(shell.Sessions);
        Assert.Null(shell.ActiveSession);
        Assert.True(shell.HasNoActiveSession);
    }

    [Fact]
    public void Opening_a_session_makes_it_active_and_loads_its_files()
    {
        var shell = TestShell.Create(out var backend);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");

        var session = shell.OpenSession(web);
        Assert.Same(session, shell.ActiveSession);
        Assert.True(shell.HasActiveSession);
        Assert.False(session.Files.IsAvailable);

        // In der App startet das Terminal die Sitzung, sobald es seine Größe kennt.
        session.Start();

        Assert.Equal("/var/www", session.Files.CurrentPath);
        Assert.NotEmpty(session.Files.Entries);
        Assert.Equal("Ubuntu 24.04.1 LTS", session.Inspector.OsName);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("web01"));
    }

    [Fact]
    public void The_active_tab_decides_which_server_the_side_panes_show()
    {
        var shell = TestShell.Create(out var backend);
        var profiles = backend.GetProfiles();
        var web = shell.OpenSession(profiles.Single(profile => profile.Id == "web01"));
        var database = shell.OpenSession(profiles.Single(profile => profile.Id == "database01"));
        web.Start();
        database.Start();

        Assert.Equal("database01", shell.ActiveSession!.Inspector.Profile.Name);
        Assert.Equal("/home/admin", shell.ActiveSession.Files.CurrentPath);

        shell.ActiveSession = web;

        Assert.Equal("web01", shell.ActiveSession.Inspector.Profile.Name);
        Assert.Equal("/var/www", shell.ActiveSession.Files.CurrentPath);
        Assert.NotSame(web.Files, database.Files);
    }

    [Fact]
    public void The_same_server_can_be_open_twice_with_independent_state()
    {
        var shell = TestShell.Create(out var backend);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");

        var first = shell.OpenSession(web);
        var second = shell.OpenSession(web);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotSame(first.Terminal, second.Terminal);
    }

    [Fact]
    public void Closing_the_active_session_activates_its_neighbour()
    {
        var shell = TestShell.Create(out var backend);
        var profiles = backend.GetProfiles();
        var first = shell.OpenSession(profiles[0]);
        var second = shell.OpenSession(profiles[1]);
        var third = shell.OpenSession(profiles[2]);

        shell.ActiveSession = second;
        shell.CloseSession(second);
        Assert.Same(third, shell.ActiveSession);

        shell.CloseSession(third);
        Assert.Same(first, shell.ActiveSession);

        shell.CloseSession(first);
        Assert.Null(shell.ActiveSession);
        Assert.Empty(shell.Sessions);
    }

    [Fact]
    public void Closing_a_background_session_keeps_the_active_one()
    {
        var shell = TestShell.Create(out var backend);
        var profiles = backend.GetProfiles();
        var first = shell.OpenSession(profiles[0]);
        var second = shell.OpenSession(profiles[1]);

        shell.CloseSession(first);

        Assert.Same(second, shell.ActiveSession);
    }

    [Theory]
    [InlineData("web", new[] { "web01", "staging" })] // Name und Tag
    [InlineData("10.0.1", new[] { "web01", "database01" })] // Host
    [InlineData("DEVELOPMENT", new[] { "staging", "test01" })] // Gruppe, ohne Rücksicht auf Groß/Klein
    [InlineData("postgres", new[] { "database01" })] // Tag
    [InlineData("nothing-like-this", new string[0])]
    public void Search_matches_name_host_user_group_and_tags(string query, string[] expected)
    {
        var shell = TestShell.Create(out _);

        Assert.Equal(expected, shell.SearchProfiles(query).Select(profile => profile.Name));
    }

    [Fact]
    public void An_empty_search_lists_everything_with_favourites_first()
    {
        var shell = TestShell.Create(out var backend);

        var all = shell.SearchProfiles("  ");

        Assert.Equal(backend.GetProfiles().Count, all.Count);
        Assert.True(all[0].IsFavorite);
    }

    [Fact]
    public void Profiles_are_grouped_as_in_the_spec()
    {
        var shell = TestShell.Create(out _);

        Assert.Equal(["Production", "Development"], shell.ProfileGroups.Select(group => group.Name));
    }

    [Fact]
    public void Pane_visibility_comes_from_and_goes_back_to_the_settings()
    {
        var settings = new AppSettings { ShowSnippets = false };
        var shell = TestShell.Create(out _, settings);
        Assert.False(shell.IsSnippetsVisible);
        Assert.True(shell.IsSftpVisible);

        shell.IsSftpVisible = false;
        shell.IsSnippetsVisible = true;

        Assert.False(settings.ShowSftp);
        Assert.True(settings.ShowSnippets);
    }
}
