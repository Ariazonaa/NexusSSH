using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Gemerkte Abgleiche: ein lokaler Ordner und einer am Server samt Richtung, Vergleich und Ausschlüssen unter einem
// Namen. Ausführen heißt dasselbe wie beim ersten Mal – erst die Vorschau, dann der Start –, nur ohne die Auswahl
// davor. Eine Verbindung zum Server nimmt der Abgleich, wo es eine gibt; sonst öffnet er einen Tab und wartet, bis
// der steht. Gelöscht wird am Ziel nie von selbst: Das Kästchen in der Vorschau ist auch hier nie vorgewählt.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Die Vorschau eines Abgleichs: zeigt den Plan und fragt, ob er beginnen soll – <c>null</c> bei Abbruch, sonst
    /// ob vorher gelöscht werden soll, was nur am Ziel liegt. Setzt die Oberfläche; ohne sie beginnt nichts.
    /// </summary>
    public Func<SyncPlan, Task<bool?>>? ConfirmSync { get; set; }

    /// <summary>Die gemerkten Abgleiche, nach Namen, jeder mit Server, Richtung und Ordnern für die Liste.</summary>
    public IReadOnlyList<SavedSync> SavedSyncs
        => BindableList.Of(Settings.SavedSyncs
            .OrderBy(sync => sync.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(sync => sync with { Summary = Describe(sync) }));

    /// <summary>Ob es schon einen gemerkten Abgleich dieses Namens gibt.</summary>
    public bool HasSavedSync(string name) => FindSync(name) is not null;

    /// <summary>
    /// Merkt sich einen Abgleich unter diesem Namen; einen vorhandenen gleichen Namens ersetzt das (er behält seine
    /// Kennung). Liefert den gemerkten, oder <c>null</c>, wenn etwas daran nicht taugt.
    /// </summary>
    public SavedSync? SaveSync(string name, ServerProfile profile, string localFolder, string remoteFolder, SyncDirection direction, bool byContent, IReadOnlyList<string> exclude)
    {
        if (!SavedSync.IsValidName(name) || profile.IsTransient)
        {
            return null;
        }

        var saved = new SavedSync(
            FindSync(name)?.Id ?? Guid.NewGuid().ToString("N"),
            name.Trim(),
            profile.Id,
            localFolder,
            remoteFolder,
            direction,
            byContent,
            TransferExcludes.Clean(exclude));
        if (!saved.IsUsable || (FindSync(name) is null && Settings.SavedSyncs.Count >= SavedSync.MaxCount))
        {
            return null;
        }

        Settings.SavedSyncs = [.. Settings.SavedSyncs.Where(other => other.Id != saved.Id), saved];
        OnPropertyChanged(nameof(SavedSyncs));
        Log(L.Get("LogSyncSaved", saved.Name));
        return saved;
    }

    /// <summary>Gibt einem gemerkten Abgleich einen anderen Namen. <c>false</c>, wenn der Name nicht taugt oder vergeben ist.</summary>
    public bool RenameSync(SavedSync sync, string name)
    {
        if (!SavedSync.IsValidName(name) || FindSync(name) is { } other && other.Id != sync.Id)
        {
            return false;
        }

        Settings.SavedSyncs = [.. Settings.SavedSyncs.Select(saved => saved.Id == sync.Id ? saved with { Name = name.Trim() } : saved)];
        OnPropertyChanged(nameof(SavedSyncs));
        return true;
    }

    public void DeleteSync(SavedSync sync)
    {
        Settings.SavedSyncs = [.. Settings.SavedSyncs.Where(other => other.Id != sync.Id)];
        OnPropertyChanged(nameof(SavedSyncs));
    }

    /// <summary>
    /// Gleicht einen gemerkten Abgleich erneut ab: über eine Verbindung zu seinem Server (eine stehende, sonst ein
    /// neuer Tab, auf den gewartet wird), mit der Vorschau davor. Liefert, ob der Abgleich begonnen hat; warum
    /// nicht, steht im Protokoll.
    /// </summary>
    public async Task<bool> RunSavedSyncAsync(SavedSync sync)
    {
        if (ConfirmSync is not { } confirm)
        {
            return false;
        }

        if (await SessionForSyncAsync(sync) is not { } session)
        {
            return false;
        }

        // Der Abgleich gehört zu diesem Tab: Er steht vorn, damit sein Dateibrowser zeigt, was geschieht.
        ActiveSession = session;
        if (await session.Files.PlanSyncAsync(sync.LocalFolder, sync.Direction, sync.ByContent, sync.Exclude, sync.RemoteFolder) is not { } plan)
        {
            Log(L.Get("LogSyncFailed", sync.Name, session.Files.Error ?? string.Empty), LogSeverity.Error);
            return false;
        }

        if (await confirm(plan) is not { } deleteExtra)
        {
            return false;
        }

        return await session.Files.StartSyncAsync(plan, deleteExtra);
    }

    // Eine Sitzung zum Server des Abgleichs mit Dateiverbindung: die aktive, sonst eine andere, die steht oder gerade
    // verbindet – sonst ein neuer Tab. Auf eine, die noch verbindet, wird gewartet (eine Rückfrage nach dem Passwort
    // kann dauern); endet sie, ohne zu stehen, gibt es keine.
    private async Task<SessionViewModel?> SessionForSyncAsync(SavedSync sync)
    {
        var candidates = Sessions
            .Where(session => session.Profile.Id == sync.ProfileId && !session.IsVirtual && !session.IsClosed)
            .OrderByDescending(session => ReferenceEquals(session, ActiveSession))
            .ThenByDescending(session => session.State == ConnectionState.Connected)
            .ToList();
        var session = candidates.FirstOrDefault(candidate => candidate is { State: ConnectionState.Connected, Files.IsAvailable: true })
            ?? candidates.FirstOrDefault(candidate => candidate.IsSettling || candidate.State == ConnectionState.Idle);
        if (session is null)
        {
            if (_profiles.FirstOrDefault(profile => profile.Id == sync.ProfileId) is not { } profile)
            {
                Log(L.Get("LogSyncNoServer", sync.Name), LogSeverity.Warning);
                return null;
            }

            session = AddSession(profile, Sessions.Count, activate: false);
        }

        if (session.State is not (ConnectionState.Connected or ConnectionState.Closed))
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done() => settled.TrySetResult();
            session.Settled += Done;
            try
            {
                // Verbunden wird erst, wenn das Terminal zu sehen ist – ein Tab, der noch wartet, kommt nach vorn.
                ActiveSession = session;
                if (session.State is not (ConnectionState.Connected or ConnectionState.Closed))
                {
                    await settled.Task;
                }
            }
            finally
            {
                session.Settled -= Done;
            }
        }

        if (session is not { State: ConnectionState.Connected, Files.IsAvailable: true })
        {
            Log(L.Get("LogSyncNoFiles", sync.Name, Format.Visible(session.Profile.Name)), LogSeverity.Warning);
            return null;
        }

        return session;
    }

    private SavedSync? FindSync(string name)
        => Settings.SavedSyncs.FirstOrDefault(sync => string.Equals(sync.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // Server, dann die Ordner in der Richtung des Abgleichs.
    private string Describe(SavedSync sync)
    {
        var server = _profiles.FirstOrDefault(profile => profile.Id == sync.ProfileId)?.Name ?? L.Get("SyncMissingServer");
        var (from, to) = sync.Direction == SyncDirection.Upload
            ? (sync.LocalFolder, $"{server}:{sync.RemoteFolder}")
            : ($"{server}:{sync.RemoteFolder}", sync.LocalFolder);
        return Format.Visible($"{from} → {to}");
    }
}
