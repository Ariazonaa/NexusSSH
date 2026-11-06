using System.Text;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.Terminals.Tmux;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Der Steuermodus von tmux. Die Zeilen in diesen Tests sind die, die ein echtes tmux 3.7 geschickt hat
/// (aufgezeichnet am 2026-10-10), gekürzt.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TmuxControlTests
{
    private const string Esc = "\u001b";

    private static readonly ServerProfile Web = new("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // --- Der Strom: Terminal oder Steuerprotokoll ---

    [Fact]
    public void Before_and_after_control_mode_the_bytes_belong_to_the_terminal_however_they_are_cut()
    {
        var all = Bytes($"prompt$ tmux -CC attach\r\n{Esc}P1000p%begin 1 2 0\r\n%end 1 2 0\r\n%output %0 hi\r\n%exit\r\n{Esc}\\prompt$ ");
        foreach (var size in new[] { 1, 2, 3, 5, 64, all.Length })
        {
            var stream = new TmuxStream();
            var (plain, lines, ended) = (new List<byte>(), new List<string>(), 0);
            for (var at = 0; at < all.Length; at += size)
            {
                stream.Feed(all.AsSpan(at, Math.Min(size, all.Length - at)), plain.AddRange, line => lines.Add(Encoding.UTF8.GetString(line)), () => ended++);
            }

            Assert.Equal("prompt$ tmux -CC attach\r\nprompt$ ", Encoding.UTF8.GetString([.. plain]));
            Assert.Equal(["%begin 1 2 0", "%end 1 2 0", "%output %0 hi", "%exit"], lines);
            Assert.Equal(1, ended);
            Assert.False(stream.InControl);
        }
    }

    [Fact]
    public void Something_that_only_starts_like_the_introduction_still_reaches_the_terminal()
    {
        var stream = new TmuxStream();
        var plain = new List<byte>();

        stream.Feed(Bytes($"a{Esc}P10x{Esc}{Esc}P1b"), plain.AddRange, _ => Assert.Fail("keine Zeile erwartet"), () => { });

        Assert.Equal($"a{Esc}P10x{Esc}{Esc}P1b", Encoding.UTF8.GetString([.. plain]));
    }

    [Fact]
    public void Output_is_unescaped_from_octal_and_everything_else_stays_as_it_is()
    {
        // „'hä\'“ und Wagenrücklauf, Zeilenvorschub – so schickt es tmux.
        Assert.Equal(Bytes("'hä\\'\r\n"), TmuxStream.Unescape(Bytes("'hä\\134'\\015\\012")));
        Assert.Equal(Bytes($"{Esc}[6n"), TmuxStream.Unescape(Bytes("\\033[6n")));

        // Kein vollständiges Oktal: bleibt, wie es dasteht.
        Assert.Equal(Bytes("\\9 \\12 \\"), TmuxStream.Unescape(Bytes("\\9 \\12 \\")));
        Assert.Equal(Bytes("\\777"), TmuxStream.Unescape(Bytes("\\777")));
    }

    // --- Der Client: Blöcke, Meldungen, Befehle ---

    private static (TmuxClient Client, List<string> Sent) Client()
    {
        var sent = new List<string>();
        return (new TmuxClient(bytes => sent.Add(Encoding.UTF8.GetString(bytes.Span))), sent);
    }

    private static void Feed(TmuxClient client, params string[] lines)
    {
        foreach (var line in lines)
        {
            client.OnLine(Bytes(line));
        }
    }

    [Fact]
    public async Task The_first_block_is_the_handshake_and_later_blocks_answer_the_commands_in_order()
    {
        var (client, sent) = Client();
        var started = 0;
        client.Started += () => started++;

        Feed(client, "%begin 1791646569 279 0", "%end 1791646569 279 0", "%window-add @0", "%sessions-changed", "%session-changed $0 nxprobe");
        Assert.Equal(1, started);

        var windows = client.WindowsAsync();
        var broken = client.CommandAsync("nonsense-command");
        Assert.Equal(["list-panes -s -F \"#{window_id} #{pane_id} #{pane_active} #{pane_left} #{pane_top} #{pane_width} #{pane_height} #{window_name}\"\n", "nonsense-command\n"], sent);

        // So nennt tmux 3.7 ein Fenster mit einem Bereich und eines mit dreien (links einer, rechts zwei übereinander).
        Feed(
            client,
            "%begin 1791646569 285 1",
            "@0 %0 1 0 0 80 24 sh",
            "@1 %3 0 0 0 40 24 second window",
            "@1 %4 0 41 0 39 12 second window",
            "@1 %5 1 41 13 39 11 second window",
            "garbage",
            "@1 %5 1 0 0 1 1 a pane named twice",
            "%end 1791646569 285 1");
        var listed = await windows;
        Assert.Equal([("@0", "sh", 1), ("@1", "second window", 3)], listed.Select(window => (window.Id, window.Name, window.Panes.Count)));
        Assert.Equal(new TmuxPaneInfo("%5", true, 41, 13, 39, 11), listed[1].ActivePane);

        Feed(client, "%begin 1791646575 296 1", "parse error: unknown command: nonsense-command", "%error 1791646575 296 1");
        var error = await Assert.ThrowsAsync<TmuxException>(() => broken);
        Assert.Contains("unknown command", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_inside_a_block_that_looks_like_an_end_does_not_end_it()
    {
        var (client, _) = Client();
        Feed(client, "%begin 1 1 0", "%end 1 1 0");
        var answer = client.CommandAsync("capture-pane -p -t %0");

        Feed(client, "%begin 5 7 1", "%end 5 99 1", "%output %0 not a message here", "%end 5 7 1");

        Assert.Equal(["%end 5 99 1", "%output %0 not a message here"], (await answer).Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public void Output_of_a_pane_is_handed_on_unescaped_and_changes_of_windows_are_announced()
    {
        var (client, _) = Client();
        var (output, changes) = (new List<(string, string)>(), 0);
        client.Output += (pane, data) => output.Add((pane, Encoding.UTF8.GetString(data)));
        client.WindowsChanged += () => changes++;
        Feed(client, "%begin 1 1 0", "%end 1 1 0");

        Feed(client, "%output %0 hä\\134\\015\\012", "%output %12  two  spaces", "%output bad data", "%window-add @1", "%window-renamed @1 logs", "%window-close @1", "%layout-change @0 a87d,100x30,0,0,0");

        Assert.Equal([("%0", "hä\\\r\n"), ("%12", " two  spaces")], output);
        // Auch eine geänderte Anordnung zählt: Mit ihr kommen und gehen die Bereiche eines Fensters.
        Assert.Equal(4, changes);

        // So meldet tmux 3.7 ein Fenster, das mit seiner Shell endet: Es ist beim Melden schon aus der Sitzung.
        Feed(client, "%unlinked-window-close @1");

        Assert.Equal(5, changes);
    }

    [Fact]
    public async Task Leaving_control_mode_fails_what_still_waits_and_refuses_what_comes_after()
    {
        var (client, _) = Client();
        string? reason = "unset";
        client.Exited += why => reason = why;
        Feed(client, "%begin 1 1 0", "%end 1 1 0");
        var waiting = client.CommandAsync("list-windows");

        Feed(client, "%exit detached");

        Assert.Equal("detached", reason);
        await Assert.ThrowsAsync<TmuxException>(() => waiting);
        await Assert.ThrowsAsync<TmuxException>(() => client.CommandAsync("list-windows"));
        Assert.True(client.HasExited);
    }

    [Fact]
    public async Task Input_goes_as_hex_so_nothing_typed_can_become_a_command()
    {
        var (client, sent) = Client();
        Feed(client, "%begin 1 1 0", "%end 1 1 0");

        client.SendKeys("%0", Bytes("echo 'hä';\r"));
        client.SendKeys("%0; kill-server", Bytes("x"));
        client.SendKeys("%1", new byte[300]);
        client.Resize(100, 30);
        client.Resize(100, 30);
        client.Resize(100_000, 0);

        Assert.Equal("send-keys -t %0 -H 65 63 68 6f 20 27 68 c3 a4 27 3b 0d\n", sent[0]);
        Assert.Equal(3, sent.Count(command => command.StartsWith("send-keys -t %1 -H", StringComparison.Ordinal)));
        Assert.DoesNotContain(sent, command => command.Contains("kill-server", StringComparison.Ordinal));
        Assert.Equal(["refresh-client -C 100,30\n", "refresh-client -C 1000,2\n"], sent.Where(command => command.StartsWith("refresh", StringComparison.Ordinal)));

        // Ein Zeilenumbruch in einem Befehl wäre ein zweiter Befehl.
        await Assert.ThrowsAsync<TmuxException>(() => client.CommandAsync("list-windows\nkill-server"));
        Assert.True(TmuxClient.IsId("%12", '%'));
        Assert.False(TmuxClient.IsId("%", '%'));
        Assert.False(TmuxClient.IsId("@1", '%'));
        Assert.False(TmuxClient.IsId("%1 ", '%'));
    }

    [Fact]
    public async Task A_snapshot_clears_the_screen_writes_the_lines_and_puts_the_cursor_where_it_was()
    {
        var (client, sent) = Client();
        Feed(client, "%begin 1 1 0", "%end 1 1 0");

        // Ohne Verlauf: der Bildschirm, wie er ist.
        var snapshot = client.SnapshotAsync("%0");
        Feed(client, "%begin 2 2 1", "7 2 5 0 0", "%end 2 2 1");
        await Settle();
        Feed(client, "%begin 2 3 1", "b9b:/# echo hi", "hi", "b9b:/# ", string.Empty, string.Empty, "%end 2 3 1");

        Assert.Equal($"{Esc}[0m{Esc}[H{Esc}[2J{Esc}[3Jb9b:/# echo hi\r\nhi\r\nb9b:/# {Esc}[0m{Esc}[3;8H", Encoding.UTF8.GetString(await snapshot));
        Assert.Equal("capture-pane -p -e -t %0\n", sent[^1]);
        Assert.Empty(await client.SnapshotAsync("%0; kill-server"));

        // Mit Verlauf: erst dessen Zeilen, dann so viele Leerzeilen, wie der Bereich hoch ist – so rutscht der
        // Verlauf nach oben aus dem Bild, und der Bildschirm beginnt wieder in der ersten Zeile.
        var deep = client.SnapshotAsync("%0");
        Feed(client, "%begin 2 4 1", "2 1 3 0 5000", "%end 2 4 1");
        await Settle();
        Assert.Equal($"capture-pane -p -e -S -{TmuxClient.HistoryLines} -t %0\n", sent[^1]);
        Feed(client, "%begin 2 5 1", "old 1", "old 2", "$ ls", "$ ", string.Empty, "%end 2 5 1");
        Assert.Equal(
            $"{Esc}[0m{Esc}[H{Esc}[2J{Esc}[3Jold 1{Esc}[0m\r\nold 2{Esc}[0m\r\n\r\n\r\n\r\n{Esc}[H$ ls\r\n$ {Esc}[0m{Esc}[2;3H",
            Encoding.UTF8.GetString(await deep));

        // Ein Programm im zweiten Bildschirm (vim) hat keinen Verlauf, der dazugehört.
        var full = client.SnapshotAsync("%0");
        Feed(client, "%begin 2 6 1", "0 0 3 1 5000", "%end 2 6 1");
        await Settle();
        Assert.Equal("capture-pane -p -e -t %0\n", sent[^1]);
        Feed(client, "%begin 2 7 1", "~", "%end 2 7 1");
        await full;
    }

    [Fact]
    public async Task An_old_tmux_gets_keys_without_the_hex_switch_and_still_nothing_typed_becomes_a_command()
    {
        var (client, sent) = Client();
        Feed(client, "%begin 1 1 0", "%end 1 1 0");
        var probe = client.ProbeAsync();
        Feed(client, "%begin 1 2 1", "v=2.6", "%end 1 2 1");
        await probe;
        Assert.Equal((2, 6), client.Version);
        sent.Clear();

        // ASCII als Tastennamen in Hex, alles andere als Text in einfachen Anführungszeichen – das Anführungszeichen
        // selbst ist ASCII und kommt nie hinein.
        client.SendKeys("%0", Bytes("echo 'hä€';\r"));
        Assert.Equal(
            ["send-keys -t %0 0x65 0x63 0x68 0x6f 0x20 0x27 0x68\n", "send-keys -t %0 -l 'ä€'\n", "send-keys -t %0 0x27 0x3b 0x0d\n"],
            sent);

        // Ein Zeichen, das über zwei Aufrufe verteilt ankommt, wird eines.
        sent.Clear();
        client.SendKeys("%0", new byte[] { 0xc3 });
        Assert.Empty(sent);
        client.SendKeys("%0", new byte[] { 0xa4 });
        Assert.Equal(["send-keys -t %0 -l 'ä'\n"], sent);

        // Anhalten kann dieses tmux nicht.
        Assert.False(client.SetPaused("%0", true));
    }

    [Theory]
    [InlineData("v=3.7c", 3, 7)]
    [InlineData("v=next-3.5", 3, 5)]
    [InlineData("v=2.6", 2, 6)]
    [InlineData("v=3", 3, 0)]
    [InlineData("v=", 2, 0)]
    [InlineData("", 2, 0)]
    [InlineData("v=99999.1", 2, 0)]
    public void The_version_is_read_from_what_tmux_calls_itself(string answer, int major, int minor)
        => Assert.Equal((major, minor), TmuxClient.ParseVersion(answer));

    [Fact]
    public async Task Pausing_new_windows_and_names_are_commands_made_of_fixed_words_and_checked_parts()
    {
        var (client, sent) = Client();
        Feed(client, "%begin 1 1 0", "%end 1 1 0");

        // Solange die Version nicht bekannt ist, gilt die älteste, die der Steuermodus hier voraussetzt – ohne Anhalten.
        Assert.False(client.SetPaused("%3", true));
        var probe = client.ProbeAsync();
        Feed(client, "%begin 1 2 1", "v=3.7c", "%end 1 2 1");
        await probe;
        sent.Clear();

        Assert.True(client.SetPaused("%3", true));
        Assert.True(client.SetPaused("%3", false));
        Assert.False(client.SetPaused("%3\"; kill-server", true));
        client.NewWindow();
        Assert.True(client.RenameWindow("@2", "Protokolle 2.1_a-b"));
        client.ResizePane("%3", 40, null);
        client.ResizePane("%3", null, 100_000);
        client.ResizePane("%3; kill-server", 1, 1);
        Assert.Equal(
            [
                "refresh-client -A \"%3:pause\"\n", "refresh-client -A \"%3:continue\"\n", "new-window\n", "rename-window -t @2 'Protokolle 2.1_a-b'\n",
                "resize-pane -t %3 -x 40\n", "resize-pane -t %3 -y 1000\n",
            ],
            sent);

        // Was tmux oder eine Shell anders lesen könnte als gemeint, ist kein Name.
        foreach (var name in (string[])["", " x", "x ", "-x", "a'b", "a\"b", "a;b", "a#b", "a$b", "a\nb", "a\\b", "a`b", new string('x', 41)])
        {
            Assert.False(client.RenameWindow("@2", name), name);
        }

        Assert.False(client.RenameWindow("@2; kill-server", "ok"));
        Assert.Equal(6, sent.Count);
    }

    [Fact]
    public async Task Tmux_brakes_on_its_own_and_its_late_output_and_its_pause_are_understood()
    {
        var (client, sent) = Client();
        var output = new List<(string, string)>();
        var paused = new List<string>();
        client.Output += (pane, data) => output.Add((pane, Encoding.UTF8.GetString(data)));
        client.Paused += paused.Add;
        Feed(client, "%begin 1 1 0", "%end 1 1 0");

        // Ab 3.2 bittet die App tmux, selbst zu bremsen; ein älteres bekommt den Befehl nicht.
        var probe = client.ProbeAsync();
        Feed(client, "%begin 1 2 1", "v=3.7c", "%end 1 2 1");
        await probe;
        Assert.Equal("refresh-client -f pause-after=5\n", sent[^1]);

        // So nennt tmux 3.7 die Ausgabe danach – die Daten beginnen hinter „ : “, auch wenn darin eines steht.
        Feed(client, "%extended-output %0 0 : seq 1 3\\015\\012", "%extended-output %0 1234 : a : b", "%extended-output bad 0 : x", "%pause %0", "%pause nonsense", "%continue %0");
        Assert.Equal([("%0", "seq 1 3\r\n"), ("%0", "a : b")], output);
        Assert.Equal(["%0"], paused);

        var (old, sentOld) = Client();
        Feed(old, "%begin 1 1 0", "%end 1 1 0");
        var probeOld = old.ProbeAsync();
        Feed(old, "%begin 1 2 1", "v=3.1", "%end 1 2 1");
        await probeOld;
        Assert.DoesNotContain(sentOld, command => command.Contains("pause-after", StringComparison.Ordinal));
    }

    private static TmuxPaneInfo Pane(int id, int left, int top, int width, int height) => new($"%{id}", id == 0, left, top, width, height);

    // „H“ = nebeneinander, „V“ = untereinander.
    private static string Show(SplitNode<TmuxPaneInfo>? node) => node switch
    {
        SplitLeaf<TmuxPaneInfo> leaf => leaf.Item.Id,
        SplitBranch<TmuxPaneInfo> branch => $"{(branch.SideBySide ? 'H' : 'V')}({string.Join(',', branch.Children.Select(Show))})",
        _ => "null",
    };

    [Fact]
    public void Any_layout_tmux_can_make_becomes_a_tree()
    {
        // Zwei nebeneinander, zwei untereinander – gleich, wie tmux die Bereiche nennt.
        Assert.Equal("H(%0,%1)", Show(TmuxTree.Of([Pane(1, 41, 0, 39, 24), Pane(0, 0, 0, 40, 24)])));
        Assert.Equal("V(%0,%1)", Show(TmuxTree.Of([Pane(0, 0, 0, 80, 12), Pane(1, 0, 13, 80, 11)])));
        Assert.Equal("%0", Show(TmuxTree.Of([Pane(0, 0, 0, 80, 24)])));

        // Fünf in L-Form: einer links, rechts oben ein breiter, darunter drei in einer Reihe.
        Assert.Equal("H(%0,V(%1,H(%2,%3,%4)))", Show(TmuxTree.Of([Pane(0, 0, 0, 40, 24), Pane(1, 41, 0, 39, 12), Pane(2, 41, 13, 12, 11), Pane(3, 54, 13, 12, 11), Pane(4, 67, 13, 13, 11)])));

        // Drei in einer Reihe; breiter Bereich oben und zwei darunter; einer links neben zweien.
        Assert.Equal("H(%0,%1,%2)", Show(TmuxTree.Of([Pane(0, 0, 0, 26, 24), Pane(1, 27, 0, 26, 24), Pane(2, 54, 0, 26, 24)])));
        Assert.Equal("V(%0,H(%1,%2))", Show(TmuxTree.Of([Pane(0, 0, 0, 80, 12), Pane(1, 0, 13, 40, 11), Pane(2, 41, 13, 39, 11)])));
        Assert.Equal("H(%0,V(%1,%2))", Show(TmuxTree.Of([Pane(0, 0, 0, 40, 24), Pane(1, 41, 0, 39, 12), Pane(2, 41, 13, 39, 11)])));

        // Ein Raster, dessen Linien nicht durchgehen: erst waagerecht, dann je Zeile senkrecht. Sind sie durchgehend, schneidet das
        // Senkrechte zuerst.
        var staggered = TmuxTree.Of([Pane(0, 0, 0, 30, 12), Pane(1, 31, 0, 49, 12), Pane(2, 0, 13, 50, 11), Pane(3, 51, 13, 29, 11)]);
        Assert.Equal("V(H(%0,%1),H(%2,%3))", Show(staggered));
        Assert.Equal("H(V(%0,%2),V(%1,%3))", Show(TmuxTree.Of([Pane(0, 0, 0, 40, 12), Pane(1, 41, 0, 39, 12), Pane(2, 0, 13, 40, 11), Pane(3, 41, 13, 39, 11)])));

        // Die Anteile sind die Breiten (ohne Trennlinie).
        var row = (SplitBranch<TmuxPaneInfo>)((SplitBranch<TmuxPaneInfo>)staggered!).Children[0];
        Assert.Equal(30.0 / 79, row.Shares[0], 6);
        Assert.Equal(49.0 / 79, row.Shares[1], 6);

        // Sechzehn gehen noch.
        Assert.Equal(16, SplitTree.Leaves(TmuxTree.Of([.. Enumerable.Range(0, 16).Select(index => Pane(index, 0, index * 5, 80, 4))])!).Count);
    }

    [Fact]
    public void Layouts_beyond_sixteen_panes_fall_back_to_the_active_pane()
    {
        Assert.Null(TmuxTree.Of([.. Enumerable.Range(0, 17).Select(index => Pane(index, 0, index * 5, 80, 4))]));
        Assert.Null(TmuxTree.Of([]));

        // Rechtecke, die sich überlappen oder Lücken lassen, lassen sich nicht zerlegen.
        Assert.Null(TmuxTree.Of([Pane(0, 0, 0, 80, 24), Pane(1, 10, 5, 20, 10)]));
        Assert.Null(TmuxTree.Of([Pane(0, 0, 0, 30, 24), Pane(1, 50, 0, 30, 24)]));

        // Ein Bereich, der nur ein Zeichen breit ist, fiele unter den kleinsten Anteil.
        Assert.Null(TmuxTree.Of([Pane(0, 0, 0, 1, 24), Pane(1, 2, 0, 78, 24)]));
    }

    // --- Die Tabs ---

    private static (ShellViewModel Shell, ScriptedBackend Backend, SessionViewModel Carrier, ScriptedConnection Connection) Connected()
    {
        var backend = new ScriptedBackend(Web);
        var shell = TestShell.Create(backend);
        var carrier = shell.OpenSession(Web);
        carrier.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        return (shell, backend, carrier, backend.Last);
    }

    private static readonly MuxSession Work = new("tmux", "work", 2, false, "tmux attach-session -t =work");

    // Handschlag, Version, dann die Bereiche der Sitzung – die Zeilen in der Form von `list-panes -s`.
    private static async Task Attach(ShellViewModel shell, SessionViewModel carrier, ScriptedConnection connection, params string[] panes)
    {
        shell.AttachTmuxControl(carrier, Work);
        connection.Emit($"{Esc}P1000p%begin 1 1 0\r\n%end 1 1 0\r\n");
        await Settle();
        connection.Emit("%begin 1 2 1\r\nv=3.7c\r\n%end 1 2 1\r\n");
        await Settle();

        // Die Antwort auf die Bitte, selbst zu bremsen (pause-after), dann die Bereiche.
        await Panes(connection, 3, string.Empty);
        await Panes(connection, 4, panes);
    }

    private static async Task Panes(ScriptedConnection connection, int block, params string[] panes)
    {
        connection.Emit($"%begin 1 {block} 1\r\n{string.Join("\r\n", panes)}\r\n%end 1 {block} 1\r\n");
        await Settle();
    }

    private static async Task Settle()
    {
        // Die Antworten laufen über Aufgaben weiter – kurz abwarten, bis sie durch sind.
        for (var turn = 0; turn < 20; turn++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_windows_of_tmux_become_tabs_and_what_is_typed_there_goes_to_their_pane()
    {
        var (shell, _, carrier, connection) = Connected();
        var shown = new List<string>();
        carrier.Output += data => shown.Add(Encoding.UTF8.GetString(data.Span));

        Assert.True(shell.AttachTmuxControl(carrier, Work));
        Assert.Equal("tmux -CC attach-session -t =work\r", connection.Input[^1]);

        connection.Emit($"tmux -CC attach-session -t =work\r\n{Esc}P1000p%begin 1 1 0\r\n%end 1 1 0\r\n");
        await Settle();
        Assert.Equal("display-message -p \"v=#{version}\"\n", connection.Input[^1]);
        connection.Emit("%begin 1 2 1\r\nv=3.7c\r\n%end 1 2 1\r\n");
        await Settle();
        Assert.StartsWith("list-panes -s", connection.Input[^1], StringComparison.Ordinal);

        // Zwei Antworten: auf die Bitte, selbst zu bremsen, und auf die Frage nach den Bereichen.
        connection.Emit("%begin 1 3 1\r\n%end 1 3 1\r\n%begin 1 4 1\r\n@0 %0 1 0 0 80 24 sh\r\n@1 %1 1 0 0 80 24 logs\r\n%end 1 4 1\r\n");
        await Settle();

        // Der Träger zeigt nur, was vor dem Steuermodus kam; die Fenster stehen als Tabs dahinter.
        Assert.Equal(["tmux -CC attach-session -t =work\r\n"], shown);
        Assert.Equal(["web01", "web01 · sh", "web01 · logs"], shell.Sessions.Select(session => session.Title));
        Assert.True(shell.Sessions[1].IsVirtual);
        Assert.Same(shell.Sessions[1], shell.ActiveSession);

        // Ein Fenster-Tab startet, zeichnet seinen Stand und nimmt Eingaben an.
        var tab = shell.Sessions[1];
        var screen = new List<string>();
        tab.Output += data => screen.Add(Encoding.UTF8.GetString(data.Span));
        tab.Start();
        await Settle();
        Assert.Equal(ConnectionState.Connected, tab.State);
        // Drei Antworten: auf die Frage nach Cursor und Verlauf, auf die Größe des Terminals (sie geht erst hinaus,
        // wenn das Terminal eine Weile still war), dann der Bildschirm.
        Assert.Equal(
            ["display-message -p -t %0 ", "refresh-client -C 80,24\n"],
            connection.Input.TakeLast(2).Select(line => line.StartsWith("display-message -p -t %0 ", StringComparison.Ordinal) ? "display-message -p -t %0 " : line));
        connection.Emit("%output %0 early\r\n%begin 1 4 1\r\n2 0 24 0 0\r\n%end 1 4 1\r\n%begin 1 5 1\r\n%end 1 5 1\r\n");
        await Settle();
        Assert.Equal("capture-pane -p -e -t %0\n", connection.Input[^1]);
        connection.Emit("%begin 1 6 1\r\n$ \r\n%end 1 6 1\r\n%output %0 later\r\n%output %7 elsewhere\r\n");
        await Settle();
        Assert.Equal([$"{Esc}[0m{Esc}[H{Esc}[2J{Esc}[3J$ {Esc}[0m{Esc}[1;3H", "early", "later"], screen);

        shell.Input(tab, Bytes("ls\r"));
        Assert.Equal("send-keys -t %0 -H 6c 73 0d\n", connection.Input[^1]);

        // Im Träger getippt wäre es ein Befehl an tmux – es geht nicht hinaus.
        var before = connection.Input.Count;
        shell.Input(carrier, Bytes("kill-server\r"));
        Assert.Equal(before, connection.Input.Count);
    }

    [Fact]
    public async Task Closing_a_window_tab_detaches_and_the_end_of_control_mode_closes_the_tabs_and_frees_the_carrier()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 80 24 sh", "@1 %1 1 0 0 80 24 logs");
        Assert.Equal(3, shell.Sessions.Count);

        // Ein Fenster verschwindet auf dem Server: Sein Tab geht.
        connection.Emit("%window-close @1\r\n");
        await Settle();
        await Panes(connection, 4, "@0 %0 1 0 0 80 24 sh");
        Assert.Equal(["web01", "web01 · sh"], shell.Sessions.Select(session => session.Title));

        // Der Benutzer schließt den Tab des Fensters: abhängen, nicht beenden – der Tab bleibt, bis tmux es meldet.
        shell.CloseSession(shell.Sessions[1]);
        Assert.Equal("detach-client\n", connection.Input[^1]);
        Assert.Equal(2, shell.Sessions.Count);

        connection.Emit($"%exit\r\n{Esc}\\$ ");
        await Settle();
        Assert.Equal([carrier], shell.Sessions);
        Assert.Same(carrier, shell.ActiveSession);
        Assert.False(shell.CarriesTmux(carrier));

        // Der Träger ist wieder ein gewöhnliches Terminal.
        shell.Input(carrier, Bytes("ls\r"));
        Assert.Equal("ls\r", connection.Input[^1]);
    }

    [Fact]
    public async Task When_the_connection_of_the_carrier_ends_the_window_tabs_go_with_it()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 80 24 sh");
        Assert.Equal(2, shell.Sessions.Count);

        connection.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost, "gone"));
        await Settle();

        Assert.Equal([carrier], shell.Sessions);
        Assert.False(shell.CarriesTmux(carrier));
    }

    [Fact]
    public async Task The_panes_of_a_window_stand_as_a_split_and_tmux_is_told_their_sizes()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 0 0 0 40 24 sh", "@0 %1 1 41 0 39 24 sh");

        // Ein Fenster, zwei Bereiche nebeneinander: zwei Tabs, eine Teilung, links vor rechts.
        Assert.Equal(["web01", "web01 · sh", "web01 · sh"], shell.Sessions.Select(session => session.Title));
        Assert.Equal((2, true), (shell.Layout.Panes.Count, shell.Layout.RootSideBySide()));
        Assert.Equal([shell.Sessions[1], shell.Sessions[2]], shell.Layout.Panes);
        Assert.True(shell.Layout.Fixed);

        // Die Anordnung gehört tmux: nicht weiter teilen, nicht umsetzen, nicht vergrößern. Nur die Anteile am Platz
        // lassen sich ziehen – die Größen gehen danach an tmux.
        Assert.False(shell.CanSplit);
        Assert.False(shell.CanZoom);
        Assert.False(shell.MovePane(shell.Sessions[1], shell.Sessions[2], PaneSide.Bottom));
        Assert.Equal([shell.Sessions[1], shell.Sessions[2]], shell.Layout.Panes);

        // Was in einem getippt wird, geht an seinen Bereich.
        shell.Sessions[1].Start();
        shell.Sessions[2].Start();
        shell.Input(shell.Sessions[2], Bytes("x"));
        Assert.Equal("send-keys -t %1 -H 78\n", connection.Input[^1]);

        // Die Terminals melden ihre Größe: tmux bekommt die Summe samt Trennlinie, und der linke Bereich sein Maß –
        // einmal, wenn alle gemeldet haben, nicht nach jedem.
        var before = connection.Input.Count;
        shell.Sessions[1].Resize(50, 30);
        shell.Sessions[2].Resize(60, 30);
        await Settle();
        Assert.Equal(["refresh-client -C 111,30\n", "resize-pane -t %0 -x 50\n"], Sized(connection, before));

        // Dieselbe Größe noch einmal ist kein neuer Befehl.
        before = connection.Input.Count;
        shell.Sessions[2].Resize(60, 30);
        await Settle();
        Assert.Empty(Sized(connection, before));

        // Zieht der Benutzer die Trennlinie, ändern sich die Anteile; die neuen Größen der Terminals gehen an tmux.
        // Der Client bleibt so groß – nur die Grenze wandert. Dass das linke Terminal zuerst meldet, ergibt keine
        // Zwischengröße.
        Assert.True(shell.SetShares(shell.Sessions[1], [], [0.3, 0.7]));
        Assert.Equal([0.3, 0.7], ((SplitBranch<SessionViewModel>)shell.Layout.Tree!).Shares);
        before = connection.Input.Count;
        shell.Sessions[1].Resize(30, 30);
        shell.Sessions[2].Resize(80, 30);
        await Settle();
        Assert.Equal(["resize-pane -t %0 -x 30\n"], Sized(connection, before));
    }

    // Was die App tmux seit <paramref name="before"/> über Größen gesagt hat.
    private static List<string> Sized(ScriptedConnection connection, int before)
        => [.. connection.Input.Skip(before).Where(line => line.StartsWith("refresh-client -C", StringComparison.Ordinal) || line.StartsWith("resize-pane", StringComparison.Ordinal))];

    [Fact]
    public async Task A_dragged_divider_tells_tmux_only_what_changed_and_each_size_once()
    {
        var (shell, _, carrier, connection) = Connected();

        // So stand es im Gast (tmux 3.5a): links zwei untereinander, rechts oben zwei nebeneinander, darunter einer –
        // [[%0/%3]|[[%1|%4]/%2]], 97 × 27.
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 48 13 sh", "@0 %3 0 0 14 48 13 sh", "@0 %1 0 49 0 21 13 sh", "@0 %4 0 71 0 26 13 sh", "@0 %2 0 49 14 48 13 sh");
        Assert.Equal(5, shell.Layout.Panes.Count);
        // Die Tabs stehen in der Reihenfolge der Fläche: oben vor unten, links vor rechts. Gestartet wird keiner – ihre
        // Bilder wären Befehle, auf deren Antwort die Liste unten warten müsste.
        var tabs = new[] { "%0", "%1", "%4", "%3", "%2" }.Zip(shell.Sessions.Skip(1)).ToDictionary(pair => pair.First, pair => pair.Second);

        void Report(params (string Pane, int Columns, int Rows)[] sizes)
        {
            foreach (var (pane, columns, rows) in sizes)
            {
                tabs[pane].Resize(columns, rows);
            }
        }

        // Beim Anhängen haben die Terminals schon die Größen, die tmux nennt: Nur der Client wird genannt, kein
        // Bereich – und das einmal.
        var before = connection.Input.Count;
        Report(("%0", 48, 13), ("%1", 21, 13), ("%4", 26, 13), ("%3", 48, 13), ("%2", 48, 13));
        await Settle();
        Assert.Equal(["refresh-client -C 97,27\n"], Sized(connection, before));

        // Die senkrechte Linie in der Mitte wandert nach rechts. Unterwegs melden die Terminals jede Zwischengröße,
        // jedes für sich; tmux bekommt danach einmal die linke Spalte und die Breite oben rechts – die Höhen und der
        // Client sind dieselben geblieben, und was tmux selbst richtet (der Rest der rechten Spalte), kommt nicht.
        before = connection.Input.Count;
        Report(("%0", 55, 13), ("%3", 55, 13), ("%1", 19, 13), ("%4", 21, 13), ("%2", 41, 13));
        Report(("%0", 60, 13), ("%3", 60, 13), ("%1", 17, 13));
        Report(("%0", 63, 13), ("%3", 63, 13), ("%1", 16, 13), ("%4", 16, 13), ("%2", 33, 13));
        await Settle();
        Assert.Equal(["resize-pane -t %0 -x 63\n", "resize-pane -t %1 -x 16\n"], Sized(connection, before));

        // tmux meldet die neue Anordnung (dieselbe Form): Daraus wird keine neue Runde.
        before = connection.Input.Count;
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        await Panes(connection, 5, "@0 %0 1 0 0 63 13 sh", "@0 %3 0 0 14 63 13 sh", "@0 %1 0 64 0 16 13 sh", "@0 %4 0 81 0 16 13 sh", "@0 %2 0 64 14 33 13 sh");
        Report(("%0", 63, 13), ("%3", 63, 13), ("%1", 16, 13), ("%4", 16, 13), ("%2", 33, 13));
        await Settle();
        Assert.Empty(Sized(connection, before));

        // Eine Zeile weniger für alle (das Fenster der App wurde flacher): der Client, dann jede Höhe, die tmux beim
        // Verkleinern selbst verteilt hat – einmal.
        before = connection.Input.Count;
        Report(("%0", 63, 12), ("%3", 63, 13), ("%1", 16, 12), ("%4", 16, 12), ("%2", 33, 13));
        await Settle();
        Assert.Equal(["refresh-client -C 97,26\n", "resize-pane -t %0 -y 12\n", "resize-pane -t %1 -y 12\n"], Sized(connection, before));
    }

    [Fact]
    public async Task A_divider_dragged_in_the_app_stays_when_tmux_reports_the_same_arrangement_again()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 0 0 0 40 24 sh", "@0 %1 1 41 0 39 24 sh");
        Assert.True(shell.SetShares(shell.Sessions[1], [], [0.3, 0.7]));

        // Dieselbe Anordnung mit anderen Breiten: die Teilung bleibt, wie die App sie hat.
        var group = shell.Layout.Tree;
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        await Panes(connection, 5, "@0 %0 0 0 0 20 24 sh", "@0 %1 1 21 0 59 24 sh");
        Assert.Same(group, shell.Layout.Tree);
        Assert.Equal([0.3, 0.7], ((SplitBranch<SessionViewModel>)shell.Layout.Tree!).Shares);

        // Eine andere Anordnung (jetzt untereinander) ersetzt sie durch die von tmux.
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        await Panes(connection, 6, "@0 %0 0 0 0 80 12 sh", "@0 %1 1 0 13 80 11 sh");
        Assert.False(((SplitBranch<SessionViewModel>)shell.Layout.Tree!).SideBySide);
        Assert.Equal(12.0 / 23, ((SplitBranch<SessionViewModel>)shell.Layout.Tree!).Shares[0], 6);
    }

    [Fact]
    public async Task Four_panes_in_a_grid_keep_their_places_and_get_the_sizes_of_the_app()
    {
        var (shell, _, carrier, connection) = Connected();

        // So nennt tmux ein Fenster, das erst in zwei Spalten und dann jede Spalte in zwei Zeilen geteilt wurde:
        // Die Reihenfolge von tmux (%4, %6, %5, %7) ist nicht die der Fläche.
        await Attach(shell, carrier, connection, "@0 %4 0 0 0 60 20 vier", "@0 %6 0 0 21 60 19 vier", "@0 %5 0 61 0 59 20 vier", "@0 %7 1 61 21 59 19 vier");
        Assert.Equal(5, shell.Sessions.Count);
        Assert.Equal(4, shell.Layout.Panes.Count);

        // Oben links, oben rechts, unten links, unten rechts.
        var tabs = shell.Sessions.Skip(1).ToList();
        foreach (var tab in tabs)
        {
            tab.Start();
        }

        var targets = new List<string>();
        foreach (var tab in tabs)
        {
            shell.Input(tab, Bytes("x"));
            targets.Add(connection.Input[^1]);
        }

        Assert.Equal(["%4", "%5", "%6", "%7"], targets.Select(line => line.Split(' ')[2]));

        // Die Größen der App: Summe samt Trennlinie für den Client (der Baum ist [[%4/%6]|[%5/%7]]); die linke Spalte
        // bekommt ihre Breite, %4 und %5 ihre Höhe. Kein Befehl ordnet die Bereiche neu an.
        var before = connection.Input.Count;
        tabs[0].Resize(45, 15);
        tabs[1].Resize(44, 15);
        tabs[2].Resize(45, 14);
        tabs[3].Resize(44, 14);
        await Settle();
        Assert.Equal(
            ["refresh-client -C 90,30\n", "resize-pane -t %4 -x 45\n", "resize-pane -t %4 -y 15\n", "resize-pane -t %5 -y 15\n"],
            Sized(connection, before));
        Assert.DoesNotContain(connection.Input, line => line.Contains("select-layout", StringComparison.Ordinal));
    }

    // Siebzehn Bereiche untereinander, der mit der Nummer <paramref name="active"/> ist der aktive.
    private static string[] Crowd(int active) => [.. Enumerable.Range(0, 17).Select(index => $"@0 %{index} {(index == active ? 1 : 0)} 0 {index * 5} 80 4 sh")];

    [Fact]
    public async Task A_tree_of_panes_tells_tmux_the_client_size_and_each_pane_once()
    {
        var (shell, _, carrier, connection) = Connected();

        // Links einer, rechts zwei übereinander: [%0|[%1/%2]].
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 40 24 sh", "@0 %1 0 41 0 39 12 sh", "@0 %2 0 41 13 39 11 sh");
        Assert.Equal(4, shell.Sessions.Count);
        Assert.Equal(3, shell.Layout.Panes.Count);
        var tabs = shell.Sessions.Skip(1).ToList();
        foreach (var tab in tabs)
        {
            tab.Start();
        }

        var before = connection.Input.Count;
        tabs[0].Resize(50, 30);
        tabs[1].Resize(60, 14);
        tabs[2].Resize(58, 15);
        await Settle();

        // Der Client ist so groß wie der Baum (Breite: 50 + 1 + 60, Höhe: größte); die Bereiche von oben nach unten.
        Assert.Equal(["refresh-client -C 111,30\n", "resize-pane -t %0 -x 50\n", "resize-pane -t %1 -y 14\n"], Sized(connection, before));

        // Dieselben Größen noch einmal: nichts. Eine andere Breite: der Client und die Breite, die tmux dabei selbst
        // verteilt hat – die Höhen rechts verschiebt eine Breite nicht.
        before = connection.Input.Count;
        tabs[2].Resize(58, 15);
        await Settle();
        Assert.Empty(Sized(connection, before));
        tabs[1].Resize(55, 14);
        await Settle();
        Assert.Equal(["refresh-client -C 109,30\n", "resize-pane -t %0 -x 50\n"], Sized(connection, before));
        Assert.DoesNotContain(connection.Input, line => line.Contains("select-layout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Five_panes_in_an_l_shape_stand_as_a_split_and_a_divider_deeper_in_the_tree_is_sized_through_its_leaf()
    {
        var (shell, _, carrier, connection) = Connected();

        // [%0|[%1/[%2|%3|%4]]]: Die Reihe unten rechts ist ein Zweig im Zweig.
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 40 24 sh", "@0 %1 0 41 0 39 12 sh", "@0 %2 0 41 13 12 11 sh", "@0 %3 0 54 13 12 11 sh", "@0 %4 0 67 13 13 11 sh");
        Assert.Equal(6, shell.Sessions.Count);
        var tabs = shell.Sessions.Skip(1).ToList();
        foreach (var tab in tabs)
        {
            tab.Start();
        }

        var before = connection.Input.Count;
        tabs[0].Resize(30, 24);
        tabs[1].Resize(49, 10);
        tabs[2].Resize(15, 13);
        tabs[3].Resize(15, 13);
        tabs[4].Resize(17, 13);
        await Settle();

        // Rechts: die Reihe ist 15 + 1 + 15 + 1 + 17 = 49 breit, die Spalte 10 + 1 + 13 = 24 hoch; der Client bleibt
        // 30 + 1 + 49 = 80 breit.
        Assert.Equal(
            ["refresh-client -C 80,24\n", "resize-pane -t %0 -x 30\n", "resize-pane -t %1 -y 10\n", "resize-pane -t %2 -x 15\n", "resize-pane -t %3 -x 15\n"],
            Sized(connection, before));
    }

    [Fact]
    public async Task Panes_come_and_go_with_tmux_and_more_than_sixteen_fall_back_to_the_active_pane()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 80 24 sh");
        var only = shell.Sessions[1];
        Assert.False(only.IsSplit);

        // Der Benutzer teilt in tmux: ein zweiter Tab daneben, der erste bleibt, was er war.
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        var block = 4;
        await Panes(connection, block++, "@0 %0 0 0 0 80 12 sh", "@0 %1 1 0 13 80 11 sh");
        Assert.Equal(3, shell.Sessions.Count);
        Assert.Same(only, shell.Sessions[1]);
        Assert.Equal((2, false), (shell.Layout.Panes.Count, shell.Layout.RootSideBySide()));

        // Siebzehn Bereiche: mehr als ein Fenster zeigt – es bleibt der aktive Bereich allein.
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        await Panes(connection, block++, Crowd(1));
        Assert.Equal(2, shell.Sessions.Count);
        Assert.False(shell.Sessions[1].IsSplit);

        // Der aktive Bereich wechselt: Derselbe Tab zeigt jetzt ihn – beim Start zeichnet er dessen Stand.
        var tab = shell.Sessions[1];
        connection.Emit("%window-pane-changed @0 %2\r\n");
        await Settle();
        await Panes(connection, block++, Crowd(2));
        Assert.Same(tab, shell.Sessions[1]);
        tab.Start();
        await Settle();
        Assert.StartsWith("display-message -p -t %2 ", connection.Input[^2], StringComparison.Ordinal);
        Assert.Equal("refresh-client -C 80,24\n", connection.Input[^1]);

        // Zwei Antworten stehen aus: auf die Frage nach Cursor und Verlauf, dann auf die Größe des Terminals (sie geht
        // erst hinaus, wenn das Terminal eine Weile still war).
        await Panes(connection, block++, "0 0 24 0 0");
        await Panes(connection, block++, string.Empty);
        Assert.Equal("capture-pane -p -e -t %2\n", connection.Input[^1]);
        await Panes(connection, block++, "$ ");

        // Zurück auf einen Bereich: ein Tab, keine Teilung.
        connection.Emit("%layout-change @0 x\r\n");
        await Settle();
        await Panes(connection, block, "@0 %2 1 0 0 80 24 sh");
        Assert.Equal(2, shell.Sessions.Count);
        Assert.Single(shell.Layout.Panes);
    }

    [Fact]
    public async Task A_new_window_and_a_new_name_are_asked_of_tmux_and_the_tabs_follow_what_it_reports()
    {
        var (shell, _, carrier, connection) = Connected();
        await Attach(shell, carrier, connection, "@0 %0 1 0 0 80 24 sh");
        Assert.True(shell.IsInTmux);

        shell.NewTmuxWindow();
        Assert.Equal("new-window\n", connection.Input[^1]);
        connection.Emit("%begin 1 4 1\r\n%end 1 4 1\r\n%window-add @1\r\n");
        await Settle();
        await Panes(connection, 5, "@0 %0 1 0 0 80 24 sh", "@1 %1 1 0 0 80 24 ash");
        Assert.Equal("web01 · ash", shell.ActiveSession!.Title);

        // Umbenennen geht an tmux; der Reiter ändert sich erst, wenn tmux es meldet.
        var tab = shell.ActiveSession;
        shell.RenameTab(tab, "Protokolle");
        Assert.Equal("rename-window -t @1 'Protokolle'\n", connection.Input[^1]);
        Assert.Equal("web01 · ash", tab.Title);

        // Ein Name, der mehr wäre als ein Name, geht nicht hinaus.
        var before = connection.Input.Count;
        shell.RenameTab(tab, "x'; kill-server; '");
        Assert.Equal(before, connection.Input.Count);

        // Ein gewöhnlicher Tab heißt, wie der Benutzer will.
        shell.RenameTab(carrier, "mein Server");
        Assert.Equal("mein Server", carrier.Title);
    }

    [Fact]
    public void Control_mode_is_only_for_tmux_sessions_with_a_plain_name_on_a_real_connected_tab()
    {
        var (shell, _, carrier, connection) = Connected();
        var before = connection.Input.Count;

        Assert.False(shell.AttachTmuxControl(carrier, new MuxSession("screen", "12.x", null, false, "screen -x 12.x")));
        Assert.False(shell.AttachTmuxControl(carrier, new MuxSession("tmux", "a; rm -rf ~", 1, false, "tmux attach-session -t =a; rm -rf ~")));
        Assert.Equal(before, connection.Input.Count);

        Assert.True(shell.AttachTmuxControl(carrier, Work));
        Assert.False(shell.AttachTmuxControl(carrier, Work));
    }

    [Fact]
    public void Window_tabs_are_not_remembered_for_the_next_start()
    {
        Assert.Equal("control:tmux:work", MuxSteps.For(Work, control: true));
        Assert.Equal(("tmux", "work"), MuxSteps.Target("control:tmux:work"));
        Assert.True(MuxSteps.IsControl("control:tmux:work"));
        Assert.False(MuxSteps.IsControl("attach:tmux:work"));
        Assert.Equal("attach:screen:12.x", MuxSteps.For(new MuxSession("screen", "12.x", null, false, "screen -x 12.x"), control: true));
    }
}
