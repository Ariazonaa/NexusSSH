namespace NexusSSH.Core.Services;

/// <summary>Wie eine Datei eingefärbt wird – nach ihrem Namen.</summary>
public enum SyntaxKind
{
    Plain,

    /// <summary>Konfiguration und Skripte mit <c>#</c>-Kommentaren: <c>.conf</c>, <c>.ini</c>, <c>.sh</c>, <c>.yml</c>, <c>.toml</c>, <c>.py</c> …</summary>
    Hash,

    /// <summary>Sprachen mit <c>//</c>-Kommentaren: <c>.js</c>, <c>.c</c>, <c>.cs</c>, <c>.go</c>, <c>.rs</c>, <c>.css</c> …</summary>
    Slash,
    Json,

    /// <summary><c>.xml</c>, <c>.html</c>, <c>.svg</c>: Tags.</summary>
    Markup,
}

/// <summary>Was ein Stück einer Zeile ist.</summary>
public enum SyntaxToken
{
    Text,
    Comment,
    String,
    Number,

    /// <summary>Der Name vor <c>=</c> oder <c>:</c>.</summary>
    Key,

    /// <summary><c>[abschnitt]</c> einer INI-Datei.</summary>
    Section,
    Tag,
}

/// <summary>Ein Stück einer Zeile mit seiner Art.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxToken Token);

/// <summary>
/// Farben für die Vorschau: zerlegt eine Zeile in Kommentar, Zeichenkette, Zahl, Schlüssel, Abschnitt und Tag.
/// Bewusst grob – Zeile für Zeile, ohne Zustand über Zeilen hinweg und ohne eine Sprache wirklich zu lesen: Es
/// soll eine Konfigurationsdatei lesbarer machen, nicht übersetzen. Eine Zeile wird nie verändert, nur eingeteilt;
/// die Stücke decken sie lückenlos ab.
/// </summary>
public static class SyntaxColors
{
    /// <summary>Längere Zeilen bleiben ungefärbt – dort lohnt es nicht und kostet nur.</summary>
    public const int MaxLineLength = 2000;

    public static SyntaxKind KindOf(string fileName)
    {
        var name = Path.GetFileName(fileName).ToLowerInvariant();
        return Path.GetExtension(name) switch
        {
            ".json" or ".jsonc" => SyntaxKind.Json,
            ".xml" or ".html" or ".htm" or ".svg" or ".xhtml" or ".plist" => SyntaxKind.Markup,
            ".js" or ".ts" or ".jsx" or ".tsx" or ".c" or ".h" or ".cpp" or ".hpp" or ".cs" or ".java" or ".go" or ".rs" or ".css" or ".scss" or ".php" or ".swift" or ".kt" => SyntaxKind.Slash,
            ".conf" or ".cfg" or ".cnf" or ".ini" or ".toml" or ".yml" or ".yaml" or ".sh" or ".bash" or ".zsh" or ".fish" or ".py" or ".rb" or ".pl" or ".env"
                or ".service" or ".timer" or ".socket" or ".mount" or ".properties" or ".list" or ".rules" or ".tf" => SyntaxKind.Hash,
            ".txt" or ".log" or ".md" or ".csv" => SyntaxKind.Plain,
            // Ohne Endung oder mit einer unbekannten: Unter /etc ist das meist Konfiguration mit #-Kommentaren.
            _ => name is "dockerfile" or "makefile" or "crontab" or "fstab" or "hosts" or "passwd" or "group" or "sudoers" || !name.Contains('.') || name.StartsWith('.') ? SyntaxKind.Hash : SyntaxKind.Plain,
        };
    }

