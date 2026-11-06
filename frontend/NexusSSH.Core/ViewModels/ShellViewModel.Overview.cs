using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Die Übersicht aller offenen Sitzungen (eine Seite der Startseite, HomePage.Overview): je Sitzung eine Zeile mit
// dem, was ihr Inspector ohnehin kennt. Neu ist nur, dass dafür alle messen – und nur, solange die Seite zu sehen
// ist (IsOverviewShown, UpdateMonitoring): Jede Messung ist eine Schleife auf dem Server.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Was in der Zeile einer Sitzung steht, solange es keine Werte gibt – und warum nicht. Leer, sobald Werte da
    /// sind.
    /// </summary>
    public static string OverviewNote(bool measurable, ConnectionState state, bool hasMetrics, bool unavailable)
    {
        if (hasMetrics)
        {
            return string.Empty;
        }

        // Telnet, seriell, FTP und die Fenster von tmux messen nicht; ebenso ein Server, der keine Werte liefert.
        if (!measurable || unavailable)
        {
            return L.Get("OverviewNone");
        }

        return state == ConnectionState.Connected ? L.Get("OverviewMeasuring") : L.Get("OverviewOffline");
    }
}
