using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Plugins;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Plugins mit Code (WebAssembly im Kern): installieren nach einer Vorschau der Rechte, Befehle in der Palette, die
// Antwort geprüft – was sie zeigen will, sagen will oder sich merken will, gilt nur im Rahmen der gewährten Rechte.
// Ein Plugin, das zu lange rechnet, zu viel Speicher will, abbricht oder Unbrauchbares antwortet, wird abgeschaltet.
public sealed partial class ShellViewModel
{
    private PluginStore? _pluginStore;

    /// <summary>Die installierten Plugins.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InstalledPlugin> Plugins { get; private set; } = BindableList.Of<InstalledPlugin>([]);

    public bool HasNoPlugins => Plugins.Count == 0;

    /// <summary>Ob sich Plugins installieren lassen (mit dem Kern, nicht mit Beispieldaten).</summary>
    public bool CanUsePlugins => _pluginStore is not null && _backend.Plugins is not null;

    /// <summary>Ein Plugin will ein Ergebnis zeigen: Name des Plugins und was es zeigt (geprüft).</summary>
    public event Action<string, PluginView>? PluginViewRequested;

    /// <summary>Liest die Plugins aus <paramref name="store"/>; ihre Beschreibungen kommen aus den Dateien (über den Kern).</summary>
    public void UsePlugins(PluginStore store)
    {
        _pluginStore = store;
        ReloadPlugins();
        OnPropertyChanged(nameof(CanUsePlugins));
    }

    /// <summary>
    /// Liest eine Datei für die Vorschau: die Beschreibung und die Prüfsumme. <c>null</c> und ein Grund, wenn sie kein
    /// Plugin ist.
    /// </summary>
    public (PluginManifest Manifest, PluginInspection Inspection)? InspectPlugin(string path, out string problem)
    {
        problem = string.Empty;
        if (_backend.Plugins is not { } runtime)
        {
            problem = "no plugin runtime";
            return null;
        }

        try
        {
            var inspection = runtime.Inspect(path);
            if (PluginManifest.Parse(inspection.Manifest, out var reason) is { } manifest)
            {
                return (manifest, inspection);
            }

            problem = reason ?? string.Empty;
        }
        catch (PluginException e)
        {
            problem = $"{e.Code}: {e.Message}";
        }

        return null;
    }

