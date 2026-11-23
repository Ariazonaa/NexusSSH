namespace NexusSSH.Core.Services;

/// <summary>Ein Plugin ließ sich nicht lesen oder nicht ausführen; <see cref="Code"/> sagt, woran es lag.</summary>
public sealed class PluginException(string code, string message) : Exception(message)
{
    /// <summary>
    /// <c>plugin_invalid</c>, <c>plugin_missing</c>, <c>plugin_changed</c> (die Datei ist nicht mehr die installierte),
    /// <c>plugin_fuel</c> (rechnete zu lange), <c>plugin_memory</c>, <c>plugin_trap</c> (brach ab), <c>plugin_output</c>
    /// (unbrauchbare Antwort), <c>plugin_input</c>.
    /// </summary>
    public string Code => code;

    /// <summary>Ob das Plugin selbst versagt hat (statt der Datei) – dann schaltet die App es ab.</summary>
    public bool IsMisbehaving => code is "plugin_fuel" or "plugin_memory" or "plugin_trap" or "plugin_output";
}

/// <summary>Was die Vorschau vor dem Installieren bekommt: die Beschreibung (JSON, ungeprüft) und der SHA-256 der Datei.</summary>
public sealed record PluginInspection(string Manifest, string Sha256);

/// <summary>
/// Führt Plugins aus – im Kern (WebAssembly, <c>plugins.rs</c>): ohne Importe, mit begrenzten Rechenschritten und
/// begrenztem Speicher, je Aufruf eine frische Instanz. Was ein Plugin antwortet, ist fremd; die App prüft es.
/// </summary>
public interface IPluginRuntime
{
    /// <summary>Liest und prüft die Datei. Wirft <see cref="PluginException"/>.</summary>
    PluginInspection Inspect(string path);

    /// <summary>
    /// Ruft das Plugin auf – nur, wenn die Datei noch <paramref name="sha256"/> hat. Liefert seine Antwort (JSON, fremd).
    /// Wirft <see cref="PluginException"/>.
    /// </summary>
    Task<string> CallAsync(string path, string sha256, string input, CancellationToken cancellation = default);
}
