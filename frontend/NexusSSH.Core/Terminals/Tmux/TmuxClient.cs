using System.Globalization;
using System.Text;

namespace NexusSSH.Core.Terminals.Tmux;

/// <summary>tmux hat einen Befehl abgelehnt (<c>%error</c>) oder den Steuermodus beendet, bevor die Antwort kam.</summary>
public sealed class TmuxException(string message) : Exception(message);

/// <summary>
/// Die Seite der App im Steuermodus von tmux: liest die Zeilen des Protokolls und schickt Befehle. Ohne Netz und
/// ohne Oberfläche – die Zeilen bekommt er von <see cref="TmuxStream"/>, seine Befehle gehen über
/// <c>send</c> in die Shell, in der <c>tmux -CC</c> läuft.
/// <para>
/// Antworten kommen als Blöcke (<c>%begin … %end</c>) in der Reihenfolge der Befehle. Der erste Block gehört dem
/// Anhängen selbst; tmux kennzeichnet die Blöcke zu Befehlen dieses Clients mit einer 1 am Ende.
/// </para>
/// <para>
/// Was in einen Befehl gesetzt wird, ist nie Text des Servers: nur feste Wörter, Zahlen und Kennungen von Fenstern
/// (<c>@3</c>) und Bereichen (<c>%7</c>), die vorher auf genau diese Form geprüft sind.
/// </para>
/// </summary>
public sealed class TmuxClient(Action<ReadOnlyMemory<byte>> send)
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource<IReadOnlyList<byte[]>>> _pending = new();
    private List<byte[]>? _block;
    private string? _blockNumber;
    private bool _blockOwn;
    private bool _started;
    private bool _exited;
    private (int Columns, int Rows) _size;
    private (int Major, int Minor) _version = (3, 0);
    private readonly Dictionary<string, Decoder> _decoders = [];

    /// <summary>So viele Zeilen des Verlaufs holt ein Bild höchstens mit.</summary>
    public const int HistoryLines = 2000;

    /// <summary>Die Version von tmux, soweit sie sich erfragen ließ (sonst 3.0).</summary>
    public (int Major, int Minor) Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    private bool AtLeast(int major, int minor)
    {
        var (have, low) = Version;
        return have > major || (have == major && low >= minor);
    }

    /// <summary>
    /// Fragt tmux nach seiner Version – danach richtet sich, wie Tasten geschickt werden (vor 3.0 ohne
    /// <c>-H</c>) und ob sich ein Bereich anhalten lässt (ab 3.2). Ein tmux, das die Frage nicht kennt, gilt als alt.
    /// </summary>
    public async Task ProbeAsync()
    {
        IReadOnlyList<byte[]> lines;
        try
        {
            lines = await CommandAsync("display-message -p \"v=#{version}\"").ConfigureAwait(false);
        }
        catch (TmuxException)
        {
            return;
        }

        var version = ParseVersion(lines.Count > 0 ? Text(lines[0]) : string.Empty);
        lock (_gate)
        {
            _version = version;
        }

        // tmux bremst von da an selbst: Liegt dieser Client zu weit zurück (langsame Leitung), hält es den Bereich
        // an, statt die Ausgabe ohne Ende zu sammeln, und meldet es (`%pause`).
        if (AtLeast(3, 2))
        {
            Fire(string.Create(CultureInfo.InvariantCulture, $"refresh-client -f pause-after={PauseAfterSeconds}"));
        }
    }

    /// <summary>„v=3.7c“, „v=next-3.5“, „v=2.6“ → Haupt- und Nebennummer; ohne Zahl ein altes tmux (2.0).</summary>
    public static (int Major, int Minor) ParseVersion(string answer)
    {
        var text = answer.AsSpan();
        var start = text.IndexOfAnyInRange('0', '9');
        if (start < 0)
        {
            return (2, 0);
        }

        text = text[start..];
        var end = text.IndexOfAnyExceptInRange('0', '9');
        var first = end < 0 ? text : text[..end];
        if (first.Length > 3 || !int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return (2, 0);
        }

        var minor = 0;
        if (end >= 0 && end + 1 < text.Length && text[end] == '.')
        {
            var rest = text[(end + 1)..];
            var stop = rest.IndexOfAnyExceptInRange('0', '9');
            var second = stop < 0 ? rest : rest[..stop];
            if (second.Length is > 0 and <= 3)
            {
                _ = int.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture, out minor);
            }
        }

        return (major, minor);
    }

    /// <summary>tmux hat das Anhängen bestätigt – ab jetzt nimmt es Befehle an.</summary>
    public event Action? Started;

    /// <summary>Ein Fenster kam dazu, fiel weg oder änderte sich (Name, Bereiche, aktiver Bereich): Die Liste ist neu zu lesen.</summary>
    public event Action? WindowsChanged;

    /// <summary>Ausgabe eines Bereichs.</summary>
    public event Action<string, byte[]>? Output;

    /// <summary>
    /// tmux hat einen Bereich von sich aus angehalten, weil dieser Client zu weit zurückliegt (<c>pause-after</c>).
    /// Was unterdessen kommt, schickt es nicht nach – wer fortsetzt, zeichnet neu.
    /// </summary>
    public event Action<string>? Paused;

    /// <summary>So viele Sekunden darf dieser Client zurückliegen, bevor tmux einen Bereich anhält (ab tmux 3.2).</summary>
    public const int PauseAfterSeconds = 5;

    /// <summary>tmux hat den Steuermodus beendet; der Grund, wenn es einen nennt.</summary>
    public event Action<string?>? Exited;

    public bool HasExited => _exited;

    /// <summary>Eine Kennung eines Bereichs (<c>%12</c>) oder Fensters (<c>@3</c>), wie tmux sie vergibt.</summary>
    public static bool IsId(string? id, char sigil)
        => id is { Length: >= 2 and <= 12 } && id[0] == sigil && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Eine Zeile des Steuerprotokolls.</summary>
    public void OnLine(byte[] line)
    {
        TaskCompletionSource<IReadOnlyList<byte[]>>? done = null;
        IReadOnlyList<byte[]>? answer = null;
        string? error = null;
        Action? raise = null;

        lock (_gate)
        {
            if (_block is not null)
            {
                // Innerhalb eines Blocks ist jede Zeile Antwort – bis zum Ende mit derselben Nummer.
                var closing = Head(line);
                if (closing is ["%end" or "%error", _, var number, ..] && number == _blockNumber)
                {
                    var (lines, own) = (_block, _blockOwn);
                    (_block, _blockNumber) = (null, null);
                    if (own && _pending.TryDequeue(out done))
                    {
                        if (closing[0] == "%end")
                        {
                            answer = lines;
                        }
                        else
                        {
                            error = lines.Count > 0 ? Text(lines[0]) : "tmux refused the command";
                        }
                    }
                    else if (!_started)
                    {
                        _started = true;
                        raise = Started;
                    }
                }
                else if (_block.Count < 100_000)
                {
                    _block.Add(line);
                }
            }
            else
            {
                var words = Head(line);
                switch (words)
                {
                    case ["%begin", _, var number, var flags, ..]:
                        (_block, _blockNumber, _blockOwn) = ([], number, flags == "1");
                        break;
                    // Mit `pause-after` nennt tmux die Ausgabe so: „%extended-output %1 <Alter> : <Daten>“ – zwischen
                    // dem Alter und dem Doppelpunkt können künftig weitere Angaben stehen.
                    case ["%extended-output", var pane, ..] when IsId(pane, '%') && line.AsSpan().IndexOf(" : "u8) is > 0 and var colon:
                        var late = TmuxStream.Unescape(line.AsSpan(colon + 3));
                        raise = () => Output?.Invoke(pane, late);
                        break;
                    case ["%pause", var pane, ..] when IsId(pane, '%'):
                        raise = () => Paused?.Invoke(pane);
                        break;
                    case ["%output", var pane, ..] when IsId(pane, '%'):
                        // Die Daten beginnen hinter „%output %n “ – Leerzeichen darin gehören dazu.
                        var data = TmuxStream.Unescape(line.AsSpan(Math.Min(line.Length, "%output ".Length + pane.Length + 1)));
                        raise = () => Output?.Invoke(pane, data);
                        break;
                    // Auch die „unlinked“-Formen: Endet ein Fenster, hat tmux es beim Melden schon aus der Sitzung
                    // genommen und nennt es so (gesehen an tmux 3.7). Was wirklich gilt, sagt danach die Liste.
                    case ["%window-add" or "%window-close" or "%window-renamed" or "%window-pane-changed" or "%session-window-changed" or "%session-changed" or "%layout-change"
                        or "%unlinked-window-add" or "%unlinked-window-close" or "%unlinked-window-renamed", ..]:
                        raise = WindowsChanged;
                        break;
                    case ["%exit", ..]:
                        _exited = true;
                        var reason = line.Length > 6 ? Text(line.AsSpan(6)) : null;
                        var waiting = _pending.ToArray();
                        _pending.Clear();
                        raise = () =>
                        {
                            foreach (var open in waiting)
                            {
                                open.TrySetException(new TmuxException("tmux left control mode"));
                            }

                            Exited?.Invoke(reason);
                        };
                        break;
                }
            }
        }

        // Außerhalb der Sperre: Wer zuhört, schickt gleich den nächsten Befehl.
        if (done is not null)
        {
            if (answer is not null)
            {
                done.TrySetResult(answer);
            }
            else
            {
                done.TrySetException(new TmuxException(error ?? "tmux refused the command"));
            }
        }

        raise?.Invoke();
    }

    /// <summary>Der Träger ist weg (Verbindung zu, Tab geschlossen): Wer noch auf eine Antwort wartet, bekommt keine.</summary>
    public void Abandon()
    {
        TaskCompletionSource<IReadOnlyList<byte[]>>[] waiting;
        lock (_gate)
        {
            _exited = true;
            waiting = [.. _pending];
            _pending.Clear();
        }

        foreach (var open in waiting)
        {
            open.TrySetException(new TmuxException("the connection that carried tmux is gone"));
        }
    }

    /// <summary>Schickt einen Befehl und liefert die Zeilen seiner Antwort. Wirft <see cref="TmuxException"/>.</summary>
    public Task<IReadOnlyList<byte[]>> CommandAsync(string command)
    {
        // Ein Zeilenumbruch im Befehl wäre ein zweiter Befehl – und brächte die Zuordnung der Antworten durcheinander.
        if (command.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            return Task.FromException<IReadOnlyList<byte[]>>(new TmuxException("a command must be one line"));
        }

        var answer = new TaskCompletionSource<IReadOnlyList<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_exited)
            {
                return Task.FromException<IReadOnlyList<byte[]>>(new TmuxException("tmux left control mode"));
            }

            _pending.Enqueue(answer);

            // Unter der Sperre schicken: Die Reihenfolge auf der Leitung ist die der Warteschlange.
            send(Encoding.UTF8.GetBytes(command + "\n"));
        }

        return answer.Task;
    }

    // Ein Befehl, dessen Antwort niemand braucht; ein Fehler darin bleibt folgenlos.
    private void Fire(string command) => _ = CommandAsync(command).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    /// <summary>
    /// Eingabe für einen Bereich: Byte für Byte als Hexzahl (<c>send-keys -H</c>, tmux ab 3.0) – so muss nichts
    /// maskiert werden, was auch immer getippt oder eingefügt wird.
    /// </summary>
    public void SendKeys(string pane, ReadOnlySpan<byte> input)
    {
        if (!IsId(pane, '%'))
        {
            return;
        }

        if (!AtLeast(3, 0))
        {
            SendKeysOld(pane, input);
            return;
        }

        const int Chunk = 128;
        for (var offset = 0; offset < input.Length; offset += Chunk)
        {
            var part = input.Slice(offset, Math.Min(Chunk, input.Length - offset));
            var command = new StringBuilder("send-keys -t ").Append(pane).Append(" -H");
            foreach (var value in part)
            {
                command.Append(' ').Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            Fire(command.ToString());
        }
    }

    // tmux vor 3.0 kennt `-H` nicht. Dort gehen ASCII-Zeichen (auch Steuerzeichen) als Tastennamen in Hex
    // (`0x1b`) und alles andere als Text in einfachen Anführungszeichen (`-l 'é'`): Darin kann weder ein
    // Anführungszeichen noch ein Zeilenumbruch stehen, denn beides ist ASCII und nimmt den anderen Weg.
    private void SendKeysOld(string pane, ReadOnlySpan<byte> input)
    {
        string text;
        lock (_gate)
        {
            // Ein Zeichen kann über zwei Aufrufe verteilt ankommen – der Leser merkt sich den Anfang.
            if (!_decoders.TryGetValue(pane, out var decoder))
            {
                _decoders[pane] = decoder = Encoding.UTF8.GetDecoder();
            }

            var chars = new char[decoder.GetCharCount(input, flush: false)];
            text = new string(chars, 0, decoder.GetChars(input, chars, flush: false));
        }

        const int Most = 64;
        var index = 0;
        while (index < text.Length)
        {
            var ascii = text[index] < 0x80;
            var end = index;
            while (end < text.Length && text[end] < 0x80 == ascii && end - index < Most)
            {
                end++;
            }

            // Ein Zeichen aus zwei Hälften bleibt zusammen.
            if (!ascii && end < text.Length && char.IsLowSurrogate(text[end]))
            {
                end++;
            }

            var command = new StringBuilder("send-keys -t ").Append(pane);
            if (ascii)
            {
                foreach (var key in text.AsSpan(index, end - index))
                {
                    command.Append(" 0x").Append(((int)key).ToString("x2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                command.Append(" -l '").Append(text, index, end - index).Append('\'');
            }

            Fire(command.ToString());
            index = end;
        }
    }

    /// <summary>
    /// Hält die Ausgabe eines Bereichs an oder lässt sie weiterlaufen (tmux ab 3.2). Was unterdessen kam, schickt
    /// tmux danach <b>nicht</b> nach – wer fortsetzt, zeichnet den Bereich neu. <c>false</c>: Dieses tmux kann es nicht.
    /// </summary>
    public bool SetPaused(string pane, bool paused)
    {
        if (!IsId(pane, '%') || !AtLeast(3, 2))
        {
            return false;
        }

        Fire($"refresh-client -A \"{pane}:{(paused ? "pause" : "continue")}\"");
        return true;
    }

    /// <summary>Ein neues Fenster in der Sitzung; sein Tab entsteht, sobald tmux es meldet.</summary>
    public void NewWindow() => Fire("new-window");

    /// <summary>
    /// Ein Name für ein Fenster, der sich in einen Befehl setzen lässt: Buchstaben, Ziffern, Leerzeichen, Punkt,
    /// Unterstrich und Bindestrich – nichts, was tmux oder eine Shell anders lesen könnte als gemeint.
    /// </summary>
    public static bool IsPlainName(string? name)
        => name is { Length: > 0 and <= 40 }
            && name[0] is not ('-' or ' ')
            && name[^1] != ' '
            && name.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-');

    /// <summary>Benennt ein Fenster um – nur mit einem schlichten Namen (<see cref="IsPlainName"/>).</summary>
    public bool RenameWindow(string window, string name)
    {
        if (!IsId(window, '@') || !IsPlainName(name))
        {
            return false;
        }

        Fire($"rename-window -t {window} '{name}'");
        return true;
    }

    /// <summary>Die Größe, die tmux für diesen Client annimmt – alle Fenster der Sitzung richten sich danach.</summary>
    public void Resize(int columns, int rows)
    {
        (columns, rows) = (Math.Clamp(columns, 2, 1000), Math.Clamp(rows, 2, 1000));
        lock (_gate)
        {
            if (_size == (columns, rows))
            {
                return;
            }

            _size = (columns, rows);
        }

        Fire(string.Create(CultureInfo.InvariantCulture, $"refresh-client -C {columns},{rows}"));
    }

    /// <summary>Hängt ab: tmux beendet den Steuermodus, die Sitzung auf dem Server läuft weiter.</summary>
    public void Detach() => Fire("detach-client");

    /// <summary>
    /// Die Fenster der Sitzung samt ihren Bereichen, in der Reihenfolge von tmux. Eine Zeile je Bereich: Fenster,
    /// Bereich, ob aktiv, Lage und Größe, dann der Name des Fensters (der Leerzeichen haben darf).
    /// </summary>
    public async Task<IReadOnlyList<TmuxWindowInfo>> WindowsAsync()
    {
        var lines = await CommandAsync("list-panes -s -F \"#{window_id} #{pane_id} #{pane_active} #{pane_left} #{pane_top} #{pane_width} #{pane_height} #{window_name}\"").ConfigureAwait(false);
        var windows = new List<(string Id, string Name, List<TmuxPaneInfo> Panes)>();
        foreach (var line in lines)
        {
            var parts = Text(line).Split(' ', 8);
            if (parts.Length < 7 || !IsId(parts[0], '@') || !IsId(parts[1], '%'))
            {
                continue;
            }

            var numbers = parts[2..7].Select(word => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < 100_000 ? number : -1).ToArray();
            if (numbers.Any(number => number < 0))
            {
                continue;
            }

            var index = windows.FindIndex(window => window.Id == parts[0]);
            if (index < 0)
            {
                windows.Add((parts[0], parts.Length > 7 ? parts[7] : string.Empty, []));
                index = windows.Count - 1;
            }

            // Eine Kennung nennt tmux nur einmal; was sich wiederholt, ist nicht von ihm.
            if (windows[index].Panes.Count < 64 && !windows.Exists(window => window.Panes.Exists(pane => pane.Id == parts[1])))
            {
                windows[index].Panes.Add(new TmuxPaneInfo(parts[1], numbers[0] == 1, numbers[1], numbers[2], numbers[3], numbers[4]));
            }
        }

        return [.. windows.Select(window => new TmuxWindowInfo(window.Id, window.Name, window.Panes))];
    }

    /// <summary>Gibt einem Bereich eine Breite oder Höhe – die Nachbarn im Fenster richten sich danach.</summary>
    public void ResizePane(string pane, int? columns, int? rows)
    {
        if (!IsId(pane, '%'))
        {
            return;
        }

        if (columns is { } width)
        {
            Fire(string.Create(CultureInfo.InvariantCulture, $"resize-pane -t {pane} -x {Math.Clamp(width, 2, 1000)}"));
        }

        if (rows is { } height)
        {
            Fire(string.Create(CultureInfo.InvariantCulture, $"resize-pane -t {pane} -y {Math.Clamp(height, 2, 1000)}"));
        }
    }


    /// <summary>
    /// Was ein Bereich gerade zeigt, als Bytes für ein leeres Terminal: Bildschirm löschen, die Zeilen samt Farben,
    /// dann der Cursor an seine Stelle.
    /// </summary>
    public async Task<byte[]> SnapshotAsync(string pane)
    {
        if (!IsId(pane, '%'))
        {
            return [];
        }

        // Erst, wo der Cursor steht, wie hoch der Bereich ist und wie viel Verlauf er hat; ein Programm im
        // zweiten Bildschirm (vim, htop) hat keinen, der dazugehört.
        var state = await CommandAsync($"display-message -p -t {pane} \"#{{cursor_x}} #{{cursor_y}} #{{pane_height}} #{{alternate_on}} #{{history_size}}\"").ConfigureAwait(false);
        var numbers = (state.Count > 0 ? Text(state[0]) : string.Empty)
            .Split(' ')
            .Select(word => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1)
            .ToArray();
        var (column, row, height) = numbers is [var x, var y, var rows, ..] ? (x, y, rows) : (-1, -1, -1);
        var history = numbers is [_, _, _, 0, > 0 and var kept, ..] && height is > 0 and < 1000 ? Math.Min(kept, HistoryLines) : 0;

        var lines = (await CommandAsync(history > 0
            ? string.Create(CultureInfo.InvariantCulture, $"capture-pane -p -e -S -{history} -t {pane}")
            : $"capture-pane -p -e -t {pane}").ConfigureAwait(false)).ToList();

        // Mit Verlauf nennt tmux erst dessen Zeilen, dann die des Bildschirms.
        var before = history > 0 ? Math.Clamp(lines.Count - height, 0, history) : 0;
        var bytes = new List<byte>("\u001b[0m\u001b[H\u001b[2J\u001b[3J"u8.ToArray());
        for (var index = 0; index < before; index++)
        {
            bytes.AddRange(lines[index]);
            bytes.AddRange("\u001b[0m\r\n"u8);
        }

        if (before > 0)
        {
            // Den Verlauf aus dem Bild schieben: Danach beginnt der Bildschirm oben, wie bei tmux.
            for (var index = 0; index < height; index++)
            {
                bytes.AddRange("\r\n"u8);
            }

            bytes.AddRange("\u001b[H"u8);
        }

        var last = lines.Count;
        while (last > before && lines[last - 1].Length == 0)
        {
            last--;
        }

        for (var index = before; index < last; index++)
        {
            if (index > before)
            {
                bytes.AddRange("\r\n"u8);
            }

            bytes.AddRange(lines[index]);
        }

        bytes.AddRange("\u001b[0m"u8);
        if (column is >= 0 and < 1000 && row is >= 0 and < 1000)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\u001b[{row + 1};{column + 1}H")));
        }

        return [.. bytes];
    }

    // Die ersten Wörter einer Zeile, getrennt an Leerzeichen – für die Art der Meldung und ihre Kennungen.
    private static string[] Head(ReadOnlySpan<byte> line)
        => line.Length > 0 && line[0] == (byte)'%' ? Text(line[..Math.Min(line.Length, 200)]).Split(' ', 5) : [];

    // Text des Servers für Namen und Meldungen: als UTF-8 gelesen, was nicht passt, wird zum Ersatzzeichen.
    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
