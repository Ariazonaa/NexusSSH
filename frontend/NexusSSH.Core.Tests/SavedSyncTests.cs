using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Gemerkte Abgleiche: Ordnerpaar samt Richtung unter einem Namen, mit einem Klick erneut abgeglichen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SavedSyncTests
{
    public SavedSyncTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_sync_is_saved_listed_renamed_and_deleted_and_bad_entries_never_load()
    {
        using var folder = new TempFolder();
        var settings = new AppSettings();
        var shell = TestShell.Create(out var backend, settings);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");

        var saved = shell.SaveSync("  Site ", web, folder.Path, "/var/www/html", SyncDirection.Upload, byContent: true, ["node_modules", "bad/pattern/../x", ""]);

        Assert.NotNull(saved);
        Assert.Equal("Site", saved.Name);
        Assert.Equal(["node_modules"], saved.Exclude.Where(pattern => pattern == "node_modules"));
        var listed = Assert.Single(shell.SavedSyncs);
        Assert.Equal($"{folder.Path} → web01:/var/www/html", listed.Summary);
        Assert.Contains(shell.SearchPalette(string.Empty), item => item.Title == "Synchronize: Site");

        // Derselbe Name ersetzt den gemerkten und behält seine Kennung.
        var again = shell.SaveSync("site", web, folder.Path, "/var/www", SyncDirection.Download, byContent: false, []);
        Assert.Equal(saved.Id, again!.Id);
        Assert.Equal("/var/www", Assert.Single(shell.SavedSyncs).RemoteFolder);

        Assert.True(shell.RenameSync(again, "Website"));
        Assert.Equal("Website", Assert.Single(settings.SavedSyncs).Name);
        shell.DeleteSync(again);
        Assert.Empty(shell.SavedSyncs);

        // Was sich nicht taugt, wird nicht gemerkt: kein Name, ein relativer Pfad, ein Pfad am Server ohne `/`.
        Assert.Null(shell.SaveSync(" ", web, folder.Path, "/var/www", SyncDirection.Upload, false, []));
        Assert.Null(shell.SaveSync("x", web, "relative\\path", "/var/www", SyncDirection.Upload, false, []));
        Assert.Null(shell.SaveSync("x", web, folder.Path, "var/www", SyncDirection.Upload, false, []));
        Assert.Null(shell.SaveSync("x", web with { IsTransient = true }, folder.Path, "/var/www", SyncDirection.Upload, false, []));

        // Von Hand verdorbene Einträge in den Einstellungen fallen beim Laden weg.
        var loaded = new AppSettings
        {
            SavedSyncs =
            [
                new("a", "fine", "web01", folder.Path, "/var/www", SyncDirection.Download, false, [".git", "../x"]),
                new("b", "relative", "web01", "here", "/var/www", SyncDirection.Download, false, []),
                new("c", "no slash", "web01", folder.Path, "srv", SyncDirection.Download, false, []),
                new("d", "odd direction", "web01", folder.Path, "/srv", (SyncDirection)7, false, []),
                new("e", "bad\u0007name", "web01", folder.Path, "/srv", SyncDirection.Upload, false, []),
            ],
        }.Sanitize();
        var kept = Assert.Single(loaded.SavedSyncs);
        Assert.Equal("fine", kept.Name);
        Assert.Equal(TransferExcludes.Clean([".git", "../x"]), kept.Exclude);
    }

    [Fact]
    public async Task Running_a_saved_sync_over_a_standing_connection_shows_the_preview_and_starts_only_after_it()
    {
        using var folder = new TempFolder();
        var shell = TestShell.Create(out var backend);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");
        var session = shell.OpenSession(web);
        session.Start();
        var sync = shell.SaveSync("site", web, folder.Path, "/var/www/html", SyncDirection.Download, false, [])!;

        // Abgebrochen in der Vorschau: nichts beginnt.
        SyncPlan? shown = null;
        shell.ConfirmSync = plan =>
        {
            shown = plan;
            return Task.FromResult<bool?>(null);
        };
        Assert.False(await shell.RunSavedSyncAsync(sync));
        Assert.NotNull(shown);
        Assert.Equal("/var/www/html", shown.RemoteFolder);
        Assert.Equal(folder.Path, shown.LocalFolder);
        Assert.Equal(["css/site.css", "favicon.ico", "index.html"], shown.Items.Select(item => item.RelativePath));
        Assert.Single(shell.Sessions);

        // Bestätigt: Der Abgleich beginnt über die stehende Verbindung, ohne neuen Tab.
        shell.ConfirmSync = _ => Task.FromResult<bool?>(false);
        Assert.True(await shell.RunSavedSyncAsync(sync));
        Assert.Same(session, Assert.Single(shell.Sessions));
    }

    [Fact]
    public async Task Without_a_connection_a_saved_sync_opens_a_tab_and_waits_until_it_stands()
    {
        using var folder = new TempFolder();
        var shell = TestShell.Create(out var backend);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");
        var sync = shell.SaveSync("site", web, folder.Path, "/var/www/html", SyncDirection.Download, false, [])!;
        var asked = 0;
        shell.ConfirmSync = _ =>
        {
            asked++;
            return Task.FromResult<bool?>(false);
        };

        var running = shell.RunSavedSyncAsync(sync);

        // Der neue Tab steht vorn und wartet: Verbunden wird erst, wenn sein Terminal ihn startet.
        var tab = Assert.Single(shell.Sessions);
        Assert.Same(tab, shell.ActiveSession);
        Assert.False(running.IsCompleted);
        Assert.Equal(0, asked);

        tab.Start();
        Assert.True(await running);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_sync_whose_server_is_gone_or_whose_tab_fails_starts_nothing_and_says_why()
    {
        using var folder = new TempFolder();
        var settings = new AppSettings();
        var shell = TestShell.Create(out var backend, settings);
        var web = backend.GetProfiles().Single(profile => profile.Id == "web01");
        shell.ConfirmSync = _ => throw new InvalidOperationException("no preview without a plan");
        var sync = shell.SaveSync("site", web, folder.Path, "/var/www/html", SyncDirection.Download, false, [])!;

        var gone = sync with { ProfileId = "deleted" };
        Assert.False(await shell.RunSavedSyncAsync(gone));
        Assert.Empty(shell.Sessions);
        Assert.Contains(shell.Output, entry => entry.Text == "Synchronization site: the server no longer exists.");

        // In der Liste steht er weiter, mit einem Hinweis statt des Servers.
        settings.SavedSyncs = [gone];
        Assert.Equal($"(deleted server):/var/www/html → {folder.Path}", Assert.Single(shell.SavedSyncs).Summary);

        // Ein lokaler Ordner, den es nicht mehr gibt: keine Vorschau, der Grund im Protokoll.
        var session = shell.OpenSession(web);
        session.Start();
        var missing = sync with { LocalFolder = Path.Combine(folder.Path, "missing") };
        Assert.False(await shell.RunSavedSyncAsync(missing));
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith("Synchronization site: The local folder could not be read", StringComparison.Ordinal));
    }
}
