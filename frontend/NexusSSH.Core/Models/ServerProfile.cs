using NexusSSH.Core.Services;

namespace NexusSSH.Core.Models;

/// <summary>
/// Gespeichertes Serverprofil (Spec §8). Enthält nie ein Passwort – Geheimnisse liegen im Rust-Kern bzw. im
/// Windows Credential Manager.
/// </summary>
/// <param name="Id">Leer bei einem neuen Profil; das Backend vergibt die Kennung beim Speichern.</param>
/// <param name="KeyPath">Privater Schlüssel; ohne Angabe gelten die Standarddateien in <c>~/.ssh</c>.</param>
/// <param name="UseAgent">Schlüssel aus dem SSH-Agenten (OpenSSH-Agent, Pageant) anbieten.</param>
/// <param name="JumpId">Kennung des Profils, über dessen Server die Verbindung läuft (Sprung-Server).</param>
/// <param name="ForwardAgent">Dem Server erlauben, den lokalen SSH-Agenten zu benutzen.</param>
/// <param name="HasSavedSecret">Nur Auskunft des Backends: Zu diesem Profil ist ein Passwort oder eine Passphrase gespeichert.</param>
/// <param name="Color">Kennfarbe (<see cref="Settings.ProfileColors"/>), damit man Server an Tab und Kachel unterscheidet; <c>null</c> = keine.</param>
/// <param name="IdentityId">Kennung der Identität (<see cref="Settings.Identity"/>), von der Benutzer und Schlüssel stammen; <c>null</c> = eigene Angaben.</param>
/// <param name="Protocol"><c>telnet</c> für eine Telnet-Verbindung; <c>null</c> = SSH.</param>
/// <param name="System">Kennung des Betriebssystems (<see cref="Settings.ServerSystems"/>), beim Verbinden erkannt; <c>null</c> = noch unbekannt.</param>
/// <param name="ForwardX11">Grafische Programme des Servers auf dem X-Server dieses Rechners zeichnen lassen.</param>
/// <param name="Notes">Freier Text des Benutzers zum Server; nie ein Geheimnis.</param>
/// <param name="NoReconnect">Nach einem Abriss nicht von selbst neu verbinden – auch wenn es in den Einstellungen an ist.</param>
/// <param name="AddressFamily"><c>ipv4</c> oder <c>ipv6</c>, wenn nur solche Adressen versucht werden sollen; <c>null</c> = beide.</param>
/// <param name="ConnectTimeout">Sekunden, die der Aufbau dauern darf; <c>null</c> = die übliche Zeit.</param>
/// <param name="Compression">Die Verbindung komprimieren – für langsame Leitungen.</param>
/// <param name="LegacyAlgorithms">Auch schwache, alte Verfahren anbieten – für Geräte, die nichts anderes sprechen.</param>
/// <param name="FtpActive">Nur bei FTP: Der Server baut die Datenverbindungen zu diesem Rechner auf (aktiver Modus) – nur auf Wunsch, die Vorgabe ist passiv.</param>Nur bei FTP: <c>none</c> = ausdrücklich ohne Verschlüsselung; <c>implicit</c> = TLS ab dem ersten Byte (Port 990); <c>null</c> = mit TLS über <c>AUTH TLS</c>, die Vorgabe.</param>
/// <param name="FtpEncoding">Nur bei FTP: wie der Server Dateinamen schreibt, wenn nicht als UTF-8 – <c>latin1</c> oder <c>windows1252</c>; <c>null</c> = UTF-8.</param>
/// <param name="Tunnels">Portweiterleitungen, die mit einer Verbindung zu diesem Server stehen (nur SSH); <c>null</c> = keine.</param>
/// <param name="IsTransient">Ein vorläufiges Profil für eine Verbindung ohne gespeicherten Server („Schnellverbinden“): in keiner Liste, und weg, sobald der letzte Tab dazu schließt.</param>
/// <param name="KeepAliveSeconds">Sekunden zwischen zwei Lebenszeichen an diesen Server, 0 = keine; <c>null</c> = wie allgemein eingestellt.</param>
/// <param name="KeepAliveMissed">So viele Lebenszeichen dürfen unbeantwortet bleiben; <c>null</c> = wie allgemein eingestellt.</param>
/// <param name="Term">Als welches Terminal sich die App beim Server ausgibt (<c>TERM</c>); <c>null</c> = <c>xterm-256color</c>.</param>
/// <param name="Env">Umgebungsvariablen für die Shell (nur SSH); der Server nimmt nur an, was er erlaubt.</param>
/// <param name="Charset">In welchem Zeichensatz der Server spricht, wenn nicht in UTF-8 (<c>TerminalCharset</c>); <c>null</c> = UTF-8.</param>
/// <param name="IdentitiesOnly">Nur den genannten Schlüssel anbieten, keine weiteren aus dem Agenten (<c>IdentitiesOnly</c>); gilt nur mit einem Schlüssel im Profil.</param>
/// <param name="ThemeId">Kennung des Farbschemas, in dem das Terminal dieses Servers erscheint; <c>null</c> = das allgemein eingestellte.</param>
/// <param name="LocalDirectory">Nur bei einem lokalen Terminal: der Ordner, in dem die Shell beginnt (absolut); <c>null</c> = der Benutzerordner. Fehlt er beim Start, beginnt die Shell dort trotzdem.</param>
/// <param name="Mosh">Das Terminal über Mosh (UDP) führen statt über den Kanal der SSH-Verbindung; angemeldet wird weiter über SSH, Dateien, Auslastung und Tunnel bleiben dort. Nur SSH, nie über einen Sprung-Server; ohne Agent- und X11-Forwarding und nur UTF-8.</param>
/// <param name="MoshPorts">Die UDP-Ports, aus denen <c>mosh-server</c> einen nimmt (<see cref="Models.MoshPorts"/>): <c>60001</c> oder <c>60001:60010</c>; <c>null</c> = er wählt selbst.</param>
/// <param name="SshAlias">Der Name hinter <c>Host</c> in <c>~/.ssh/config</c>, mit dem das Profil verknüpft ist: Adresse, Port, Benutzer und Schlüssel liest der Kern dann bei jedem Start neu aus der Datei. <c>null</c> = nicht verknüpft.</param>
public sealed record ServerProfile(
    string Id,
    string Name,
    string Host,
    int Port,
    string User,
    string Group,
    IReadOnlyList<string> Tags,
    bool IsFavorite = false,
    string? KeyPath = null,
    bool UseAgent = true,
    DateTimeOffset? LastConnected = null,
    string? JumpId = null,
    bool ForwardAgent = false,
    bool HasSavedSecret = false,
    string? Color = null,
    string? IdentityId = null,
    string? Protocol = null,
    bool ForwardX11 = false,
    string? System = null,
    int? Baud = null,
    string Notes = "",
    bool NoReconnect = false,
    string? AddressFamily = null,
    int? ConnectTimeout = null,
    bool Compression = false,
    bool LegacyAlgorithms = false,
    string? SshAlias = null,
    string? FtpTls = null,
    string? ThemeId = null,
    bool FtpActive = false,
    string? FtpEncoding = null,
    IReadOnlyList<ProfileTunnel>? Tunnels = null,
    bool IsTransient = false,
    int? KeepAliveSeconds = null,
    int? KeepAliveMissed = null,
    string? Term = null,
    IReadOnlyList<ProfileEnv>? Env = null,
    string? Charset = null,
    bool IdentitiesOnly = false,
    string? LocalDirectory = null,
    bool Mosh = false,
    string? MoshPorts = null)
{
    /// <summary>
    /// Nur für die Anzeige: die Kennfarbe der Gruppe, in der das Profil liegt (oder der nächsten Gruppe darüber, die
    /// eine hat). Die App setzt sie beim Laden der Liste; gespeichert wird sie nicht im Profil.
    /// </summary>
    public string? GroupColor { get; init; }

    /// <summary>Die Farbe, die zu sehen ist: die eigene des Profils, sonst die seiner Gruppe.</summary>
    public string? ShownColor => Color ?? GroupColor;

    /// <summary>
    /// Nur für die Anzeige: das Zeichen, das der Benutzer diesem Server selbst gegeben hat
    /// (<see cref="Settings.TileMarks"/>); <c>null</c> = keines. Die App setzt es beim Laden der Liste; gespeichert
    /// wird es nicht im Profil. Was auf der Kachel steht, entscheidet <see cref="Settings.TileMarks.Face"/>: das
    /// eigene Zeichen vor dem Betriebssystem vor dem Zeichen der Gruppe.
    /// </summary>
    public string? Mark { get; init; }

    /// <summary>
    /// Nur für die Anzeige: das Zeichen der Gruppe, in der das Profil liegt (oder der nächsten Gruppe darüber, die
    /// eines hat); <c>null</c> = keines. Wie <see cref="Mark"/> beim Laden gesetzt, nie gespeichert.
    /// </summary>
    public string? GroupMark { get; init; }

    public const string Telnet = "telnet";
    public const string Serial = "serial";
    public const string Ftp = "ftp";
    public const string Local = "local";

    /// <summary>Der Wert von <see cref="FtpTls"/> für FTP ohne Verschlüsselung.</summary>
    public const string FtpPlain = "none";

    /// <summary>Der Wert von <see cref="FtpTls"/> für TLS ab dem ersten Byte (implizites FTPS, der alte Port 990).</summary>
    public const string FtpImplicit = "implicit";

    /// <summary><see cref="FtpEncoding"/>: Der Server schreibt Dateinamen als ISO-8859-1.</summary>
    public const string FtpLatin1 = "latin1";

    /// <summary><see cref="FtpEncoding"/>: Der Server schreibt Dateinamen als Windows-1252.</summary>
    public const string FtpWindows1252 = "windows1252";

    /// <summary>Die Geschwindigkeit einer seriellen Verbindung, wenn das Profil keine nennt.</summary>
    public const int DefaultBaud = 9600;

    /// <summary>
    /// Telnet statt SSH: unverschlüsselt, für Geräte, die nichts anderes sprechen. Der Benutzer meldet sich im
    /// Terminal selbst an; Dateien, Auslastung, Schlüssel und Sprung-Server gibt es dabei nicht.
    /// </summary>
    public bool IsTelnet => Protocol == Telnet;

    /// <summary>
    /// Eine serielle Schnittstelle dieses Rechners statt einer Verbindung im Netz: <see cref="Host"/> nennt sie
    /// (<c>COM3</c>), <see cref="Baud"/> die Geschwindigkeit. Sonst gilt dasselbe wie für Telnet.
    /// </summary>
    public bool IsSerial => Protocol == Serial;

    /// <summary>
    /// FTP statt SSH: nur Dateien, kein Terminal. Verschlüsselt mit TLS (FTPS), solange das Profil es nicht
    /// ausdrücklich abwählt (<see cref="IsPlainFtp"/>).
    /// </summary>
    public bool IsFtp => Protocol == Ftp;

    /// <summary>FTP ohne Verschlüsselung: Passwort und Dateien gehen im Klartext über die Leitung.</summary>
    public bool IsPlainFtp => IsFtp && FtpTls == FtpPlain;

    /// <summary>FTP mit TLS ab dem ersten Byte statt mit <c>AUTH TLS</c> – verschlüsselt wie sonst auch.</summary>
    public bool IsImplicitFtp => IsFtp && FtpTls == FtpImplicit;

    /// <summary>
    /// Eine Shell dieses Rechners statt eines Servers: <see cref="Host"/> nennt sie mit der Kennung, die der Kern
    /// beim Suchen vergibt (<c>pwsh</c>, <c>wsl:Ubuntu</c>, <c>login</c> …) – nie einen Pfad. Es gibt kein Netz, keinen
    /// Benutzer, keine Dateien und keine Auslastung; das Profil bleibt auf diesem Rechner (nicht im Export).
    /// </summary>
    public bool IsLocal => Protocol == Local;

    /// <summary>Über SSH – nur dann gibt es Schlüssel, Terminal, Auslastung und Sprung-Server.</summary>
    public bool IsSsh => !IsTelnet && !IsSerial && !IsFtp && !IsLocal;

    /// <summary>
    /// <c>user@host</c>, bei abweichendem Port mit <c>:port</c>; über Telnet <c>telnet://host</c>, seriell die
    /// Schnittstelle mit ihrer Geschwindigkeit, lokal der Name der Shell.
    /// </summary>
    public string Address => IsLocal
        ? LocalShellNames.Of(Host)
        : IsSerial
        ? $"{Host}, {Baud ?? DefaultBaud} Baud"
        : IsTelnet
            ? Port == 23 ? $"telnet://{Host}" : $"telnet://{Host}:{Port}"
            : IsFtp
                ? $"{(IsPlainFtp ? "ftp" : "ftps")}://{User}@{Host}{(Port == 21 ? string.Empty : $":{Port}")}"
            : Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";

    /// <summary>Name und Adresse – Vorschlagslisten lesen das Screenreadern vor.</summary>
    public override string ToString() => $"{Name}, {Address}";
}

