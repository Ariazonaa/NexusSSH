using System.Security.Cryptography;
using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Ein Befehl, der sichtbar in die Shell einer Sitzung getippt wird (Werkzeug <c>run_command</c>): was getippt wird, und
/// wie sich aus der Ausgabe des Terminals lesen lässt, wann er endete. Hinter dem Befehl gibt die Shell eine Steuerfolge
/// mit einer Zufallszahl und dem Rückgabewert aus. Das Echo der getippten Zeile trägt sie nur als Text (<c>\033</c>),
/// nicht als Zeichen ESC, und Ausgabe eines Servers kann die Zahl nicht kennen. Das Terminal ignoriert die Folge.
/// Was hier gelesen wird, ist Ausgabe des Servers; was davon an die KI geht, säubert <see cref="Format.VisibleText"/>.
/// </summary>
public sealed partial class TerminalCommand
{
    /// <summary>So viel vom Anfang der Ausgabe bleibt, wie im Kern für den eigenen Kanal.</summary>
    public const int MaxHead = 16 * 1024;

    /// <summary>So viel vom Ende bleibt. Zusammen mit dem Anfang passt das noch in die Antwort an ein MCP-Programm.</summary>
    public const int MaxTail = 32 * 1024;

    /// <summary>Räumt die Zeile weg, wenn sie nicht ausgeführt wird. Ohne ESC: Das änderte im vi-Modus den Zustand.</summary>
    public const string ClearAgain = "\u0005\u0015";

    // Erst ans Ende der Zeile: Strg+U löscht nur, was vor dem Cursor steht. Stünde er mitten in einer angefangenen
    // Zeile, bliebe ihr Rest hinter dem Befehl stehen und liefe mit. Beides, weil die Shells verschieden hören: Die
    // Ende-Taste gilt bei readline auch im vi-Modus (dort fügt Strg+E nur etwas ein, das Strg+U mitnimmt), Strg+E bei zsh
    // ohne Einstellungen. An bash (Emacs und vi), dash, BusyBox und zsh nachgestellt, 2026-10-31.
    private const string ClearLine = "\u001b[F\u0005\u0015";

    // zsh kennt die Ende-Taste ohne Einstellungen nicht. Im vi-Modus wäre ESC dort der Weg in den Befehlsmodus, und der
    // ganze Befehl würde zu vi-Kommandos: Aus `echo neu` wurde `echo REo neuT`. Lieber bleibt in diesem seltenen Fall
    // ein Rest hinter dem Cursor stehen, als dass etwas anderes läuft als im Dialog.
    private const string ClearLineZsh = ClearAgain;

    // So viel Echo wird angesehen: Die Zeile ist höchstens gut 16 KiB lang, und die Shell zeichnet sie um.
    private const int MaxEcho = 128 * 1024;

    private readonly string _clearLine;

    // Die Shells, die die Zeile hinter dem Befehl lesen wie gedacht. fish kennt $? nicht, csh keine Zuweisung in dieser Form.
    private static readonly string[] TypedShells = ["bash", "rbash", "zsh", "sh", "dash", "ash", "ksh", "mksh", "busybox"];

    private readonly Lock _gate = new();
    private readonly byte[] _marker;
    private readonly List<byte> _pending = [];
    private readonly MemoryStream _output = new();
    private readonly List<byte> _tail = [];
    private long _leftOut;
    private readonly List<byte> _echo = [];
    private readonly string _expected;

    private bool _entered;
    private bool _done;
    private int? _status;

    /// <param name="command">Der Befehl, ohne Steuerzeichen außer Zeilenumbrüchen (<see cref="CanType"/>).</param>
    /// <param name="number">Die Zufallszahl; nur Tests nennen eine.</param>
    /// <param name="isZsh">Die Anmelde-Shell ist zsh (<see cref="IsZsh"/>).</param>
    public TerminalCommand(string command, string? number = null, bool isZsh = false)
    {
        _clearLine = isZsh ? ClearLineZsh : ClearLine;
        number ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _marker = Encoding.ASCII.GetBytes($"\u001b]7777;nexus;{number};");
        Line = $@"{Body(command)}; printf '\033]7777;nexus;{number};%s\007' ""$?""";
        _expected = Compact(Line);
    }

    /// <summary>Die Zeile, wie sie in der Shell steht. Immer genau eine.</summary>
    public string Line { get; }

    /// <summary>
    /// Was zuerst getippt wird: die angefangene Zeile leeren (sonst stünde sie vor dem Befehl), dann die Zeile – ohne
    /// Enter. Das kommt erst, wenn die Shell sie zeigt (<see cref="IsEchoed"/>).
    /// </summary>
    public string Keys => _clearLine + Line;

