using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Backend;

/// <summary>
/// Auslastung des Servers einer Sitzung. Der Kern misst nur, solange es verlangt ist (<c>monitor.set</c>), und
/// meldet sich mit <c>monitor.stats</c> oder – wenn der Server nichts liefert – einmal mit <c>monitor.unavailable</c>.
/// </summary>
internal sealed class CoreServerMonitor(CoreBackend backend, ulong session) : IServerMonitor
{
    // Wie im Kern (monitor.rs): Mehr zeigt die App nicht.
    private const int MaxCores = 256;
    private const int MaxDisks = 64;
    private const int MaxInterfaces = 64;
    private const int MaxIoDevices = 32;

    private readonly Lock _gate = new();
    private bool _active;

    public event Action? Changed;

    public ServerStats? Current { get; private set; }

    public bool IsUnavailable { get; private set; }

    public bool IsRetrying { get; private set; }

    public string? UnavailableDetail { get; private set; }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active)
            {
                return;
            }

            _active = active;
        }

        // Die Antwort ist gleichgültig: Gibt es die Sitzung nicht mehr, gibt es auch nichts zu messen.
        backend.Send(new RequestDto("monitor.set") { Session = session, Enabled = active });
    }

    public async Task<ServerLog> LogsAsync(LogQuery query)
    {
        var journal = query.Path is null;
        var result = await backend.SftpAsync(new RequestDto("logs.read")
        {
            Session = session,
            Path = query.Path,
            Since = !journal ? null : query.Since switch
            {
                LogSince.Minutes15 => "15m",
                LogSince.Hour => "1h",
                LogSince.Hours6 => "6h",
                LogSince.Day => "1d",
                LogSince.Week => "7d",
                _ => null,
            },
            Priority = journal ? query.Priority : null,
            Unit = journal && query.Unit.Length > 0 ? query.Unit : null,
            Lines = Math.Clamp(query.Lines, 1, LogQuery.MaxLines),
        });

        return Lines(result);
    }

    // Quelle und Text sind Ausgabe des Servers: nur als sichtbarer Text, und nicht mehr Zeilen als verlangt.
    private static ServerLog Lines(EventDto result)
    {
        return new ServerLog(
            [.. (result.Logs ?? [])
                .OfType<LogLineDto>()
                .Where(line => line.Message is not null)
                .Take(LogQuery.MaxLines)
                .Select(line => new ServerLogLine(
                    line.Time is > 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeSeconds(line.Time) : null,
                    line.Priority is >= 0 and <= 7 ? line.Priority : null,
                    Format.Visible(line.Source ?? string.Empty),
                    Format.Visible(line.Message!)))],
            result.Limited == true);
    }

    public async Task<IReadOnlyList<MuxSession>> MuxSessionsAsync()
    {
        var result = await backend.SftpAsync(new RequestDto("mux.list") { Session = session });
        return [.. (result.Mux ?? [])
            .Where(mux => mux is { Kind: not null, Name: not null, Command: not null })
            .Select(mux => new MuxSession(mux.Kind!, mux.Name!, mux.Windows, mux.Attached, mux.Command!))];
    }

    public async Task<IReadOnlyList<string>> CommandNamesAsync()
    {
        var result = await backend.SftpAsync(new RequestDto("commands.list") { Session = session });
        return Terminals.Completions.CommandNames(result.Commands ?? []);
    }

    public async Task<CommandShell> CommandShellAsync()
    {
        try
        {
            // Der Kern antwortet mit der Art der Shell; ein älterer Kern nannte keine – dann POSIX.
            var result = await backend.SftpAsync(new RequestDto("commands.probe") { Session = session });
            return result.Line == "windows" ? CommandShell.Windows : CommandShell.Posix;
        }
        catch (RemoteFileException e)
        {
            // Nur diese beiden heißen „geht hier nicht“; alles andere (Frist, Verbindung weg) sagt nichts darüber.
            return e.Code is "no_posix_shell" or "no_exec" ? CommandShell.None : CommandShell.Unknown;
        }
    }

    public async Task<string> LoginShellAsync()
        => (await backend.SftpAsync(new RequestDto("commands.shell") { Session = session })).Line ?? string.Empty;

    public async Task<CommandRun> RunCommandAsync(string command, int seconds, CancellationToken cancellation)
    {
        var result = await backend.SftpAsync(new RequestDto("exec.run") { Session = session, Command = command, Seconds = seconds }, cancellation);
        return result.Run is { } run
            ? new CommandRun(run.Status, run.Stdout ?? string.Empty, run.Stderr ?? string.Empty, run.Cut, run.TimedOut)
            : throw new RemoteFileException("the core gave no result", "failed");
    }

    public Task InstallKeyAsync(string publicKey)
        => backend.SftpAsync(new RequestDto("key.install") { Session = session, PublicKey = publicKey });

    internal void OnEvent(EventDto message)
    {
        if (message.Event == "monitor.unavailable")
        {
            IsUnavailable = true;
            IsRetrying = message.Retrying == true;
            UnavailableDetail = message.Message is { } detail ? Format.Visible(detail) : null;
        }
        else
        {
            var (memoryUsed, memoryTotal) = (message.MemoryUsed ?? 0, message.MemoryTotal ?? 0);
            var (diskUsed, diskTotal) = (message.DiskUsed ?? 0, message.DiskTotal ?? 0);
            IsUnavailable = false;
            IsRetrying = false;
            Current = new ServerStats(
                Online: true,
                CpuPercent: Math.Clamp(message.Cpu ?? 0, 0, 100),
                MemoryPercent: Share(memoryUsed, memoryTotal),
                DiskPercent: Share(diskUsed, diskTotal),
                OsName: message.Os ?? string.Empty,
                Kernel: message.Kernel ?? string.Empty,
                Uptime: TimeSpan.FromSeconds(Math.Max(message.Uptime ?? 0, 0)),
                MemoryUsed: memoryUsed,
                MemoryTotal: memoryTotal,
                Memory: new MemoryParts(
                    message.MemoryCache is { } cache ? Math.Clamp(cache, 0, memoryTotal) : null,
                    message.MemoryFree is { } free ? Math.Clamp(free, 0, memoryTotal) : null,
                    Math.Clamp(message.SwapUsed ?? 0, 0, Math.Max(message.SwapTotal ?? 0, 0)),
                    Math.Max(message.SwapTotal ?? 0, 0)),
                DiskUsed: diskUsed,
                DiskTotal: diskTotal,
                Load: message.Load ?? string.Empty,
                NetReceived: Math.Max(message.NetRx ?? 0, 0),
                NetSent: Math.Max(message.NetTx ?? 0, 0),
                // Namen und Adressen sind Text des Servers: der Name nur als sichtbarer Text, als Adresse nur,
                // was eine ist.
                Interfaces: [.. (message.Interfaces ?? [])
                    .OfType<MonitorInterfaceDto>()
                    .Where(card => card.Name is { Length: > 0 and <= NetInterface.MaxNameLength })
                    .Take(MaxInterfaces)
                    .Select(card => new NetInterface(
                        Format.Visible(card.Name!),
                        Math.Max(card.Rx, 0),
                        Math.Max(card.Tx, 0),
                        string.Join(", ", (card.Addresses ?? [])
                            .Where(address => System.Net.IPAddress.TryParse(address, out _))
                            .Take(NetInterface.MaxAddresses))))],
                Io: message is { IoRead: { } read, IoWrite: { } written }
                    ? new DiskIo(Math.Max(read, 0), Math.Max(written, 0), Math.Clamp(message.IoBusy ?? 0, 0, 100), message.IoMixed == true)
                    : null,
                // Auch der Name einer Platte ist Text des Servers.
                IoDevices: [.. (message.IoDisks ?? [])
                    .OfType<MonitorIoDiskDto>()
                    .Where(disk => disk.Name is { Length: > 0 and <= DiskIoDevice.MaxNameLength })
                    .Take(MaxIoDevices)
                    .Select(disk => new DiskIoDevice(
                        Format.Visible(disk.Name!),
                        new DiskIo(Math.Max(disk.Read, 0), Math.Max(disk.Write, 0), Math.Clamp(disk.Busy, 0, 100), message.IoMixed == true)))],
                Cores: [.. (message.Cores ?? []).Take(MaxCores).Select(core => (double)Math.Clamp(core, 0, 100))],
                Split: message is { CpuUser: { } user, CpuSystem: { } system }
                    ? new CpuSplit(Math.Clamp(user, 0, 100), Math.Clamp(system, 0, 100), Math.Clamp(message.CpuIowait ?? 0, 0, 100), Math.Clamp(message.CpuSteal ?? 0, 0, 100))
                    : null,
                // Einhängepunkt und Gerät sind Text des Servers: nur als sichtbarer Text, und nur mit einem Namen.
                Disks: [.. (message.Disks ?? [])
                    .Where(disk => disk is { Mount.Length: > 0, Total: > 0, Used: >= 0 })
                    .Take(MaxDisks)
                    .Select(disk => new DiskUsage(
                        Format.Visible(disk.Mount!),
                        Format.Visible(disk.Device ?? string.Empty),
                        Math.Min(disk.Used, disk.Total),
                        disk.Total,
                        disk is { InodesTotal: > 0, InodesUsed: >= 0 } ? disk.InodesUsed : null,
                        disk is { InodesTotal: > 0, InodesUsed: >= 0 } ? disk.InodesTotal : null))],
                Info: InfoOf(message),
                Clock: ServerClock.From(message.Time, message.UtcOffset, message.Zone, DateTimeOffset.UtcNow));
        }

        Changed?.Invoke();
    }

    // Text des Servers: nur als sichtbarer Text. Die Art der Virtualisierung ist ein einzelnes Wort.
    private static ServerInfo? InfoOf(EventDto message)
    {
        var virtualization = message.Virtualization is { Length: > 0 and <= 24 } word && word.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? word.ToLowerInvariant()
            : string.Empty;
        var info = new ServerInfo(
            Format.Visible(message.Machine ?? string.Empty),
            Format.Visible(message.CpuModel ?? string.Empty),
            message.CoreCount is >= 1 and <= 4096 ? message.CoreCount : null,
            virtualization);
        return info is { Machine.Length: 0, CpuModel.Length: 0, CoreCount: null, Virtualization.Length: 0 } ? null : info;
    }

    private static double Share(long used, long total) => total > 0 ? Math.Clamp(100.0 * used / total, 0, 100) : 0;
}
