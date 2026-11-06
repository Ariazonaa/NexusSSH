using System.Globalization;
using System.Text;

namespace NexusSSH.Core.Terminals;

/// <summary>Ein Stück kopierten Terminal-Texts mit gleicher Farbe und Schrift.</summary>
/// <param name="Foreground"><c>#rrggbb</c>, oder <c>null</c> für die Schriftfarbe des Farbschemas.</param>
/// <param name="Background"><c>#rrggbb</c>, oder <c>null</c> für den Hintergrund des Farbschemas.</param>
public sealed record CopyRun(string Text, string? Foreground, string? Background, bool Bold, bool Italic, bool Underline);

/// <summary>
/// „Mit Formatierung kopieren“: macht aus den Stücken, die die Terminal-Seite zu einer Auswahl nennt, HTML und RTF
/// für die Zwischenablage. Die Seite liefert nur Text und Farben – die Formatierung entsteht hier, aus geprüften
/// Werten: Der Text stammt vom Server, und was in der Zwischenablage liegt, landet in anderen Programmen.
/// </summary>
public static class RichCopy
{
    /// <summary>Mehr als das wird nicht formatiert kopiert.</summary>
    public const int MaxLength = 1024 * 1024;

    public const int MaxRuns = 20_000;

    /// <summary>Der reine Text der Stücke.</summary>
    public static string Plain(IReadOnlyList<CopyRun> runs) => string.Concat(runs.Select(run => run.Text));

    /// <summary>
    /// Die Auswahl als HTML-Fragment: ein Block in der Schrift und den Farben des Terminals, darin je Stück ein
    /// <c>span</c>. Leerraum bleibt, wie er ist.
    /// </summary>
    public static string Html(IReadOnlyList<CopyRun> runs, string font, int fontSize, string foreground, string background)
    {
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:'").Append(CssName(font)).Append("',Consolas,monospace;font-size:")
            .Append(fontSize.ToString(CultureInfo.InvariantCulture)).Append("px;white-space:pre;color:").Append(foreground)
            .Append(";background-color:").Append(background).Append(";padding:4px\">");
        foreach (var run in runs)
        {
            var style = new StringBuilder();
            if (run.Foreground is not null)
            {
                style.Append("color:").Append(run.Foreground).Append(';');
            }

            if (run.Background is not null)
            {
                style.Append("background-color:").Append(run.Background).Append(';');
            }

            if (run.Bold)
            {
                style.Append("font-weight:bold;");
            }

            if (run.Italic)
            {
                style.Append("font-style:italic;");
            }

            if (run.Underline)
            {
                style.Append("text-decoration:underline;");
            }

            var text = Escape(run.Text);
            if (style.Length > 0)
            {
                html.Append("<span style=\"").Append(style).Append("\">").Append(text).Append("</span>");
            }
            else
            {
                html.Append(text);
            }
        }

        return html.Append("</div>").ToString();
    }