    public static IReadOnlyList<SyntaxSpan> Spans(string line, SyntaxKind kind)
    {
        if (kind == SyntaxKind.Plain || line.Length == 0 || line.Length > MaxLineLength)
        {
            return [new SyntaxSpan(0, line.Length, SyntaxToken.Text)];
        }

        var spans = new List<SyntaxSpan>();
        var text = 0;
        void Take(int start, int end, SyntaxToken token)
        {
            if (start > text)
            {
                spans.Add(new SyntaxSpan(text, start - text, SyntaxToken.Text));
            }

            spans.Add(new SyntaxSpan(start, end - start, token));
            text = end;
        }

        var first = FirstNonSpace(line);
        var index = first;

        // Ein Abschnitt „[name]“ und der Schlüssel am Zeilenanfang.
        if (kind == SyntaxKind.Hash && index < line.Length && line[index] == '[' && line.TrimEnd().EndsWith(']'))
        {
            Take(index, line.TrimEnd().Length, SyntaxToken.Section);
            index = text;
        }
        else if (kind == SyntaxKind.Hash && KeyEnd(line, index) is { } keyEnd)
        {
            Take(index, keyEnd, SyntaxToken.Key);
            index = keyEnd;
        }

        while (index < line.Length)
        {
            var character = line[index];
            if (kind == SyntaxKind.Markup)
            {
                if (line.AsSpan(index).StartsWith("<!--"))
                {
                    var close = line.IndexOf("-->", index + 4, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 3;
                    Take(index, end, SyntaxToken.Comment);
                    index = end;
                    continue;
                }

                if (character == '<')
                {
                    // Der Name des Tags; Attribute und ihre Werte folgen als Text und Zeichenketten.
                    var end = index + 1;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '/' or '!' or '?' or ':' or '-' or '_'))
                    {
                        end++;
                    }

                    Take(index, end, SyntaxToken.Tag);
                    index = end;
                    continue;
                }

                if (character is '>' || (character == '/' && index + 1 < line.Length && line[index + 1] == '>'))
                {
                    var end = character == '>' ? index + 1 : index + 2;
                    Take(index, end, SyntaxToken.Tag);
                    index = end;
                    continue;
                }
            }

            if ((kind == SyntaxKind.Hash && (character == '#' || (character == ';' && index == first)) && (index == 0 || char.IsWhiteSpace(line[index - 1]) || index == first))
                || (kind is SyntaxKind.Slash or SyntaxKind.Json && character == '/' && index + 1 < line.Length && line[index + 1] == '/'))
            {
                Take(index, line.Length, SyntaxToken.Comment);
                index = line.Length;
                break;
            }

            if (character is '"' or '\'' && !(kind == SyntaxKind.Json && character == '\''))
            {
                var end = StringEnd(line, index);

                // In JSON ist eine Zeichenkette vor einem Doppelpunkt ein Schlüssel.
                var token = kind == SyntaxKind.Json && NextIs(line, end, ':') ? SyntaxToken.Key : SyntaxToken.String;
                Take(index, end, token);
                index = end;
                continue;
            }

            if (char.IsAsciiDigit(character) && (index == 0 || !IsWord(line[index - 1])))
            {
                var end = index + 1;
                while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] is '.' or '_'))
                {
                    end++;
                }

                // „8080“ und „1.5“ sind Zahlen, „1st“ oder „10.0.0.1“ sind keine – die bleiben Text.
                if (decimal.TryParse(line.AsSpan(index, end - index), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out _)
                    || IsHex(line.AsSpan(index, end - index)))
                {
                    Take(index, end, SyntaxToken.Number);
                }

                index = end;
                continue;
            }

            index++;
        }

        if (text < line.Length)
        {
            spans.Add(new SyntaxSpan(text, line.Length - text, SyntaxToken.Text));
        }

        return spans;
    }

    private static int FirstNonSpace(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        return index;
    }

    // Das Ende eines Schlüssels am Zeilenanfang: ein Wort (auch mit Punkt und Bindestrich, in YAML mit „- “ davor),
    // dem „=“ oder „:“ folgt. `null`, wenn die Zeile nicht so beginnt.
    private static int? KeyEnd(string line, int start)
    {
        var index = start;
        if (line.AsSpan(index).StartsWith("- "))
        {
            index += 2;
        }

        var begin = index;
        while (index < line.Length && (IsWord(line[index]) || line[index] is '.' or '-' or '/'))
        {
            index++;
        }

        if (index == begin)
        {
            return null;
        }

        var after = index;
        while (after < line.Length && line[after] == ' ')
        {
            after++;
        }

        // „http://…“ ist kein Schlüssel: Nach dem Doppelpunkt muss Leerraum oder das Ende der Zeile kommen.
        return after < line.Length && (line[after] == '=' || (line[after] == ':' && (after + 1 == line.Length || line[after + 1] == ' '))) ? index : null;
    }

    private static int StringEnd(string line, int start)
    {
        var quote = line[start];
        for (var index = start + 1; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                index++;
            }
            else if (line[index] == quote)
            {
                return index + 1;
            }
        }

        return line.Length;
    }

    private static bool NextIs(string line, int index, char expected)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        return index < line.Length && line[index] == expected;
    }

    private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static bool IsHex(ReadOnlySpan<char> text)
        => text.Length > 2 && text[0] == '0' && text[1] is 'x' or 'X' && !text[2..].ContainsAnyExcept("0123456789abcdefABCDEF");
}
