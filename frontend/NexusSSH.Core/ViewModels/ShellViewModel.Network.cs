using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Kleine Werkzeuge rund um den Weg zum Server: Verbindungstest und Traceroute.
public sealed partial class ShellViewModel
{
    private IDisposable? _trace;

    /// <summary>
    /// Verbindungstest für ein Profil, wie es im Dialog steht (gespeichert oder nicht) – mit dem, was die Identität
    /// dazu beiträgt. Liefert den Bericht als Text; ein Profil, das so nicht taugt, ergibt den Satz dazu.
    /// </summary>
    public async Task<string> ProbeAsync(ServerProfile profile)
    {
        try
        {
            return ProbeWording.Text(await _backend.ProbeAsync(WithIdentity(profile)));
        }
        catch (BackendException e)
        {
            return $"{L.Get("ProbeFailed")} ({e.Code}: {e.Message})";
        }
    }

    /// <summary>
    /// Liest die mit <c>~/.ssh/config</c> verknüpften Profile neu aus der Datei – das geschieht auch bei jedem Start;
    /// der Befehl ist für den Fall, dass die Datei sich geändert hat, während die App läuft. Liefert, wie viele
    /// Profile sich geändert haben.
    /// </summary>
    public int RefreshLinkedProfiles()
    {
        try
        {
            var changed = _backend.Profiles.RefreshSshConfig();
            Log(L.Get("LogLinkedRefreshed", changed));
            return changed;
        }
        catch (BackendException e)
        {
            Log(L.Get("LogImportFailed", $"{e.Code}: {e.Message}"), LogSeverity.Error);
            return 0;
        }
    }

    /// <summary>
    /// Verfolgt den Weg zum Server der Sitzung (Traceroute); die Zeilen erscheinen im Reiter „Ausgabe“. Ein Lauf
    /// zur Zeit – ein neuer beendet den alten. Bei einem Weg über Sprung-Server geht es bis zum ersten davon: Weiter
    /// reicht der Blick dieses Rechners nicht.
    /// </summary>
    public bool TraceRoute(SessionViewModel? session)
    {
        if (session is null || RouteTrace.Target(session.Profile, Profiles) is not { } host)
        {
            return false;
        }

        _trace?.Dispose();
        Log(L.Get("TraceStarted", host));
        IsBottomPanelVisible = true;
        _trace = RouteTrace.Start(
            host,
            line => _dispatcher.Post(() => Log(Format.Visible(line))),
            failure => _dispatcher.Post(() =>
            {
                if (failure is null)
                {
                    Log(L.Get("TraceEnded", host));
                }
                else
                {
                    Log(L.Get("TraceFailed", failure), LogSeverity.Warning);
                }
            }));
        return true;
    }
}