    /// <summary>Das installierte Plugin mit dieser Kennung (für „ersetzt Version …“ in der Vorschau).</summary>
    public InstalledPlugin? InstalledPlugin(string id) => Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id);

    /// <summary>
    /// Installiert ein geprüftes Plugin mit den Rechten, die der Benutzer gewährt hat (nur solche, die es erbittet).
    /// <c>false</c>, wenn es nicht ging.
    /// </summary>
    public bool InstallPlugin(string path, PluginManifest manifest, PluginInspection inspection, IReadOnlyList<string> granted)
    {
        if (_pluginStore is null || (InstalledPlugin(manifest.Id) is null && Plugins.Count >= PluginStore.MaxPlugins))
        {
            return false;
        }

        try
        {
            _pluginStore.Install(manifest.Id, path, inspection.Sha256, [.. granted.Where(manifest.Permissions.Contains)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(L.Get("LogPluginNotInstalled", manifest.Name, e.Message), LogSeverity.Error);
            return false;
        }

        Log(L.Get("LogPluginInstalled", manifest.Name, manifest.Version, Permissions(granted)));
        ReloadPlugins();
        return true;
    }

    public void RemovePlugin(InstalledPlugin plugin)
    {
        _pluginStore?.Remove(plugin.Manifest.Id);
        Log(L.Get("LogPluginRemoved", plugin.Manifest.Name));
        ReloadPlugins();
    }

    public void SetPluginEnabled(InstalledPlugin plugin, bool enabled)
    {
        _pluginStore?.SetDisabled(plugin.Manifest.Id, !enabled);
        ReloadPlugins();
    }

    /// <summary>Gewährt oder entzieht ein Recht – nur eines, das das Plugin erbittet.</summary>
    public void SetPluginPermission(InstalledPlugin plugin, string permission, bool granted)
    {
        if (!plugin.Manifest.Permissions.Contains(permission))
        {
            return;
        }

        var next = granted ? plugin.Granted.Append(permission).Distinct() : plugin.Granted.Where(known => known != permission);
        _pluginStore?.SetGranted(plugin.Manifest.Id, [.. next]);
        Log(L.Get(granted ? "LogPluginGranted" : "LogPluginRevoked", plugin.Manifest.Name, Permissions([permission])));
        ReloadPlugins();
    }

    /// <summary>So viele Schritte (Befehl am Server, Datei) darf ein Aufruf eines Plugins brauchen.</summary>
    public const int MaxPluginSteps = 10;

    /// <summary>So viel einer Datei bekommt ein Plugin je Schritt; den Rest holt es mit dem nächsten (<c>offset</c>).</summary>
    public const int MaxPluginRead = 256 * 1024;

    private readonly HashSet<string> _pluginsRunning = [];

    /// <summary>
    /// Ruft einen Befehl eines Plugins auf. Das Plugin bekommt, welchen Befehl, seine eigenen Daten, Sprache und Version
    /// der App und seine Rechte – sonst nichts; mit dem Recht für Befehle oder Dateien dazu den Namen des Servers der
    /// aktiven Sitzung. Verlangt es einen Befehl am Server oder eine Datei, bekommt es das Ergebnis mit seinem Stand
    /// zurück, bis es nichts mehr verlangt (höchstens <see cref="MaxPluginSteps"/> Schritte). Alles geht an die Sitzung,
    /// die beim Aufruf aktiv war. Seine Antworten gelten nur im Rahmen der Rechte.
    /// </summary>
    public async Task RunPluginCommandAsync(InstalledPlugin plugin, PluginCommand command)
    {
        if (_backend.Plugins is not { } runtime || _pluginStore is null || plugin.IsDisabled)
        {
            return;
        }

        // Ein Plugin läuft nicht zweimal zugleich: Seine Daten und Fragen kämen sich in die Quere.
        if (!_pluginsRunning.Add(plugin.Manifest.Id))
        {
            Log(L.Get("LogPluginBusy", plugin.Manifest.Name), LogSeverity.Warning);
            return;
        }

        try
        {
            var session = plugin.Has(PluginPermission.Commands) || plugin.Has(PluginPermission.Files) ? ActiveSession : null;
            Action<Utf8JsonWriter>? result = null;
            string? state = null;
            for (var step = 0; ; step++)
            {
                if (await CallPluginAsync(runtime, plugin, command.Title, interactive: true, CommandFields(command, session, state, result)) is not { } output)
                {
                    return;
                }

                if (output.Request is not { } request)
                {
                    return;
                }

                if (step + 1 >= MaxPluginSteps)
                {
                    Log(L.Get("LogPluginTooManySteps", plugin.Manifest.Name, command.Title, MaxPluginSteps), LogSeverity.Warning);
                    return;
                }

                state = output.State;
                result = request.Command is { } text
                    ? await RunForPluginAsync(plugin, session, text, request.Seconds)
                    : await ReadForPluginAsync(plugin, session, request.Path!, request.Offset);
            }
        }
        finally
        {
            _pluginsRunning.Remove(plugin.Manifest.Id);
        }
    }

    // Ein Schritt: aufrufen, die Antwort prüfen und, was sie zeigen, sagen und behalten will, im Rahmen der Rechte tun.
    // Nur ein Aufruf über die Palette (interactive) zeigt ein Ergebnis; Ereignisse und Zeilen kommen ungefragt, da
    // ginge kein Dialog auf.
    private async Task<PluginOutput?> CallPluginAsync(IPluginRuntime runtime, InstalledPlugin plugin, string label, bool interactive, Action<Utf8JsonWriter> fields)
    {
        string answer;
        try
        {
            answer = await runtime.CallAsync(plugin.Path, plugin.Sha256, Input(plugin, fields));
        }
        catch (PluginException e)
        {
            Log(L.Get("LogPluginFailed", plugin.Manifest.Name, label, e.Code, e.Message), LogSeverity.Error);
            if (e.IsMisbehaving || e.Code == "plugin_changed")
            {
                SwitchOff(plugin);
            }

            return null;
        }

        if (PluginOutput.Parse(answer) is not { } output)
        {
            Log(L.Get("LogPluginFailed", plugin.Manifest.Name, label, "plugin_output", "not a usable answer"), LogSeverity.Error);
            SwitchOff(plugin);
            return null;
        }

        if (output.Storage is { } storage)
        {
            _pluginStore?.WriteData(plugin.Manifest.Id, storage);
        }

        // Hinweise und Einträge im Protokoll nur mit dem Recht dazu; der Name des Plugins steht immer davor.
        if (plugin.Has(PluginPermission.Notify))
        {
            if (output.Log.Length > 0)
            {
                Log($"{plugin.Manifest.Name}: {output.Log}");
            }

            if (output.Notify.Length > 0)
            {
                Notify(plugin.Manifest.Name, output.Notify);
            }
        }

        if (interactive && output.View is { } view)
        {
            PluginViewRequested?.Invoke(plugin.Manifest.Name, view);
        }

        return output;
    }

    // Ein Befehl am Server für ein Plugin: nur mit dem Recht, nur an die Sitzung vom Aufruf, immer nach Rückfrage mit
    // dem ganzen Text (keine dauerhafte Freigabe), im eigenen Kanal – nie ins Terminal getippt.
    private async Task<Action<Utf8JsonWriter>> RunForPluginAsync(InstalledPlugin plugin, SessionViewModel? session, string command, int seconds)
    {
        void Failed(Utf8JsonWriter writer, string error)
        {
            writer.WriteString("command", command);
            writer.WriteString("error", error);
        }

        if (!plugin.Has(PluginPermission.Commands))
        {
            return writer => Failed(writer, "permission");
        }

        if (session is not { State: ConnectionState.Connected, Connection.Monitor: { } monitor })
        {
            return writer => Failed(writer, "no_session");
        }

        var name = plugin.Manifest.Name;
        var server = Format.Visible(session.Profile.Name);
        Log(L.Get("LogPluginCommand", name, server, Format.Visible(command)));
        var answer = CommandAnswer.Decline;
        try
        {
            using var limit = new CancellationTokenSource(McpPromptLimit);
            answer = await _prompts.ConfirmCommandAsync(new CommandQuestion(server, command, InTerminal: false, DateTimeOffset.UtcNow + McpPromptLimit, name), limit.Token);
            answer = limit.IsCancellationRequested ? CommandAnswer.Decline : answer;
        }
        catch (OperationCanceledException)
        {
        }

        if (answer is not (CommandAnswer.Run or CommandAnswer.Always))
        {
            Log(L.Get("LogPluginCommandDeclined", name, server), LogSeverity.Warning);
            return writer => Failed(writer, "declined");
        }

        // Während der Frage kann die Sitzung gegangen sein: Dann läuft nichts, auch nicht auf einer neuen.
        if (session.State != ConnectionState.Connected || !ReferenceEquals(session.Connection.Monitor, monitor))
        {
            return writer => Failed(writer, "no_session");
        }

        CommandRun run;
        try
        {
            run = await monitor.RunCommandAsync(command, seconds, CancellationToken.None);
        }
        catch (RemoteFileException e)
        {
            return writer => Failed(writer, e.Code);
        }

        Log(L.Get("LogPluginCommandFinished", name, server, run.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"));
        return writer =>
        {
            writer.WriteString("command", command);
            if (run.Status is { } status)
            {
                writer.WriteNumber("status", status);
            }
            else
            {
                writer.WriteNull("status");
            }

            writer.WriteString("stdout", Format.VisibleText(run.Output));
            writer.WriteString("stderr", Format.VisibleText(run.Errors));
            writer.WriteBoolean("cut", run.Cut);
            writer.WriteBoolean("timed_out", run.TimedOut);
        };
    }

    // Eine Datei der Sitzung für ein Plugin: nur mit dem Recht, höchstens MaxPluginRead je Schritt. Ohne Rückfrage – das
    // Recht hat der Benutzer gewährt, und hinaus kommt das Gelesene nur über einen Befehl, und der wird gefragt.
    private async Task<Action<Utf8JsonWriter>> ReadForPluginAsync(InstalledPlugin plugin, SessionViewModel? session, string path, long offset)
    {
        void Failed(Utf8JsonWriter writer, string error)
        {
            writer.WriteString("path", path);
            writer.WriteString("error", error);
        }

        if (!plugin.Has(PluginPermission.Files))
        {
            return writer => Failed(writer, "permission");
        }

        if (session is not { State: ConnectionState.Connected, Connection.Files: { } files })
        {
            return writer => Failed(writer, "no_session");
        }

        Log(L.Get("LogPluginRead", plugin.Manifest.Name, Format.Visible(session.Profile.Name), Format.Visible(path)));
        FilePart part;
        try
        {
            part = await files.ReadAsync(path, offset, MaxPluginRead);
        }
        catch (RemoteFileException e)
        {
            return writer => Failed(writer, e.Code);
        }

        var binary = part.Data.AsSpan().Contains((byte)0);
        return writer =>
        {
            writer.WriteString("path", path);
            writer.WriteNumber("size", part.TotalSize);
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("length", part.Data.Length);
            writer.WriteBoolean("binary", binary);
            if (!binary)
            {
                writer.WriteString("text", Encoding.UTF8.GetString(part.Data));
            }
        };
    }

    /// <summary>Die Rechte, wie die App sie nennt, mit Komma.</summary>
    public static string Permissions(IEnumerable<string> permissions)
    {
        var names = permissions.Select(PermissionName).ToList();
        return names.Count == 0 ? L.Get("PluginNoPermissions") : string.Join(", ", names);
    }

    /// <summary>Der Name eines Rechts, wie die App ihn zeigt.</summary>
    public static string PermissionName(string permission) => permission switch
    {
        PluginPermission.Notify => L.Get("PluginPermissionNotify"),
        PluginPermission.Commands => L.Get("PluginPermissionCommands"),
        PluginPermission.Files => L.Get("PluginPermissionFiles"),
        PluginPermission.Watch => L.Get("PluginPermissionWatch"),
        PluginPermission.Events => L.Get("PluginPermissionEvents"),
        _ => permission,
    };

    private void SwitchOff(InstalledPlugin plugin)
    {
        _pluginStore?.SetDisabled(plugin.Manifest.Id, true);
        Log(L.Get("LogPluginSwitchedOff", plugin.Manifest.Name), LogSeverity.Warning);
        ReloadPlugins();
    }

    // Die Felder eines Aufrufs über die Palette: welcher Befehl, die Sitzung und – ab dem zweiten Schritt – Stand und Ergebnis.
    private static Action<Utf8JsonWriter> CommandFields(PluginCommand command, SessionViewModel? session, string? state, Action<Utf8JsonWriter>? result) => writer =>
    {
        writer.WriteString("kind", result is null ? "command" : "result");
        writer.WriteString("command", command.Id);
        WriteSession(writer, session);
        if (result is not null)
        {
            writer.WritePropertyName("state");
            if (state is not null)
            {
                writer.WriteRawValue(state);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteStartObject("result");
            result(writer);
            writer.WriteEndObject();
        }
    };

    private static void WriteSession(Utf8JsonWriter writer, SessionViewModel? session)
    {
        if (session is not null)
        {
            writer.WriteStartObject("session");
            writer.WriteString("server", Format.Visible(session.Profile.Name));
            writer.WriteBoolean("connected", session.State == ConnectionState.Connected);
            writer.WriteEndObject();
        }
    }

    // Was das Plugin bekommt – als JSON, gebaut von der App, nie aus fremdem Text zusammengesetzt: die Felder der Art
    // des Aufrufs, dazu immer Sprache, Version, Rechte und eigene Daten.
    private string Input(InstalledPlugin plugin, Action<Utf8JsonWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            fields(writer);
            writer.WriteString("language", L.Language switch
            {
                AppLanguage.German => "de",
                AppLanguage.French => "fr",
                AppLanguage.Spanish => "es",
                _ => "en",
            });
            writer.WriteString("version", _currentVersion.ToString(3));
            writer.WriteStartArray("permissions");
            foreach (var permission in plugin.Granted)
            {
                writer.WriteStringValue(permission);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("storage");
            if (_pluginStore?.ReadData(plugin.Manifest.Id) is { } stored && IsJsonObject(stored))
            {
                writer.WriteRawValue(stored);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ReloadPlugins()
    {
        var found = new List<InstalledPlugin>();
        if (_pluginStore is not null && _backend.Plugins is { } runtime)
        {
            foreach (var (id, path, sha256, granted, disabled) in _pluginStore.Load())
            {
                // Die Beschreibung aus der Datei, wie sie jetzt ist; eine geänderte Datei gilt nicht als installiert.
                try
                {
                    var inspection = runtime.Inspect(path);
                    if (inspection.Sha256 != sha256)
                    {
                        Log(L.Get("LogPluginChanged", id), LogSeverity.Warning);
                        continue;
                    }

                    if (PluginManifest.Parse(inspection.Manifest, out _) is { } manifest && manifest.Id == id)
                    {
                        found.Add(new InstalledPlugin(manifest, path, sha256, [.. granted.Where(manifest.Permissions.Contains)], disabled));
                    }
                }
                catch (PluginException e)
                {
                    Log(L.Get("LogPluginUnreadable", id, e.Code), LogSeverity.Warning);
                }
            }
        }

        Plugins = BindableList.Of(found);
        OnPropertyChanged(nameof(HasNoPlugins));
        _defaultCommands = BuildCommands();
        ApplyShortcuts();
        UpdatePluginWatch();
    }
}
