using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Extensions;

/// <summary>
/// Ein Erweiterungspaket: eine Datei <c>.nxext</c> (JSON) mit Farbschemata, gespeicherten Befehlen, Regeln zum
/// Hervorheben und Tabellen zum Ergänzen – Dinge, die die App schon kennt. Kein Code: Ein Paket führt nichts aus und
/// sieht keine Geheimnisse. Alles darin ist fremde Eingabe und wird geprüft wie ein Import (<see cref="ExtensionFormat"/>).
/// </summary>
public sealed record ExtensionPackage(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    IReadOnlyList<TerminalTheme> Themes,
    IReadOnlyList<Snippet> Snippets,
    IReadOnlyList<HighlightRule> Highlights,
    IReadOnlyList<CommandTable> Completions)
{
    /// <summary>Ob der Benutzer das Paket gerade abgeschaltet hat (es bleibt installiert).</summary>
    public bool IsDisabled { get; init; }

    /// <summary>Was drin ist, in einer Zeile – für die Vorschau und die Liste.</summary>
    public string Summary => string.Join(", ", new[]
    {
        Themes.Count > 0 ? Localization.L.Get("ExtensionCountThemes", Themes.Count) : null,
        Snippets.Count > 0 ? Localization.L.Get("ExtensionCountSnippets", Snippets.Count) : null,
        Highlights.Count > 0 ? Localization.L.Get("ExtensionCountHighlights", Highlights.Count) : null,
        Completions.Count > 0 ? Localization.L.Get("ExtensionCountCompletions", Completions.Count) : null,
    }.OfType<string>());

    public override string ToString() => Name;
}

/// <summary>Das Format der Pakete: lesen (mit allen Prüfungen) und schreiben (für „Als Paket speichern“).</summary>
public static partial class ExtensionFormat
{
    public const string FileExtension = ".nxext";

    /// <summary>Größer ist kein Paket.</summary>
    public const int MaxLength = 1024 * 1024;

    public const int MaxThemes = 30;
    public const int MaxSnippets = 300;
    public const int MaxHighlights = 20;
    public const int MaxCompletions = 20;
    public const int MaxSnippetText = 4000;
    public const int MaxSnippetName = 80;
    private const int MaxWords = 200;

    /// <summary>
    /// Liest ein Paket. <c>null</c> und ein Grund (<paramref name="problem"/>, englisch, fürs Protokoll), wenn es keines
    /// ist. Was sich darin nicht verstehen lässt (eine Farbe, die keine ist, ein leerer Befehl), fällt einzeln weg.
    /// </summary>
    public static ExtensionPackage? Parse(string? text, out string? problem)
    {
        problem = null;
        if (text is null || text.Length is 0 or > MaxLength)
        {
            problem = "empty or larger than 1 MB";
            return null;
        }

        PackageFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, ExtensionJsonContext.Default.PackageFile);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        {
            problem = "not a package (JSON)";
            return null;
        }

        if (file is null || file.Format != 1)
        {
            problem = "unknown format";
            return null;
        }

        if (file.Id is not { } id || !PackageId().IsMatch(id) || id.Length > 64)
        {
            problem = "no valid id";
            return null;
        }

        if (Clean(file.Name, 60) is not { Length: > 0 } name)
        {
            problem = "no name";
            return null;
        }

        if (file.Version is not { } version || !VersionPattern().IsMatch(version))
        {
            problem = "no valid version";
            return null;
        }

        var slug = id.Replace('.', '-');
        var themes = (file.Themes ?? [])
            .Select(theme => Theme(theme, slug))
            .OfType<TerminalTheme>()
            .DistinctBy(theme => theme.Id)
            .Take(MaxThemes)
            .ToList();
        var snippets = (file.Snippets ?? [])
            .Select(SnippetOf)
            .OfType<Snippet>()
            .Take(MaxSnippets)
            .ToList();
        var highlights = (file.Highlights ?? [])
            .Select(Highlight)
            .OfType<HighlightRule>()
            .Take(MaxHighlights)
            .ToList();
        var completions = (file.Completions ?? [])
            .Select(Completion)
            .OfType<CommandTable>()
            .DistinctBy(table => table.Command)
            .Take(MaxCompletions)
            .ToList();
        if (themes.Count + snippets.Count + highlights.Count + completions.Count == 0)
        {
            problem = "nothing in it";
            return null;
        }

