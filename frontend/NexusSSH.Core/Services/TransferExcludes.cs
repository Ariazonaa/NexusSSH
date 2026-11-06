using System.Text;

namespace NexusSSH.Core.Services;

/// <summary>
/// Etwas, das ein Ordner-Transfer auslassen würde: der oberste Eintrag, der auf ein Muster passt. Passt er auf
/// mehrere, steht er unter jedem – mit geht er erst, wenn keines davon mehr gilt.
/// </summary>
/// <param name="Pattern">Das Muster, wie der Benutzer es geschrieben hat.</param>
/// <param name="Path">Pfad unterhalb des übertragenen Ordners, mit <c>/</c>.</param>
public sealed record ExcludedItem(string Pattern, string Path, bool IsDirectory);

/// <summary>Was ein Muster in den gewählten Ordnern trifft – eine Zeile der Rückfrage vor dem Transfer.</summary>
/// <param name="Example">Einer der Treffer, zum Zeigen.</param>
public sealed record ExcludedGroup(string Pattern, int Folders, int Files, string Example)
{
    public override string ToString() => $"{Pattern}, {Folders + Files}";
}

/// <summary>
/// Ausschlüsse: Muster, die ein Ordner-Transfer auslässt (<c>.git</c>, <c>node_modules</c>, <c>*.tmp</c>). Dieselbe
/// Regel steht im Kern (<c>excludes.rs</c>) – er lässt beim Übertragen aus, hier wird vorab gezeigt, was fehlen
/// würde, und beim Abgleich verglichen. Beide müssen dasselbe sagen (dieselbe Tabelle in beiden Tests).
/// <para>
/// Ein Muster ohne <c>/</c> meint den Namen einer Datei oder eines Ordners in jeder Tiefe; eines mit <c>/</c> den
/// Pfad unterhalb des übertragenen Ordners, Stück für Stück; mit <c>/</c> vorn gilt auch ein einzelner Name nur
/// ganz oben. <c>*</c> steht für beliebig viele Zeichen, <c>?</c> für eines.
/// </para>
/// <para>
/// Verglichen wird Zeichen für Zeichen (Unicode-Zeichen, nicht UTF-16-Einheiten), und Groß- und Kleinschreibung
/// zählt nur bei A–Z nicht: Jede weitergehende Regel hängt von der Laufzeit ab, und dann sagten Kern und App bei
/// manchen Namen Verschiedenes (Hinweis von Codex).
/// </para>
/// </summary>
public sealed class TransferExcludes
{
    /// <summary>So viele Muster nimmt ein Transfer höchstens – wie im Kern.</summary>
    public const int MaxPatterns = 64;

    /// <summary>So viele Zeichen darf ein Muster haben – wie im Kern.</summary>
    public const int MaxPattern = 255;

    /// <summary>So viele Einträge sieht die Suche auf diesem Rechner höchstens an, bevor sie aufgibt.</summary>
    public const int ScanLimit = 200_000;

    /// <summary>Die Vorgabe: was fast nie mit soll.</summary>
    public static IReadOnlyList<string> Default { get; } = [".git", "node_modules", "*.tmp"];

    private readonly List<(string Written, Rune[][] Pieces, bool IsName)> _patterns = [];

    public TransferExcludes(IEnumerable<string>? patterns)
    {
        foreach (var written in Clean(patterns))
        {
            var pieces = Pieces(Normalize(written)!);
            _patterns.Add((written, pieces, pieces.Length == 1 && !written.Trim().StartsWith('/')));
        }
    }

    /// <summary>Die Muster, wie sie an den Kern gehen.</summary>
    public IReadOnlyList<string> Patterns => [.. _patterns.Select(pattern => pattern.Written)];

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>
    /// Ein Muster, wie verglichen wird: ohne Leerraum außen, ohne <c>/</c> am Anfang und Ende, A–Z klein.
    /// <c>null</c>, wenn es keines sein kann – leer, zu lang, mit Steuerzeichen oder <c>\</c>, mit einem leeren
    /// Stück oder mit <c>.</c>/<c>..</c> als Stück.
    /// </summary>
    public static string? Normalize(string? pattern)
    {
        var text = Lower((pattern ?? string.Empty).Trim().Trim('/'));
        return text.Length > 0
            && text.EnumerateRunes().Count() <= MaxPattern
            && !text.Any(c => char.IsControl(c) || c == '\\')
            && text.Split('/').All(piece => piece.Length > 0 && piece is not ("." or ".."))
                ? text
                : null;
    }

    /// <summary>Die gültigen Muster einer Liste, jedes einmal, höchstens <see cref="MaxPatterns"/>.</summary>
    public static List<string> Clean(IEnumerable<string>? patterns)
    {
        List<string> clean = [];
        HashSet<string> seen = [];
        foreach (var pattern in patterns ?? [])
        {
            var written = (pattern ?? string.Empty).Trim();
            if (clean.Count < MaxPatterns && Normalize(written) is { } normal && seen.Add((written.StartsWith('/') ? "/" : string.Empty) + normal))
            {
                clean.Add(written);
            }
        }

        return clean;
    }

