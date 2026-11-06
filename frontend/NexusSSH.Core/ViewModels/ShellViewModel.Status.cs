using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was der untere Bereich gerade zeigt – die Zeile unten schaltet danach um.</summary>
public enum BottomTab
{
    Output,
    Transfers,
    Problems,
    Search,
    History,
    Follow,
}

// Die Zeile unten im Fenster (Entwurf A, statt der Statusleiste): links die Auslastung des Servers der aktiven Sitzung
// (MonitorBar), rechts „Details“, „Transfers n“ und „Probleme n“. Dauer und Größe des Terminals stehen nirgends mehr,
// die Antwortzeit misst die Seite mit den Einzelheiten des Servers – weiter nur auf Klick.
public sealed partial class ShellViewModel
{
    /// <summary>Wie viele Transfers laufen oder warten – die Zahl in „Transfers n“.</summary>
    [ObservableProperty]
    public partial int TransferCount { get; private set; }

    /// <summary>Wie viele Probleme gemeldet sind – die Zahl in „Probleme n“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial int ProblemCount { get; private set; }

    /// <summary>„Probleme n“ steht nur da, wenn es welche gibt.</summary>
    public bool HasProblems => ProblemCount > 0;

    /// <summary>Welcher Reiter des unteren Bereichs gewählt ist; die Ansicht meldet jeden Wechsel.</summary>
    public BottomTab BottomTab { get; set; }

    /// <summary>Zeigt den unteren Bereich mit den Problemen.</summary>
    public void ShowProblems()
    {
        IsTerminalOnly = false;
        IsBottomPanelVisible = true;
        BottomTab = BottomTab.Problems;
        UiRequested?.Invoke(UiRequest.ShowProblems);
    }

    /// <summary>
    /// „Transfers n“: blendet den unteren Bereich mit den Transfers ein – oder wieder aus, wenn er sie gerade zeigt.
    /// </summary>
    public void ToggleTransfers()
    {
        if (ShowsBottom && BottomTab == BottomTab.Transfers)
        {
            IsBottomPanelVisible = false;
        }
        else
        {
            ShowTransfers();
        }
    }

    /// <summary>„Probleme n“: wie <see cref="ToggleTransfers"/>, für die Probleme.</summary>
    public void ToggleProblems()
    {
        if (ShowsBottom && BottomTab == BottomTab.Problems)
        {
            IsBottomPanelVisible = false;
        }
        else
        {
            ShowProblems();
        }
    }

    /// <summary>
    /// Zählt an den Reitern die Sekunden bis zum nächsten selbsttätigen Versuch herunter
    /// (<see cref="SessionViewModel.RetryText"/>); die Oberfläche ruft es im Sekundentakt. Ohne Sitzung, die es
    /// gerade von selbst versucht, geschieht nichts.
    /// </summary>
    public void RefreshRetries()
    {
        foreach (var session in Sessions.Where(session => session.IsRetrying || session.RetryText.Length > 0))
        {
            session.RefreshRetry();
        }
    }

    // Die Zahlen der Zeile unten auf den Stand bringen – nach jeder Meldung der Transfers und jedem Problem.
    private void RefreshCounts()
    {
        TransferCount = Transfers.ActiveCount;
        ProblemCount = Problems.Count;
    }
}
