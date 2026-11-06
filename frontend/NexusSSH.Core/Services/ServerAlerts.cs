using System.Globalization;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Ab wann gemeldet wird.</summary>
/// <param name="DiskPercent">Belegung eines Dateisystems (Platz oder Inodes) in Prozent.</param>
/// <param name="MemoryPercent">Belegter Arbeitsspeicher in Prozent.</param>
/// <param name="LoadPerCore">Lastdurchschnitt der letzten Minute, geteilt durch die Zahl der Kerne.</param>
public sealed record AlertThresholds(double DiskPercent, double MemoryPercent, double LoadPerCore);

public enum AlertKind
{
    Disk,
    Inodes,
    Memory,
    Load,
}

/// <summary>Ein Hinweis: was über der Schwelle liegt, als Satz für Protokoll und Benachrichtigung.</summary>
/// <param name="Subject">Bei einem Dateisystem der Einhängepunkt, sonst leer.</param>
public sealed record ServerAlert(AlertKind Kind, string Subject, string Text);

/// <summary>
/// Vergleicht die Messungen eines Servers mit den Schwellen und meldet jede Überschreitung einmal – nicht bei jeder
/// Messung wieder. Gemeldet wird erneut erst, nachdem der Wert deutlich unter die Schwelle gefallen war. Speicher
/// und Last müssen einige Messungen lang darüber liegen: Beide springen, und ein einzelner Ausschlag ist kein Zustand.
/// </summary>
public sealed class AlertWatch
{
    /// <summary>So viele Messungen hintereinander müssen Speicher oder Last über der Schwelle liegen.</summary>
    public const int Sustained = 3;

    /// <summary>So viele Prozentpunkte unter der Schwelle gilt ein Dateisystem wieder als in Ordnung.</summary>
    public const double DiskSlack = 2;

    /// <summary>Ebenso für den Arbeitsspeicher.</summary>
    public const double MemorySlack = 5;

    /// <summary>Die Last gilt wieder als in Ordnung unter diesem Anteil der Schwelle.</summary>
    public const double LoadSlack = 0.8;

    private readonly HashSet<(AlertKind Kind, string Subject)> _raised = [];
    private int _memoryAbove;
    private int _loadAbove;

    /// <summary>Vergisst alles – für eine neue Verbindung.</summary>
    public void Reset()
    {
        _raised.Clear();
        (_memoryAbove, _loadAbove) = (0, 0);
    }

    /// <summary>Die Hinweise, die mit dieser Messung neu dazukommen.</summary>
    public IReadOnlyList<ServerAlert> Check(ServerStats stats, AlertThresholds thresholds)
    {
        List<ServerAlert> alerts = [];

        // Nennt der Server keine Liste, gibt es nur `/`.
        IReadOnlyList<DiskUsage> disks = stats.Disks is { Count: > 0 } listed
            ? listed
            : stats.DiskTotal > 0 ? [new DiskUsage("/", string.Empty, stats.DiskUsed, stats.DiskTotal)] : [];
        var limit = Format.Percent(thresholds.DiskPercent);
        foreach (var disk in disks)
        {
            if (Crossed(AlertKind.Disk, disk.Mount, disk.Percent, thresholds.DiskPercent, thresholds.DiskPercent - DiskSlack))
            {
                alerts.Add(new ServerAlert(AlertKind.Disk, disk.Mount, L.Get("AlertDisk", disk.Mount, disk.PercentText, limit)));
            }

            if (disk.InodePercent is { } inodes
                && Crossed(AlertKind.Inodes, disk.Mount, inodes, thresholds.DiskPercent, thresholds.DiskPercent - DiskSlack))
            {
                alerts.Add(new ServerAlert(AlertKind.Inodes, disk.Mount, L.Get("AlertInodes", disk.Mount, Format.Percent(inodes), limit)));
            }
        }

        // Ein Dateisystem, das nicht mehr eingehängt ist, wird beim nächsten Mal wieder gemeldet.
        _raised.RemoveWhere(raised => raised.Kind is AlertKind.Disk or AlertKind.Inodes && disks.All(disk => disk.Mount != raised.Subject));

        _memoryAbove = stats.MemoryTotal > 0 && stats.MemoryPercent >= thresholds.MemoryPercent ? _memoryAbove + 1 : 0;
        if (Crossed(AlertKind.Memory, string.Empty, stats.MemoryPercent, thresholds.MemoryPercent, thresholds.MemoryPercent - MemorySlack, _memoryAbove >= Sustained))
        {
            alerts.Add(new ServerAlert(AlertKind.Memory, string.Empty, L.Get("AlertMemory", Format.Percent(stats.MemoryPercent), Format.Percent(thresholds.MemoryPercent))));
        }

        if (LoadOf(stats) is { } load)
        {
            var cores = Math.Max(stats.Info?.CoreCount ?? stats.Cores?.Count ?? 1, 1);
            var perCore = load / cores;
            _loadAbove = perCore >= thresholds.LoadPerCore ? _loadAbove + 1 : 0;
            if (Crossed(AlertKind.Load, string.Empty, perCore, thresholds.LoadPerCore, thresholds.LoadPerCore * LoadSlack, _loadAbove >= Sustained))
            {
                alerts.Add(new ServerAlert(
                    AlertKind.Load,
                    string.Empty,
                    L.Get("AlertLoad", Number(load), cores == 1 ? L.Get("CoreCountOne") : L.Get("CoreCount", cores), Number(thresholds.LoadPerCore))));
            }
        }

        return alerts;
    }

    /// <summary>Der Lastdurchschnitt der letzten Minute – die erste der drei Zahlen; <c>null</c>, wenn der Server keinen nennt.</summary>
    internal static double? LoadOf(ServerStats stats)
        => double.TryParse(stats.Load.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var load)
            && double.IsFinite(load) && load >= 0
            ? load
            : null;

    // Meldet einmal beim Überschreiten; gibt die Meldung wieder frei, wenn der Wert unter `calm` gefallen ist.
    private bool Crossed(AlertKind kind, string subject, double value, double threshold, double calm, bool sustained = true)
    {
        var key = (kind, subject);
        if (value < calm)
        {
            _raised.Remove(key);
            return false;
        }

        return value >= threshold && sustained && _raised.Add(key);
    }

    private static string Number(double value) => value.ToString("0.0#", L.Culture);
}
