using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Anzeigeformate und was daran hängt: Rechte, Durchsatz, Last im Inspektor, Verlauf eines Terminals speichern.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FormatTests
{
    public FormatTests() => L.Init(AppLanguage.English);

    [Theory]
    [InlineData(0b110_100_100, false, false, "-rw-r--r-- (644)")]
    [InlineData(0b111_101_101, true, false, "drwxr-xr-x (755)")]
    [InlineData(0b111_111_111, false, true, "lrwxrwxrwx (777)")]
    [InlineData(0, false, false, "---------- (000)")]
    [InlineData(0b100_111_101_101, false, false, "-rwsr-xr-x (4755)")]
    [InlineData(0b010_111_101_101, true, false, "drwxr-sr-x (2755)")]
    [InlineData(0b001_111_111_111, true, false, "drwxrwxrwt (1777)")]
    [InlineData(0b100_110_100_100, false, false, "-rwSr--r-- (4644)")]
    [InlineData(0b001_111_111_110, true, false, "drwxrwxrwT (1776)")]
    public void Permissions_read_like_ls_with_the_number_for_chmod(int mode, bool isDirectory, bool isLink, string expected)
    {
        Assert.Equal(expected, Format.Permissions(mode, isDirectory, isLink));
    }

    [Fact]
    public void Unknown_permissions_show_nothing_and_file_type_bits_are_ignored()
    {
        Assert.Equal(string.Empty, Format.Permissions(null, isDirectory: false, isLink: false));

        // Manche Server liefern die Art der Datei in den oberen Bits mit (S_IFREG = 0100000).
        Assert.Equal("-rw-r--r-- (644)", Format.Permissions(0b1_000_000_110_100_100, isDirectory: false, isLink: false));
    }

    [Fact]
    public void Names_from_a_server_are_shown_without_control_characters()
    {
        // Zeichen als Zahlen: Im Quelltext wären sie unsichtbar oder brächen die Zeile um.
        static string Text(params int[] codes) => string.Concat(codes.Select(code => (char)code));

        Assert.Equal("eins\nzwei\tdrei", Format.VisibleText("eins\r\n" + Text(0x1B) + "[31mzwei" + Text(0x1B) + "[0m\tdrei"));
        Assert.Equal("titel", Format.VisibleText(Text(0x1B) + "]0;x" + Text(7) + "titel"));
        Assert.Equal("less", Format.VisibleText(Text(0x1B) + "=" + Text(0x1B) + "(Bless" + Text(0x1B) + ">"));
        Assert.Equal(Text(0x241B) + "kurz", Format.VisibleText(Text(0x1B) + "kurz"));
        Assert.Equal("zeilen" + Text(0x240A) + "umbruch", Format.Visible("zeilen\numbruch"));
        Assert.Equal("tab" + Text(0x2409) + "name", Format.Visible("tab\tname"));
        Assert.Equal(Text(0x241B) + "[2J", Format.Visible(Text(0x1B) + "[2J"));
        Assert.Equal("a" + Text(0x2421) + "b", Format.Visible("a" + Text(0x7F) + "b"));

        // Mit umgekehrter Schreibrichtung (U+202E) sähe der Name aus, als endete er auf „exe.txt“.
        Assert.Equal("rechnung" + Text(0xFFFD) + "txt.exe", Format.Visible("rechnung" + Text(0x202E) + "txt.exe"));
        Assert.Equal("a" + Text(0xFFFD) + "b", Format.Visible("a" + Text(0x2028) + "b"));

        // Alles andere bleibt, wie es ist – Umlaute, andere Schriften, Emojis.
        const string Harmless = "Ümläute ßtraße – 日本語 🚀.txt";
        Assert.Same(Harmless, Format.Visible(Harmless));

        // Die Liste zeigt und liest den sichtbaren Namen vor; für Aktionen bleibt der echte.
        var entry = new RemoteEntry("zeilen\numbruch", IsDirectory: false, Size: 1, DateTimeOffset.MinValue);
        Assert.Equal("zeilen" + Text(0x240A) + "umbruch", entry.ToString());
        Assert.Equal("zeilen\numbruch", entry.Name);
    }

    [Fact]
    public void A_target_is_shown_without_control_characters_and_cut_short()
    {
        var link = new RemoteEntry("l", IsDirectory: false, Size: 0, DateTimeOffset.MinValue, IsLink: true);

        Assert.Equal("→ ziel", Format.LinkText(link with { LinkTarget = "ziel" }));

        // Das Ziel steht im Link und kommt vom Server: wie ein Name, ohne Steuerzeichen und ohne Umkehr der Schreibrichtung.
        // Die Zeichen stehen als Zahlen da: Im Quelltext wären sie unsichtbar.
        static string Text(params int[] codes) => string.Concat(codes.Select(code => (char)code));
        Assert.Equal("→ a" + Text(0x2400) + "b", Format.LinkText(link with { LinkTarget = "a" + Text(0) + "b" }));
        Assert.Equal("→ a" + Text(0x240A) + "b", Format.LinkText(link with { LinkTarget = "a\nb" }));
        Assert.Equal("→ rechnung" + Text(0xFFFD) + "txt.exe", Format.LinkText(link with { LinkTarget = "rechnung" + Text(0x202E) + "txt.exe" }));

        // Ohne Ziel steht nichts da.
        Assert.Equal(string.Empty, Format.LinkText(link));
        Assert.Equal(string.Empty, Format.LinkText(link with { LinkTarget = string.Empty }));

        // Was auf 120 Zeichen passt, bleibt; mehr wird mit … gekürzt (samt „→ “ höchstens 120).
        var fits = "→ " + new string('y', 118);
        Assert.Equal(fits, Format.LinkText(link with { LinkTarget = new string('y', 118) }));
        var cut = Format.LinkText(link with { LinkTarget = new string('x', 500) });
        Assert.Equal(120, cut.Length);
        Assert.StartsWith("→ xxx", cut);
        Assert.EndsWith("x…", cut);

        // Mitten in einem Emoji (zwei UTF-16-Einheiten) wird nicht geschnitten.
        var emoji = Format.LinkText(link with { LinkTarget = string.Concat(Enumerable.Repeat("🚀", 200)) });
        Assert.True(emoji.Length <= 120);
        Assert.EndsWith("🚀…", emoji);
    }

    [Fact]
    public void A_link_is_read_out_with_its_target_and_one_into_nothing_says_so()
    {
        // Die Liste liest ToString() vor: Was nur das Symbol zeigt, hört ein Screenreader sonst nie.
        var file = new RemoteEntry("datei", IsDirectory: false, Size: 1, DateTimeOffset.MinValue);
        var link = new RemoteEntry("aktuell", IsDirectory: true, Size: 0, DateTimeOffset.MinValue, IsLink: true, LinkTarget: "../releases/42");

        Assert.Equal("datei", file.ToString());
        Assert.Equal("aktuell, → ../releases/42", link.ToString());

        // Ohne bekanntes Ziel sagt die Zeile wenigstens, dass es ein Link ist – sonst klänge sie wie eine Datei.
        Assert.Equal("aktuell, " + L.Get("TypeLink"), (link with { LinkTarget = null }).ToString());

        var dead = link with { LinkTarget = "fehlt", IsDangling = true, IsSpecial = true };
        Assert.Equal("aktuell, → fehlt, The link points to nothing – its target does not exist.", dead.ToString());

        // Der Name für Aktionen und die Anzeige bleibt, wie er war.
        Assert.Equal("aktuell", dead.DisplayName);
        Assert.Equal("aktuell", dead.Name);

        // Name und Ziel kommen vom Server: ohne Steuerzeichen, das Ziel auf die Länge der Zeile gekürzt.
        var odd = new RemoteEntry("a\nb", IsDirectory: false, Size: 0, DateTimeOffset.MinValue, IsLink: true, LinkTarget: new string('x', 500));
        Assert.DoesNotContain('\n', odd.ToString());
        Assert.True(odd.ToString().Length <= 3 + 2 + Format.LinkTextLength);
    }

    [Theory]
    [InlineData("644", 0b110_100_100)]
    [InlineData("0755", 0b111_101_101)]
    [InlineData(" 600 ", 0b110_000_000)]
    [InlineData("2775", 0b010_111_111_101)]
    [InlineData("000", 0)]
    public void A_mode_is_typed_as_three_or_four_octal_digits(string text, int expected)
    {
        Assert.True(Format.TryParseMode(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("64")]
    [InlineData("64448")]
    [InlineData("789")]
    [InlineData("rwx")]
    [InlineData("-644")]
    [InlineData("6 44")]
    public void Anything_else_is_no_mode(string? text)
    {
        Assert.False(Format.TryParseMode(text, out _));
    }

    [Fact]
    public void A_mode_reads_back_as_the_number_for_chmod()
    {
        Assert.Equal("644", Format.Octal(0b110_100_100));
        Assert.Equal("000", Format.Octal(0));
        Assert.Equal("2775", Format.Octal(0b010_111_111_101));

        // Die Art der Datei in den oberen Bits gehört nicht dazu.
        Assert.Equal("644", Format.Octal(0b1_000_000_110_100_100));
    }

    [Fact]
    public void Rates_use_the_units_of_file_sizes()
    {
        Assert.Equal("0 B/s", Format.Rate(0));
        Assert.Equal("1.5 KB/s", Format.Rate(1536));
        Assert.Equal("↓ 1.0 MB/s  ↑ 2.0 KB/s", Format.Network(1024 * 1024, 2048));

        L.Init(AppLanguage.German);
        Assert.Equal("1,5 KB/s", Format.Rate(1536));
    }

    [Fact]
    public void The_inspector_shows_the_throughput_of_the_disks_only_when_the_server_names_it()
    {
        var inspector = new ServerInspectorViewModel(new ServerProfile("p", "web01", "h", 22, "u", string.Empty, []), new ImmediateDispatcher());
        var stats = new ServerStats(true, 10, 20, 30, "Debian", "6.1", TimeSpan.FromHours(5), Io: new DiskIo(2 * 1024 * 1024, 512 * 1024, 25));
        var monitor = new FixedMonitor(stats);
        inspector.Attach(monitor);
        inspector.SetWatched(true);

        Assert.True(inspector.HasDiskIo);
        Assert.Equal($"reading {Format.Rate(2 * 1024 * 1024)} · writing {Format.Rate(512 * 1024)} · {Format.Percent(25)} busy", inspector.DiskIoText);

        // Eine einzelne Platte steht schon in der Summe; erst mehrere bekommen je eine Zeile.
        var sda = new DiskIoDevice("sda", new DiskIo(1024 * 1024, 0, 25));
        monitor.Set(stats with { IoDevices = [sda] });
        Assert.False(inspector.HasDiskIoDevices);

        monitor.Set(stats with { Io = new DiskIo(512 * 1024, 0, 5), IoDevices = [sda, new DiskIoDevice("nvme0n1", new DiskIo(0, 0, 0))] });
        Assert.Equal(["sda", "nvme0n1"], inspector.DiskIoDevices.Select(device => device.Name));
        Assert.Equal($"sda, reading {Format.Rate(1024 * 1024)} · writing {Format.Rate(0)} · {Format.Percent(25)} busy", inspector.DiskIoDevices[0].ToString());

        // Die Kurve misst am höchsten Wert im Zeitraum, wie beim Netz – und endet, wenn der Server nichts mehr nennt.
        Assert.Equal(1.0, inspector.DiskIoHistory.Max());
        Assert.Equal(0.2, inspector.DiskIoHistory[^1], precision: 6);

        monitor.Set(stats with { Io = null });

        Assert.False(inspector.HasDiskIo);
        Assert.False(inspector.HasDiskIoDevices);
        Assert.Empty(inspector.DiskIoHistory);
    }

    [Fact]
    public void The_inspector_shows_load_and_network_when_the_server_reports_them()
    {
        var inspector = new ServerInspectorViewModel(new ServerProfile("p", "web01", "h", 22, "u", string.Empty, []), new ImmediateDispatcher());
        var monitor = new FixedMonitor(new ServerStats(true, 10, 20, 30, "Debian", "6.1", TimeSpan.FromHours(5), Load: "0.42 0.31 0.25", NetReceived: 1024 * 1024, NetSent: 2048));
        inspector.Attach(monitor);
        inspector.SetWatched(true);

        Assert.True(inspector.HasLoad);
        Assert.Equal("0.42 0.31 0.25", inspector.LoadText);
        Assert.Equal("↓ 1.0 MB/s  ↑ 2.0 KB/s", inspector.NetworkText);

        var changed = new List<string?>();
        inspector.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        monitor.Set(new ServerStats(true, 10, 20, 30, "FreeBSD", "14", TimeSpan.FromHours(5)));

        Assert.False(inspector.HasLoad);
        Assert.Contains(nameof(ServerInspectorViewModel.HasLoad), changed);
        Assert.Equal("↓ 0 B/s  ↑ 0 B/s", inspector.NetworkText);
    }

    [Fact]
    public void The_mock_reports_load_with_a_dot_whatever_the_system_language_is()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var backend = new MockBackend();
            var connection = backend.Connect(backend.GetProfiles().Single(profile => profile.Id == "web01"));
            connection.Terminal.Start();

            Assert.Matches(@"^\d+\.\d\d \d+\.\d\d \d+\.\d\d$", connection.Monitor!.Current!.Load);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_file_name_for_a_saved_terminal_carries_server_and_time_and_no_forbidden_characters()
    {
        var shell = TestShell.Create(out var backend);
        var time = new DateTimeOffset(2026, 10, 2, 14, 5, 9, TimeSpan.Zero).ToLocalTime();
        var session = shell.OpenSession(backend.GetProfiles()[0] with { Name = "web/01: prod" });

        var name = ShellViewModel.TerminalFileName(session, time);

        Assert.DoesNotContain('/', name);
        Assert.EndsWith($"-{time:yyyyMMdd-HHmmss}.txt", name);
        Assert.StartsWith("web_01", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));

        var unnamed = shell.OpenSession(backend.GetProfiles()[0] with { Name = " " });
        Assert.StartsWith("terminal-", ShellViewModel.TerminalFileName(unnamed, time));
    }

    [Fact]
    public void A_terminal_is_saved_with_the_line_endings_of_this_computer()
    {
        using var folder = new TempFolder();
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        var path = Path.Combine(folder.Path, "out.txt");

        Assert.True(shell.SaveTerminalText(session, path, "ubuntu@web01:~$ ls\nübung.txt  日本"));

        var expected = $"ubuntu@web01:~$ ls{Environment.NewLine}übung.txt  日本{Environment.NewLine}";
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Contains(shell.Output, entry => entry.Text == $"Scrollback of {session.Title} saved: {path}");
        Assert.Empty(shell.Problems);
    }

    [Fact]
    public void A_terminal_that_cannot_be_saved_is_a_problem_not_a_crash()
    {
        using var folder = new TempFolder();
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles()[0]);

        Assert.False(shell.SaveTerminalText(session, Path.Combine(folder.Path, "missing", "out.txt"), "text"));

        Assert.StartsWith($"The scrollback of {session.Title} could not be saved: ", Assert.Single(shell.Problems).Text);
    }

    [Fact]
    public void Saving_the_terminal_asks_the_window_and_needs_a_session()
    {
        var shell = TestShell.Create(out var backend);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        Assert.False(shell.RunCommand("terminal.save"));
        shell.OpenSession(backend.GetProfiles()[0]);
        Assert.True(shell.RunCommand("terminal.save"));

        Assert.Equal([UiRequest.SaveTerminal], requests);
    }

    private sealed class FixedMonitor(ServerStats stats) : Services.IServerMonitor
    {
        public ServerStats? Current { get; private set; } = stats;

        public bool IsUnavailable => false;

        public event Action? Changed;

        public void SetActive(bool active)
        {
        }

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public void Set(ServerStats next)
        {
            Current = next;
            Changed?.Invoke();
        }
    }
}
