using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Lokale Terminals: eine Shell dieses Rechners in einem Tab (Strg+Umschalt+T, die Palette, ein gespeichertes Profil
// mit `protocol: local`). Wie beim Schnellverbinden steckt darunter ein Profil – der Kern braucht für alles eines –,
// und die schnelle Fassung ist vorläufig: Es fällt mit dem letzten Tab dazu weg, „Behalten“ speichert es
// (`ShellViewModel.Quick.cs`). Was der Kern startet, entscheidet er aus der Kennung der Shell, die er selbst gefunden
// hat; die App nennt nie einen Pfad oder Argumente. Ein lokaler Tab hat keinen Dateibrowser und keine
// Server-Info: Es gibt keinen Server.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Die Shells dieses Rechners, wie der Kern sie fand. Anfangs leer: Unter Windows fragt der Kern dafür
    /// <c>wsl.exe</c> und braucht bis zu drei Sekunden – sie kommen kurz nach dem Start.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalShellChoices), nameof(DefaultLocalShellIndex))]
    public partial IReadOnlyList<LocalShell> LocalShells { get; private set; }

    /// <summary>
    /// Die Auswahl in den Einstellungen: zuerst „automatisch“ (leere Kennung), dann die gefundenen Shells. Eine
    /// Shell, die der Benutzer gewählt hat und die der Kern nicht mehr findet, steht trotzdem darin – sonst
    /// verschwände die Wahl still.
    /// </summary>
    public IReadOnlyList<LocalShell> LocalShellChoices
    {
        get
        {
            List<LocalShell> choices = [new(string.Empty, L.Get("LocalShellAutomatic")), .. LocalShells];
            if (Settings.LocalShell.Length > 0 && choices.TrueForAll(choice => choice.Id != Settings.LocalShell))
            {
                choices.Add(new LocalShell(Settings.LocalShell, LocalShellNames.Of(Settings.LocalShell)));
            }

            return BindableList.Of(choices);
        }
    }

    /// <summary>
    /// Die Shell, die Strg+Umschalt+T öffnet, als Nummer in <see cref="LocalShellChoices"/> (0 = automatisch). Gilt
    /// nur für diesen Rechner und steht nicht im Export.
    /// </summary>
    public int DefaultLocalShellIndex
    {
        get
        {
            var index = LocalShellChoices.ToList().FindIndex(choice => choice.Id == Settings.LocalShell);
            return Math.Max(0, index);
        }
        set
        {
            var choices = LocalShellChoices;
            if (value >= 0 && value < choices.Count && choices[value].Id != Settings.LocalShell)
            {
                Settings.LocalShell = choices[value].Id;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die Kennung der Shell für Strg+Umschalt+T; leer = die erste, die der Kern fand (PowerShell 7 vor Windows
    /// PowerShell, unter Unix die Anmelde-Shell).
    /// </summary>
    public string DefaultLocalShell
    {
        get => Settings.LocalShell;
        set
        {
            var id = LocalShellNames.IsId(value) ? value : string.Empty;
            if (id != Settings.LocalShell)
            {
                Settings.LocalShell = id;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultLocalShellIndex));
                OnPropertyChanged(nameof(LocalShellChoices));
            }
        }
    }

    /// <summary>Der aktive Tab ist ein lokales Terminal – dann gibt es weder Dateien noch Auslastung dazu.</summary>
    public bool IsLocalActive => ActiveSession is { Profile.IsLocal: true };

    // Fragt den Kern nach den Shells; ohne Antwort bleibt die Liste leer, und Strg+Umschalt+T nimmt die Shell, die
    // es auf jedem System gibt.
    internal async Task LoadLocalShellsAsync()
    {
        IReadOnlyList<LocalShell> found;
        try
        {
            found = await _backend.LocalShellsAsync();
        }
        catch (Exception e) when (e is BackendException or InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        _dispatcher.Post(() => LocalShells = found);
    }

    /// <summary>
    /// Öffnet einen Tab mit einer Shell dieses Rechners: die genannte (<paramref name="shellId"/>, eine Kennung aus
    /// <see cref="LocalShells"/>) oder die eingestellte, sonst die erste, die der Kern fand. Das Profil dahinter ist
    /// vorläufig (<see cref="SessionViewModel.IsQuick"/>). <c>null</c>, wenn die Kennung keine sein kann oder der
    /// Kern das Profil nicht annimmt.
    /// </summary>
    public SessionViewModel? OpenLocalTerminal(string? shellId = null)
    {
        var shell = shellId is null ? ChosenLocalShell() : LocalShellNames.IsId(shellId) ? ShellFor(shellId) : null;
        if (shell is null)
        {
            Log(L.Get("LogNoLocalShell"), LogSeverity.Warning);
            return null;
        }

        var profile = SaveProfile(new ServerProfile(
            string.Empty,
            LocalShellNames.Of(shell.Id, shell.Name),
            shell.Id,
            22, // der Kern prüft den Port eines lokalen Terminals nicht
            string.Empty,
            string.Empty,
            [],
            Protocol: ServerProfile.Local,
            IsTransient: true));
        if (profile is null)
        {
            return null;
        }

        var session = OpenSession(profile);
        session.IsQuick = true;
        return session;
    }

    private LocalShell? ShellFor(string id) => LocalShells.FirstOrDefault(shell => shell.Id == id) ?? new LocalShell(id, LocalShellNames.Of(id));

    // Was Strg+Umschalt+T öffnet: die eingestellte Shell, solange sie gültig ist und – sobald der Kern die Liste
    // genannt hat – es sie hier noch gibt; sonst die bevorzugte. Ohne Liste die, die es auf jedem System gibt.
    private LocalShell? ChosenLocalShell()
    {
        var wanted = Settings.LocalShell;
        if (wanted.Length > 0 && LocalShellNames.IsId(wanted) && (LocalShells.Count == 0 || LocalShells.Any(shell => shell.Id == wanted)))
        {
            return ShellFor(wanted);
        }

        return LocalShellNames.Preferred(LocalShells)
            ?? new LocalShell(OperatingSystem.IsWindows() ? "powershell" : "login", string.Empty);
    }
}
