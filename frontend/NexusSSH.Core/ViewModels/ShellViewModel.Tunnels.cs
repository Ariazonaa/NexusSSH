using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Eine Zeile der Tunnel-Listen (Startseite, Inspector): der Tunnel, sein Server und wie es gerade um ihn steht.
/// </summary>
public sealed partial class TunnelRow(ServerProfile profile, ProfileTunnel tunnel) : ObservableObject
{
    public ServerProfile Profile { get; } = profile;

    public ProfileTunnel Tunnel { get; } = tunnel;

    public string Name => Tunnel.DisplayName;

    /// <summary>Die Strecke – unter dem Namen, wenn der Tunnel einen hat.</summary>
    public string Route => Tunnel.Route;

    public string KindText => Tunnel.KindText;

    public string Server => Format.Visible(Profile.Name);

    /// <summary>Für das ganze Netz offen – das soll in jeder Liste zu sehen sein.</summary>
    public bool IsOpen => Tunnel.Open;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening), nameof(StateText), nameof(HasFailed))]
    public partial TunnelState State { get; set; }

    /// <summary>Bei einem Fehlschlag: der Grund, englisch, wie der Kern ihn nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Ob es eine stehende Verbindung zu dem Server gibt – nur dann lässt sich der Tunnel schalten.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool CanSwitch { get; set; }

    public bool IsListening => State == TunnelState.Listening;

    public bool HasFailed => State == TunnelState.Failed;

    public string StateText => State switch
    {
        TunnelState.Listening => L.Get("TunnelListening"),
        TunnelState.Failed => Detail.Length > 0 ? L.Get("TunnelFailedBecause", Format.Visible(Detail)) : L.Get("TunnelFailed"),
        _ when !CanSwitch => L.Get("TunnelNoConnection"),
        _ => L.Get("TunnelStopped"),
    };

    /// <summary>Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Name}, {Server}, {StateText}";
}

// Portweiterleitung: Jedes Profil trägt seine Tunnel; sie stehen, solange eine Verbindung zu dem Server steht. Die
// Startseite listet alle, der Inspector die der aktiven Sitzung. Lauschen und weiterreichen tut allein der Kern –
// hier steht, was es gibt und wie es darum steht.
public sealed partial class ShellViewModel
{
    private readonly Dictionary<(string Profile, string Tunnel), (TunnelState State, string Detail)> _tunnelStates = [];
    private bool _watchesTunnels;

    /// <summary>Alle Tunnel aller Profile, in der Reihenfolge der Serverliste.</summary>
    public ObservableCollection<TunnelRow> Tunnels { get; } = [];

    /// <summary>Die Tunnel des Servers der aktiven Sitzung – dieselben Zeilen wie in <see cref="Tunnels"/>.</summary>
    public ObservableCollection<TunnelRow> SessionTunnels { get; } = [];

    public bool HasTunnels => Tunnels.Count > 0;

    public bool HasSessionTunnels => SessionTunnels.Count > 0;

    /// <summary>Die Profile, die Tunnel tragen können: SSH, kein Telnet, FTP oder serieller Anschluss.</summary>
    public IReadOnlyList<ServerProfile> TunnelServers
        => BindableList.Of(_profiles.Where(profile => profile.Protocol is null or { Length: 0 }));

    /// <summary>Startet einen Tunnel oder hält ihn an. Ohne stehende Verbindung zu dem Server geschieht nichts.</summary>
    public bool SwitchTunnel(TunnelRow row, bool on)
    {
        if (ConnectedTo(row.Profile.Id) is not { } session)
        {
            return false;
        }

        _backend.SetTunnel(session.Connection, row.Tunnel.Id, on);
        return true;
    }

    /// <summary>
    /// Legt einen Tunnel an oder ändert ihn (gleiche Kennung). Läuft er gerade, wird er vorher angehalten – er
    /// lauschte sonst weiter an der alten Stelle. <c>false</c>, wenn der Kern das Profil so nicht annimmt.
    /// </summary>
    public bool SaveTunnel(ServerProfile profile, ProfileTunnel tunnel)
    {
        if (!tunnel.IsValid || _profiles.FirstOrDefault(candidate => candidate.Id == profile.Id) is not { } current)
        {
            return false;
        }

        var tunnels = (current.Tunnels ?? []).ToList();
        var index = tunnel.Id.Length > 0 ? tunnels.FindIndex(existing => existing.Id == tunnel.Id) : -1;
        if (index < 0 && tunnels.Count >= ProfileTunnel.MaxPerProfile)
        {
            return false;
        }

        var wasListening = index >= 0 && StateOf(current.Id, tunnel.Id).State == TunnelState.Listening;
        if (wasListening && ConnectedTo(current.Id) is { } session)
        {
            _backend.SetTunnel(session.Connection, tunnel.Id, on: false);
        }

        if (index >= 0)
        {
            tunnels[index] = tunnel;
        }
        else
        {
            tunnels.Add(tunnel);
        }

        if (SaveProfile(current with { Tunnels = BindableList.Of(tunnels) }) is not { } saved)
        {
            return false;
        }

        // Ein neuer oder geänderter Tunnel, der mit der Verbindung stehen soll, startet gleich – die Verbindung
        // steht ja schon.
        var stored = index >= 0 ? tunnel : saved.Tunnels?.LastOrDefault();
        if (stored is { Auto: true } && ConnectedTo(saved.Id) is { } open)
        {
            _backend.SetTunnel(open.Connection, stored.Id, on: true);
        }

        return true;
    }

