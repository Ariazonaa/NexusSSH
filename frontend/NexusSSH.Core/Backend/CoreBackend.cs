using System.Collections.Concurrent;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Backend;

/// <summary>
/// Der Draht zum Rust-Kern: Anfragen als JSON hin, Ereignisse und Terminal-Ausgabe zurück. Die App setzt die
/// DLL dahinter (<c>NativeCoreTransport</c>), Tests eine Attrappe.
/// </summary>
public interface ICoreTransport : IDisposable
{
    /// <summary>Führt eine Anfrage aus und liefert die Antwort.</summary>
    string Request(string json);

    /// <summary>Eingabe an die Shell einer Sitzung.</summary>
    bool Write(ulong session, ReadOnlySpan<byte> data);

    /// <summary>Ereignis des Kerns als JSON. Beliebiger Thread.</summary>
    event Action<string>? Event;

    /// <summary>Terminal-Ausgabe einer Sitzung. Beliebiger Thread; der Puffer gilt nur während des Aufrufs.</summary>
    event Action<ulong, ReadOnlyMemory<byte>>? Output;
}

/// <summary><see cref="INexusBackend"/> über dem Rust-Kern (Protokoll: <see cref="CoreProtocol"/>).</summary>
public sealed class CoreBackend : INexusBackend, IProfileStore, IDisposable
{
    private readonly ICoreTransport _transport;
    private readonly ConcurrentDictionary<ulong, CoreConnection> _connections = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<EventDto>> _requests = new();
    private readonly ConcurrentDictionary<ulong, (Action<string> Text, Action<string?> Ended)> _follows = new();
    private long _lastFollow;
    private long _lastSession;
    private long _lastRequest;
    private volatile IReadOnlyList<ServerProfile> _profiles = [];

    /// <param name="transfersFile">
    /// Wohin die Transfers gespeichert werden, die sich noch einmal versuchen lassen (<c>transfers.json</c>); ohne
    /// Datei beginnt jeder Start mit einer leeren Liste.
    /// </param>
    public CoreBackend(ICoreTransport transport, string? transfersFile = null)
    {
        _transport = transport;
        TransferQueue = new CoreTransferQueue(this, transfersFile);
        transport.Event += OnEvent;
        transport.Output += OnOutput;
        Reload();
        TransferQueue.Restore();
    }

    public event Action<string>? Warning;

    public void SetX11Display(int display) => Send(new RequestDto("x11.display") { Display = Math.Clamp(display, 0, 63) });

    public void SetKeepAlive(int seconds, int missed) => Send(new RequestDto("keepalive.set") { Interval = seconds, Max = missed });

    // Die Vorhersage beim Tippen geht mit jedem `session.open` eines Profils mit Mosh; den Kern fragt das nicht vorher.
    private volatile string? _moshPrediction;

    internal string? MoshPrediction => _moshPrediction;

    public void SetMoshPrediction(string prediction)
        => _moshPrediction = prediction is { Length: > 0 } and not Settings.AppSettings.DefaultMoshPredict ? prediction : null;

    public event Action<TunnelNotice>? TunnelChanged;

    public void SetTunnel(IServerConnection connection, string tunnelId, bool on)
    {
        if (connection is CoreConnection { Session: var session })
        {
            Send(new RequestDto("tunnel.set") { Session = session, Tunnel = tunnelId, On = on });
        }
    }

