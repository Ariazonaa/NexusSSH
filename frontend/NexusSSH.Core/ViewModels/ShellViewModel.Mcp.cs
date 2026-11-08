using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mcp;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

// Der MCP-Server: Werkzeuge für ein KI-Programm, das die Verbindungen dieser App benutzt, ohne Schlüssel oder Passwörter
// zu sehen. Text vom Server erreicht die KI und kann sie zu etwas verleiten; die App behandelt sie deshalb als fremden
// Aufrufer. Regeln, nicht lockern: von Haus aus aus, nur freigegebene Server (ihre Freigabe gilt dem Ziel, nicht dem
// Profil), jeder Befehl nur nach einer Rückfrage mit seinem ganzen Text, „nicht geantwortet“ heißt nie „zugestimmt“.
public sealed partial class ShellViewModel
{
    private const int DefaultLogEntries = 100;
    private const int MaxLogEntriesForTools = 200;
    private const int DefaultLogLines = 200;
    private const int MaxListedEntries = 500;

    private static readonly TimeSpan McpConnectLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan McpMeasureLimit = TimeSpan.FromSeconds(20);

    private bool _mcpAsking;
    private string? _mcpPipe;
    private CancellationTokenSource? _mcpStop;
    private Task _mcpListening = Task.CompletedTask;

    private TimeSpan? _mcpPromptLimit;

    /// <summary>So lange steht die Rückfrage zu einem Befehl; danach gilt er als abgelehnt. Tests setzen es kürzer.</summary>
    internal TimeSpan McpPromptLimit
    {
        get => _mcpPromptLimit ?? TimeSpan.FromSeconds(Settings.McpPromptSeconds);
        set => _mcpPromptLimit = value;
    }

