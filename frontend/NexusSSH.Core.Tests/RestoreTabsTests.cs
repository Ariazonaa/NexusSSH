using System.Globalization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Tabs samt Teilungen über einen Neustart: beim Beenden gemerkt, beim Start wieder geöffnet.</summary>
[Collection(LanguageCollection.Name)]
public sealed class RestoreTabsTests
{
    /// <summary>Ein „Neustart“: neue Shell über demselben Backend und denselben Einstellungen.</summary>
    private static ShellViewModel Restart(MockBackend backend, AppSettings settings)
        => new(backend, new ImmediateDispatcher(), settings, new FakePrompts());

    private static ShellViewModel WithTabs(out MockBackend backend, AppSettings settings)
    {
        var shell = TestShell.Create(out backend, settings);
        var profiles = backend.GetProfiles();
        shell.OpenSession(profiles[0]);
        shell.Split(sideBySide: false);
        shell.OpenSession(profiles[1]);
        shell.OpenSession(profiles[2]);
        return shell;
    }

    [Fact]
    public void Tabs_their_split_and_the_active_one_come_back_after_a_restart()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var shell = WithTabs(out var backend, settings);
        shell.ActiveSession = shell.Sessions[1];
        shell.RememberOpenSessions();

        var again = Restart(backend, settings);
        Assert.Empty(again.Sessions);
        again.RestoreOpenSessions();

        Assert.Equal(shell.Sessions.Select(session => session.Profile.Id), again.Sessions.Select(session => session.Profile.Id));
        Assert.Same(again.Sessions[1], again.ActiveSession);
        Assert.Equal([again.Sessions[0], again.Sessions[1]], again.Layout.Panes);
        Assert.False(again.Layout.RootSideBySide());
        Assert.Equal([true, true, false, false], again.Sessions.Select(session => session.IsSplit));

