using System.Globalization;

namespace NexusSSH.Core.Settings;

/// <summary>
/// Ordner der Snippets: eine Ebene, nur ein Name am Befehl (<see cref="Snippet.Folder"/>). Die Reihenfolge ergibt sich
/// aus der Liste selbst – erst die Befehle ohne Ordner, dann die Ordner in der Reihenfolge, in der sie zuerst
/// vorkommen, darin die Befehle in ihrer Reihenfolge.
/// </summary>
public static class SnippetFolders
{
    /// <summary>Längster Name eines Ordners.</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// Der Name, wie er gespeichert wird: ohne Steuer- und Formatzeichen (Zeilenumbruch, Zeichen, die die
    /// Schreibrichtung drehen), ohne Leerraum außen, höchstens <see cref="MaxLength"/> Zeichen. Er kann aus einer
    /// Export-Datei stammen und steht als Überschrift in der Liste.
    /// </summary>
    public static string Clean(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return string.Empty;
        }

        var kept = new string([.. folder.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return kept.Length <= MaxLength ? kept : kept[..MaxLength].TrimEnd();
    }

    /// <summary>Derselbe Befehl mit bereinigtem Ordner.</summary>
    public static Snippet Cleaned(Snippet snippet)
    {
        var folder = Clean(snippet.Folder);
        return folder == snippet.Folder ? snippet : snippet with { Folder = folder };
    }

    /// <summary>Die Ordner in ihrer Reihenfolge, ohne „ohne Ordner“.</summary>
    public static List<string> Names(IEnumerable<Snippet> snippets)
        => [.. snippets.Select(snippet => snippet.Folder).Where(folder => folder.Length > 0).Distinct()];

    /// <summary>Die Befehle so geordnet, wie die Listen sie zeigen: erst ohne Ordner, dann Ordner für Ordner.</summary>
    public static List<T> Ordered<T>(IEnumerable<T> items, Func<T, string> folderOf)
    {
        var list = items.ToList();
        var rank = list.Select(folderOf).Where(folder => folder.Length > 0).Distinct()
            .Select((folder, index) => (folder, index)).ToDictionary(entry => entry.folder, entry => entry.index + 1);
        return [.. list.Select((item, index) => (item, index))
            .OrderBy(entry => folderOf(entry.item) is { Length: > 0 } folder ? rank[folder] : 0)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)];
    }
}
