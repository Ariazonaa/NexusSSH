using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Schnellverbinden: `benutzer@host[:port]` in der Palette öffnet eine Sitzung, ohne dass es einen Server dazu gibt.
// Darunter liegt ein vorläufiges Profil (`ServerProfile.IsTransient`): Der Kern braucht für alles – Anmeldung,
// Host-Schlüssel, Dateien, Transfers – ein Profil, und so verhält sich die Sitzung wie jede andere. In den Listen
// taucht es nicht auf; es fällt weg, sobald der letzte Tab dazu schließt, und spätestens beim nächsten Start des
// Kerns. Behalten macht daraus einen gewöhnlichen Server.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Verbindet mit einem Ziel, wie man es <c>ssh</c> nennt. Gibt es den Server schon (gleicher Benutzer, Host und
    /// Port), öffnet sich der – es entsteht kein zweites, vorläufiges Profil daneben. <c>null</c>, wenn die Eingabe
    /// kein Ziel ist oder der Kern es nicht annimmt.
    /// </summary>
    public SessionViewModel? QuickConnect(string? text)
    {
        if (QuickTarget.Parse(text) is not { } target)
        {
            return null;
        }

        var known = _profiles.FirstOrDefault(profile => profile.IsSsh
            && profile.Port == target.Port
            && string.Equals(profile.User, target.User, StringComparison.Ordinal)
            && string.Equals(profile.Host, target.Host, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            return OpenSession(known);
        }

        var quick = SaveProfile(new ServerProfile(string.Empty, target.Display, target.Host, target.Port, target.User, string.Empty, [], IsTransient: true));
        if (quick is null)
        {
            return null;
        }

        var session = OpenSession(quick);
        session.IsQuick = true;
        return session;
    }

    /// <summary>Ob sich aus der aktiven Sitzung ein Server machen lässt: Sie kam über das Schnellverbinden.</summary>
    public bool CanKeepQuick => ActiveSession is { IsQuick: true };

    /// <summary>
    /// Macht aus der Verbindung einer Sitzung einen gewöhnlichen Server: dasselbe Profil, nun ohne die Marke. Alle
    /// Tabs dazu gelten danach als gewöhnlich. <c>false</c>, wenn es nichts zu behalten gab oder das Speichern scheiterte.
    /// </summary>
    public bool KeepQuick(SessionViewModel session)
    {
        if (!session.IsQuick || SaveProfile(session.Profile with { IsTransient = false }) is null)
        {
            return false;
        }

        foreach (var other in Sessions.Where(other => other.Profile.Id == session.Profile.Id))
        {
            other.IsQuick = false;
        }

        OnPropertyChanged(nameof(CanKeepQuick));
        Log(L.Get("LogQuickKept", session.Profile.Name));
        return true;
    }

    // Schließt der letzte Tab zu einem vorläufigen Profil, fällt es weg – samt dem, was der Kern dazu gemerkt hat.
    private void DropQuick(SessionViewModel closed)
    {
        if (!closed.IsQuick || Sessions.Any(session => session.Profile.Id == closed.Profile.Id))
        {
            return;
        }

        try
        {
            _backend.Profiles.Delete(closed.Profile.Id);
            Settings.Bookmarks.Remove(closed.Profile.Id);
        }
        catch (BackendException)
        {
            // Der Kern räumt es beim nächsten Start selbst weg.
        }
    }
}
