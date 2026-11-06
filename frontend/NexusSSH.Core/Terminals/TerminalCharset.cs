using System.Text;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Der Zeichensatz, in dem ein Server spricht, wenn nicht in UTF-8: je Byte ein Zeichen. Die Terminal-Seite kennt
/// nur UTF-8, also wird hier umgerechnet – was der Server schickt nach UTF-8, was getippt wird zurück. An einer
/// Stelle für jede Art von Verbindung (<c>SessionViewModel</c>); der Kern merkt sich die Wahl im Profil bloß.
/// Die Tabellen stehen hier und kommen nicht aus dem System: So gilt auf jedem Rechner dasselbe, auch unter AOT.
/// </summary>
public static class TerminalCharset
{
    public const string Latin1 = "latin1";
    public const string Latin9 = "latin9";
    public const string Windows1252 = "windows1252";

    /// <summary>Die Auswahl in der Reihenfolge, in der der Dialog sie zeigt; <c>null</c> = UTF-8.</summary>
    public static readonly string?[] Ids = [null, Latin1, Latin9, Windows1252];

    private static readonly char[] Latin1Table = Build([]);

    // ISO-8859-15 ist Latin-1 mit acht anderen Zeichen (Euro, Š, Œ …).
    private static readonly char[] Latin9Table = Build(new Dictionary<int, char>
    {
        [0xA4] = '€',
        [0xA6] = 'Š',
        [0xA8] = 'š',
        [0xB4] = 'Ž',
        [0xB8] = 'ž',
        [0xBC] = 'Œ',
        [0xBD] = 'œ',
        [0xBE] = 'Ÿ',
    });

    // Windows-1252 belegt den Bereich der C1-Steuerzeichen mit Schriftzeichen; fünf Plätze bleiben frei.
    private static readonly char[] Windows1252Table = Build(new Dictionary<int, char>
    {
        [0x80] = '€',
        [0x82] = '‚',
        [0x83] = 'ƒ',
        [0x84] = '„',
        [0x85] = '…',
        [0x86] = '†',
        [0x87] = '‡',
        [0x88] = 'ˆ',
        [0x89] = '‰',
        [0x8A] = 'Š',
        [0x8B] = '‹',
        [0x8C] = 'Œ',
        [0x8E] = 'Ž',
        [0x91] = '‘',
        [0x92] = '’',
        [0x93] = '“',
        [0x94] = '”',
        [0x95] = '•',
        [0x96] = '–',
        [0x97] = '—',
        [0x98] = '˜',
        [0x99] = '™',
        [0x9A] = 'š',
        [0x9B] = '›',
        [0x9C] = 'œ',
        [0x9E] = 'ž',
        [0x9F] = 'Ÿ',
    });

    /// <summary>Ob das einer der Zeichensätze ist, die hier umgerechnet werden.</summary>
    public static bool IsKnown(string? id) => id is Latin1 or Latin9 or Windows1252;

    /// <summary>Rechnet um, was der Server geschickt hat: jedes Byte ein Zeichen, heraus kommt UTF-8.</summary>
    public static byte[] Decode(string id, ReadOnlySpan<byte> data)
    {
        var table = TableOf(id);
        var text = new char[data.Length];
        for (var index = 0; index < data.Length; index++)
        {
            text[index] = table[data[index]];
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private static char[] TableOf(string id) => id switch
    {
        Latin9 => Latin9Table,
        Windows1252 => Windows1252Table,
        _ => Latin1Table,
    };

    private static char[] Build(Dictionary<int, char> different)
    {
        var table = new char[256];
        for (var value = 0; value < table.Length; value++)
        {
            table[value] = different.TryGetValue(value, out var other) ? other : (char)value;
        }

        return table;
    }

    /// <summary>
    /// Rechnet um, was getippt oder eingefügt wird: UTF-8 hinein, je Zeichen ein Byte heraus. Ein Zeichen, das es im
    /// Zeichensatz nicht gibt, wird ein Fragezeichen. Merkt sich ein angefangenes Zeichen bis zum nächsten Stück –
    /// Eingefügtes kommt in Teilen, und die Grenze kann mitten in einem Zeichen liegen.
    /// </summary>
    public sealed class Input
    {
        private readonly Dictionary<char, byte> _bytes = [];
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly Lock _gate = new();

        public Input(string id)
        {
            var table = TableOf(id);

            // Rückwärts, damit bei doppelten Zeichen der kleinere Wert gilt (es gibt keine, aber so ist es eindeutig).
            for (var value = table.Length - 1; value >= 0; value--)
            {
                _bytes[table[value]] = (byte)value;
            }
        }

        public byte[] Encode(ReadOnlySpan<byte> utf8)
        {
            lock (_gate)
            {
                var text = new char[_decoder.GetCharCount(utf8, flush: false)];
                var count = _decoder.GetChars(utf8, text, flush: false);
                var result = new List<byte>(count);
                foreach (var c in text.AsSpan(0, count))
                {
                    // Ein Zeichen außerhalb der Grundebene sind zwei Hälften – zusammen ein Fragezeichen.
                    if (char.IsLowSurrogate(c))
                    {
                        continue;
                    }

                    result.Add(_bytes.TryGetValue(c, out var value) ? value : (byte)'?');
                }

                return [.. result];
            }
        }
    }
}
