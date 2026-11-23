using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusSSH.Core.Plugins;

/// <summary>Ein installiertes Plugin: seine Beschreibung, die Datei, ihre Prüfsumme, die gewährten Rechte.</summary>
public sealed record InstalledPlugin(PluginManifest Manifest, string Path, string Sha256, IReadOnlyList<string> Granted, bool IsDisabled)
{
    public bool Has(string permission) => Granted.Contains(permission);

    public override string ToString() => Manifest.Name;
}

/// <summary>
/// Die installierten Plugins im Ordner <c>plugins</c> des Datenordners: je eines als <c>&lt;Kennung&gt;.nxplugin</c>,
/// daneben <c>state.json</c> (Prüfsumme der installierten Fassung, gewährte Rechte, an/aus) und unter <c>data</c> die
/// eigenen Daten jedes Plugins. Die Beschreibung liest die App bei jedem Start neu aus der Datei – über den Kern.
/// </summary>
public sealed class PluginStore(string directory)
{
    public const string FileExtension = ".nxplugin";

    /// <summary>So viele Plugins nimmt die App höchstens.</summary>
    public const int MaxPlugins = 30;

    private Dictionary<string, Entry> _state = new(StringComparer.Ordinal);

    public string Directory => directory;

    /// <summary>Die Pfade und Einträge der installierten Plugins; die Beschreibungen liest der Aufrufer.</summary>
    public IReadOnlyList<(string Id, string Path, string Sha256, IReadOnlyList<string> Granted, bool Disabled)> Load()
    {
        _state = ReadState();
        return [.. _state
            .Where(entry => File.Exists(PathOf(entry.Key)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxPlugins)
            .Select(entry => (entry.Key, PathOf(entry.Key), entry.Value.Sha256, (IReadOnlyList<string>)[.. entry.Value.Granted], entry.Value.Disabled))];
    }

    /// <summary>
    /// Kopiert die Datei hinein und merkt sich Prüfsumme und gewährte Rechte. Gibt den Pfad der Kopie zurück; ob sie
    /// noch dieselbe ist, prüft der Kern bei jedem Aufruf (Prüfsumme).
    /// </summary>
    public string Install(string id, string source, string sha256, IReadOnlyList<string> granted)
    {
        System.IO.Directory.CreateDirectory(directory);
        var target = PathOf(id);
        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
        _state[id] = new Entry { Sha256 = sha256, Granted = [.. granted] };
        SaveState();
        return target;
    }

    public void Remove(string id)
    {
        foreach (var path in new[] { PathOf(id), DataOf(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Beim nächsten Mal.
            }
        }

        _state.Remove(id);
        SaveState();
    }

    public void SetDisabled(string id, bool disabled)
    {
        if (_state.TryGetValue(id, out var entry))
        {
            entry.Disabled = disabled;
            SaveState();
        }
    }

    /// <summary>Setzt die gewährten Rechte (nur solche, die das Plugin erbittet – das prüft der Aufrufer).</summary>
    public void SetGranted(string id, IReadOnlyList<string> granted)
    {
        if (_state.TryGetValue(id, out var entry))
        {
            entry.Granted = [.. granted];
            SaveState();
        }
    }

    /// <summary>Die eigenen Daten des Plugins (ein JSON-Objekt); <c>null</c>, wenn es noch keine hat.</summary>
    public string? ReadData(string id)
    {
        try
        {
            var path = DataOf(id);
            return File.Exists(path) && new FileInfo(path).Length <= PluginOutput.MaxStorage ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteData(string id, string json)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "data"));
            var path = DataOf(id);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dann behält das Plugin die alten Daten.
        }
    }

    // Die Kennung ist geprüft (Kleinbuchstaben, Ziffern, Punkt, Bindestrich) – sie taugt als Dateiname.
    private string PathOf(string id) => Path.Combine(directory, id + FileExtension);

    private string DataOf(string id) => Path.Combine(directory, "data", id + ".json");

    private Dictionary<string, Entry> ReadState()
    {
        try
        {
            var path = Path.Combine(directory, "state.json");
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), PluginStateJsonContext.Default.DictionaryStringEntry) is { } state)
            {
                return new Dictionary<string, Entry>(state.Where(entry => PluginManifest.IsValidId(entry.Key) && entry.Value is { Sha256.Length: 64 }), StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // Dann eben ohne – die Plugins müssen neu installiert werden.
        }

        return new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private void SaveState()
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_state, PluginStateJsonContext.Default.DictionaryStringEntry));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gilt bis zum Ende der App.
        }
    }

    internal sealed class Entry
    {
        public string Sha256 { get; set; } = string.Empty;

        public List<string> Granted { get; set; } = [];

        public bool Disabled { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, PluginStore.Entry>))]
internal sealed partial class PluginStateJsonContext : JsonSerializerContext;
