namespace NexusSSH.Core.Models;

/// <summary>
/// Eintrag in einem Remote-Verzeichnis. Bei einem Link (<see cref="IsLink"/>) beschreiben die übrigen Werte
/// sein Ziel. <see cref="Modified"/> ist <see cref="DateTimeOffset.MinValue"/>, wenn der Server keine Zeit nennt.
/// </summary>
/// <param name="Permissions">Zugriffsrechte wie bei <c>chmod</c> (z. B. <c>0644</c> oktal); <c>null</c>, wenn unbekannt.</param>
/// <param name="Owner">Besitzer: der Name, wenn der Server ihn kennt, sonst die Nummer.</param>
/// <param name="Group">Gruppe, wie <paramref name="Owner"/>.</param>
/// <param name="IsSpecial">Weder Datei noch Ordner: Pipe, Gerät, Socket oder ein Link ohne Ziel. Lässt sich nicht öffnen.</param>
/// <param name="LinkTarget">
/// Nur bei einem Link: wohin er zeigt, so wie es im Link steht (relativ oder absolut, nicht aufgelöst) – Text vom
/// Server. <c>null</c>, wenn die Liste es nicht nennt: Bei Dateizugriff über Befehle und bei Servern, die es nicht
/// sagen (DSM), ist das Ziel unbekannt, nicht leer; <see cref="NexusSSH.Core.Services.IRemoteFileSystem.ReadLinkAsync"/> fragt es einzeln.
/// </param>
/// <param name="IsDangling">
/// Der Link zeigt ins Leere: Sein Ziel gibt es nicht (<see cref="IsSpecial"/> ist dann auch wahr). Nur <c>true</c> ist
/// eine Aussage des Servers – <c>false</c> heißt „nicht bekannt“, nicht „zeigt auf etwas“.
/// </param>
public sealed record RemoteEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified,
    bool IsLink = false,
    int? Permissions = null,
    string? Owner = null,
    string? Group = null,
    bool IsSpecial = false,
    string? LinkTarget = null,
    bool IsDangling = false)
{
    /// <summary>Versteckt nach Unix-Brauch: Der Name beginnt mit einem Punkt.</summary>
    public bool IsHidden => Name.StartsWith('.');

    /// <summary>Der Name, wie er angezeigt wird: ohne Steuerzeichen (<see cref="Format.Visible"/>). Für Aktionen gilt <see cref="Name"/>.</summary>
    public string DisplayName => Format.Visible(Name);

    /// <summary>Das Ziel eines Links für die Zeile: „→ ziel“, sichtbar gemacht und gekürzt (<see cref="Format.LinkText"/>); leer ohne Ziel.</summary>
    public string LinkText => Format.LinkText(this);

    /// <summary>Die Art für die Spalte „Typ“: Link, Ordner, „ZIP-Datei“ … (<see cref="Format.EntryType(RemoteEntry)"/>).</summary>
    public string TypeText => Format.EntryType(this);

    /// <summary>Ob sich der Eintrag herunterladen lässt: alles außer Pipes, Geräten, Sockets und Links ohne Ziel.</summary>
    public bool IsTransferable => !IsSpecial;

    /// <summary>
    /// Der Name – Listen lesen ihn Screenreadern als Bezeichnung der Zeile vor. Bei einem Link gehören sein Ziel und
    /// der Hinweis dazu, dass es ins Leere zeigt (<see cref="Format.Spoken"/>); <see cref="DisplayName"/> bleibt der Name allein.
    /// </summary>
    public override string ToString() => Format.Spoken(this);
}
