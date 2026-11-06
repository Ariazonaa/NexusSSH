using System.Text.Json;
using System.Text.Json.Serialization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Backend;

// Nachrichten zwischen App und Rust-Kern, als JSON. Die Gegenstelle ist backend/nexus-core/src/protocol.rs –
// beide Seiten gemeinsam ändern (und ABI_VERSION / NativeCore.ExpectedAbi erhöhen, wenn es inkompatibel wird).
// Die Namen entstehen aus den Eigenschaften in snake_case: ProfileId → "profile_id".

internal sealed record ProfileDto(
    string Id,
    string Name,
    string Host,
    int Port,
    string User,
    string Group,
    List<string>? Tags,
    bool Favorite,
    string? KeyPath,
    bool UseAgent,
    long? LastConnected,
    string? Jump = null,
    bool ForwardAgent = false,
    bool? SavedSecret = null,
    string? Color = null,
    string? Identity = null,
    string? Protocol = null,
    bool ForwardX11 = false,
    string? System = null,
    int? Baud = null,
    string? Notes = null,
    bool NoReconnect = false,
    string? AddressFamily = null,
    int? ConnectTimeout = null,
    bool Compression = false,
    bool LegacyAlgorithms = false,
    string? SshAlias = null,
    string? FtpTls = null,
    string? Theme = null,
    bool FtpActive = false,
    string? FtpEncoding = null,
    List<TunnelDto>? Tunnels = null,
    bool? Transient = null,
    int? KeepaliveInterval = null,
    int? KeepaliveMax = null,
    string? Term = null,
    List<EnvDto>? Env = null,
    string? Charset = null,
    bool IdentitiesOnly = false,
    string? LocalDir = null,
    bool Mosh = false,
    string? MoshPorts = null)
{
    public static ProfileDto From(ServerProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.Host,
        profile.Port,
        profile.User,
        profile.Group,
        [.. profile.Tags],
        profile.IsFavorite,
        profile.KeyPath,
        profile.UseAgent,
        profile.LastConnected?.ToUnixTimeSeconds(),
        profile.JumpId,
        profile.ForwardAgent,
        Color: profile.Color,
        Identity: profile.IdentityId,
        Protocol: profile.Protocol,
        ForwardX11: profile.ForwardX11,
        System: profile.System,
        Baud: profile.Baud,
        Notes: profile.Notes,
        NoReconnect: profile.NoReconnect,
        AddressFamily: profile.AddressFamily,
        ConnectTimeout: profile.ConnectTimeout,
        Compression: profile.Compression,
        LegacyAlgorithms: profile.LegacyAlgorithms,
        SshAlias: profile.SshAlias,
        FtpTls: profile.FtpTls,
        Theme: profile.ThemeId,
        FtpActive: profile.FtpActive,
        FtpEncoding: profile.FtpEncoding,
        Tunnels: profile.Tunnels is { Count: > 0 } tunnels ? [.. tunnels.Select(TunnelDto.From)] : null,
        Transient: profile.IsTransient ? true : null,
        KeepaliveInterval: profile.KeepAliveSeconds,
        KeepaliveMax: profile.KeepAliveMissed,
        Term: profile.Term,
        Env: profile.Env is { Count: > 0 } env ? [.. env.Select(variable => new EnvDto(variable.Name, variable.Value))] : null,
        Charset: profile.Charset,
        IdentitiesOnly: profile.IdentitiesOnly && profile.KeyPath is { Length: > 0 },
        LocalDir: profile.IsLocal ? profile.LocalDirectory : null,
        Mosh: profile.Mosh && profile.IsSsh,
        MoshPorts: profile.IsSsh ? profile.MoshPorts : null);

    public ServerProfile ToProfile() => new(
        Id,
        Name,
        Host,
        Port,
        User,
        Group,
        Tags ?? [],
        Favorite,
        KeyPath,
        UseAgent,
        LastConnected is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
        Jump,
        ForwardAgent,
        SavedSecret ?? false,
        Color: Color,
        IdentityId: Identity,
        Protocol: Protocol,
        ForwardX11: ForwardX11,
        System: System,
        Baud: Baud,
        Notes: Notes ?? string.Empty,
        NoReconnect: NoReconnect,
        AddressFamily: AddressFamily,
        ConnectTimeout: ConnectTimeout,
        Compression: Compression,
        LegacyAlgorithms: LegacyAlgorithms,
        SshAlias: SshAlias,
        FtpTls: FtpTls,
        ThemeId: Theme,
        FtpActive: FtpActive,
        FtpEncoding: FtpEncoding,
        Tunnels: Tunnels is { Count: > 0 } ? BindableList.Of(Tunnels.Select(tunnel => tunnel.ToTunnel())) : null,
        IsTransient: Transient == true,
        KeepAliveSeconds: KeepaliveInterval,
        KeepAliveMissed: KeepaliveMax,
        Term: Term,
        Env: Env is { Count: > 0 } ? [.. Env.Select(variable => new ProfileEnv(variable.Name ?? string.Empty, variable.Value ?? string.Empty))] : null,
        Charset: Charset,
        IdentitiesOnly: IdentitiesOnly,
        LocalDirectory: LocalDir,
        Mosh: Mosh,
        MoshPorts: MoshPorts);
}

