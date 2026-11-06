namespace NexusSSH.Core.Settings;

/// <summary>
/// Lesezeichen für Ordner am Server, je Profil (<see cref="AppSettings.Bookmarks"/>). Gemerkt wird nur der Pfad –
/// ob es den Ordner noch gibt, zeigt sich beim Wechsel dorthin.
/// </summary>
public static class FolderBookmarks
{
    /// <summary>Mehr Lesezeichen als das hat ein Server nicht: Sie stehen in einem Menü.</summary>
    public const int MaxPerProfile = 30;

    private const int MaxPathLength = 4096;

    public static IReadOnlyList<string> Of(AppSettings settings, string profileId)
        => settings.Bookmarks.TryGetValue(profileId, out var paths) ? paths : [];

    public static bool Contains(AppSettings settings, string profileId, string path)
        => Of(settings, profileId).Contains(RemotePath.Normalize(path), StringComparer.Ordinal);

    /// <summary>Merkt sich den Ordner. <c>false</c>, wenn er schon gemerkt ist oder kein Platz mehr ist.</summary>
    public static bool Add(AppSettings settings, string profileId, string path)
    {
        var normalized = RemotePath.Normalize(path);
        var paths = Of(settings, profileId);
        if (profileId.Length == 0 || normalized.Length > MaxPathLength || paths.Count >= MaxPerProfile || paths.Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        settings.Bookmarks[profileId] = [.. paths, normalized];
        return true;
    }

    public static bool Remove(AppSettings settings, string profileId, string path)
    {
        var normalized = RemotePath.Normalize(path);
        var paths = Of(settings, profileId);
        if (!paths.Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        if (paths.Count == 1)
        {
            settings.Bookmarks.Remove(profileId);
        }
        else
        {
            settings.Bookmarks[profileId] = [.. paths.Where(other => other != normalized)];
        }

        return true;
    }

    /// <summary>Holt eine von Hand bearbeitete oder beschädigte Liste in den erlaubten Bereich zurück.</summary>
    public static Dictionary<string, List<string>> Sanitize(Dictionary<string, List<string>>? bookmarks)
        => (bookmarks ?? [])
            .Where(entry => entry.Key is { Length: > 0 and <= 128 } && entry.Value is not null)
            .Select(entry => (entry.Key, Paths: entry.Value
                .Where(path => path is { Length: > 0 and <= MaxPathLength } && path.StartsWith('/'))
                .Select(RemotePath.Normalize)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxPerProfile)
                .ToList()))
            .Where(entry => entry.Paths.Count > 0)
            .Take(2000)
            .ToDictionary(entry => entry.Key, entry => entry.Paths);
}
