using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NexusSSH.Core.Plugins;

/// <summary>
/// Die Rechte eines Plugins. Jedes erbittet das Plugin in seiner Beschreibung; gelten tut, was der Benutzer gewährt
/// hat. Ohne jedes Recht darf ein Plugin Befehle in die Palette legen, Ergebnisse zeigen und eigene Daten behalten.
/// </summary>
public static class PluginPermission
{
    /// <summary>Hinweise von Windows und Einträge im Protokoll.</summary>
    public const string Notify = "notify";

    /// <summary>Einen Befehl am Server der aktiven Sitzung vorschlagen – er läuft nur nach Freigabe.</summary>
    public const string Commands = "commands";

    /// <summary>Dateien der aktiven Sitzung lesen.</summary>
    public const string Files = "files";

    /// <summary>Zeilen der Ausgabe sehen, die auf ein Muster der Beschreibung passen.</summary>
    public const string Watch = "watch";

    /// <summary>Erfahren, wenn eine Sitzung verbunden oder getrennt wird.</summary>
    public const string Events = "events";

    /// <summary>Alle, in der Reihenfolge, in der die App sie zeigt.</summary>
    public static IReadOnlyList<string> All { get; } = [Notify, Commands, Files, Watch, Events];
}

/// <summary>Ein Befehl, den ein Plugin in die Palette legt.</summary>
public sealed record PluginCommand(string Id, string Title);

/// <summary>Die Ereignisse, auf die ein Plugin hören kann (Recht <see cref="PluginPermission.Events"/>).</summary>
public static class PluginEvent
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";

    public static IReadOnlyList<string> All { get; } = [Connected, Disconnected];
}

/// <summary>
/// Ein Muster, dessen Zeilen ein Plugin sehen will (Recht <see cref="PluginPermission.Watch"/>): Text oder regulärer
/// Ausdruck. Fremd, also ohne Rückverfolgung (<see cref="RegexOptions.NonBacktracking"/>, Laufzeit linear) und mit
/// Frist; ein Muster, das auf eine leere Zeile passt (<c>.*</c>), gilt nicht – es sähe alles.
/// </summary>
public sealed partial class PluginPattern
{
    public const int MaxPatterns = 10;
    public const int MaxLength = 200;

    private readonly Regex? _regex;

    private PluginPattern(string id, string text, bool isRegex, bool ignoreCase, Regex? regex)
        => (Id, Text, IsRegex, IgnoreCase, _regex) = (id, text, isRegex, ignoreCase, regex);

    public string Id { get; }

    public string Text { get; }

    public bool IsRegex { get; }

    public bool IgnoreCase { get; }

    /// <summary>Ein Muster aus der Beschreibung; <c>null</c> und ein Grund, wenn es nicht taugt.</summary>
    public static PluginPattern? Create(string? id, string? text, bool isRegex, bool ignoreCase, out string? problem)
    {
        problem = null;
        if (id is not { Length: > 0 and <= 40 } || !PatternId().IsMatch(id))
        {
            problem = "a pattern without a valid id";
            return null;
        }

        if (text is not { Length: > 0 and <= MaxLength } || !PluginRequest.IsShowable(text) || text.Contains('\n', StringComparison.Ordinal))
        {
            problem = $"pattern {id} is empty, too long or has control characters";
            return null;
        }

        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                regex = new Regex(text, options, TimeSpan.FromMilliseconds(100));
                if (regex.IsMatch(string.Empty))
                {
                    problem = $"pattern {id} matches an empty line";
                    return null;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                problem = $"pattern {id} is no supported regular expression";
                return null;
            }
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            problem = $"pattern {id} is only blanks";
            return null;
        }

