using System.Globalization;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core;

/// <summary>Anzeigeformate, die mehrere Ansichten teilen. Aus XAML per <c>{x:Bind core:Format.…}</c> aufrufbar.</summary>
public static partial class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Länge der Zeile mit dem Ziel eines Links, samt „→ “ (<see cref="LinkText"/>).</summary>
    public const int LinkTextLength = 120;

    /// <summary>Dateigröße in binären Einheiten; Ordner haben keine.</summary>
    public static string FileSize(long bytes, bool isDirectory)
    {
        if (isDirectory)
        {
            return string.Empty;
        }

        double value = Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = unit == 0 || value >= 100
            ? value.ToString("0", L.Culture)
            : value.ToString("0.0", L.Culture);
        return $"{number} {Units[unit]}";
    }

    /// <summary>Nur das Datum – für schmale Spalten. Leer, wenn der Server keine Zeit genannt hat.</summary>
    public static string ShortDate(DateTimeOffset time)
        => time == DateTimeOffset.MinValue ? string.Empty : time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateAndTime(DateTimeOffset time)
        => time == DateTimeOffset.MinValue ? string.Empty : time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Wann zuletzt verbunden wurde, als kurzes Datum – oder dass es noch nie war. Für die Tabelle der Startseite.</summary>
    public static string LastSeen(DateTimeOffset? lastConnected)
        => lastConnected is { } time ? ShortDate(time) : L.Get("NeverConnected");

    /// <summary>Dritte Zeile einer Server-Kachel: die Schlagworte – ohne die Adresse, die in der Zeile darüber steht.</summary>
    public static string ProfileExtras(IReadOnlyList<string> tags)
        => string.Join(", ", tags);

    /// <summary>Zweite Zeile eines Profils in der Serverliste: Adresse und Schlagworte.</summary>
    public static string ProfileSummary(string address, IReadOnlyList<string> tags)
        => tags.Count > 0 ? $"{address}  ·  {string.Join(", ", tags)}" : address;

    public static string ClockTime(DateTimeOffset time)
        => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Percent(double value)
        => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + (L.English ? "%" : " %");

    /// <summary>Anteil und, wenn bekannt, die Mengen dahinter: <c>42 % · 3,4 GB / 8,0 GB</c>.</summary>
    public static string Usage(double percent, long used, long total)
        => total > 0
            ? $"{Percent(percent)} · {FileSize(used, isDirectory: false)} / {FileSize(total, isDirectory: false)}"
            : Percent(percent);

    /// <summary>Eine Dauer in Worten, grob: <c>45 s</c>, <c>12 min</c>, <c>3 h 05 min</c>.</summary>
    public static string Duration(int seconds) => seconds switch
    {
        < 60 => $"{Math.Max(seconds, 0)} s",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60:00} min",
    };

    /// <summary>Durchsatz: <c>1,2 MB/s</c>.</summary>
    public static string Rate(long bytesPerSecond) => FileSize(bytesPerSecond, isDirectory: false) + "/s";

    /// <summary>Empfangen und gesendet in einer Zeile: <c>↓ 1,2 MB/s  ↑ 34 KB/s</c>.</summary>
    public static string Network(long received, long sent) => $"↓ {Rate(received)}  ↑ {Rate(sent)}";

    /// <summary>
    /// Empfangen und gesendet knapp, für die Zeile unten: beide in der Einheit des größeren, die Einheit einmal –
    /// <c>↓ 1,2 ↑ 0,3 MB/s</c>.
    /// </summary>
    public static string NetworkShort(long received, long sent)
    {
        var unit = UnitOf(Math.Max(received, sent));
        return $"↓ {Amount(received, unit, whole: false)} ↑ {Amount(sent, unit, whole: false)} {Units[unit]}/s";
    }

    /// <summary>
    /// Belegt von insgesamt, knapp, für die Zeile unten: beide in der Einheit der Summe, die Einheit einmal, eine runde
    /// Summe ohne Nachkommastelle – <c>2,1 / 8 GB</c>.
    /// </summary>
    public static string UsedOfTotal(long used, long total)
    {
        var unit = UnitOf(total);
        return $"{Amount(used, unit, whole: false)} / {Amount(total, unit, whole: true)} {Units[unit]}";
    }

    // Die Einheit, in der eine Menge unter 1024 bleibt.
    private static int UnitOf(long bytes)
    {
        double value = Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit;
    }

    // Eine Menge in der gegebenen Einheit: ganze Bytes und Werte ab 100 ohne Nachkommastelle, sonst mit einer –
    // mit `whole` auch einen Wert, der gerundet ganz ist (8,0 → 8).
    private static string Amount(long bytes, int unit, bool whole)
    {
        var value = Math.Max(bytes, 0) / Math.Pow(1024, unit);
        var plain = unit == 0 || value >= 100 || (whole && Math.Abs(value - Math.Round(value)) < 0.05);
        return value.ToString(plain ? "0" : "0.0", L.Culture);
    }

    /// <summary>Wie lange der Server läuft, als Satz für die Zeile unten: <c>läuft seit 12 Tagen</c>.</summary>
    public static string UptimeSince(TimeSpan uptime) => uptime.TotalDays switch
    {
        >= 2 => L.Get("UptimeSinceDays", (int)uptime.TotalDays),
        >= 1 => L.Get("UptimeSinceDay"),
        _ => L.Get("UptimeSinceHours", (int)uptime.TotalHours),
    };

    /// <summary>Die zwölf Bits der Zugriffsrechte (rwx für Besitzer, Gruppe, andere, dazu setuid, setgid, sticky).</summary>
    public const int PermissionBits = 0xFFF;

    /// <summary>Die Rechte als Zahl für <c>chmod</c>: <c>644</c>, <c>2775</c>.</summary>
    public static string Octal(int mode) => Convert.ToString(mode & PermissionBits, 8).PadLeft(3, '0');

    /// <summary>Liest drei oder vier Oktalziffern (<c>644</c>, <c>0755</c>, <c>2775</c>) als Zugriffsrechte.</summary>
    public static bool TryParseMode(string? text, out int mode)
    {
        mode = 0;
        var digits = text?.Trim() ?? string.Empty;
        if (digits.Length is < 3 or > 4 || digits.Any(digit => digit is < '0' or > '7'))
        {
            return false;
        }

        mode = Convert.ToInt32(digits, 8);
        return true;
    }

    /// <summary>
    /// Ein Text als ein einziges Argument für eine POSIX-Shell: in einfachen Anführungszeichen, ein enthaltenes
    /// <c>'</c> als <c>'\''</c>. <c>null</c>, wenn der Text Steuerzeichen enthält (Zeilenumbruch, Escape) – die
    /// ließen sich getippt nicht sicher übergeben.
    /// </summary>
    public static string? ShellQuote(string text)
        => text.Length == 0 || text.Any(char.IsControl) ? null : $"'{text.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>Nur die zehn Zeichen wie bei <c>ls -l</c> (<c>-rw-r--r--</c>) – für die Spalte der Dateiliste.</summary>
    public static string ModeText(int? mode, bool isDirectory, bool isLink)
        => Permissions(mode, isDirectory, isLink) is { Length: >= 10 } text ? text[..10] : string.Empty;

    /// <summary>
    /// Zugriffsrechte wie bei <c>ls -l</c> samt der Zahl für <c>chmod</c>: <c>-rw-r--r-- (644)</c>. Leer, wenn der
    /// Server keine nennt.
    /// </summary>
    public static string Permissions(int? mode, bool isDirectory, bool isLink)
    {
        if (mode is not { } bits)
        {
            return string.Empty;
        }

        Span<char> text = stackalloc char[10];
        text[0] = isLink ? 'l' : isDirectory ? 'd' : '-';
        for (var i = 0; i < 9; i++)
        {
            text[i + 1] = (bits & (0x100 >> i)) != 0 ? "rwx"[i % 3] : '-';
        }

        // setuid, setgid und Sticky-Bit stehen an der Stelle des jeweiligen x – groß geschrieben, wenn x fehlt.
        Special(text, 3, 0x800, 's');
        Special(text, 6, 0x400, 's');
        Special(text, 9, 0x200, 't');
        return $"{text} ({Octal(bits)})";

        void Special(Span<char> text, int position, int bit, char letter)
        {
            if ((bits & bit) != 0)
            {
                text[position] = text[position] == 'x' ? letter : char.ToUpperInvariant(letter);
            }
        }
    }

    /// <summary>
    /// Ein Name vom Server, wie er angezeigt wird. Steuerzeichen (Zeilenumbruch, Tabulator, Escape …) erscheinen
    /// als ihr Zeichenbild (␊, ␉, ␛), Zeichen, die die Schreibrichtung umkehren, als Ersatzzeichen: So bleibt eine
    /// Zeile eine Zeile, und ein Name kann sich nicht als ein anderer ausgeben. Nur für die Anzeige – Aktionen
    /// brauchen den echten Namen.
    /// </summary>
    public static string Visible(string name)
    {
        if (!name.Any(IsInvisible))
        {
            return name;
        }

        return string.Create(name.Length, name, static (text, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var character = source[i];
                text[i] = character switch
                {
                    _ when !IsInvisible(character) => character,
                    < Space => (char)(ControlPictures + character),
                    Delete => DeletePicture,
                    _ => Replacement,
                };
            }
        });
    }

    /// <summary>
    /// Ein ganzer Text vom Server (Datei, Ausgabe eines Befehls) für das Werkzeug eines MCP-Programms: Zeilen und
    /// Tabulatoren bleiben, Steuerfolgen fallen weg, alles andere wie bei <see cref="Visible"/>.
    /// </summary>
    public static string VisibleText(string text)
    {
        var plain = ControlSequence().Replace(text, string.Empty).ReplaceLineEndings("\n");
        return string.Join('\n', plain.Split('\n').Select(line => string.Join('\t', line.Split('\t').Select(Visible))));
    }

    /// <summary>Der Text ohne Steuerfolgen eines Terminals; alles andere bleibt, wie es ist.</summary>
    public static string WithoutControlSequences(string text) => ControlSequence().Replace(text, string.Empty);

    // CSI, OSC (bis BEL oder ST), Folgen mit Zwischenzeichen (Zeichensatz: ESC ( B) und die kurzen mit ESC.
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[ -/]+[0-~]|[@-Z\\^_=>78c])")]
    private static partial System.Text.RegularExpressions.Regex ControlSequence();

    /// <summary>
    /// Wohin ein Link zeigt, für die Zeile der Dateiliste: „→ ziel“. Das Ziel steht im Link und kommt vom Server,
    /// es geht deshalb wie ein Name durch <see cref="Visible"/>; höchstens <see cref="LinkTextLength"/> Zeichen
    /// samt Pfeil, danach folgt „…“. Leer, wenn die Liste kein Ziel nennt.
    /// </summary>
    public static string LinkText(RemoteEntry entry)
    {
        const string Arrow = "→ ";
        if (entry.LinkTarget is not { Length: > 0 } target)
        {
            return string.Empty;
        }

        if (Arrow.Length + target.Length <= LinkTextLength)
        {
            return Arrow + Visible(target);
        }

        // Erst kürzen, dann sichtbar machen (ein Ziel kann Kilobytes lang sein): bis zur Grenze, mit einer Stelle
        // für das „…“ – und nicht mitten in einem Ersatzpaar.
        var length = LinkTextLength - Arrow.Length - 1;
        if (char.IsHighSurrogate(target[length - 1]))
        {
            length--;
        }

        return Arrow + Visible(target[..length]) + "…";
    }

    /// <summary>
    /// Was die Dateiliste einem Screenreader vorliest (<c>RemoteEntry.ToString</c>): der angezeigte Name, bei einem
    /// Link mit bekanntem Ziel dazu „→ ziel“ (<see cref="LinkText"/>), bei einem, der ins Leere zeigt, außerdem der
    /// Hinweis darauf – sonst erführe es nur, wer das Symbol sieht. Ein Link ohne bekanntes Ziel nennt stattdessen seine Art (<c>TypeLink</c>).
    /// </summary>
    public static string Spoken(RemoteEntry entry)
    {
        if (!entry.IsLink)
        {
            return entry.DisplayName;
        }

        var parts = new List<string> { entry.DisplayName };
        if (LinkText(entry) is { Length: > 0 } target)
        {
            parts.Add(target);
        }
        else
        {
            // Ohne bekanntes Ziel klänge die Zeile sonst wie eine Datei: die Art dazusagen.
            parts.Add(L.Get("TypeLink"));
        }

        if (entry.IsDangling)
        {
            parts.Add(L.Get("LinkDangling"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Die Art eines Eintrags für die Spalte „Typ“: Link, Ordner, besondere Datei (Pipe, Gerät, Socket) oder Datei – mit
    /// ihrer Endung, wenn sie eine hat („ZIP-Datei“). Jeder symbolische Link ist ein Link, gleich worauf er zeigt (wie
    /// im Eigenschaften-Dialog), und wohin einer zeigt, sagt die Zeile. Beim Sortieren nach der Art stehen deshalb alle
    /// Links beieinander (<c>SftpBrowserViewModel</c> zählt dort einen Link auf einen Ordner nicht zu den Ordnern).
    /// </summary>
    public static string EntryType(RemoteEntry entry) => entry switch
    {
        { IsLink: true } => L.Get("TypeLink"),
        { IsSpecial: true } => L.Get("TypeSpecial"),
        { IsDirectory: true } => L.Get("TypeFolder"),
        _ => FileType(entry.Name),
    };

    /// <summary>Wie <see cref="EntryType(RemoteEntry)"/> für die lokale Liste; Laufwerke haben keine Art.</summary>
    public static string EntryType(LocalEntry entry) => entry switch
    {
        { IsDrive: true } => string.Empty,
        { IsDirectory: true } => L.Get("TypeFolder"),
        _ => FileType(entry.Name),
    };

    // Die Endung ist, was nach dem letzten Punkt steht – nicht bei „.bashrc“ (da ist der Punkt der Anfang des Namens),
    // und nur, wenn es ein kurzes Wort aus Buchstaben und Ziffern ist: Der Name kommt vom Server, und die Zeile soll
    // weder lang noch rätselhaft werden.
    private const int MaxEndingLength = 8;

    private static string FileType(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length - dot - 1 is 0 or > MaxEndingLength)
        {
            return L.Get("TypeFile");
        }

        var ending = name.AsSpan(dot + 1);
        foreach (var character in ending)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return L.Get("TypeFile");
            }
        }

        return L.Get("TypeFileWithExtension", ending.ToString().ToUpperInvariant());
    }

    /// <summary>
    /// Vergleicht zwei Namen so, wie man sie liest – genau wie der Kern (<c>sftp::natural</c>): ohne Rücksicht auf
    /// Groß- und Kleinschreibung, und Ziffernfolgen als Zahlen (<c>datei-2</c> vor <c>datei-10</c>, <c>a007</c> gleich
    /// <c>a7</c>). Zeichen werden als Unicode-Zeichen verglichen, nicht als Einheiten von UTF-16. Gleich heißt hier
    /// „gleich zu lesen“; für eine feste Reihenfolge gilt <see cref="NameOrder"/>.
    /// </summary>
    public static int CompareNames(string a, string b)
    {
        var (x, y) = (0, 0);
        while (true)
        {
            if (x >= a.Length || y >= b.Length)
            {
                // Wer zuerst zu Ende ist, steht vorn.
                return (x < a.Length).CompareTo(y < b.Length);
            }

            int order;
            if (IsDigit(a[x]) && IsDigit(b[y]))
            {
                order = CompareNumbers(a, ref x, b, ref y);
            }
            else
            {
                order = LowerCodePoint(NextCodePoint(a, ref x)).CompareTo(LowerCodePoint(NextCodePoint(b, ref y)));
            }

            if (order != 0)
            {
                return order;
            }
        }
    }

    /// <summary>
    /// Die Reihenfolge der Namen in beiden Dateilisten: <see cref="CompareNames"/>, bei Gleichstand der Name genau, Zeichen
    /// für Zeichen (<c>Datei-2</c> vor <c>datei-02</c>) – so sortiert auch der Kern, und die Liste steht damit fest.
    /// </summary>
    public static IComparer<string> NameOrder { get; } = Comparer<string>.Create(static (a, b)
        => CompareNames(a, b) is var order and not 0 ? order : CompareCodePoints(a, b));

    private static int CompareCodePoints(string a, string b)
    {
        var (x, y) = (0, 0);
        while (x < a.Length && y < b.Length)
        {
            var order = NextCodePoint(a, ref x).CompareTo(NextCodePoint(b, ref y));
            if (order != 0)
            {
                return order;
            }
        }

        return (x < a.Length).CompareTo(y < b.Length);
    }

    private static bool IsDigit(char character) => character is >= '0' and <= '9';

    // Zwei Ziffernfolgen ab x und y als Zahlen, ohne führende Nullen: die längere ist die größere, sonst entscheidet
    // der Vergleich Ziffer für Ziffer. Danach stehen x und y hinter den Folgen.
    private static int CompareNumbers(string a, ref int x, string b, ref int y)
    {
        var (startA, startB) = (x, y);
        while (x < a.Length && IsDigit(a[x]))
        {
            x++;
        }

        while (y < b.Length && IsDigit(b[y]))
        {
            y++;
        }

        var left = a.AsSpan(startA, x - startA).TrimStart('0');
        var right = b.AsSpan(startB, y - startB).TrimStart('0');
        return left.Length != right.Length ? left.Length.CompareTo(right.Length) : Math.Sign(left.SequenceCompareTo(right));
    }

    // Das Unicode-Zeichen an der Stelle (ein Paar aus zwei Einheiten ist eines); eine einzelne Hälfte, wie sie ein
    // Dateiname unter Windows haben kann, gilt als sie selbst.
    private static int NextCodePoint(string text, ref int index)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            index += 2;
            return char.ConvertToUtf32(text[index - 2], text[index - 1]);
        }

        return text[index++];
    }

    private static int LowerCodePoint(int codePoint)
        => codePoint is >= 0xD800 and <= 0xDFFF ? codePoint
            : codePoint < 0x10000 ? char.ToLowerInvariant((char)codePoint)
            : System.Text.Rune.ToLowerInvariant(new System.Text.Rune(codePoint)).Value;

    // Als Zahlen statt als Zeichen: Im Quelltext wären die meisten davon selbst unsichtbar.
    private const char Space = (char)0x20;
    private const char Delete = (char)0x7F;
    private const char ControlPictures = (char)0x2400;
    private const char DeletePicture = (char)0x2421;
    private const char Replacement = (char)0xFFFD;

    // Steuerzeichen, Zeilen- und Absatztrenner (U+2028, U+2029) und die Zeichen, die die Schreibrichtung festlegen
    // oder umkehren (U+200E, U+200F, U+202A–U+202E, U+2066–U+2069).
    internal static bool IsInvisible(char character)
        => char.IsControl(character)
            || (int)character is 0x2028 or 0x2029 or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    public static string Uptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? L.Get("UptimeDays", (int)uptime.TotalDays)
            : L.Get("UptimeHours", (int)uptime.TotalHours);
}
