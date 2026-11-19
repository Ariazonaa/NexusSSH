using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ein Löschvorgang als Zeile der Transfers (Wunsch des Users 2026-11-19: „Löschen soll unten sichtbar sein wie ein
/// Upload“): was gelöscht wird, auf welchem Server, wie viele Einträge schon weg sind, und „Abbrechen“. Wie viele es
/// werden, ist vorher nicht bekannt – es gibt einen Zähler, kein Prozent. Nicht gespeichert: Nach einem Neustart
/// setzt nichts ein Löschen fort.
/// </summary>
public sealed partial class DeletionViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly TaskCompletionSource<DeletionStatus> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDeletion? _run;
    private bool _cancelWanted;

    internal DeletionViewModel(string name, string server, IUiDispatcher dispatcher)
    {
        (Name, Server, _dispatcher) = (name, server, dispatcher);
        Show(new DeletionStatus(DeletionState.Running, 0));
    }

    /// <summary>Eindeutig je Löschvorgang.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Was gelöscht wird, wie es angezeigt wird: der Name des Eintrags (schon gesäubert) oder „3 Einträge“.</summary>
    public string Name { get; }

    public string Server { get; }

    [ObservableProperty]
    public partial DeletionState State { get; private set; }

    /// <summary>So viele Einträge sind schon weg.</summary>
    [ObservableProperty]
    public partial long Removed { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>„1.234 gelöscht“, bei einem Fehler dazu dessen Grund.</summary>
    [ObservableProperty]
    public partial string DetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Läuft noch – lässt sich also anhalten.</summary>
    public bool CanCancel => IsRunning;

    /// <summary>Bei <see cref="DeletionState.Failed"/> der Grund als Satz (<see cref="FailureText"/>); sonst <c>null</c>.</summary>
    public string? Failure { get; private set; }

    /// <summary>Endet mit dem Endzustand, im Thread der Oberfläche gesetzt.</summary>
    public Task<DeletionStatus> Finished => _finished.Task;

    /// <summary>Sein Stand hat sich geändert (im Thread der Oberfläche).</summary>
    internal event Action<DeletionViewModel>? Changed;

    /// <summary>„1.234 gelöscht“ in der Schreibweise der Sprache.</summary>
    public string RemovedText => L.Get("DeletionRemoved", Removed.ToString("N0", L.Culture));

    /// <summary>Was gelöscht wird, Server und Stand – die Liste liest das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Name}, {Server}, {StatusText}";

    /// <summary>Hält ihn nach dem Eintrag an, der gerade gelöscht wird. Was weg ist, bleibt weg.</summary>
    public void Cancel()
    {
        if (!IsRunning)
        {
            return;
        }

        _cancelWanted = true;
        _run?.Cancel();
    }

    /// <summary>Beginnt den Löschvorgang; <paramref name="start"/> bekommt, wohin der Stand gemeldet wird.</summary>
    internal void Begin(Func<Action<DeletionStatus>, IDeletion> start)
    {
        try
        {
            _run = start(status => _dispatcher.Post(() => Apply(status)));
        }
        catch (RemoteFileException e)
        {
            Apply(new DeletionStatus(DeletionState.Failed, Removed, e.Code, e.Message));
            return;
        }

        // Abgebrochen, bevor der Start zurückkam (die erste Meldung kann davor liegen).
        if (_cancelWanted)
        {
            _run.Cancel();
        }
    }

    // Nach dem Ende zählt nichts mehr – auch keine Meldung, die sich unterwegs verspätet hat.
    private void Apply(DeletionStatus status)
    {
        if (State != DeletionState.Running)
        {
            return;
        }

        Show(status);
        if (status.IsFinished)
        {
            _finished.TrySetResult(status with { Removed = Removed });
        }

        Changed?.Invoke(this);
    }

    private void Show(DeletionStatus status)
    {
        Removed = Math.Max(Removed, status.Removed);
        State = status.State;
        IsRunning = status.State == DeletionState.Running;
        Failure = status.State == DeletionState.Failed ? FailureText.Describe(status.Code, status.Message) : null;
        StatusText = status.State switch
        {
            DeletionState.Running => L.Get("DeletionRunning"),
            DeletionState.Done => L.Get("DeletionDone"),
            DeletionState.Cancelled => L.Get("TransferCancelled"),
            _ => L.Get("TransferFailed"),
        };
        DetailText = Failure is { } failure ? $"{RemovedText}  ·  {failure}" : RemovedText;
        OnPropertyChanged(nameof(RemovedText));
    }
}
