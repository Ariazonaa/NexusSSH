using System.Text;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Ein Befehl, der in eine Shell getippt wird: was getippt wird, und was davon zurückkommt.</summary>
public sealed class TerminalCommandTests
{
    private const string Nonce = "ab12";
    private const string Marker = "\u001b]7777;nexus;ab12;";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Ein Befehl, dessen Zeile die Shell schon gezeigt hat: Enter ist gedrückt.
    private static TerminalCommand Entered(string text)
    {
        var command = new TerminalCommand(text, Nonce);
        Assert.False(command.Feed(Bytes(command.Line)));
        Assert.True(command.IsEchoed);
        command.Entered();
        return command;
    }

    [Fact]
    public void What_a_highlighter_redraws_before_enter_is_no_output()
    {
        var command = new TerminalCommand("echo a", Nonce);
        command.Feed(Bytes(command.Line));

        // So zeichnete zsh-syntax-highlighting die Zeile neu (gesehen 2026-10-31).
        command.Feed(Bytes("\u001b[33mecho\u001b[39m a\u001b[32mls\u001b[39m"));
        command.Entered();
        command.Feed(Bytes($"\r\na\r\n{Marker}0\u0007"));

        Assert.Equal("a\r\n", command.Result(timedOut: false).Output);
    }

    [Fact]
    public void A_plain_command_stays_as_it_is_with_the_marker_behind_it()
    {
        var command = new TerminalCommand("df -h", Nonce);

        Assert.Equal(@"df -h; printf '\033]7777;nexus;ab12;%s\007' ""$?""", command.Line);
    }