    /// <summary>Die Frist der Rückfrage in Sekunden, eine aus <see cref="AppSettings.McpPromptChoices"/>.</summary>
    public int McpPromptSeconds
    {
        get => Settings.McpPromptSeconds;
        set
        {
            var seconds = AppSettings.McpPromptChoices.Contains(value) ? value : AppSettings.DefaultMcpPromptSeconds;
            if (seconds != Settings.McpPromptSeconds)
            {
                Settings.McpPromptSeconds = seconds;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ob der MCP-Server läuft. Die App lauscht nur dann; rechnereigen, nicht im Export.</summary>
    public bool McpEnabled
    {
        get => Settings.McpEnabled;
        set
        {
            if (value != Settings.McpEnabled)
            {
                Settings.McpEnabled = value;
                OnPropertyChanged();
                ApplyMcp();
            }
        }
    }

    /// <summary>Ab jetzt lauscht die App an dieser Pipe, solange <see cref="McpEnabled"/> gilt. Ohne diesen Aufruf nie.</summary>
    public void WatchMcp(string pipe)
    {
        _mcpPipe = pipe;
        ApplyMcp();
    }

    /// <summary>Beendet das Lauschen; laufende Aufrufe gelten als abgebrochen.</summary>
    public void StopMcp()
    {
        _mcpPipe = null;
        ApplyMcp();
    }

    private void ApplyMcp()
    {
        if (McpEnabled && _mcpPipe is { } pipe)
        {
            if (_mcpStop is null)
            {
                // Erst, wenn ein voriges Lauschen vorbei ist: Die Pipe gehört jeweils einem.
                var stop = new CancellationTokenSource();
                var previous = _mcpListening;
                _mcpStop = stop;
                _mcpListening = Task.Run(
                    async () =>
                    {
                        await previous;
                        await McpPipe.ListenAsync(pipe, CallMcpOnUiAsync, stop.Token);
                    },
                    CancellationToken.None);
            }
        }
        else if (_mcpStop is { } running)
        {
            running.Cancel();
            _mcpStop = null;
        }
    }

    /// <summary>Die Server, die sich freigeben lassen: alles außer lokalen Terminals, Telnet und seriell.</summary>
    public IReadOnlyList<ServerProfile> McpCandidates => BindableList.Of([.. _profiles.Where(IsMcpCandidate)]);

    /// <summary>Ob der Server für KI-Programme freigegeben ist.</summary>
    public bool IsMcpServer(ServerProfile profile)
        => IsMcpCandidate(profile) && Settings.McpServers.Contains(McpTarget(profile), StringComparer.Ordinal);

    /// <summary>Gibt einen Server frei oder nimmt die Freigabe zurück.</summary>
    public void SetMcpServer(string profileId, bool released)
    {
        if (_profiles.FirstOrDefault(profile => profile.Id == profileId) is not { } profile || IsMcpServer(profile) == released)
        {
            return;
        }

        var target = McpTarget(profile);
        if (released)
        {
            Settings.McpServers.Add(target);
        }
        else
        {
            // Mit der Freigabe gehen die Befehle, die ohne Rückfrage laufen durften: Wer sie erneut erteilt, beginnt leer.
            Settings.McpServers.RemoveAll(entry => entry == target);
            Settings.McpAllowedCommands.RemoveAll(entry => entry.Target == target);
        }
    }

    /// <summary>Ob genau dieser Befehl auf diesem Server ohne Rückfrage laufen darf. Kein Muster: Ein Zeichen mehr fragt wieder.</summary>
    public bool IsMcpCommandAllowed(ServerProfile profile, string command)
        => IsMcpServer(profile) && Settings.McpAllowedCommands.Contains(new McpAllowedCommand(McpTarget(profile), command));

    // Nur aus der Antwort des Benutzers auf die Rückfrage, nie aus etwas, das die KI schickt.
    private void AllowMcpCommand(ServerProfile profile, string command)
    {
        var entry = new McpAllowedCommand(McpTarget(profile), command);
        if (!Settings.McpAllowedCommands.Contains(entry))
        {
            if (Settings.McpAllowedCommands.Count >= AppSettings.MaxMcpAllowedCommands)
            {
                Settings.McpAllowedCommands.RemoveAt(0);
            }

            Settings.McpAllowedCommands.Add(entry);
            OnPropertyChanged(nameof(McpAllowedCommandList));
        }
    }

    /// <summary>Die Befehle, die ohne Rückfrage laufen dürfen, je freigegebenem Server – für die Einstellungen.</summary>
    public IReadOnlyList<(ServerProfile Server, McpAllowedCommand Entry)> McpAllowedCommandList
    {
        get
        {
            var servers = _profiles.Where(IsMcpServer).ToDictionary(McpTarget, StringComparer.Ordinal);
            return
            [
                .. Settings.McpAllowedCommands
                    .Where(entry => servers.ContainsKey(entry.Target))
                    .Select(entry => (Server: servers[entry.Target], Entry: entry))
                    .OrderBy(pair => pair.Server.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(pair => pair.Entry.Command, StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>Nimmt einen Befehl zurück: Er fragt wieder.</summary>
    public void ForgetMcpCommand(McpAllowedCommand entry)
    {
        if (Settings.McpAllowedCommands.Remove(entry))
        {
            OnPropertyChanged(nameof(McpAllowedCommandList));
        }
    }

    /// <summary>Nimmt alle Befehle zurück, auch die von Servern, die gerade nicht freigegeben sind.</summary>
    public void ForgetMcpCommands()
    {
        Settings.McpAllowedCommands.Clear();
        OnPropertyChanged(nameof(McpAllowedCommandList));
    }

    private static bool IsMcpCandidate(ServerProfile profile)
        => profile is { IsLocal: false, IsTelnet: false, IsSerial: false, IsTransient: false };

    // Die Freigabe gilt Profil und Ziel zusammen: Zeigt ein Profil nach einem Import oder einer Änderung in
    // ~/.ssh/config woandershin, ist sie weg – wie beim gespeicherten Passwort.
    private static string McpTarget(ServerProfile profile)
        => string.Join('\n', profile.Id, profile.Protocol ?? string.Empty, profile.User, profile.Host, profile.Port.ToString(CultureInfo.InvariantCulture));

    /// <summary>Führt einen Aufruf auf dem Thread der Oberfläche aus. Wirft nicht, außer wenn abgebrochen wurde.</summary>
    public Task<McpResult> CallMcpOnUiAsync(McpCall call, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource<McpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() => _ = RunAsync());
        return done.Task;

        async Task RunAsync()
        {
            try
            {
                done.TrySetResult(await CallMcpAsync(call, cancellation));
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }
    }

    /// <summary>Führt ein Werkzeug aus. Fehler gehen als Antwort an die KI, in ihrer Sprache: englisch.</summary>
    public async Task<McpResult> CallMcpAsync(McpCall call, CancellationToken cancellation)
    {
        if (!McpEnabled)
        {
            return Refused("the MCP server is switched off in NexusSSH");
        }

        try
        {
            var arguments = new McpArguments(call.Arguments);
            return call.Tool switch
            {
                McpTools.Servers => McpServers(),
                McpTools.AppLog => McpAppLog(arguments),
                McpTools.Connect => await McpConnectAsync(arguments, cancellation),
                McpTools.ListFiles => await OnMcpSessionAsync(call.Tool, arguments, session => McpListFilesAsync(session, arguments, cancellation)),
                McpTools.ReadFile => await OnMcpSessionAsync(call.Tool, arguments, session => McpReadFileAsync(session, arguments, cancellation)),
                McpTools.ServerLogs => await OnMcpSessionAsync(call.Tool, arguments, session => McpLogsAsync(session, arguments, cancellation)),
                McpTools.ServerStats => await OnMcpSessionAsync(call.Tool, arguments, session => McpStatsAsync(session, cancellation)),
                McpTools.RunCommand => await McpRunCommandAsync(arguments, cancellation),
                _ => Refused("unknown tool"),
            };
        }
        catch (McpRefusedException exception)
        {
            return Refused(exception.Message);
        }
        catch (RemoteFileException exception)
        {
            return Refused(exception.Code == "no_channel"
                ? "the server allows no further channel on this connection (MaxSessions in sshd_config); close something or raise the limit"
                : Format.VisibleText(exception.Message));
        }
    }

    private static McpResult Refused(string text) => new(text, IsError: true);

    private McpResult McpServers()
    {
        Log(L.Get("LogMcpTool", McpTools.Servers));
        return McpJson.Write(writer =>
        {
            writer.WriteStartArray("servers");
            foreach (var profile in _profiles.Where(IsMcpServer))
            {
                var session = McpSessionsOf(profile).FirstOrDefault();
                writer.WriteStartObject();
                writer.WriteString("id", profile.Id);
                writer.WriteString("name", Format.Visible(profile.Name));
                writer.WriteString("protocol", profile.IsFtp ? "ftp" : "ssh");
                writer.WriteString("state", McpState(session));
                if (profile.System is { } system)
                {
                    writer.WriteString("system", system);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private McpResult McpAppLog(McpArguments arguments)
    {
        var limit = arguments.Integer("limit", DefaultLogEntries, 1, MaxLogEntriesForTools);
        Log(L.Get("LogMcpTool", McpTools.AppLog));

        // Unter „Ausgabe“ stehen alle Server; wer nicht freigegeben ist, kommt auch hier nicht vor.
        // Nur Name und Adresse zählen: Ein Stichwort oder eine Gruppe träfe auch den Namen eines freigegebenen Servers.
        var hidden = _profiles
            .Where(profile => !IsMcpServer(profile))
            .Select(profile => profile with { Tags = [], Group = string.Empty, User = string.Empty })
            .ToList();
        var mask = DiagnosticBundle.Redactor(new DiagnosticInput(
            string.Empty, string.Empty, false, hidden, [], null, null, [], 0, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        void Entries(Utf8JsonWriter writer, string name, IEnumerable<LogEntry> entries)
        {
            writer.WriteStartArray(name);
            foreach (var entry in entries.TakeLast(limit))
            {
                writer.WriteStartObject();
                writer.WriteString("time", entry.Time);
                writer.WriteString("severity", entry.Severity.ToString().ToLowerInvariant());
                writer.WriteString("text", Format.VisibleText(mask(entry.Text)));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return McpJson.Write(writer =>
        {
            Entries(writer, "output", Output);
            Entries(writer, "problems", Problems);
        });
    }

    private async Task<McpResult> McpConnectAsync(McpArguments arguments, CancellationToken cancellation)
    {
        var profile = McpServerOf(arguments);
        Log(L.Get("LogMcpCall", McpTools.Connect, Format.Visible(profile.Name)));

        // Ein Tab im Hintergrund: Er nimmt dem Benutzer nicht das Terminal weg. Dafür verbindet er, bevor sein Terminal
        // die Größe kennt – die kommt nach, sobald jemand hinsieht.
        var session = Sessions
            .Where(candidate => candidate.Profile.Id == profile.Id && !candidate.IsVirtual && !candidate.IsClosed)
            .OrderByDescending(candidate => candidate.State == ConnectionState.Connected)
            .FirstOrDefault()
            ?? AddSession(profile, Sessions.Count, activate: false);
        if (session.State != ConnectionState.Connected)
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done() => settled.TrySetResult();
            session.Settled += Done;
            try
            {
                session.Start();
                if (session.State is not (ConnectionState.Connected or ConnectionState.Closed))
                {
                    await settled.Task.WaitAsync(McpConnectLimit, cancellation);
                }
            }
            catch (TimeoutException)
            {
                throw new McpRefusedException("the connection did not finish in time; the app may be waiting for the user");
            }
            finally
            {
                session.Settled -= Done;
            }
        }

        if (session.State != ConnectionState.Connected)
        {
            throw new McpRefusedException(Format.Visible(session.StatusText));
        }

        return McpJson.Write(writer =>
        {
            writer.WriteString("server", Format.Visible(profile.Name));
            writer.WriteString("state", McpState(session));
        });
    }

    private async Task<McpResult> OnMcpSessionAsync(string tool, McpArguments arguments, Func<SessionViewModel, Task<McpResult>> work)
    {
        var session = McpSession(McpServerOf(arguments));
        Log(L.Get("LogMcpCall", tool, Format.Visible(session.Profile.Name)));
        session.BeginMcpCall();
        try
        {
            return await work(session);
        }
        finally
        {
            session.EndMcpCall();
        }
    }

    private static async Task<McpResult> McpListFilesAsync(SessionViewModel session, McpArguments arguments, CancellationToken cancellation)
    {
        var files = McpFiles(session);
        var path = arguments.Path();
        var entries = await files.ListAsync(path, cancellation).WaitAsync(cancellation);
        return McpJson.Write(writer =>
        {
            writer.WriteString("path", Format.Visible(path));
            writer.WriteBoolean("cut", entries.Count > MaxListedEntries);
            writer.WriteStartArray("entries");
            foreach (var entry in entries.Take(MaxListedEntries))
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.DisplayName);
                writer.WriteString("type", entry.IsLink ? "link" : entry.IsDirectory ? "directory" : entry.IsSpecial ? "special" : "file");
                writer.WriteNumber("size", entry.Size);
                writer.WriteString("modified", entry.Modified);
                if (entry.Permissions is { } permissions)
                {
                    writer.WriteString("permissions", Convert.ToString(permissions, 8));
                }

                if (entry.LinkTarget is { } target)
                {
                    writer.WriteString("link_target", Format.Visible(target));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static async Task<McpResult> McpReadFileAsync(SessionViewModel session, McpArguments arguments, CancellationToken cancellation)
    {
        var files = McpFiles(session);
        var path = arguments.Path();
        var offset = arguments.Long("offset", 0, 0, long.MaxValue);

        // Mehr, als ein Aufruf liefert, wird nicht verlangt; die Antwort nennt die Größe, der Rest ist der nächste Aufruf.
        var length = Math.Min(arguments.Long("length", McpTools.MaxReadLength, 1, long.MaxValue), McpTools.MaxReadLength);
        var part = await files.ReadAsync(path, offset, (int)length).WaitAsync(cancellation);
        var binary = part.Data.AsSpan().Contains((byte)0);
        return McpJson.Write(writer =>
        {
            writer.WriteString("path", Format.Visible(path));
            writer.WriteNumber("size", part.TotalSize);
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("length", part.Data.Length);
            writer.WriteBoolean("binary", binary);
            if (!binary)
            {
                writer.WriteString("text", Format.VisibleText(Encoding.UTF8.GetString(part.Data)));
            }
        });
    }

    private static async Task<McpResult> McpLogsAsync(SessionViewModel session, McpArguments arguments, CancellationToken cancellation)
    {
        var monitor = McpMonitor(session);
        var since = arguments.Text("since") switch
        {
            null => LogSince.All,
            "15m" => LogSince.Minutes15,
            "1h" => LogSince.Hour,
            "6h" => LogSince.Hours6,
            "1d" => LogSince.Day,
            "7d" => LogSince.Week,
            _ => throw new McpRefusedException("since must be one of 15m, 1h, 6h, 1d, 7d"),
        };
        var unit = arguments.Text("unit") ?? string.Empty;
        if (unit.Length > 0 && !LogQuery.IsUnitName(unit))
        {
            throw new McpRefusedException("unit is not the name of a systemd unit");
        }

        var priority = arguments.Has("priority") ? arguments.Integer("priority", 0, 0, 7) : (int?)null;
        var query = new LogQuery(arguments.Text("path"), since, priority, unit, arguments.Integer("lines", DefaultLogLines, 1, LogQuery.MaxLines));
        var log = await monitor.LogsAsync(query).WaitAsync(cancellation);
        return McpJson.Write(writer =>
        {
            // Die neuesten Zeilen, so viele in eine Antwort passen.
            var room = McpTools.MaxReadLength;
            var lines = log.Lines.Reverse().TakeWhile(line => (room -= line.Message.Length + line.Source.Length + 64) > 0).Reverse().ToList();
            writer.WriteBoolean("limited", log.Limited);
            writer.WriteBoolean("cut", lines.Count < log.Lines.Count);
            writer.WriteStartArray("lines");
            foreach (var line in lines)
            {
                writer.WriteStartObject();
                if (line.Time is { } time)
                {
                    writer.WriteString("time", time);
                }

                if (line.Priority is { } severity)
                {
                    writer.WriteNumber("priority", severity);
                }

                writer.WriteString("source", line.Source);
                writer.WriteString("message", line.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private async Task<McpResult> McpStatsAsync(SessionViewModel session, CancellationToken cancellation)
    {
        var monitor = McpMonitor(session);
        if (monitor is { Current: null, IsUnavailable: false })
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done() => changed.TrySetResult();
            monitor.Changed += Done;
            try
            {
                session.Inspector.Probe();
                await changed.Task.WaitAsync(McpMeasureLimit, cancellation);
            }
            catch (TimeoutException)
            {
                throw new McpRefusedException("the server did not answer in time");
            }
            finally
            {
                monitor.Changed -= Done;
            }
        }

        if (monitor.Current is not { } stats)
        {
            throw new McpRefusedException(monitor.UnavailableDetail ?? "the server provides no load figures");
        }

        return McpJson.Write(writer =>
        {
            // Gemessen wird nur, solange jemand hinsieht; sonst ist es die letzte Messung.
            writer.WriteBoolean("live", ReferenceEquals(session, ActiveSession) && MonitorBar);
            writer.WriteNumber("cpu_percent", stats.CpuPercent);
            writer.WriteNumber("memory_percent", stats.MemoryPercent);
            writer.WriteNumber("disk_percent", stats.DiskPercent);
            writer.WriteString("load", Format.Visible(stats.Load));
            writer.WriteNumber("uptime_seconds", (long)stats.Uptime.TotalSeconds);
            writer.WriteString("os", Format.Visible(stats.OsName));
            writer.WriteString("kernel", Format.Visible(stats.Kernel));
        });
    }

    private async Task<McpResult> McpRunCommandAsync(McpArguments arguments, CancellationToken cancellation)
    {
        var profile = McpServerOf(arguments);
        var session = McpSession(profile);
        var monitor = McpMonitor(session);
        var command = arguments.Required("command");
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0', StringComparison.Ordinal) || command.Length > McpTools.MaxCommandLength)
        {
            throw new McpRefusedException("the command is empty, too long or contains a NUL character");
        }

        var seconds = arguments.Integer("timeout_seconds", McpTools.DefaultCommandSeconds, 1, McpTools.MaxCommandSeconds);

        // Dauerhaft erlaubt läuft im eigenen Kanal: Ohne Dialog leerte das Tippen eine Zeile, die der Benutzer gerade
        // schreibt, und der Befehl liefe in dem Verzeichnis, in dem das Terminal gerade steht – nicht vorhersehbar.
        var always = IsMcpCommandAllowed(profile, command);
        var typed = !always && await TypesInTerminalAsync(session, monitor, command, cancellation);
        if (!always && (_mcpAsking || session.HasTypedCommand))
        {
            throw new McpRefusedException("busy: the user still has another command to answer, or it still runs in this terminal");
        }

        if (typed)
        {
            RefuseFullScreen(session);
        }

        var name = Format.Visible(profile.Name);
        Log(L.Get(always ? "LogMcpCommandAllowed" : "LogMcpCommand", name, Format.Visible(command)));
        session.BeginMcpCall();
        try
        {
            if (!always)
            {
                var answer = await AskForCommandAsync(name, command, typed, cancellation);
                if (answer == CommandAnswer.Decline)
                {
                    Log(L.Get("LogMcpDeclined", name), LogSeverity.Warning);
                    return Refused("the user declined this command");
                }

                // Während der Frage kann sich alles geändert haben. Der Befehl geht an genau die Verbindung, für die er
                // freigegeben wurde – steht sie nicht mehr, scheitert er, statt auf einer neuen zu laufen.
                if (!McpEnabled || !IsMcpServer(profile))
                {
                    return Refused("the release of this server ended while the user was asked");
                }

                if (answer == CommandAnswer.Always)
                {
                    AllowMcpCommand(profile, command);
                    Log(L.Get("LogMcpAlwaysAllowed", name, Format.Visible(command)));
                }
            }

            CommandRun run;
            if (typed)
            {
                RefuseFullScreen(session);
                run = await session.RunInTerminalAsync(new TerminalCommand(command, isZsh: session.IsZshLogin), seconds, cancellation);
            }
            else
            {
                run = await monitor.RunCommandAsync(command, seconds, cancellation);
            }

            Log(L.Get("LogMcpFinished", name, run.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"));
            return McpJson.Write(writer =>
            {
                if (run.Status is { } status)
                {
                    writer.WriteNumber("status", status);
                }
                else
                {
                    writer.WriteNull("status");
                }

                writer.WriteString("stdout", typed ? Format.VisibleText(run.Output).TrimStart('\n') : Format.VisibleText(run.Output));
                writer.WriteString("stderr", Format.VisibleText(run.Errors));
                writer.WriteBoolean("cut", run.Cut);
                writer.WriteBoolean("timed_out", run.TimedOut);
                writer.WriteBoolean("terminal", typed);
                writer.WriteString("approved", always ? "always" : "asked");
            });
        }
        finally
        {
            session.EndMcpCall();
        }
    }

    // Sichtbar im Terminal, wo das geht. Sonst der eigene Kanal: Mosh schickt keinen Strom, sondern den Bildschirm, ein
    // anderer Zeichensatz und der Steuermodus von tmux (OutputTap) stehen dazwischen, fish und csh lesen die Zeile hinter
    // dem Befehl nicht, und die Konsole von Windows verschluckt ihre Marke.
    private static async Task<bool> TypesInTerminalAsync(SessionViewModel session, IServerMonitor monitor, string command, CancellationToken cancellation)
        => !session.IsFilesOnly && !session.IsMosh && session.OutputTap is null && !session.Files.IsWindowsServer
            && !TerminalCharset.IsKnown(session.Profile.Charset) && TerminalCommand.CanType(command)
            && await session.HasTypedShellAsync(monitor, cancellation);

    private static void RefuseFullScreen(SessionViewModel session)
    {
        if (session.IsFullScreenProgram)
        {
            throw new McpRefusedException("a full-screen program (vim, htop, less, tmux) is running in that terminal; nothing was typed");
        }
    }

    // Nur eine Frage zugleich: Ein Programm in einer Schleife soll keine Warteschlange von Dialogen aufbauen.
    // Die Frist steht im Dialog als Zeitpunkt: Wartet er hinter einem anderen Dialog, zeigt er trotzdem, was bleibt.
    private async Task<CommandAnswer> AskForCommandAsync(string server, string command, bool inTerminal, CancellationToken cancellation)
    {
        _mcpAsking = true;
        try
        {
            var span = McpPromptLimit;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(span);
            var answer = await _prompts.ConfirmCommandAsync(new CommandQuestion(server, command, inTerminal, DateTimeOffset.UtcNow + span), limit.Token);

            // Eine Antwort, die mit dem Ende der Frist zusammenfällt, gilt nicht.
            return limit.IsCancellationRequested || !Enum.IsDefined(answer) ? CommandAnswer.Decline : answer;
        }
        catch (OperationCanceledException)
        {
            return CommandAnswer.Decline;
        }
        finally
        {
            _mcpAsking = false;
        }
    }

    private ServerProfile McpServerOf(McpArguments arguments)
    {
        var wanted = arguments.Required("server");
        var hits = _profiles
            .Where(profile => IsMcpServer(profile) && (profile.Id == wanted || string.Equals(profile.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return hits switch
        {
            [var one] => one,
            [] => throw new McpRefusedException($"there is no released server called {Format.Visible(wanted)}; the servers tool lists them"),
            _ => throw new McpRefusedException($"several servers are called {Format.Visible(wanted)}; use the id from the servers tool"),
        };
    }

    private IEnumerable<SessionViewModel> McpSessionsOf(ServerProfile profile)
        => Sessions
            .Where(session => session.Profile.Id == profile.Id && !session.IsVirtual)
            .OrderByDescending(session => session.State == ConnectionState.Connected)
            .ThenByDescending(session => ReferenceEquals(session, ActiveSession));

    private SessionViewModel McpSession(ServerProfile profile)
        => McpSessionsOf(profile).FirstOrDefault(session => session.State == ConnectionState.Connected)
            ?? throw new McpRefusedException($"{Format.Visible(profile.Name)} is not connected; call {McpTools.Connect} first");

    private static IRemoteFileSystem McpFiles(SessionViewModel session)
        => session.Connection.Files ?? throw new McpRefusedException("this connection has no file access");

    private static IServerMonitor McpMonitor(SessionViewModel session)
        => session.Connection.Monitor ?? throw new McpRefusedException("this connection cannot run commands");

    private static string McpState(SessionViewModel? session) => session?.State switch
    {
        ConnectionState.Connected => "connected",
        null or ConnectionState.Idle or ConnectionState.Closed => "disconnected",
        _ => "connecting",
    };
}
