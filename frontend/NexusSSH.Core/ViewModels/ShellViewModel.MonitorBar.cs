namespace NexusSSH.Core.ViewModels;

// Die Auslastung des Servers in der Zeile unten im Fenster – wie „Remote monitoring“ bei MobaXterm (
// 2026-10-24; seit Entwurf A in der Zeile für das ganze Fenster): CPU, Speicher, Netz, Platte, Benutzer und Laufzeit
// aus derselben Messung wie die Einzelheiten des Servers, also ohne weitere Last am Server. Das Kästchen davor schaltet
// die Messung ab; es bleibt dann allein stehen, damit sie sich dort wieder einschalten lässt. Gemessen wird nur der
// aktive Tab (UpdateMonitoring).
public sealed partial class ShellViewModel
{
    /// <summary>Ob die Zeile unten misst und ihre Werte zeigt (Einstellung, von Haus aus an).</summary>
    public bool MonitorBar
    {
        get => Settings.MonitorBar;
        set
        {
            if (value != Settings.MonitorBar)
            {
                Settings.MonitorBar = value;
                OnPropertyChanged();
                UpdateMonitoring();
            }
        }
    }

    /// <summary>
    /// Ob links in der Zeile unten die Auslastung steht: für einen aktiven Tab, dessen Server sich messen lässt (SSH) –
    /// nicht bei einem lokalen Terminal, Telnet oder seriell, nicht auf der Startseite und nicht in „nur das Terminal“
    /// (dann gibt es die Zeile nicht). Rechts stehen die Knöpfe immer.
    /// </summary>
    public bool ShowsMonitorBar => !IsTerminalOnly && ActiveSession is { CanMeasure: true };

    /// <summary>Schaltet die Messung der Zeile unten ein oder aus (Palette).</summary>
    public void ToggleMonitorBar() => MonitorBar = !MonitorBar;
}
