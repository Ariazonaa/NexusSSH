using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusSSH.Core.Extensions;

/// <summary>
/// Die installierten Pakete: je eines als <c>&lt;Kennung&gt;.nxext</c> im Ordner <c>extensions</c> des Datenordners,
/// daneben <c>state.json</c> – welche abgeschaltet sind und welche Befehle aus welchem Paket der Benutzer für immer
/// freigegeben hat (genau diese Texte, nie ein Muster). Beim Lesen geht jedes Paket wieder durch
/// <see cref="ExtensionFormat.Parse"/>: Eine Datei, die jemand daneben legt oder ändert, wird nicht anders behandelt.
/// </summary>
public sealed class ExtensionStore(string directory)
{
    /// <summary>So viele Pakete nimmt die App höchstens.</summary>
    public const int MaxPackages = 50;

    private State _state = new();

    public string Directory => directory;

    /// <summary>Liest alle Pakete; was sich nicht lesen lässt, bleibt weg (und liegt weiter da).</summary>
    public IReadOnlyList<ExtensionPackage> Load()
    {
        _state = ReadState();
        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        var packages = new List<ExtensionPackage>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*" + ExtensionFormat.FileExtension).Order(StringComparer.OrdinalIgnoreCase).Take(MaxPackages))
        {
            try
            {
                if (new FileInfo(file).Length <= ExtensionFormat.MaxLength
                    && ExtensionFormat.Parse(File.ReadAllText(file), out _) is { } package
                    && Path.GetFileNameWithoutExtension(file) == package.Id)
                {
                    packages.Add(package with { IsDisabled = _state.Disabled.Contains(package.Id) });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Bleibt weg.
            }
        }

        return packages;
    }

    /// <summary>Legt ein geprüftes Paket ab (ein vorhandenes mit derselben Kennung wird ersetzt).</summary>
    public void Install(ExtensionPackage package)
    {
        System.IO.Directory.CreateDirectory(directory);
        WriteAtomic(PathOf(package.Id), ExtensionFormat.Write(package));
        _state.Disabled.Remove(package.Id);
        SaveState();
    }

    /// <summary>Nimmt ein Paket weg, samt seinen Freigaben.</summary>
    public void Remove(string id)
    {
        try
        {
            File.Delete(PathOf(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Der Benutzer sieht es weiter in der Liste; beim nächsten Versuch wieder.
        }

        _state.Disabled.Remove(id);
        _state.Approved.Remove(id);
        SaveState();
    }

    public void SetDisabled(string id, bool disabled)
    {
        if (disabled)
        {
            _state.Disabled.Add(id);
        }
        else
        {
            _state.Disabled.Remove(id);
        }

        SaveState();
    }

    /// <summary>Ob der Benutzer genau diesen Text aus diesem Paket für immer freigegeben hat.</summary>
    public bool IsApproved(string id, string text) => _state.Approved.TryGetValue(id, out var texts) && texts.Contains(text);

    public void Approve(string id, string text)
    {
        if (!_state.Approved.TryGetValue(id, out var texts))
        {
            _state.Approved[id] = texts = [];
        }

        if (texts.Add(text))
        {
            SaveState();
        }
    }

    // Die Kennung ist geprüft (Kleinbuchstaben, Ziffern, Punkt, Bindestrich) – sie taugt als Dateiname.
    private string PathOf(string id) => Path.Combine(directory, id + ExtensionFormat.FileExtension);

    private State ReadState()
    {
        try
        {
            var path = Path.Combine(directory, "state.json");
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), ExtensionStateJsonContext.Default.State) is { } state)
            {
                state.Disabled ??= [];
                state.Approved ??= [];
                return state;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // Dann eben ohne: alles an, nichts freigegeben.
        }

        return new State();
    }

    private void SaveState()
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(_state, ExtensionStateJsonContext.Default.State));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gilt bis zum Ende der App.
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    internal sealed class State
    {
        public HashSet<string> Disabled { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<string>> Approved { get; set; } = new(StringComparer.Ordinal);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExtensionStore.State))]
internal sealed partial class ExtensionStateJsonContext : JsonSerializerContext;
