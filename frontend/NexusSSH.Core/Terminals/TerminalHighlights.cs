using System.Text.RegularExpressions;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Eine Regel, nach der die Terminal-Seite Text einfärbt: ein Wort, wie es dasteht, oder ein regulärer Ausdruck.
/// <see cref="Color"/> ist einer der Namen aus <see cref="TerminalHighlights.Colors"/>; die Farbe selbst kommt aus
/// dem Farbschema.
/// </summary>
public sealed record HighlightRule(string Pattern, bool Regex, bool IgnoreCase, string Color);

/// <summary>
/// Die Regeln zum Hervorheben, wie der Benutzer sie in den Einstellungen tippt: eine je Zeile, <c>Farbe: Text</c>
/// oder <c>Farbe: /Ausdruck/</c> (mit <c>/i</c> ohne Rücksicht auf Groß und Klein). Was sich nicht verstehen lässt,
/// fällt weg, statt die Seite zu erreichen.
/// </summary>
public static class TerminalHighlights
{
    public const int MaxRules = 20;
    public const int MaxPatternLength = 200;

    /// <summary>Länge, bis zu der der Text der Einstellung gespeichert wird.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Die Namen der Farben, wie die Seite sie kennt.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["red", "green", "yellow", "blue", "magenta", "cyan"];

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "red",
        ["rot"] = "red",
        ["green"] = "green",
        ["grün"] = "green",
        ["yellow"] = "yellow",
        ["gelb"] = "yellow",
        ["blue"] = "blue",
        ["blau"] = "blue",
        ["magenta"] = "magenta",
        ["cyan"] = "cyan",
    };

    /// <summary>Die verstandenen Regeln, höchstens <see cref="MaxRules"/>.</summary>
    public static IReadOnlyList<HighlightRule> Parse(string? text)
        => [.. Lines(text).Select(Rule).OfType<HighlightRule>().Take(MaxRules)];

    /// <summary>Wie viele Zeilen keine Regel ergeben – nicht verstanden oder über <see cref="MaxRules"/> hinaus.</summary>
    public static int Ignored(string? text) => Lines(text).Count() - Parse(text).Count;

    /// <summary>Der Text, wie er gespeichert wird: nie <c>null</c>, nie länger als <see cref="MaxTextLength"/>.</summary>
    public static string Limit(string? text)
        => text is null ? string.Empty : text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    private static IEnumerable<string> Lines(string? text)
        => (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HighlightRule? Rule(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !Names.TryGetValue(line[..colon].Trim(), out var color))
        {
            return null;
        }

        var pattern = line[(colon + 1)..].Trim();
        if (pattern.Length is 0 or > MaxPatternLength)
        {
            return null;
        }

        var ignoreCase = pattern.EndsWith("/i", StringComparison.Ordinal);
        var end = ignoreCase ? pattern.Length - 2 : pattern.Length - 1;
        if (pattern[0] != '/' || end < 2 || pattern[end] != '/')
        {
            return new HighlightRule(pattern, Regex: false, IgnoreCase: false, color);
        }

        var expression = pattern[1..end];
        return IsExpression(expression) ? new HighlightRule(expression, Regex: true, ignoreCase, color) : null;
    }

    // Die Seite prüft den Ausdruck noch einmal mit ihren eigenen Regeln; hier fällt weg, was schon kein Ausdruck ist.
    private static bool IsExpression(string expression)
    {
        try
        {
            _ = new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
