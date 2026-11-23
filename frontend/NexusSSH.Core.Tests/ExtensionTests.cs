using System.Text.Json;
using NexusSSH.Core.Extensions;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Erweiterungspakete: lesen und prüfen, ablegen, über die eigenen Inhalte legen, Befehle nur nach Freigabe.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ExtensionTests : IDisposable
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nexus-ext-{Guid.NewGuid():N}");
    private readonly ScriptedBackend _backend = new(Profile);

    public ExtensionTests() => L.Init(AppLanguage.English);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string Colors = """
        "background":"#101010","foreground":"#eeeeee","cursor":"#ffffff","selection":"#333333",
        "ansi":["#000000","#cc0000","#4e9a06","#c4a000","#3465a4","#75507b","#06989a","#d3d7cf","#555753","#ef2929","#8ae234","#fce94f","#729fcf","#ad7fa8","#34e2e2","#eeeeec"]
        """;

    private static string Package(string id = "example.k8s", string version = "1.2.0", string extra = "")
        => $$"""
            {"format":1,"id":"{{id}}","name":"K8s helpers","version":"{{version}}","author":"Someone","description":"Pods\u202e and more",
             "themes":[{"name":"Night <shift>",{{Colors}}},{"name":"Broken","background":"red"}],
             "snippets":[{"name":"Pods","text":"kubectl get pods -A","run":true,"folder":"k8s"},{"name":"Evil","text":"ls\u001b[2J"},{"name":"","text":"x"}],
             "highlights":[{"pattern":"CrashLoopBackOff","color":"red","ignoreCase":true},{"pattern":"x","color":"pink"},{"pattern":"(oops","regex":true,"color":"red"}],
             "completions":[{"command":"kubectl","subs":["get","logs","bad word"],"flags":["-n","-A"]},{"command":"git","subs":["evil"]}]{{extra}}}
            """;

    [Fact]
    public void A_package_is_read_with_everything_checked_like_an_import()
    {
        var package = ExtensionFormat.Parse(Package(), out var problem)!;

        Assert.Null(problem);
        Assert.Equal(("example.k8s", "K8s helpers", "1.2.0", "Someone", "Pods and more"), (package.Id, package.Name, package.Version, package.Author, package.Description));
        var theme = Assert.Single(package.Themes);
        Assert.Equal("pack-example-k8s-night-shift", theme.Id);
        Assert.Equal("Night shift", theme.Name);
        Assert.Equal(new Snippet("Pods", "kubectl get pods -A", true, "k8s"), Assert.Single(package.Snippets));
        Assert.Equal(new HighlightRule("CrashLoopBackOff", false, false, "red"), Assert.Single(package.Highlights));
        Assert.Equal(2, package.Completions.Count);
        Assert.Equal(["get", "logs"], package.Completions[0].Subs);
    }

    [Theory]
    [InlineData("""{"format":2,"id":"a.b","name":"x","version":"1","snippets":[{"name":"a","text":"b"}]}""")]
    [InlineData("""{"format":1,"id":"A..b","name":"x","version":"1","snippets":[{"name":"a","text":"b"}]}""")]
    [InlineData("""{"format":1,"id":"a.b","name":"x","version":"v1","snippets":[{"name":"a","text":"b"}]}""")]
    [InlineData("""{"format":1,"id":"a.b","name":"\u0007","version":"1","snippets":[{"name":"a","text":"b"}]}""")]
    [InlineData("""{"format":1,"id":"a.b","name":"x","version":"1"}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public void What_is_no_package_is_refused_with_a_reason(string text)
    {
        Assert.Null(ExtensionFormat.Parse(text, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void A_package_too_large_is_refused_before_reading()
        => Assert.Null(ExtensionFormat.Parse(new string(' ', ExtensionFormat.MaxLength + 1), out _));

    [Fact]
    public void Writing_and_reading_again_gives_the_same_package()
    {
        var package = ExtensionFormat.Parse(Package(), out _)!;
        var again = ExtensionFormat.Parse(ExtensionFormat.Write(package), out _)!;

        Assert.Equal(package.Themes.Select(theme => (theme.Id, theme.Name, theme.Background, string.Join(",", theme.Ansi))), again.Themes.Select(theme => (theme.Id, theme.Name, theme.Background, string.Join(",", theme.Ansi))));
        Assert.Equal(package.Snippets, again.Snippets);
        Assert.Equal(package.Highlights, again.Highlights);
        Assert.Equal(package.Completions.Select(table => (table.Command, string.Join(' ', table.Subs))), again.Completions.Select(table => (table.Command, string.Join(' ', table.Subs))));
    }

    [Fact]
    public void The_store_keeps_packages_their_switch_and_approvals_and_forgets_them_on_removal()
    {
        var store = new ExtensionStore(_directory);
        var package = ExtensionFormat.Parse(Package(), out _)!;
        store.Install(package);
        store.SetDisabled(package.Id, true);
        store.Approve(package.Id, "kubectl get pods -A");

        // Eine Datei, die jemand daneben legt, unter falschem Namen: bleibt weg.
        File.WriteAllText(Path.Combine(_directory, "other.nxext"), ExtensionFormat.Write(package));

        var fresh = new ExtensionStore(_directory);
        var loaded = Assert.Single(fresh.Load());
        Assert.True(loaded.IsDisabled);
        Assert.True(fresh.IsApproved(package.Id, "kubectl get pods -A"));
        Assert.False(fresh.IsApproved(package.Id, "kubectl get pods"));

        fresh.Remove(package.Id);
        var after = new ExtensionStore(_directory);
        Assert.Empty(after.Load());
        Assert.False(after.IsApproved(package.Id, "kubectl get pods -A"));
    }

    private ShellViewModel Shell(out FakePrompts prompts, AppSettings? settings = null)
    {
        prompts = new FakePrompts();
        L.Init(AppLanguage.English);
        var shell = new ShellViewModel(_backend, new ImmediateDispatcher(), settings ?? new AppSettings(), prompts);
        shell.UseExtensions(new ExtensionStore(_directory));
        return shell;
    }

    [Fact]
    public void An_installed_package_lies_over_the_own_content_and_goes_away_with_it()
    {
        var shell = Shell(out _);
        var package = ExtensionFormat.Parse(Package(), out _)!;

        Assert.True(shell.InstallExtension(package));

        Assert.Contains(shell.Themes, theme => theme.Id == "pack-example-k8s-night-shift");
        Assert.Contains(shell.HighlightRules, rule => rule.Pattern == "CrashLoopBackOff");
        Assert.Equal("kubectl", Assert.Single(shell.PackCompletions).Command);
        var snippet = Assert.Single(shell.AllSnippets);
        Assert.Equal(("K8s helpers / k8s", "example.k8s"), (snippet.Folder, snippet.Pack));
        Assert.Empty(shell.Settings.Snippets);
        Assert.Empty(shell.TerminalSuggestions);

        // Ein Schema aus dem Paket gewählt, dann das Paket aus: Es gilt wieder das Standardschema.
        shell.TerminalTheme = shell.Themes.Single(theme => theme.Id == "pack-example-k8s-night-shift");
        shell.SetExtensionEnabled(shell.Extensions[0], enabled: false);
        Assert.Equal(TerminalThemes.DefaultId, shell.TerminalTheme.Id);
        Assert.Empty(shell.AllSnippets);
        Assert.Empty(shell.PackCompletions);

        shell.SetExtensionEnabled(shell.Extensions[0], enabled: true);
        shell.RemoveExtension(shell.Extensions[0]);
        Assert.Empty(shell.Extensions);
        Assert.DoesNotContain(shell.Themes, theme => TerminalThemes.IsFromPack(theme.Id));
    }

    [Fact]
    public void A_chosen_theme_from_a_package_is_still_chosen_after_a_restart()
    {
        var settings = new AppSettings();
        var first = Shell(out _, settings);
        first.InstallExtension(ExtensionFormat.Parse(Package(), out _)!);
        first.TerminalTheme = first.Themes.Single(theme => TerminalThemes.IsFromPack(theme.Id));

        // Über die Datei, wie beim nächsten Start – samt der Prüfung beim Lesen.
        var file = new SettingsStore(Path.Combine(_directory, "settings.json"));
        file.Save(settings);
        var second = Shell(out _, file.Load());

        Assert.Equal("pack-example-k8s-night-shift", second.TerminalTheme.Id);
    }

    [Fact]
    public void A_command_from_a_package_runs_only_after_it_was_allowed()
    {
        var shell = Shell(out var prompts);
        shell.OpenSession(Profile).Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        shell.InstallExtension(ExtensionFormat.Parse(Package(), out _)!);
        var snippet = Assert.Single(shell.AllSnippets);

        prompts.PackAnswer = CommandAnswer.Decline;
        Assert.True(shell.RunSnippet(snippet));
        Assert.Empty(_backend.Last.Input);
        Assert.Equal(("K8s helpers", "Pods", "kubectl get pods -A"), Assert.Single(prompts.PackCommands));

        prompts.PackAnswer = CommandAnswer.Run;
        shell.RunSnippet(snippet);
        Assert.Equal(["kubectl get pods -A\r"], _backend.Last.Input);

        // „Ausführen“ fragt beim nächsten Mal wieder, „Immer erlauben“ nicht mehr.
        prompts.PackAnswer = CommandAnswer.Always;
        shell.RunSnippet(snippet);
        Assert.Equal(3, prompts.PackCommands.Count);
        shell.RunSnippet(snippet);
        Assert.Equal(3, prompts.PackCommands.Count);
        Assert.Equal(3, _backend.Last.Input.Count);

        // Ein eigener Befehl fragt nie.
        shell.RunSnippet(new Snippet("own", "ls", Run: true));
        Assert.Equal(3, prompts.PackCommands.Count);
    }

    [Fact]
    public void Editing_a_command_from_a_package_edits_an_own_copy()
    {
        var shell = Shell(out _);
        shell.InstallExtension(ExtensionFormat.Parse(Package(), out _)!);

        shell.EditSnippet(shell.AllSnippets.Single(snippet => snippet.IsFromPack));

        var copy = Assert.Single(shell.Settings.Snippets);
        Assert.Equal(new Snippet("Pods", "kubectl get pods -A", true), copy);
        Assert.Equal(0, shell.TakeSnippetToEdit());
        Assert.Equal(2, shell.AllSnippets.Count);
    }

    [Fact]
    public void Own_content_saved_as_a_package_reads_back_as_such()
    {
        var shell = Shell(out _);
        shell.SetSnippets([new Snippet("up", "uptime", Run: true, "tools")]);
        shell.Highlights = "red: ERROR";

        var package = shell.OwnPackage("My tools", "Me", themes: true, snippets: true, highlights: true)!;

        Assert.Equal("own.my-tools", package.Id);
        Assert.Equal(new Snippet("up", "uptime", true, "tools"), Assert.Single(package.Snippets));
        Assert.Equal("ERROR", Assert.Single(package.Highlights).Pattern);
        Assert.Null(shell.OwnPackage("Nothing", string.Empty, themes: true, snippets: false, highlights: false));
    }

    [Fact]
    public void The_page_gets_the_tables_of_the_packages_behind_the_built_in_ones()
    {
        var table = new CommandTable("kubectl", ["get"], ["-n"], new Dictionary<string, string[]>(), []);

        using var message = JsonDocument.Parse(TerminalMessages.Options(TerminalThemes.All[0], 14, [], optionSuggestions: true, packTables: [table]));

        var commands = message.RootElement.GetProperty("completions").GetProperty("tables").EnumerateArray().Select(entry => entry.GetProperty("command").GetString()).ToList();
        Assert.Equal("kubectl", commands[^1]);
        Assert.Contains("git", commands);
    }
}