/// <summary>Ein Server aus <c>~/.ssh/config</c>, wie <c>ssh alias</c> ihn auflösen würde.</summary>
/// <param name="Jump"><c>ProxyJump</c>, wie es in der Datei steht.</param>
/// <param name="Exists">Es gibt schon ein Profil mit diesem Namen oder diesem Ziel.</param>
/// <param name="IdentitiesOnly"><c>IdentitiesOnly yes</c> mit einem Schlüssel.</param>
/// <param name="Tunnels">Wie viele Weiterleitungen (<c>LocalForward</c> …) als Tunnel übernommen würden.</param>
/// <param name="Skipped">Schlüsselwörter, die für den Server in der Datei stehen und nicht übernommen werden.</param>
public sealed record SshConfigHost(
    string Alias,
    string Host,
    int Port,
    string User,
    string? KeyPath,
    string? Jump,
    bool ForwardAgent,
    bool Exists,
    bool IdentitiesOnly = false,
    int Tunnels = 0,
    IReadOnlyList<string>? Skipped = null)
{
    public string Address => Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";

    public override string ToString() => $"{Alias}, {Address}";
}

/// <summary>Ein neu angelegtes Schlüsselpaar: wo der private Schlüssel liegt, und die Zeile des öffentlichen.</summary>
public sealed record GeneratedKey(string Path, string PublicKey);

/// <summary>Inhalt von <c>~/.ssh/config</c>. <see cref="Path"/> ist <c>null</c>, wenn es kein <c>~/.ssh</c> gibt.</summary>
/// <param name="Unchecked">Bedingungen hinter <c>Match</c>, die die App nicht prüft (<c>exec</c> …) – solche Blöcke gelten hier nie.</param>
public sealed record SshConfigListing(string? Path, IReadOnlyList<SshConfigHost> Hosts, IReadOnlyList<string>? Unchecked = null);
