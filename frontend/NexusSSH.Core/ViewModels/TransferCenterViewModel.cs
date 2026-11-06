using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Zeile im Transfer Center. Wird bei Fortschritt aktualisiert, nicht ersetzt – die Liste flackert nicht.</summary>
public sealed partial class TransferItemViewModel : ObservableObject
{
    private readonly TimeProvider _time;
    private readonly TransferRate _rate = new();
    private long _resumedAt;

    public TransferItemViewModel(TransferItem item, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        (Id, FileName, Server, IsUpload) = (item.Id, Format.Visible(item.FileName), item.Server, item.Direction == TransferDirection.Upload);
        Update(item);
    }

    public string Id { get; }

    public string FileName { get; }

    public string Server { get; }

    public bool IsUpload { get; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Übertragene und gesamte Menge – samt der Stelle, an der ein Upload fortgesetzt wurde –, bei einem Fehler dessen Grund.</summary>
    [ObservableProperty]
    public partial string DetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>Wartet oder läuft noch – lässt sich also abbrechen.</summary>
    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    /// <summary>Gescheitert oder abgebrochen – lässt sich noch einmal starten.</summary>
    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    /// <summary>Vom Benutzer angehalten („Alle anhalten“) – geht mit „Fortsetzen“ dort weiter.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// <summary>Er wartet in der Warteschlange – nur dann lässt sich seine Reihenfolge ändern.</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; }

    /// <summary>Vor ihm wartet noch einer: „nach vorn“ hat eine Wirkung.</summary>
    [ObservableProperty]
    public partial bool CanMoveUp { get; internal set; }

    /// <summary>Hinter ihm wartet noch einer.</summary>
    [ObservableProperty]
    public partial bool CanMoveDown { get; internal set; }

    /// <summary>Datei, Server und Stand – die Liste liest das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{FileName}, {Server}, {StatusText}";

    internal void Update(TransferItem item)
    {
        // Tempo und Restzeit ergeben sich aus den Meldungen, solange der Transfer läuft.
        if (item.State == TransferState.Running)
        {
            // Was ein Versuch davor schon übertragen hatte, kommt als Sprung dazu – das ist kein Tempo.
            if (item.ResumedAt != _resumedAt)
            {
                _rate.Reset();
                _resumedAt = item.ResumedAt;
            }

            _rate.Add(_time.GetUtcNow(), item.Bytes);
        }
        else
        {
            _rate.Reset();
        }

        IsPaused = WasPaused(item);
        ProgressPercent = item.Progress * 100;
        StatusText = Describe(item);
        DetailText = WithPace(Detail(item), item);
        IsRunning = item.State == TransferState.Running;
        IsWaiting = item.State == TransferState.Queued;
        CanCancel = !item.IsFinished;
        CanRetry = IsRetryable(item);
    }

    internal static bool WasPaused(TransferItem item)
        => item.State == TransferState.Failed && item.Detail?.StartsWith(ITransferQueue.PausedDetail, StringComparison.Ordinal) == true;

    // „12 MB / 92 MB · 4,1 MB/s · noch 0:19“ – das Tempo, sobald es sich messen lässt, die Restzeit, wenn das Ende bekannt ist.
    private string WithPace(string detail, TransferItem item)
    {
        if (item.State != TransferState.Running || _rate.BytesPerSecond is not { } rate)
        {
            return detail;
        }

        var parts = new List<string> { detail, Format.Rate((long)rate) };
        if (_rate.Remaining(item.Total) is { } left)
        {
            parts.Add(L.Get("TransferRemaining", Format.Duration((int)left.TotalSeconds)));
        }

        return string.Join("  ·  ", parts.Where(part => part.Length > 0));
    }

    private static bool IsRetryable(TransferItem item) => item.State is TransferState.Failed or TransferState.Cancelled;

    private static string Describe(TransferItem item)
    {
        var percent = (int)Math.Round(item.Progress * 100);
        return (item.State, item.Direction) switch
        {
            (TransferState.Running, TransferDirection.Upload) => L.Get("TransferUploading", percent),
            (TransferState.Running, TransferDirection.Download) => L.Get("TransferDownloading", percent),
            (TransferState.Queued, TransferDirection.Upload) => L.Get("TransferQueuedUpload"),
            (TransferState.Queued, TransferDirection.Download) => L.Get("TransferQueuedDownload"),
            (TransferState.Completed, _) => L.Get("TransferCompleted"),
            (TransferState.Cancelled, _) => L.Get("TransferCancelled"),
            (TransferState.Failed, _) when WasPaused(item) => L.Get("TransferPaused"),
            _ => L.Get("TransferFailed"),
        };
    }

    private static string Detail(TransferItem item) => item.State switch
    {
        TransferState.Failed when !WasPaused(item) => FailureText.OfTransfer(item.Detail),
        TransferState.Running when item is { Total: > 0, ResumedAt: > 0 }
            => L.Get("TransferProgressResumed", Format.FileSize(item.Bytes, isDirectory: false), Format.FileSize(item.Total, isDirectory: false), Format.FileSize(item.ResumedAt, isDirectory: false)),
        TransferState.Running when item.Total > 0
            => $"{Format.FileSize(item.Bytes, isDirectory: false)} / {Format.FileSize(item.Total, isDirectory: false)}",
        _ when item.Total > 0 => Format.FileSize(item.Total, isDirectory: false),

        // Lauter leere Dateien: Bytes gibt es keine, also zählen die Dateien.
        TransferState.Running when item.Files > 1 => L.Get("TransferFilesProgress", item.FilesDone, item.Files),
        _ when item.Files > 1 => L.Get("TransferFiles", item.Files),
        _ => string.Empty,
    };
}

public enum OverallTransferState
{
    /// <summary>Nichts läuft, nichts wartet.</summary>
    None,

