using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Wie eine Sitzung nach einem Abriss von selbst neu verbindet: ob es eingeschaltet ist, und wer „später“ ausführt
/// (in der App ein Timer, in Tests von Hand).
/// </summary>
/// <param name="Now">Die Uhr, nach der der Reiter die Zeit bis zum nächsten Versuch nennt; ohne Angabe die des Rechners.</param>
public sealed record SessionRetry(Func<bool> Enabled, Func<TimeSpan, Action, IDisposable> Schedule, Func<DateTimeOffset>? Now = null)
{
    /// <summary>Wartezeiten vor den Versuchen, der Reihe nach. Danach gibt die Sitzung auf.</summary>
    public static IReadOnlyList<TimeSpan> Delays { get; } =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
    ];
}

/// <summary>
/// Eine Sitzung = ein Terminal-Tab mit seinem Dateibrowser und seinem Inspector. Die Sitzung überlebt ihre
/// Verbindung: Nach einem Abriss bleibt der Tab samt Terminal-Inhalt stehen und <see cref="Reconnect"/> hängt
/// eine neue Verbindung daran.
/// </summary>
public sealed partial class SessionViewModel : ObservableObject, ITerminalSession, IDisposable
{
    private readonly INexusBackend _backend;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserPrompts _prompts;
    private readonly Func<SessionViewModel, IServerConnection?>? _share;
    private IServerConnection _connection;
    private CancellationTokenSource _promptCancellation = new();
    private int _columns = 80;
    private int _rows = 24;
    private readonly Func<SessionViewModel, SessionViewModel?>? _leader;
    private SessionViewModel? _waitingFor;
    private bool _started;
    private bool _disposed;
    private readonly SessionRetry? _retry;

    // Für einen Tab, der keine eigene Verbindung zum Server hat (ein Fenster von tmux im Steuermodus): woher
    // seine „Verbindung“ kommt.
    private readonly Func<IServerConnection>? _connect;

    /// <summary>
    /// Der Tab zeigt etwas, das über die Verbindung eines anderen Tabs läuft (ein Fenster von tmux). Er verbindet
    /// sich nicht selbst neu und wird nicht für den nächsten Start gemerkt.
    /// </summary>
    public bool IsVirtual => _connect is not null;

    /// <summary>
    /// Was der Server schickt, geht erst hier durch: Der Empfänger reicht weiter, was für das Terminal ist. Für den
    /// Steuermodus von tmux, dessen Zeilen kein Text für den Bildschirm sind.
    /// </summary>
    internal Action<ReadOnlyMemory<byte>, Action<ReadOnlyMemory<byte>>>? OutputTap { get; set; }

    /// <summary>Solange das <c>false</c> liefert, geht nichts Getipptes an den Server.</summary>
    internal Func<bool>? InputGate { get; set; }

    /// <summary>Schreibt an der Sperre für Getipptes vorbei an den Server – für die Befehle des Steuermodus.</summary>
    internal void WriteRaw(ReadOnlyMemory<byte> input)
    {
        if (State == ConnectionState.Connected && !IsFilesOnly)
        {
            _connection.Terminal.Write(input);
        }
    }
    private IDisposable? _pendingRetry;
    private int _attempt;

    /// <summary>Die Sitzung versucht gerade von selbst, eine abgerissene Verbindung wieder aufzubauen.</summary>
    public bool IsRetrying => _attempt > 0;

    private DateTimeOffset? _retryDue;

    /// <summary>
    /// Was der Reiter über einen selbsttätigen Versuch sagt: „2. Versuch in 8 s“, solange gewartet wird, „2. Versuch …“,
    /// während er läuft; leer, wenn die Sitzung nichts von selbst versucht. Die Sekunden zählt <see cref="RefreshRetry"/>
    /// herunter – die Sitzung hat dafür keine eigene Uhr.
    /// </summary>
    [ObservableProperty]
    public partial string RetryText { get; private set; } = string.Empty;

    /// <summary>Stellt <see cref="RetryText"/> auf den Stand von jetzt; die Oberfläche ruft es im Sekundentakt.</summary>
    public void RefreshRetry() => RefreshRetry((_retry?.Now ?? (() => DateTimeOffset.UtcNow))());

    public void RefreshRetry(DateTimeOffset now)
    {
        RetryText = _attempt == 0 || _disposed ? string.Empty
            : _pendingRetry is not null && _retryDue is { } due ? L.Get("TabRetryIn", _attempt, Math.Max(0, (int)Math.Ceiling((due - now).TotalSeconds)))
            : IsClosed ? string.Empty
            : L.Get("TabRetryNow", _attempt);
    }

    /// <param name="share">
    /// Nennt beim Verbinden eine stehende Verbindung zum selben Server, die mitbenutzt werden kann – dann entfällt
    /// die Anmeldung. Gefragt wird bei jedem Aufbau neu, also auch bei „Erneut verbinden“.
    /// </param>
    /// <param name="leader">
    /// Nennt beim Start eine andere Sitzung zum selben Server, die sich gerade anmeldet. Dann wartet diese hier, bis
    /// die andere steht, und benutzt deren Verbindung mit – statt sich daneben ein zweites Mal anzumelden (zwei
    /// Passwort-Rückfragen, wenn zwei Tabs zum selben Server zugleich starten).
    /// </param>
    public SessionViewModel(
        ServerProfile profile,
        INexusBackend backend,
        IUiDispatcher dispatcher,
        IUserPrompts prompts,
        Func<SessionViewModel, IServerConnection?>? share = null,
        Func<SessionViewModel, SessionViewModel?>? leader = null,
        SessionRetry? retry = null,
        Func<IServerConnection>? connect = null)
    {
        _connect = connect;
        Profile = profile;
        System = profile.System;
        _backend = backend;
        _dispatcher = dispatcher;
        _prompts = prompts;
        _share = share;
        _leader = leader;
        _retry = retry;
        Files = new SftpBrowserViewModel { HasCommands = !profile.IsFtp && !profile.IsLocal, Profile = profile };
        Inspector = new ServerInspectorViewModel(profile, dispatcher);
        StatusText = string.Empty;
        StateLabel = string.Empty;
        _connection = Open();
    }

