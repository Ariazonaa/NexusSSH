namespace NexusSSH.Core.Settings;

/// <summary>Wozu die App einen Ton spielen kann – jedes für sich an oder aus (<see cref="AppSettings"/>).</summary>
public enum SoundEvent
{
    /// <summary>Der Server war nicht zu erreichen.</summary>
    ConnectFailed,

    /// <summary>Der Server hat die Anmeldung abgelehnt.</summary>
    AuthFailed,

    /// <summary>Eine stehende Verbindung ist abgerissen.</summary>
    ConnectionLost,

    /// <summary>Ein Transfer ist gescheitert.</summary>
    TransferFailed,

    /// <summary>Eine Verbindung steht.</summary>
    Connected,

    /// <summary>Ein Transfer ist fertig.</summary>
    TransferDone,
}

/// <summary>Welchen der Töne von Windows ein Ereignis spielt; eigene Klangdateien bringt die App nicht mit.</summary>
public enum SoundTone
{
    Error,
    Warning,
    Info,
}

public static class SoundEvents
{
    public static SoundTone ToneOf(SoundEvent sound) => sound switch
    {
        SoundEvent.ConnectFailed or SoundEvent.AuthFailed or SoundEvent.TransferFailed => SoundTone.Error,
        SoundEvent.ConnectionLost => SoundTone.Warning,
        _ => SoundTone.Info,
    };
}
