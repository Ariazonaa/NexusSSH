using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusssh-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_yields_the_defaults()
    {
        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(AppLanguage.Auto, settings.Language);
        Assert.Equal(AppSettings.DefaultSftpWidth, settings.SftpWidth);
        Assert.False(settings.ShowBottomPanel);
    }

    [Fact]
    public void Saved_settings_come_back_unchanged()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings
        {
            Language = AppLanguage.German,
            SftpWidth = 412.5,
            InspectorWidth = 200,
            BottomHeight = 333,
            ShowSnippets = false,
            WindowWidth = 1600,
            WindowHeight = 1000,
        });

        var loaded = store.Load();

        Assert.Equal(AppLanguage.German, loaded.Language);
        Assert.Equal(412.5, loaded.SftpWidth);
        Assert.Equal(200, loaded.InspectorWidth);
        Assert.Equal(333, loaded.BottomHeight);
        Assert.False(loaded.ShowSnippets);
        Assert.Null(loaded.ShowInspector);
        Assert.True(loaded.ShowSftp);
        Assert.Equal((1600, 1000), (loaded.WindowWidth, loaded.WindowHeight));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Snippets_sorting_editor_and_folders_survive_a_restart()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings
        {
            ShowHiddenFiles = false,
            LastDownloadFolder = @"D:\downloads",
            ProfileSort = ProfileSort.Recent,
            EditorPath = @"C:\Program Files\Notepad++\notepad++.exe",
            Snippets = [new Snippet("logs", "journalctl -fu nginx\nexit", true), new Snippet("sudo", "sudo ", false)],
        });

        var loaded = store.Load();

        Assert.False(loaded.ShowHiddenFiles);
        Assert.Equal(@"D:\downloads", loaded.LastDownloadFolder);
        Assert.Equal(ProfileSort.Recent, loaded.ProfileSort);
        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", loaded.EditorPath);
        Assert.Equal([new Snippet("logs", "journalctl -fu nginx\nexit", true), new Snippet("sudo", "sudo ", false)], loaded.Snippets);
        Assert.Contains("\"ProfileSort\": \"Recent\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Settings_written_by_an_older_version_get_the_new_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"Language":"German","SftpWidth":300}""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.True(settings.ShowHiddenFiles);
        Assert.Equal(ProfileSort.Saved, settings.ProfileSort);
        Assert.Equal(string.Empty, settings.EditorPath);
        Assert.Equal(string.Empty, settings.LastDownloadFolder);
        Assert.Empty(settings.Snippets);
    }

    [Fact]
    public void Broken_values_for_the_new_settings_are_repaired()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(
            FilePath,
            """{"ProfileSort":9,"EditorPath":null,"LastDownloadFolder":null,"Snippets":[{"Name":"ok","Text":"ls","Run":true},{"Name":"","Text":"x","Run":true},{"Name":"no text","Text":null,"Run":false},null]}""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ProfileSort.Saved, settings.ProfileSort);
        Assert.Equal(string.Empty, settings.EditorPath);
        Assert.Equal(string.Empty, settings.LastDownloadFolder);
        Assert.Equal([new Snippet("ok", "ls", true)], settings.Snippets);

        File.WriteAllText(FilePath, """{"Snippets":null}""");
        Assert.Empty(new SettingsStore(FilePath).Load().Snippets);
    }

    [Fact]
    public void The_language_is_stored_by_name()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { Language = AppLanguage.English });

        Assert.Contains("\"Language\": \"English\"", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("""{"SftpWidth":"wide"}""")]
    [InlineData("""{"Language":"Klingon"}""")]
    public void A_broken_file_yields_the_defaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.DefaultSftpWidth, settings.SftpWidth);
        Assert.Equal(AppLanguage.Auto, settings.Language);
    }

    [Fact]
    public void Out_of_range_values_are_pulled_back()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"SftpWidth":5,"InspectorWidth":99999,"BottomHeight":-1,"WindowWidth":10,"WindowHeight":10,"Language":7}""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.MinPaneSize, settings.SftpWidth);
        Assert.Equal(AppSettings.MaxPaneSize, settings.InspectorWidth);
        Assert.Equal(AppSettings.MinPaneSize, settings.BottomHeight);
        Assert.Equal((900, 560), (settings.WindowWidth, settings.WindowHeight));
        Assert.Equal(AppLanguage.Auto, settings.Language);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Who_saw_the_server_info_sees_the_snippets_where_it_stood(string inspector, bool snippets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $$"""{"ShowInspector":{{inspector}},"InspectorWidth":300}""");

        var store = new SettingsStore(FilePath);
        var settings = store.Load();
        Assert.Equal(snippets, settings.ShowSnippets);
        Assert.Null(settings.ShowInspector);
        Assert.Equal(300, settings.InspectorWidth);

        // Übernommen ist übernommen: Der alte Schalter wird nicht mehr geschrieben.
        store.Save(settings);
        Assert.DoesNotContain("ShowInspector", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_installation_shows_the_snippets_at_the_width_of_the_draft()
    {
        var settings = new AppSettings().Sanitize();
        Assert.True(settings.ShowSnippets);
        Assert.Equal((300, 280), (settings.SftpWidth, settings.InspectorWidth));
    }
}
