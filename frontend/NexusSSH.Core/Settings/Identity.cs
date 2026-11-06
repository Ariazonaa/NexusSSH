namespace NexusSSH.Core.Settings;

/// <summary>
/// Eine Identität: Benutzer und Schlüssel, die mehrere Profile teilen. Ändert sie sich, schreibt die App die neuen
/// Angaben in jedes Profil, das auf sie verweist (<c>ServerProfile.IdentityId</c>) – der Kern verbindet weiter mit
/// dem, was im Profil steht. Kein Geheimnis: Passwort und Passphrase bleiben je Profil beim Kern.
/// </summary>
/// <param name="Id">Leer bei einer neuen; die App vergibt die Kennung beim Speichern.</param>
/// <param name="KeyPath">Privater Schlüssel; ohne Angabe gelten die Standarddateien in <c>~/.ssh</c>.</param>
public sealed record Identity(string Id, string Name, string User, string? KeyPath = null, bool UseAgent = true)
{
    /// <summary>Was die Liste darunter zeigt: der Benutzer und, wenn es einen gibt, der Name der Schlüsseldatei.</summary>
    public string Summary => KeyPath is { Length: > 0 } key ? $"{User}  ·  {Path.GetFileName(key)}" : User;

    /// <summary>Wie der Kern einen Benutzernamen prüft: nicht leer, ohne Leer- und Steuerzeichen.</summary>
    public static bool IsValidUser(string user)
        => user.Length > 0 && !user.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    public override string ToString() => $"{Name}, {Summary}";
}