        return new PluginPattern(id, text, isRegex, ignoreCase, regex);
    }

    /// <summary>Ob die Zeile passt. Läuft auf dem Faden der Ausgabe; eine überschrittene Frist heißt „passt nicht“.</summary>
    public bool Matches(string line)
    {
        if (_regex is null)
        {
            return line.Contains(Text, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        try
        {
            return _regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex PatternId();
}

/// <summary>
/// Die Beschreibung eines Plugins (der Abschnitt <c>nexus-plugin</c> im Modul): fremder Text, geprüft wie ein Import.
/// </summary>
public sealed partial record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<PluginCommand> Commands)
{
    private const int MaxCommands = 30;

    /// <summary>Liest die Beschreibung; <c>null</c> und ein Grund (englisch, fürs Protokoll), wenn sie nicht taugt.</summary>
    public static PluginManifest? Parse(string? json, out string? problem)
    {
        problem = null;
        ManifestFile? file;
        try
        {
            file = json is { Length: > 0 and <= 64 * 1024 } ? JsonSerializer.Deserialize(json, PluginJsonContext.Default.ManifestFile) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        {
            file = null;
        }

        if (file is null || file.Format != 1)
        {
            problem = "no description of format 1";
            return null;
        }

        if (file.Id is not { Length: <= 64 } id || !IdPattern().IsMatch(id))
        {
            problem = "no valid id";
            return null;
        }

        if (PluginText.Clean(file.Name, 60) is not { Length: > 0 } name)
        {
            problem = "no name";
            return null;
        }

        if (file.Version is not { } version || !VersionPattern().IsMatch(version))
        {
            problem = "no valid version";
            return null;
        }

        var permissions = (file.Permissions ?? []).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (permissions.FirstOrDefault(permission => !PluginPermission.All.Contains(permission)) is { } unknown)
        {
            problem = $"unknown permission {PluginText.Clean(unknown, 40)}";
            return null;
        }

        var commands = (file.Commands ?? [])
            .Where(command => command?.Id is { Length: <= 40 } commandId && Word().IsMatch(commandId))
            .Select(command => new PluginCommand(command!.Id!, PluginText.Clean(command.Title, 60) ?? string.Empty))
            .Where(command => command.Title.Length > 0)
            .DistinctBy(command => command.Id)
            .Take(MaxCommands)
            .ToList();
        // Ereignisse und Muster nur mit dem Recht dazu in der Beschreibung: Sonst sähe die Vorschau sie nicht als Recht.
        var events = (file.Events ?? []).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (events.FirstOrDefault(name => !PluginEvent.All.Contains(name)) is { } unknownEvent)
        {
            problem = $"unknown event {PluginText.Clean(unknownEvent, 40)}";
            return null;
        }

        if (events.Count > 0 && !permissions.Contains(PluginPermission.Events))
        {
            problem = "events need the events permission";
            return null;
        }

        var watch = new List<PluginPattern>();
        foreach (var entry in file.Watch ?? [])
        {
            if (PluginPattern.Create(entry?.Id, entry?.Pattern, entry?.Regex == true, entry?.IgnoreCase == true, out var reason) is not { } pattern)
            {
                problem = reason;
                return null;
            }

            if (watch.Any(known => known.Id == pattern.Id))
            {
                problem = $"pattern {pattern.Id} twice";
                return null;
            }

            watch.Add(pattern);
        }

        if (watch.Count > PluginPattern.MaxPatterns)
        {
            problem = $"more than {PluginPattern.MaxPatterns} patterns";
            return null;
        }

        if (watch.Count > 0 && !permissions.Contains(PluginPermission.Watch))
        {
            problem = "patterns need the watch permission";
            return null;
        }

        // Ohne Befehl geht es nur, wenn das Plugin auf Ereignisse oder Muster hört.
        if (commands.Count == 0 && events.Count == 0 && watch.Count == 0)
        {
            problem = "no commands";
            return null;
        }

        return new PluginManifest(id, name, version, PluginText.Clean(file.Author, 60) ?? string.Empty, PluginText.Clean(file.Description, 400) ?? string.Empty, [.. PluginPermission.All.Where(permissions.Contains)], commands)
        {
            Events = [.. PluginEvent.All.Where(events.Contains)],
            Watch = watch,
        };
    }

    /// <summary>Die Ereignisse, auf die das Plugin hört (wirken nur mit dem Recht <see cref="PluginPermission.Events"/>).</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>Die Muster, deren Zeilen das Plugin sehen will (wirken nur mit dem Recht <see cref="PluginPermission.Watch"/>).</summary>
    public IReadOnlyList<PluginPattern> Watch { get; init; } = [];

    /// <summary>Eine Kennung, wie sie ein Plugin haben darf – sie dient auch als Dateiname.</summary>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 64 } && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d{1,6}(\.\d{1,6}){0,3}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex Word();
}

/// <summary>Ein Ergebnis zum Zeigen: Überschrift, Text, eine Tabelle – alles gesäubert und begrenzt.</summary>
public sealed record PluginView(string Title, string Text, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Was ein Plugin als Nächstes braucht (Stufe 2): einen Befehl am Server (<see cref="Command"/>, läuft nur nach
/// Freigabe) oder eine Datei der Sitzung (<see cref="Path"/>). Genau eines von beiden.
/// </summary>
public sealed record PluginRequest(string? Command, int Seconds, string? Path, long Offset)
{
    public const int MaxCommandLength = 16 * 1024;
    public const int MaxPathLength = 4096;
    public const int DefaultSeconds = 60;
    public const int MaxSeconds = 300;

    /// <summary>
    /// Ob ein Befehlstext so gezeigt werden kann, wie er läuft: kein Steuerzeichen außer Zeilenumbruch und Tabulator,
    /// nichts, was die Schreibrichtung dreht. Sonst sähe der Benutzer in der Rückfrage etwas anderes, als liefe.
    /// </summary>
    public static bool IsShowable(string text)
        => !text.Any(c => (char.IsControl(c) && c is not ('\n' or '\t')) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format);
}

/// <summary>
/// Die Antwort eines Plugins, geprüft: ein Ergebnis zum Zeigen, ein Hinweis, ein Eintrag fürs Protokoll, neue eigene
/// Daten. Was nicht taugt, fällt einzeln weg; ob ein Hinweis erscheint, entscheiden die Rechte. Dazu, was es als
/// Nächstes braucht (<see cref="Request"/>), und sein Stand (<see cref="State"/>, JSON), den es mit dem Ergebnis zurückbekommt.
/// </summary>
public sealed record PluginOutput(PluginView? View, string Notify, string Log, string? Storage, PluginRequest? Request = null, string? State = null)
{
    public const int MaxStorage = 1024 * 1024;
    public const int MaxState = 64 * 1024;
    private const int MaxText = 20_000;
    private const int MaxColumns = 12;
    private const int MaxRows = 500;
    private const int MaxCell = 200;

    /// <summary>
    /// Liest die Antwort; <c>null</c>, wenn sie kein JSON-Objekt ist – oder wenn sie etwas verlangt, das nicht taugt
    /// (beides zugleich, ein leerer, zu langer oder nicht zeigbarer Befehl, ein unbrauchbarer Pfad, ein zu großer Stand).
    /// </summary>
    public static PluginOutput? Parse(string? json)
    {
        // JsonNode liest erst beim Zugriff: Ein doppelter Schlüssel wirft also erst unten – darum alles im try.
        try
        {
            return json is { Length: > 0 } && JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) is JsonObject root ? Read(root) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static PluginOutput? Read(JsonObject root)
    {
        // Was es als Nächstes braucht: Ein Plugin, das Unbrauchbares verlangt, antwortet unbrauchbar.
        PluginRequest? request = null;
        var run = root["run"];
        var read = root["read"];
        if (run is not null && read is not null)
        {
            return null;
        }

        if (run is not null)
        {
            if (run is not JsonObject asked || Text(asked["command"]) is not { } command || string.IsNullOrWhiteSpace(command)
                || command.Length > PluginRequest.MaxCommandLength || !PluginRequest.IsShowable(command))
            {
                return null;
            }

            var seconds = asked["timeout_seconds"] is JsonValue value && value.TryGetValue<int>(out var wanted) ? wanted : PluginRequest.DefaultSeconds;
            request = new PluginRequest(command, Math.Clamp(seconds, 1, PluginRequest.MaxSeconds), null, 0);
        }
        else if (read is not null)
        {
            if (read is not JsonObject asked || Text(asked["path"]) is not { Length: > 0 and <= PluginRequest.MaxPathLength } path
                || path.Any(char.IsControl))
            {
                return null;
            }

            var offset = asked["offset"] is JsonValue value && value.TryGetValue<long>(out var from) ? Math.Max(0, from) : 0;
            request = new PluginRequest(null, 0, path, offset);
        }

        string? state = null;
        if (root["state"] is { } kept)
        {
            state = kept.ToJsonString();
            if (state.Length > MaxState)
            {
                return null;
            }
        }

        PluginView? view = null;
        if (root["view"] is JsonObject shown)
        {
            var columns = Strings(shown["table"]?["columns"] as JsonArray, MaxColumns, MaxCell);
            var rows = (shown["table"]?["rows"] as JsonArray ?? [])
                .OfType<JsonArray>()
                .Take(MaxRows)
                .Select(row => (IReadOnlyList<string>)Strings(row, Math.Max(columns.Count, 1), MaxCell))
                .ToList();
            var title = PluginText.Clean(Text(shown["title"]), 100) ?? string.Empty;
            var text = PluginText.CleanMultiline(Text(shown["text"]), MaxText);
            view = title.Length + text.Length + rows.Count > 0 ? new PluginView(title, text, columns, rows) : null;
        }

        // Die eigenen Daten bleiben, wie das Plugin sie schrieb – nur als JSON-Objekt und begrenzt groß.
        var storage = root["storage"] is JsonObject data && data.ToJsonString() is { Length: <= MaxStorage } stored ? stored : null;
        return new PluginOutput(view, PluginText.Clean(Text(root["notify"]), 200) ?? string.Empty, PluginText.Clean(Text(root["log"]), 500) ?? string.Empty, storage, request, state);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Strings(JsonArray? array, int count, int length)
        => [.. (array ?? []).Take(count).Select(node => PluginText.Clean(Text(node) ?? node?.ToJsonString(), length) ?? string.Empty)];
}

/// <summary>Text aus einem Plugin: ohne Steuer- und Formatzeichen (die die Schreibrichtung drehen), gekürzt.</summary>
public static class PluginText
{
    public static string? Clean(string? text, int max)
    {
        if (text is null)
        {
            return null;
        }

        // Tab und Zeilenumbruch werden zu Leerzeichen (sonst klebten die Spalten einer Ausgabe aneinander), alle
        // anderen Steuer- und Formatzeichen fallen weg.
        var kept = new string([.. text
            .Select(c => c is '\t' or '\n' or '\r' ? ' ' : c)
            .Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return kept.Length <= max ? kept : kept[..max].TrimEnd();
    }

    /// <summary>Wie <see cref="Clean"/>, aber Zeilenumbrüche bleiben.</summary>
    public static string CleanMultiline(string? text, int max)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(line => Clean(line, max) ?? string.Empty);
        var joined = string.Join('\n', lines).Trim('\n');
        return joined.Length <= max ? joined : joined[..max];
    }
}

internal sealed class ManifestFile
{
    public int Format { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Author { get; set; }

    public string? Description { get; set; }

    public List<string?>? Permissions { get; set; }

    public List<CommandFile?>? Commands { get; set; }

    public List<string?>? Events { get; set; }

    public List<WatchFile?>? Watch { get; set; }
}

internal sealed class WatchFile
{
    public string? Id { get; set; }

    public string? Pattern { get; set; }

    public bool Regex { get; set; }

    public bool IgnoreCase { get; set; }
}

internal sealed class CommandFile
{
    public string? Id { get; set; }

    public string? Title { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ManifestFile))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
