using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was die Pille „Transfers“ unten gerade zeigt (<see cref="TransferBadge"/>).</summary>
public enum TransferBadgeKind
{
    /// <summary>Nichts läuft: „Transfers 0“ wie früher.</summary>
    Idle,

    /// <summary>Etwas läuft oder wartet: Richtung, Name oder Anzahl, Prozent und ein Balken.</summary>
    Running,

    /// <summary>Was zuletzt lief, ist angehalten und wartet auf „Fortsetzen“.</summary>
    Paused,

    /// <summary>Was zuletzt lief, ist fertig – steht <see cref="TransferBadge.DoneFor"/> lang da.</summary>
    Done,

    /// <summary>Was zuletzt lief, ist zum Teil gescheitert – steht, bis man draufklickt.</summary>
    Failed,
}

/// <summary>In welche Richtung das läuft, was die Pille zeigt.</summary>
public enum TransferBadgeDirection
{
    Up,
    Down,
    Both,

    /// <summary>Es wird gelöscht (<see cref="DeletionViewModel"/>): Papierkorb statt Pfeil.</summary>
    Delete,
}

/// <summary>
/// Die Pille „Transfers“ in der Zeile unten, als Anzeige des laufenden Transfers (Wunsch des Users 2026-11-18: „besser
/// sehen, wenn eine Datei hochgeladen wird“). Vorher stand dort nur „Transfers n“ in Grau – ob gerade etwas lief, sah
/// man nicht, und ein kleiner Upload war vorbei, bevor man hinsah. Jetzt: während etwas läuft Richtung, Name (oder
/// Anzahl), Prozent und ein Balken; danach <see cref="DoneFor"/> lang „fertig“, bei einem Fehler „fehlgeschlagen“, bis
/// man draufklickt (<see cref="Acknowledge"/>). Ein „Lauf“ reicht vom ersten Transfer, der beginnt, bis keiner mehr
/// läuft oder wartet – was in ihm geschah, entscheidet, was danach dasteht: gescheitert vor angehalten vor fertig.
/// Löschvorgänge (<see cref="DeletionViewModel"/>, 2026-11-19) gehören wie Transfers zum Lauf: Papierkorb, Name und
/// Zähler statt Prozent.
/// </summary>
public sealed partial class TransferBadge : ObservableObject, IDisposable
{
    /// <summary>So lange steht „fertig“ da, bevor die Pille wieder „Transfers 0“ zeigt.</summary>
    public static readonly TimeSpan DoneFor = TimeSpan.FromSeconds(5);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;

    // Der letzte bekannte Stand jedes Transfers dieses Laufs – auch eines, der inzwischen aus der Liste verschwand.
    private readonly Dictionary<string, TransferItem> _run = new(StringComparer.Ordinal);

    // Die Transfers, deren Scheitern gerade dasteht: Verschwinden sie (Liste geleert), geht die Meldung mit.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    // Wie bei den Transfers: die Löschvorgänge dieses Laufs und die, deren Scheitern gerade dasteht.
    private readonly Dictionary<string, DeletionViewModel> _deletionRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedDeletions = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private long _shown;

