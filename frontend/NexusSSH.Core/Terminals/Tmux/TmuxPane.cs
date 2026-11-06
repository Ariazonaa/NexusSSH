using NexusSSH.Core.Services;

namespace NexusSSH.Core.Terminals.Tmux;

/// <summary>
/// Ein Fenster von tmux als „Verbindung“ für einen Tab der App: Ausgabe kommt aus dem Steuermodus, Eingabe geht als
/// Befehl dorthin. Dateien und Auslastung sind die der Verbindung, über die tmux läuft – der Tab zeigt denselben
/// Server.
/// </summary>
public sealed class TmuxPane : IServerConnection, ITerminalSession
{
    private readonly TmuxClient _client;
    private readonly Lock _gate = new();
    private readonly List<byte[]> _held = [];
    private bool _started;
    private bool _ready;
    private bool _closed;
    private bool _paused;

    private readonly Action<TmuxPane>? _resized;

    /// <param name="resized">Wer die Größe des Fensters bei tmux bestimmt, wenn mehrere Bereiche es sich teilen; ohne ihn gilt die Größe dieses Terminals für den ganzen Client.</param>
    public TmuxPane(TmuxClient client, string window, string pane, IServerConnection carrier, Action<TmuxPane>? resized = null)
    {
        _resized = resized;
        _client = client;
        Window = window;
        Pane = pane;
        Files = carrier.Files;
        Monitor = carrier.Monitor;
    }

    /// <summary>Die Kennung des Fensters (<c>@3</c>).</summary>
    public string Window { get; }

    /// <summary>Der Bereich, den der Tab zeigt (<c>%7</c>) – der aktive des Fensters.</summary>
    public string Pane { get; private set; }

    /// <summary>Die Größe, die das Terminal des Tabs zuletzt gemeldet hat; (0, 0), solange es keine kennt.</summary>
    public (int Columns, int Rows) Size { get; private set; }

    public ITerminalSession Terminal => this;

    public IRemoteFileSystem? Files { get; }

    public IServerMonitor? Monitor { get; }

    // Nicht „verbindet“: Die Verbindung steht längst, der Tab zeigt nur ein Fenster darauf.
    public ConnectionStatus Status { get; private set; } = new(ConnectionState.Idle);

    public event Action<ConnectionStatus>? StatusChanged;

    public event Action<ConnectionPrompt>? PromptRequested
    {
        add { }
        remove { }
    }

    public event Action<string>? Banner
    {
        add { }
        remove { }
    }

    public event Action<ReadOnlyMemory<byte>>? Output;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _closed)
            {
                return;
            }

            _started = true;
        }

        SetStatus(new ConnectionStatus(ConnectionState.Connected));
        _ = RepaintAsync();
    }

    /// <summary>Das Fenster zeigt jetzt einen anderen Bereich: neu zeichnen, was dort steht.</summary>
    public void Show(string pane)
    {
        lock (_gate)
        {
            if (Pane == pane || _closed)
            {
                return;
            }

            (Pane, _ready) = (pane, false);
            _held.Clear();
        }

        if (_started)
        {
            _ = RepaintAsync();
        }
    }

    // Erst der Stand des Bildschirms, dann die Ausgabe, die unterdessen kam – in dieser Reihenfolge.
    private async Task RepaintAsync()
    {
        var pane = Pane;
        byte[] snapshot;
        try
        {
            snapshot = await _client.SnapshotAsync(pane).ConfigureAwait(false);
        }
        catch (TmuxException)
        {
            snapshot = [];
        }

        byte[][] held;
        lock (_gate)
        {
            if (_closed || pane != Pane)
            {
                return;
            }

            held = [.. _held];
            _held.Clear();
            _ready = true;
        }

        if (snapshot.Length > 0)
        {
            Output?.Invoke(snapshot);
        }

        foreach (var part in held)
        {
            Output?.Invoke(part);
        }
    }

    /// <summary>Ausgabe des Bereichs aus dem Steuermodus.</summary>
    public void Receive(byte[] data)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!_ready)
            {
                // Bis das Bild steht, wird gesammelt – aber nicht beliebig viel.
                if (_held.Count < 4096)
                {
                    _held.Add(data);
                }

                return;
            }
        }

        Output?.Invoke(data);
    }

    public void Write(ReadOnlyMemory<byte> input)
    {
        if (!_closed)
        {
            _client.SendKeys(Pane, input.Span);
        }
    }

    public void Resize(int columns, int rows)
    {
        // Solange das Terminal seine Größe nicht kennt, gibt es nichts mitzuteilen.
        if (_closed || columns <= 0 || rows <= 0)
        {
            return;
        }

        Size = (columns, rows);
        if (_resized is not null)
        {
            _resized(this);
        }
        else
        {
            _client.Resize(columns, rows);
        }
    }

    // Die Bremse des Terminals (zu viel Ausgabe im Rückstand) hält den Bereich bei tmux an – ab tmux 3.2. Was
    // unterdessen kommt, schickt tmux nicht nach: Beim Weiterlaufen wird deshalb neu gezeichnet, samt Verlauf.
    public void SetPaused(bool paused)
    {
        string pane;
        lock (_gate)
        {
            if (_closed || _paused == paused)
            {
                return;
            }

            pane = Pane;
            if (!_client.SetPaused(pane, paused))
            {
                return;
            }

            _paused = paused;
            if (!paused)
            {
                _ready = false;
                _held.Clear();
            }
        }

        if (!paused)
        {
            _ = RepaintAsync();
        }
    }

    /// <summary>
    /// tmux hat den Bereich von sich aus angehalten (dieser Client lag zu weit zurück). Bremst das Terminal gerade
    /// selbst, läuft er weiter, sobald es wieder Platz hat; sonst gleich – mit einem neuen Bild, denn was
    /// unterdessen kam, schickt tmux nicht nach.
    /// </summary>
    public void PausedByTmux()
    {
        string pane;
        lock (_gate)
        {
            if (_closed || _paused)
            {
                return;
            }

            pane = Pane;
            _ready = false;
            _held.Clear();
        }

        _client.SetPaused(pane, false);
        if (_started)
        {
            _ = RepaintAsync();
        }
    }

    /// <summary>Das Fenster gibt es nicht mehr, oder tmux hat den Steuermodus beendet.</summary>
    public void Close(string? detail)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _held.Clear();
        }

        SetStatus(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote, detail));
    }

    public void Dispose() => Close(null);

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
