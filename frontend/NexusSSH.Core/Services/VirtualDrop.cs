using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Einträge des Servers als <see cref="IVirtualFiles"/>: Dateien und ganze Ordner, die ein anderes Programm bekommt,
/// ohne dass sie vorher geladen sein müssen – beim Ziehen aus der Liste und nach „Kopieren“.
/// <para>
/// Was in einem Ordner liegt, sagt das Backend vorab (<see cref="IRemoteFileSystem.TreeAsync"/>) – mit denselben
/// Namen, unter denen ein Download die Dateien dann ablegt. Geladen wird erst, wenn der Empfänger den Inhalt
/// verlangt: je ausgewähltem Eintrag ein Download in einen eigenen Zwischenordner.
/// </para>
/// <para>
/// Der Empfänger bekommt den Inhalt schon, während er lädt: Der Download schreibt jede Datei erst unter einem
/// anderen Namen (<see cref="PartialSuffix"/>) und benennt sie zum Schluss um – diese Teil-Datei wird mitgelesen.
/// So bewegt sich die Anzeige des Empfängers, und ein Abbruch dort kommt gleich an, statt erst, wenn die Datei
/// ganz geladen ist.
/// </para>
/// </summary>
public sealed class VirtualDrop : IVirtualFiles
{
    /// <summary>Länger darf ein Pfad samt Unterordnern nicht sein: Mehr fasst die Liste nicht, die Windows dem Empfänger reicht.</summary>
    public const int MaxPathLength = 259;

    /// <summary>Mehr Einträge werden nicht angeboten – das Backend nennt auch nicht mehr.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Die Endung der Datei, in die ein Download schreibt, bis er vollständig ist – dieselbe wie im Kern
    /// (<c>transfers.rs</c>, <c>PARTIAL_SUFFIX</c>). Stimmte sie nicht, käme der Inhalt trotzdem an, nur erst am Stück.
    /// </summary>
    public const string PartialSuffix = ".nexus-part";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly DropFetcher _fetcher;
    private readonly ITransferQueue _queue;
    private readonly Func<IRemoteFileSystem?> _files;
    private readonly IReadOnlyList<DropItem> _items;
    private readonly Action<DropNotice, string?>? _notify;
    private CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Download> _downloads = [];
    private readonly HashSet<DropNotice> _told = [];
    private Task<Plan?>? _plan;

    /// <param name="files">Das Dateisystem der Sitzung, gefragt erst beim Holen: Bis dahin kann die Verbindung ersetzt oder weg sein.</param>
    /// <param name="notify">Bekommt jede Meldung höchstens einmal, dazu die Einzelheit des Servers, wenn es eine gibt.</param>
    public VirtualDrop(DropFetcher fetcher, ITransferQueue queue, Func<IRemoteFileSystem?> files, IEnumerable<DropItem> items, Action<DropNotice, string?>? notify = null)
    {
        _fetcher = fetcher;
        _queue = queue;
        _files = files;
        _items = [.. items];
        _notify = notify;
    }

    /// <summary>Wie viele Einträge ausgewählt wurden – Ordner zählen einmal, nicht mit ihrem Inhalt.</summary>
    public int Count => _items.Count;

    /// <summary>Ob ein Ordner dabei ist – das Bild beim Ziehen zeigt dann einen.</summary>
    public bool HasFolders => _items.Any(item => item.IsDirectory);

    /// <summary>Beginnt, den Inhalt der Ordner zu ermitteln – ehe jemand danach fragt: Der Empfänger wartet darauf.</summary>
    public void Prepare() => _ = PlanAsync();

    public async Task<IReadOnlyList<VirtualEntry>?> EntriesAsync() => (await PlanAsync().ConfigureAwait(false))?.Entries;