/// <summary>Eine Umgebungsvariable eines Profils, wie der Kern sie speichert (<c>profiles::EnvVar</c>).</summary>
internal sealed record EnvDto(string? Name, string? Value);

/// <summary>Ein Tunnel eines Profils, wie der Kern ihn speichert (<c>profiles::Tunnel</c>).</summary>
internal sealed record TunnelDto(string? Id, string? Kind, string? Name, int ListenPort, string? TargetHost, int TargetPort, bool Open, bool Auto)
{
    public static TunnelDto From(ProfileTunnel tunnel) => new(
        tunnel.Id,
        tunnel.Kind switch { TunnelKind.Remote => "remote", TunnelKind.Dynamic => "dynamic", _ => "local" },
        tunnel.Name,
        tunnel.ListenPort,
        tunnel.TargetHost,
        tunnel.TargetPort,
        tunnel.Open,
        tunnel.Auto);

    public ProfileTunnel ToTunnel() => new(
        Id ?? string.Empty,
        Kind switch { "remote" => TunnelKind.Remote, "dynamic" => TunnelKind.Dynamic, _ => TunnelKind.Local },
        Name ?? string.Empty,
        ListenPort,
        TargetHost ?? string.Empty,
        TargetPort,
        Open,
        Auto);
}

/// <summary>Anfrage an den Kern. <see cref="Op"/> wählt die Art, der Rest sind deren Felder.</summary>
internal sealed record RequestDto(string Op)
{
    public ProfileDto? Profile { get; init; }

    public string? Id { get; init; }

    public ulong? Session { get; init; }

    public string? ProfileId { get; init; }

    public int? Cols { get; init; }

    public int? Rows { get; init; }

    public ulong? Prompt { get; init; }

