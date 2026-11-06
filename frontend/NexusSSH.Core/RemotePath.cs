namespace NexusSSH.Core;

/// <summary>Ein Glied des Pfads für die Breadcrumb-Leiste.</summary>
public sealed record PathSegment(string Name, string FullPath)
{
    /// <summary>Der Name – die Breadcrumb-Leiste liest ihn Screenreadern vor.</summary>
    public override string ToString() => Name;
}

/// <summary>POSIX-Pfade des Servers. Bewusst nicht <see cref="Path"/>: das rechnet mit Windows-Regeln.</summary>
public static class RemotePath
{
    public const string Root = "/";

    /// <summary>Absoluter Pfad ohne doppelte Schrägstriche, ohne <c>.</c>/<c>..</c> und ohne Schrägstrich am Ende.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return Root + string.Join('/', parts);
    }

    public static string Combine(string directory, string name)
        => Normalize(directory + "/" + name);

    /// <summary>Letzter Teil des Pfads; leer für die Wurzel.</summary>
    public static string Name(string path)
    {
        var normalized = Normalize(path);
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Ob sich der Text als Name für eine neue Datei oder einen neuen Ordner eignet: nicht leer, kein
    /// Schrägstrich, nicht <c>.</c> oder <c>..</c>, keine Steuerzeichen.
    /// </summary>
    public static bool IsValidName(string name)
        => name.Length > 0 && name is not ("." or "..") && !name.Any(c => c == '/' || char.IsControl(c));

    /// <summary>Übergeordnetes Verzeichnis; <c>null</c> für die Wurzel.</summary>
    public static string? Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == Root)
        {
            return null;
        }

        var cut = normalized.LastIndexOf('/');
        return cut == 0 ? Root : normalized[..cut];
    }

    /// <summary>Die Wurzel und jedes Verzeichnis darunter bis zum Pfad selbst.</summary>
    public static IReadOnlyList<PathSegment> Segments(string path)
    {
        var segments = new List<PathSegment> { new(Root, Root) };
        var current = string.Empty;
        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add(new PathSegment(part, current));
        }

        return segments;
    }
}