    public async Task<ProbeReport> ProbeAsync(ServerProfile profile, CancellationToken cancellation = default)
    {
        var number = (ulong)Interlocked.Increment(ref _lastRequest);
        var pending = new TaskCompletionSource<EventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[number] = pending;

        var response = Send(new RequestDto("probe") { Request = number, Profile = ProfileDto.From(profile) });
        if (!response.Ok)
        {
            _requests.TryRemove(number, out _);
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        await using var registration = cancellation.Register(() =>
        {
            if (_requests.TryRemove(number, out var abandoned))
            {
                abandoned.TrySetCanceled(cancellation);
            }
        });

        var result = await pending.Task;
        return new ProbeReport(
            result.Step ?? string.Empty,
            result.Addresses ?? [],
            result.Address,
            result.ConnectMs is { } milliseconds ? (int)Math.Min(milliseconds, int.MaxValue) : null,
            result.Software,
            result.KeyAlgorithm,
            result.Fingerprint,
            result.KeyState,
            result.Methods ?? [],
            result.Trouble,
            result.Message);
    }

    // Die Antwort kommt sofort – nur dauert sie unter Windows bis zu drei Sekunden (der Kern fragt wsl.exe), und die
    // Anfrage steht so lange im Thread, der sie stellt. Deshalb in einem eigenen.
    public async Task<IReadOnlyList<LocalShell>> LocalShellsAsync(CancellationToken cancellation = default)
    {
        var response = await Task.Run(() => Send(new RequestDto("local.shells")), cancellation);
        if (!response.Ok)
        {
            Warning?.Invoke($"local.shells: {response.Error} {response.Message}");
            return [];
        }

        return
        [
            .. (response.Shells ?? [])
                .Where(shell => LocalShellNames.IsId(shell.Id))
                .Take(MaxLocalShells)
                .Select(shell => new LocalShell(shell.Id!, shell.Name ?? shell.Id!)),
        ];
    }

    // Mehr Shells bietet die App nicht an – eine Liste, die der Kern aus einem fremden Verzeichnis (WSL) bildet.
    private const int MaxLocalShells = 64;

    public void RememberUnlockedKeys(bool remember) => Send(new RequestDto("keys.unlocked") { Remember = remember });

    public void ForgetUnlockedKeys() => Send(new RequestDto("keys.unlocked"));

    public event Action? Changed;

    public IProfileStore Profiles => this;

    public ITransferQueue Transfers => TransferQueue;

    internal CoreTransferQueue TransferQueue { get; }

    public IReadOnlyList<ServerProfile> GetProfiles() => _profiles;

    public ServerProfile Save(ServerProfile profile)
    {
        var response = Send(new RequestDto("profiles.save") { Profile = ProfileDto.From(profile) });
        if (!response.Ok || response.Profile is null)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        Reload();
        Changed?.Invoke();
        return response.Profile.ToProfile();
    }

    public void Delete(string id)
    {
        var response = Send(new RequestDto("profiles.delete") { Id = id });
        if (!response.Ok)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        Reload();
        Changed?.Invoke();
    }

    public SshConfigListing ReadSshConfig()
    {
        var response = Send(new RequestDto("sshconfig.read"));
        if (!response.Ok)
        {
            Warning?.Invoke($"sshconfig.read: {response.Error} {response.Message}");
        }

        return new SshConfigListing(
            response.Path,
            [.. (response.Hosts ?? []).Select(host => host.ToHost())],
            response.Unchecked is { Count: > 0 } conditions ? [.. conditions.Take(24).Select(word => Format.Visible(word.Length > 40 ? word[..40] : word))] : null);
    }

    public int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false)
    {
        var response = Send(new RequestDto("sshconfig.import") { Names = aliases, Linked = linked ? true : null });
        if (!response.Ok)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        Reload();
        Changed?.Invoke();
        return response.Imported ?? 0;
    }

    public int RefreshSshConfig()
    {
        var response = Send(new RequestDto("sshconfig.refresh"));
        if (!response.Ok)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        if (response.Imported is > 0)
        {
            Reload();
            Changed?.Invoke();
        }

        return response.Imported ?? 0;
    }

    public IReadOnlyList<KnownHost> KnownHosts()
        => [.. (Send(new RequestDto("hostkeys.list")).HostKeys ?? [])
            .Select(ToKnownHost)];

    public bool ForgetHost(string name, string? authority = null) => Send(new RequestDto("hostkeys.forget") { Name = name, Authority = authority }).Ok;

    public KnownHost TrustAuthority(string patterns, string key)
    {
        var response = Send(new RequestDto("hostkeys.authority") { Patterns = patterns, Key = key });
        if (!response.Ok || response.HostKeys is not [var added, ..])
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        return ToKnownHost(added);
    }

    private static KnownHost ToKnownHost(KnownHostDto host)
        => new(host.Name ?? string.Empty, host.Algorithm ?? string.Empty, host.Fingerprint ?? string.Empty, host.Authority == true);

