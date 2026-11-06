using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Models;

/// <summary>
/// Momentaufnahme für den Server Inspector (Spec §6). Prozentwerte liegen in 0–100, Mengen sind Bytes;
/// eine Gesamtmenge von 0 heißt „unbekannt“.
/// </summary>
/// <param name="Load">Lastdurchschnitt über 1, 5 und 15 Minuten (<c>0.42 0.31 0.25</c>); leer, wenn unbekannt.</param>
/// <param name="NetReceived">Empfangene Bytes je Sekunde.</param>
/// <param name="NetSent">Gesendete Bytes je Sekunde.</param>
/// <param name="Cores">Auslastung jedes Kerns, 0–100; leer, wenn der Server sie nicht einzeln nennt.</param>
/// <param name="Split">Woraus die Auslastung besteht; <c>null</c>, wenn der Server es nicht aufteilt.</param>
/// <param name="Disks">Alle Dateisysteme des Servers; leer, wenn er nur <c>/</c> nennt.</param>
/// <param name="Info">Was der Rechner ist; <c>null</c>, wenn der Server nichts davon nennt.</param>
/// <param name="Interfaces">Das Netz je Karte, mit ihren Adressen; leer, wenn der Server sie nicht einzeln nennt.</param>
/// <param name="Io">Durchsatz der Datenträger; <c>null</c>, wenn der Server ihn nicht nennt.</param>
/// <param name="IoDevices">Dasselbe je Platte; leer, wenn der Server ihn nicht nennt.</param>
/// <param name="Clock">Die Uhr des Servers bei dieser Messung; <c>null</c>, wenn er sie nicht nennt.</param>
public sealed record ServerStats(
    bool Online,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent,
    string OsName,
    string Kernel,
    TimeSpan Uptime,
    long MemoryUsed = 0,
    long MemoryTotal = 0,
    long DiskUsed = 0,
    long DiskTotal = 0,
    string Load = "",
    long NetReceived = 0,
    long NetSent = 0,
    IReadOnlyList<double>? Cores = null,
    CpuSplit? Split = null,
    IReadOnlyList<DiskUsage>? Disks = null,
    ServerInfo? Info = null,
    ServerClock? Clock = null,
    MemoryParts? Memory = null,
    DiskIo? Io = null,
    IReadOnlyList<NetInterface>? Interfaces = null,
    IReadOnlyList<DiskIoDevice>? IoDevices = null);

/// <summary>Eine Netzwerkkarte des Servers: was sie je Sekunde empfängt und sendet, und ihre Adressen.</summary>
/// <param name="Addresses">Die Adressen, mit Komma getrennt; leer, wenn der Server keine nennt.</param>
public sealed record NetInterface(string Name, long Received, long Sent, string Addresses)
{
    public const int MaxNameLength = 32;
    public const int MaxAddresses = 8;

    public string RateText => Format.Network(Received, Sent);

    public bool HasAddresses => Addresses.Length > 0;

    /// <summary>Name, Durchsatz und Adressen – Screenreader lesen das als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => HasAddresses ? $"{Name}, {RateText}, {Addresses}" : $"{Name}, {RateText}";
}

/// <summary>
/// Was die Datenträger zusammen leisten: gelesene und geschriebene Bytes je Sekunde über alle Platten, und wie
/// beschäftigt die meistbeschäftigte war (Anteil der Zeit mit Ein- und Ausgabe, 0–100).
/// </summary>
/// <param name="Mixed">
/// Der Server nennt Lesen und Schreiben nur zusammen (OpenBSD): Die Summe steht in <c>Read</c>, <c>Write</c> ist 0.
/// </param>
public sealed record DiskIo(long Read, long Write, double Busy, bool Mixed = false)
{
    /// <summary>„liest 2 MB/s · schreibt 512 KB/s · 25 % beschäftigt“ – oder, wo der Server nur die Summe nennt, diese.</summary>
    public string Text => Mixed
        ? L.Get("DiskIoMixedText", Format.Rate(Read), Format.Percent(Busy))
        : L.Get("DiskIoText", Format.Rate(Read), Format.Rate(Write), Format.Percent(Busy));
}

/// <summary>Der Durchsatz einer einzelnen Platte, als Zeile für die Anzeige.</summary>
public sealed record DiskIoDevice(string Name, DiskIo Io)
{
    public const int MaxNameLength = 32;

    public string Text => Io.Text;

    /// <summary>Name und Durchsatz – Screenreader lesen das als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Name}, {Text}";
}

/// <summary>
/// Woraus der Arbeitsspeicher neben dem Belegten besteht, und die Auslagerung – in Bytes.
/// </summary>
/// <param name="Cache">Zwischenspeicher, den das System bei Bedarf hergibt; <c>null</c>, wenn der Server nicht aufteilt.</param>
/// <param name="Free">Ganz Freies; <c>null</c>, wenn der Server nicht aufteilt.</param>
/// <param name="SwapTotal">Größe der Auslagerung; 0, wenn es keine gibt oder der Server sie nicht nennt.</param>
public sealed record MemoryParts(long? Cache, long? Free, long SwapUsed, long SwapTotal)
{
    public bool HasSplit => Cache is not null && Free is not null;

    public bool HasSwap => SwapTotal > 0;

    public double SwapPercent => SwapTotal > 0 ? Math.Clamp(100.0 * SwapUsed / SwapTotal, 0, 100) : 0;
}

