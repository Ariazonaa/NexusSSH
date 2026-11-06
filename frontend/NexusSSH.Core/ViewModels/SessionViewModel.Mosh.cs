using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Mosh: Das Terminal läuft über UDP, die SSH-Verbindung daneben trägt Dateien, Auslastung und Tunnel. Reißt sie, bleibt
// das Terminal stehen und läuft weiter; die Sitzung hängt Dateien und Server-Info ab und – kommt die Verbindung zurück,
// was der Kern von selbst versucht – wieder an. Was der Kern meldet, liest die Sitzung von der Verbindung (nicht aus
// dem Ereignis): Zwei Meldungen kurz nacheinander ergeben so denselben Stand wie eine.
public sealed partial class SessionViewModel
{
    /// <summary>Das Terminal läuft über Mosh – der Reiter trägt die Marke dazu. Bei SSH (auch bei einem Mosh-Profil, das nicht ging) <c>false</c>.</summary>
    [ObservableProperty]
    public partial bool IsMosh { get; private set; }

    /// <summary>Mosh hört seit einer Weile nichts vom Server („Keine Antwort seit 12 s …“); leer, solange es ihn hört.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoshNote))]
    public partial string MoshNote { get; private set; } = string.Empty;

    public bool HasMoshNote => MoshNote.Length > 0;

    /// <summary>
    /// Die SSH-Verbindung unter dem Terminal ist weg: Dateien und Server-Info fehlen, das Terminal läuft. Dateibrowser,
    /// Inspector und Transfers sehen das wie eine Sitzung ohne Dateien.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RelinkCommand))]
    public partial bool IsLinkLost { get; private set; }

    // Das Verzeichnis, in dem der Browser stand, als die Verbindung riss – dorthin geht er zurück.
    private string? _linkPath;

    /// <summary>Die SSH-Verbindung jetzt neu aufbauen lassen, statt auf den nächsten Versuch des Kerns zu warten.</summary>
    [RelayCommand(CanExecute = nameof(IsLinkLost))]
    private void Relink() => _connection.Relink();

    private void OnMoshChanged(MoshStatus status)
    {
        var connection = _connection;
        _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(connection, _connection))
            {
                ApplyMosh(connection);
            }
        });
    }

    private void OnLinkChanged()
    {
        var connection = _connection;
        _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(connection, _connection))
            {
                ApplyLink(connection);
            }
        });
    }

    private void ApplyMosh(IServerConnection connection)
    {
        var status = connection.Mosh;
        IsMosh = status is not null;
        MoshNote = status is { Silent: true } ? L.Get("MoshSilent", status.SilentSeconds) : string.Empty;
    }

    private void ApplyLink(IServerConnection connection)
    {
        if (connection.IsLinkLost)
        {
            // Ein neuer Aufbau, der scheiterte, kommt als weitere Meldung: dann ändert sich nur der Text.
            if (!IsLinkLost)
            {
                _linkPath = Files.CurrentPath;
                Files.Attach(null);
                Inspector.Attach(null);
                IsLinkLost = true;
            }

            // Die Einzelheit ist Text des Kerns, englisch: gesäubert hinter dem Satz.
            var text = connection.LinkLostDetail is { Length: > 0 } detail
                ? $"{L.Get("MoshLinkLost")} ({Format.Visible(detail)})"
                : L.Get("MoshLinkLost");
            Files.SetUnavailableReason(text);
            Inspector.SetLinkLost(text);
        }
        else if (IsLinkLost || !ReferenceEquals(connection.Monitor, Inspector.Monitor))
        {
            // Wieder da: neue Dateien und eine neue Messung – sie hängen an der neuen Verbindung des Kerns.
            IsLinkLost = false;
            Files.SetUnavailableReason(null);
            Inspector.SetLinkLost(null);
            Inspector.Attach(connection.Monitor);
            _ = Files.ReattachAsync(connection.Files, _linkPath);
            _linkPath = null;
        }
    }

    // Mit dem Ende oder dem Neubeginn einer Verbindung ist alles davon vorbei.
    private void ResetMosh()
    {
        (IsMosh, MoshNote, IsLinkLost, _linkPath) = (false, string.Empty, false, null);
        Files.SetUnavailableReason(null);
        Inspector.SetLinkLost(null);
    }
}
