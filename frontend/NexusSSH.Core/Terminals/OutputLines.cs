using System.Text;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Zerlegt die Ausgabe eines Terminals in Zeilen lesbaren Texts (über <see cref="PlainText"/>, wie die Mitschrift) –
/// für Plugins, die Zeilen auf Muster prüfen. Eine Zeile ist fertig mit ihrem Zeilenumbruch; was darüber hinaus
/// keinen findet, wird nach <see cref="MaxLine"/> Bytes abgeschnitten. Nicht für mehrere Fäden zugleich.
/// </summary>
public sealed class OutputLines
{
    /// <summary>Länger wird eine Zeile nicht (in Bytes); der Rest bis zum Zeilenumbruch fällt weg.</summary>
    public const int MaxLine = 2000;

    private readonly PlainText _plain = new();
    private readonly MemoryStream _text = new();
    private readonly byte[] _line = new byte[MaxLine];
    private int _length;

    /// <summary>Nimmt einen Block der Ausgabe; jede fertige, nicht leere Zeile geht an <paramref name="line"/>.</summary>
    public void Append(ReadOnlySpan<byte> data, Action<string> line)
    {
        _text.SetLength(0);
        _plain.Append(data, _text);
        foreach (var value in _text.GetBuffer().AsSpan(0, (int)_text.Length))
        {
            if (value == (byte)'\n')
            {
                var text = Encoding.UTF8.GetString(_line, 0, _length).Trim();
                _length = 0;
                if (text.Length > 0)
                {
                    line(text);
                }
            }
            else if (value != (byte)'\r' && _length < MaxLine)
            {
                _line[_length++] = value;
            }
        }
    }
}
