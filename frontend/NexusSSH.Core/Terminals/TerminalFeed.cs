using NexusSSH.Core.Services;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Die Ausgabe einer Sitzung auf dem Weg zu ihrem Terminal – der eine Hörer, der bleibt, wenn das Terminal in ein
/// anderes Fenster umzieht. Solange kein Terminal angeschlossen ist, hebt er auf, was kommt; das neue Terminal
/// bekommt zuerst den Inhalt des alten (<see cref="CompleteMove"/>), dann das Aufgehobene, dann alles Weitere –
/// nichts doppelt, nichts verloren, gleich von welchem Thread die Ausgabe kommt.
/// </summary>
public sealed class TerminalFeed : IDisposable
{
    /// <summary>Mehr wird während eines Umzugs nicht aufgehoben; darüber zählt das Neueste.</summary>
    public const int MaxHeld = 16 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly ITerminalSession _terminal;
    private MemoryStream _held = new();
    private Action<ReadOnlyMemory<byte>>? _sink;
    private TaskCompletionSource<byte[]>? _move;

    public TerminalFeed(ITerminalSession terminal)
    {
        _terminal = terminal;
        terminal.Output += OnOutput;
    }

    /// <summary>Ob gerade ein Terminal angeschlossen ist.</summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _sink is not null;
            }
        }
    }

    /// <summary>
    /// Der Inhalt, den das vorige Terminal hinterlassen hat – fertig, sobald es ihn genannt hat. Ohne Umzug sofort
    /// und leer.
    /// </summary>
    public Task<byte[]> Moved
    {
        get
        {
            lock (_gate)
            {
                return _move?.Task ?? Task.FromResult<byte[]>([]);
            }
        }
    }

    /// <summary>
    /// Schließt ein Terminal an: <paramref name="first"/> (der Inhalt des vorigen), dann was unterdessen kam, dann
    /// alles Weitere gehen an <paramref name="sink"/>. Der Puffer gilt dort nur während des Aufrufs.
    /// </summary>
    public void Attach(Action<ReadOnlyMemory<byte>> sink, ReadOnlyMemory<byte> first = default)
    {
        lock (_gate)
        {
            if (!first.IsEmpty)
            {
                sink(first);
            }

            if (_held.Length > 0)
            {
                sink(_held.ToArray());
            }

            _held = new MemoryStream();
            _sink = sink;
            _move = null;
        }
    }

    /// <summary>
    /// Das Terminal zieht um: Ab jetzt wird aufgehoben, bis das neue angeschlossen ist. Das alte nennt seinen Inhalt
    /// danach mit <see cref="CompleteMove"/>.
    /// </summary>
    public void BeginMove()
    {
        lock (_gate)
        {
            _sink = null;
            _move ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Der Inhalt des alten Terminals – leer, wenn es keinen nennen konnte. Ein zweiter Aufruf tut nichts.</summary>
    public void CompleteMove(byte[] content)
    {
        TaskCompletionSource<byte[]>? move;
        lock (_gate)
        {
            move = _move;
        }

        move?.TrySetResult(content);
    }

    public void Dispose()
    {
        _terminal.Output -= OnOutput;
        CompleteMove([]);
    }

    private void OnOutput(ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            if (_sink is { } sink)
            {
                sink(data);
                return;
            }

            if (_held.Length + data.Length > MaxHeld)
            {
                _held = new MemoryStream();
            }

            if (data.Length <= MaxHeld)
            {
                _held.Write(data.Span);
            }
        }
    }
}