    /// <summary>
    /// Ausgabe für das Terminal: was der Server schickt, dazu Statuszeilen der App (verbinden, getrennt).
    /// Beliebiger Thread; der Puffer gilt nur während des Aufrufs.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? Output;

    /// <summary>Die Verbindung ist zu Ende – von selbst, durch einen Fehler oder weil sie geschlossen wurde.</summary>
    public event Action<SessionViewModel, ConnectionStatus>? ConnectionEnded;

    /// <summary>Eindeutig je Tab – derselbe Server kann mehrfach offen sein. Dient auch als Terminal-ID im WebView.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ServerProfile Profile { get; }

    /// <summary>
    /// Kennung des Betriebssystems für das Symbol in der Kopfzeile: aus dem Profil, oder – beim ersten Verbinden –
    /// sobald die Shell es erkannt hat. Das Profil der Sitzung selbst ändert sich nicht.
    /// </summary>
    [ObservableProperty]
    public partial string? System { get; set; }

    /// <summary>
    /// Was am Reiter steht: der Name, den der Benutzer dem Tab gegeben hat, sonst – wenn es eingeschaltet ist – der
    /// Titel, den der Server gesetzt hat, sonst der Name des Profils.
    /// </summary>
    public string Title => CustomTitle ?? (ShowServerTitle && ServerTitle is { Length: > 0 } fromServer ? fromServer : Profile.Name);

    /// <summary>Der Name, den der Benutzer diesem Tab gegeben hat; <c>null</c>, wenn er keinen eigenen hat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? CustomTitle { get; private set; }

    /// <summary>Der Titel, den ein Programm auf dem Server gesetzt hat (OSC 0/2) – fremder Text, schon gesäubert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? ServerTitle { get; private set; }

    /// <summary>Ob der Titel des Servers am Reiter stehen darf – eine Einstellung der App, für alle Tabs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool ShowServerTitle { get; set; }

    /// <summary>
    /// Das Arbeitsverzeichnis, das die Shell zuletzt gemeldet hat (OSC 7); <c>null</c>, solange sie keines gemeldet
    /// hat. Ab hier gelten relative Pfade, die im Terminal angeklickt werden.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFollowShell))]
    public partial string? ShellDirectory { get; set; }

    /// <summary>Ob der Dateibrowser der Shell folgen kann: Nur, wenn sie ihren Ordner selbst meldet (OSC 7).</summary>
    public bool CanFollowShell => ShellDirectory is not null;

    /// <summary>
    /// „Terminal-Ordner folgen“: Der Dateibrowser dieser Sitzung wechselt in den Ordner, den die Shell meldet. Je
    /// Sitzung, von Haus aus aus, und nur, solange die Shell ihren Ordner meldet – die App tippt dafür nie etwas in
    /// die Shell. Eingeschaltet wechselt er gleich dorthin, wo die Shell gerade steht.
    /// </summary>
    public bool FollowsShell
    {
        get => _followsShell;
        set
        {
            if (SetProperty(ref _followsShell, value && CanFollowShell) && _followsShell
                && ShellDirectory is { } directory && Files.IsAvailable && Files.CurrentPath != directory)
            {
                _ = Files.NavigateToAsync(directory);
            }
        }
    }

    private bool _followsShell;

    /// <summary>
    /// Die Namen im Verzeichnis der Shell, die das Terminal beim Tippen ergänzen darf (<see cref="Completions.Names"/>).
    /// Setzt die Shell (<c>ShellViewModel.LoadPathNamesAsync</c>); leer, solange nichts gelesen wurde.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<PathName> PathNames { get; internal set; } = [];

    /// <summary>Die Programme im Suchpfad des Servers, als erstes Wort einer Zeile zu ergänzen. Setzt die Shell.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> CommandNames { get; internal set; } = [];

    // Inhalt von Unterverzeichnissen, schon gelesen – mit der Zeit, zu der das war.
    internal Dictionary<string, (DateTimeOffset Read, IReadOnlyList<PathName> Names)> ChildNames { get; } = [];

    /// <summary>Wo die Shell nach dem Verbinden steht: dort, wo auch der Dateibrowser beginnt. <c>null</c> ohne Dateien.</summary>
    internal string? StartDirectory { get; set; }

    // Welches Verzeichnis zuletzt für das Ergänzen gelesen wurde, wann, und die Nummer der Anfrage (die jüngste gilt).
    internal string? NamesPath { get; set; }

    internal DateTimeOffset NamesRead { get; set; }

    internal int NamesRequest { get; set; }

    /// <summary>Nimmt die Namen zurück – die Verbindung ist neu oder weg, oder das Ergänzen ist abgeschaltet.</summary>
    internal void ForgetPathNames()
    {
        NamesRequest++;
        NamesPath = null;
        ChildNames.Clear();
        var before = PathNames;
        PathNames = [];

        // Auch wenn schon keine da waren: Die Seite soll erfahren, dass die Verbindung neu ist.
        if (ReferenceEquals(before, PathNames))
        {
            OnPropertyChanged(nameof(PathNames));
        }
    }

