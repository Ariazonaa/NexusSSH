using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Die Naht zwischen Oberfläche und Backend. Dahinter steckt <see cref="Backend.CoreBackend"/> (Rust-Kern) oder
/// <see cref="Mock.MockBackend"/> (Beispieldaten für Tests und <c>--demo</c>). ViewModels kennen nur diese
/// Schnittstellen. Ereignisse können auf einem beliebigen Thread ausgelöst werden.
/// </summary>
public interface INexusBackend
{
    IProfileStore Profiles { get; }

    ITransferQueue Transfers { get; }

    /// <summary>
    /// Bereitet eine Verbindung vor. Aufgebaut wird sie erst mit <see cref="ITerminalSession.Start"/>.
    /// <paramref name="share"/> nennt eine stehende Verbindung zum selben Profil, die mitbenutzt werden soll
    /// (geteiltes Terminal, duplizieren) – dann entfällt die Anmeldung, wenn der Server es zulässt.
    /// </summary>
    IServerConnection Connect(ServerProfile profile, IServerConnection? share = null);

    /// <summary>
    /// Verbindungstest für ein Profil, gespeichert oder nicht: Name auflösen, Port öffnen, Host-Schlüssel und
    /// Anmeldeverfahren ansehen – ohne sich anzumelden, ohne zu fragen, ohne etwas zu merken. Kommt immer mit einem
    /// Bericht zurück; wirft nur, wenn das Profil selbst nicht taugt (<see cref="BackendException"/>).
    /// </summary>
    Task<ProbeReport> ProbeAsync(ServerProfile profile, CancellationToken cancellation = default);

    /// <summary>
    /// Die Shells dieses Rechners, die ein lokales Terminal öffnen kann (<see cref="ServerProfile.IsLocal"/>). Unter
    /// Windows fragt der Kern dafür <c>wsl.exe</c> nach den Distributionen – das kann Sekunden dauern, deshalb
    /// asynchron. Ohne Pfade: Was gestartet wird, entscheidet der Kern aus der Kennung.
    /// </summary>
    Task<IReadOnlyList<LocalShell>> LocalShellsAsync(CancellationToken cancellation = default);

    /// <summary>Hinweise des Backends für das Protokoll (Reiter „Ausgabe“), englisch.</summary>
    event Action<string>? Warning;

    /// <summary>
    /// Das Display des X-Servers dieses Rechners für X11-Forwarding (0 = Port 6000, das Übliche bei VcXsrv, Xming
    /// und MobaXterm). Gilt für Verbindungen, die danach aufgebaut werden.
    /// </summary>
    void SetX11Display(int display);

    /// <summary>
    /// Lebenszeichen für Server, deren Profil nichts Eigenes nennt: alle wie viele Sekunden (0 = keine) und wie
    /// viele unbeantwortet bleiben dürfen. Gilt für Verbindungen, die danach aufgebaut werden.
    /// </summary>
    void SetKeepAlive(int seconds, int missed);

    /// <summary>
    /// Die Vorhersage beim Tippen für Terminals über Mosh: <c>adaptive</c> (die Vorgabe), <c>always</c> oder <c>never</c>
    /// (<see cref="Settings.AppSettings.MoshPredictions"/>). Gilt für Sitzungen, die danach beginnen. Ein Backend ohne
    /// Mosh lässt es dabei.
    /// </summary>
    void SetMoshPrediction(string prediction)
    {
    }

    /// <summary>
    /// Entsperrte Schlüssel (mit Passphrase) im Speicher behalten, bis die App endet – dann fragt nur die erste
    /// Verbindung nach der Passphrase. Abgeschaltet wird sofort alles vergessen. Nie auf der Platte.
    /// </summary>
    void RememberUnlockedKeys(bool remember);

    /// <summary>Vergisst die entsperrten Schlüssel; die nächste Verbindung fragt wieder nach der Passphrase.</summary>
    void ForgetUnlockedKeys();

    /// <summary>
    /// Startet einen Tunnel des Profils über diese Verbindung oder hält ihn an. Wie es ausging, meldet
    /// <see cref="TunnelChanged"/>; ohne stehende Verbindung geschieht nichts.
    /// </summary>
    void SetTunnel(IServerConnection connection, string tunnelId, bool on);

    /// <summary>Ein Tunnel steht, wurde angehalten oder kam nicht zustande. Von einem beliebigen Thread.</summary>
    event Action<TunnelNotice>? TunnelChanged;
}

public interface IProfileStore
{
    IReadOnlyList<ServerProfile> GetProfiles();

    /// <summary>Legt an (leere <see cref="ServerProfile.Id"/>) oder ersetzt. Wirft <see cref="BackendException"/>.</summary>
    ServerProfile Save(ServerProfile profile);

    /// <summary>Entfernt das Profil samt gespeicherter Geheimnisse. Wirft <see cref="BackendException"/>.</summary>
    void Delete(string id);

