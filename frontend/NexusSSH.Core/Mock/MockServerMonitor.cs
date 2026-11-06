using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Mock;

/// <summary>Lässt CPU und RAM leicht um ihren Ausgangswert schwanken, damit die Anzeige lebt.</summary>
public sealed class MockServerMonitor : IServerMonitor, IDisposable
{
    private readonly ServerStats _baseline;
    private readonly bool _steady;
    private readonly Random _random;
    private readonly ITimer? _timer;
    private ServerStats _current;

    /// <param name="timeProvider">Ohne <see cref="TimeProvider"/> läuft kein Timer; dann treibt <see cref="Advance"/> die Werte (Tests).</param>
    /// <param name="steady">Die Werte bleiben, wie sie sind (nur die Laufzeit wächst) – für das Bild der Beispieldaten.</param>
    public MockServerMonitor(ServerStats baseline, int seed, TimeProvider? timeProvider = null, bool steady = false)
    {
        _steady = steady;
        _baseline = baseline;
        _current = baseline;
        _random = new Random(seed);
        _timer = timeProvider?.CreateTimer(_ => Advance(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Beispiel: ein paar Zeilen des Journals bzw. einer Datei, nach der Abfrage gefiltert wie am echten Server.</summary>
    public Task<ServerLog> LogsAsync(LogQuery query)
    {
        var now = DateTimeOffset.UtcNow;
        if (query.Path is not null)
        {
            return Task.FromResult(new ServerLog(
            [
                new ServerLogLine(null, null, string.Empty, "2026-10-11 12:00:01 startup: configured 4 workers"),
                new ServerLogLine(null, null, string.Empty, "2026-10-11 12:00:02 listening on 0.0.0.0:443"),
                new ServerLogLine(null, null, string.Empty, "2026-10-11 12:04:17 warning: slow request /api/report (2140 ms)"),
            ]));
        }

        ServerLogLine[] journal =
        [
            new(now.AddMinutes(-190), 6, "systemd", "Started nginx.service - A high performance web server."),
            new(now.AddMinutes(-44), 4, "nginx", "upstream response is buffered to a temporary file"),
            new(now.AddMinutes(-12), 6, "sshd", "Accepted publickey for deploy from 192.0.2.44 port 51122"),
            new(now.AddMinutes(-9), 3, "postgres", "could not open file \"base/16384/2619\": No such file or directory"),
            new(now.AddMinutes(-2), 5, "cron", "(root) CMD (run-parts /etc/cron.hourly)"),
            new(now.AddSeconds(-20), 7, "kernel", "audit: type=1400 apparmor=\"STATUS\" operation=\"profile_load\""),
        ];
        var oldest = query.Since switch
        {
            LogSince.Minutes15 => now.AddMinutes(-15),
            LogSince.Hour => now.AddHours(-1),
            LogSince.Hours6 => now.AddHours(-6),
            LogSince.Day => now.AddDays(-1),
            LogSince.Week => now.AddDays(-7),
            _ => DateTimeOffset.MinValue,
        };
        return Task.FromResult(new ServerLog(
            [.. journal
                .Where(line => line.Time >= oldest
                    && (query.Priority is not { } priority || line.Priority <= priority)
                    && (query.Unit.Length == 0 || query.Unit.StartsWith(line.Source, StringComparison.Ordinal)))
                .TakeLast(query.Lines)]));
    }

    /// <summary>Beispiel: zwei Sitzungen von tmux und eine von screen.</summary>
    public Task<IReadOnlyList<string>> CommandNamesAsync()
        => Task.FromResult<IReadOnlyList<string>>(["apt", "awk", "cat", "docker", "git", "grep", "htop", "journalctl", "ls", "systemctl", "tar", "vim"]);

    public Task<IReadOnlyList<MuxSession>> MuxSessionsAsync() => Task.FromResult<IReadOnlyList<MuxSession>>(
    [
        new MuxSession("tmux", "deploy", 3, Attached: false, "tmux attach-session -t =deploy"),
        new MuxSession("tmux", "logs", 1, Attached: true, "tmux attach-session -t =logs"),
        new MuxSession("screen", "4711.backup", null, Attached: false, "screen -x 4711.backup"),
    ]);

    /// <summary>Befehle, die <see cref="RunCommandAsync"/> bekam, samt Frist. Für Tests.</summary>
    public List<(string Command, int Seconds)> Commands { get; } = [];

    /// <summary>Für Tests: Das Ergebnis des nächsten Befehls.</summary>
    public CommandRun NextRun { get; set; } = new(0, "ok\n", string.Empty, Cut: false, TimedOut: false);

    /// <summary>Für Tests: Der Befehl läuft nicht, sondern scheitert so.</summary>
    public RemoteFileException? RunFailure { get; set; }

    /// <summary>Für Tests: Läuft, während der Befehl läuft.</summary>
    public Action? Running { get; set; }

    /// <summary>
    /// Die Anmelde-Shell. Keine, die Befehle aus dem Terminal zurückmeldet: Die nachgespielte Shell kann das nicht, also
    /// nehmen die Beispieldaten den eigenen Kanal.
    /// </summary>
    public string LoginShell { get; set; } = "mock";

    /// <summary>Für Tests: Die Frage nach der Shell scheitert so.</summary>
    public RemoteFileException? LoginShellFailure { get; set; }

    public Task<string> LoginShellAsync()
        => LoginShellFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult(LoginShell);

    public Task<CommandRun> RunCommandAsync(string command, int seconds, CancellationToken cancellation)
    {
        if (RunFailure is { } failure)
        {
            return Task.FromException<CommandRun>(failure);
        }

        Commands.Add((command, seconds));
        Running?.Invoke();
        return Task.FromResult(NextRun);
    }

    /// <summary>Was in <c>authorized_keys</c> „eingetragen“ wurde. Für Tests.</summary>
    public List<string> InstalledKeys { get; } = [];

    /// <summary>Für Tests: Das Hinterlegen scheitert, als ließe der Server keine Befehle zu.</summary>
    public bool RefusesCommands { get; set; }

    /// <summary>Für Tests: Der Server ist einer mit OpenSSH unter Windows (Befehle als PowerShell).</summary>
    public bool IsWindows { get; set; }

    /// <summary>Für Tests: So oft kann der Server noch nicht sagen, wie er Befehle annimmt (die Probe blieb stumm).</summary>
    public int UnknownShellAnswers { get; set; }

    /// <summary>Wie oft danach gefragt wurde. Für Tests.</summary>
    public int ShellQuestions { get; private set; }

    public Task<CommandShell> CommandShellAsync()
    {
        ShellQuestions++;
        if (UnknownShellAnswers > 0)
        {
            UnknownShellAnswers--;
            return Task.FromResult(CommandShell.Unknown);
        }

        return Task.FromResult(RefusesCommands ? CommandShell.None : IsWindows ? CommandShell.Windows : CommandShell.Posix);
    }

    public Task InstallKeyAsync(string publicKey)
    {
        if (RefusesCommands)
        {
            throw new RemoteFileException("the server does not run commands for this account", "no_exec");
        }

        if (!InstalledKeys.Contains(publicKey))
        {
            InstalledKeys.Add(publicKey);
        }

        return Task.CompletedTask;
    }

    public ServerStats? Current => _current;

    public bool IsUnavailable => UnavailableDetail is not null;

    public string? UnavailableDetail { get; private set; }

    /// <summary>Für Tests: Der Server liefert ab jetzt keine Werte, aus diesem Grund.</summary>
    public void Refuse(string detail)
    {
        UnavailableDetail = detail;
        Changed?.Invoke();
    }

    /// <summary>Ob gerade jemand hinsieht – die Beispielwerte schwanken nur dann.</summary>
    public bool IsActive { get; private set; }

    public event Action? Changed;

    public void SetActive(bool active) => IsActive = active;

    public void Advance()
    {
        if (!IsActive)
        {
            return;
        }

        lock (_random)
        {
            if (_steady)
            {
                _current = _current with { Uptime = _current.Uptime + TimeSpan.FromSeconds(2) };
                Changed?.Invoke();
                return;
            }

            var memory = Jitter(_baseline.MemoryPercent, spread: 3);
            _current = _current with
            {
                CpuPercent = Jitter(_baseline.CpuPercent, spread: 12),
                MemoryPercent = memory,
                MemoryUsed = (long)(_baseline.MemoryTotal * memory / 100),
                Memory = new MemoryParts(_baseline.MemoryTotal * 3 / 10, _baseline.MemoryTotal / 10, _baseline.MemoryTotal / 32, _baseline.MemoryTotal / 4),
                Uptime = _current.Uptime + TimeSpan.FromSeconds(2),
            };
        }

        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();

    private double Jitter(double center, double spread)
        => Math.Clamp(center + ((_random.NextDouble() * 2) - 1) * spread, 1, 99);
}
