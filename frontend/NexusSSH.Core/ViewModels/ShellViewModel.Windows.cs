using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Ein Tab in einem eigenen Fenster: Dort steht nur sein Terminal. Die Sitzung bleibt, wo sie ist – in der Liste der
// Sitzungen, mit ihrem Reiter im Hauptfenster; wählt man ihn dort, zeigen Dateibrowser und Server-Info sie wie
// jede andere, nur die Terminal-Fläche nennt statt des Terminals das Fenster. Die Fenster selbst baut die App.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Eine Sitzung ist in ein eigenes Fenster umgezogen oder daraus zurückgekehrt (<see cref="SessionViewModel.IsDetached"/>
    /// sagt, wohin). Die App öffnet oder schließt daraufhin das Fenster und zieht das Terminal um.
    /// </summary>
    public event Action<SessionViewModel>? DetachedChanged;

    /// <summary>Ob der aktive Tab sein Terminal in einem eigenen Fenster hat – dann zeigt die Terminal-Fläche einen Hinweis.</summary>
    public bool ShowsDetachedNote => ActiveSession is { IsDetached: true } && ShowsTerminal;

    /// <summary>
    /// Ob sich die Sitzung in ein eigenes Fenster lösen lässt. Fenster von tmux und ihr Träger bleiben, wo sie sind:
    /// Ihre Anordnung gehört tmux.
    /// </summary>
    public bool CanDetach(SessionViewModel? session)
        => session is { IsVirtual: false, IsDetached: false } && Sessions.Contains(session) && !CarriesTmux(session);

    /// <summary>
    /// Löst die Sitzung in ein eigenes Fenster. Aus einer Teilung kommt sie dabei heraus – die übrigen bleiben geteilt.
    /// </summary>
    /// <param name="bounds">Wo das Fenster stehen soll; <c>null</c> lässt die App wählen.</param>
    public bool Detach(SessionViewModel session, WindowBounds? bounds = null)
    {
        if (!CanDetach(session))
        {
            return false;
        }

        RemoveFromSplit(session);
        session.DetachedBounds = bounds ?? session.DetachedBounds;
        session.IsDetached = true;
        UpdateLayout();
        DetachedChanged?.Invoke(session);
        return true;
    }

    /// <summary>Holt die Sitzung aus ihrem Fenster zurück ins Hauptfenster und macht ihren Tab aktiv.</summary>
    public bool Reattach(SessionViewModel session)
    {
        if (!session.IsDetached || !Sessions.Contains(session))
        {
            return false;
        }

        session.IsDetached = false;
        ActiveSession = session;
        UpdateLayout();
        DetachedChanged?.Invoke(session);
        return true;
    }
}
