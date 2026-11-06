using System.Text.Json.Serialization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Settings;

/// <summary>
/// Ein gemerkter Abgleich: ein lokaler Ordner und einer am Server samt Richtung, Art des Vergleichs und
/// Ausschlüssen unter einem Namen – mit einem Klick wieder abzugleichen, die Vorschau davor bleibt. Steht in den
/// Einstellungen dieses Rechners (ein lokaler Pfad gilt nur hier) und wandert nicht in den Export.
/// </summary>
/// <param name="Id">Leer bei einem neuen; die App vergibt die Kennung beim Speichern.</param>
/// <param name="ProfileId">Der Server, als Kennung seines Profils – gibt es das Profil nicht mehr, lässt sich der
/// Abgleich nicht ausführen.</param>
/// <param name="LocalFolder">Der Ordner auf diesem Rechner, vollständiger Pfad.</param>
/// <param name="RemoteFolder">Der Ordner am Server, so wie der Dateibrowser ihn nennt.</param>
/// <param name="ByContent">Nach Inhalt (Prüfsummen) vergleichen statt nach Größe und Zeit.</param>
/// <param name="Exclude">Die Ausschlüsse dieses Abgleichs (eigene, nicht die der Einstellungen).</param>
public sealed record SavedSync(
    string Id,
    string Name,
    string ProfileId,
    string LocalFolder,
    string RemoteFolder,
    SyncDirection Direction,
    bool ByContent,
    List<string> Exclude)
{
    public const int MaxNameLength = 60;

    /// <summary>Mehr gemerkte Abgleiche nimmt die App nicht an.</summary>
    public const int MaxCount = 200;

    /// <summary>Was die Liste darunter zeigt: Server, Richtung und Ordner. Setzt die App beim Aufbau der Liste.</summary>
    [JsonIgnore]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Ein Name taugt, wenn nach dem Kürzen etwas übrig bleibt und er keine Steuerzeichen enthält.</summary>
    public static bool IsValidName(string name)
        => name.Trim() is { Length: > 0 and <= MaxNameLength } trimmed && !trimmed.Any(char.IsControl);

    /// <summary>
    /// Ob alles darin taugt: Name, Profil, ein vollständiger lokaler Pfad, ein Pfad am Server, der mit <c>/</c>
    /// beginnt, und eine bekannte Richtung. Die Datei der Einstellungen lässt sich von Hand bearbeiten.
    /// </summary>
    public bool IsUsable
        => Id is { Length: > 0 }
            && IsValidName(Name ?? string.Empty)
            && ProfileId is { Length: > 0 }
            && LocalFolder is { Length: > 0 and <= 1024 } && !LocalFolder.Any(char.IsControl) && Path.IsPathFullyQualified(LocalFolder)
            && RemoteFolder is { Length: > 0 and <= 4096 } && RemoteFolder.StartsWith('/') && !RemoteFolder.Any(char.IsControl)
            && Enum.IsDefined(Direction);

    public override string ToString() => Summary.Length > 0 ? $"{Name}, {Summary}" : Name;
}