    /// <summary>Löscht einen Tunnel; läuft er, endet er.</summary>
    public bool DeleteTunnel(TunnelRow row)
    {
        if (_profiles.FirstOrDefault(candidate => candidate.Id == row.Profile.Id) is not { } current)
        {
            return false;
        }

        if (ConnectedTo(current.Id) is { } session)
        {
            _backend.SetTunnel(session.Connection, row.Tunnel.Id, on: false);
        }

        var left = (current.Tunnels ?? []).Where(tunnel => tunnel.Id != row.Tunnel.Id).ToList();
        _tunnelStates.Remove((current.Id, row.Tunnel.Id));
        return SaveProfile(current with { Tunnels = left.Count > 0 ? BindableList.Of(left) : null }) is not null;
    }

    private SessionViewModel? ConnectedTo(string profileId)
        => Sessions.FirstOrDefault(session => session.Profile.Id == profileId && session is { IsVirtual: false, State: NexusSSH.Core.Services.ConnectionState.Connected });

    private (TunnelState State, string Detail) StateOf(string profile, string tunnel)
        => _tunnelStates.GetValueOrDefault((profile, tunnel), (TunnelState.Stopped, string.Empty));

    // Beim ersten Laden der Profile: Ab jetzt hört die Shell auf das, was das Backend über Tunnel meldet.
    private void WatchTunnels()
    {
        if (_watchesTunnels)
        {
            return;
        }

        _watchesTunnels = true;
        _backend.TunnelChanged += notice => _dispatcher.Post(() => OnTunnelChanged(notice));
    }

    private void OnTunnelChanged(TunnelNotice notice)
    {
        var detail = notice.State == TunnelState.Failed ? notice.Message ?? string.Empty : string.Empty;
        // „Aus“ ist der Zustand, den ohnehin jeder Tunnel hat, von dem nichts bekannt ist – so bleibt von einem
        // gelöschten nichts liegen, das ein späterer mit derselben Kennung erbte.
        if (notice.State == TunnelState.Stopped)
        {
            _tunnelStates.Remove((notice.ProfileId, notice.TunnelId));
        }
        else
        {
            _tunnelStates[(notice.ProfileId, notice.TunnelId)] = (notice.State, detail);
        }

        if (Tunnels.FirstOrDefault(row => row.Profile.Id == notice.ProfileId && row.Tunnel.Id == notice.TunnelId) is not { } row)
        {
            return;
        }

        (row.Detail, row.State) = (detail, notice.State);

        // Ein Tunnel, der nicht zustande kam, gehört unter „Probleme“ – mit dem Grund, den der Kern nennt.
        if (notice.State == TunnelState.Failed)
        {
            Log(L.Get("LogTunnelFailed", row.Server, row.Name, Format.Visible(detail)), LogSeverity.Warning);
        }
    }

    // Nach jedem Laden der Profile und jedem Wechsel der Sitzungen: die Zeilen neu bilden, mit dem Stand von eben.
    private void RefreshTunnels()
    {
        WatchTunnels();
        var rows = new List<TunnelRow>();
        foreach (var profile in _profiles)
        {
            var connected = ConnectedTo(profile.Id) is not null;
            foreach (var tunnel in profile.Tunnels ?? [])
            {
                var (state, detail) = StateOf(profile.Id, tunnel.Id);
                rows.Add(new TunnelRow(profile, tunnel) { Detail = detail, State = connected ? state : TunnelState.Stopped, CanSwitch = connected });
            }
        }

        Replace(Tunnels, rows);
        Replace(SessionTunnels, ActiveSession is { } active ? rows.Where(row => row.Profile.Id == active.Profile.Id) : []);
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(HasSessionTunnels));
        OnPropertyChanged(nameof(TunnelServers));

        static void Replace(ObservableCollection<TunnelRow> list, IEnumerable<TunnelRow> rows)
        {
            list.Clear();
            foreach (var row in rows)
            {
                list.Add(row);
            }
        }
    }
}