    /// <summary>
    /// Die Server, deren Host-Schlüssel die App sich gemerkt hat (ihre eigene <c>known_hosts</c>, nicht die von
    /// OpenSSH – die wird nur gelesen).
    /// </summary>
    IReadOnlyList<KnownHost> KnownHosts();

    /// <summary>
    /// Vergisst einen davon: Beim nächsten Verbinden wird wieder nach dem Schlüssel gefragt. <c>false</c>, wenn es
    /// den Eintrag nicht (mehr) gab.
    /// Mit <paramref name="authority"/> (dem Fingerabdruck) wird stattdessen die Zertifizierungsstelle mit diesen
    /// Mustern entfernt.
    /// </summary>
    bool ForgetHost(string name, string? authority = null);

    /// <summary>
    /// Trägt eine Zertifizierungsstelle für Host-Zertifikate ein: Server, auf die die Muster passen und die ein
    /// Zertifikat dieser Stelle zeigen, gelten danach ohne Rückfrage als vertraut. Nur auf ausdrücklichen Wunsch
    /// des Benutzers. Wirft <see cref="BackendException"/> (<c>invalid_patterns</c>, <c>invalid_key</c>).
    /// </summary>
    KnownHost TrustAuthority(string patterns, string key);

    /// <summary>Die Server aus <c>~/.ssh/config</c>, noch ohne etwas anzulegen.</summary>
    SshConfigListing ReadSshConfig();

    /// <summary>
    /// Legt für die genannten Einträge aus <c>~/.ssh/config</c> Profile an, samt ihrer Sprung-Server. Liefert die
    /// Zahl der angelegten Profile. Wirft <see cref="BackendException"/>.
    /// </summary>
    /// <param name="linked">Die Profile bleiben mit ihrem Eintrag verknüpft: Ihre Angaben werden bei jedem Start neu aus der Datei gelesen.</param>
    int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false);

    /// <summary>
    /// Liest die Angaben der mit <c>~/.ssh/config</c> verknüpften Profile neu aus der Datei und liefert, wie viele sich
    /// geändert haben. Ein Backend ohne solche Datei ändert nichts.
    /// </summary>
    int RefreshSshConfig() => 0;

    /// <summary>
    /// Legt in <c>~/.ssh</c> ein neues Schlüsselpaar an (<paramref name="name"/> und <c>name.pub</c>); vorhandene
    /// Dateien werden nie überschrieben. Mit <paramref name="passphrase"/> ist der private Schlüssel verschlüsselt.
    /// Wirft <see cref="BackendException"/>.
    /// </summary>
    GeneratedKey GenerateKey(string name, string? passphrase, string comment);

    /// <summary>
    /// Die eigenen Schlüsseldateien: was in <c>~/.ssh</c> liegt, dazu die genannten Dateien (Schlüssel aus Profilen,
    /// die woanders liegen). Gelesen wird ohne Passphrase; was kein privater Schlüssel ist, fehlt.
    /// </summary>
    IReadOnlyList<KeyFile> KeyFiles(IReadOnlyList<string> otherPaths) => [];

    /// <summary>
    /// Ändert die Passphrase einer Schlüsseldatei; ohne <paramref name="newPassphrase"/> liegt der Schlüssel danach
    /// unverschlüsselt da. Das Format bleibt (nur ein altes PEM-Format wird zu dem von OpenSSH). Die Passphrasen
    /// gehen nur an den Kern. Wirft <see cref="BackendException"/> – <c>wrong_passphrase</c>, wenn die alte nicht stimmt.
    /// </summary>
    void ChangeKeyPassphrase(string path, string? passphrase, string? newPassphrase)
        => throw new BackendException("unsupported", "this backend has no key files");

    /// <summary>
    /// Schreibt den Schlüssel in das andere Format, als neue Datei <paramref name="name"/> im selben Ordner und mit
    /// derselben Passphrase; die alte Datei bleibt. Liefert den Pfad der neuen. Nichts Vorhandenes wird
    /// überschrieben (<c>exists</c>). Wirft <see cref="BackendException"/>.
    /// </summary>
    string ConvertKey(string path, string? passphrase, KeyFormat format, string name)
        => throw new BackendException("unsupported", "this backend has no key files");

    /// <summary>Vergisst Passwort und Passphrase, die zu einem Profil gespeichert sind; das Profil bleibt.</summary>
    void ForgetSecrets(string id);

    event Action? Changed;
}

/// <summary>Das Backend hat eine Anfrage abgelehnt. <see cref="Code"/> ist maschinenlesbar, die Nachricht englisch.</summary>
public sealed class BackendException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum ConnectionState
{
    /// <summary>Noch nicht gestartet.</summary>
    Idle,
    Connecting,
    Authenticating,
    Connected,
    Closed,
}

public enum CloseReason
{
    None,
    User,
    Remote,
    ConnectFailed,
    HostKeyRejected,
    AuthFailed,
    ConnectionLost,
    UnknownProfile,
}

