using System.Globalization;
using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Models;

/// <summary>Welche Art von Portweiterleitung ein Tunnel ist – wie <c>ssh -L</c>, <c>-R</c> und <c>-D</c>.</summary>
public enum TunnelKind
{
    /// <summary>Ein Port auf diesem Rechner führt zu einem Dienst, den der Server erreicht.</summary>
    Local,

    /// <summary>Ein Port am Server führt zu einem Dienst, den dieser Rechner erreicht.</summary>
    Remote,

    /// <summary>Ein Port auf diesem Rechner ist ein SOCKS-Proxy, der über den Server hinausgeht.</summary>
    Dynamic,
}

/// <summary>Wie es um einen Tunnel steht. Ohne Verbindung zum Server steht keiner.</summary>
public enum TunnelState
{
    /// <summary>Läuft nicht: nie gestartet, angehalten, oder die Verbindung ist zu Ende.</summary>
    Stopped,

    /// <summary>Steht: Am Port wird gelauscht.</summary>
    Listening,

    /// <summary>Kam nicht zustande – der Grund steht dabei.</summary>
    Failed,
}

/// <summary>
/// Eine Portweiterleitung eines Profils. Sie steht, solange eine Verbindung zu dem Server steht.
/// </summary>
/// <param name="Id">Kennung innerhalb des Profils; leer bei einem neuen Tunnel, dann vergibt der Kern eine.</param>
/// <param name="Name">Anzeigename; leer heißt: nach den Ports benannt (<see cref="DisplayName"/>).</param>
/// <param name="ListenPort">Der Port, an dem gelauscht wird – hier (lokal, dynamisch) oder am Server (entfernt).</param>
/// <param name="TargetHost">Wohin es geht: vom Server aus gesehen (lokal) oder von diesem Rechner aus (entfernt). Leer bei dynamisch.</param>
/// <param name="Open">Nicht nur der Rechner selbst, sondern das ganze Netz darf sich an den Port hängen. Nur auf ausdrücklichen Wunsch.</param>
/// <param name="Auto">Mit der Verbindung starten; sonst erst auf Zuruf.</param>
public sealed record ProfileTunnel(
    string Id,
    TunnelKind Kind,
    string Name,
    int ListenPort,
    string TargetHost = "",
    int TargetPort = 0,
    bool Open = false,
    bool Auto = true)
{
    public const int MaxPerProfile = 32;

    /// <summary>Der Name, sonst die Strecke – der Name ist Text des Benutzers.</summary>
    public string DisplayName => Name.Length > 0 ? Format.Visible(Name) : Route;

    /// <summary>Woher und wohin, in der Schreibweise von <c>ssh</c>: <c>localhost:5432 → db:5432</c>.</summary>
    public string Route => Kind switch
    {
        TunnelKind.Local => L.Get("TunnelRouteLocal", Port(ListenPort), Target),
        TunnelKind.Remote => L.Get("TunnelRouteRemote", Port(ListenPort), Target),
        _ => L.Get("TunnelRouteDynamic", Port(ListenPort)),
    };

    public string KindText => Kind switch
    {
        TunnelKind.Local => L.Get("TunnelKindLocal"),
        TunnelKind.Remote => L.Get("TunnelKindRemote"),
        _ => L.Get("TunnelKindDynamic"),
    };

    private string Target => TargetHost.Contains(':', StringComparison.Ordinal) ? $"[{TargetHost}]:{Port(TargetPort)}" : $"{TargetHost}:{Port(TargetPort)}";

    /// <summary>
    /// Ob der Tunnel so stehen kann – dieselben Regeln wie im Kern (<c>Tunnel::normalized</c>), damit der Dialog es
    /// sagt, bevor das Speichern scheitert.
    /// </summary>
    public bool IsValid
        => ListenPort is >= 1 and <= 65535
            && (Kind == TunnelKind.Dynamic
                || (TargetPort is >= 1 and <= 65535 && IsHost(TargetHost.Trim())));

    /// <summary>Ein Hostname oder eine Adresse, nichts sonst.</summary>
    public static bool IsHost(string host)
        => host.Length is > 0 and <= 255 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':');

    /// <summary>Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{DisplayName}, {KindText}";

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Was das Backend über einen Tunnel meldet. <paramref name="Message"/> ist englisch und nennt bei einem Fehlschlag den Grund.</summary>
public sealed record TunnelNotice(string ProfileId, string TunnelId, TunnelState State, string? Message = null);
