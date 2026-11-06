using System.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Das Betriebssystem eines Servers, für das Symbol an Kachel und Kopfzeile. Ist es am Profil noch nicht vermerkt,
// liest die App beim Verbinden /etc/os-release über die Dateiverbindung – das geht auch, wo der Server keine Befehle
// zulässt oder die Messung scheitert. Findet sich dort nichts, wird einmal gemessen (die Auslastung nennt das System
// ebenfalls) – nur dieses eine Mal, danach ruht die Messung wieder, solange den Inspector niemand ansieht.
public sealed partial class ShellViewModel
{
    private void WatchSystem(SessionViewModel session)
    {
        session.Settled += () =>
        {
            if (session.State == ConnectionState.Connected && session.System is null && session.Profile.IsSsh)
            {
                _ = RecognizeSystemAsync(session);
            }
        };
        session.Inspector.PropertyChanged += (_, e) => OnInspectorChanged(session, e);
    }

    internal async Task RecognizeSystemAsync(SessionViewModel session)
    {
        var monitor = session.Inspector.Monitor;
        if (session.Connection.Files is { } files)
        {
            try
            {
                var part = await files.ReadAsync("/etc/os-release", 0, ServerSystems.OsReleaseLength);
                if (ServerSystems.FromOsRelease(part.Data) is { } system)
                {
                    Recognize(session, system);
                    return;
                }
            }
            catch (RemoteFileException)
            {
                // Keine Datei (OpenBSD, Windows) oder kein Zugriff: dann eben die Messung.
            }

            // Inzwischen getrennt oder neu verbunden: Die neue Verbindung fragt selbst.
            if (session.State != ConnectionState.Connected || !ReferenceEquals(monitor, session.Inspector.Monitor) || session.System is not null)
            {
                return;
            }
        }

        session.Inspector.Probe();
    }

    private void OnInspectorChanged(SessionViewModel session, PropertyChangedEventArgs e)
    {
        // Warum der Server keine Auslastung liefert, sagt nur der Kern – ins Protokoll damit, sonst ist es weg.
        if (e.PropertyName == nameof(ServerInspectorViewModel.IsUnavailable)
            && session.Inspector is { IsUnavailable: true, UnavailableDetail: { Length: > 0 } detail })
        {
            Log(L.Get("LogMonitorUnavailable", session.Profile.Name, detail), LogSeverity.Warning);
        }

        if (e.PropertyName == nameof(ServerInspectorViewModel.OsName) && ServerSystems.Detect(session.Inspector.OsName) is { } system)
        {
            Recognize(session, system);
        }
    }

    private void Recognize(SessionViewModel session, ServerSystem system)
    {
        if (session.System == system.Id)
        {
            return;
        }

        session.System = system.Id;

        // Am Profil, wie es jetzt gespeichert ist – die Sitzung kann älter sein als seine letzte Änderung. Gibt es
        // das Profil nicht mehr, bleibt es beim Symbol dieser Sitzung.
        if (_profiles.FirstOrDefault(profile => profile.Id == session.Profile.Id) is { } stored && stored.System != system.Id)
        {
            SaveProfile(stored with { System = system.Id });
        }
    }
}