/// <summary>
/// Zustand einer Verbindung. <see cref="Detail"/> ist die englische Meldung des Backends zum Ende.
/// <see cref="Hop"/> nennt beim Aufbau über Sprung-Server den Server, um den es gerade geht (<c>user@host</c>).
/// <see cref="FilesOnly"/>: verbunden, aber ohne Shell – der Server lässt für das Konto keine zu (nur SFTP); die
/// Verbindung steht für Dateien.
/// <see cref="Trouble"/>: bei einem gescheiterten Aufbau die Kennung dessen, woran es lag (<see cref="ConnectTrouble"/>).
/// </summary>
public sealed record ConnectionStatus(
    ConnectionState State,
    CloseReason Reason = CloseReason.None,
    string? Detail = null,
    int? ExitStatus = null,
    string? Hop = null,
    bool FilesOnly = false,
    string? Trouble = null);

/// <summary>Wie es dem Terminal über Mosh geht: <paramref name="SilentSeconds"/> Sekunden ohne Antwort des Servers (bei <paramref name="Silent"/>).</summary>
public sealed record MoshStatus(bool Silent, int SilentSeconds = 0);

/// <summary>Eine Verbindung zu einem Server. <see cref="IDisposable.Dispose"/> beendet sie.</summary>
public interface IServerConnection : IDisposable
{
    ITerminalSession Terminal { get; }

    /// <summary>Dateisystem des Servers; <c>null</c>, solange das Backend kein SFTP kann.</summary>
    IRemoteFileSystem? Files { get; }

    /// <summary>Auslastung des Servers; <c>null</c>, solange das Backend sie nicht liefert.</summary>
    IServerMonitor? Monitor { get; }

    ConnectionStatus Status { get; }

    event Action<ConnectionStatus>? StatusChanged;

    /// <summary>Das Backend braucht eine Entscheidung oder Eingabe des Benutzers.</summary>
    event Action<ConnectionPrompt>? PromptRequested;

    /// <summary>Text, den der Server vor der Anmeldung zeigt (Banner). Schon auf druckbare Zeichen beschränkt.</summary>
    event Action<string>? Banner;

    /// <summary>
    /// Den Aufbau dieser Verbindung ausführlich melden (<see cref="Traced"/>). Vor dem Start des Terminals zu
    /// setzen. Ein Backend, das nichts zu melden hat, lässt es dabei.
    /// </summary>
    bool Trace
    {
        get => false;
        set { }
    }

    /// <summary>
    /// Eine Zeile über den Aufbau der Verbindung, englisch: was ausgehandelt wurde, welche Anmeldung versucht wird.
    /// Nie ein Geheimnis.
    /// </summary>
    event Action<string>? Traced
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Wie es dem Terminal über Mosh geht; <c>null</c>, solange das Backend nichts von Mosh gesagt hat – das Terminal
    /// läuft dann über SSH (auch bei einem Profil mit Mosh, wenn es nicht ging).
    /// </summary>
    MoshStatus? Mosh => null;

    /// <summary>
    /// Mosh läuft (wieder) oder hört seit einer Weile nichts vom Server. Ein Backend ohne Mosh meldet nie etwas.
    /// </summary>
    event Action<MoshStatus>? MoshChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Unter einem Mosh-Terminal ist die SSH-Verbindung weg: <see cref="Files"/> und <see cref="Monitor"/> sind
    /// <c>null</c>, bis sie von selbst oder auf <see cref="Relink"/> wiederkommt. Das Terminal läuft weiter.
    /// </summary>
    bool IsLinkLost => false;

    /// <summary>Warum der letzte Versuch, die SSH-Verbindung neu aufzubauen, scheiterte (englisch, Text des Kerns); sonst <c>null</c>.</summary>
    string? LinkLostDetail => null;

    /// <summary>
    /// Die SSH-Verbindung unter dem Mosh-Terminal ging verloren oder steht wieder – <see cref="Files"/> und
    /// <see cref="Monitor"/> sind dann schon gesetzt. Auch ein neuer Versuch, der scheiterte (<see cref="LinkLostDetail"/>).
    /// </summary>
    event Action? LinkChanged
    {
        add { }
        remove { }
    }

    /// <summary>Die SSH-Verbindung unter dem Mosh-Terminal jetzt neu aufbauen, statt auf den nächsten Versuch zu warten.</summary>
    void Relink()
    {
    }
}

/// <summary>Rückfrage während des Verbindungsaufbaus. Beantwortet wird sie über den mitgelieferten Rückruf.</summary>
public abstract record ConnectionPrompt
{
    private ConnectionPrompt()
    {
    }

    /// <summary>
    /// Soll dem Host-Schlüssel vertraut werden? <see cref="Changed"/> heißt: Für den Server ist ein anderer
    /// Schlüssel hinterlegt – möglicher Angriff. Dann nennt <see cref="PreviousFingerprint"/> den bisherigen zum
    /// Vergleich, und <see cref="KnownHostsForeign"/> sagt, dass der alte Eintrag in <c>~/.ssh/known_hosts</c> des
    /// Benutzers steht: Die App ändert diese Datei nie, der Eintrag bleibt dort also stehen.
    /// </summary>
    public sealed record HostKey(
        string Host,
        int Port,
        string Algorithm,
        string Fingerprint,
        bool Changed,
        string? KnownHostsFile,
        int? KnownHostsLine,
        Action<bool> Answer,
        string? PreviousFingerprint = null,
        bool KnownHostsForeign = false) : ConnectionPrompt;

