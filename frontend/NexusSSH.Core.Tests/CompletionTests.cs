using System.Text.Json;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Ergänzen beim Tippen im Terminal: Die App liefert Listen – Tabellen bekannter Programme und die Namen im Verzeichnis der Shell.</summary>
[Collection(LanguageCollection.Name)]
public sealed class CompletionTests
{
    private static RemoteEntry Entry(string name, bool directory = false)
        => new(name, directory, 0, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Only_names_that_can_be_shown_and_typed_safely_are_offered()
    {
        var names = Completions.Names(
        [
            Entry("zeta.txt"),
            Entry("Alpha", directory: true),
            Entry("mit leerzeichen.txt"),
            Entry("it's"),
            Entry("-rf"),
            Entry(".hidden"),
            Entry("böse‮txt.exe"),
            Entry("zeile\numbruch"),
            Entry("glocke\u0007"),
            Entry("a/b"),
            Entry("."),
            Entry(".."),
            Entry(string.Empty),
            Entry(new string('x', Completions.MaxNameLength + 1)),
        ]);

        // Geordnet, damit die Vorschläge nicht von der Reihenfolge des Servers abhängen.
        Assert.Equal(
            [new PathName("-rf", false), new PathName(".hidden", false), new PathName("Alpha", true), new PathName("it's", false), new PathName("mit leerzeichen.txt", false), new PathName("zeta.txt", false)],
            names);
    }

    [Fact]
    public void A_directory_larger_than_the_limit_offers_nothing_instead_of_a_part()
    {
        var many = Enumerable.Range(0, Completions.MaxNames + 1).Select(index => Entry($"f{index}")).ToList();
        Assert.Empty(Completions.Names(many));
        Assert.Equal(Completions.MaxNames, Completions.Names(many[..Completions.MaxNames]).Count);
    }

    [Fact]
    public void The_tables_hold_only_short_plain_words()
    {
        static bool Plain(string word) => word.Length is > 0 and <= 40 && word.All(c => c is > ' ' and <= '~');

        Assert.Equal(Completions.Tables.Count, Completions.Tables.Select(table => table.Command).Distinct().Count());
        foreach (var table in Completions.Tables)
        {
            Assert.True(Plain(table.Command), table.Command);
            Assert.All(table.Subs, word => Assert.True(Plain(word) && !word.StartsWith('-'), word));
            Assert.All(table.Flags, word => Assert.True(Plain(word) && word.StartsWith('-'), word));
            Assert.All(table.SubFlags, pair =>
            {
                Assert.Contains(pair.Key, table.Subs);
                Assert.All(pair.Value, word => Assert.True(Plain(word), word));
            });
            Assert.All(table.PathSubs, word => Assert.Contains(word, table.Subs));
        }

        Assert.All(Completions.PathCommands, word => Assert.True(Plain(word), word));
        Assert.All(Completions.DirectoryCommands, word => Assert.Contains(word, Completions.PathCommands));
    }

    [Fact]
    public void The_page_gets_tables_and_path_commands_only_when_switched_on()
    {
        var shell = TestShell.Create(out _);
        JsonElement Sent(bool options, bool paths)
            => JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], optionSuggestions: options, pathSuggestions: paths)).RootElement.GetProperty("completions");

        var both = Sent(true, true);
        var git = both.GetProperty("tables").EnumerateArray().Single(table => table.GetProperty("command").GetString() == "git");
        Assert.Contains("commit", git.GetProperty("subs").EnumerateArray().Select(word => word.GetString()));
        Assert.Contains("--amend", git.GetProperty("subFlags").GetProperty("commit").EnumerateArray().Select(word => word.GetString()));
        Assert.Contains("add", git.GetProperty("pathSubs").EnumerateArray().Select(word => word.GetString()));
        Assert.Contains("cd", both.GetProperty("pathCommands").EnumerateArray().Select(word => word.GetString()));
        Assert.Contains("cd", both.GetProperty("dirCommands").EnumerateArray().Select(word => word.GetString()));

        // Abgeschaltet steht die leere Liste da – so nimmt die Seite zurück, was sie hatte.
        var none = Sent(false, false);
        Assert.Equal((0, 0, 0), (none.GetProperty("tables").GetArrayLength(), none.GetProperty("pathCommands").GetArrayLength(), none.GetProperty("dirCommands").GetArrayLength()));
        Assert.Equal(0, Sent(false, true).GetProperty("tables").GetArrayLength());
        Assert.Equal(0, Sent(true, false).GetProperty("pathCommands").GetArrayLength());
    }

    [Fact]
    public void Names_travel_as_a_list_for_one_terminal()
    {
        var message = JsonDocument.Parse(TerminalMessages.Names("s1", [new PathName("src", true), new PathName("it's", false)])).RootElement;

        Assert.Equal(("names", "s1"), (message.GetProperty("type").GetString(), message.GetProperty("id").GetString()));
        var names = message.GetProperty("names").EnumerateArray().Select(name => (name.GetProperty("name").GetString(), name.GetProperty("dir").GetBoolean())).ToList();
        Assert.Equal([("src", true), ("it's", false)], names);

        // Zurückgenommen wird mit einer leeren Liste, die auch wirklich dasteht.
        Assert.Equal(0, JsonDocument.Parse(TerminalMessages.Names("s1", [])).RootElement.GetProperty("names").GetArrayLength());
    }

    [Fact]
    public async Task The_directory_of_the_shell_is_read_on_connect_and_when_the_shell_moves()
    {
        var shell = TestShell.Create(out var backend);
        var now = DateTimeOffset.Parse("2026-10-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        shell.Now = () => now;
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        List<int> changes = [];
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.PathNames))
            {
                changes.Add(session.PathNames.Count);
            }
        };
        session.Start();
        await Task.Yield();

        // Nach dem Verbinden: die Namen dort, wo auch der Dateibrowser beginnt.
        Assert.NotEmpty(session.PathNames);
        var home = session.PathNames;
        var start = session.Files.CurrentPath;

        // Die Shell meldet dasselbe Verzeichnis mit jeder Zeile wieder: gefragt wird nicht jedes Mal.
        shell.OnTerminalDirectory(session, start);
        await Task.Yield();
        Assert.Same(home, session.PathNames);

        // Nach einer Weile schon – neue Dateien sollen auftauchen.
        now += ShellViewModel.PathNamesAge + TimeSpan.FromSeconds(1);
        shell.OnTerminalDirectory(session, start);
        await Task.Yield();
        Assert.NotSame(home, session.PathNames);
        Assert.Equal(home, session.PathNames);

        // Ein anderes Verzeichnis: seine Namen. Eines, das es nicht gibt: keine.
        var directory = session.PathNames.First(name => name.Dir).Name;
        shell.OnTerminalDirectory(session, start.TrimEnd('/') + "/" + directory);
        await Task.Yield();
        Assert.NotEqual(home, session.PathNames);
        shell.OnTerminalDirectory(session, "/gibt/es/nicht");
        await Task.Yield();
        Assert.Empty(session.PathNames);

        // Endet die Verbindung, gibt es keine Namen mehr – und die Seite erfährt es.
        shell.OnTerminalDirectory(session, start);
        await Task.Yield();
        Assert.NotEmpty(session.PathNames);
        changes.Clear();
        session.Disconnect();
        Assert.Empty(session.PathNames);
        Assert.Contains(0, changes);
    }

    [Fact]
    public async Task Switched_off_the_server_is_not_asked_and_the_names_are_taken_back()
    {
        var shell = TestShell.Create(out var backend, new AppSettings { PathSuggestions = false });
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        session.Start();
        await Task.Yield();
        Assert.Empty(session.PathNames);
        shell.OnTerminalDirectory(session, session.Files.CurrentPath);
        await Task.Yield();
        Assert.Empty(session.PathNames);

        shell.PathSuggestions = true;
        await Task.Yield();
        Assert.NotEmpty(session.PathNames);

        shell.PathSuggestions = false;
        Assert.Empty(session.PathNames);
        Assert.False(shell.Settings.PathSuggestions);

        // Beide Schalter stehen von Haus aus auf „an“ und gelten nach einer frischen Installation nicht als geändert.
        var fresh = TestShell.Create(out _);
        Assert.True(fresh.OptionSuggestions && fresh.PathSuggestions);
        Assert.True(fresh.IsDefaultSetting("OptionSuggestions") && fresh.IsDefaultSetting("PathSuggestions"));
        fresh.OptionSuggestions = false;
        Assert.False(fresh.IsDefaultSetting("OptionSuggestions"));
    }

    [Fact]
    public void Words_that_are_typed_without_quotes_are_plain()
    {
        Assert.Equal(
            ["a2enmod", "c++", "git-upload-pack", "python3.12"],
            Completions.CommandNames(["python3.12", "git-upload-pack", "c++", "a2enmod", "a2enmod", "-rf", ".x", "mit leer", "rm;reboot", "$(id)", "böse", null, string.Empty, new string('x', 65)]));

        ServerProfile Profile(string host, string? protocol = null) => new("i" + host, host, host, 22, "u", "", [], Protocol: protocol);
        Assert.Equal(
            ["10.0.0.5", "web01.example.org"],
            Completions.Hosts([Profile("web01.example.org"), Profile("10.0.0.5"), Profile("web01.example.org"), Profile("fe80::1"), Profile("COM3", ServerProfile.Serial), Profile("-oProxyCommand=x")]));
    }

    [Fact]
    public void Commands_hosts_and_the_tab_switch_reach_the_page()
    {
        var shell = TestShell.Create(out _);
        var commands = JsonDocument.Parse(TerminalMessages.Commands("s1", ["git", "ls"])).RootElement;
        Assert.Equal(("commands", "s1", 2), (commands.GetProperty("type").GetString(), commands.GetProperty("id").GetString(), commands.GetProperty("words").GetArrayLength()));

        var options = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], hosts: ["web01"], tabCompletes: true)).RootElement;
        Assert.Equal("web01", options.GetProperty("hosts")[0].GetString());
        Assert.True(options.GetProperty("tabCompletes").GetBoolean());
        var plain = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [])).RootElement;
        Assert.Equal(0, plain.GetProperty("hosts").GetArrayLength());
        Assert.False(plain.GetProperty("tabCompletes").GetBoolean());

        // Ein Ordner kann seinen Inhalt mitbringen – eine Ebene, nicht tiefer.
        var names = JsonDocument.Parse(TerminalMessages.Names("s1", [new PathName("src", true, [new PathName("main.rs", false)]), new PathName("a.txt", false)])).RootElement.GetProperty("names");
        Assert.Equal("main.rs", names[0].GetProperty("children")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_programs_of_the_server_are_asked_for_once_per_connection_and_only_when_switched_on()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        session.Start();
        await Task.Yield();
        Assert.Contains("git", session.CommandNames);

        shell.CommandSuggestions = false;
        Assert.Empty(session.CommandNames);
        shell.CommandSuggestions = true;
        await Task.Yield();
        Assert.Contains("git", session.CommandNames);

        // Mit der Verbindung enden die Listen dieses Servers.
        session.Disconnect();
        Assert.Empty(session.CommandNames);

        var off = TestShell.Create(out var other, new AppSettings { CommandSuggestions = false });
        var quiet = off.OpenSession(other.GetProfiles()[0]);
        quiet.Start();
        await Task.Yield();
        Assert.Empty(quiet.CommandNames);
    }

    [Fact]
    public async Task Folders_bring_their_content_for_one_level_more_and_are_not_read_again_soon()
    {
        var shell = TestShell.Create(out var backend);
        var now = DateTimeOffset.Parse("2026-10-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        shell.Now = () => now;
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        session.Start();
        await Task.Yield();

        var folder = Assert.Single(session.PathNames.Where(name => name is { Dir: true, Children.Count: > 0 }).Take(1));
        Assert.All(folder.Children!, child => Assert.Null(child.Children));
        Assert.All(session.PathNames.Where(name => !name.Dir), name => Assert.Null(name.Children));

        // Das Verzeichnis selbst wird nach einer Weile neu gelesen, der Inhalt der Ordner darin so bald nicht.
        now += ShellViewModel.PathNamesAge + TimeSpan.FromSeconds(1);
        shell.OnTerminalDirectory(session, session.Files.CurrentPath);
        await Task.Yield();
        Assert.Same(folder.Children, session.PathNames.Single(name => name.Name == folder.Name).Children);

        now += ShellViewModel.ChildNamesAge;
        shell.OnTerminalDirectory(session, session.Files.CurrentPath);
        await Task.Yield();
        var again = session.PathNames.Single(name => name.Name == folder.Name).Children;
        Assert.NotSame(folder.Children, again);
        Assert.Equal(folder.Children, again);
    }

    [Fact]
    public void The_addresses_of_the_saved_servers_are_known_to_the_terminal_while_switched_on()
    {
        var shell = TestShell.Create(out _);

        // Die Adressen der gespeicherten Server gibt es nur, solange das eingeschaltet ist.
        Assert.NotEmpty(shell.TerminalHosts);
        List<string> changed = [];
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        shell.KnownNameSuggestions = false;
        Assert.Empty(shell.TerminalHosts);
        Assert.Contains(nameof(ShellViewModel.TerminalHosts), changed);

        // Tab bleibt von Haus aus der Shell.
        Assert.False(shell.TabCompletes);
        Assert.True(shell.IsDefaultSetting("TabCompletes") && shell.IsDefaultSetting("CommandSuggestions"));
        Assert.False(shell.IsDefaultSetting("KnownNameSuggestions"));
    }
}
