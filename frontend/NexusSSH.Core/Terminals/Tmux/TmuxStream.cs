namespace NexusSSH.Core.Terminals.Tmux;

/// <summary>
/// Trennt, was ein Terminal im Steuermodus von tmux (<c>tmux -CC</c>) empfängt: Vor dem Beginn und nach dem Ende
/// ist es gewöhnliche Ausgabe für das Terminal; dazwischen sind es Zeilen des Steuerprotokolls. tmux leitet den
/// Modus mit <c>ESC P 1000p</c> ein und beendet ihn nach <c>%exit</c> mit <c>ESC \</c>.
/// <para>
/// Die Bytes kommen vom Server, in beliebigen Stücken. Eine Zeile wird nie länger gesammelt als
/// <see cref="MaxLine"/> – was darüber hinausgeht, fällt weg, statt den Speicher zu füllen.
/// </para>
/// </summary>
public sealed class TmuxStream
{
    /// <summary>Länger wird keine Zeile gesammelt; tmux schickt Ausgabe in weit kleineren Stücken.</summary>
    public const int MaxLine = 1024 * 1024;

    private static readonly byte[] Intro = [0x1B, (byte)'P', (byte)'1', (byte)'0', (byte)'0', (byte)'0', (byte)'p'];

    private readonly List<byte> _line = [];
    private int _matched;
    private bool _control;
    private bool _exited;
    private bool _escape;

    /// <summary>Ob gerade Zeilen des Steuerprotokolls kommen.</summary>
    public bool InControl => _control;

    /// <summary>
    /// Nimmt ein Stück Ausgabe entgegen. <paramref name="passthrough"/> bekommt, was für das Terminal ist,
    /// <paramref name="line"/> jede vollständige Zeile des Steuerprotokolls (ohne Zeilenende),
    /// <paramref name="ended"/> wird gerufen, wenn tmux den Modus beendet hat.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, Action<byte[]> passthrough, Action<byte[]> line, Action ended)
    {
        var plain = new List<byte>();
        foreach (var value in data)
        {
            if (!_control)
            {
                // Auf die Einleitung warten; was ihr nur ähnelt, geht verspätet doch noch ans Terminal.
                if (value == Intro[_matched])
                {
                    _matched++;
                    if (_matched == Intro.Length)
                    {
                        (_matched, _control, _exited, _escape) = (0, true, false, false);
                        _line.Clear();
                    }

                    continue;
                }

                for (var held = 0; held < _matched; held++)
                {
                    plain.Add(Intro[held]);
                }

                _matched = value == Intro[0] ? 1 : 0;
                if (_matched == 0)
                {
                    plain.Add(value);
                }

                continue;
            }

            // Nach %exit folgt ESC \ – dann ist wieder das Terminal an der Reihe.
            if (_exited)
            {
                if (_escape && value == (byte)'\\')
                {
                    (_control, _exited, _escape) = (false, false, false);
                    ended();
                }
                else
                {
                    _escape = value == 0x1B;
                }

                continue;
            }

            if (value == (byte)'\n')
            {
                if (_line.Count > 0 && _line[^1] == (byte)'\r')
                {
                    _line.RemoveAt(_line.Count - 1);
                }

                var complete = _line.ToArray();
                _line.Clear();
                _exited = complete.AsSpan().StartsWith("%exit"u8) && (complete.Length == 5 || complete[5] == (byte)' ');
                line(complete);
            }
            else if (_line.Count < MaxLine)
            {
                _line.Add(value);
            }
        }

        if (plain.Count > 0)
        {
            passthrough([.. plain]);
        }
    }

    /// <summary>
    /// Die Daten hinter <c>%output %n</c>: tmux schreibt Steuerzeichen und den Rückstrich als <c>\ooo</c> (oktal),
    /// alles andere steht als das Byte da, das es ist.
    /// </summary>
    public static byte[] Unescape(ReadOnlySpan<byte> text)
    {
        var bytes = new List<byte>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == (byte)'\\'
                && index + 3 < text.Length
                && Octal(text[index + 1]) is >= 0 and <= 3 and var high
                && Octal(text[index + 2]) is >= 0 and var middle
                && Octal(text[index + 3]) is >= 0 and var low)
            {
                bytes.Add((byte)((high << 6) | (middle << 3) | low));
                index += 3;
            }
            else
            {
                bytes.Add(text[index]);
            }
        }

        return [.. bytes];

        static int Octal(byte digit) => digit is >= (byte)'0' and <= (byte)'7' ? digit - '0' : -1;
    }
}