    /// <summary>Passwort, Passphrase oder Fragen des Servers. Antwort <c>null</c> = abgebrochen.</summary>
    public sealed record Secret(
        SecretKind Kind,
        string User,
        string Host,
        string? KeyPath,
        string? Name,
        string? Instructions,
        IReadOnlyList<SecretField> Fields,
        bool CanSave,
        int Attempt,
        Action<SecretAnswer?> Answer) : ConnectionPrompt;
}

public enum SecretKind
{
    Password,
    Passphrase,
    KeyboardInteractive,
}

/// <summary>Ein Eingabefeld. <see cref="Text"/> ist nur bei Fragen des Servers gefüllt.</summary>
public sealed record SecretField(string Text, bool Echo);

public sealed record SecretAnswer(IReadOnlyList<string> Values, bool Save);

/// <summary>Wie ein Server die Befehle des Kerns ausführt (<see cref="IServerMonitor.CommandShellAsync"/>).</summary>
public enum CommandShell
{
    /// <summary>Gerade nicht zu sagen (Frist, Verbindung weg) – dann bleibt angeboten, was es gibt.</summary>
    Unknown,

    /// <summary><c>sh</c> wie gedacht: alles.</summary>
    Posix,

    /// <summary>OpenSSH unter Windows, Befehle als PowerShell: Auslastung, Prozesse, Prüfsumme, Belegung, Kopieren, Packen.</summary>
    Windows,

    /// <summary>Keine Befehle.</summary>
    None,
}

/// <summary>Zeigt Rückfragen an. Die App setzt Dialoge ein, Tests eine Attrappe.</summary>
public interface IUserPrompts
{
    Task<bool> ConfirmHostKeyAsync(ConnectionPrompt.HostKey prompt, CancellationToken cancellation);

    Task<SecretAnswer?> AskSecretAsync(ConnectionPrompt.Secret prompt, CancellationToken cancellation);

    /// <summary>
    /// Eine Frage, die nur „ja“ oder „nein“ kennt. Wer nicht fragen kann, antwortet mit „nein“ – nicht gefragt heißt
    /// nie zugestimmt.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string text, string confirm) => Task.FromResult(false);

    /// <summary>
    /// Fragt, ob ein Befehl eines KI-Programms laufen darf, und zeigt ihn vollständig; <paramref name="inTerminal"/> sagt,
    /// ob er in ein Terminal getippt wird. Wer nicht fragen kann, antwortet mit „nein“; fällt
    /// <paramref name="cancellation"/>, schließt die Frage.
    /// </summary>
    Task<bool> ConfirmCommandAsync(string server, string command, bool inTerminal, CancellationToken cancellation) => Task.FromResult(false);
}

/// <summary>
/// Terminal-Kanal. Ein- und Ausgabe sind rohe Bytes: SSH liefert Bytes, und ein UTF-8-Zeichen kann über zwei
/// Blöcke verteilt ankommen – das Zusammensetzen übernimmt xterm.js.
/// </summary>
public interface ITerminalSession
{
    /// <summary>Ausgabe des Servers. Der Puffer gilt nur während des Aufrufs.</summary>
    event Action<ReadOnlyMemory<byte>>? Output;

    /// <summary>Baut die Verbindung auf und startet die Shell. Ein zweiter Aufruf tut nichts.</summary>
    void Start();

    /// <summary>Eingabe des Benutzers (Tasten, eingefügter Text).</summary>
    void Write(ReadOnlyMemory<byte> input);

    /// <summary>Größe des Terminals in Zeichen. Vor <see cref="Start"/> aufrufen, damit die Shell sie von Beginn an kennt.</summary>
    void Resize(int columns, int rows);

    /// <summary>
    /// Hält die Ausgabe an oder lässt sie weiterlaufen. Das Terminal ruft das, wenn es mit dem Zeichnen nicht
    /// nachkommt; der Rückstau bremst dann den Server.
    /// </summary>
    void SetPaused(bool paused);
}

/// <summary>
/// Dateien des Servers (SFTP). Alle Operationen werfen <see cref="RemoteFileException"/>, wenn der Server sie
/// ablehnt. Up- und Downloads laufen nicht hier, sondern landen in der <see cref="ITransferQueue"/>.
/// </summary>
public interface IRemoteFileSystem
{
    /// <summary>Verzeichnis, in dem der Browser startet.</summary>
    string InitialPath { get; }

    /// <summary>Inhalt eines Verzeichnisses, Ordner zuerst.</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path);

    /// <summary>Legt eine leere Datei an. Gibt es den Namen schon, scheitert es – nichts wird überschrieben.</summary>
    Task CreateFileAsync(string path);

