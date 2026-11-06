using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ausschlüsse: Muster, die ein Ordner-Transfer und ein Abgleich auslassen – und die Auskunft vorab, was fehlen würde.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferExcludesTests
{
    public TransferExcludesTests() => L.Init(AppLanguage.English);

    /// <summary>Dieselbe Tabelle steht in den Tests des Kerns (<c>excludes.rs</c>): Beide Seiten müssen dasselbe sagen.</summary>
    [Theory]
    [InlineData(".git", true)]
    [InlineData("src/.git", true)]
    [InlineData(".gitignore", false)]
    [InlineData("node_modules", true)]
    [InlineData("web/node_modules", true)]
    [InlineData("node_modules_old", false)]
    [InlineData("x.tmp", true)]
    [InlineData("deep/er/Y.TMP", true)]
    [InlineData("x.tmp.bak", false)]
    [InlineData("thumbs.db", true)]
    [InlineData("build/app.map", true)]
    [InlineData("build/x/app.map", false)]
    [InlineData("src/build/app.map", false)]
    [InlineData("dist", true)]
    [InlineData("src/dist", false)]
    [InlineData("abc", true)]
    [InlineData("ac", false)]
    [InlineData("src/main.rs", false)]
    [InlineData("\U0001F600.txt", true)]
    [InlineData("ab.txt", false)]
    [InlineData("Ärger.LOG", true)]
    [InlineData("ärger.log", false)]
    [InlineData("", false)]
    public void Names_match_at_any_depth_and_paths_only_where_they_say(string path, bool skipped)
    {
        var rules = new TransferExcludes([".git", "node_modules", "*.tmp", "Thumbs.db", "build/*.map", "/dist/", "a?c", "?.txt", "\u00C4*.log"]);
        Assert.Equal(skipped, rules.Skips(path));
    }

    /// <summary>
    /// Ein Eintrag, auf den mehrere Muster passen, steht in der Rückfrage unter jedem: Mit geht er erst, wenn keines
    /// davon mehr gilt – stünde er nur unter dem ersten, bliebe er trotz „mitnehmen“ aus.
    /// </summary>
    [Fact]
    public void An_entry_that_several_patterns_match_is_listed_under_each()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "cache.tmp"), "x");
        File.WriteAllText(Path.Combine(folder.Path, "other.tmp"), "x");
        var rules = new TransferExcludes(["*.tmp", "cache.*", "/cache.tmp"]);

        Assert.Equal(["*.tmp", "cache.*", "/cache.tmp"], rules.PatternsFor("cache.tmp"));
        Assert.Equal(["*.tmp", "cache.*"], rules.PatternsFor("sub/cache.tmp"));
        Assert.Empty(rules.PatternsFor("cache"));

        var groups = rules.Group(rules.ScanLocal(folder.Path));
        Assert.Equal([("*.tmp", 2), ("cache.*", 1), ("/cache.tmp", 1)], groups.Select(group => (group.Pattern, group.Files)));

        // Nur das erste Muster abgewählt: Die Datei bleibt aus, und die Rückfrage hat das gezeigt.
        Assert.True(new TransferExcludes(["cache.*", "/cache.tmp"]).Skips("cache.tmp"));
    }

    /// <summary>
    /// Ein gemerkter Transfer nennt dem Kern seine Muster unverändert: Was keines ist, lehnt der ab – aussortiert
    /// würde „Wiederholen“ mehr übertragen als der erste Versuch.
    /// </summary>
    [Fact]
    public void A_saved_transfer_hands_its_patterns_on_as_they_are()
    {
        var request = new TransferRequest(1, "web01", TransferDirection.Upload, "proj", @"C:\work\proj", "/srv", Exclude: ["node_modules", "../x"]);

        var saved = SavedTransfer.From(new TransferItem("1", "proj", "web01", TransferDirection.Upload, TransferState.Failed, 0), request);

        Assert.Equal(["node_modules", "../x"], saved.ToRequest().Exclude);
    }

    [Fact]
    public void What_cannot_be_a_pattern_is_dropped_and_named()
    {
        Assert.All(new[] { "", "  ", "/", "a//b", "../x", "a/./b", "a\\b", "a\u0007b", new string('x', TransferExcludes.MaxPattern + 1) }, bad => Assert.Null(TransferExcludes.Normalize(bad)));
        Assert.Equal("dist", TransferExcludes.Normalize("  /Dist/ "));

        // Aus einem Textfeld: eine Zeile je Muster, jedes einmal; `dist` und `/dist` sind zweierlei.
        Assert.Equal([".git", "*.tmp", "dist", "/dist"], TransferExcludes.Parse(".git\r\n\n  *.tmp \n../x\n.git\ndist\n/dist\n.GIT"));
        Assert.Equal(["../x", "a\\b"], TransferExcludes.Rejected(".git\n../x\n\na\\b"));
        Assert.Equal(TransferExcludes.MaxPatterns, TransferExcludes.Clean(Enumerable.Range(0, 200).Select(index => $"p{index}")).Count);

        Assert.True(new TransferExcludes(null).IsEmpty);
        Assert.False(new TransferExcludes([]).Skips("anything"));
        Assert.Equal("Not a pattern: ../x", ShellViewModel.ExcludePatternsRejected("ok\n../x"));
        Assert.Equal(string.Empty, ShellViewModel.ExcludePatternsRejected("ok\n*.tmp"));
    }

    [Fact]
    public void A_path_is_left_out_when_a_folder_above_it_is()
    {
        var rules = new TransferExcludes(["node_modules", "build/*.map"]);

        Assert.True(rules.SkipsAnyLevel("web/node_modules/left-pad/index.js"));
        Assert.True(rules.SkipsAnyLevel("build/app.map"));
        Assert.False(rules.SkipsAnyLevel("web/app.js"));
        Assert.False(rules.SkipsAnyLevel("build/app.js"));
    }

    [Fact]
    public async Task A_local_folder_is_searched_without_entering_what_is_left_out()
    {
        using var folder = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(folder.Path, "proj")).FullName;
        void Write(string relative)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        Write("src/main.rs");
        Write("src/old.tmp");
        Write(".git/config");
        Write(".git/objects/aa/bb");
        Write("web/node_modules/left-pad/index.js");
        Write("web/node_modules/left-pad/x.tmp");
        Write("api/node_modules/a/b.js");
        Write("notes.TMP");
        var rules = new TransferExcludes([".git", "node_modules", "*.tmp", "nothing-here"]);

        var found = await ShellViewModel.FindExcludedLocallyAsync(rules, [root, Path.Combine(folder.Path, "missing")]);

        // Jeweils der oberste Eintrag; die .tmp-Datei in node_modules zählt nicht eigens.
        Assert.Equal(
            [("proj/.git", true), ("proj/api/node_modules", true), ("proj/notes.TMP", false), ("proj/src/old.tmp", false), ("proj/web/node_modules", true)],
            found.Select(item => (item.Path, item.IsDirectory)).OrderBy(item => item.Path, StringComparer.Ordinal));

        // Die Zeilen der Rückfrage: je Muster, in der Reihenfolge der Muster, nur was etwas trifft.
        var groups = rules.Group(found);
        Assert.Equal([(".git", 1, 0), ("node_modules", 2, 0), ("*.tmp", 0, 2)], groups.Select(group => (group.Pattern, group.Folders, group.Files)));
        Assert.Equal("proj/.git", groups[0].Example);

        // Eine einzelne Datei oder ein Ordner ohne Treffer: nichts zu fragen.
        Assert.Empty(await ShellViewModel.FindExcludedLocallyAsync(rules, [Path.Combine(root, "src", "main.rs")]));
        Assert.Empty(await ShellViewModel.FindExcludedLocallyAsync(new TransferExcludes(["*.log"]), [root]));
    }

    [Fact]
    public async Task The_server_is_asked_what_would_be_left_out_and_the_choice_reaches_the_transfer()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        session.Start();
        await Task.Yield();
        var files = Assert.IsType<MockRemoteFileSystem>(session.Files.FileSystem);
        var here = session.Files.CurrentPath.TrimEnd('/');
        files.File($"{here}/proj/src/main.rs", size: 1, hoursAgo: 1)
            .File($"{here}/proj/.git/config", size: 1, hoursAgo: 1)
            .File($"{here}/proj/web/node_modules/a/index.js", size: 1, hoursAgo: 1)
            .File($"{here}/proj/old.tmp", size: 1, hoursAgo: 1);
        await session.Files.LoadAsync();
        var folder = session.Files.Entries.Single(entry => entry.Name == "proj");

        var found = await session.Files.FindExcludedAsync(shell.ExcludeRules, [folder]);

        Assert.Equal(
            [(".git", "proj/.git", true), ("*.tmp", "proj/old.tmp", false), ("node_modules", "proj/web/node_modules", true)],
            found.Select(item => (item.Pattern, item.Path, item.IsDirectory)).OrderBy(item => item.Path, StringComparer.Ordinal));

        // Der Benutzer nimmt node_modules mit: Für diesen Transfer gelten die beiden anderen Muster.
        session.Files.Download([folder], "D:\\downloads", exclude: [".git", "*.tmp"]);
        Assert.Equal([".git", "*.tmp"], files.Excludes[^1]);

        // Ohne Muster wird der Server nicht gefragt.
        Assert.Empty(await session.Files.FindExcludedAsync(new TransferExcludes([]), [folder]));
    }

    [Fact]
    public void The_setting_starts_with_the_usual_three_and_keeps_only_patterns()
    {
        var shell = TestShell.Create(out _);
        Assert.Equal(".git\nnode_modules\n*.tmp", shell.ExcludePatternsText);
        Assert.True(shell.IsDefaultSetting("ExcludePatterns"));

        List<string> changed = [];
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        shell.ExcludePatternsText = "dist\n../oops\n\n*.log";
        Assert.Equal(["dist", "*.log"], shell.Settings.ExcludePatterns);
        Assert.Equal(["dist", "*.log"], shell.ExcludeRules.Patterns);
        Assert.Contains(nameof(ShellViewModel.ExcludePatternsText), changed);
        Assert.False(shell.IsDefaultSetting("ExcludePatterns"));

        shell.ResetSetting("ExcludePatterns");
        Assert.Equal(TransferExcludes.Default, shell.Settings.ExcludePatterns);

        // Leer heißt: Ein Ordner geht immer ganz.
        shell.ExcludePatternsText = string.Empty;
        Assert.True(shell.ExcludeRules.IsEmpty);

        // Von der Platte: nur, was Muster sein können.
        Assert.Equal(["ok"], new AppSettings { ExcludePatterns = ["ok", "../x", "", "ok"] }.Sanitize().ExcludePatterns);
    }
}