    /// <summary>
    /// Ein HTML-Fragment so, wie es unter Windows in der Zwischenablage liegt („HTML Format“): UTF-8 mit einem
    /// Kopf, der in Bytes nennt, wo das Dokument und das Fragment darin beginnen und enden.
    /// </summary>
    public static byte[] ClipboardHtml(string fragment)
    {
        const string Before = "<html><body><!--StartFragment-->";
        const string After = "<!--EndFragment--></body></html>";

        // Der Kopf ist gleich lang, welche Zahlen auch darin stehen – so lassen sich die Stellen vorab nennen.
        static string Head(int startHtml, int endHtml, int startFragment, int endFragment) => string.Create(
            CultureInfo.InvariantCulture,
            $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n");

        var head = Head(0, 0, 0, 0).Length;
        var startFragment = head + Before.Length;
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(Head(head, endFragment + After.Length, startFragment, endFragment) + Before + fragment + After);
    }

    /// <summary>Die Auswahl als RTF, mit Farbtabelle; Zeichen außerhalb von ASCII als <c>\u</c>.</summary>
    public static string Rtf(IReadOnlyList<CopyRun> runs, string font, int fontSize, string foreground, string background)
    {
        // Farbe 1 ist die Schrift, 2 der Hintergrund des Terminals; die übrigen kommen der Reihe nach dazu.
        var colors = new List<string> { foreground, background };
        int Color(string? color, int fallback)
        {
            if (color is null)
            {
                return fallback;
            }

            var index = colors.IndexOf(color);
            if (index < 0)
            {
                colors.Add(color);
                index = colors.Count - 1;
            }

            return index + 1;
        }

        var body = new StringBuilder();
        foreach (var run in runs)
        {
            body.Append("{\\cf").Append(Color(run.Foreground, 1).ToString(CultureInfo.InvariantCulture))
                .Append("\\chshdng0\\chcbpat").Append(Color(run.Background, 2).ToString(CultureInfo.InvariantCulture))
                .Append("\\cb").Append(Color(run.Background, 2).ToString(CultureInfo.InvariantCulture));
            if (run.Bold)
            {
                body.Append("\\b");
            }

            if (run.Italic)
            {
                body.Append("\\i");
            }

            if (run.Underline)
            {
                body.Append("\\ul");
            }

            body.Append(' ');
            foreach (var character in run.Text)
            {
                switch (character)
                {
                    case '\\' or '{' or '}':
                        body.Append('\\').Append(character);
                        break;
                    case '\n':
                        body.Append("\\line ");
                        break;
                    case '\t':
                        body.Append("\\tab ");
                        break;
                    case < (char)0x80:
                        body.Append(character);
                        break;
                    default:
                        // RTF kennt hier nur 16 Bit mit Vorzeichen; ein Zeichen außerhalb davon kommt als zwei Hälften.
                        body.Append("\\u").Append(((short)character).ToString(CultureInfo.InvariantCulture)).Append('?');
                        break;
                }
            }

            body.Append('}');
        }

        var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fmodern ");
        rtf.Append(RtfName(font)).Append(";}}{\\colortbl;");
        foreach (var color in colors)
        {
            rtf.Append("\\red").Append(Convert.ToInt32(color.Substring(1, 2), 16).ToString(CultureInfo.InvariantCulture))
                .Append("\\green").Append(Convert.ToInt32(color.Substring(3, 2), 16).ToString(CultureInfo.InvariantCulture))
                .Append("\\blue").Append(Convert.ToInt32(color.Substring(5, 2), 16).ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        // \fs zählt halbe Punkt; die Schriftgröße des Terminals ist in Pixeln, drei Viertel davon sind Punkt.
        var halfPoints = Math.Clamp((int)Math.Round(fontSize * 1.5), 12, 96);
        return rtf.Append("}\\f0\\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(body).Append('}').ToString();
    }

    /// <summary>
    /// Prüft, was die Seite schickt: Farben nur als <c>#rrggbb</c> (sonst die des Farbschemas), im Text nur
    /// Druckbares, Tabulator und Zeilenumbruch, und nicht mehr als die Obergrenzen. <c>null</c>, wenn nichts
    /// Brauchbares übrig bleibt.
    /// </summary>
    public static IReadOnlyList<CopyRun>? Clean(IEnumerable<(string? Text, string? Foreground, string? Background, int Style)>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var runs = new List<CopyRun>();
        var length = 0;
        foreach (var (text, foreground, background, style) in raw.Take(MaxRuns))
        {
            if (text is not { Length: > 0 } || length >= MaxLength)
            {
                continue;
            }

            var clean = Printable(text.Length > MaxLength - length ? text[..(MaxLength - length)] : text);
            if (clean.Length == 0)
            {
                continue;
            }

            length += clean.Length;
            runs.Add(new CopyRun(clean, Color(foreground), Color(background), (style & 1) != 0, (style & 2) != 0, (style & 4) != 0));
        }

        return runs.Count > 0 ? runs : null;
    }

    /// <summary><c>#rrggbb</c> in Kleinbuchstaben, oder <c>null</c>, wenn es keine solche Farbe ist.</summary>
    public static string? Color(string? value)
        => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? value.ToLowerInvariant() : null;

    /// <summary>Text ohne Steuerzeichen (außer Tabulator und Zeilenumbruch) und ohne Zeichen, die die Schreibrichtung umkehren.</summary>
    public static string Printable(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\n' or '\t' || (!char.IsControl(character) && character is not (>= '‪' and <= '‮') and not (>= '⁦' and <= '⁩')))
            {
                clean.Append(character);
            }
        }

        return clean.ToString();
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    // Der Name einer Schrift für CSS und RTF: nur Buchstaben, Ziffern, Leerzeichen und Bindestrich.
    private static string CssName(string font) => new([.. font.Where(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-').Take(64)]);

    private static string RtfName(string font) => CssName(font) is { Length: > 0 } name ? name : "Consolas";
}