        return new ExtensionPackage(id, name, version, Clean(file.Author, 60) ?? string.Empty, Clean(file.Description, 400) ?? string.Empty, themes, snippets, highlights, completions);
    }

    /// <summary>Ein Paket als Text, so wie <see cref="Parse"/> es liest.</summary>
    public static string Write(ExtensionPackage package)
        => JsonSerializer.Serialize(
            new PackageFile
            {
                Format = 1,
                Id = package.Id,
                Name = package.Name,
                Version = package.Version,
                Author = package.Author,
                Description = package.Description,
                Themes = [.. package.Themes.Select(theme => new ThemeFile
                {
                    Name = theme.Name,
                    Background = theme.Background,
                    Foreground = theme.Foreground,
                    Cursor = theme.Cursor,
                    Selection = theme.Selection,
                    Ansi = [.. theme.Ansi],
                })],
                Snippets = [.. package.Snippets.Select(snippet => new SnippetFile { Name = snippet.Name, Text = snippet.Text, Run = snippet.Run, Folder = snippet.Folder })],
                Highlights = [.. package.Highlights.Select(rule => new HighlightFile { Pattern = rule.Pattern, Regex = rule.Regex, IgnoreCase = rule.IgnoreCase, Color = rule.Color })],
                Completions = [.. package.Completions.Select(table => new CompletionFile { Command = table.Command, Subs = [.. table.Subs], Flags = [.. table.Flags], PathSubs = [.. table.PathSubs] })],
            },
            ExtensionJsonContext.Default.PackageFile);

    /// <summary>Eine Kennung für ein Paket aus einem Namen (für „Als Paket speichern“): Kleinbuchstaben, Ziffern, Punkte.</summary>
    public static string IdOf(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return "own." + (slug.Length > 0 ? slug[..Math.Min(slug.Length, 50)].Trim('-') : "package");
    }

    // Name ohne Steuer- und Formatzeichen (Zeilenumbruch, Zeichen, die die Schreibrichtung drehen), gekürzt.
    private static string? Clean(string? text, int max)
    {
        if (text is null)
        {
            return null;
        }

        var kept = new string([.. text.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return kept.Length <= max ? kept : kept[..max].TrimEnd();
    }

    // Ein Schema bekommt die Kennung pack-<Paket>-<Name>: Es steht neben den mitgelieferten und den eigenen, und
    // keins von beiden lässt sich damit überschreiben.
    private static TerminalTheme? Theme(ThemeFile? file, string slug)
    {
        if (file is null || ThemeImport.CleanName(file.Name) is not { } name || file.Ansi is not { Count: 16 } ansi)
        {
            return null;
        }

        var colors = new[] { file.Background, file.Foreground, file.Cursor, file.Selection }.Concat(ansi).ToList();
        if (!colors.All(ThemeImport.IsColor))
        {
            return null;
        }

        var id = $"pack-{slug}-{ThemeImport.IdOf(name)["custom-".Length..]}";
        return id.Length <= 96 ? new TerminalTheme(id, name, file.Background!, file.Foreground!, file.Cursor!, file.Selection!, [.. ansi.Select(color => color!)]) : null;
    }

    private static Snippet? SnippetOf(SnippetFile? file)
    {
        if (file is null || Clean(file.Name, MaxSnippetName) is not { Length: > 0 } name || file.Text is not { Length: > 0 and <= MaxSnippetText } text)
        {
            return null;
        }

        // Der Text geht ins Terminal: kein Steuerzeichen außer Zeilenumbruch und Tabulator, kein ESC, nichts, was
        // die Schreibrichtung dreht und so verbirgt, was wirklich dasteht.
        if (text.Any(c => (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            return null;
        }

        return new Snippet(name, text, file.Run, SnippetFolders.Clean(file.Folder));
    }

    // Über denselben Leser wie die Regeln aus den Einstellungen: dieselben Farben, dieselbe Prüfung der Ausdrücke.
    private static HighlightRule? Highlight(HighlightFile? file)
    {
        if (file is null || file.Pattern is not { Length: > 0 } pattern || file.Color is not { } color || pattern.Contains('\n') || pattern.Contains('\r'))
        {
            return null;
        }

        var line = file.Regex ? $"{color}: /{pattern}/{(file.IgnoreCase ? "i" : string.Empty)}" : $"{color}: {pattern}";
        return TerminalHighlights.Parse(line) is [var rule] && rule.Regex == file.Regex ? rule : null;
    }

    private static CommandTable? Completion(CompletionFile? file)
    {
        if (file?.Command is not { } command || !Word().IsMatch(command))
        {
            return null;
        }

        static string[] Words(List<string?>? list) => [.. (list ?? []).OfType<string>().Where(word => Word().IsMatch(word)).Distinct(StringComparer.Ordinal).Take(MaxWords)];
        return new CommandTable(command, Words(file.Subs), Words(file.Flags), new Dictionary<string, string[]>(), Words(file.PathSubs));
    }

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex PackageId();

    [GeneratedRegex(@"^\d{1,6}(\.\d{1,6}){0,3}$")]
    private static partial Regex VersionPattern();

    // Wie die Terminal-Seite ein Wort annimmt: sichtbares ASCII, höchstens 40 Zeichen.
    [GeneratedRegex("^[\\x21-\\x7e]{1,40}$")]
    private static partial Regex Word();
}

internal sealed class PackageFile
{
    public int Format { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Author { get; set; }

    public string? Description { get; set; }

    public List<ThemeFile?>? Themes { get; set; }

    public List<SnippetFile?>? Snippets { get; set; }

    public List<HighlightFile?>? Highlights { get; set; }

    public List<CompletionFile?>? Completions { get; set; }
}

internal sealed class ThemeFile
{
    public string? Name { get; set; }

    public string? Background { get; set; }

    public string? Foreground { get; set; }

    public string? Cursor { get; set; }

    public string? Selection { get; set; }

    public List<string?>? Ansi { get; set; }
}

internal sealed class SnippetFile
{
    public string? Name { get; set; }

    public string? Text { get; set; }

    public bool Run { get; set; }

    public string? Folder { get; set; }
}

internal sealed class HighlightFile
{
    public string? Pattern { get; set; }

    public bool Regex { get; set; }

    public bool IgnoreCase { get; set; }

    public string? Color { get; set; }
}

internal sealed class CompletionFile
{
    public string? Command { get; set; }

    public List<string?>? Subs { get; set; }

    public List<string?>? Flags { get; set; }

    public List<string?>? PathSubs { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PackageFile))]
internal sealed partial class ExtensionJsonContext : JsonSerializerContext;