    /// <summary>Etwas läuft oder wartet, und es ist bekannt, wie viel es wird.</summary>
    Running,

    /// <summary>Etwas läuft oder wartet, aber die Menge ist noch nicht bekannt.</summary>
    Unknown,

    /// <summary>Nichts läuft mehr, aber etwas ist angehalten und wartet auf „Fortsetzen“.</summary>
    Paused,
}

/// <summary>Der Stand aller Transfers zusammen – für das Symbol in der Taskleiste. <see cref="Fraction"/> liegt in 0–1.</summary>
public readonly record struct OverallTransfer(OverallTransferState State, double Fraction)
{
    public static readonly OverallTransfer Idle = new(OverallTransferState.None, 0);
}

/// <summary>Transfer Center (Spec §7): alle Transfers aller Sitzungen.</summary>
public sealed class TransferCenterViewModel : IDisposable
{
    private readonly ITransferQueue _queue;
    private readonly IUiDispatcher _dispatcher;
    private readonly HashSet<string> _finishedUploads = [];
    private readonly HashSet<string> _failed = [];
    private readonly HashSet<string> _completed = [];
    private readonly HashSet<string> _batch = [];
    private bool _ran;

    private readonly TimeProvider? _time;

    public TransferCenterViewModel(ITransferQueue queue, IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _time = time;

        // Was beim Start schon fertig ist, gilt nicht als „gerade fertig geworden“.
        _finishedUploads.UnionWith(queue.Items.Where(IsFinishedUpload).Select(item => item.Id));
        _failed.UnionWith(queue.Items.Where(item => item.State == TransferState.Failed).Select(item => item.Id));
        _completed.UnionWith(queue.Items.Where(item => item.State == TransferState.Completed).Select(item => item.Id));
        Sync();
        queue.Changed += OnChanged;
    }

    /// <summary>Ein Upload ist gerade fertig geworden; genannt wird der Server. Der Dateibrowser lädt dann neu.</summary>
    public event Action<string>? UploadCompleted;

    /// <summary>Ein Transfer ist gerade fertig geworden – in welche Richtung auch immer: Datei und Server.</summary>
    public event Action<string, string>? TransferCompleted;

    /// <summary>Ein Transfer ist gerade gescheitert: Datei, Server und der Grund als Satz (<see cref="FailureText"/>).</summary>
    public event Action<string, string, string>? TransferFailed;

    /// <summary>Die Liste wurde mit der Warteschlange abgeglichen – auch der Stand der einzelnen Zeilen.</summary>
    public event Action? Synced;

    public ObservableCollection<TransferItemViewModel> Items { get; } = [];

    /// <summary>
    /// Der Stand aller Transfers zusammen, nach jedem Abgleich neu (<see cref="Synced"/>). Gerechnet wird über
    /// alles, was seit dem letzten Leerlauf lief: Wird von drei Transfers einer fertig, springt der Balken nicht
    /// zurück. Was scheitert oder abgebrochen wird, zählt nicht mehr mit.
    /// </summary>
    public OverallTransfer Overall { get; private set; } = OverallTransfer.Idle;

    /// <summary>So viele Transfers laufen oder warten noch.</summary>
    public int ActiveCount => Items.Count(item => item.CanCancel);

    public void Cancel(TransferItemViewModel item) => _queue.Cancel(item.Id);

    /// <summary>Startet einen gescheiterten oder abgebrochenen Transfer noch einmal.</summary>
    public bool Retry(TransferItemViewModel item) => _queue.Retry(item.Id);

    public void ClearFinished() => _queue.ClearFinished();

    /// <summary>Etwas läuft oder wartet – lässt sich also anhalten.</summary>
    public bool CanPauseAll => Items.Any(item => item.CanCancel);

    /// <summary>Etwas ist angehalten – lässt sich also fortsetzen.</summary>
    public bool CanResumeAll => Items.Any(item => item.IsPaused);