/// <summary>Was der Rechner ist – Angaben, die sich nicht ändern. Leer bzw. <c>null</c> heißt: nicht bekannt.</summary>
/// <param name="Machine">Hersteller und Modell.</param>
/// <param name="Virtualization"><c>none</c> für eigene Hardware, <c>vm</c> für „virtuell, Art unbekannt“, sonst die Art (<c>kvm</c>, <c>docker</c>).</param>
public sealed record ServerInfo(string Machine, string CpuModel, int? CoreCount, string Virtualization)
{
    public const string NoVirtualization = "none";
    public const string SomeVirtualization = "vm";
}

/// <summary>Die Uhr des Servers.</summary>
/// <param name="Time">Die Zeit in der Zeitzone des Servers.</param>
/// <param name="Zone">Kürzel der Zeitzone (<c>CEST</c>); leer, wenn der Server keines nennt.</param>
/// <param name="Drift">Um so viel geht die Uhr des Servers vor (negativ: nach) – gemessen beim Eintreffen der Messung, also auf wenige Sekunden genau.</param>
public sealed record ServerClock(DateTimeOffset Time, string Zone, TimeSpan Drift)
{
    /// <summary>Darunter gilt die Uhr als gleich: Die Messung ist unterwegs, und der Server nennt nur ganze Sekunden.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Die Uhr aus dem, was der Kern meldet; <c>null</c>, wenn etwas davon keine Zeit oder keine Zeitzone sein kann.
    /// </summary>
    /// <param name="received">Die eigene Uhr beim Eintreffen der Messung.</param>
    public static ServerClock? From(long? time, int? utcOffset, string? zone, DateTimeOffset received)
    {
        if (time is not (> 0 and < 253_402_300_800) || utcOffset is not { } offset || offset % 60 != 0 || Math.Abs(offset) > 14 * 3600)
        {
            return null;
        }

        var utc = DateTimeOffset.FromUnixTimeSeconds(time.Value);
        var name = zone is { Length: > 0 and <= 8 } && zone.All(char.IsAsciiLetter) ? zone : string.Empty;
        return new ServerClock(utc.ToOffset(TimeSpan.FromSeconds(offset)), name, utc - received);
    }
}

/// <summary>
/// Anteile an der CPU-Zeit, je 0–100. Warten auf Platte oder Netz zählt in der Auslastung als untätig und steht
/// hier für sich; „gestohlen“ ist Zeit, die der Wirt einer virtuellen Maschine anderen gab.
/// </summary>
public sealed record CpuSplit(double User, double System, double IoWait, double Steal);

/// <summary>Die Auslastung eines Kerns als Zeile für die Anzeige.</summary>
/// <param name="Number">Nummer des Kerns, wie der Server zählt (ab 0).</param>
public sealed record CoreLoad(int Number, double Percent)
{
    /// <summary>„Kern 3: 42 %“ – Tooltip und Name für Screenreader.</summary>
    public string Text => L.Get("CoreLoad", Number, Format.Percent(Percent));

    public override string ToString() => Text;
}

/// <summary>Wie voll etwas ist – danach richtet sich die Farbe des Balkens.</summary>
public enum UsageLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Ein Dateisystem des Servers: Platz und, wenn bekannt, Inodes.</summary>
/// <param name="Device">Gerät oder Quelle, wie <c>df</c> sie nennt.</param>
/// <param name="Total">Bytes, die Benutzern zur Verfügung stehen (belegt + frei).</param>
public sealed record DiskUsage(string Mount, string Device, long Used, long Total, long? InodesUsed = null, long? InodesTotal = null)
{
    /// <summary>Ab diesem Anteil (Platz oder Inodes) wird gewarnt.</summary>
    public const double WarnAt = 90;

    /// <summary>Ab diesem Anteil ist es dringend.</summary>
    public const double CriticalAt = 95;

    public double Percent => Total > 0 ? Math.Clamp(100.0 * Used / Total, 0, 100) : 0;

    /// <summary>Anteil belegter Inodes; <c>null</c>, wenn der Server sie nicht nennt.</summary>
    public double? InodePercent => InodesTotal is > 0 && InodesUsed is { } used ? Math.Clamp(100.0 * used / InodesTotal.Value, 0, 100) : null;

    /// <summary>Der vollere von Platz und Inodes entscheidet: Auch ohne freie Inodes lässt sich nichts mehr anlegen.</summary>
    public UsageLevel Level => Math.Max(Percent, InodePercent ?? 0) switch
    {
        >= CriticalAt => UsageLevel.Critical,
        >= WarnAt => UsageLevel.Warning,
        _ => UsageLevel.Normal,
    };

    public string PercentText => Format.Percent(Percent);

    /// <summary>Die Mengen und, wenn bekannt, die Inodes: <c>448 GB / 477 GB · Inodes 4 %</c>.</summary>
    public string Detail
    {
        get
        {
            var space = $"{Format.FileSize(Used, isDirectory: false)} / {Format.FileSize(Total, isDirectory: false)}";
            return InodePercent is { } inodes ? $"{space} · {L.Get("Inodes", Format.Percent(inodes))}" : space;
        }
    }

    /// <summary>Alles in einem Satz – für Screenreader und als Name der Zeile.</summary>
    public string Summary => $"{Mount}, {PercentText}, {Detail}";

    public override string ToString() => Summary;
}
