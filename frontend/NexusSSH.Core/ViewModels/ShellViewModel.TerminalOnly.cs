using System.ComponentModel;

namespace NexusSSH.Core.ViewModels;

// „Nur das Terminal“: alles andere weicht – Titelleiste mit den Reitern, Dateibrowser, die Spalte rechts, unterer
// Bereich, die Zeile unten –, und das Fenster füllt den Bildschirm. Für lange Sitzungen in vim, htop oder mc, in
// denen jede Zeile zählt. Die Schalter der Bereiche bleiben dabei, wie sie sind; der Modus gilt, bis er abgeschaltet
// wird, und nie über einen Neustart hinaus.
public sealed partial class ShellViewModel
{
    private bool _terminalOnly;

    /// <summary>
    /// Ob nur das Terminal zu sehen ist. Der Modus endet von selbst, sobald etwas anderes als das Terminal an der
    /// Reihe ist (Startseite, Einstellungen, zwei Spalten, eine Seite des Servers, ein abgelöstes Terminal) –
    /// sonst stünde der Benutzer vor einer Ansicht ohne Titelleiste und ohne Weg zurück.
    /// </summary>
    public bool IsTerminalOnly
    {
        get => _terminalOnly;
        set
        {
            value = value && CanBeTerminalOnly;
            if (value == _terminalOnly)
            {
                return;
            }

            _terminalOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsFiles));
            OnPropertyChanged(nameof(ShowsSnippets));
            OnPropertyChanged(nameof(ShowsBottom));

            // Ohne die Zeile unten sieht niemand die Auslastung – dann wird auch nicht gemessen.
            UpdateMonitoring();
        }
    }

    /// <summary>Ob es gerade ein Terminal gibt, das den Bildschirm füllen kann.</summary>
    public bool CanBeTerminalOnly
        => ShowsTerminal && ActiveSession is { IsDetached: false } && !IsSettingsOpen && !IsHelpOpen && Viewer is null && !HasServerPage;

    /// <summary>Der untere Bereich ist zu sehen: eingeschaltet, und nicht „nur das Terminal“.</summary>
    public bool ShowsBottom => IsBottomPanelVisible && !IsTerminalOnly;

    /// <summary>Schaltet „nur das Terminal“ um; das Terminal bekommt danach den Fokus.</summary>
    public void ToggleTerminalOnly()
    {
        IsTerminalOnly = !IsTerminalOnly;
        FocusTerminal();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ShowsTerminal) or nameof(ActiveSession) or nameof(IsTerminalOnly))
        {
            OnPropertyChanged(nameof(ShowsMonitorBar));
            OnPropertyChanged(nameof(CanShowDetails));
            NotifySnippetsRunnable();
        }

        if (_terminalOnly && e.PropertyName != nameof(IsTerminalOnly) && !CanBeTerminalOnly)
        {
            IsTerminalOnly = false;
        }
    }
}