    /// <summary>Hält alles an, was läuft oder wartet. Was schon übertragen ist, bleibt; „Alle fortsetzen“ macht dort weiter.</summary>
    public void PauseAll() => _queue.PauseAll();

    /// <summary>Hält einen einzelnen Transfer an; „Wiederholen“ setzt ihn fort.</summary>
    public void Pause(TransferItemViewModel item) => _queue.Pause(item.Id);

    /// <summary>Rückt einen wartenden Transfer in der Warteschlange. <c>false</c>, wenn er nicht mehr wartet.</summary>
    public bool Move(TransferItemViewModel item, QueueMove move) => _queue.Move(item.Id, move);

    /// <summary>Setzt fort, was angehalten wurde; liefert, wie viele das waren.</summary>
    public int ResumeAll() => Items.Where(item => item.IsPaused).Select(item => item.Id).ToList().Count(_queue.Retry);

    public void Dispose() => _queue.Changed -= OnChanged;

    private static bool IsFinishedUpload(TransferItem item)
        => item is { Direction: TransferDirection.Upload, State: TransferState.Completed };

    private void OnChanged() => _dispatcher.Post(Sync);

    private void Sync()
    {
        var current = _queue.Items;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!current.Any(item => item.Id == Items[i].Id))
            {
                Items.RemoveAt(i);
            }
        }

        foreach (var item in current)
        {
            var existing = Items.FirstOrDefault(row => row.Id == item.Id);
            if (existing is null)
            {
                Items.Add(new TransferItemViewModel(item, _time));
            }
            else
            {
                existing.Update(item);
            }

            if (IsFinishedUpload(item) && _finishedUploads.Add(item.Id))
            {
                UploadCompleted?.Invoke(item.Server);
            }

            if (item.State == TransferState.Completed && _completed.Add(item.Id))
            {
                TransferCompleted?.Invoke(Format.Visible(item.FileName), item.Server);
            }

            // Angehalten ist nicht gescheitert: kein Hinweis, kein Eintrag unter den Problemen.
            if (item.State == TransferState.Failed && _failed.Add(item.Id) && !TransferItemViewModel.WasPaused(item))
            {
                TransferFailed?.Invoke(Format.Visible(item.FileName), item.Server, FailureText.OfTransfer(item.Detail));
            }

            // Läuft er nach „Wiederholen“ noch einmal, zählt ein erneutes Scheitern wieder als neu.
            if (item.State != TransferState.Failed)
            {
                _failed.Remove(item.Id);
            }
        }

        // Die Zeilen stehen wie in der Warteschlange – nach einem Umstellen ziehen sie nach.
        for (var i = 0; i < current.Count; i++)
        {
            if (Items[i].Id != current[i].Id)
            {
                Items.Move(Items.IndexOf(Items.First(row => row.Id == current[i].Id)), i);
            }
        }

        var waiting = Items.Where(row => row.IsWaiting).ToList();
        foreach (var row in Items)
        {
            var at = waiting.IndexOf(row);
            (row.CanMoveUp, row.CanMoveDown) = (at > 0, at >= 0 && at < waiting.Count - 1);
        }

        _finishedUploads.IntersectWith(current.Select(item => item.Id));
        _failed.IntersectWith(current.Select(item => item.Id));
        _completed.IntersectWith(current.Select(item => item.Id));
        Overall = Sum(current);
        Synced?.Invoke();
    }

    private OverallTransfer Sum(IReadOnlyList<TransferItem> current)
    {
        var active = current.Where(item => !item.IsFinished).ToList();
        if (active.Count == 0)
        {
            // Angehalten zeigt nur, was in diesem Lauf angehalten wurde – nicht, was vom letzten Start noch dasteht.
            _batch.Clear();
            return _ran && current.Any(TransferItemViewModel.WasPaused) ? new OverallTransfer(OverallTransferState.Paused, 1) : OverallTransfer.Idle;
        }

        _ran = true;
        _batch.UnionWith(active.Select(item => item.Id));
        var counted = current.Where(item => _batch.Contains(item.Id) && (!item.IsFinished || item.State == TransferState.Completed)).ToList();

        // Solange ein Transfer seine Menge nicht kennt (ein Ordner wird noch erfasst), wäre jeder Anteil geraten.
        if (active.Any(item => item.Total <= 0))
        {
            return new OverallTransfer(OverallTransferState.Unknown, 0);
        }

        var (bytes, total) = (0d, 0d);
        foreach (var item in counted)
        {
            total += item.Total;
            bytes += item.State == TransferState.Completed ? item.Total : Math.Clamp(item.Bytes, 0, item.Total);
        }

        return new OverallTransfer(OverallTransferState.Running, total > 0 ? Math.Clamp(bytes / total, 0, 1) : 0);
    }
}
