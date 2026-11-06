namespace NexusSSH.Core.Models;

/// <summary>
/// Was im Eigenschaften-Dialog des Dateibrowsers geändert wurde: Rechte (wie <c>chmod</c>), Besitzer und Gruppe
/// (wie <c>chown</c>). Was <c>null</c> ist, bleibt. <see cref="Recursive"/>: bei einem Ordner auch alles darin.
/// </summary>
public sealed record PropertyChange(int? Mode, string? Owner, string? Group, bool Recursive)
{
    /// <summary>
    /// Liest die Eingaben des Dialogs. <c>null</c>, wenn sie nicht taugen oder nichts ändern würden.
    /// <para>
    /// Ohne „auch alles darin“ zählt nur, was vom Bisherigen abweicht. Mit ihm gelten Besitzer und Gruppe für den
    /// ganzen Ordner so, wie sie in den Feldern stehen – auch wenn sie für den Ordner selbst schon stimmen: Darin
    /// kann es anders sein. Die Rechte dagegen wandern nur mit, wenn sie geändert wurden: Sonst bekäme, wer nur die
    /// Gruppe eines Baums ändern will, nebenbei jede Datei darin die Rechte des Ordners (ausführbar).
    /// </para>
    /// </summary>
    public static PropertyChange? From(RemoteEntry entry, string? modeText, string? ownerText, string? groupText, bool recursive)
    {
        recursive &= entry.IsDirectory;
        var (owner, group) = (ownerText?.Trim() ?? string.Empty, groupText?.Trim() ?? string.Empty);
        if (!IsValidName(owner) || !IsValidName(group))
        {
            return null;
        }

        int? mode = null;
        if (entry.Permissions is { } current)
        {
            if (!Format.TryParseMode(modeText, out var typed))
            {
                return null;
            }

            mode = Format.Octal(typed) != Format.Octal(current) ? typed : null;
        }

        var change = new PropertyChange(
            mode,
            owner.Length > 0 && (recursive || owner != entry.Owner) ? owner : null,
            group.Length > 0 && (recursive || group != entry.Group) ? group : null,
            recursive);
        return change is { Mode: null, Owner: null, Group: null } ? null : change;
    }

    // Ein Benutzer- oder Gruppenname, wie er in /etc/passwd stehen kann, oder eine Nummer. Leer heißt: nicht ändern.
    private static bool IsValidName(string name)
        => name.Length <= 64 && !name.Any(character => character == ':' || char.IsWhiteSpace(character) || char.IsControl(character));
}
