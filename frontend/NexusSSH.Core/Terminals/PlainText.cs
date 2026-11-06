namespace NexusSSH.Core.Terminals;

/// <summary>
/// Macht aus der Ausgabe eines Terminals lesbaren Text für eine Mitschrift: ohne Escape-Folgen (Farben,
/// Cursorbewegungen, Fenstertitel) und ohne Steuerzeichen außer Zeilenumbruch und Tabulator – auch ohne die
/// Steuerzeichen, die als UTF-8 kommen (C1), und ohne Zeichen, die die Schreibrichtung umkehren oder eine Zeile
/// vortäuschen. Ein Block kann mitten in einer Folge enden – der Zustand bleibt deshalb zwischen den Aufrufen
/// erhalten. Übrige Bytes ab 0x80 (UTF-8) gehen unverändert durch.
///
/// Die Folgen enden hier, wo sie auch für das Terminal enden: Der Server soll die Mitschrift weder blind machen
/// (eine Zeichenkette, die das Terminal längst abgebrochen hat) noch etwas hineinschreiben können, das am
/// Bildschirm nie stand.
/// </summary>
public sealed class PlainText
{
    private enum State
    {
        Text,
        Escape,
        Csi,

        // OSC endet mit BEL oder ESC \; DCS, SOS, PM und APC nur mit ESC \.
        Osc,
        String,
        OscEscape,
        StringEscape,
    }

    private const byte Esc = 0x1B;
    private const byte Can = 0x18;
    private const byte Sub = 0x1A;

    private State _state;

    // Der Anfang eines UTF-8-Zeichens, über das erst das nächste Byte entscheidet (0xC2 oder 0xE2 …).
    private readonly byte[] _held = new byte[2];
    private int _heldCount;

    /// <summary>Hängt den lesbaren Teil von <paramref name="data"/> an <paramref name="target"/> an.</summary>
    public void Append(ReadOnlySpan<byte> data, Stream target)
    {
        Span<byte> buffer = stackalloc byte[256];
        var used = 0;

        void Emit(byte value, Span<byte> buffer)
        {
            buffer[used++] = value;
            if (used == buffer.Length)
            {
                target.Write(buffer);
                used = 0;
            }
        }

        foreach (var value in data)
        {
            switch (_state)
            {
                case State.Text:
                    Text(value, buffer);
                    break;

                case State.Escape:
                    _state = AfterEscape(value);
                    break;

                // Parameter und Zwischenzeichen, dann ein Schlusszeichen zwischen @ und ~. CAN und SUB brechen ab,
                // ein neues ESC beginnt von vorn.
                case State.Csi:
                    _state = value switch
                    {
                        Esc => State.Escape,
                        Can or Sub => State.Text,
                        >= 0x40 and <= 0x7E => State.Text,
                        _ => State.Csi,
                    };
                    break;

                case State.Osc:
                    _state = value switch
                    {
                        0x07 or Can or Sub => State.Text,
                        Esc => State.OscEscape,
                        _ => State.Osc,
                    };
                    break;

                // Hier beendet BEL nichts: Was danach käme, zeigte das Terminal nicht.
                case State.String:
                    _state = value switch
                    {
                        Can or Sub => State.Text,
                        Esc => State.StringEscape,
                        _ => State.String,
                    };
                    break;

                // ESC \ ist das Ende; jedes andere Zeichen nach ESC beginnt eine neue Folge – die Zeichenkette ist
                // damit abgebrochen.
                case State.OscEscape or State.StringEscape:
                    _state = value == (byte)'\\' ? State.Text : AfterEscape(value);
                    break;
            }
        }

        target.Write(buffer[..used]);

        void Text(byte value, Span<byte> buffer)
        {
            if (_heldCount > 0)
            {
                Held(value, buffer);
                return;
            }

            switch (value)
            {
                case Esc:
                    _state = State.Escape;
                    break;

                // Über diese beiden entscheidet erst, was folgt.
                case 0xC2 or 0xE2:
                    _held[0] = value;
                    _heldCount = 1;
                    break;

                case (byte)'\n' or (byte)'\t' or >= 0x20 and not 0x7F:
                    Emit(value, buffer);
                    break;

                    // Übrige Steuerzeichen (Wagenrücklauf, Glocke, Rückschritt) fallen weg.
            }
        }

        // Ein angefangenes Zeichen: C1-Steuerzeichen (C2 80–9F), Zeilen- und Absatztrenner und die Zeichen, die
        // die Schreibrichtung umkehren (E2 80 A8–AE, E2 81 A6–A9), fallen weg; alles andere geht durch.
        void Held(byte value, Span<byte> buffer)
        {
            if (_held[0] == 0xC2)
            {
                _heldCount = 0;
                if (value is >= 0x80 and <= 0x9F)
                {
                    return;
                }

                Emit(0xC2, buffer);
                Text(value, buffer);
                return;
            }

            if (_heldCount == 1)
            {
                if (value is 0x80 or 0x81)
                {
                    _held[1] = value;
                    _heldCount = 2;
                    return;
                }

                _heldCount = 0;
                Emit(0xE2, buffer);
                Text(value, buffer);
                return;
            }

            var second = _held[1];
            _heldCount = 0;
            if ((second == 0x80 && value is >= 0xA8 and <= 0xAE) || (second == 0x81 && value is >= 0xA6 and <= 0xA9))
            {
                return;
            }

            Emit(0xE2, buffer);
            Emit(second, buffer);
            Text(value, buffer);
        }
    }

    // Was auf ESC folgt, entscheidet über die Art der Folge.
    private static State AfterEscape(byte value) => value switch
    {
        (byte)'[' => State.Csi,
        (byte)']' => State.Osc,

        // DCS, SOS, PM, APC: Zeichenketten bis ESC \.
        (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.String,

        // Zwischenzeichen (ESC ( B wählt einen Zeichensatz): Das Schlusszeichen kommt noch.
        >= 0x20 and <= 0x2F => State.Escape,
        Esc => State.Escape,
        _ => State.Text,
    };
}