    /// <summary>
    /// Die Shell zeigt genau die freigegebene Zeile. Erst dann darf Enter folgen: Liest eine Shell im vi-Befehlsmodus die
    /// Tasten als Kommandos, steht dort etwas anderes, und an einer Passwortfrage oder bei einem Programm ohne Echo gar
    /// nichts. So läuft nie etwas anderes als im Dialog.
    /// </summary>
    public bool IsEchoed { get; private set; }

    /// <summary>Ob die Anmelde-Shell zsh ist.</summary>
    public static bool IsZsh(string loginShell) => loginShell.Trim().EndsWith("/zsh", StringComparison.Ordinal) || loginShell.Trim() == "zsh";

    /// <summary>
    /// Ob sich der Text tippen lässt. Andere Steuerzeichen wären Tasten: Tab ergänzt, ESC und Strg+C wirken auf die Shell –
    /// und zwar anders, als der Dialog es zeigt. So ein Befehl läuft im eigenen Kanal.
    /// </summary>
    public static bool CanType(string command) => !command.Any(character => char.IsControl(character) && character != '\n');

    /// <summary>Ob in diese Anmelde-Shell getippt wird. Leer heißt: Der Server nennt keine, also die von POSIX.</summary>
    public static bool IsTypedShell(string loginShell)
    {
        var name = loginShell.Trim();
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Length == 0 || TypedShells.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>Enter ist gedrückt: Ab jetzt ist, was kommt, Ausgabe. Was davor kam, zeichnet nur die Zeile neu.</summary>
    public void Entered()
    {
        lock (_gate)
        {
            _entered = true;
        }
    }

    /// <summary>Liest ein Stück Ausgabe des Terminals; <c>true</c>, sobald der Befehl fertig ist.</summary>
    public bool Feed(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_done)
            {
                return true;
            }

            if (!IsEchoed)
            {
                NoteEcho(data);
                return false;
            }

            if (!_entered)
            {
                return false;
            }

            _pending.AddRange(data);
            var pending = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
            var at = pending.IndexOf(_marker);
            if (at >= 0)
            {
                var rest = pending[(at + _marker.Length)..];
                var end = rest.IndexOf((byte)7);
                if (end >= 0)
                {
                    Keep(pending[..at]);
                    _status = StatusOf(rest[..end]);
                    _done = true;
                    _pending.Clear();
                    return true;
                }

                Keep(pending[..at]);
                _pending.RemoveRange(0, at);
                return false;
            }

            // Das Ende kann die Marke sein, die im nächsten Stück weitergeht.
            var kept = Math.Max(0, pending.Length - _marker.Length);
            Keep(pending[..kept]);
            _pending.RemoveRange(0, kept);
            return false;
        }
    }

    /// <summary>Was bisher kam. Fehler- und Standardausgabe stehen in einem Terminal im selben Strom.</summary>
    public CommandRun Result(bool timedOut)
    {
        lock (_gate)
        {
            if (!_done)
            {
                Keep(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending));
                _pending.Clear();
            }

            // Was die Shell beim Enter schreibt, steht vor der Ausgabe: der Weg in die nächste Zeile, mal ein Zeilenumbruch,
            // mal eine Bewegung des Cursors (zsh nach einer umbrochenen Zeile).
            DropMiddle();
            var head = Encoding.UTF8.GetString(_output.GetBuffer(), 0, (int)_output.Length);
            var tail = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_tail));
            var text = _leftOut > 0 ? $"{head}\n[... {_leftOut} bytes left out ...]\n{tail}" : head + tail;
            var output = Format.WithoutControlSequences(text);
            return new CommandRun(_status, output.TrimStart('\r', '\n'), string.Empty, _leftOut > 0, timedOut);
        }
    }

    // Hinter diesen Befehlen wäre „; printf“ ein Fehler oder ein Kommentar; `eval` nimmt sie ganz. Dasselbe gilt für `!`
    // und ein `^` am Anfang: Eine interaktive Shell setzte dort etwas aus ihrem Verlauf ein, und es liefe nicht mehr, was
    // im Dialog stand (gesehen an bash, 2026-10-31: aus `echo !!` wurde der vorige Befehl). In einfachen
    // Anführungszeichen erweitert sie nichts.
    private static string Body(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Contains('\n'))
        {
            return Lines(trimmed);
        }

        var plain = !trimmed.Contains('#') && !trimmed.Contains('!') && !trimmed.StartsWith('^')
            && !trimmed.EndsWith(';') && !trimmed.EndsWith('&') && !trimmed.EndsWith('|') && !trimmed.EndsWith('\\');
        return plain ? trimmed : $"eval '{command.Replace("'", @"'\''", StringComparison.Ordinal)}'";
    }

    // Mehrere Zeilen als eine: Ein getipptes Enter mitten im Text käme vor der Prüfung des Echos. `%b` macht aus \n den
    // Zeilenumbruch. Die doppelten Anführungszeichen um $(…) sehen die Verlaufs-Erweiterung, deshalb ist `!` hier eine Zahl.
    private static string Lines(string command)
    {
        var text = new StringBuilder();
        foreach (var character in command)
        {
            text.Append(character switch
            {
                '\\' => @"\\",
                '\'' => @"'\''",
                '\n' => @"\n",
                '!' => @"\0041",
                _ => character.ToString(),
            });
        }

        return $"""eval "$(printf %b '{text}')" """.TrimEnd();
    }

    // Was vom Echo zählt: ohne Steuerfolgen und Zwischenraum (umbrochene Zeilen, Farben), Rückschritte ausgeführt.
    private void NoteEcho(ReadOnlySpan<byte> data)
    {
        _echo.AddRange(data);
        if (_echo.Count > MaxEcho)
        {
            _echo.RemoveRange(0, _echo.Count - MaxEcho);
        }

        var text = Format.WithoutControlSequences(Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_echo)));
        var seen = Compact(WrappedByZsh().Replace(text, "$1"));
        IsEchoed = seen.Contains(_expected, StringComparison.Ordinal);
    }

    // Am Rand schreibt zsh Leerzeichen, Rücklauf, das nächste Zeichen, Rücklauf und dasselbe Zeichen noch einmal. Eine zweite
    // Fassung davon, die stimmt, gibt es nur mit einem Modell des Bildschirms; schlägt das fehl, läuft eben nichts.
    [System.Text.RegularExpressions.GeneratedRegex(@" \r(.)\r\1")]
    private static partial System.Text.RegularExpressions.Regex WrappedByZsh();

    private static string Compact(string text)
    {
        var compact = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\b')
            {
                compact.Length = Math.Max(0, compact.Length - 1);
            }
            else if (!char.IsWhiteSpace(character) && !char.IsControl(character))
            {
                compact.Append(character);
            }
        }

        return compact.ToString();
    }

    private static int? StatusOf(ReadOnlySpan<byte> text)
        => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var status) ? status : null;

    // Anfang und Ende bleiben, die Mitte fällt weg: Beim Suchen eines Fehlers steht das Wichtige am Schluss.
    private void Keep(ReadOnlySpan<byte> data)
    {
        var intoHead = Math.Min(data.Length, MaxHead - (int)_output.Length);
        _output.Write(data[..intoHead]);
        _tail.AddRange(data[intoHead..]);
        if (_tail.Count >= MaxTail * 2)
        {
            DropMiddle();
        }
    }

    private void DropMiddle()
    {
        var excess = _tail.Count - MaxTail;
        if (excess > 0)
        {
            _tail.RemoveRange(0, excess);
            _leftOut += excess;
        }
    }
}

