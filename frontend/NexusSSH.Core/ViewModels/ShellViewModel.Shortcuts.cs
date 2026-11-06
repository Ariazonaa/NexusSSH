using NexusSSH.Core.Localization;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Zeile der Liste „Tastenkürzel“ in den Einstellungen.</summary>
/// <param name="Shortcut">Das Kürzel, das gerade gilt, zum Anzeigen; leer, wenn der Befehl keines hat.</param>
/// <param name="IsCustom">Der Benutzer hat es geändert oder entfernt – dann lässt es sich zurücksetzen.</param>
public sealed record ShortcutRow(string Id, string Title, string Shortcut, KeyChord? Chord, bool IsCustom)
{
    public override string ToString() => Shortcut.Length > 0 ? $"{Title}, {Shortcut}" : Title;
}

// Tastenkürzel selbst belegen. Die Vorgaben stehen bei den Befehlen (BuildCommands); was der Benutzer ändert, steht
// in den Einstellungen dieses Rechners (AppSettings.Shortcuts: Kennung des Befehls → Kürzel, leer = keines). Fenster
// und Terminal-Seite lesen beide aus Commands – ändert sich dort etwas, bekommen sie es über PropertyChanged mit.
public sealed partial class ShellViewModel
{
    private IReadOnlyList<ShellCommand> _defaultCommands = [];

    /// <summary>Die Befehle mit ihren Kürzeln, für die Einstellungen – ohne die Farbschemata, die keines brauchen.</summary>
    public IReadOnlyList<ShortcutRow> ShortcutRows
        => BindableList.Of(Commands
            .Where(command => !command.Id.StartsWith("theme.", StringComparison.Ordinal))
            .Select(command => new ShortcutRow(command.Id, command.Title, command.Shortcut?.Display ?? string.Empty, command.Shortcut, Settings.Shortcuts.ContainsKey(command.Id))));

    /// <summary>
    /// Belegt einen Befehl mit einem Kürzel; <c>null</c> nimmt ihm seines. Liefert <c>null</c>, wenn es gilt – sonst
    /// den Grund, in der Sprache der Oberfläche: Das Kürzel lässt sich nicht belegen, oder ein anderer Befehl hat es.
    /// </summary>
    public string? SetShortcut(string commandId, KeyChord? chord)
    {
        if (ShortcutProblem(commandId, chord) is { } problem)
        {
            return problem;
        }

        var command = _defaultCommands.First(candidate => candidate.Id == commandId);

        // Was der Vorgabe gleicht, braucht keinen Eintrag.
        if (chord == command.Shortcut)
        {
            Settings.Shortcuts.Remove(commandId);
        }
        else
        {
            Settings.Shortcuts[commandId] = chord?.ToSetting() ?? string.Empty;
        }

        ApplyShortcuts();
        return null;
    }

    /// <summary>
    /// Warum sich ein Befehl nicht so belegen lässt – ohne etwas zu ändern, damit der Dialog es schon beim Wählen
    /// sagt. <c>null</c>, wenn nichts dagegen spricht.
    /// </summary>
    public string? ShortcutProblem(string commandId, KeyChord? chord)
    {
        if (_defaultCommands.All(command => command.Id != commandId))
        {
            return L.Get("ShortcutUnknownCommand");
        }

        if (chord is { IsAllowed: false })
        {
            return L.Get("ShortcutNotAllowed");
        }

        return chord is not null && Commands.FirstOrDefault(other => other.Id != commandId && other.Shortcut == chord) is { } taken
            ? L.Get("ShortcutTaken", chord.Display, taken.Title)
            : null;
    }

    /// <summary>Gibt einem Befehl sein vorgegebenes Kürzel zurück – wenn das inzwischen kein anderer hat.</summary>
    public string? ResetShortcut(string commandId)
        => _defaultCommands.FirstOrDefault(command => command.Id == commandId) is { } command
            ? SetShortcut(commandId, command.Shortcut)
            : L.Get("ShortcutUnknownCommand");

    /// <summary>
    /// Übernimmt die eigenen Kürzel einer Export-Datei – sie ersetzen die bisherigen. Die Datei ist fremde Eingabe:
    /// Es zählt nur, was einen Befehl dieser Version nennt und ein belegbares Kürzel ist (oder leer: keines); zwei
    /// Befehle mit demselben Kürzel löst <see cref="ApplyShortcuts"/> wie bei den Einstellungen auf.
    /// </summary>
    public void ImportShortcuts(IReadOnlyDictionary<string, string> shortcuts)
    {
        Settings.Shortcuts = shortcuts
            .Where(entry => entry.Value is not null
                && _defaultCommands.Any(command => command.Id == entry.Key)
                && (entry.Value.Length == 0 || KeyChord.Parse(entry.Value) is { IsAllowed: true }))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        ApplyShortcuts();
    }

    /// <summary>Alle Kürzel wie vorgegeben.</summary>
    public void ResetShortcuts()
    {
        Settings.Shortcuts.Clear();
        ApplyShortcuts();
    }

    // Die Vorgaben mit dem, was der Benutzer geändert hat. Ein Eintrag, der nicht (mehr) taugt – unbekannter Befehl,
    // nicht belegbar, oder das Kürzel hat inzwischen ein anderer –, wird übergangen, statt etwas doppelt zu belegen.
    private void ApplyShortcuts()
    {
        var taken = new HashSet<KeyChord>();
        var custom = new Dictionary<string, KeyChord?>(StringComparer.Ordinal);
        foreach (var (id, setting) in Settings.Shortcuts)
        {
            if (_defaultCommands.Any(command => command.Id == id) && (setting.Length == 0 || KeyChord.Parse(setting) is { } parsed && taken.Add(parsed)))
            {
                custom[id] = KeyChord.Parse(setting);
            }
        }

        Commands =
        [
            .. _defaultCommands.Select(command => custom.TryGetValue(command.Id, out var chord)
                ? command with { Shortcut = chord }
                : command.Shortcut is { } standard && !taken.Add(standard) ? command with { Shortcut = null } : command),
        ];
        OnPropertyChanged(nameof(Commands));
        OnPropertyChanged(nameof(TerminalShortcuts));
        OnPropertyChanged(nameof(ShortcutRows));
    }
}