    public GeneratedKey GenerateKey(string name, string? passphrase, string comment)
    {
        var response = Send(new RequestDto("key.generate") { Name = name, Passphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase, Comment = comment });
        if (!response.Ok || response.Path is null || response.PublicKey is null)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        return new GeneratedKey(response.Path, response.PublicKey);
    }

    public IReadOnlyList<KeyFile> KeyFiles(IReadOnlyList<string> otherPaths)
        => [.. (Send(new RequestDto("key.list") { Paths = otherPaths }).Keys ?? []).Select(ToKeyFile).OfType<KeyFile>()];

    // Namen und Kommentare stehen in Dateien, die von überall stammen können: wie jeder fremde Text gezeigt.
    private static KeyFile? ToKeyFile(KeyFileDto key)
        => key is { Path.Length: > 0, Name.Length: > 0 }
            ? new KeyFile(
                key.Path,
                NexusSSH.Core.Format.Visible(key.Name),
                key.Format switch { "ppk" => KeyFormat.Ppk, "pem" => KeyFormat.Pem, _ => KeyFormat.OpenSsh },
                NexusSSH.Core.Format.Visible(key.Algorithm ?? string.Empty),
                key.Bits is > 0 ? key.Bits : null,
                NexusSSH.Core.Format.Visible(key.Fingerprint ?? string.Empty),
                NexusSSH.Core.Format.Visible(key.Comment ?? string.Empty),
                key.Encrypted == true,
                NexusSSH.Core.Format.Visible(key.PublicKey ?? string.Empty))
            {
                IsUnchecked = key.Unchecked == true,
            }
            : null;