    [Fact]
    public void The_keys_clear_the_line_and_stop_before_enter()
    {
        var keys = new TerminalCommand("df -h", Nonce).Keys;

        // Erst ans Ende der Zeile: Strg+U löscht nur, was vor dem Cursor steht. Die Ende-Taste gilt auch im vi-Modus.
        Assert.StartsWith("\u001b[F\u0005\u0015df -h;", keys, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', keys);
        Assert.DoesNotContain('\n', keys);
    }

    [Fact]
    public void Zsh_gets_no_escape_because_in_vi_mode_it_would_turn_the_command_into_vi_keys()
    {
        var keys = new TerminalCommand("df -h", Nonce, isZsh: true).Keys;

        Assert.StartsWith("\u0005\u0015df -h;", keys, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', keys);
    }

    [Theory]
    [InlineData("/usr/bin/zsh", true)]
    [InlineData("/bin/zsh", true)]
    [InlineData("/bin/bash", false)]
    [InlineData("", false)]
    public void Zsh_is_recognised_by_its_name(string shell, bool expected)
        => Assert.Equal(expected, TerminalCommand.IsZsh(shell));

    [Theory]
    [InlineData("echo !!")]
    [InlineData("!rm")]
    [InlineData("grep 'a!b' datei")]
    [InlineData("^alt^neu")]
    public void What_the_shell_would_take_from_its_history_goes_through_eval(string text)
    {
        var line = new TerminalCommand(text, Nonce).Line;

        Assert.Equal($"eval '{text.Replace("'", @"'\''", StringComparison.Ordinal)}'", line[..line.IndexOf("; printf", StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData("sleep 5 &")]
    [InlineData("ls;")]
    [InlineData("ls # alles")]
    [InlineData("ls |")]
    [InlineData("ls \\")]
    public void What_the_marker_could_not_follow_goes_through_eval(string text)
        => Assert.StartsWith("eval '", new TerminalCommand(text, Nonce).Line, StringComparison.Ordinal);

    [Fact]
    public void Several_lines_stay_one_typed_line()
    {
        var command = new TerminalCommand("echo 'a!'\nprintf \"%s\\n\" b", Nonce);

        Assert.StartsWith(@"eval ""$(printf %b 'echo '\''a\0041'\''\nprintf ""%s\\n"" b')""; printf", command.Line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command.Keys);
        Assert.DoesNotContain('\r', command.Keys);
    }

    [Fact]
    public void Inside_double_quotes_no_exclamation_mark_is_left_for_the_history()
        => Assert.DoesNotContain('!', new TerminalCommand("a\nb!c", Nonce).Line);

    [Theory]
    [InlineData("df -h", true)]
    [InlineData("a\nb", true)]
    [InlineData("a\tb", false)]
    [InlineData("a\u0003b", false)]
    [InlineData("a\u001b[Ab", false)]
    [InlineData("a\rb", false)]
    public void Keys_that_would_act_on_the_shell_are_never_typed(string text, bool expected)
        => Assert.Equal(expected, TerminalCommand.CanType(text));

    [Theory]
    [InlineData("/bin/bash", true)]
    [InlineData("/usr/bin/zsh", true)]
    [InlineData("/bin/sh", true)]
    [InlineData("/bin/ash", true)]
    [InlineData("/bin/dash", true)]
    [InlineData("/bin/ksh", true)]
    [InlineData("", true)]
    [InlineData("/usr/bin/fish", false)]
    [InlineData("/bin/csh", false)]
    [InlineData("/bin/tcsh", false)]
    [InlineData("windows", false)]
    public void Only_shells_that_read_the_marker_get_typed_into(string shell, bool expected)
        => Assert.Equal(expected, TerminalCommand.IsTypedShell(shell));

    [Fact]
    public void Enter_comes_only_when_the_line_stands_there_in_full()
    {
        var command = new TerminalCommand("df -h", Nonce);

        command.Feed(Bytes(command.Line[..20]));
        Assert.False(command.IsEchoed);

        command.Feed(Bytes(command.Line[20..]));
        Assert.True(command.IsEchoed);
    }

    // Die Folgen stammen aus echten Shells (2026-10-31): bash leert mit Rückschritten, zsh setzt das erste Zeichen zweimal,
    // BusyBox zeichnet die Zeile neu.
    [Theory]
    [InlineData("\u001b[?2004hbash-5.2# echo REST\b\b\u001b[C\u001b[C\b\b\b\b\b\b\b\b\b\u001b[K{0}")]
    [InlineData("\u001b[K\u001b[?2004he\b{0}")]
    [InlineData("/ # \u001b[6necho REST\b\bST\r/ # \u001b[J{0}")]
    [InlineData("# echo REST\u001b[D\u001b[D^E\b \b\b \b\b \b{0}")]
    public void The_echo_of_a_real_shell_counts(string echo)
    {
        var command = new TerminalCommand("echo neu", Nonce);

        command.Feed(Bytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, echo, command.Line)));

        Assert.True(command.IsEchoed);
    }

    [Fact]
    public void A_line_that_wraps_counts()
    {
        var command = new TerminalCommand("echo " + new string('a', 200), Nonce);
        var line = command.Line;

        // So bricht readline um: Leerzeichen, Wagenrücklauf, weiter in der nächsten Zeile.
        command.Feed(Bytes(line[..70] + " \r" + line[70..150] + " \r\n" + line[150..]));

        Assert.True(command.IsEchoed);
    }

    [Fact]
    public void A_line_that_wraps_in_zsh_counts()
    {
        var command = new TerminalCommand("echo " + new string('a', 200), Nonce);
        var line = command.Line;

        // So bricht zsh um (gesehen 2026-10-31): Es schreibt das Zeichen hinter dem Rand zweimal, mit Rücklauf dazwischen.
        command.Feed(Bytes("\u001b[K\u001b[?2004he\b" + line[..66] + " \r\u001b[K" + line[66] + "\r" + line[66..]));

        Assert.True(command.IsEchoed);
    }

    [Fact]
    public void Zsh_may_reach_the_next_line_without_a_line_feed()
    {
        var command = Entered("echo a");

        // Nach einer umbrochenen Zeile geht zsh mit einer Cursor-Bewegung nach unten (gesehen 2026-10-31).
        var done = command.Feed(Bytes($"\u001b[?2004l\u001b[1B\r\ra\r\n{Marker}0\u0007"));

        Assert.True(done);
        Assert.Equal("a\r\n", command.Result(timedOut: false).Output);
    }

    [Fact]
    public void Colours_of_a_syntax_highlighter_do_not_count()
    {
        var command = new TerminalCommand("ls", Nonce);
        var line = command.Line;

        command.Feed(Bytes("\u001b[32m" + line[..2] + "\u001b[39m\u001b[33m" + line[2..] + "\u001b[39m"));

        Assert.True(command.IsEchoed);
    }

    [Fact]
    public void What_vi_commands_make_of_the_line_is_no_echo()
    {
        var command = new TerminalCommand("echo neu", Nonce);

        // So sah es aus, als zsh im vi-Modus die Tasten als Kommandos las.
        command.Feed(Bytes("echo REST\b\b\u0007\u0007\b\u0007\u001b[2C\bT \b\boT\b T\bnT\beT\buT\b"));

        Assert.False(command.IsEchoed);
    }

    [Fact]
    public void A_password_prompt_shows_no_echo()
    {
        var command = new TerminalCommand("echo neu", Nonce);

        command.Feed(Bytes("[sudo] password for user: "));

        Assert.False(command.IsEchoed);
    }

    [Fact]
    public void Output_between_the_echo_and_the_marker_is_the_result()
    {
        var command = Entered("df -h");

        var done = command.Feed(Bytes($"\r\nline 1\r\nline 2\r\n{Marker}3\u0007user@host:~$ "));

        Assert.True(done);
        var result = command.Result(timedOut: false);
        Assert.Equal("line 1\r\nline 2\r\n", result.Output);
        Assert.Equal(3, result.Status);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void It_does_not_matter_how_the_bytes_are_cut()
    {
        var command = Entered("df -h");

        var done = false;
        foreach (var piece in Bytes($"\r\nüber\r\n{Marker}0\u0007$ "))
        {
            done = command.Feed([piece]);
        }

        Assert.True(done);
        Assert.Equal("über\r\n", command.Result(timedOut: false).Output);
    }

    [Fact]
    public void The_echo_comes_in_pieces_too()
    {
        var command = new TerminalCommand("df -h", Nonce);

        foreach (var piece in Bytes(command.Line))
        {
            command.Feed([piece]);
        }

        Assert.True(command.IsEchoed);
    }

    [Fact]
    public void The_echoed_line_is_not_the_marker()
    {
        var command = Entered("df -h");

        // So steht er auf dem Bildschirm: mit Backslash statt mit dem Zeichen ESC.
        Assert.False(command.Feed(Bytes(command.Line + "\r\n")));
        Assert.False(command.Feed(Bytes("still running\r\n")));
    }

    [Fact]
    public void A_marker_with_another_number_ends_nothing()
    {
        var command = Entered("cat evil");

        var done = command.Feed(Bytes("\r\n\u001b]7777;nexus;ffff;0\u0007rest\r\n"));

        Assert.False(done);
        Assert.Contains("rest", command.Result(timedOut: true).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_much_output_is_cut_and_says_so()
    {
        var command = Entered("flood");

        command.Feed(Bytes("anfang"));
        for (var round = 0; round < 20; round++)
        {
            command.Feed(Enumerable.Repeat((byte)'x', 20_000).ToArray());
        }

        var done = command.Feed(Bytes($"ende\r\n{Marker}0\u0007"));

        // Anfang und Ende bleiben: Beim Suchen eines Fehlers steht das Wichtige am Schluss.
        Assert.True(done);
        var result = command.Result(timedOut: false);
        Assert.True(result.Cut);
        Assert.StartsWith("anfang", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("ende\r\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("bytes left out", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length < TerminalCommand.MaxHead + TerminalCommand.MaxTail + 64, result.Output.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Past_its_time_it_keeps_what_came_and_has_no_status()
    {
        var command = Entered("tail -f x");
        command.Feed(Bytes("\r\nfirst\r\n"));

        var result = command.Result(timedOut: true);

        Assert.True(result.TimedOut);
        Assert.Null(result.Status);
        Assert.Contains("first", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_that_is_no_number_is_no_status()
    {
        var command = Entered("x");

        Assert.True(command.Feed(Bytes($"\r\n{Marker}abc\u0007")));

        Assert.Null(command.Result(timedOut: false).Status);
    }

    [Fact]
    public void Every_command_gets_its_own_number()
        => Assert.NotEqual(new TerminalCommand("a").Line, new TerminalCommand("a").Line);

    [Theory]
    [InlineData("\u001b[?1049h", true)]
    [InlineData("\u001b[?1047h", true)]
    [InlineData("\u001b[?47h", true)]
    [InlineData("\u001b[?1049h text \u001b[?1049l", false)]
    [InlineData("\u001b[?1049l", false)]
    [InlineData("\u001b[?25h plain", false)]
    public void The_full_screen_of_a_program_is_seen_in_the_output(string text, bool expected)
    {
        var screen = new AlternateScreen();

        screen.Feed(Bytes(text));

        Assert.Equal(expected, screen.IsOn);
    }

    [Fact]
    public void The_full_screen_is_seen_however_the_bytes_are_cut()
    {
        var screen = new AlternateScreen();

        foreach (var piece in Bytes("x\u001b[?1049h"))
        {
            screen.Feed([piece]);
        }

        Assert.True(screen.IsOn);
        foreach (var piece in Bytes("\u001b[?1049lz"))
        {
            screen.Feed([piece]);
        }

        Assert.False(screen.IsOn);
    }
}
