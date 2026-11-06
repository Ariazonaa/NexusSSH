using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Kennfarbe für eine ganze Gruppe: gilt für alles darin, was keine eigene Farbe hat.</summary>
[Collection(LanguageCollection.Name)]
public sealed class GroupColorTests
{
    public GroupColorTests() => L.Init(AppLanguage.English);

    private static ShellViewModel Shell(AppSettings? settings = null)
    {
        var shell = TestShell.Create(out _, settings);
        Add(shell, "shop-web", "Acme/Shop");
        Add(shell, "shop-prod", "Acme/Shop/Prod", color: "green");
        Add(shell, "mail", "Acme/Mail");
        Add(shell, "loose", string.Empty);
        return shell;
    }

    private static void Add(ShellViewModel shell, string name, string group, string? color = null)
        => Assert.NotNull(shell.SaveProfile(new ServerProfile(string.Empty, name, $"{name}.example.org", 22, "root", group, [], Color: color)));

    private static ServerProfile Named(ShellViewModel shell, string name) => shell.Profiles.Single(profile => profile.Name == name);

    private static ProfileGroup Group(ShellViewModel shell, string path) => shell.ProfileGroups.Single(group => group.Path == path);

    [Fact]
    public void A_group_colour_shows_on_everything_below_that_has_no_colour_of_its_own()
    {
        var shell = Shell();
        Assert.Null(Named(shell, "shop-web").ShownColor);

        shell.SetGroupColor(Group(shell, "Acme/Shop"), "red");

        Assert.Equal("red", Named(shell, "shop-web").ShownColor);
        Assert.Equal("green", Named(shell, "shop-prod").ShownColor);
        Assert.Null(Named(shell, "mail").ShownColor);
        Assert.Null(Named(shell, "loose").ShownColor);

        // Im Profil selbst steht davon nichts – die Farbe gehört der Gruppe.
        Assert.Null(Named(shell, "shop-web").Color);
        Assert.Equal("red", shell.OwnGroupColor(Group(shell, "Acme/Shop")));
    }

    [Fact]
    public void The_nearest_group_with_a_colour_wins_and_removing_it_falls_back()
    {
        var shell = Shell();
        Add(shell, "deep", "Acme/Shop/Prod/East");
        Add(shell, "vpn", "Acme");
        shell.SetGroupColor(Group(shell, "Acme"), "blue");
        shell.SetGroupColor(Group(shell, "Acme/Shop"), "orange");

        Assert.Equal("orange", Named(shell, "deep").ShownColor);
        Assert.Equal("blue", Named(shell, "mail").ShownColor);
        Assert.Equal("orange", shell.GroupColorOf("Acme/Shop/Prod/East"));
        Assert.Null(shell.OwnGroupColor(Group(shell, "Acme/Shop/Prod/East")));

        shell.SetGroupColor(Group(shell, "Acme/Shop"), string.Empty);
        Assert.Equal("blue", Named(shell, "deep").ShownColor);

        shell.SetGroupColor(Group(shell, "Acme"), "no-such-colour");
        Assert.Null(Named(shell, "deep").ShownColor);
        Assert.Empty(shell.Settings.GroupColors);
    }

    [Fact]
    public void A_group_that_is_gone_takes_its_colour_with_it_and_a_settings_file_by_hand_is_tidied()
    {
        var shell = Shell();
        shell.SetGroupColor(Group(shell, "Acme/Mail"), "violet");
        Assert.True(shell.MoveProfileToGroup(Named(shell, "mail"), "Acme/Shop"));
        Assert.DoesNotContain("Acme/Mail", shell.Settings.GroupColors.Keys);

        var settings = new AppSettings { GroupColors = new() { ["Acme"] = "RED?", ["Ops"] = "green", [string.Empty] = "blue" } };
        settings.Sanitize();
        Assert.Equal(new Dictionary<string, string> { ["Ops"] = "green" }, settings.GroupColors);
    }

    [Fact]
    public void A_session_keeps_the_colour_it_was_opened_with_and_the_terminal_is_marked_with_it()
    {
        var shell = Shell();
        shell.SetGroupColor(Group(shell, "Acme/Shop"), "red");

        var session = shell.OpenSession(Named(shell, "shop-web"));
        Assert.Equal("red", session.Profile.ShownColor);

        var open = JsonDocument.Parse(TerminalMessages.Open(session.Id, ProfileColors.Find(session.Profile.ShownColor).Hex)).RootElement;
        Assert.Equal("#e5484d", open.GetProperty("mark").GetString());

        // Ohne Farbe schickt die App keine.
        Assert.False(JsonDocument.Parse(TerminalMessages.Open("x")).RootElement.TryGetProperty("mark", out _));
    }
}
