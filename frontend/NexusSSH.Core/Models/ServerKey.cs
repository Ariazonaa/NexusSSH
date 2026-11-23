namespace NexusSSH.Core.Models;

/// <summary>
/// Eine Zeile aus <c>authorized_keys</c> des Servers: ein Schlüssel, der sich an diesem Konto anmelden darf. Art,
/// Länge und Fingerabdruck errechnet der Kern; Kommentar und Optionen kommen vom Server und sind schon gesäubert.
/// </summary>
/// <param name="Line">Nummer der Zeile in der Datei, ab 1 – mit <see cref="Fingerprint"/> das, was Entfernen braucht.</param>
/// <param name="Algorithm">Art, z. B. <c>ssh-ed25519</c>; leer bei einer Zeile, die kein Schlüssel ist.</param>
/// <param name="Bits">Länge in Bit, wo bekannt.</param>
/// <param name="Fingerprint">SHA-256 in der Schreibweise von OpenSSH; leer wie <see cref="Algorithm"/>.</param>
/// <param name="Comment">Der Kommentar (meist <c>benutzer@rechner</c>).</param>
/// <param name="Options">Was vor dem Schlüssel steht (<c>from=…</c>, <c>command=…</c>); leer, wenn nichts.</param>
/// <param name="Unreadable">Bei einer Zeile, die kein Schlüssel ist: ihr Anfang. Sonst leer.</param>
public sealed record ServerKey(uint Line, string Algorithm, int? Bits, string Fingerprint, string Comment, string Options, string Unreadable)
{
    /// <summary>Ob die Zeile ein Schlüssel ist – nur dann lässt sie sich entfernen.</summary>
    public bool IsReadable => Fingerprint.Length > 0;
}

/// <summary>
/// Eine Datei mit Schlüsseln auf dem Server: <c>~/.ssh/authorized_keys</c>, bei root unter OpenWrt die von dropbear,
/// unter Windows die für alle Administratoren (<see cref="Shared"/>).
/// </summary>
/// <param name="Path">Der Pfad, wie SFTP ihn nennt.</param>
/// <param name="Exists">Ob es die Datei gibt.</param>
/// <param name="Shared">Gilt für alle Administratoren des Servers.</param>
/// <param name="Version">Der Stand der Datei; Entfernen geht nur, solange er gilt.</param>
/// <param name="Keys">Die Zeilen mit Schlüsseln, in der Reihenfolge der Datei.</param>
/// <param name="Error">Warum die Datei nicht (ganz) gelesen wurde: <c>too_large</c>, <c>permission_denied</c>, <c>too_many</c>; sonst leer.</param>
public sealed record ServerKeyFile(string Path, bool Exists, bool Shared, string Version, IReadOnlyList<ServerKey> Keys, string Error);
