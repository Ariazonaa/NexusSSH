using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Reiter anheften und Befehle für viele Reiter auf einmal. Angeheftete stehen links, haben kein × und bleiben bei
// „alle anderen schließen“ stehen; schließen lassen sie sich weiter mit dem Befehl dafür.
public sealed partial class ShellViewModel
{
    private int PinnedCount => Sessions.Count(session => session.IsPinned);

    /// <summary>Heftet einen Reiter an oder löst ihn; er rückt dabei an das Ende bzw. hinter die Angehefteten.</summary>
    public void TogglePin(SessionViewModel session)
    {
        // Fenster von tmux kommen und gehen mit tmux – sie bleiben, wo ihr Träger sie hinstellt.
        var from = Sessions.IndexOf(session);
        if (from < 0 || session.IsVirtual)
        {
            return;
        }

        var others = Sessions.Count(other => other.IsPinned && !ReferenceEquals(other, session));
        session.IsPinned = !session.IsPinned;

        // Angeheftet: hinter die, die es schon sind. Gelöst: an den Anfang der übrigen. Beides ist dieselbe Stelle.
        if (from != others)
        {
            Sessions.Move(from, others);
        }
    }

    /// <summary>Wohin ein Reiter darf: Angeheftete bleiben unter sich, die übrigen dahinter.</summary>
    private int InZone(SessionViewModel session, int index)
    {
        var pinned = PinnedCount;
        return session.IsPinned ? Math.Min(index, Math.Max(pinned - 1, 0)) : Math.Max(index, pinned);
    }

    /// <summary>Ob es außer diesem Reiter einen gibt, den „alle anderen schließen“ schlösse.</summary>
    public bool HasOthersToClose(SessionViewModel? session)
        => session is not null && Sessions.Any(other => !ReferenceEquals(other, session) && !other.IsPinned && !other.IsVirtual);

    /// <summary>Schließt alle Reiter außer diesem und den angehefteten.</summary>
    public void CloseOthers(SessionViewModel session)
    {
        if (!Sessions.Contains(session))
        {
            return;
        }

        // Fenster von tmux schließen mit ihrem Träger, nicht einzeln (das hinge nur ab).
        foreach (var other in Sessions.Where(other => !ReferenceEquals(other, session) && !other.IsPinned && !other.IsVirtual).ToList())
        {
            // Der Träger dieses Reiters bleibt, sonst ginge er mit.
            if (Sessions.Contains(other) && !(session.IsVirtual && CarriesTmux(other)))
            {
                CloseSession(other);
            }
        }

        if (Sessions.Contains(session))
        {
            ActiveSession = session;
        }
    }

    /// <summary>Ob eine Sitzung verbunden ist oder gerade verbindet.</summary>
    public bool HasConnected => Sessions.Any(session => !session.IsVirtual && session.State is not (ConnectionState.Idle or ConnectionState.Closed));

    /// <summary>Ob eine Sitzung getrennt ist und sich neu verbinden ließe.</summary>
    public bool HasClosed => Sessions.Any(session => session is { IsVirtual: false, IsClosed: true });

    /// <summary>Trennt alle Sitzungen; die Reiter bleiben stehen.</summary>
    public void DisconnectAll()
    {
        foreach (var session in Sessions.Where(session => !session.IsVirtual && session.State is not (ConnectionState.Idle or ConnectionState.Closed)).ToList())
        {
            session.Disconnect();
        }
    }

    /// <summary>Verbindet alle getrennten Sitzungen neu.</summary>
    public void ReconnectAll()
    {
        foreach (var session in Sessions.Where(session => session is { IsVirtual: false, IsClosed: true }).ToList())
        {
            session.Reconnect();
        }
    }
}
