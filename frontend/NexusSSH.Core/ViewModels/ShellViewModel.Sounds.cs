using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Töne zu Ereignissen: einen Fehler bemerken, ohne auf das Fenster zu sehen. Gespielt werden die Töne von Windows
// (SoundEvents.ToneOf); welches Ereignis tönt, steht je für sich in den Einstellungen – die Fehler sind von Haus
// aus an, „verbunden“ und „Transfer fertig“ aus. Die Glocke des Terminals ist eine eigene Sache (BellSound).
public sealed partial class ShellViewModel
{
    /// <summary>So lange nach einem Ton kommt derselbe nicht noch einmal – ein Ordner mit hundert Dateien scheitert sonst hundertfach.</summary>
    public static readonly TimeSpan SoundPause = TimeSpan.FromSeconds(3);

    // Was der Kern sagt, wenn der Benutzer die Rückfrage nach dem Passwort schließt (session.rs); über einen
    // Sprung-Server steht dessen Name davor.
    private const string SignInCancelled = "the sign-in was cancelled";

    private readonly Dictionary<SoundEvent, DateTimeOffset> _lastSound = [];

    /// <summary>
    /// Ein Ton soll zu hören sein: welcher, und ob nur, wenn das Fenster nicht vorn ist. Die Oberfläche spielt ihn.
    /// </summary>
    public event Action<SoundEvent, bool>? SoundRequested;

    public bool SoundConnectFailed
    {
        get => Settings.SoundConnectFailed;
        set
        {
            if (value != Settings.SoundConnectFailed)
            {
                Settings.SoundConnectFailed = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SoundAuthFailed
    {
        get => Settings.SoundAuthFailed;
        set
        {
            if (value != Settings.SoundAuthFailed)
            {
                Settings.SoundAuthFailed = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SoundConnectionLost
    {
        get => Settings.SoundConnectionLost;
        set
        {
            if (value != Settings.SoundConnectionLost)
            {
                Settings.SoundConnectionLost = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SoundTransferFailed
    {
        get => Settings.SoundTransferFailed;
        set
        {
            if (value != Settings.SoundTransferFailed)
            {
                Settings.SoundTransferFailed = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SoundConnected
    {
        get => Settings.SoundConnected;
        set
        {
            if (value != Settings.SoundConnected)
            {
                Settings.SoundConnected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SoundTransferDone
    {
        get => Settings.SoundTransferDone;
        set
        {
            if (value != Settings.SoundTransferDone)
            {
                Settings.SoundTransferDone = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Die Töne nur, wenn das Fenster nicht vorn ist.</summary>
    public bool SoundsOnlyInBackground
    {
        get => Settings.SoundsOnlyInBackground;
        set
        {
            if (value != Settings.SoundsOnlyInBackground)
            {
                Settings.SoundsOnlyInBackground = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Probehören in den Einstellungen: sofort, auch wenn das Ereignis aus ist und das Fenster vorn.</summary>
    public void PreviewSound(SoundEvent sound) => SoundRequested?.Invoke(sound, false);

    private bool Sounds(SoundEvent sound) => sound switch
    {
        SoundEvent.ConnectFailed => Settings.SoundConnectFailed,
        SoundEvent.AuthFailed => Settings.SoundAuthFailed,
        SoundEvent.ConnectionLost => Settings.SoundConnectionLost,
        SoundEvent.TransferFailed => Settings.SoundTransferFailed,
        SoundEvent.Connected => Settings.SoundConnected,
        SoundEvent.TransferDone => Settings.SoundTransferDone,
        _ => false,
    };

    private void Sound(SoundEvent sound)
    {
        if (!Sounds(sound))
        {
            return;
        }

        var now = Now();
        if (_lastSound.TryGetValue(sound, out var last) && now - last < SoundPause)
        {
            return;
        }

        _lastSound[sound] = now;
        SoundRequested?.Invoke(sound, Settings.SoundsOnlyInBackground);
    }

    // Das Ende einer Verbindung: Was der Benutzer selbst wollte (Trennen, „exit“, Schlüssel abgelehnt, Rückfrage
    // geschlossen), tönt nicht – und auch nicht jeder Versuch, den die Sitzung nach einem Abriss von selbst macht:
    // Der Abriss hat schon getönt.
    private void SoundForEnd(SessionViewModel session, ConnectionStatus status, bool wasConnected)
    {
        if (session.IsVirtual || session.IsRetrying)
        {
            return;
        }

        switch (status.Reason)
        {
            case CloseReason.ConnectionLost when wasConnected:
                Sound(SoundEvent.ConnectionLost);
                break;
            case CloseReason.ConnectFailed or CloseReason.ConnectionLost or CloseReason.UnknownProfile:
                Sound(SoundEvent.ConnectFailed);
                break;
            case CloseReason.AuthFailed when status.Detail?.EndsWith(SignInCancelled, StringComparison.Ordinal) != true:
                Sound(SoundEvent.AuthFailed);
                break;
        }
    }
}
