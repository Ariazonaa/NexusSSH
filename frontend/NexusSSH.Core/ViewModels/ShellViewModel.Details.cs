using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusSSH.Core.ViewModels;

// Die Seite „Server-Details“ (Entwurf A): was von der Server-Info rechts übrig ist – Kerne und Aufteilung der CPU,
// Speicher und Swap, Dateisysteme, Durchsatz der Platten, Netz je Karte, Maschine, Tunnel, der Verlauf, die
// Protokolle und die Antwortzeit auf Klick. Sie liegt über den Bereichen wie die Protokolle, gehört zu der Sitzung,
// für die sie geöffnet wurde, und schließt, wenn ein anderer Tab aktiv wird. Gemessen wird, solange sie offen ist –
// auch ohne die Zeile unten. Die Werte liefert weiter ServerInspectorViewModel; nur die Ansicht zog um.
public sealed partial class ShellViewModel
{
    /// <summary>Die Sitzung, deren Einzelheiten die Seite zeigt; <c>null</c>, wenn sie zu ist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPage), nameof(IsDetailsOpen))]
    public partial SessionViewModel? Details { get; private set; }

    /// <summary>Ob die Seite „Server-Details“ offen ist.</summary>
    public bool IsDetailsOpen => Details is not null;

    /// <summary>„Details“ gibt es nur mit einer aktiven Sitzung, deren Server sich messen lässt.</summary>
    public bool CanShowDetails => ActiveSession is { CanMeasure: true };

    /// <summary>„Details“: öffnet die Seite für die aktive Sitzung – oder schließt sie, wenn sie schon offen ist.</summary>
    public void ShowDetails()
    {
        if (Details is not null)
        {
            CloseDetails();
            return;
        }

        if (!CanShowDetails)
        {
            return;
        }

        IsTerminalOnly = false;
        IsSettingsOpen = false;
        IsHelpOpen = false;
        Logs = null;
        Details = ActiveSession;
    }

    public void CloseDetails() => Details = null;

    partial void OnDetailsChanged(SessionViewModel? value) => UpdateMonitoring();
}
