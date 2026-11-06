namespace NexusSSH.Core.Models;

/// <summary>
/// Das Tempo eines Transfers aus seinen Fortschrittsmeldungen: der Schnitt über die letzten Sekunden, damit die
/// Zahl nicht bei jeder Meldung springt. Daraus ergibt sich, wie lange der Rest noch dauert.
/// </summary>
public sealed class TransferRate
{
    /// <summary>Über diese Spanne wird gemittelt.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>Kürzer als das sagt eine Messung nichts.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);

    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private (DateTimeOffset At, long Bytes) _last;

    /// <summary>Merkt sich den Stand zu einem Zeitpunkt. Ein kleinerer Stand als zuvor ist ein neuer Versuch – dann beginnt die Messung neu.</summary>
    public void Add(DateTimeOffset now, long bytes)
    {
        if (_samples.Count > 0 && (bytes < _last.Bytes || now < _last.At))
        {
            _samples.Clear();
        }

        _last = (now, bytes);
        _samples.Enqueue(_last);

        // Der älteste Stand bleibt, solange der nächste noch im Fenster liegt – so reicht die Messung über das ganze Fenster.
        while (_samples.Count > 2 && now - _samples.ElementAt(1).At >= Window)
        {
            _samples.Dequeue();
        }
    }

    public void Reset() => _samples.Clear();

    /// <summary>Bytes je Sekunde; <c>null</c>, solange zu kurz gemessen wurde.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples.Peek();
            var span = _last.At - first.At;
            return span >= Shortest ? (_last.Bytes - first.Bytes) / span.TotalSeconds : null;
        }
    }

    /// <summary>Wie lange es bei diesem Tempo bis <paramref name="total"/> noch dauert; <c>null</c>, wenn es steht oder unbekannt ist.</summary>
    public TimeSpan? Remaining(long total)
        => BytesPerSecond is > 0 and var rate && total > _last.Bytes && (total - _last.Bytes) / rate is < 360_000 and var seconds
            ? TimeSpan.FromSeconds(Math.Ceiling(seconds))
            : null;
}