    /// <summary>So lang darf ein Titel am Reiter höchstens sein.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>Gibt dem Tab einen eigenen Namen; leer nimmt ihn wieder weg.</summary>
    public void Rename(string? title)
    {
        var text = TitleText(title);
        CustomTitle = text.Length > 0 ? text : null;
    }

    /// <summary>Das Terminal meldet den Titel, den der Server gesetzt hat. Er gilt, bis der Server einen anderen setzt.</summary>
    public void SetServerTitle(string? title) => ServerTitle = TitleText(title) is { Length: > 0 } text ? text : null;

    // Eine Zeile ohne Steuerzeichen und ohne Zeichen, die die Schreibrichtung umkehren, gekürzt.
    private static string TitleText(string? title)
    {
        var text = Format.Visible(title ?? string.Empty).Trim();
        return text.Length > MaxTitleLength ? text[..MaxTitleLength].TrimEnd() + "…" : text;
    }

    /// <summary>Das Terminal des Tabs – die Sitzung selbst, nicht die jeweilige Verbindung.</summary>
    public ITerminalSession Terminal => this;

    /// <summary>Die aktuelle Verbindung – damit eine andere Sitzung zum selben Server sie mitbenutzen kann.</summary>
    internal IServerConnection Connection => _connection;

    public SftpBrowserViewModel Files { get; }