        // Wiederhergestellt ist nicht verbunden: Das geschieht erst, wenn ein Terminal zu sehen ist.
        Assert.All(again.Sessions, session => Assert.Equal(Services.ConnectionState.Idle, session.State));
    }

    [Fact]
    public void A_grid_of_three_keeps_its_order()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var shell = TestShell.Create(out var backend, settings);
        var first = shell.OpenSession(backend.GetProfiles()[0]);
        var second = shell.Split(sideBySide: true)!;
        shell.ActiveSession = first;
        var third = shell.Split(sideBySide: true)!;
        Assert.Equal([first, third, second], shell.Layout.Panes);
        shell.RememberOpenSessions();

        var again = Restart(backend, settings);
        again.RestoreOpenSessions();

        Assert.Equal(3, again.Layout.Panes.Count);
        Assert.Equal(again.Sessions, again.Layout.Panes);
        Assert.True(again.Layout.RootSideBySide());
    }

    [Fact]
    public void Without_the_setting_nothing_is_remembered_or_restored()
    {
        var settings = new AppSettings();
        var shell = WithTabs(out var backend, settings);
        shell.RememberOpenSessions();
        Assert.Empty(settings.OpenTabs);
        Assert.Empty(settings.OpenSplits);

        // Auch ein Rest aus der Zeit, als die Einstellung an war, wird nicht mehr benutzt – und beim Beenden gelöscht.
        settings.OpenTabs = ["web01"];
        var again = Restart(backend, settings);
        again.RestoreOpenSessions();
        Assert.Empty(again.Sessions);
        again.RememberOpenSessions();
        Assert.Empty(settings.OpenTabs);
    }

    [Fact]
    public void The_setting_is_switched_from_the_shell_and_kept()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out var backend, settings);
        Assert.False(shell.RestoreTabs);

        shell.RestoreTabs = true;

        Assert.True(settings.RestoreTabs);
        Assert.True(Restart(backend, settings).RestoreTabs);
    }

    [Fact]
    public void A_profile_that_is_gone_is_skipped_and_its_split_dissolves()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var shell = TestShell.Create(out var backend, settings);
        var profiles = backend.GetProfiles();
        shell.OpenSession(profiles[0]);
        shell.SplitWith(shell.OpenSession(profiles[1]), sideBySide: true);
        shell.OpenSession(profiles[2]);
        shell.RememberOpenSessions();
        Assert.Equal(2, settings.ActiveTab);

        backend.Delete(profiles[1].Id);
        backend.Delete(profiles[2].Id);
        var again = Restart(backend, settings);
        again.RestoreOpenSessions();

        var only = Assert.Single(again.Sessions);
        Assert.Equal(profiles[0].Id, only.Profile.Id);
        Assert.False(only.IsSplit);
        Assert.Same(only, again.ActiveSession);
        Assert.Equal([only], again.Layout.Panes);
    }

    [Fact]
    public void Broken_saved_tabs_do_no_harm()
    {
        var settings = new AppSettings
        {
            RestoreTabs = true,
            OpenTabs = ["web01", "gibt-es-nicht", "web01"],
            OpenSplits = [new SavedSplit([0, 0, 7, -1], true), new SavedSplit([0, 2], false), new SavedSplit([2, 0], true)],
            ActiveTab = 99,
        };
        var shell = TestShell.Create(out _, settings);

        shell.RestoreOpenSessions();

        Assert.Equal(2, shell.Sessions.Count);
        Assert.Same(shell.Sessions[0], shell.ActiveSession);
        Assert.Equal([shell.Sessions[0], shell.Sessions[1]], shell.Layout.Panes);
        Assert.False(shell.Layout.RootSideBySide(), "die erste brauchbare Teilung gilt; eine Sitzung gehört nur zu einer");

        // Ein zweiter Aufruf öffnet nichts doppelt.
        shell.RestoreOpenSessions();
        Assert.Equal(2, shell.Sessions.Count);
    }

    [Fact]
    public void Saved_tabs_survive_the_settings_file()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
        store.Save(new AppSettings { RestoreTabs = true, OpenTabs = ["a", "b", "c"], OpenSplits = [new SavedSplit([0, 2], false)], ActiveTab = 2 });

        var loaded = store.Load();

        Assert.True(loaded.RestoreTabs);
        Assert.Equal(["a", "b", "c"], loaded.OpenTabs);
        var split = Assert.Single(loaded.OpenSplits);
        Assert.Equal([0, 2], split.Tabs);
        Assert.False(split.SideBySide);
        Assert.Equal(2, loaded.ActiveTab);

        File.WriteAllText(Path.Combine(folder.Path, "settings.json"), """{"OpenTabs":[null,""],"OpenSplits":[null,{"Tabs":[1],"SideBySide":true},{"Tabs":null,"SideBySide":true}]}""");
        var repaired = store.Load();
        Assert.Empty(repaired.OpenTabs);
        Assert.Empty(repaired.OpenSplits);
    }

    // Der Baum als Text: Nummern der Tabs, „|“ nebeneinander, „/“ untereinander, dahinter die Anteile.
    private static string Shape(ShellViewModel shell, SplitNode<SessionViewModel>? node = null)
    {
        node ??= shell.Layout.Tree;
        return node switch
        {
            SplitLeaf<SessionViewModel> leaf => shell.Sessions.IndexOf(leaf.Item).ToString(CultureInfo.InvariantCulture),
            SplitBranch<SessionViewModel> branch => "[" + string.Join(branch.SideBySide ? "|" : "/", branch.Children.Select(child => Shape(shell, child)))
                + "]" + string.Join(',', branch.Shares.Select(share => share.ToString("0.##", CultureInfo.InvariantCulture))),
            _ => string.Empty,
        };
    }

    [Fact]
    public void The_tree_and_its_sizes_come_back_after_a_restart()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var shell = TestShell.Create(out var backend, settings);
        var first = shell.OpenSession(backend.GetProfiles()[0]);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: false)!;
        Assert.True(shell.SetShares(first, [], [0.3, 0.7]));
        Assert.True(shell.SetShares(second, [1], [0.6, 0.4]));
        shell.OpenSession(backend.GetProfiles()[1]);
        shell.ActiveSession = third;
        var before = Shape(shell);
        Assert.Equal("[0|[1/2]0.6,0.4]0.3,0.7", before);
        shell.RememberOpenSessions();

        // Über die Datei, wie beim echten Neustart.
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
        store.Save(settings);
        var loaded = store.Load();
        var split = Assert.Single(loaded.OpenSplits);
        Assert.Equal([0, 1, 2], split.Tabs);
        Assert.NotNull(split.Tree);

        var again = Restart(backend, loaded);
        again.RestoreOpenSessions();

        Assert.Same(again.Sessions[2], again.ActiveSession);
        Assert.Equal(before, Shape(again));
        Assert.Equal([true, true, true, false], again.Sessions.Select(session => session.IsSplit));
    }

    [Fact]
    public void A_saved_split_from_before_the_tree_opens_in_its_old_shape()
    {
        using var folder = new TempFolder();
        var file = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(file, """{"RestoreTabs":true,"OpenTabs":["web01","web01","web01","web01"],"OpenSplits":[{"Tabs":[0,1,2],"SideBySide":true}],"ActiveTab":0}""");
        var loaded = new SettingsStore(file).Load();
        Assert.Null(Assert.Single(loaded.OpenSplits).Tree);

        var shell = TestShell.Create(out _, loaded);
        shell.RestoreOpenSessions();

        // Drei: zwei nebeneinander und der dritte in ganzer Breite darunter, wie früher.
        Assert.Equal("[[0|1]0.5,0.5/2]0.5,0.5", Shape(shell));
        Assert.False(shell.Sessions[3].IsSplit);
    }

    [Fact]
    public void A_tree_in_the_settings_that_does_not_fit_its_tabs_falls_back()
    {
        SavedSplitNode Leaf(int tab) => new(Tab: tab);
        SavedSplitNode Row(params SavedSplitNode[] children) => new(SideBySide: true, Shares: [.. children.Select(_ => 1.0 / children.Length)], Children: [.. children]);

        // Tiefer, als es je einen Baum gäbe: Zweige mit einem Kind.
        var deep = Leaf(0);
        for (var level = 0; level < 40; level++)
        {
            deep = new SavedSplitNode(SideBySide: level % 2 == 0, Shares: [1.0], Children: [deep]);
        }

        SavedSplitNode[] broken =
        [
            Row(Leaf(0), Leaf(7), Leaf(2)),
            deep,
            new SavedSplitNode(SideBySide: true, Shares: [double.NaN, double.NaN], Children: [Leaf(0), Leaf(1)]),
            Row(Leaf(0), Leaf(1)),
            Row(Leaf(0), Leaf(0), Leaf(1), Leaf(2)),
        ];
        foreach (var tree in broken)
        {
            var settings = new AppSettings
            {
                RestoreTabs = true,
                OpenTabs = ["web01", "web01", "web01"],
                OpenSplits = [new SavedSplit([0, 1, 2], true, tree)],
            };
            var shell = TestShell.Create(out _, settings);

            shell.RestoreOpenSessions();

            Assert.Equal("[[0|1]0.5,0.5/2]0.5,0.5", Shape(shell));
        }

        // Aus der Datei: Was nicht passt, wird beim Laden verworfen, die Nummern bleiben.
        using var folder = new TempFolder();
        var file = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(file, """{"OpenTabs":["a","b","c"],"OpenSplits":[{"Tabs":[0,1,2],"SideBySide":true,"Tree":{"SideBySide":true,"Shares":[0.5],"Children":[{"Tab":0},{"Tab":1},{"Tab":2}]}}]}""");
        var loaded = new SettingsStore(file).Load();
        var split = Assert.Single(loaded.OpenSplits);
        Assert.Equal([0, 1, 2], split.Tabs);
        Assert.Null(split.Tree);
    }

    [Fact]
    public void A_stored_tree_loses_the_tabs_whose_profile_is_gone_and_keeps_the_rest_arranged()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var shell = TestShell.Create(out var backend, settings);
        var profiles = backend.GetProfiles();
        shell.OpenSession(profiles[0]);
        shell.Split(sideBySide: true);
        shell.Split(sideBySide: false);
        shell.RememberOpenSessions();

        // Der Tab in der Mitte hat kein Profil mehr, die Nummern bleiben.
        settings.OpenTabs[1] = "gibt-es-nicht";
        var again = Restart(backend, settings);
        again.RestoreOpenSessions();

        Assert.Equal(2, again.Sessions.Count);
        Assert.Equal("[0|1]0.5,0.5", Shape(again));
        Assert.All(again.Sessions, session => Assert.True(session.IsSplit));
    }
}