    public TransferBadge(IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        ShowIdle();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(HasIcon))]
    public partial TransferBadgeKind Kind { get; private set; }

    [ObservableProperty]
    public partial TransferBadgeDirection Direction { get; private set; }

    /// <summary>Der Text der Pille: „Transfers“, ein Dateiname, „3 Transfers“, „index.php hochgeladen“ …</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>Die fette Zahl dahinter: die Anzahl (ruhend) oder die Prozent (läuft); sonst leer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(HasNumber))]
    public partial string Number { get; private set; } = string.Empty;

    /// <summary>Der Stand in Prozent (0–100) für den Balken.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    /// <summary>Noch ist nicht bekannt, wie viel es wird (ein Ordner wird erfasst): Der Balken läuft ohne Stand.</summary>
    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    /// <summary>Der Tooltip: bei einem einzelnen Transfer Server, Menge, Tempo und Restzeit.</summary>
    [ObservableProperty]
    public partial string ToolTip { get; private set; } = string.Empty;

    public bool HasNumber => Number.Length > 0;

    public bool ShowsProgress => Kind == TransferBadgeKind.Running;

    public bool HasIcon => Kind != TransferBadgeKind.Idle;

    /// <summary>Was eine Sprachausgabe für die Pille liest.</summary>
    public string Name => HasNumber ? $"{Text} {Number}" : Text;

    /// <summary>
    /// Der Benutzer hat die Pille angeklickt: Ein „fertig“ oder „fehlgeschlagen“ ist gesehen und geht; was läuft oder
    /// angehalten ist, bleibt.
    /// </summary>
    public void Acknowledge()
    {
        if (Kind is TransferBadgeKind.Done or TransferBadgeKind.Failed)
        {
            ShowIdle();
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Nach jedem Abgleich der Transfers und jeder Meldung eines Löschvorgangs (<see cref="TransferCenterViewModel"/>).
    /// Löschvorgänge zählen wie Transfers zum Lauf; danach gilt dieselbe Reihenfolge – gescheitert vor angehalten
    /// vor fertig. Laufen beide Arten, nennt die Pille die Zahl der Vorgänge.
    /// </summary>
    internal void Update(IReadOnlyList<TransferItem> current, IReadOnlyList<TransferItemViewModel> rows, OverallTransfer overall, IReadOnlyList<DeletionViewModel>? deletions = null)
    {
        deletions ??= [];
        var active = current.Where(item => !item.IsFinished).ToList();
        foreach (var item in current.Where(item => !item.IsFinished || _run.ContainsKey(item.Id)))
        {
            _run[item.Id] = item;
        }

        var deleting = deletions.Where(deletion => deletion.IsRunning).ToList();
        foreach (var deletion in deleting)
        {
            _deletionRun[deletion.Id] = deletion;
        }

        if (active.Count > 0 || deleting.Count > 0)
        {
            ShowRunning(active, rows, overall, deleting);
            return;
        }

        if (_run.Count > 0 || _deletionRun.Count > 0)
        {
            var ended = _run.Values.ToList();
            var deleted = _deletionRun.Values.ToList();
            _run.Clear();
            _deletionRun.Clear();
            Conclude(ended, deleted);
            return;
        }

        // Kein Lauf seit dem letzten Mal: Was dasteht, gilt nur, solange es seine Transfers noch gibt.
        if (Kind == TransferBadgeKind.Failed
            && !current.Any(item => _failed.Contains(item.Id) && IsFailure(item))
            && !deletions.Any(deletion => _failedDeletions.Contains(deletion.Id)))
        {
            ShowIdle();
        }
        else if (Kind == TransferBadgeKind.Paused && !current.Any(TransferItemViewModel.WasPaused))
        {
            ShowIdle();
        }
    }

    private static bool IsFailure(TransferItem item)
        => item.State == TransferState.Failed && !TransferItemViewModel.WasPaused(item);

    private static TransferBadgeDirection DirectionOf(IEnumerable<TransferItem> items)
    {
        var directions = items.Select(item => item.Direction).Distinct().ToList();
        return directions switch
        {
            [TransferDirection.Upload] => TransferBadgeDirection.Up,
            [TransferDirection.Download] => TransferBadgeDirection.Down,
            _ => TransferBadgeDirection.Both,
        };
    }

    private static string PercentText(double fraction)
        => L.Get("TransferBadgePercent", ((int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100)).ToString(L.Culture));

    // Welche Richtung zeigt, was aus Transfers und Löschvorgängen besteht: nur gelöscht – der Papierkorb, beides – beide Pfeile.
    private static TransferBadgeDirection DirectionOf(IReadOnlyList<TransferItem> transfers, int deletions)
        => deletions == 0 ? DirectionOf(transfers)
            : transfers.Count == 0 ? TransferBadgeDirection.Delete
            : TransferBadgeDirection.Both;

    private static string Count(long number) => number.ToString(L.Culture);

    private void ShowRunning(List<TransferItem> active, IReadOnlyList<TransferItemViewModel> rows, OverallTransfer overall, List<DeletionViewModel> deleting)
    {
        if (deleting.Count == 0)
        {
            ShowRunning(active, rows, overall);
            return;
        }

        Show(TransferBadgeKind.Running);
        Direction = DirectionOf(active, deleting.Count);

        // Wie viele es werden, weiß niemand vorher: ein Zähler statt eines Prozents, der Balken läuft ohne Stand.
        IsIndeterminate = true;
        if (active.Count > 0)
        {
            Text = L.Get("TransferBadgeTasks", Count(active.Count + deleting.Count));
            return;
        }

        if (deleting is [var only])
        {
            Text = only.Name;
            ToolTip = only.Server;
        }
        else
        {
            Text = L.Get("DeletionBadgeMany", Count(deleting.Count));
        }

        Number = L.Get("DeletionBadgeRemoved", deleting.Sum(deletion => deletion.Removed).ToString("N0", L.Culture));
    }

    private void ShowRunning(List<TransferItem> active, IReadOnlyList<TransferItemViewModel> rows, OverallTransfer overall)
    {
        Show(TransferBadgeKind.Running);
        Direction = DirectionOf(active);

        // Einer allein: sein Name und sein eigener Stand. Mehrere: wie viele, und der Stand aller zusammen.
        if (active is [var only])
        {
            Text = Format.Visible(only.FileName);
            var row = rows.FirstOrDefault(row => row.Id == only.Id);
            ToolTip = row is null || row.DetailText.Length == 0 ? only.Server : $"{only.Server}\n{row.DetailText}";
            SetProgress(only.Total > 0 || only.Files > 0 ? only.Progress : null);
        }
        else
        {
            Text = L.Get("TransferBadgeMany", active.Count.ToString(L.Culture));
            ToolTip = L.Get("StatusShowTransfers");
            SetProgress(overall.State == OverallTransferState.Running ? overall.Fraction : null);
        }
    }

    private void SetProgress(double? fraction)
    {
        IsIndeterminate = fraction is null;
        Percent = fraction is { } known ? Math.Clamp(known, 0, 1) * 100 : 0;
        Number = fraction is { } shown ? PercentText(shown) : string.Empty;
    }

    private void Conclude(List<TransferItem> ended, List<DeletionViewModel> deleted)
    {
        var failed = ended.Where(IsFailure).ToList();
        var paused = ended.Where(TransferItemViewModel.WasPaused).ToList();
        var completed = ended.Where(item => item.State == TransferState.Completed).ToList();
        var failedDeletions = deleted.Where(deletion => deletion.State == DeletionState.Failed).ToList();
        var doneDeletions = deleted.Where(deletion => deletion.State == DeletionState.Done).ToList();
        if (failed.Count > 0 || failedDeletions.Count > 0)
        {
            Show(TransferBadgeKind.Failed);
            _failed.UnionWith(failed.Select(item => item.Id));
            _failedDeletions.UnionWith(failedDeletions.Select(deletion => deletion.Id));
            Direction = DirectionOf(failed, failedDeletions.Count);
            Text = (failed, failedDeletions) switch
            {
                ([var one], []) => L.Get("TransferBadgeFailed", Format.Visible(one.FileName)),
                ([], [var gone]) => L.Get("DeletionBadgeFailed", gone.Name),
                (_, []) => L.Get("TransferBadgeFailedMany", Count(failed.Count)),
                ([], _) => L.Get("DeletionBadgeFailedMany", Count(failedDeletions.Count)),
                _ => L.Get("TransferBadgeTasksFailed", Count(failed.Count + failedDeletions.Count)),
            };
        }
        else if (paused.Count > 0)
        {
            Show(TransferBadgeKind.Paused);
            Direction = DirectionOf(paused);
            Text = L.Get("TransferBadgePaused", paused is [var one] ? Format.Visible(one.FileName) : paused.Count.ToString(L.Culture));
        }
        else if (completed.Count > 0 || doneDeletions.Count > 0)
        {
            Show(TransferBadgeKind.Done);
            Direction = DirectionOf(completed, doneDeletions.Count);
            Text = (completed, doneDeletions) switch
            {
                ([{ Direction: TransferDirection.Upload } one], []) => L.Get("TransferBadgeUploaded", Format.Visible(one.FileName)),
                ([var one], []) => L.Get("TransferBadgeDownloaded", Format.Visible(one.FileName)),
                ([], [var gone]) => L.Get("DeletionBadgeDone", gone.Name),
                (_, []) => L.Get("TransferBadgeDone", Count(completed.Count)),
                ([], _) => L.Get("DeletionBadgeDoneMany", Count(doneDeletions.Count)),
                _ => L.Get("TransferBadgeTasksDone", Count(completed.Count + doneDeletions.Count)),
            };
            var shown = _shown;
            _timer = _time.CreateTimer(_ => _dispatcher.Post(() => EndDone(shown)), null, DoneFor, Timeout.InfiniteTimeSpan);
        }
        else
        {
            // Alles abgebrochen: Das hat der Benutzer selbst getan, dazu braucht es keine Meldung.
            ShowIdle();
        }
    }

    private void EndDone(long shown)
    {
        if (shown == _shown && Kind == TransferBadgeKind.Done)
        {
            ShowIdle();
        }
    }

    // Ein neuer Zustand: Was eine frühere Anzeige noch vorhatte (das Ende von „fertig“), gilt nicht mehr.
    private void Show(TransferBadgeKind kind)
    {
        _shown++;
        _timer?.Dispose();
        _timer = null;
        if (kind != TransferBadgeKind.Failed)
        {
            _failed.Clear();
            _failedDeletions.Clear();
        }

        Kind = kind;
        Number = string.Empty;
        Percent = 0;
        IsIndeterminate = false;
        ToolTip = L.Get("StatusShowTransfers");
    }

    private void ShowIdle()
    {
        Show(TransferBadgeKind.Idle);
        Direction = TransferBadgeDirection.Up;
        Text = L.Get("Transfers");
        Number = 0.ToString(CultureInfo.InvariantCulture);
    }
}