    public ServerInspectorViewModel Inspector { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosed), nameof(IsBusy))]
    public partial ConnectionState State { get; private set; }

    /// <summary>Zustand in Worten, für die Statuszeile über dem Terminal und den Inspector.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Ergebnis der letzten Messung der Antwortzeit („12 ms bis 203.0.113.5“); leer, solange keine lief.</summary>
    [ObservableProperty]
    public partial string LatencyText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMeasuring { get; private set; }

    /// <summary>
    /// Misst, wie lange der Server braucht, um den Port zu öffnen – ein eigener kurzer Verbindungsaufbau ohne
    /// Anmeldung (<see cref="INexusBackend.ProbeAsync"/>), neben der stehenden Sitzung. Bei einem Weg über
    /// Sprung-Server gibt es das nicht; dann sagt der Text, warum.
    /// </summary>
    public async Task MeasureLatencyAsync()
    {
        if (IsMeasuring)
        {
            return;
        }

        IsMeasuring = true;
        try
        {
            LatencyText = ProbeWording.Latency(await _backend.ProbeAsync(Profile));
        }
        catch (BackendException e)
        {
            LatencyText = e.Message;
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    /// <summary>Zustand in einem Wort, am Reiter für eine Sprachausgabe: verbunden oder nicht – ohne den Grund.</summary>
    [ObservableProperty]
    public partial string StateLabel { get; private set; }

    /// <summary>Das Terminal teilt sich die Fläche mit dem einer anderen Sitzung (Spec §5, „Split View“).</summary>
    [ObservableProperty]
    public partial bool IsSplit { get; internal set; }

    /// <summary>
    /// Verbunden, aber ohne Shell: Der Server lässt für das Konto keine zu (nur SFTP). Dateibrowser und Transfers
    /// gehen, das Terminal bleibt leer.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFilesOnly { get; private set; }

    /// <summary>Die Verbindung ist zu Ende; der Tab bietet „Erneut verbinden“ an.</summary>
    public bool IsClosed => State == ConnectionState.Closed;

    /// <summary>Die Verbindung wird gerade aufgebaut.</summary>
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Authenticating;

    /// <summary>
    /// Gestartet und selbst dabei, sich anzumelden – noch weder verbunden noch gescheitert. Eine Sitzung, die ihrerseits
    /// auf eine andere wartet, zählt nicht: Sonst könnten zwei aufeinander warten.
    /// </summary>
    internal bool IsSettling
        => _started && !_disposed && _waitingFor is null && State is not (ConnectionState.Connected or ConnectionState.Closed);

    /// <summary>Die Sitzung steht, ist gescheitert oder wurde geschlossen – wer auf sie gewartet hat, kann weitermachen.</summary>
    internal event Action? Settled;

    /// <summary>Baut die Verbindung auf. Das Terminal ruft das, sobald es seine Größe kennt.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        // Erst nachsehen, dann als gestartet gelten: Sonst hielte sich die Sitzung selbst für eine, auf die man wartet.
        var leader = _leader?.Invoke(this);
        _started = true;
        if (leader is { IsSettling: true })
        {
            _waitingFor = leader;
            leader.Settled += OnLeaderSettled;
            Print(L.Get("TerminalWaitingForSignIn", Profile.Address));
            return;
        }

        Begin();
    }

    // Die Sitzung, auf die diese gewartet hat, steht (oder nicht): jetzt verbinden – mit ihrer Verbindung, wenn es
    // eine gibt. Die beim Anlegen vorbereitete Verbindung wusste davon noch nichts, deshalb eine neue.
    private void OnLeaderSettled()
    {
        if (_waitingFor is not { } leader)
        {
            return;
        }

        leader.Settled -= OnLeaderSettled;
        _waitingFor = null;

        // Ist die andere gescheitert und haben mehrere auf sie gewartet, meldet sich die erste von ihnen an – die
        // übrigen warten dann auf diese.
        if (_leader?.Invoke(this) is { IsSettling: true } next)
        {
            _waitingFor = next;
            next.Settled += OnLeaderSettled;
            return;
        }

        Reconnect();
    }

    public void Write(ReadOnlyMemory<byte> input)
    {
        if (State == ConnectionState.Connected && !IsFilesOnly && InputGate?.Invoke() != false)
        {
            _connection.Terminal.Write(ToServer(input));
            WasTypedIn = true;
        }
    }

    /// <summary>
    /// Seit dem letzten Verbinden wurde im Terminal getippt oder eingefügt. Endet die Shell danach von selbst
    /// („exit“), schließt der Tab (<see cref="ShellViewModel"/>); ohne Eingabe – der Server wirft gleich wieder
    /// hinaus, ein Konto mit festem Befehl – bleibt er, damit man lesen kann, was dasteht.
    /// </summary>
    public bool WasTypedIn { get; private set; }

    public void Resize(int columns, int rows)
    {
        var changed = (_columns, _rows) != (columns, rows);
        (_columns, _rows) = (columns, rows);
        _connection.Terminal.Resize(columns, rows);
        if (changed)
        {
            OnPropertyChanged(nameof(TerminalSize));
        }
    }

    /// <summary>Spalten und Zeilen des Terminals, wie es sie zuletzt gemeldet hat.</summary>
    public (int Columns, int Rows) TerminalSize => (_columns, _rows);

    /// <summary>Seit wann die Verbindung steht; <c>null</c>, solange sie es nicht tut.</summary>
    public DateTimeOffset? ConnectedSince { get; private set; }

    partial void OnStateChanging(ConnectionState value)
    {
        // Vor der Meldung der Änderung: Wer auf sie hört, liest gleich den neuen Stand.
        ConnectedSince = value == ConnectionState.Connected ? ConnectedSince ?? DateTimeOffset.UtcNow : null;
    }

    public void SetPaused(bool paused) => _connection.Terminal.SetPaused(paused);

    /// <summary>Schickt Text an die Shell, als wäre er getippt – für gespeicherte Befehle.</summary>
    public void Send(string text) => Write(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Der Aufbau der Verbindungen dieser Sitzung wird ausführlich gemeldet (<see cref="Traced"/>) – eingeschaltet
    /// vom Benutzer zum Fehlersuchen, gilt, bis der Tab schließt.
    /// </summary>
    public bool IsVerbose { get; private set; }

    /// <summary>Eine Zeile des ausführlichen Protokolls: was ausgehandelt wurde, welche Anmeldung versucht wird.</summary>
    public event Action<SessionViewModel, string>? Traced;

    /// <summary>Baut die Verbindung neu auf und meldet dabei jeden Schritt.</summary>
    public void ReconnectVerbose()
    {
        IsVerbose = true;
        Reconnect();
    }

    private void OnTraced(string text) => _dispatcher.Post(() => Traced?.Invoke(this, text));

    /// <summary>
    /// Im Terminal dieser Sitzung hat die Glocke geläutet, während ein anderer Tab vorn war – der Reiter zeigt
    /// es, bis die Sitzung angesehen wird.
    /// </summary>
    [ObservableProperty]
    public partial bool HasBell { get; private set; }

    /// <summary>
    /// Die Sitzung kam über das Schnellverbinden: Ihr Profil ist vorläufig und fällt mit dem letzten Tab dazu weg –
    /// bis der Benutzer es behält (<see cref="ShellViewModel.KeepQuick"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool IsQuick { get; internal set; }

    /// <summary>Der Reiter ist angeheftet: Er steht links, hat kein × und bleibt bei „alle anderen schließen“.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; internal set; }

    /// <summary>
    /// Das Terminal dieser Sitzung steht in einem eigenen Fenster. Ihr Reiter bleibt im Hauptfenster; Dateibrowser
    /// und Server-Info zeigen sie dort weiter.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDetached { get; internal set; }

    /// <summary>Wo ihr Fenster zuletzt stand – die App schreibt es mit, für das nächste Lösen und den nächsten Start.</summary>
    public WindowBounds? DetachedBounds { get; set; }

    /// <summary>Ob sich für diese Sitzung die Auslastung messen lässt: SSH, und kein Fenster von tmux.</summary>
    public bool CanMeasure => Profile.IsSsh && !IsVirtual;

    /// <summary>Der Name des Tabs – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => Title;

    internal void MarkBell() => HasBell = true;

    internal void ClearBell() => HasBell = false;

    /// <summary>
    /// Schaltet im Terminal ab, was ein Programm der vorigen Verbindung eingeschaltet haben kann: den zweiten
    /// Bildschirm, jede Art Mausmeldung samt Kodierung, dann der Soft-Reset (DECSTR: Cursortasten, Ziffernblock,
    /// Bracketed Paste, Fokusmeldungen, sichtbarer Cursor, Farben). Den Inhalt lässt das stehen. Die Maus und der
    /// zweite Bildschirm stehen einzeln da, weil xterm.js sie bei DECSTR nicht zurücksetzt. Zurück zum ersten
    /// Bildschirm mit 1047, nicht 1049: Bei 1049 setzt xterm.js den Cursor immer auf den gemerkten Platz, auch wenn
    /// gar kein zweiter Bildschirm an war – die neue Verbindung schrieb dann über den alten Inhalt.
    /// </summary>
    internal const string ModesReset =
        "\e[?1047l\e[?9l\e[?1000l\e[?1002l\e[?1003l\e[?1005l\e[?1006l\e[?1015l\e[?1016l\e[!p";

    /// <summary>Verwirft die alte Verbindung und baut eine neue auf; der Terminal-Inhalt bleibt stehen.</summary>
    public void Reconnect()
    {
        // Von Hand neu verbunden: Die Reihe der selbsttätigen Versuche beginnt von vorn.
        _attempt = 0;
        Connect();
        RefreshRetry();
    }

    private void Connect()
    {
        CancelRetry();

        // Ein Fenster von tmux lässt sich nicht „neu verbinden“ – es lebt von der Verbindung seines Trägers.
        if (_disposed || !_started || (IsVirtual && State != ConnectionState.Idle))
        {
            return;
        }

        Detach();
        _connection.Dispose();

        // Riss die Verbindung ab, während htop oder vim lief, kam deren „Maus aus“ nie an: Jeder Klick ins Terminal
        // ginge sonst als Mausmeldung an die neue Shell und stünde dort als Text in der Zeile.
        Output?.Invoke(Encoding.ASCII.GetBytes(ModesReset));
        _connection = Open();
        Begin();
    }

    // Lässt die vorbereitete Verbindung beginnen – beim ersten Mal und bei jedem neuen Versuch.
    private void Begin()
    {
        _connection.Terminal.Resize(_columns, _rows);
        _typed = null;
        RestartRoute();

        // Ohne Sprung-Server steht schon fest, wohin es geht – dann steht die Zeile da, bevor die Verbindung
        // beginnt. Der Zustand „verbindet“ kommt über den Dispatcher, die Ausgabe direkt: Ein Gerät, das sofort
        // grüßt (Telnet, seriell), stünde sonst mit seiner Begrüßung vor der Zeile.
        // Ein Fenster von tmux verbindet nichts: Dort steht die Zeile nicht.
        _announced = Profile.JumpId is null && !IsVirtual;
        if (_announced)
        {
            Print(L.Get("TerminalConnecting", Profile.Address));
        }

        _connection.Terminal.Start();
    }

    // Die Zeile „Verbinde mit …“ für das Ziel steht schon da (siehe Connect) und kommt nicht noch einmal.
    private bool _announced;

    /// <summary>
    /// Beendet die Verbindung, lässt den Tab aber stehen – wie nach einem Abriss, nur gewollt. Weiter geht es mit
    /// <see cref="Reconnect"/>.
    /// </summary>
    public void Disconnect()
    {
        if (_disposed || !_started || _waitingFor is not null || IsClosed)
        {
            return;
        }

        // Das Ende meldet die Sitzung selbst: Eine geschlossene Verbindung sagt dem, der sie schloss, nichts mehr.
        CancelRetry();
        var connection = _connection;
        Detach();
        connection.Dispose();
        Apply(connection, new ConnectionStatus(ConnectionState.Closed, CloseReason.User));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRetry();
        StopLogging();
        if (_waitingFor is { } leader)
        {
            leader.Settled -= OnLeaderSettled;
            _waitingFor = null;
        }

        Settled?.Invoke();
        Detach();
        _promptCancellation.Dispose();
        Inspector.Dispose();
        _connection.Dispose();
    }

    private IServerConnection Open()
    {
        // Ausführlich heißt: eine eigene Verbindung – eine mitbenutzte hätte nichts auszuhandeln und nichts zu melden.
        var connection = _connect?.Invoke() ?? _backend.Connect(Profile, IsVerbose ? null : _share?.Invoke(this));
        connection.Trace = IsVerbose;
        connection.Traced += OnTraced;
        connection.Terminal.Output += OnOutput;
        connection.StatusChanged += OnStatusChanged;
        connection.PromptRequested += OnPromptRequested;
        connection.Banner += OnBanner;
        connection.MoshChanged += OnMoshChanged;
        connection.LinkChanged += OnLinkChanged;
        Apply(connection, connection.Status);
        return connection;
    }

    private void Detach()
    {
        _connection.Terminal.Output -= OnOutput;
        _connection.StatusChanged -= OnStatusChanged;
        _connection.PromptRequested -= OnPromptRequested;
        _connection.Banner -= OnBanner;
        _connection.MoshChanged -= OnMoshChanged;
        _connection.LinkChanged -= OnLinkChanged;
        _connection.Traced -= OnTraced;
        _screen = new AlternateScreen();

        // Offene Rückfragen gehören zur alten Verbindung: Dialog schließen, nicht mehr beantworten.
        _promptCancellation.Cancel();
        _promptCancellation.Dispose();
        _promptCancellation = new CancellationTokenSource();
    }

    private void OnOutput(ReadOnlyMemory<byte> data)
    {
        _screen.Feed(data.Span);
        if (OutputTap is { } tap)
        {
            // Der Steuermodus von tmux liest die Bytes des Servers selbst; umgerechnet wird, was er durchlässt –
            // und was ein Fenster von tmux zeigt, rechnet dessen eigene Sitzung um.
            tap(data, passed => Forward(FromServer(passed)));
        }
        else
        {
            Forward(FromServer(data));
        }
    }

    private void Forward(ReadOnlyMemory<byte> data)
    {
        Output?.Invoke(data);
        WriteLog(data.Span);
    }

    // Spricht der Server nicht in UTF-8, wird an dieser einen Stelle umgerechnet (TerminalCharset) – für SSH,
    // Telnet und seriell gleich. Was die App selbst ins Terminal schreibt (Print), ist schon UTF-8 und geht daran
    // vorbei; die Befehle des Steuermodus von tmux (WriteRaw) sind ASCII.
    private TerminalCharset.Input? _typed;

    private ReadOnlyMemory<byte> FromServer(ReadOnlyMemory<byte> data)
        => TerminalCharset.IsKnown(Profile.Charset) ? TerminalCharset.Decode(Profile.Charset!, data.Span) : data;

    private ReadOnlyMemory<byte> ToServer(ReadOnlyMemory<byte> input)
        => TerminalCharset.IsKnown(Profile.Charset) ? (_typed ??= new TerminalCharset.Input(Profile.Charset!)).Encode(input.Span) : input;

    // --- MCP ---

    private int _mcpCalls;

    /// <summary>Ein KI-Programm arbeitet gerade auf dieser Sitzung; der Reiter trägt dann eine Marke.</summary>
    [ObservableProperty]
    public partial bool IsMcpActive { get; private set; }

    internal void BeginMcpCall()
    {
        _mcpCalls++;
        IsMcpActive = true;
    }

    internal void EndMcpCall()
    {
        _mcpCalls--;
        IsMcpActive = _mcpCalls > 0;
    }

    private (IServerMonitor Monitor, bool Typed, bool IsZsh)? _typedShell;
    private AlternateScreen _screen = new();

    /// <summary>Die Anmelde-Shell dieser Verbindung ist zsh, soweit <see cref="HasTypedShellAsync"/> es weiß.</summary>
    internal bool IsZshLogin => _typedShell is { IsZsh: true };

    /// <summary>Ein Programm belegt den ganzen Bildschirm des Terminals (vim, htop, less, tmux).</summary>
    internal bool IsFullScreenProgram => _screen.IsOn;

    /// <summary>So lange hat die Shell Zeit, die getippte Zeile zu zeigen; danach läuft nichts.</summary>
    internal TimeSpan TypingLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>So lange muss die Shell nach dem Echo still sein, bevor Enter folgt.</summary>
    internal TimeSpan EchoQuiet { get; set; } = TimeSpan.FromMilliseconds(150);

    // Länger wartet Enter nicht auf die Stille: Im Hintergrund kann etwas anderes dauernd schreiben.
    private static readonly TimeSpan EchoQuietMost = TimeSpan.FromSeconds(1);

    /// <summary>Ein getippter Befehl läuft gerade in diesem Terminal.</summary>
    internal bool HasTypedCommand { get; private set; }

    /// <summary>
    /// Ob sich in die Shell dieser Verbindung tippen lässt; einmal je Verbindung gefragt. Lässt sich das nicht sagen (der
    /// Server gibt keinen Kanal her, gerade dort hilft das Terminal), gilt die Regel und nicht die Ausnahme: POSIX.
    /// </summary>
    internal async Task<bool> HasTypedShellAsync(IServerMonitor monitor, CancellationToken cancellation)
    {
        if (_typedShell is { } known && ReferenceEquals(known.Monitor, monitor))
        {
            return known.Typed;
        }

        try
        {
            var shell = await monitor.LoginShellAsync().WaitAsync(cancellation);
            var typed = TerminalCommand.IsTypedShell(shell);
            _typedShell = (monitor, typed, TerminalCommand.IsZsh(shell));
            return typed;
        }
        catch (RemoteFileException)
        {
            return true;
        }
    }

    /// <summary>
    /// Tippt einen Befehl sichtbar in die Shell und liest mit, bis er endet oder die Frist abläuft; danach läuft er
    /// im Terminal weiter, und die App lässt es los. Was der Server schickt, geht unverändert auch ans Terminal. Getippt
    /// wird weiter: Fragt <c>sudo</c> nach dem Passwort, gibt der Benutzer es hier ein, und es erscheint in keiner Ausgabe.
    /// </summary>
    internal async Task<CommandRun> RunInTerminalAsync(TerminalCommand command, int seconds, CancellationToken cancellation)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastOutput = Environment.TickCount64;
        void Watch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State) && State != ConnectionState.Connected)
            {
                finished.TrySetResult(false);
            }
        }

        PropertyChanged += Watch;
        HasTypedCommand = true;
        OutputTap = (data, forward) =>
        {
            if (command.Feed(data.Span))
            {
                finished.TrySetResult(true);
            }

            forward(data);
            Volatile.Write(ref lastOutput, Environment.TickCount64);
            if (command.IsEchoed)
            {
                echoed.TrySetResult();
            }
        };
        try
        {
            try
            {
                Send(command.Keys);
                await Task.WhenAny(echoed.Task, finished.Task).WaitAsync(TypingLimit, cancellation);
                if (!echoed.Task.IsCompleted)
                {
                    throw new RemoteFileException("the connection ended before the command was entered", "connection");
                }

                // Plugins zeichnen die Zeile kurz nach dem Tippen neu. Das darf nicht als Ausgabe gelten.
                var latest = Environment.TickCount64 + (long)EchoQuietMost.TotalMilliseconds;
                while (Environment.TickCount64 - Volatile.Read(ref lastOutput) < EchoQuiet.TotalMilliseconds && Environment.TickCount64 < latest)
                {
                    await Task.Delay(20, cancellation);
                }

                command.Entered();
                Send("\r");
            }
            catch (TimeoutException)
            {
                // Dort steht nicht, was freigegeben wurde: kein Enter, und die Zeile bleibt nicht liegen.
                Send(TerminalCommand.ClearAgain);
                throw new RemoteFileException(
                    "nothing ran: the shell did not show the approved line, so Enter was not pressed. A program may be running there, or the shell is in vi command mode",
                    "not_typed");
            }
            catch (OperationCanceledException)
            {
                Send(TerminalCommand.ClearAgain);
                throw;
            }

            return await finished.Task.WaitAsync(TimeSpan.FromSeconds(seconds), cancellation)
                ? command.Result(timedOut: false)
                : throw new RemoteFileException("the connection ended while the command ran", "connection");
        }
        catch (TimeoutException)
        {
            return command.Result(timedOut: true);
        }
        finally
        {
            OutputTap = null;
            HasTypedCommand = false;
            PropertyChanged -= Watch;
        }
    }

    // --- Mitschrift ---

    private readonly Lock _logGate = new();
    private Stream? _log;
    private PlainText? _logText;

    /// <summary>Die Ausgabe des Servers wird gerade in eine Datei mitgeschrieben.</summary>
    [ObservableProperty]
    public partial bool IsLogging { get; private set; }

    /// <summary>
    /// Schreibt von jetzt an mit, was der Server ausgibt – als lesbaren Text, angehängt an die Datei. Nie das
    /// Getippte: Was die Shell davon zurückschickt (das Echo), gehört zur Ausgabe, ein Passwort an einem Prompt
    /// ohne Echo also nicht. Nur auf ausdrücklichen Wunsch und nie von selbst; mit der Sitzung endet es.
    /// <c>false</c>, wenn sich die Datei nicht öffnen lässt.
    /// </summary>
    public bool StartLogging(string path)
    {
        StopLogging();
        try
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            lock (_logGate)
            {
                (_log, _logText) = (stream, new PlainText());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Print(L.Get("TerminalLogFailed", exception.Message));
            return false;
        }

        IsLogging = true;
        Print(L.Get("TerminalLogStarted", path));
        return true;
    }

    public void StopLogging()
    {
        Stream? stream;
        lock (_logGate)
        {
            (stream, _log, _logText) = (_log, null, null);
        }

        if (stream is null)
        {
            return;
        }

        stream.Dispose();
        IsLogging = false;
        if (!_disposed)
        {
            Print(L.Get("TerminalLogStopped"));
        }
    }

    // Ausgabe kommt von Threads des Backends. Scheitert das Schreiben (Platte voll, Datei weg), endet die Mitschrift.
    private void WriteLog(ReadOnlySpan<byte> data)
    {
        lock (_logGate)
        {
            if (_log is null || _logText is null)
            {
                return;
            }

            try
            {
                _logText.Append(data, _log);
                _log.Flush();
                return;
            }
            catch (IOException)
            {
                _log.Dispose();
                (_log, _logText) = (null, null);
            }
        }

        _dispatcher.Post(() => IsLogging = false);
    }

    // Was der Server vor der Anmeldung zeigt. Der Text kommt von einem Server, bei dem noch niemand angemeldet
    // ist: Er wird als reiner Text gedruckt – ohne Steuerzeichen, die das Terminal deuten könnte.
    private void OnBanner(string text)
    {
        var lines = text.Split('\n').Select(line => new string([.. line.Where(character => !char.IsControl(character))]));
        Output?.Invoke(Encoding.UTF8.GetBytes(string.Join("\r\n", lines).TrimEnd() + "\r\n"));
    }

    private void OnStatusChanged(ConnectionStatus status)
    {
        var connection = _connection;
        _dispatcher.Post(() =>
        {
            // Die Meldung kann zu einer Verbindung gehören, die inzwischen ersetzt wurde.
            if (!_disposed && ReferenceEquals(connection, _connection))
            {
                Apply(connection, status);
            }
        });
    }

    private void Apply(IServerConnection connection, ConnectionStatus status)
    {
        var previous = State;
        var hadShell = !IsFilesOnly;

        // Mosh und die Verbindung darunter gibt es nur, solange die Sitzung steht; danach sagt der Kern es neu.
        if (status.State != ConnectionState.Connected)
        {
            ResetMosh();
        }

        State = status.State;
        IsFilesOnly = status is { State: ConnectionState.Connected, FilesOnly: true };
        StatusText = Describe(status);
        StateLabel = status.State switch
        {
            ConnectionState.Connected when !status.FilesOnly => L.Get("StateConnected"),
            ConnectionState.Idle or ConnectionState.Closed => L.Get("StateDisconnected"),
            _ => StatusLabel(status),
        };
        Inspector.SetConnection(status.State == ConnectionState.Connected, StatusLabel(status));

        // Die Shell ist weg, die Verbindung bleibt: einmal sagen, warum im Terminal nichts mehr kommt.
        if (IsFilesOnly && hadShell)
        {
            Print(Profile.IsFtp ? L.Get("TerminalFtp") : L.Get("TerminalNoShell"));

            // Unverschlüsseltes FTP: bei jedem Verbinden sagen, nicht nur im Profil.
            if (Profile.IsPlainFtp)
            {
                Print(L.Get("TerminalFtpPlain"));
            }
        }

        FollowRoute(previous, status);

        if (status.State == previous)
        {
            return;
        }

        switch (status.State)
        {
            case ConnectionState.Connecting:
                // Über Sprung-Server nennt jede Zeile den Server, zu dem es gerade geht.
                if (_announced)
                {
                    _announced = false;
                }
                else
                {
                    Print(L.Get("TerminalConnecting", status.Hop ?? Profile.Address));
                }

                break;

            case ConnectionState.Connected:
                _attempt = 0;
                WasTypedIn = false;
                Files.Attach(connection.Files);
                Inspector.Attach(connection.Monitor);
                _ = Files.LoadAsync();
                Settled?.Invoke();
                break;

            case ConnectionState.Closed:
                _promptCancellation.Cancel();
                Files.Attach(null);
                Inspector.Attach(null);
                Print(StatusText);
                if (_stepFailed)
                {
                    Print(L.Get("TerminalRouteFailed", _step + 1, _via.Count + 1, StepName(_step)));
                }

                ConnectionEnded?.Invoke(this, status);
                Settled?.Invoke();
                ScheduleRetry(status, wasConnected: previous == ConnectionState.Connected);
                break;
        }

        // Der Reiter nennt, was die Sitzung gerade von selbst versucht.
        RefreshRetry();
    }

    // Reißt eine stehende Verbindung ab, versucht es die Sitzung von selbst wieder – mit wachsender Wartezeit, und
    // nur ein paar Mal. Nicht nach einem gewollten Ende (Trennen, „exit“) und nicht, wenn die Anmeldung scheitert
    // oder der Host-Schlüssel abgelehnt wird: Dort würde ein neuer Versuch nur wieder fragen oder nichts ändern.
    private void ScheduleRetry(ConnectionStatus status, bool wasConnected)
    {
        var lost = status.Reason == CloseReason.ConnectionLost && wasConnected;
        var stillUnreachable = _attempt > 0 && status.Reason is CloseReason.ConnectFailed or CloseReason.ConnectionLost;
        // Das Profil kann es für seinen Server abstellen, auch wenn es in den Einstellungen an ist.
        if (_retry is not { } retry || _disposed || IsVirtual || !retry.Enabled() || Profile.NoReconnect || !(lost || stillUnreachable))
        {
            _attempt = 0;
            return;
        }

        if (_attempt >= SessionRetry.Delays.Count)
        {
            _attempt = 0;
            Print(L.Get("TerminalReconnectGaveUp"));
            return;
        }

        var delay = SessionRetry.Delays[_attempt++];
        Print(L.Get("TerminalReconnectIn", (int)delay.TotalSeconds, _attempt, SessionRetry.Delays.Count));
        _retryDue = (retry.Now ?? (() => DateTimeOffset.UtcNow))() + delay;
        _pendingRetry = retry.Schedule(delay, () => _dispatcher.Post(() =>
        {
            if (!_disposed && _pendingRetry is not null && IsClosed)
            {
                Connect();
            }
        }));
    }

    private void CancelRetry()
    {
        _pendingRetry?.Dispose();
        _pendingRetry = null;
        _retryDue = null;
    }

    // Eigene Zeilen der App im Terminal: abgesetzt und gedimmt, damit man sie nicht für Serverausgabe hält.
    private void Print(string text) => Output?.Invoke(Encoding.UTF8.GetBytes($"\r\n\e[2m{text}\e[0m\r\n"));

    private static string StatusLabel(ConnectionStatus status) => status.State switch
    {
        ConnectionState.Connecting => L.Get("StateConnecting"),
        ConnectionState.Authenticating => L.Get("StateAuthenticating"),
        ConnectionState.Connected when status.FilesOnly => L.Get("StateFilesOnly"),
        ConnectionState.Connected => L.Get("Online"),
        _ => L.Get("Offline"),
    };

    private static string Describe(ConnectionStatus status)
    {
        if (status.State != ConnectionState.Closed)
        {
            return StatusLabel(status);
        }

        var reason = status.Reason switch
        {
            CloseReason.User => L.Get("ClosedByUser"),
            CloseReason.Remote => status.ExitStatus is { } code and not 0
                ? L.Get("ClosedByRemoteWithCode", code)
                : L.Get("ClosedByRemote"),
            // Weiß der Kern, woran es lag, steht das da – mit dem, was man versuchen kann.
            CloseReason.ConnectFailed => ConnectTrouble.Sentence(status.Trouble) ?? L.Get("ClosedConnectFailed"),
            CloseReason.HostKeyRejected => L.Get("ClosedHostKeyRejected"),
            CloseReason.AuthFailed => L.Get("ClosedAuthFailed"),
            CloseReason.UnknownProfile => L.Get("ClosedUnknownProfile"),
            _ => L.Get("ClosedConnectionLost"),
        };

        // Die Einzelheit kommt englisch vom Backend; sie hilft bei der Fehlersuche und steht deshalb dahinter –
        // außer dort, wo der Satz davor schon alles sagt.
        return status.Reason is CloseReason.User or CloseReason.Remote or CloseReason.HostKeyRejected
            || string.IsNullOrWhiteSpace(status.Detail)
            ? reason
            : $"{reason} ({status.Detail})";
    }

    private void OnPromptRequested(ConnectionPrompt prompt)
    {
        var connection = _connection;
        _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(connection, _connection))
            {
                _ = AnswerAsync(prompt, _promptCancellation.Token);
            }
        });
    }

    private async Task AnswerAsync(ConnectionPrompt prompt, CancellationToken cancellation)
    {
        try
        {
            switch (prompt)
            {
                case ConnectionPrompt.HostKey hostKey:
                    hostKey.Answer(await _prompts.ConfirmHostKeyAsync(hostKey, cancellation));
                    break;

                case ConnectionPrompt.Secret secret:
                    secret.Answer(await _prompts.AskSecretAsync(secret, cancellation));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Die Verbindung ist inzwischen zu Ende oder ersetzt; ihre Rückfrage braucht keine Antwort mehr.
        }
        catch (Exception error)
        {
            // Lässt sich die Rückfrage nicht anzeigen, gilt sie als abgelehnt – sonst hinge die Verbindung
            // für immer im Aufbau, und „nicht gefragt“ darf nie „zugestimmt“ heißen. Warum, steht im Terminal:
            // Sonst sähe es aus, als hätte der Benutzer selbst abgelehnt.
            Print(L.Get("TerminalPromptFailed", Format.Visible($"{error.GetType().Name}: {error.Message}")));
            switch (prompt)
            {
                case ConnectionPrompt.HostKey hostKey:
                    hostKey.Answer(false);
                    break;

                case ConnectionPrompt.Secret secret:
                    secret.Answer(null);
                    break;
            }
        }
    }
}
