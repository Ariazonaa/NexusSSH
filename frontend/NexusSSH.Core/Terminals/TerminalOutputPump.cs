namespace NexusSSH.Core.Terminals;

/// <summary>
/// Bringt die Ausgabe der Sitzungen geordnet zum Terminal und bremst, wenn es nicht nachkommt
/// (<c>docs/architecture.md</c>, „Flusskontrolle“).
/// <para>
/// Ein Server kann viel schneller schreiben, als ein Terminal zeichnet (<c>cat</c> einer großen Datei). Ohne
/// Bremse wüchse die Warteschlange des UI-Threads ohne Grenze. Deshalb:
/// </para>
/// <list type="bullet">
/// <item>Blöcke, die zwischen zwei Durchläufen des UI-Threads eintreffen, werden je Sitzung zu einer Nachricht
/// zusammengefasst.</item>
/// <item>Das Terminal bestätigt, was es gezeichnet hat. Steht zu viel aus (<see cref="HighWater"/>), wird die
/// Sitzung angehalten – der Kern holt dann nichts mehr vom Server ab – und unter <see cref="LowWater"/>
/// fortgesetzt.</item>
/// </list>
/// </summary>
public sealed class TerminalOutputPump
{
    /// <summary>Ab so vielen unbestätigten Bytes wird eine Sitzung angehalten.</summary>
    public const int HighWater = 1024 * 1024;

    /// <summary>Unter so vielen unbestätigten Bytes läuft sie weiter.</summary>
    public const int LowWater = 256 * 1024;

    /// <summary>Größer wird eine einzelne Nachricht an das Terminal nicht.</summary>
    public const int MaxChunk = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemoryStream> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unacknowledged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private readonly Action<string, ReadOnlyMemory<byte>> _post;
    private readonly Action<Action> _schedule;
    private readonly Action<string, bool> _setPaused;
    private bool _scheduled;

    /// <param name="post">Schickt einen Block an das Terminal der Sitzung. Läuft auf dem UI-Thread.</param>
    /// <param name="schedule">Führt eine Aktion demnächst auf dem UI-Thread aus.</param>
    /// <param name="setPaused">Hält die Ausgabe einer Sitzung an oder lässt sie weiterlaufen.</param>
    public TerminalOutputPump(Action<string, ReadOnlyMemory<byte>> post, Action<Action> schedule, Action<string, bool> setPaused)
    {
        _post = post;
        _schedule = schedule;
        _setPaused = setPaused;
    }

    /// <summary>Ausgabe einer Sitzung, von einem beliebigen Thread. Der Puffer wird kopiert.</summary>
    public void Add(string id, ReadOnlySpan<byte> data)
    {
        bool schedule;
        lock (_gate)
        {
            if (!_waiting.TryGetValue(id, out var buffer))
            {
                _waiting[id] = buffer = new MemoryStream();
            }

            buffer.Write(data);
            schedule = !_scheduled;
            _scheduled = true;
        }

        if (schedule)
        {
            _schedule(Flush);
        }
    }

    /// <summary>Das Terminal hat so viele Bytes der Sitzung gezeichnet. UI-Thread.</summary>
    public void Acknowledge(string id, long count)
    {
        bool resume;
        lock (_gate)
        {
            var left = Math.Max(_unacknowledged.GetValueOrDefault(id) - Math.Max(count, 0), 0);
            _unacknowledged[id] = left;
            resume = left < LowWater && _paused.Remove(id);
        }

        if (resume)
        {
            _setPaused(id, false);
        }
    }

    /// <summary>
    /// So viele Bytes der Sitzung hat die Seite noch nicht bestätigt (wartend oder unterwegs). 0 heißt: Alles, was
    /// der Server geschickt hat, ist im Terminal angekommen – daran misst der Selbsttest das Tempo.
    /// </summary>
    public long Backlog(string id)
    {
        lock (_gate)
        {
            return (_waiting.TryGetValue(id, out var buffer) ? buffer.Length : 0) + _unacknowledged.GetValueOrDefault(id);
        }
    }

    /// <summary>Die Sitzung ist zu; was noch für sie wartet, entfällt.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            _waiting.Remove(id);
            _unacknowledged.Remove(id);
            _paused.Remove(id);
        }
    }

    /// <summary>
    /// Das Terminal der Sitzung zieht in ein anderes Fenster um: Was noch für sie wartet, kommt zurück statt zu
    /// entfallen, und eine angehaltene Sitzung läuft weiter – die Bestätigungen dieser Seite kommen nicht mehr.
    /// </summary>
    public byte[] Take(string id)
    {
        byte[] waiting;
        bool resume;
        lock (_gate)
        {
            waiting = _waiting.Remove(id, out var buffer) ? buffer.ToArray() : [];
            _unacknowledged.Remove(id);
            resume = _paused.Remove(id);
        }

        if (resume)
        {
            _setPaused(id, false);
        }

        return waiting;
    }

    /// <summary>
    /// Die Terminal-Seite wurde neu geladen: Ihre Bestätigungen für das, was unterwegs war, kommen nie – sonst
    /// bliebe eine angehaltene Sitzung für immer stehen.
    /// </summary>
    public void Reset()
    {
        List<string> resumed;
        lock (_gate)
        {
            _unacknowledged.Clear();
            resumed = [.. _paused];
            _paused.Clear();
        }

        foreach (var id in resumed)
        {
            _setPaused(id, false);
        }
    }

    // Läuft auf dem UI-Thread: alles, was sich angesammelt hat, je Sitzung in einem Stück weiterreichen.
    private void Flush()
    {
        List<(string Id, byte[] Data)> blocks;
        List<string> pause = [];
        lock (_gate)
        {
            _scheduled = false;
            blocks = [.. _waiting.Where(pair => pair.Value.Length > 0).Select(pair => (pair.Key, pair.Value.ToArray()))];
            _waiting.Clear();
            foreach (var (id, data) in blocks)
            {
                var total = _unacknowledged.GetValueOrDefault(id) + data.Length;
                _unacknowledged[id] = total;
                if (total > HighWater && _paused.Add(id))
                {
                    pause.Add(id);
                }
            }
        }

        foreach (var id in pause)
        {
            _setPaused(id, true);
        }

        foreach (var (id, data) in blocks)
        {
            for (var offset = 0; offset < data.Length; offset += MaxChunk)
            {
                _post(id, data.AsMemory(offset, Math.Min(MaxChunk, data.Length - offset)));
            }
        }
    }
}