    public async Task<Stream?> OpenAsync(int index)
    {
        if (await PlanAsync().ConfigureAwait(false) is not { } plan || index < 0 || index >= plan.Entries.Count || plan.Entries[index].IsDirectory)
        {
            return null;
        }

        if (Start(plan.Tops[index]) is not { } download)
        {
            return null;
        }

        var local = Path.Combine(download.Folder, plan.Entries[index].Path.Replace('/', Path.DirectorySeparatorChar));
        var cancellation = download.Cancellation;
        var content = new Arriving(local, () => IsFinished(download.Transfer), () => WasCancelled(download.Transfer), cancellation);
        while (true)
        {
            if (File.Exists(local) || File.Exists(local + PartialSuffix))
            {
                return content;
            }

            // Der Transfer ist zu Ende, und die Datei ist nicht gekommen – gescheitert, abgebrochen, oder es gibt
            // sie auf dem Server inzwischen nicht mehr.
            if (IsFinished(download.Transfer))
            {
                return File.Exists(local) ? content : null;
            }

            try
            {
                await Task.Delay(Poll, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Ein Durchgang ist zu Ende: Der Empfänger ist fertig, hat aufgegeben, oder es gab nie einen. Was noch lädt,
    /// wird abgebrochen; die Zwischenordner verschwinden, sobald ihre Transfers zu Ende sind. Fragt später wieder
    /// jemand (ein zweites Einfügen aus der Zwischenablage), beginnt alles von vorn – auch die Frage, was in den
    /// Ordnern liegt.
    /// </summary>
    public void Completed(bool succeeded)
    {
        List<Download> downloads;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            downloads = [.. _downloads.Values];
            _downloads.Clear();
            (cancellation, _cancellation) = (_cancellation, new CancellationTokenSource());
            if (_plan is { IsCompleted: true })
            {
                _plan = null;
            }
        }

        // Was jetzt noch lädt, holt niemand mehr ab – auch nach einem Erfolg nicht: Der Empfänger hat, was er wollte.
        cancellation.Cancel();
        foreach (var download in downloads.Where(download => !IsFinished(download.Transfer)))
        {
            _queue.Cancel(download.Transfer);
        }

        _ = Task.Run(async () =>
        {
            foreach (var download in downloads)
            {
                while (!IsFinished(download.Transfer))
                {
                    await Task.Delay(Poll).ConfigureAwait(false);
                }

                WorkFolders.Remove(download.Folder);
            }
        });
    }

    private Task<Plan?> PlanAsync()
    {
        lock (_gate)
        {
            return _plan ??= BuildAsync();
        }
    }

    private async Task<Plan?> BuildAsync()
    {
        if (_files() is not { } files)
        {
            Notify(DropNotice.NotConnected);
            return null;
        }

        var entries = new List<VirtualEntry>();
        var tops = new List<int>();
        try
        {
            for (var top = 0; top < _items.Count; top++)
            {
                var item = _items[top];
                if (!item.IsDirectory)
                {
                    entries.Add(new VirtualEntry(item.Name, false, item.Size, item.Modified));
                    tops.Add(top);
                    continue;
                }

                foreach (var entry in await files.TreeAsync(item.RemotePath).ConfigureAwait(false))
                {
                    entries.Add(new VirtualEntry(entry.LocalPath, entry.IsDirectory, entry.Size, entry.Modified));
                    tops.Add(top);
                }

                if (entries.Count > MaxEntries)
                {
                    Notify(DropNotice.TooMany);
                    return null;
                }
            }
        }
        catch (RemoteFileException exception)
        {
            Notify(exception.Code == "too_many" ? DropNotice.TooMany : DropNotice.ListFailed, exception.Code == "too_many" ? null : exception.Message);
            return null;
        }

        if (entries.Exists(entry => entry.Path.Length > MaxPathLength))
        {
            Notify(DropNotice.TooLong);
            return null;
        }

        return entries.Count == 0 ? null : new Plan(entries, tops);
    }

    // Je ausgewähltem Eintrag ein Download, begonnen beim ersten Lesen einer seiner Dateien.
    private Download? Start(int top)
    {
        lock (_gate)
        {
            if (_downloads.TryGetValue(top, out var running))
            {
                return running;
            }

            if (_files() is not { } files)
            {
                return null;
            }

            // Jeder bekommt seinen eigenen Ordner: Zwei Einträge, die hier gleich heißen, kommen sich nicht in die Quere.
            var folder = _fetcher.NewFolder();
            var download = new Download(folder, files.Download(_items[top].RemotePath, folder), _cancellation.Token);
            _downloads[top] = download;
            Notify(DropNotice.Fetching);
            return download;
        }
    }

    private bool IsFinished(string transfer)
        => _queue.Items.FirstOrDefault(item => item.Id == transfer) is not { IsFinished: false };

    // Ein Transfer, den jemand aus der Liste genommen hat, gilt als abgebrochen.
    private bool WasCancelled(string transfer)
        => _queue.Items.FirstOrDefault(item => item.Id == transfer) is not { State: not TransferState.Cancelled };

    private void Notify(DropNotice notice, string? detail = null)
    {
        lock (_gate)
        {
            if (!_told.Add(notice))
            {
                return;
            }
        }

        _notify?.Invoke(notice, detail);
    }

    /// <param name="Tops">Zu jedem Eintrag: der wievielte ausgewählte Eintrag ihn mitbringt.</param>
    private sealed record Plan(List<VirtualEntry> Entries, List<int> Tops);

    /// <param name="Cancellation">Endet mit dem Durchgang, zu dem der Download gehört.</param>
    private sealed record Download(string Folder, string Transfer, CancellationToken Cancellation);

    /// <summary>
    /// Der Inhalt einer Datei, die gerade geladen wird: Lesen liefert, was schon da ist, und wartet auf den Rest.
    /// Kommt die Datei nicht vollständig an, scheitert das Lesen – ein halber Inhalt gilt nie als ganzer: mit
    /// <see cref="OperationCanceledException"/>, wenn der Transfer abgebrochen wurde, sonst mit <see cref="IOException"/>.
    /// </summary>
    private sealed class Arriving(string local, Func<bool> finished, Func<bool> cancelled, CancellationToken cancellation) : Stream
    {
        private FileStream? _file;
        private bool _complete;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                if (_file is null && !TryOpen())
                {
                    // Noch gar nichts da: Entweder kommt noch etwas, oder der Transfer ist ohne die Datei zu Ende.
                    if (finished() && !TryOpen())
                    {
                        throw Missing();
                    }
                }

                var read = _file?.Read(buffer) ?? 0;
                if (read > 0)
                {
                    _position += read;
                    return read;
                }

                if (_complete)
                {
                    return 0;
                }

                // Am vorläufigen Ende. Liegt die Datei inzwischen unter ihrem Namen, ist sie vollständig – was seit
                // dem letzten Lesen noch dazukam, liefert der nächste Durchgang. Ist der Transfer zu Ende und sie
                // liegt dort nicht, ist er gescheitert oder abgebrochen.
                if (File.Exists(local))
                {
                    _complete = true;
                    continue;
                }

                if (finished())
                {
                    if (!File.Exists(local))
                    {
                        throw Missing();
                    }

                    continue;
                }

                if (cancellation.WaitHandle.WaitOne(Poll))
                {
                    throw new OperationCanceledException();
                }
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file?.Dispose();
                _file = null;
            }

            base.Dispose(disposing);
        }

        private Exception Missing()
            => cancelled() ? new OperationCanceledException() : new IOException($"{Path.GetFileName(local)} did not arrive completely.");

        // Die fertige Datei, sonst die Teil-Datei. Wer sie offen hat, steht dem Transfer nicht im Weg: Er darf
        // weiterschreiben, umbenennen und – wenn er scheitert – löschen.
        private bool TryOpen()
        {
            foreach (var (path, complete) in new[] { (local, true), (local + PartialSuffix, false) })
            {
                try
                {
                    _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 18, FileOptions.SequentialScan);
                    _complete = complete;
                    return true;
                }
                catch (IOException)
                {
                    // Noch nicht da (oder gerade umbenannt): gleich noch einmal.
                }
            }

            return false;
        }
    }
}