    public void ChangeKeyPassphrase(string path, string? passphrase, string? newPassphrase)
    {
        var response = Send(new RequestDto("key.passphrase")
        {
            Path = path,
            Passphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase,
            NewPassphrase = string.IsNullOrEmpty(newPassphrase) ? null : newPassphrase,
        });
        if (!response.Ok)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }
    }

    public string ConvertKey(string path, string? passphrase, KeyFormat format, string name)
    {
        var response = Send(new RequestDto("key.convert")
        {
            Path = path,
            Passphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase,
            Format = format == KeyFormat.Ppk ? "ppk" : "openssh",
            Name = name,
        });
        if (!response.Ok || response.Path is null)
        {
            throw new BackendException(response.Error ?? "unknown", response.Message ?? string.Empty);
        }

        return response.Path;
    }

    public void ForgetSecrets(string id)
    {
        Send(new RequestDto("secrets.forget") { Id = id });
        Reload();
        Changed?.Invoke();
    }

    public IServerConnection Connect(ServerProfile profile, IServerConnection? share = null)
    {
        var session = (ulong)Interlocked.Increment(ref _lastSession);
        var connection = new CoreConnection(this, session, profile, share as CoreConnection);

        // Vor dem Start eintragen: Die ersten Ereignisse können kommen, bevor session.open zurückkehrt.
        _connections[session] = connection;
        return connection;
    }

    public void Dispose()
    {
        _transport.Event -= OnEvent;
        _transport.Output -= OnOutput;
        _transport.Dispose();
    }

    internal ResponseDto Send(RequestDto request) => CoreProtocol.ParseResponse(_transport.Request(CoreProtocol.Serialize(request)));

    /// <summary>Beginnt, eine Datei mitzulesen; das Ergebnis fallen zu lassen beendet es.</summary>
    internal IDisposable? Follow(ulong session, string path, Action<string> text, Action<string?> ended)
    {
        var number = (ulong)Interlocked.Increment(ref _lastFollow);
        _follows[number] = (text, ended);
        var response = Send(new RequestDto("follow.start") { Session = session, Follow = number, Path = path });
        if (!response.Ok)
        {
            _follows.TryRemove(number, out _);
            ended(FailureText.Describe(response.Error, response.Message));
            return null;
        }

        return new Following(this, number);
    }

    private sealed class Following(CoreBackend backend, ulong number) : IDisposable
    {
        // Wer aufhört, will nichts mehr hören – auch nicht das Ende.
        public void Dispose()
        {
            if (backend._follows.TryRemove(number, out _))
            {
                backend.Send(new RequestDto("follow.stop") { Follow = number });
            }
        }
    }

    internal bool Write(ulong session, ReadOnlySpan<byte> data) => _transport.Write(session, data);

    internal void Forget(ulong session) => _connections.TryRemove(session, out _);

    /// <summary>
    /// Eine Sitzung, über die sich Dateien zu diesem Profil übertragen lassen: die bevorzugte, wenn sie noch steht,
    /// sonst eine andere zum selben Server. <c>null</c>, wenn es keine gibt.
    /// </summary>
    internal ulong? SessionFor(string? profileId, ulong preferred)
    {
        static bool HasFiles(CoreConnection connection) => connection is { Status.State: ConnectionState.Connected, Files: not null };

        if (_connections.TryGetValue(preferred, out var same) && HasFiles(same))
        {
            return preferred;
        }

        return profileId is null
            ? null
            : _connections.Values.Where(connection => connection.ProfileId == profileId && HasFiles(connection)).Select(connection => (ulong?)connection.Session).Min();
    }

    /// <summary>
    /// Stößt eine Dateioperation an und wartet auf ihr Ergebnis (<c>sftp.result</c>). Wirft
    /// <see cref="RemoteFileException"/>, wenn der Kern oder der Server sie ablehnt.
    /// </summary>
    internal async Task<EventDto> SftpAsync(RequestDto request, CancellationToken cancellation = default)
    {
        var number = (ulong)Interlocked.Increment(ref _lastRequest);

        // Die Fortsetzung soll nicht auf dem Thread des Kerns laufen, der das Ergebnis meldet.
        var pending = new TaskCompletionSource<EventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[number] = pending;

        var response = Send(request with { Request = number });
        if (!response.Ok)
        {
            _requests.TryRemove(number, out _);
            throw new RemoteFileException(response.Message ?? string.Empty, response.Error ?? "failed");
        }

        await using var registration = cancellation.Register(() =>
        {
            if (_requests.TryRemove(number, out var abandoned))
            {
                abandoned.TrySetCanceled(cancellation);
            }
        });

        var result = await pending.Task;
        return result.Ok == true
            ? result
            : throw new RemoteFileException(result.Message ?? string.Empty, result.Error ?? "failed");
    }

    private void Reload()
    {
        var response = Send(new RequestDto("profiles.list"));
        if (response is { Ok: true, Profiles: { } profiles })
        {
            _profiles =
            [
                // Vorläufige Profile (Schnellverbinden) stehen in keiner Liste – ihre Sitzung kennt sie selbst.
                .. profiles.Where(profile => profile.Transient != true).Select(profile => profile.ToProfile()),
            ];
        }
        else
        {
            Warning?.Invoke($"profiles.list: {response.Error} {response.Message}");
        }
    }

    private void OnOutput(ulong session, ReadOnlyMemory<byte> data)
    {
        if (_connections.TryGetValue(session, out var connection))
        {
            connection.OnOutput(data);
        }
    }

    /// <summary>
    /// Der Satz zu einem Hinweis des Kerns in der Sprache des Benutzers; die englische Meldung des Kerns steht
    /// dahinter. <c>null</c> für einen Hinweis, den die App noch nicht kennt – der erscheint dann, wie er kam.
    /// </summary>
    private static string? Translated(string? code) => code switch
    {
        "profiles_unreadable" => L.Get("WarnProfilesUnreadable"),
        "secret_not_saved" => L.Get("WarnSecretNotSaved"),
        "tls_trust_not_saved" => L.Get("WarnTlsTrustNotSaved"),
        "known_hosts_not_saved" => L.Get("WarnKnownHostsNotSaved"),
        "agent_unavailable" => L.Get("WarnAgentUnavailable"),
        "x11_unavailable" => L.Get("WarnX11Unavailable"),
        "files_over_shell" => L.Get("WarnFilesOverShell"),
        "host_certificate_invalid" => L.Get("WarnHostCertificateInvalid"),
        "host_key_revoked" => L.Get("WarnHostKeyRevoked"),
        "key_unreadable" => L.Get("WarnKeyUnreadable"),
        "agent_more_keys" => L.Get("WarnAgentMoreKeys"),
        "agent_declined" => L.Get("WarnAgentDeclined"),
        "agent_locked" => L.Get("WarnAgentLocked"),
        "local_dir_missing" => L.Get("WarnLocalDirMissing"),
        "mosh_unavailable" => L.Get("WarnMoshUnavailable"),
        "mosh_locale" => L.Get("WarnMoshLocale"),
        "mosh_blocked" => L.Get("WarnMoshBlocked"),
        _ => null,
    };

    private void OnEvent(string json)
    {
        if (CoreProtocol.ParseEvent(json) is not { } message)
        {
            return;
        }

        switch (message.Event)
        {
            case "core.warning":
                // Unverschlüsseltes FTP: der Satz in der Sprache des Benutzers, die Meldung des Kerns dahinter.
                Warning?.Invoke(message.Code switch
                {
                    "ftp_unencrypted" => $"{L.Get("WarnFtpPlain")} ({message.Message})",
                    // Der Server ließ keine weitere Verbindung zu – was er dazu sagt, steht in der Meldung.
                    "ftp_refused" => $"{L.Get("WarnFtpRefused")} ({Format.Visible(message.Message ?? string.Empty)})",
                    // Im aktiven Modus kam der Server nicht zu diesem Rechner durch – sonst stünde nur „Failure“ da.
                    "ftp_active" => $"{L.Get("WarnFtpActive")} ({Format.Visible(message.Message ?? string.Empty)})",
                    // Der Server hält die Stelle zum Fortsetzen nicht (FileZilla Server) – sonst merkte das niemand.
                    "ftp_no_restart" => $"{L.Get("WarnFtpNoRestart")} ({Format.Visible(message.Message ?? string.Empty)})",
                    _ => Translated(message.Code) is { } sentence
                        ? $"{sentence} ({Format.Visible(message.Message ?? string.Empty)})"
                        : $"{message.Code}: {message.Message}",
                });
                break;

            case "tunnel.state" when message is { Profile: { } profile, Tunnel: { } tunnel }:
                TunnelChanged?.Invoke(new TunnelNotice(
                    profile,
                    tunnel,
                    message.State switch { "listening" => TunnelState.Listening, "failed" => TunnelState.Failed, _ => TunnelState.Stopped },
                    message.Message));
                break;

            case "profiles.changed":
                Reload();
                Changed?.Invoke();
                break;

            case "sftp.result" or "probe.result" when message.Request is { } request && _requests.TryRemove(request, out var pending):
                pending.TrySetResult(message);
                break;

            case "transfer.state":
                TransferQueue.OnEvent(message);
                break;

            case "follow.data" when message.Follow is { } follow && _follows.TryGetValue(follow, out var reader):
                reader.Text(message.Text ?? string.Empty);
                break;

            case "follow.end" when message.Follow is { } follow && _follows.TryRemove(follow, out var reader):
                reader.Ended(message.Error is null ? null : FailureText.Describe(message.Error, message.Message));
                break;

            case "session.state" or "session.files_only" or "session.banner" or "session.trace" or "session.mosh" or "session.link_lost" or "session.linked"
                or "prompt.host_key" or "prompt.secret" or "monitor.stats" or "monitor.unavailable"
                when message.Session is { } session && _connections.TryGetValue(session, out var connection):
                connection.OnEvent(message);
                break;
        }
    }
}