    /// <summary>Legt den symbolischen Link <paramref name="path"/> an, der auf <paramref name="target"/> zeigt. Nichts Vorhandenes wird ersetzt, und das Ziel muss es geben.</summary>
    Task CreateLinkAsync(string path, string target);

    /// <summary>
    /// Wohin der symbolische Link <paramref name="path"/> zeigt, so wie es im Link steht (relativ oder absolut,
    /// nicht aufgelöst; Text vom Server). Die Liste nennt es meist selbst (<see cref="RemoteEntry.LinkTarget"/>) –
    /// das hier fragt ein einzelnes Ziel nach, wo sie es nicht tut (Dateizugriff über Befehle). Wirft
    /// <see cref="RemoteFileException"/>, wenn der Pfad kein Link ist oder der Server das Ziel nicht nennt (DSM).
    /// </summary>
    Task<string?> ReadLinkAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromException<string?>(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>Benennt um oder verschiebt. Ein vorhandenes Ziel wird nie überschrieben.</summary>
    Task RenameAsync(string from, string to);

    /// <summary>Löscht eine Datei oder ein Verzeichnis samt Inhalt.</summary>
    Task DeleteAsync(string path);

    /// <summary>
    /// Setzt die Zugriffsrechte wie <c>chmod</c> (<paramref name="mode"/> z. B. <c>0644</c> oktal). Mit
    /// <paramref name="recursive"/> bei einem Ordner auch für alles darin; Ordner bleiben dabei betretbar.
    /// </summary>
    Task SetPermissionsAsync(string path, int mode, bool recursive = false);

    /// <summary>
    /// Setzt Besitzer und Gruppe wie <c>chown</c>, als Name oder Nummer; <c>null</c> lässt den Wert, wie er ist.
    /// </summary>
    Task SetOwnerAsync(string path, string? owner, string? group, bool recursive = false);

    /// <summary>
    /// Öffnet den Kanal für Dateien neu – wenn der alte nicht mehr antwortet, die Verbindung aber steht. Wirft
    /// <see cref="RemoteFileException"/>, wenn der Server keinen neuen zulässt.
    /// </summary>
    Task ReopenAsync();

    /// <summary>
    /// Was <see cref="Download"/> von <paramref name="path"/> anlegen würde, ohne etwas zu laden: erst die
    /// Verzeichnisse – jedes vor denen, die in ihm liegen –, dann die Dateien, mit den Namen, unter denen sie auf
    /// diesem Rechner ankämen. Eine einzelne Datei ergibt einen Eintrag. Wirft <see cref="RemoteFileException"/>
    /// mit <c>too_many</c>, wenn es mehr sind, als sich sinnvoll vorab nennen lassen.
    /// </summary>
    /// <param name="exclude">Muster, die ausgelassen werden (<see cref="TransferExcludes"/>) – ein ausgelassener Ordner wird nicht gelesen.</param>
    /// <param name="excluded">Statt des Baums das liefern, was die Muster auslassen würden: jeweils der oberste Eintrag.</param>
    Task<IReadOnlyList<RemoteTreeEntry>> TreeAsync(string path, IReadOnlyList<string>? exclude = null, bool excluded = false);

    /// <summary>
    /// Sucht unter einem Verzeichnis nach Einträgen, deren Name den Text enthält (ohne Rücksicht auf Groß- und
    /// Kleinschreibung); nahe Treffer zuerst, nicht mehr als das Backend auf einmal liefert. Das Ergebnis sagt
    /// auch, ob die Suche vorzeitig aufhörte – dann kann es weitere Treffer geben.
    /// </summary>
    Task<FoundEntries> FindAsync(string path, string name);

    /// <summary>
    /// Packt Einträge eines Verzeichnisses dort zu einem Archiv (<c>tar</c>, gzip) – auf dem Server, ohne den Umweg
    /// über diesen Rechner. Braucht ein Konto, das Befehle ausführen darf; ein vorhandenes Archiv bleibt.
    /// </summary>
    Task PackAsync(string directory, IReadOnlyList<string> names, string archive);

    /// <summary>Entpackt ein Archiv (<c>tar</c> oder <c>zip</c>) in sein Verzeichnis, ohne Vorhandenes zu überschreiben.</summary>
    Task UnpackAsync(string directory, string archive);

    /// <summary>
    /// Wie viel Platz ein Verzeichnis samt allem darin belegt, in Bytes (<c>du</c> auf dem Server) – <c>null</c>,
    /// wenn der Server keine Zahl nennt. Braucht ein Konto, das Befehle ausführen darf.
    /// </summary>
    Task<long?> FolderSizeAsync(string path);

    /// <summary>
    /// Was direkt unter einem Verzeichnis wie viel Platz belegt (<c>du</c> auf dem Server), das Verzeichnis selbst
    /// eingeschlossen. Braucht ein Konto, das Befehle ausführen darf.
    /// </summary>
    Task<IReadOnlyList<FolderUsage>> UsageAsync(string path)
        => Task.FromException<IReadOnlyList<FolderUsage>>(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>
    /// SHA-256 von Dateien auf dem Server, als Hex in Kleinbuchstaben – je Datei eine, in der Reihenfolge der
    /// Anfrage; <c>null</c>, wo der Server keine nennen konnte. Braucht ein Konto, das Befehle ausführen darf.
    /// </summary>
    Task<IReadOnlyList<string?>> ChecksumsAsync(IReadOnlyList<string> paths)
        => Task.FromException<IReadOnlyList<string?>>(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>
    /// Stellt den Dateizugriff dieser Sitzung auf root um (<c>sudo</c> auf dem Server). Ohne
    /// <paramref name="password"/> gelingt das nur, wenn <c>sudo</c> keines verlangt – sonst scheitert es mit
    /// <c>sudo_password</c>, und der Aufrufer fragt den Benutzer. Das Passwort wird nicht gespeichert.
    /// <see cref="ReopenAsync"/> stellt zurück.
    /// </summary>
    Task ElevateAsync(string? password = null)
        => Task.FromException(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>Ob der Dateizugriff gerade mit den Rechten von root läuft.</summary>
    bool IsElevated => false;

    /// <summary>
    /// Liest ein Stück einer Datei, ohne sie zu übertragen: ab <paramref name="offset"/> höchstens
    /// <paramref name="length"/> Bytes (das Backend liefert nie mehr als <see cref="FilePart.MaxLength"/> auf
    /// einmal) – und nennt dazu die Größe der ganzen Datei. Für Vorschau und Editor.
    /// </summary>
    Task<FilePart> ReadAsync(string path, long offset, int length)
        => Task.FromException<FilePart>(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>Schreibt eine kleine Datei ganz neu (Editor): anlegen oder leeren, dann der Inhalt.</summary>
    Task WriteAsync(string path, ReadOnlyMemory<byte> content)
        => Task.FromException(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>
    /// Liest eine Datei des Servers mit (<c>tail -f</c>): erst ihr Ende, dann alles, was dazukommt – bis das
    /// Ergebnis fallen gelassen wird oder der Server es beendet. <paramref name="text"/> und
    /// <paramref name="ended"/> kommen von einem beliebigen Thread; <paramref name="ended"/> nennt den Grund, wenn
    /// es nicht auf Wunsch endete. <c>null</c>, wenn das Backend das nicht kann.
    /// </summary>
    IDisposable? Follow(string path, Action<string> text, Action<string?> ended) => null;

    /// <summary>
    /// Kopiert oder verschiebt Einträge auf dem Server selbst (<c>cp</c>, <c>mv</c>): je Eintrag Quelle und Ziel
    /// als volle Pfade. Ob am Ziel schon etwas liegt, klärt der Aufrufer vorher. Braucht ein Konto, das Befehle
    /// ausführen darf.
    /// </summary>
    Task CopyAsync(IReadOnlyList<(string From, string To)> pairs, bool move = false)
        => Task.FromException(new RemoteFileException("not supported here", "unsupported"));

    /// <summary>
    /// Stellt einen Upload in die Warteschlange: lokale Datei oder lokaler Ordner in ein Verzeichnis des Servers.
    /// Liefert die Kennung des Transfers (<see cref="TransferItem.Id"/>). <paramref name="remoteName"/> nennt den
    /// Namen im Zielverzeichnis, wenn die Datei dort anders heißen soll als lokal.
    /// </summary>
    /// <param name="keep">Welche vorhandenen Dateien am Server bleiben, statt überschrieben zu werden.</param>
    /// <param name="exclude">Bei einem Ordner: Muster, die ausgelassen werden (<see cref="TransferExcludes"/>).</param>
    string Upload(string localPath, string remoteDirectory, string? remoteName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null);

    /// <summary>Stellt einen Download in die Warteschlange: Datei oder Ordner des Servers in einen lokalen Ordner.</summary>
    /// <param name="limit">
    /// Mehr Bytes lädt der Transfer nicht – für Downloads, deren Größe vorher an der Liste geprüft wurde
    /// (Bearbeiten, Ansehen): Die Liste nennt, was der Server behauptet; geladen würde sonst, bis er aufhört.
    /// </param>
    /// <param name="keep">Welche vorhandenen Dateien hier bleiben, statt überschrieben zu werden.</param>
    /// <param name="localName">Der Name im lokalen Ordner, wenn er nicht der vom Server sein soll.</param>
    /// <param name="exclude">Bei einem Ordner: Muster, die ausgelassen werden (<see cref="TransferExcludes"/>).</param>
    string Download(string remotePath, string localDirectory, long? limit = null, TransferKeep keep = TransferKeep.None, string? localName = null, IReadOnlyList<string>? exclude = null);

    /// <summary>
    /// Schickt eine Datei oder einen Ordner dieses Servers direkt in ein Verzeichnis eines anderen – gelesen und
    /// geschrieben in einem Zug, ohne die Platte dieses Rechners. Liefert die Kennung des Transfers, oder
    /// <c>null</c>, wenn das zwischen diesen beiden nicht geht (dann bleibt der Umweg über einen Zwischenordner).
    /// </summary>
    /// <param name="targetName">Der Name im Zielverzeichnis, wenn er nicht der der Quelle sein soll.</param>
    /// <param name="keep">Welche vorhandenen Dateien am Ziel bleiben, statt überschrieben zu werden.</param>
    /// <param name="exclude">Bei einem Ordner: Muster, die ausgelassen werden (<see cref="TransferExcludes"/>).</param>
    string? SendTo(string remotePath, IRemoteFileSystem target, string targetDirectory, string? targetName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null);

    /// <summary>
    /// Ein Abgleich als ein einziger Transfer: aus dem lokalen Ordner <paramref name="localFolder"/> nur die
    /// genannten Dateien (Pfade darunter, mit <c>/</c>) in den Ordner <paramref name="remoteFolder"/> des Servers.
    /// <c>null</c>, wenn das Backend das so nicht kann – dann überträgt der Aufrufer Datei für Datei.
    /// </summary>
    string? UploadSome(string localFolder, string remoteFolder, IReadOnlyList<string> files);

    /// <summary>Wie <see cref="UploadSome"/>, in die andere Richtung; die Pfade gelten unterhalb des Ordners des Servers.</summary>
    string? DownloadSome(string remoteFolder, string localFolder, IReadOnlyList<string> files);

    /// <summary>
    /// Wohin <see cref="Download"/> schriebe: der volle lokale Pfad der Datei oder des Ordners, der entstünde. Der
    /// Name kommt vom Server und muss hier nicht derselbe sein (<c>a:b</c> wird unter Windows zu <c>a_b</c>);
    /// <c>null</c>, wenn sich aus ihm kein lokaler Name machen lässt.
    /// </summary>
    string? LocalTarget(string remotePath, string localDirectory);
}

/// <summary>Der Server hat eine Dateioperation abgelehnt. <see cref="Code"/> ist maschinenlesbar, die Nachricht englisch.</summary>
public sealed class RemoteFileException(string message, string code = "failed") : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Auslastung eines Servers. Gemessen wird nur, solange jemand hinsieht (<see cref="SetActive"/>) – auf dem
/// Server läuft dafür eine kleine Schleife.
/// </summary>
public interface IServerMonitor
{
    /// <summary>Die letzte Messung; <c>null</c>, solange es keine gibt.</summary>
    ServerStats? Current { get; }

    /// <summary>Der Server liefert keine Werte (kein Linux, oder er lässt keine Befehle zu).</summary>
    bool IsUnavailable { get; }

    /// <summary>
    /// Zu <see cref="IsUnavailable"/>: Der Server lieferte schon Werte und verstummte (ein hängendes Dateisystem); die
    /// Messung beginnt nach einer Pause von selbst neu.
    /// </summary>
    bool IsRetrying => false;

    /// <summary>Zu <see cref="IsUnavailable"/>: warum, wie der Kern es nennt (englisch); <c>null</c>, wenn er nichts sagt.</summary>
    string? UnavailableDetail => null;

    /// <summary><see cref="Current"/> oder <see cref="IsUnavailable"/> hat sich geändert. Beliebiger Thread.</summary>
    event Action? Changed;

    /// <summary>
    /// Die letzten Zeilen eines Protokolls: das Journal von systemd oder eine Textdatei (<see cref="LogQuery"/>).
    /// Wirft <see cref="RemoteFileException"/>, wenn der Server für das Konto keine Befehle ausführt, kein Journal
    /// hat (<c>no_journal</c>) oder die Datei nicht lesen lässt.
    /// </summary>
    Task<ServerLog> LogsAsync(LogQuery query) => Task.FromException<ServerLog>(new RemoteFileException("not supported", "no_exec"));

    /// <summary>
    /// Laufende Sitzungen von tmux und screen auf dem Server, jede mit der Zeile, die daran anhängt. Leer, wenn
    /// nichts läuft oder das Backend es nicht kann; wirft <see cref="RemoteFileException"/>, wenn der Server für
    /// das Konto keine Befehle ausführt.
    /// </summary>
    Task<IReadOnlyList<MuxSession>> MuxSessionsAsync() => Task.FromResult<IReadOnlyList<MuxSession>>([]);

    /// <summary>
    /// Die Namen der Programme im Suchpfad des Kontos, für das Ergänzen beim Tippen – ein fester Befehl am Server.
    /// Leer, wenn das Backend es nicht kann; wirft <see cref="RemoteFileException"/>, wenn der Server für das
    /// Konto keine Befehle ausführt.
    /// </summary>
    Task<IReadOnlyList<string>> CommandNamesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Wie der Server für dieses Konto Befehle ausführt: POSIX, PowerShell (OpenSSH unter Windows – dort gibt es nur
    /// einen Teil der Befehle) oder gar nicht (das Konto darf keine, oder seine Shell liest keines von beiden).
    /// Lässt es sich gerade nicht sagen, gilt <see cref="CommandShell.Unknown"/>. Wirft nicht.
    /// </summary>
    Task<CommandShell> CommandShellAsync() => Task.FromResult(CommandShell.Posix);

    /// <summary>Die Anmelde-Shell des Kontos, wie der Server sie nennt (<c>/bin/bash</c>), unter Windows <c>windows</c>.</summary>
    Task<string> LoginShellAsync() => Task.FromResult(string.Empty);

    /// <summary>
    /// Führt einen Befehl aus, wie er dasteht – der einzige Weg, auf dem eine Befehlszeile aus der App kommt. Nur für das
    /// Werkzeug <c>run_command</c>, und nur, nachdem der Benutzer genau diesen Text freigegeben hat. Ein Rückgabewert
    /// ungleich 0 ist ein Ergebnis, kein Fehler. Wirft <see cref="RemoteFileException"/>, wenn der Befehl nicht lief.
    /// </summary>
    Task<CommandRun> RunCommandAsync(string command, int seconds, CancellationToken cancellation)
        => Task.FromException<CommandRun>(new RemoteFileException("not supported", "no_exec"));

    /// <summary>
    /// Trägt einen öffentlichen Schlüssel in <c>~/.ssh/authorized_keys</c> des angemeldeten Benutzers ein (wie
    /// <c>ssh-copy-id</c>); steht er schon dort, geschieht nichts. Wirft <see cref="RemoteFileException"/>.
    /// </summary>
    Task InstallKeyAsync(string publicKey);

    /// <summary>Schaltet das Messen ein oder aus.</summary>
    void SetActive(bool active);
}

/// <summary>Alle Up- und Downloads aller Sitzungen (Spec §7).</summary>
public interface ITransferQueue
{
    IReadOnlyList<TransferItem> Items { get; }

    event Action? Changed;

    /// <summary>Bricht einen wartenden oder laufenden Transfer ab.</summary>
    void Cancel(string id);

    /// <summary>Nimmt alle beendeten Transfers aus der Liste.</summary>
    void ClearFinished();

    /// <summary>
    /// Startet einen gescheiterten oder abgebrochenen Transfer noch einmal. <c>false</c>, wenn es ihn nicht gibt
    /// oder er noch läuft.
    /// </summary>
    bool Retry(string id);

    /// <summary>
    /// Die App wird beendet: Laufende und wartende Transfers enden, als wäre die Verbindung abgerissen, und lassen
    /// sich nach dem nächsten Start fortsetzen. Vor dem Schließen der Sitzungen rufen – das allein wäre ein Abbruch.
    /// </summary>
    void Suspend();

    /// <summary>
    /// Hält alles an, was läuft oder wartet: Die Transfers enden wie bei einer abgerissenen Verbindung, behalten,
    /// was schon übertragen ist, und stehen danach als angehalten in der Liste (<see cref="PausedDetail"/>) –
    /// <see cref="Retry"/> setzt jeden dort fort.
    /// </summary>
    void PauseAll();

    /// <summary>
    /// Hält einen einzelnen Transfer an, ob er läuft oder wartet – wie <see cref="PauseAll"/>, nur für diesen.
    /// <see cref="Retry"/> setzt ihn fort; er reiht sich dann hinten ein.
    /// </summary>
    void Pause(string id);

    /// <summary>
    /// Ändert, wann ein wartender Transfer an die Reihe kommt. <c>false</c>, wenn er nicht (mehr) wartet oder
    /// schon dort steht.
    /// </summary>
    bool Move(string id, QueueMove move);

    /// <summary>Obergrenze für die Bandbreite aller Transfers zusammen, in Bytes je Sekunde; 0 = keine. Gilt sofort.</summary>
    void SetLimit(long bytesPerSecond);

    /// <summary>
    /// Woher und wohin ein Transfer der Liste geht – für das Transfer-Protokoll. <c>null</c>, wenn die
    /// Warteschlange es nicht weiß (Beispieldaten).
    /// </summary>
    TransferPaths? PathsOf(string id) => null;

    /// <summary>Grund eines Transfers, den der Benutzer angehalten hat (<see cref="FailureText"/>).</summary>
    public const string PausedDetail = "paused: ";
}

/// <summary>Wohin ein wartender Transfer in der Warteschlange rückt.</summary>
public enum QueueMove
{
    /// <summary>Vor alle anderen, die warten.</summary>
    First,

    /// <summary>Einen Platz nach vorn.</summary>
    Up,

    /// <summary>Einen Platz nach hinten.</summary>
    Down,
}

/// <summary>Was eine Suche auf dem Server fand.</summary>
/// <param name="StoppedAfter">
/// Die Suche hörte auf, nachdem sie so viele Einträge angesehen hatte – <c>null</c>, wenn sie alles durchsucht hat.
/// </param>
public sealed record FoundEntries(IReadOnlyList<RemoteTreeEntry> Hits, long? StoppedAfter = null);

/// <summary>Bringt Aufrufe aus Hintergrund-Threads auf den UI-Thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
