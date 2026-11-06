using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Services;

/// <summary>
/// Woran ein Verbindungsaufbau gescheitert ist – die Kennungen, die der Kern mit dem Ende der Sitzung nennt
/// (<c>session.state</c>, Feld <c>trouble</c>; Gegenstelle: <c>dial.rs</c>), und der Satz mit Vorschlag dazu.
/// </summary>
public static class ConnectTrouble
{
    public const string Name = "name";
    public const string Family = "family";
    public const string Refused = "refused";
    public const string Timeout = "timeout";
    public const string Unreachable = "unreachable";
    public const string Algorithms = "algorithms";

    /// <summary>
    /// Der Satz für den Benutzer: was geschah und was er versuchen kann. <c>null</c>, wenn die Kennung nichts
    /// Genaueres sagt – dann bleibt es beim allgemeinen „Server nicht erreichbar“.
    /// </summary>
    public static string? Sentence(string? trouble) => trouble switch
    {
        Name => L.Get("TroubleName"),
        Family => L.Get("TroubleFamily"),
        Refused => L.Get("TroubleRefused"),
        Timeout => L.Get("TroubleTimeout"),
        Unreachable => L.Get("TroubleUnreachable"),
        Algorithms => L.Get("TroubleAlgorithms"),
        _ => null,
    };
}
