namespace NexusSSH.Core;

/// <summary>
/// Gruppen der Profile als Pfad: <c>Kunde/Projekt/Prod</c> ist die Gruppe „Prod“ in „Projekt“ in „Kunde“. Im Profil
/// steht weiter ein einziger Text; verschachtelt ist er nur durch den Schrägstrich. Die Wurzel ist der leere Text.
/// </summary>
public static class GroupPath
{
    public const char Separator = '/';

    /// <summary>Ohne leere Glieder und ohne Leerraum um die Schrägstriche: <c>" a / b//c "</c> wird <c>a/b/c</c>.</summary>
    public static string Normalize(string? group)
        => string.Join(Separator, (group ?? string.Empty).Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Das letzte Glied – der Name der Gruppe in ihrem Ordner.</summary>
    public static string Name(string path) => path[(path.LastIndexOf(Separator) + 1)..];

    /// <summary>Die Gruppe darüber; leer für eine Gruppe ganz oben.</summary>
    public static string Parent(string path) => path.LastIndexOf(Separator) is > 0 and var slash ? path[..slash] : string.Empty;

    /// <summary>Ob <paramref name="path"/> die Gruppe <paramref name="folder"/> selbst ist oder darunter liegt.</summary>
    public static bool IsWithin(string path, string folder)
        => folder.Length == 0
            || path == folder
            || (path.Length > folder.Length && path[folder.Length] == Separator && path.StartsWith(folder, StringComparison.Ordinal));

    /// <summary>
    /// Die Gruppe direkt unter <paramref name="folder"/>, in der <paramref name="path"/> liegt – oder
    /// <c>null</c>, wenn er nicht unterhalb liegt (auch nicht, wenn er der Ordner selbst ist).
    /// </summary>
    public static string? ChildOf(string path, string folder)
    {
        if (path.Length == 0 || path == folder || !IsWithin(path, folder))
        {
            return null;
        }

        var start = folder.Length == 0 ? 0 : folder.Length + 1;
        var end = path.IndexOf(Separator, start);
        return end < 0 ? path : path[..end];
    }

    /// <summary>Der Pfad selbst und alle Gruppen darüber, von oben nach unten: <c>a</c>, <c>a/b</c>, <c>a/b/c</c>.</summary>
    public static IEnumerable<string> WithAncestors(string path)
    {
        for (var index = path.IndexOf(Separator); index >= 0; index = path.IndexOf(Separator, index + 1))
        {
            yield return path[..index];
        }

        if (path.Length > 0)
        {
            yield return path;
        }
    }
}