    public bool? Accept { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    public bool? Save { get; init; }

    /// <summary>Nummer einer <c>sftp.*</c>-Anfrage; ihr Ergebnis kommt als Ereignis mit derselben Nummer.</summary>
    public ulong? Request { get; init; }

    public string? Path { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    /// <summary><c>sftp.chmod</c>: die Rechte als Zahl (<c>0644</c> oktal = 420).</summary>
    public int? Mode { get; init; }

    public ulong? Transfer { get; init; }

    /// <summary><c>transfer.move</c>: der wartende Transfer, vor dem der genannte danach steht.</summary>
    public ulong? Before { get; init; }

    public string? Direction { get; init; }

    public string? Local { get; init; }

    public string? Remote { get; init; }

    /// <summary><c>transfer.start</c>: Name am Ziel, wenn er nicht der der Quelle sein soll.</summary>
    public string? Name { get; init; }

    /// <summary><c>transfer.start</c> von Server zu Server (<c>relay</c>): die Sitzung des Ziels.</summary>
    public ulong? TargetSession { get; init; }

    /// <summary><c>transfer.start</c> (Ordner): nur diese Dateien, als Pfade unterhalb des Ordners der Quelle.</summary>
    public IReadOnlyList<string>? Only { get; init; }

    /// <summary><c>transfer.start</c>, <c>sftp.tree</c>: Muster, die bei einem Ordner ausgelassen werden.</summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary><c>sftp.tree</c>: statt des Baums das liefern, was die Muster auslassen würden.</summary>
    public bool? Excluded { get; init; }

    /// <summary>Bei <c>transfer.start</c>: welche vorhandenen Dateien am Ziel bleiben (<c>same</c>, <c>newer</c>).</summary>
    public string? Keep { get; init; }

    /// <summary><c>sftp.pack</c>, <c>sftp.unpack</c>: Verzeichnis, in dem gepackt oder entpackt wird.</summary>
    public string? Directory { get; init; }

    /// <summary><c>sftp.pack</c>, <c>sftp.unpack</c>: Name des Archivs im Verzeichnis.</summary>
    public string? Archive { get; init; }

    /// <summary><c>logs.read</c>: wie weit das Journal zurückreicht (<c>15m</c>, <c>1h</c>, <c>6h</c>, <c>1d</c>, <c>7d</c>).</summary>
    public string? Since { get; init; }

    /// <summary><c>logs.read</c>: diese Schwere und alles Ernstere, 0 bis 7.</summary>
    public int? Priority { get; init; }

    /// <summary><c>logs.read</c>: Name einer Einheit von systemd.</summary>
    public string? Unit { get; init; }

    /// <summary><c>logs.read</c>: so viele Zeilen höchstens.</summary>
    public int? Lines { get; init; }

    /// <summary><c>exec.run</c>: der Befehl, wie er am Server laufen soll.</summary>
    public string? Command { get; init; }

    /// <summary><c>exec.run</c>: so viele Sekunden darf er laufen.</summary>
    public int? Seconds { get; init; }

    // tunnel.set: welcher Tunnel des Profils, und ob er laufen soll.
    public string? Tunnel { get; init; }

    public bool? On { get; init; }

    /// <summary><c>key.generate</c>: Passphrase für den neuen privaten Schlüssel; ohne sie bleibt er unverschlüsselt.</summary>
    public string? Passphrase { get; init; }

    /// <summary><c>key.generate</c>: Kommentar am öffentlichen Schlüssel.</summary>
    public string? Comment { get; init; }

    /// <summary><c>key.passphrase</c>: die neue Passphrase; ohne sie liegt der Schlüssel danach unverschlüsselt da.</summary>
    public string? NewPassphrase { get; init; }

    /// <summary><c>key.convert</c>: das Format der neuen Datei, <c>openssh</c> oder <c>ppk</c>.</summary>
    public string? Format { get; init; }

    /// <summary><c>key.install</c>: die Zeile des öffentlichen Schlüssels.</summary>
    public string? PublicKey { get; init; }

    /// <summary><c>transfer.start</c> (neuer Versuch): so viele Bytes hatte der abgebrochene übertragen.</summary>
    public long? Resume { get; init; }

    /// <summary><c>transfer.start</c>: mehr Bytes überträgt der Transfer nicht.</summary>
    public long? Limit { get; init; }

    /// <summary><c>transfer.limit</c>: Obergrenze in Bytes je Sekunde, 0 = keine.</summary>
    public long? BytesPerSecond { get; init; }

    /// <summary><c>keys.unlocked</c>: entsperrte Schlüssel merken oder nicht; ohne Angabe nur die gemerkten vergessen.</summary>
    public bool? Remember { get; init; }

    /// <summary><c>hostkeys.authority</c>: Muster der Server, für die eine Zertifizierungsstelle gilt.</summary>
    public string? Patterns { get; init; }

    /// <summary><c>hostkeys.authority</c>: öffentlicher Schlüssel der Stelle als Zeile.</summary>
    public string? Key { get; init; }

    /// <summary><c>hostkeys.forget</c>: Fingerabdruck der Zertifizierungsstelle, wenn eine entfernt wird.</summary>
    public string? Authority { get; init; }

    /// <summary><c>keepalive.set</c>: Sekunden zwischen zwei Lebenszeichen, 0 = keine.</summary>
    public int? Interval { get; init; }

    /// <summary><c>keepalive.set</c>: so viele Lebenszeichen dürfen unbeantwortet bleiben.</summary>
    public int? Max { get; init; }

    /// <summary><c>x11.display</c>: Nummer des Displays des lokalen X-Servers, 0 bis 63.</summary>
    public int? Display { get; init; }

    /// <summary><c>sftp.chown</c>: neuer Besitzer, als Name oder Nummer.</summary>
    public string? Owner { get; init; }

    /// <summary><c>sftp.chown</c>: neue Gruppe.</summary>
    public string? Group { get; init; }

    /// <summary><c>sftp.chmod</c>, <c>sftp.chown</c>: bei einem Ordner auch alles darin.</summary>
    public bool? Recursive { get; init; }

    /// <summary><c>monitor.set</c></summary>
    public bool? Enabled { get; init; }

    /// <summary><c>session.open</c>: Sitzung, deren Verbindung mitbenutzt werden soll.</summary>
    public ulong? Share { get; init; }

    /// <summary>session.open: den Aufbau der Verbindung ausführlich melden.</summary>
    public bool? Trace { get; init; }

    /// <summary><c>session.open</c> bei einem Profil mit Mosh: die Vorhersage beim Tippen (<c>always</c>, <c>never</c>); ohne Angabe gilt <c>adaptive</c>.</summary>
    public string? MoshPredict { get; init; }

    /// <summary><c>session.flow</c></summary>
    public bool? Paused { get; init; }

    /// <summary><c>sshconfig.import</c>: die Aliasse. <c>sftp.pack</c>: Namen der Einträge im Verzeichnis.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary><c>follow.start</c>, <c>follow.stop</c>: Nummer des Mitlesens.</summary>
    public ulong? Follow { get; init; }

    /// <summary><c>sftp.elevate</c>: das Passwort für <c>sudo</c>; wird nie gespeichert.</summary>
    public string? Password { get; init; }

    /// <summary><c>sftp.read</c>: ab hier, in Bytes.</summary>
    public long? Offset { get; init; }

    /// <summary><c>sftp.read</c>: höchstens so viele Bytes.</summary>
    public long? Length { get; init; }

    /// <summary><c>sftp.write</c>: der neue Inhalt als Base64.</summary>
    public string? Data { get; init; }

    /// <summary><c>sftp.checksum</c>: die Dateien, als absolute Pfade.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary><c>sftp.copy</c>: Quelle und Ziel je Eintrag.</summary>
    public IReadOnlyList<PairDto>? Pairs { get; init; }

    /// <summary><c>sftp.copy</c>: verschieben statt kopieren.</summary>
    public bool? Move { get; init; }

    /// <summary><c>sshconfig.import</c>: Die Profile bleiben mit ihrem Eintrag der Datei verknüpft.</summary>
    public bool? Linked { get; init; }

    /// <summary><c>transfer.retain</c>: die Transfers, die die App noch zeigt.</summary>
    public IReadOnlyList<TransferJobDto>? Jobs { get; init; }
}

/// <summary>Woran der Kern einen Transfer wiedererkennt – die Angaben aus <c>transfer.start</c> ohne Sitzung und Nummer.</summary>
internal sealed record TransferJobDto(string Direction, string Local, string Remote, string? Name = null);

internal sealed record ResponseDto(bool Ok, string? Error = null, string? Message = null)
{
    public List<ProfileDto>? Profiles { get; init; }

    public ProfileDto? Profile { get; init; }

    /// <summary><c>sshconfig.read</c></summary>
    public List<ConfigHostDto>? Hosts { get; init; }

    /// <summary><c>sshconfig.read</c>: Bedingungen hinter <c>Match</c>, die der Kern nicht prüft.</summary>
    public List<string>? Unchecked { get; init; }

    public string? Path { get; init; }

    /// <summary><c>key.generate</c></summary>
    public string? PublicKey { get; init; }

    /// <summary><c>key.list</c></summary>
    public List<KeyFileDto>? Keys { get; init; }

    /// <summary><c>hostkeys.list</c></summary>
    public List<KnownHostDto>? HostKeys { get; init; }

    /// <summary><c>sshconfig.import</c></summary>
    public int? Imported { get; init; }

    /// <summary><c>local.shells</c></summary>
    public List<LocalShellDto>? Shells { get; init; }
}

/// <summary>Eine Shell dieses Rechners, wie der Kern sie nennt (<c>protocol::LocalShell</c>) – ohne Pfad.</summary>
internal sealed record LocalShellDto(string? Id, string? Name);

internal sealed record ConfigHostDto(string? Alias, string? Host, int Port, string? User, string? KeyPath, string? Jump, bool ForwardAgent, bool Exists, bool IdentitiesOnly = false, List<TunnelDto>? Tunnels = null, List<string>? Skipped = null)
{
    public SshConfigHost ToHost() => new(
        Alias ?? string.Empty,
        Host ?? string.Empty,
        Port,
        User ?? string.Empty,
        KeyPath,
        Jump,
        ForwardAgent,
        Exists,
        IdentitiesOnly,
        Tunnels?.Count ?? 0,
        // Die Wörter stehen in einer fremden Datei: gesäubert, gekürzt, und nur eine Handvoll.
        Skipped is { Count: > 0 } ? [.. Skipped.Where(word => word is { Length: > 0 }).Take(24).Select(word => Format.Visible(word.Length > 40 ? word[..40] : word))] : null);
}

internal sealed record KnownHostDto(string? Name, string? Algorithm, string? Fingerprint, bool? Authority = null);

internal sealed record KeyFileDto(
    string? Path,
    string? Name,
    string? Format,
    string? Algorithm,
    int? Bits,
    string? Fingerprint,
    string? Comment,
    bool? Encrypted,
    string? PublicKey,
    bool? Unchecked = null);

internal sealed record MonitorInterfaceDto(string? Name, long Rx, long Tx, List<string?>? Addresses = null);

internal sealed record MonitorIoDiskDto(string? Name, long Read, long Write, int Busy);

internal sealed record MonitorDiskDto(string? Mount, string? Device, long Used, long Total, long? InodesUsed = null, long? InodesTotal = null);

internal sealed record LogLineDto(long Time, int? Priority, string? Source, string? Message);

internal sealed record MuxDto(string? Kind, string? Name, int? Windows, bool Attached, string? Command);

internal sealed record RunDto(int? Status, string? Stdout, string? Stderr, bool Cut, bool TimedOut);

internal sealed record PairDto(string From, string To);

internal sealed record UsageDto(string? Path, long Bytes);

internal sealed record FieldDto(string? Text, bool Echo);

/// <remarks>
/// <c>Target</c> und <c>Dangling</c> gibt es nur bei einem Link: das Ziel, wie es im Link steht (Text vom Server), und
/// ob es ins Leere zeigt. Fehlt beides, ist nichts bekannt – <c>Dangling</c> fehlend heißt nicht „zeigt auf etwas“.
/// </remarks>
internal sealed record EntryDto(string? Name, string? Kind, bool Link, long Size, long? Modified, int? Permissions = null, string? Owner = null, string? Group = null, string? Target = null, bool Dangling = false)
{
    public RemoteEntry ToEntry() => new(
        Name ?? string.Empty,
        Kind == "directory",
        Size,
        Modified is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue,
        Link,
        Permissions,
        Owner,
        Group,
        IsSpecial: Kind == "other",
        LinkTarget: Link ? Target : null,
        IsDangling: Link && Dangling);
}

internal sealed record TreeDto(string? Remote, string? Local, bool Directory, long Size, long? Modified)
{
    public RemoteTreeEntry ToEntry() => new(
        Remote ?? string.Empty,
        Local ?? string.Empty,
        Directory,
        Size,
        Modified is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue);
}

/// <summary>Ereignis des Kerns. <see cref="Event"/> nennt die Art; gefüllt sind nur deren Felder.</summary>
internal sealed record EventDto
{
    public string? Event { get; init; }

    public ulong? Session { get; init; }

    public string? State { get; init; }

    public string? Reason { get; init; }

    public string? Message { get; init; }

    public uint? ExitStatus { get; init; }

    /// <summary>Bei einem gescheiterten Aufbau: woran es lag (<c>name</c>, <c>refused</c>, <c>timeout</c> …).</summary>
    public string? Trouble { get; init; }

    // tunnel.state: Profil und Tunnel, um die es geht (der Zustand steht in State, der Grund in Message).
    public string? Profile { get; init; }

    public string? Tunnel { get; init; }

    // probe.result: der Bericht des Verbindungstests.
    public string? Step { get; init; }

    public List<string>? Addresses { get; init; }

    public string? Address { get; init; }

    public long? ConnectMs { get; init; }

    public string? Software { get; init; }

    public string? KeyAlgorithm { get; init; }

    public string? KeyState { get; init; }

    public List<string>? Methods { get; init; }

    public string? Home { get; init; }

    /// <summary>Beim Aufbau über Sprung-Server: der Server, um den es gerade geht.</summary>
    public string? Hop { get; init; }

    public ulong? Prompt { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Algorithm { get; init; }

    public string? Fingerprint { get; init; }

    public bool? Changed { get; init; }

    public string? KnownHostsFile { get; init; }

    public int? KnownHostsLine { get; init; }

    /// <summary>Bei geändertem Host-Schlüssel: Fingerabdruck des bisher hinterlegten.</summary>
    public string? PreviousFingerprint { get; init; }

    /// <summary>Bei geändertem Host-Schlüssel: Der alte Eintrag steht in der Datei des Benutzers, die die App nie ändert.</summary>
    public bool? KnownHostsForeign { get; init; }

    public string? Kind { get; init; }

    public string? User { get; init; }

    public string? KeyPath { get; init; }

    public string? Name { get; init; }

    public string? Instructions { get; init; }

    public List<FieldDto>? Fields { get; init; }

    public bool? CanSave { get; init; }

    public int? Attempt { get; init; }

    public string? Code { get; init; }

    public ulong? Request { get; init; }

    public bool? Ok { get; init; }

    public List<EntryDto>? Entries { get; init; }

    public List<TreeDto>? Tree { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.find</c>: Die Suche hörte nach so vielen angesehenen Einträgen auf.</summary>
    public long? Seen { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.readlink</c>: wohin der Link zeigt, wie er es nennt (nicht aufgelöst).</summary>
    public string? Target { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.size</c>: belegter Platz in Bytes.</summary>
    public long? Size { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.usage</c>: belegter Platz je Eintrag, das Verzeichnis selbst eingeschlossen.</summary>
    public List<UsageDto>? Usage { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.checksum</c>: SHA-256 je Datei, <c>null</c> wo es keine gab.</summary>
    public List<string?>? Sums { get; init; }

    /// <summary><c>sftp.result</c> zu <c>commands.list</c>: die Namen der Programme im Suchpfad.</summary>
    public List<string>? Commands { get; init; }

    /// <summary><c>sftp.result</c> zu <c>exec.run</c>: was der Befehl hinterlassen hat.</summary>
    public RunDto? Run { get; init; }

    /// <summary><c>sftp.result</c> zu <c>mux.list</c>: laufende Sitzungen von tmux und screen.</summary>
    public List<MuxDto>? Mux { get; init; }

    /// <summary><c>sftp.result</c> zu <c>commands.probe</c>: die Art der Shell (<c>posix</c> oder <c>windows</c>).</summary>
    public string? Line { get; init; }

    /// <summary><c>sftp.result</c> zu <c>logs.read</c>: die Zeilen, älteste zuerst.</summary>
    public List<LogLineDto?>? Logs { get; init; }

    /// <summary><c>sftp.result</c> zu <c>logs.read</c>: Das Konto sieht nur seine eigenen Einträge im Journal.</summary>
    public bool? Limited { get; init; }

    public string? Error { get; init; }

    public ulong? Transfer { get; init; }

    public long? Bytes { get; init; }

    public long? Total { get; init; }

    /// <summary><c>transfer.state</c>: So viel war von einem Versuch davor schon am Ziel und wurde nicht noch einmal übertragen.</summary>
    public long? Resumed { get; init; }

    /// <summary><c>transfer.state</c>: alle Dateien des Transfers.</summary>
    public long? Files { get; init; }

    /// <summary><c>transfer.state</c>: fertige Dateien, auch übersprungene.</summary>
    public long? FilesDone { get; init; }

    // monitor.stats
    public int? Cpu { get; init; }

    public long? MemoryUsed { get; init; }

    public long? MemoryTotal { get; init; }

    public long? MemoryCache { get; init; }

    public long? MemoryFree { get; init; }

    public long? SwapUsed { get; init; }

    public long? SwapTotal { get; init; }

    public long? DiskUsed { get; init; }

    public long? DiskTotal { get; init; }

    public string? Os { get; init; }

    public string? Kernel { get; init; }

    public long? Uptime { get; init; }

    public string? Load { get; init; }

    public long? NetRx { get; init; }

    public long? NetTx { get; init; }

    /// <summary><c>monitor.stats</c>: Durchsatz und Adressen je Netzwerkkarte.</summary>
    public List<MonitorInterfaceDto?>? Interfaces { get; init; }

    /// <summary><c>monitor.stats</c>: von allen Datenträgern gelesene und geschriebene Bytes je Sekunde; fehlt, wenn der Server sie nicht nennt.</summary>
    public long? IoRead { get; init; }

    public long? IoWrite { get; init; }

    /// <summary><c>monitor.stats</c>: wie beschäftigt der meistbeschäftigte Datenträger war, 0–100.</summary>
    public int? IoBusy { get; init; }

    /// <summary><c>monitor.stats</c>: Der Server nennt Lesen und Schreiben nur zusammen; die Summe steht in <c>IoRead</c>.</summary>
    public bool? IoMixed { get; init; }

    /// <summary><c>monitor.unavailable</c>: Der Server lieferte schon Werte und verstummte; der Kern versucht es nach einer Pause neu.</summary>
    public bool? Retrying { get; init; }

    /// <summary><c>monitor.stats</c>: der Durchsatz je Platte.</summary>
    public List<MonitorIoDiskDto?>? IoDisks { get; init; }

    /// <summary><c>monitor.stats</c>: Auslastung jedes Kerns, 0–100.</summary>
    public List<int>? Cores { get; init; }

    public int? CpuUser { get; init; }

    public int? CpuSystem { get; init; }

    public int? CpuIowait { get; init; }

    public int? CpuSteal { get; init; }

    /// <summary><c>monitor.stats</c>: die Dateisysteme des Servers.</summary>
    public List<MonitorDiskDto>? Disks { get; init; }

    // monitor.stats: was der Rechner ist, und seine Uhr
    public string? CpuModel { get; init; }

    public int? CoreCount { get; init; }

    public string? Machine { get; init; }

    public string? Virtualization { get; init; }

    public long? Time { get; init; }

    public int? UtcOffset { get; init; }

    public string? Zone { get; init; }

    /// <summary><c>session.mosh</c> (<c>silent</c>): seit so vielen Sekunden hört Mosh nichts vom Server.</summary>
    public long? SilentFor { get; init; }

    /// <summary><c>session.banner</c>; <c>follow.data</c>: was beim Mitlesen dazugekommen ist.</summary>
    public string? Text { get; init; }

    /// <summary><c>follow.data</c>, <c>follow.end</c>: Nummer des Mitlesens.</summary>
    public ulong? Follow { get; init; }

    /// <summary><c>sftp.result</c> zu <c>sftp.read</c>: das gelesene Stück als Base64; <c>Size</c> nennt dann die ganze Datei.</summary>
    public string? Data { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RequestDto))]
[JsonSerializable(typeof(ResponseDto))]
[JsonSerializable(typeof(EventDto))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

internal static class CoreProtocol
{
    public static string Serialize(RequestDto request) => JsonSerializer.Serialize(request, CoreJsonContext.Default.RequestDto);

    /// <summary>Antwort des Kerns; unlesbares JSON gilt als Fehler des Kerns, nicht als Absturzgrund.</summary>
    public static ResponseDto ParseResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.ResponseDto)
                ?? new ResponseDto(false, "bad_response", "empty response");
        }
        catch (JsonException e)
        {
            return new ResponseDto(false, "bad_response", e.Message);
        }
    }

    public static EventDto? ParseEvent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.EventDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
