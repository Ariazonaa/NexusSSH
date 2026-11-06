using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Der Weg über Sprung-Server: welche Server der Reihe nach, und an welchem es gerade liegt – damit das Terminal
// den Weg nennen kann und, wenn der Aufbau scheitert, an welchem Server.
public sealed partial class SessionViewModel
{
    private IReadOnlyList<ServerProfile> _via = [];

    // Der Schritt, an dem der Aufbau gerade ist (0 = erster Sprung-Server), und ob er dort gescheitert ist.
    private int _step = -1;
    private bool _stepFailed;

    /// <summary>
    /// Die Sprung-Server in der Reihenfolge, in der verbunden wird. Nennt die Shell beim Öffnen; der Kern bildet
    /// den Weg selbst (<c>ProfileStore::chain</c>) – das hier ist nur für die Anzeige.
    /// </summary>
    public IReadOnlyList<ServerProfile> Via
    {
        get => _via;
        set
        {
            _via = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRoute));
        }
    }

    /// <summary>Ob die Verbindung über mindestens einen Sprung-Server läuft.</summary>
    public bool HasRoute => _via.Count > 0;

    /// <summary>Der Weg in einer Zeile: „Dieser Rechner → bastion → web01“.</summary>
    public string RouteLine => string.Join(" → ", [L.Get("RouteHere"), .. _via.Select(jump => jump.Name), Profile.Name]);

    // Ein neuer Versuch beginnt vorn.
    private void RestartRoute()
    {
        _step = -1;
        _stepFailed = false;
        if (HasRoute)
        {
            Print(L.Get("TerminalRoute", RouteLine));
        }
    }

    // Zieht den Stand des Wegs aus einer Meldung der Verbindung nach.
    private void FollowRoute(ConnectionState previous, ConnectionStatus status)
    {
        if (!HasRoute)
        {
            return;
        }

        switch (status.State)
        {
            case ConnectionState.Connecting or ConnectionState.Authenticating:
                _stepFailed = false;
                _step = StepOf(status.Hop, previous);
                break;

            case ConnectionState.Connected:
                _stepFailed = false;
                _step = _via.Count + 1;
                break;

            // Gescheitert ist ein Schritt nur, wenn der Aufbau noch lief – und nicht, wenn der Benutzer abbrach.
            case ConnectionState.Closed when previous is ConnectionState.Connecting or ConnectionState.Authenticating
                && status.Reason != CloseReason.User && _step >= 0:
                _stepFailed = true;
                break;

            case ConnectionState.Closed:
                _stepFailed = false;
                _step = -1;
                break;
        }
    }

    // Der Kern nennt den Server so, wie die App ihn nennt (`benutzer@host`). Passt nichts – das Profil wurde
    // inzwischen geändert –, gilt: Mit jedem neuen „verbindet“ ist der nächste an der Reihe.
    private int StepOf(string? hop, ConnectionState previous)
    {
        var from = Math.Max(_step, 0);
        for (var index = from; index <= _via.Count; index++)
        {
            if (hop is not null && StepProfile(index).Address == hop)
            {
                return index;
            }
        }

        return State == ConnectionState.Connecting && previous == ConnectionState.Authenticating
            ? Math.Min(from + 1, _via.Count)
            : from;
    }

    private ServerProfile StepProfile(int index) => index < _via.Count ? _via[index] : Profile;

    private string StepName(int index) => StepProfile(index).Name;
}
