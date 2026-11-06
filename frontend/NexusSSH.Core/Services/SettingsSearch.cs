using System.Globalization;
using System.Text;

namespace NexusSSH.Core.Services;

/// <summary>
/// Die Suche in den Einstellungen: ob ein Text (Beschriftung, Hinweis, Name des Bereichs) zu dem passt, was der
/// Benutzer getippt hat. Jedes getippte Wort muss vorkommen, gleich wo; Groß- und Kleinschreibung und Akzente
/// zählen nicht („tone“ findet „Töne“).
/// </summary>
public static class SettingsSearch
{
    public static bool Matches(string? text, string? query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return true;
        }

        var folded = Fold(text);
        return words.All(word => folded.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>Kleinbuchstaben ohne Akzente; „ß“ wird „ss“.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
            }
        }

        return builder.ToString();
    }
}