/// <summary>Eine Sitzung im Rust-Kern.</summary>
internal sealed class CoreConnection(CoreBackend backend, ulong session, ServerProfile profile, CoreConnection? share = null) : IServerConnection, ITerminalSession
{
    /// <summary>Nummer der Sitzung im Kern.</summary>
    internal ulong Session => session;

    internal string ProfileId => profile.Id;

    private readonly Lock _gate = new();
    private int _columns = 80;
    private int _rows = 24;
    private bool _started;
    private bool _disposed;
    private CoreServerMonitor? _monitor;

    public event Action<ReadOnlyMemory<byte>>? Output;

    public event Action<ConnectionStatus>? StatusChanged;

    public event Action<ConnectionPrompt>? PromptRequested;

    public event Action<string>? Banner;

    public bool Trace { get; set; }

    public event Action<string>? Traced;

    public ITerminalSession Terminal => this;

    /// <summary>Steht mit dem Zustand „verbunden“ fest; <c>null</c>, wenn der Server kein SFTP anbietet.</summary>
    public IRemoteFileSystem? Files { get; private set; }

    /// <summary>Steht mit dem Zustand „verbunden“; ob der Server Werte liefert, zeigt sich erst beim Messen.</summary>
    public IServerMonitor? Monitor => _monitor;