/// <summary>
/// Ob gerade ein Programm den ganzen Bildschirm des Terminals belegt (vim, htop, less, tmux), gelesen aus der Ausgabe.
/// Dann wird nicht getippt: Der Text landete in diesem Programm.
/// </summary>
public sealed class AlternateScreen
{
    private static readonly byte[] Start = "\u001b[?"u8.ToArray();

    // Die längste der Folgen: ESC [ ? 1 0 4 9 h.
    private const int Longest = 8;

    private byte[] _carry = [];

    /// <summary>Ein Programm belegt den Bildschirm.</summary>
    public bool IsOn { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        // Der häufige Fall, Stück für Stück Text: nichts zu tun.
        if (_carry.Length == 0 && data.IndexOf((byte)0x1b) < 0)
        {
            return;
        }

        byte[] all = [.. _carry, .. data];
        ReadOnlySpan<byte> rest = all;
        _carry = [];
        while (true)
        {
            var at = rest.IndexOf(Start);
            if (at < 0)
            {
                // ESC am Ende kann der Anfang einer Folge sein.
                var open = rest.LastIndexOf((byte)0x1b);
                if (open >= 0 && rest.Length - open < Longest)
                {
                    _carry = rest[open..].ToArray();
                }

                return;
            }

            rest = rest[(at + Start.Length)..];
            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit((char)rest[digits]))
            {
                digits++;
            }

            if (digits == rest.Length)
            {
                if (digits < Longest)
                {
                    _carry = [.. Start, .. rest];
                }

                return;
            }

            if (rest[..digits].SequenceEqual("1049"u8) || rest[..digits].SequenceEqual("1047"u8) || rest[..digits].SequenceEqual("47"u8))
            {
                if (rest[digits] == (byte)'h')
                {
                    IsOn = true;
                }
                else if (rest[digits] == (byte)'l')
                {
                    IsOn = false;
                }
            }

            rest = rest[digits..];
        }
    }
}
