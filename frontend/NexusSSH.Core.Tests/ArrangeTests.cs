using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ordnen auf dem Server: kopieren, verschieben, duplizieren, mehrere umbenennen, zurücknehmen, Prüfsumme.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ArrangeTests
{
    public ArrangeTests() => L.Init(AppLanguage.English);

    private static async Task<(SftpBrowserViewModel Browser, MockRemoteFileSystem Files)> BrowserAsync()
    {
        var files = new MockRemoteFileSystem("/var/www", new MockTransferQueue(), "web01")
            .File("/var/www/html/index.html", 100, hoursAgo: 1)
            .File("/var/www/html/css/site.css", 50, hoursAgo: 1)
            .File("/var/www/nginx.conf", 200, hoursAgo: 2)
            .File("/var/www/notes.txt", 10, hoursAgo: 3);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();
        return (browser, files);
    }

    private static IEnumerable<string> Names(SftpBrowserViewModel browser) => browser.Entries.Select(entry => entry.Name);

    [Fact]
    public async Task One_rule_for_everything_that_already_exists_instead_of_a_question_per_file()
    {
        var (browser, files) = await BrowserAsync();

        // Überspringen und „nur Neuere“ entscheidet das Backend je Datei – der Browser reicht die Regel weiter.
        browser.Upload([LocalPath.Of("up", "notes.txt"), LocalPath.Of("up", "new.txt")], ExistingRule.SkipSame);
        Assert.Equal([(TransferKeep.Same, null), (TransferKeep.Same, null)], files.Rules.Select(rule => (rule.Keep, rule.Name)));

        // Umbenennen: Was es schon gibt, bleibt; das Neue kommt daneben – auch zwei gleichnamige nacheinander.
        files.Rules.Clear();
        browser.Upload([LocalPath.Of("up", "notes.txt"), LocalPath.Of("other", "notes.txt"), LocalPath.Of("up", "new.txt"), LocalPath.Of("up", "html")], ExistingRule.Rename);
        Assert.Equal(["notes (2).txt", "notes (3).txt", null, "html (2)"], files.Rules.Select(rule => rule.Name));
        Assert.All(files.Rules, rule => Assert.Equal(TransferKeep.None, rule.Keep));

        // Ohne Regel – und mit „überschreiben“ – bleibt alles beim Alten.
        files.Rules.Clear();
        browser.Upload([LocalPath.Of("up", "notes.txt")]);
        browser.Upload([LocalPath.Of("up", "notes.txt")], ExistingRule.Overwrite);
        Assert.All(files.Rules, rule => Assert.Equal((TransferKeep.None, null), (rule.Keep, rule.Name)));

        // Herunterladen: Umbenannt wird, was im lokalen Ordner schon liegt.
        var here = Directory.CreateTempSubdirectory("nexus-rule-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(here, "notes.txt"), "old");
            Directory.CreateDirectory(Path.Combine(here, "notes (2).txt"));
            files.Rules.Clear();
            var entries = browser.Entries.Where(entry => entry.Name is "notes.txt" or "nginx.conf").OrderBy(entry => entry.Name).ToList();
            browser.Download(entries, here, ExistingRule.Rename);
            Assert.Equal([null, "notes (3).txt"], files.Rules.Select(rule => rule.Name));

            files.Rules.Clear();
            browser.Download(entries, here, ExistingRule.Newer);
            Assert.All(files.Rules, rule => Assert.Equal((TransferKeep.Newer, null), (rule.Keep, rule.Name)));
        }
        finally
        {
            Directory.Delete(here, recursive: true);
        }

        Assert.Equal(TransferKeep.Same, ExistingRule.SkipSame.Keep());
        Assert.Equal(TransferKeep.None, ExistingRule.Ask.Keep());
        Assert.Equal(ExistingRule.Ask, new AppSettings { TransferRule = (ExistingRule)99 }.Sanitize().TransferRule);
        Assert.Equal(ExistingRule.Ask, new AppSettings().TransferRule);
    }

    [Fact]
    public async Task Copied_entries_are_pasted_into_another_folder_with_everything_below_them()
    {
        var (browser, files) = await BrowserAsync();
        Assert.False(browser.CanPaste);

        browser.Hold(browser.Entries.Where(entry => entry.Name is "html" or "notes.txt"), cut: false);
        Assert.True(browser.CanPaste);
        await browser.CreateFolderAsync("backup");
        await browser.NavigateToAsync("/var/www/backup");
        Assert.Empty(browser.PasteConflicts());

        Assert.True(await browser.PasteAsync());

        Assert.Equal(["html", "notes.txt"], Names(browser));
        Assert.Equal(["css", "index.html"], (await files.ListAsync("/var/www/backup/html", TestContext.Current.CancellationToken)).Select(entry => entry.Name));
        // Kopiert, nicht verschoben: Das Original ist noch da, und es lässt sich noch einmal einfügen.
        Assert.Contains("html", (await files.ListAsync("/var/www", TestContext.Current.CancellationToken)).Select(entry => entry.Name));
        Assert.True(browser.CanPaste);
        Assert.False(browser.CanUndo);
    }

    [Fact]
    public async Task Pasting_into_the_same_folder_makes_a_copy_with_a_name_of_its_own()
    {
        var (browser, _) = await BrowserAsync();
        browser.Hold(browser.Entries.Where(entry => entry.Name == "notes.txt"), cut: false);

        Assert.True(await browser.PasteAsync());
        Assert.True(await browser.PasteAsync());

        Assert.Equal(["html", "nginx.conf", "notes (2).txt", "notes (3).txt", "notes.txt"], Names(browser));
    }

    [Fact]
    public async Task Cut_entries_move_and_the_move_can_be_undone()
    {
        var (browser, files) = await BrowserAsync();
        browser.Hold(browser.Entries.Where(entry => entry.Name == "notes.txt"), cut: true);

        // An Ort und Stelle gibt es nichts zu verschieben.
        Assert.False(await browser.PasteAsync());

        await browser.NavigateToAsync("/var/www/html");
        Assert.True(await browser.PasteAsync());

        Assert.Contains("notes.txt", Names(browser));
        Assert.DoesNotContain("notes.txt", (await files.ListAsync("/var/www", TestContext.Current.CancellationToken)).Select(entry => entry.Name));
        Assert.False(browser.CanPaste);
        Assert.True(browser.CanUndo);

        Assert.True(await browser.UndoAsync());

        Assert.DoesNotContain("notes.txt", Names(browser));
        Assert.Contains("notes.txt", (await files.ListAsync("/var/www", TestContext.Current.CancellationToken)).Select(entry => entry.Name));
        Assert.False(browser.CanUndo);
    }

    [Fact]
    public async Task What_already_lies_at_the_target_is_only_replaced_on_request()
    {
        var (browser, files) = await BrowserAsync();
        files.File("/var/www/html/notes.txt", 999, hoursAgo: 9);
        browser.Hold(browser.Entries.Where(entry => entry.Name is "notes.txt" or "nginx.conf"), cut: false);
        await browser.NavigateToAsync("/var/www/html");

        Assert.Equal(["notes.txt"], browser.PasteConflicts());

        // Ohne Erlaubnis bleibt, was dort liegt – der andere Eintrag kommt trotzdem an.
        Assert.True(await browser.PasteAsync(replace: false));
        Assert.Equal(999, browser.Entries.Single(entry => entry.Name == "notes.txt").Size);
        Assert.Contains("nginx.conf", Names(browser));

        Assert.True(await browser.PasteAsync(replace: true));
        Assert.Equal(10, browser.Entries.Single(entry => entry.Name == "notes.txt").Size);
    }

    [Fact]
    public async Task Duplicating_puts_a_copy_next_to_each_entry()
    {
        var (browser, files) = await BrowserAsync();

        Assert.True(await browser.DuplicateAsync([.. browser.Entries.Where(entry => entry.Name is "html" or "nginx.conf")]));

        Assert.Equal(["html", "html (2)", "nginx (2).conf", "nginx.conf", "notes.txt"], Names(browser));
        Assert.Equal(["css", "index.html"], (await files.ListAsync("/var/www/html (2)", TestContext.Current.CancellationToken)).Select(entry => entry.Name));
    }

    [Fact]
    public async Task A_single_rename_can_be_undone_too()
    {
        var (browser, _) = await BrowserAsync();

        Assert.True(await browser.RenameAsync(browser.Entries.Single(entry => entry.Name == "notes.txt"), "todo.txt"));
        Assert.True(browser.CanUndo);
        Assert.True(await browser.UndoAsync());

        Assert.Contains("notes.txt", Names(browser));
        Assert.DoesNotContain("todo.txt", Names(browser));
    }

    [Fact]
    public void The_plan_replaces_text_applies_the_pattern_and_numbers_the_entries()
    {
        string[] names = ["IMG_0007.JPG", "IMG_0008.JPG", ".hidden"];

        var replaced = RenamePlan.Build(names, new RenameRule(Find: "img_", Replace: "trip-", IgnoreCase: true), names);
        Assert.Equal(["trip-0007.JPG", "trip-0008.JPG", ".hidden"], replaced.Select(line => line.To));
        Assert.Equal([true, true, false], replaced.Select(line => line.Changes));
        Assert.True(RenamePlan.CanApply(replaced));

        var numbered = RenamePlan.Build(names, new RenameRule(Pattern: "holiday-{n}{ext}", Start: 9, Digits: 3), names);
        Assert.Equal(["holiday-009.JPG", "holiday-010.JPG", "holiday-011"], numbered.Select(line => line.To));

        // Ohne Regel ändert sich nichts – dann gibt es auch nichts auszuführen.
        Assert.False(RenamePlan.CanApply(RenamePlan.Build(names, new RenameRule(), names)));
    }

    [Fact]
    public void The_plan_says_what_stands_in_the_way()
    {
        string[] names = ["a.txt", "b.txt"];
        string[] folder = ["a.txt", "b.txt", "c.txt"];

        Assert.Equal(
            [RenameProblem.Twice, RenameProblem.Twice],
            RenamePlan.Build(names, new RenameRule(Pattern: "same{ext}"), folder).Select(line => line.Problem));
        Assert.Equal(
            [RenameProblem.Exists, RenameProblem.None],
            RenamePlan.Build(names, new RenameRule(Find: "a", Replace: "c"), folder).Select(line => line.Problem));
        Assert.Equal(
            [RenameProblem.Invalid, RenameProblem.Invalid],
            RenamePlan.Build(names, new RenameRule(Pattern: "x/{name}"), folder).Select(line => line.Problem));

        // Die Namen untereinander zu tauschen ist kein Zusammenstoß: Beide werden ja umbenannt.
        var swapped = RenamePlan.Build(["a.txt", "b.txt"], new RenameRule(Find: "a", Replace: "b"), folder);
        Assert.Equal(RenameProblem.Twice, swapped[0].Problem);
    }

    [Fact]
    public async Task Several_entries_are_renamed_at_once_even_when_they_swap_names_and_it_can_be_undone()
    {
        var (browser, files) = await BrowserAsync();

        Assert.True(await browser.RenameManyAsync([("nginx.conf", "notes.txt"), ("notes.txt", "nginx.conf")]));

        // Die Größen zeigen, dass die Namen getauscht sind – und kein Zwischenname ist übrig.
        Assert.Equal(["html", "nginx.conf", "notes.txt"], Names(browser));
        Assert.Equal((10, 200), (browser.Entries.Single(entry => entry.Name == "nginx.conf").Size, browser.Entries.Single(entry => entry.Name == "notes.txt").Size));

        Assert.True(await browser.UndoAsync());
        Assert.Equal((200, 10), (browser.Entries.Single(entry => entry.Name == "nginx.conf").Size, browser.Entries.Single(entry => entry.Name == "notes.txt").Size));
        Assert.Equal(3, (await files.ListAsync("/var/www", TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public void The_dialog_previews_every_change_and_only_lets_a_clean_plan_through()
    {
        var rename = new BatchRenameViewModel(["a.txt", "b.txt"], ["a.txt", "b.txt", "c.txt"]);
        Assert.False(rename.CanApply);
        Assert.Equal("0 of 2 will be renamed.", rename.Summary);

        rename.Pattern = "doc-{n}{ext}";
        Assert.True(rename.CanApply);
        Assert.Equal(["a.txt → doc-1.txt", "b.txt → doc-2.txt"], rename.Lines.Select(line => line.ToString()));
        Assert.Equal([("a.txt", "doc-1.txt"), ("b.txt", "doc-2.txt")], rename.Changes);

        rename.Pattern = "c{ext}";
        Assert.False(rename.CanApply);
        Assert.Equal("2 names do not work like this.", rename.Summary);
        Assert.All(rename.Lines, line => Assert.True(line.HasProblem));
    }

    [Fact]
    public async Task A_local_file_has_the_same_checksum_the_tools_print()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexusssh-sum-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "abc", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Checksums.OfFileAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_usage_lists_the_largest_first_and_folders_can_be_entered()
    {
        var (_, files) = await BrowserAsync();
        var usage = new FolderUsageViewModel(files, "/var/www");

        await usage.LoadAsync();

        // html (150) vor nginx.conf (200)? Nein: nach Größe – nginx.conf, html, notes.txt.
        Assert.Equal(["nginx.conf", "html", "notes.txt"], usage.Rows.Select(row => row.Name));
        Assert.Equal([false, true, false], usage.Rows.Select(row => row.IsDirectory));
        Assert.Equal(55.6, usage.Rows[0].Percent);
        Assert.Equal("360 B in total.", usage.Summary);
        Assert.True(usage.CanGoUp);

        // Eine Datei führt nirgendhin, ein Ordner hinein – und von dort wieder hinauf.
        await usage.OpenAsync(usage.Rows[0]);
        Assert.Equal("/var/www", usage.Path);
        await usage.OpenAsync(usage.Rows[1]);
        Assert.Equal("/var/www/html", usage.Path);
        Assert.Equal(["index.html", "css"], usage.Rows.Select(row => row.Name));
        await usage.GoUpAsync();
        Assert.Equal("/var/www", usage.Path);

        // Ein Verzeichnis, das es nicht gibt: Der Grund steht da, die Liste bleibt.
        var missing = new FolderUsageViewModel(files, "/nowhere");
        await missing.LoadAsync();
        Assert.Empty(missing.Rows);
        Assert.Contains("No such file", missing.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_browser_becomes_root_only_when_sudo_agrees_and_says_when_a_password_is_needed()
    {
        var (browser, files) = await BrowserAsync();
        Assert.False(browser.IsRoot);

        // Ohne Passwort geht es, wenn sudo keines verlangt.
        Assert.True(await browser.ElevateAsync());
        Assert.True(browser.IsRoot);
        await browser.LeaveRootAsync();
        Assert.False(browser.IsRoot);

        // Verlangt sudo eines, ist die Antwort „frag nach“ – kein Fehler, und root ist man noch nicht.
        files.SudoPassword = "s3cret";
        Assert.Null(await browser.ElevateAsync());
        Assert.False(browser.IsRoot);
        Assert.False(browser.HasError);

        Assert.False(await browser.ElevateAsync("wrong"));
        Assert.False(browser.IsRoot);
        Assert.StartsWith("sudo refused.", browser.Error);

        Assert.True(await browser.ElevateAsync("s3cret"));
        Assert.True(browser.IsRoot);

        // Ein neu geöffneter Kanal (nach einem Abriss) ist wieder der des Benutzers – das zeigt die Liste danach.
        await files.ReopenAsync();
        await browser.LoadAsync();
        Assert.False(browser.IsRoot);

        files.SudoPassword = string.Empty;
        Assert.False(await browser.ElevateAsync());
        Assert.StartsWith("There is no sudo on the server", browser.Error);
    }

    [Fact]
    public async Task The_core_is_asked_to_become_root_with_the_password_only_in_that_one_request()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        var files = connection.Files!;

        var first = files.ElevateAsync();
        var asked = Assert.Single(transport.Sent("sftp.elevate"));
        Assert.False(asked.TryGetProperty("password", out _));
        transport.Raise($$"""{"event":"sftp.result","request":{{asked.GetProperty("request").GetUInt64()}},"ok":false,"error":"sudo_password","message":"sudo asks for a password"}""");
        Assert.Equal("sudo_password", (await Assert.ThrowsAsync<Services.RemoteFileException>(() => first)).Code);
        Assert.False(files.IsElevated);

        var second = files.ElevateAsync("s3cret");
        var again = transport.Sent("sftp.elevate").Last();
        Assert.Equal("s3cret", again.GetProperty("password").GetString());
        transport.Raise($$"""{"event":"sftp.result","request":{{again.GetProperty("request").GetUInt64()}},"ok":true}""");
        await second;
        Assert.True(files.IsElevated);

        // Zurück: Der neue Kanal ist der des Benutzers, und in keiner anderen Anfrage steht das Passwort.
        var back = files.ReopenAsync();
        transport.Raise($$"""{"event":"sftp.result","request":{{Assert.Single(transport.Sent("sftp.reopen")).GetProperty("request").GetUInt64()}},"ok":true}""");
        await back;
        Assert.False(files.IsElevated);
    }

    [Fact]
    public async Task The_core_is_asked_for_checksums_copies_and_usage()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        var files = connection.Files!;

        var summing = files.ChecksumsAsync(["/a", "/b"]);
        var asked = Assert.Single(transport.Sent("sftp.checksum"));
        Assert.Equal(["/a", "/b"], asked.GetProperty("paths").EnumerateArray().Select(path => path.GetString()));
        transport.Raise($$"""{"event":"sftp.result","request":{{asked.GetProperty("request").GetUInt64()}},"ok":true,"sums":["{{new string('a', 64)}}",null]}""");
        Assert.Equal([new string('a', 64), null], await summing);

        var copying = files.CopyAsync([("/a", "/tmp/a")], move: true);
        var copy = Assert.Single(transport.Sent("sftp.copy"));
        Assert.True(copy.GetProperty("move").GetBoolean());
        var pair = Assert.Single(copy.GetProperty("pairs").EnumerateArray());
        Assert.Equal(("/a", "/tmp/a"), (pair.GetProperty("from").GetString(), pair.GetProperty("to").GetString()));
        transport.Raise($$"""{"event":"sftp.result","request":{{copy.GetProperty("request").GetUInt64()}},"ok":true}""");
        await copying;

        var measuring = files.UsageAsync("/srv");
        var usage = Assert.Single(transport.Sent("sftp.usage"));
        Assert.Equal("/srv", usage.GetProperty("path").GetString());
        transport.Raise($$"""{"event":"sftp.result","request":{{usage.GetProperty("request").GetUInt64()}},"ok":true,"usage":[{"path":"/srv/a","bytes":4096},{"path":"/srv","bytes":8192}]}""");
        Assert.Equal([new Models.FolderUsage("/srv/a", 4096), new Models.FolderUsage("/srv", 8192)], await measuring);
    }
}