    /// <summary>Liest Muster aus einem Textfeld: eines je Zeile.</summary>
    public static List<string> Parse(string? text) => Clean((text ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')));

    /// <summary>Welche Zeilen eines Textfelds keine Muster sind (leere zählen nicht).</summary>
    public static List<string> Rejected(string? text)
        => [.. (text ?? string.Empty).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && Normalize(line) is null)];

    /// <summary>
    /// Alle Muster, die den Eintrag mit diesem Pfad unterhalb des übertragenen Ordners auslassen – in der
    /// Reihenfolge der Muster. Gefragt wird je Eintrag beim Ablaufen eines Baums; ein ausgelassener Ordner wird
    /// nicht betreten.
    /// </summary>
    public IReadOnlyList<string> PatternsFor(string relative)
    {
        if (_patterns.Count == 0 || relative.Length == 0)
        {
            return [];
        }

        var pieces = Pieces(Lower(relative));
        return
        [
            .. _patterns
                .Where(pattern => pattern.IsName
                    ? Glob(pattern.Pieces[0], pieces[^1])
                    : pattern.Pieces.Length == pieces.Length && pattern.Pieces.Zip(pieces).All(pair => Glob(pair.First, pair.Second)))
                .Select(pattern => pattern.Written),
        ];
    }

    public bool Skips(string relative) => PatternsFor(relative).Count > 0;

    /// <summary>
    /// Ob ein Pfad unter dem übertragenen Ordner wegfällt, weil er selbst oder ein Ordner über ihm ausgelassen wird –
    /// für Listen, die schon alle Dateien nennen (Abgleich).
    /// </summary>
    public bool SkipsAnyLevel(string relative)
    {
        if (_patterns.Count == 0)
        {
            return false;
        }

        for (var end = relative.IndexOf('/'); end >= 0; end = relative.IndexOf('/', end + 1))
        {
            if (Skips(relative[..end]))
            {
                return true;
            }
        }

        return Skips(relative);
    }

    /// <summary>
    /// Was die Muster in einem Ordner dieses Rechners auslassen würden – jeweils der oberste Eintrag; in einen
    /// ausgelassenen Ordner wird nicht hineingesehen. Links werden nicht verfolgt (wie beim Upload im Kern). Was sich
    /// nicht lesen lässt, zählt nicht; nach <see cref="ScanLimit"/> Einträgen hört die Suche auf.
    /// </summary>
    public List<ExcludedItem> ScanLocal(string folder)
    {
        List<ExcludedItem> found = [];
        if (_patterns.Count == 0 || !Directory.Exists(folder))
        {
            return found;
        }

        var seen = 0;
        Queue<(string Path, string Relative)> open = new([(folder, string.Empty)]);
        while (open.Count > 0 && seen < ScanLimit)
        {
            var (directory, prefix) = open.Dequeue();
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                seen++;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                var relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
                var patterns = PatternsFor(relative);
                found.AddRange(patterns.Select(pattern => new ExcludedItem(pattern, relative, isDirectory)));
                if (patterns.Count == 0 && isDirectory)
                {
                    open.Enqueue((entry.FullName, relative));
                }
            }
        }

        return found;
    }

    /// <summary>Die Treffer je Muster, in der Reihenfolge der Muster – die Zeilen der Rückfrage.</summary>
    public IReadOnlyList<ExcludedGroup> Group(IEnumerable<ExcludedItem> items)
    {
        var byPattern = items.ToLookup(item => item.Pattern);
        return BindableList.Of(_patterns
            .Where(pattern => byPattern.Contains(pattern.Written))
            .Select(pattern => new ExcludedGroup(
                pattern.Written,
                byPattern[pattern.Written].Count(item => item.IsDirectory),
                byPattern[pattern.Written].Count(item => !item.IsDirectory),
                byPattern[pattern.Written].First().Path)));
    }

    // Nur A–Z: wie `to_ascii_lowercase` im Kern.
    private static string Lower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            span[index] = source[index] is >= 'A' and <= 'Z' ? (char)(source[index] + 32) : source[index];
        }
    });

    // Ein Pfad in Stücken, jedes als Unicode-Zeichen – wie `chars()` im Kern.
    private static Rune[][] Pieces(string path) => [.. path.Split('/').Select(piece => piece.EnumerateRunes().ToArray())];

    // `*` für beliebig viele Zeichen, `?` für eines – ohne Rekursion, wie im Kern.
    private static bool Glob(Rune[] pattern, Rune[] text)
    {
        var (star, question) = (new Rune('*'), new Rune('?'));
        var (at, seen) = (0, 0);
        (int At, int Taken)? last = null;
        while (seen < text.Length)
        {
            if (at < pattern.Length && pattern[at] == star)
            {
                last = (at, seen);
                at++;
            }
            else if (at < pattern.Length && (pattern[at] == question || pattern[at] == text[seen]))
            {
                at++;
                seen++;
            }
            else if (last is { } back)
            {
                last = (back.At, back.Taken + 1);
                at = back.At + 1;
                seen = back.Taken + 1;
            }
            else
            {
                return false;
            }
        }

        return pattern.Skip(at).All(rest => rest == star);
    }
}
