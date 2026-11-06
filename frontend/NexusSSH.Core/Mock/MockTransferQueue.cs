using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Mock;

/// <summary>
/// Beginnt mit drei Transfers wie in Spec §7: einer läuft, einer ist fertig, einer wartet. Mit jedem
/// <see cref="Advance"/> (Tests, oder ein Timer, wenn es einen <see cref="TimeProvider"/> gibt) rückt der laufende
/// vor, danach startet der nächste wartende. Sind alle fertig, bleibt die Liste stehen.
/// </summary>
public sealed class MockTransferQueue : ITransferQueue, IDisposable
{
    private const double Step = 0.02;

    private readonly Lock _gate = new();
    private readonly ITimer? _timer;
    private int _lastId = 3;
    private TransferItem[] _items =
    [
        new("t1", "backup.tar.gz", "web01", TransferDirection.Upload, TransferState.Running, 0.72, 528_482_304, 734_003_200),
        new("t2", "nginx.conf", "web01", TransferDirection.Upload, TransferState.Completed, 1, 1_834, 1_834),
        new("t3", "database.sql", "database01", TransferDirection.Download, TransferState.Queued, 0, 0, 96_468_992),
    ];

    /// <param name="timeProvider">Ohne <see cref="TimeProvider"/> läuft kein Timer; dann treibt <see cref="Advance"/> die Liste (Tests).</param>
    /// <param name="showcase">Die Server aus dem Bild der Beispieldaten (<see cref="MockBackend.ShowcaseTabs"/>).</param>
    public MockTransferQueue(TimeProvider? timeProvider = null, bool showcase = false)
    {
        if (showcase)
        {
            _items = [_items[0], _items[1], _items[2] with { FileName = "dump.sql", Server = "db02" }];
        }

        _timer = timeProvider?.CreateTimer(_ => Advance(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public IReadOnlyList<TransferItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    public event Action? Changed;

    /// <summary>Stellt einen neuen Transfer ans Ende der Warteschlange und liefert seine Kennung.</summary>
    public string Add(string fileName, string server, TransferDirection direction, long total)
    {
        string id;
        lock (_gate)
        {
            id = $"t{++_lastId}";
            _items = [.. _items, new TransferItem(id, fileName, server, direction, TransferState.Queued, 0, 0, total)];
        }

        Changed?.Invoke();
        return id;
    }

    /// <summary>Schließt einen laufenden oder wartenden Transfer sofort ab – für Tests.</summary>
    public void Complete(string id)
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.Id == id && !item.IsFinished ? item with { State = TransferState.Completed, Progress = 1, Bytes = item.Total } : item)];
        }

        Changed?.Invoke();
    }

    /// <summary>Lässt einen laufenden oder wartenden Transfer scheitern – für Tests.</summary>
    public void Fail(string id, string detail)
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.Id == id && !item.IsFinished ? item with { State = TransferState.Failed, Detail = detail } : item)];
        }

        Changed?.Invoke();
    }

    public bool Retry(string id)
    {
        lock (_gate)
        {
            if (!_items.Any(item => item.Id == id && item.State is TransferState.Failed or TransferState.Cancelled))
            {
                return false;
            }

            _items = [.. _items.Select(item => item.Id == id ? item with { State = TransferState.Queued, Progress = 0, Bytes = 0, Detail = null } : item)];
        }

        Changed?.Invoke();
        return true;
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.Id == id && !item.IsFinished ? item with { State = TransferState.Cancelled } : item)];
        }

        Changed?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            _items = [.. _items.Where(item => !item.IsFinished)];
        }

        Changed?.Invoke();
    }

    /// <summary>Wie im Kern endet, was noch nicht fertig ist – gespeichert wird hier nichts, die Beispieldaten beginnen jedes Mal neu.</summary>
    public void Suspend()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.IsFinished ? item : item with { State = TransferState.Failed, Detail = "interrupted: " })];
        }

        Changed?.Invoke();
    }

    /// <summary>Hält an, was läuft oder wartet; „Wiederholen“ setzt es fort.</summary>
    public void PauseAll()
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.IsFinished ? item : item with { State = TransferState.Failed, Detail = ITransferQueue.PausedDetail })];
        }

        Changed?.Invoke();
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item => item.Id == id && !item.IsFinished ? item with { State = TransferState.Failed, Detail = ITransferQueue.PausedDetail } : item)];
        }

        Changed?.Invoke();
    }

    public bool Move(string id, QueueMove move)
    {
        lock (_gate)
        {
            var waiting = _items.Where(item => item.State == TransferState.Queued).Select(item => item.Id).ToList();
            var at = waiting.IndexOf(id);
            if (at < 0 || (move == QueueMove.Down ? at == waiting.Count - 1 : at == 0))
            {
                return false;
            }

            var (moved, before) = move switch
            {
                QueueMove.First => (id, waiting[0]),
                QueueMove.Up => (id, waiting[at - 1]),
                _ => (waiting[at + 1], id),
            };
            var rows = _items.ToList();
            var row = rows.First(item => item.Id == moved);
            rows.Remove(row);
            rows.Insert(rows.FindIndex(item => item.Id == before), row);
            _items = [.. rows];
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Die zuletzt gesetzte Obergrenze in Bytes je Sekunde – die Beispieldaten halten sich nicht daran.</summary>
    public long Limit { get; private set; }

    public void SetLimit(long bytesPerSecond) => Limit = bytesPerSecond;

    public void Advance()
    {
        lock (_gate)
        {
            var next = (TransferItem[])_items.Clone();
            var running = Array.FindIndex(next, item => item.State == TransferState.Running);
            if (running < 0)
            {
                running = Array.FindIndex(next, item => item.State == TransferState.Queued);
                if (running < 0)
                {
                    return;
                }

                next[running] = next[running] with { State = TransferState.Running };
            }
            else
            {
                var progress = Math.Min(1, next[running].Progress + Step);
                next[running] = next[running] with
                {
                    Progress = progress,
                    Bytes = (long)(next[running].Total * progress),
                    State = progress >= 1 ? TransferState.Completed : TransferState.Running,
                };
            }

            _items = next;
        }

        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();
}
