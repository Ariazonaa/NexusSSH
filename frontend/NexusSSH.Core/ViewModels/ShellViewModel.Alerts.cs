using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Hinweise, wenn Platte, Speicher oder Last eines Servers über einer Schwelle liegen. Nur für offene Sitzungen, und
// nur, wenn der Benutzer es einschaltet: Dafür wird bei allen offenen SSH-Sitzungen im Hintergrund gemessen – sonst
// misst die App nur für den Tab, dessen Werte zu sehen sind.
public sealed partial class ShellViewModel
{
    /// <summary>Hinweise zu den Schwellen. Aus heißt: Gemessen wird nur, was gerade zu sehen ist.</summary>
    public bool ServerAlerts
    {
        get => Settings.ServerAlerts;
        set
        {
            if (value != Settings.ServerAlerts)
            {
                Settings.ServerAlerts = value;
                OnPropertyChanged();
                UpdateMonitoring();
            }
        }
    }

    /// <summary>Belegung eines Dateisystems in Prozent, ab der gemeldet wird. double, weil die NumberBox so bindet.</summary>
    public double AlertDiskPercent
    {
        get => Settings.AlertDiskPercent;
        set
        {
            if (double.IsFinite(value) && (int)Math.Clamp(value, AppSettings.MinAlertPercent, AppSettings.MaxAlertPercent) is var percent && percent != Settings.AlertDiskPercent)
            {
                Settings.AlertDiskPercent = percent;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Belegter Arbeitsspeicher in Prozent, ab dem gemeldet wird.</summary>
    public double AlertMemoryPercent
    {
        get => Settings.AlertMemoryPercent;
        set
        {
            if (double.IsFinite(value) && (int)Math.Clamp(value, AppSettings.MinAlertPercent, AppSettings.MaxAlertPercent) is var percent && percent != Settings.AlertMemoryPercent)
            {
                Settings.AlertMemoryPercent = percent;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Last der letzten Minute je Kern, ab der gemeldet wird.</summary>
    public double AlertLoadPerCore
    {
        get => Settings.AlertLoadPerCore;
        set
        {
            if (double.IsFinite(value) && Math.Round(Math.Clamp(value, AppSettings.MinAlertLoad, AppSettings.MaxAlertLoad), 1) is var load && load != Settings.AlertLoadPerCore)
            {
                Settings.AlertLoadPerCore = load;
                OnPropertyChanged();
            }
        }
    }

    private void WatchAlerts(SessionViewModel session) => session.Inspector.Measured += stats => OnMeasured(session, stats);

    private void OnMeasured(SessionViewModel session, ServerStats stats)
    {
        if (!ServerAlerts)
        {
            return;
        }

        var thresholds = new AlertThresholds(Settings.AlertDiskPercent, Settings.AlertMemoryPercent, Settings.AlertLoadPerCore);
        var alerts = session.Inspector.Alerts.Check(stats, thresholds).ToList();
        foreach (var alert in alerts)
        {
            // Unter „Probleme“ mit dem Namen des Servers, je Lage eine Zeile.
            Log($"{session.Profile.Name}: {alert.Text}", LogSeverity.Warning);
        }

        // Als Hinweis von Windows (wenn das Fenster nicht vorn ist) einer je Messung: Von mehreren im selben
        // Augenblick blendet Windows nur einen ein, die übrigen landen ungesehen in der Mitteilungszentrale.
        if (alerts.Count > 0)
        {
            Notify(session.Profile.Name, NoticeFor([.. alerts.Select(alert => alert.Text)]));
        }
    }

    /// <summary>Mehr Zeilen zeigt ein Hinweis von Windows nicht; was darüber hinausgeht, nennt die letzte als Zahl.</summary>
    public const int NoticeLines = 3;

    internal static string NoticeFor(IReadOnlyList<string> texts) => texts.Count <= NoticeLines
        ? string.Join('\n', texts)
        : string.Join('\n', texts.Take(NoticeLines - 1).Append(L.Get("NotifyMore", texts.Count - (NoticeLines - 1))));
}