    public ConnectionStatus Status { get; private set; } = new(ConnectionState.Idle);

    /// <summary>Seit <c>session.mosh</c>: wie es dem Terminal über Mosh geht; <c>null</c>, solange der Kern nichts sagt (SSH).</summary>
    public MoshStatus? Mosh { get; private set; }

    public event Action<MoshStatus>? MoshChanged;

    /// <summary>Die SSH-Verbindung unter dem Mosh-Terminal ist weg (<c>session.link_lost</c>); Dateien und Messung fehlen dann.</summary>
    public bool IsLinkLost { get; private set; }

    public string? LinkLostDetail { get; private set; }

    public event Action? LinkChanged;

    public void Relink()
    {
        if (Status.State == ConnectionState.Connected && IsLinkLost)
        {
            backend.Send(new RequestDto("session.relink") { Session = session });
        }
    }

    public void Start()
    {
        int columns, rows;
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            (columns, rows) = (_columns, _rows);
        }

        var response = backend.Send(new RequestDto("session.open")
        {
            Session = session,
            ProfileId = profile.Id,
            Cols = columns,
            Rows = rows,

            // Mitbenutzen lohnt nur, wenn die andere Verbindung gerade steht; der Kern prüft das selbst noch einmal.
            Share = share is { Status.State: ConnectionState.Connected } ? share.Session : null,
            Trace = Trace ? true : null,
            MoshPredict = profile.Mosh ? backend.MoshPrediction : null,
        });
        if (!response.Ok)
        {
            // Auch ein Sprung-Server, den es nicht mehr gibt (jump_missing), verhindert schon den Start.
            var reason = response.Error == "unknown_profile" ? CloseReason.UnknownProfile : CloseReason.ConnectFailed;
            SetStatus(new ConnectionStatus(ConnectionState.Closed, reason, response.Message));
        }
    }

    public void Write(ReadOnlyMemory<byte> input) => backend.Write(session, input.Span);

    public void Resize(int columns, int rows)
    {
        bool started;
        lock (_gate)
        {
            (_columns, _rows) = (columns, rows);
            started = _started && !_disposed;
        }

        if (started && Status.State != ConnectionState.Closed)
        {
            backend.Send(new RequestDto("session.resize") { Session = session, Cols = columns, Rows = rows });
        }
    }

    public void SetPaused(bool paused)
    {
        if (Status.State == ConnectionState.Connected)
        {
            backend.Send(new RequestDto("session.flow") { Session = session, Paused = paused });
        }
    }

    public void Dispose()
    {
        bool close;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            close = _started;
        }

        // Erst austragen, dann schließen: Das „closed“-Ereignis dazu soll niemanden mehr erreichen.
        backend.Forget(session);
        if (close)
        {
            backend.Send(new RequestDto("session.close") { Session = session });
        }
    }

    internal void OnOutput(ReadOnlyMemory<byte> data) => Output?.Invoke(data);

    internal void OnEvent(EventDto message)
    {
        switch (message.Event)
        {
            case "session.state":
                // Jeder Zustand beginnt neu: Was Mosh und die Verbindung darunter betrifft, meldet der Kern danach.
                (Mosh, IsLinkLost, LinkLostDetail) = (null, false, null);
                // Das Dateisystem muss stehen, bevor der Zustand gemeldet wird: Wer auf „verbunden“ reagiert,
                // greift sofort darauf zu.
                Files = message is { State: "connected", Home: { } home }
                    ? new CoreRemoteFileSystem(backend, session, profile, home)
                    : null;
                _monitor = message.State == "connected" ? new CoreServerMonitor(backend, session) : null;
                SetStatus(new ConnectionStatus(
                    ParseState(message.State),
                    ParseReason(message.Reason),
                    message.Message,
                    message.ExitStatus is { } status ? unchecked((int)status) : null,
                    message.Hop,
                    Trouble: message.Trouble));
                break;

            // Die Shell ist weg, die Verbindung steht weiter – Dateisystem und Auslastung bleiben, wie sie sind.
            case "session.files_only" when Status.State == ConnectionState.Connected:
                SetStatus(Status with
                {
                    FilesOnly = true,
                    ExitStatus = message.ExitStatus is { } code ? unchecked((int)code) : null,
                });
                break;

            case "monitor.stats" or "monitor.unavailable":
                _monitor?.OnEvent(message);
                break;

            case "session.mosh" when Status.State == ConnectionState.Connected:
                var mosh = message.State == "silent"
                    ? new MoshStatus(true, (int)Math.Clamp(message.SilentFor ?? 0, 0, int.MaxValue))
                    : new MoshStatus(false);
                Mosh = mosh;
                MoshChanged?.Invoke(mosh);
                break;

            // Dateien und Messung hängen an der SSH-Verbindung, nicht am Terminal: Sie gehen mit ihr und kommen mit ihr.
            // Wie bei `session.state` steht alles, bevor es gemeldet wird.
            case "session.link_lost" when Status.State == ConnectionState.Connected:
                (Files, _monitor, IsLinkLost, LinkLostDetail) = (null, null, true, message.Message);
                LinkChanged?.Invoke();
                break;

            case "session.linked" when Status.State == ConnectionState.Connected:
                Files = message.Home is { } linkedHome ? new CoreRemoteFileSystem(backend, session, profile, linkedHome) : null;
                _monitor = new CoreServerMonitor(backend, session);
                (IsLinkLost, LinkLostDetail) = (false, null);
                LinkChanged?.Invoke();
                break;

            case "session.banner" when !string.IsNullOrEmpty(message.Text):
                Banner?.Invoke(message.Text);
                break;

            case "session.trace" when Trace && !string.IsNullOrEmpty(message.Text):
                Traced?.Invoke(message.Text);
                break;

            case "prompt.host_key" when message.Prompt is { } prompt:
                PromptRequested?.Invoke(new ConnectionPrompt.HostKey(
                    message.Host ?? string.Empty,
                    message.Port ?? 22,
                    message.Algorithm ?? string.Empty,
                    message.Fingerprint ?? string.Empty,
                    message.Changed ?? false,
                    message.KnownHostsFile,
                    message.KnownHostsLine,
                    accept => backend.Send(new RequestDto("prompt.answer") { Prompt = prompt, Accept = accept }),
                    message.PreviousFingerprint,
                    message.KnownHostsForeign ?? false));
                break;

            case "prompt.secret" when message.Prompt is { } prompt:
                PromptRequested?.Invoke(new ConnectionPrompt.Secret(
                    message.Kind switch
                    {
                        "passphrase" => SecretKind.Passphrase,
                        "keyboard_interactive" => SecretKind.KeyboardInteractive,
                        _ => SecretKind.Password,
                    },
                    message.User ?? string.Empty,
                    message.Host ?? string.Empty,
                    message.KeyPath,
                    message.Name,
                    message.Instructions,
                    [.. (message.Fields ?? []).Select(field => new SecretField(field.Text ?? string.Empty, field.Echo))],
                    message.CanSave ?? false,
                    message.Attempt ?? 1,
                    answer => backend.Send(new RequestDto("prompt.answer")
                    {
                        Prompt = prompt,
                        Values = answer?.Values,
                        Save = answer?.Save,
                    })));
                break;
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static ConnectionState ParseState(string? state) => state switch
    {
        "connecting" => ConnectionState.Connecting,
        "authenticating" => ConnectionState.Authenticating,
        "connected" => ConnectionState.Connected,
        _ => ConnectionState.Closed,
    };

    private static CloseReason ParseReason(string? reason) => reason switch
    {
        null => CloseReason.None,
        "user" => CloseReason.User,
        "remote" => CloseReason.Remote,
        "connect_failed" => CloseReason.ConnectFailed,
        "host_key_rejected" => CloseReason.HostKeyRejected,
        "auth_failed" => CloseReason.AuthFailed,
        "unknown_profile" => CloseReason.UnknownProfile,
        _ => CloseReason.ConnectionLost,
    };
}
