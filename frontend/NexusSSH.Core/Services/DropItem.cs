namespace NexusSSH.Core.Services;

/// <summary>
/// Ein Eintrag des Servers, wie ein anderes Programm ihn bekommt: unter <paramref name="Name"/>, den das Backend
/// festlegt – der Name auf dem Server ist fremd und passt nicht immer in einen Pfad dieses Rechners.
/// </summary>
/// <param name="Size">Bei Ordnern 0.</param>
/// <param name="Modified"><see cref="DateTimeOffset.MinValue"/>, wenn unbekannt.</param>
public sealed record DropItem(string RemotePath, string Name, bool IsDirectory, long Size = 0, DateTimeOffset Modified = default);

/// <summary>Was beim Anbieten und Liefern auffällt und ins Protokoll gehört.</summary>
public enum DropNotice
{
    /// <summary>Es wird geladen.</summary>
    Fetching,

    /// <summary>Keine Verbindung: Es gibt nichts zu holen.</summary>
    NotConnected,

    /// <summary>Was in den Ordnern liegt, ließ sich nicht ermitteln.</summary>
    ListFailed,

    /// <summary>Mehr Einträge, als sich anbieten lassen (<see cref="VirtualDrop.MaxEntries"/>).</summary>
    TooMany,

    /// <summary>Ein Pfad ist samt Unterordnern zu lang für den Empfänger (<see cref="VirtualDrop.MaxPathLength"/>).</summary>
    TooLong,
}
