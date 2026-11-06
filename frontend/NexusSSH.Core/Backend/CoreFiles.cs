using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Backend;

/// <summary>Dateien einer Sitzung über den SFTP-Kanal des Kerns.</summary>
internal sealed class CoreRemoteFileSystem(CoreBackend backend, ulong session, ServerProfile profile, string home) : IRemoteFileSystem
{
    public string InitialPath { get; } = RemotePath.Normalize(home);

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await backend.SftpAsync(new RequestDto("sftp.list") { Session = session, Path = path }, cancellationToken);
        return [.. (result.Entries ?? []).Select(entry => entry.ToEntry())];
    }

    public Task CreateDirectoryAsync(string path)
        => backend.SftpAsync(new RequestDto("sftp.mkdir") { Session = session, Path = path });

    public Task CreateFileAsync(string path)
        => backend.SftpAsync(new RequestDto("sftp.touch") { Session = session, Path = path });

    public Task CreateLinkAsync(string path, string target)
        => backend.SftpAsync(new RequestDto("sftp.symlink") { Session = session, Path = path, To = target });

    public async Task<string?> ReadLinkAsync(string path, CancellationToken cancellationToken = default)
        => (await backend.SftpAsync(new RequestDto("sftp.readlink") { Session = session, Path = path }, cancellationToken)).Target;

    public Task RenameAsync(string from, string to)
        => backend.SftpAsync(new RequestDto("sftp.rename") { Session = session, From = from, To = to });

    public Task DeleteAsync(string path)
        => backend.SftpAsync(new RequestDto("sftp.delete") { Session = session, Path = path });

    public Task SetPermissionsAsync(string path, int mode, bool recursive = false)
        => backend.SftpAsync(new RequestDto("sftp.chmod") { Session = session, Path = path, Mode = mode & Format.PermissionBits, Recursive = recursive ? true : null });

    public Task SetOwnerAsync(string path, string? owner, string? group, bool recursive = false)
        => backend.SftpAsync(new RequestDto("sftp.chown") { Session = session, Path = path, Owner = owner, Group = group, Recursive = recursive ? true : null });

    // Ein neu geöffneter Kanal ist immer der des Benutzers – auch wenn das Öffnen scheitert, gilt root nicht mehr.
    public async Task ReopenAsync()
    {
        IsElevated = false;
        await backend.SftpAsync(new RequestDto("sftp.reopen") { Session = session });
    }

    public async Task ElevateAsync(string? password = null)
    {
        await backend.SftpAsync(new RequestDto("sftp.elevate") { Session = session, Password = password });
        IsElevated = true;
    }

    public bool IsElevated { get; private set; }

    // Die Namen auf diesem Rechner bildet der Kern – mit demselben Code wie der Download selbst.
    public async Task<IReadOnlyList<RemoteTreeEntry>> TreeAsync(string path, IReadOnlyList<string>? exclude = null, bool excluded = false)
    {
        var result = await backend.SftpAsync(new RequestDto("sftp.tree") { Session = session, Path = path, Exclude = exclude is { Count: > 0 } ? exclude : null, Excluded = excluded ? true : null });
        return [.. (result.Tree ?? []).Select(entry => entry.ToEntry())];
    }

    public async Task<FoundEntries> FindAsync(string path, string name)
    {
        var result = await backend.SftpAsync(new RequestDto("sftp.find") { Session = session, Path = path, Name = name });
        return new FoundEntries([.. (result.Tree ?? []).Select(entry => entry.ToEntry())], result.Seen is > 0 ? result.Seen : null);
    }

    public Task PackAsync(string directory, IReadOnlyList<string> names, string archive)
        => backend.SftpAsync(new RequestDto("sftp.pack") { Session = session, Directory = directory, Names = [.. names], Archive = archive });

    public Task UnpackAsync(string directory, string archive)
        => backend.SftpAsync(new RequestDto("sftp.unpack") { Session = session, Directory = directory, Archive = archive });

    public async Task<long?> FolderSizeAsync(string path)
        => (await backend.SftpAsync(new RequestDto("sftp.size") { Session = session, Path = path })).Size;

    public async Task<IReadOnlyList<FolderUsage>> UsageAsync(string path)
    {
        var result = await backend.SftpAsync(new RequestDto("sftp.usage") { Session = session, Path = path });
        return [.. (result.Usage ?? []).Where(entry => entry.Path is { Length: > 0 }).Select(entry => new FolderUsage(entry.Path!, entry.Bytes))];
    }

    // Der Kern nimmt nur eine begrenzte Zahl je Anfrage – die Befehlszeile soll nicht zu lang werden.
    public async Task<IReadOnlyList<string?>> ChecksumsAsync(IReadOnlyList<string> paths)
    {
        var sums = new List<string?>(paths.Count);
        foreach (var chunk in paths.Chunk(ChecksumsAtOnce))
        {
            var result = await backend.SftpAsync(new RequestDto("sftp.checksum") { Session = session, Paths = chunk });
            var answered = result.Sums ?? [];
            sums.AddRange(chunk.Select((_, index) => index < answered.Count ? answered[index] : null));
        }

        return sums;
    }

    public async Task CopyAsync(IReadOnlyList<(string From, string To)> pairs, bool move = false)
    {
        foreach (var chunk in pairs.Chunk(CopiesAtOnce))
        {
            await backend.SftpAsync(new RequestDto("sftp.copy")
            {
                Session = session,
                Pairs = [.. chunk.Select(pair => new PairDto(pair.From, pair.To))],
                Move = move ? true : null,
            });
        }
    }

    public IDisposable? Follow(string path, Action<string> text, Action<string?> ended) => backend.Follow(session, path, text, ended);

    public async Task<FilePart> ReadAsync(string path, long offset, int length)
    {
        var result = await backend.SftpAsync(new RequestDto("sftp.read") { Session = session, Path = path, Offset = offset, Length = Math.Clamp(length, 0, FilePart.MaxLength) });
        try
        {
            return new FilePart(Convert.FromBase64String(result.Data ?? string.Empty), result.Size ?? 0);
        }
        catch (FormatException e)
        {
            throw new RemoteFileException(e.Message, "failed");
        }
    }

    public Task WriteAsync(string path, ReadOnlyMemory<byte> content)
        => backend.SftpAsync(new RequestDto("sftp.write") { Session = session, Path = path, Data = Convert.ToBase64String(content.Span) });

    private const int ChecksumsAtOnce = 100;

    private const int CopiesAtOnce = 100;

    public string Upload(string localPath, string remoteDirectory, string? remoteName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null)
        => backend.TransferQueue.Start(new TransferRequest(
            session,
            profile.Name,
            TransferDirection.Upload,
            remoteName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(localPath)),
            localPath,
            remoteDirectory,
            remoteName,
            profile.Id,
            Keep: keep,
            Exclude: exclude is { Count: > 0 } ? exclude : null));

    public string Download(string remotePath, string localDirectory, long? limit = null, TransferKeep keep = TransferKeep.None, string? localName = null, IReadOnlyList<string>? exclude = null)
        => backend.TransferQueue.Start(new TransferRequest(
            session,
            profile.Name,
            TransferDirection.Download,
            localName ?? RemotePath.Name(remotePath),
            localDirectory,
            remotePath,
            localName,
            ProfileId: profile.Id,
            Limit: limit,
            Keep: keep,
            Exclude: exclude is { Count: > 0 } ? exclude : null));

    // Der Kern überträgt einen Ordner und lässt alles aus, was nicht genannt ist. Der Ordner des Servers muss dafür
    // einen Namen haben – die Wurzel hat keinen.
    public string? UploadSome(string localFolder, string remoteFolder, IReadOnlyList<string> files)
        => RemotePath.Parent(remoteFolder) is not { } parent
            ? null
            : backend.TransferQueue.Start(new TransferRequest(
                session,
                profile.Name,
                TransferDirection.Upload,
                RemotePath.Name(remoteFolder),
                localFolder,
                parent,
                RemotePath.Name(remoteFolder),
                profile.Id,
                Only: [.. files]));

    public string? DownloadSome(string remoteFolder, string localFolder, IReadOnlyList<string> files)
    {
        var folder = Path.TrimEndingDirectorySeparator(localFolder);
        return Path.GetDirectoryName(folder) is not { Length: > 0 } parent || RemotePath.Parent(remoteFolder) is null
            ? null
            : backend.TransferQueue.Start(new TransferRequest(
                session,
                profile.Name,
                TransferDirection.Download,
                RemotePath.Name(remoteFolder),
                parent,
                remoteFolder,
                Path.GetFileName(folder),
                profile.Id,
                Only: [.. files]));
    }

    // Von Server zu Server liest und schreibt der Kern in einem Zug – das geht nur zwischen zwei seiner Sitzungen.
    // In der Liste steht der Transfer beim Ziel, wie ein Upload dorthin.
    public string? SendTo(string remotePath, IRemoteFileSystem target, string targetDirectory, string? targetName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null)
        => target is not CoreRemoteFileSystem other || !ReferenceEquals(other.Backend, backend)
            ? null
            : backend.TransferQueue.Start(new TransferRequest(
                session,
                other.Profile.Name,
                TransferDirection.Upload,
                targetName ?? RemotePath.Name(remotePath),
                targetDirectory,
                remotePath,
                targetName,
                ProfileId: profile.Id,
                TargetSession: other.Session,
                TargetProfileId: other.Profile.Id,
                Keep: keep,
                Exclude: exclude is { Count: > 0 } ? exclude : null));

    private CoreBackend Backend => backend;

    private ulong Session => session;

    private ServerProfile Profile => profile;

    // Den lokalen Namen bildet allein der Kern – hier wird er nur gefragt.
    public string? LocalTarget(string remotePath, string localDirectory)
        => backend.Send(new RequestDto("transfer.target") { Local = localDirectory, Remote = remotePath }) is { Ok: true, Path: { Length: > 0 } path }
            ? path
            : null;
}

/// <summary>Was zu einem Transfer gehört – aufgehoben, damit er sich wiederholen lässt.</summary>
/// <param name="Local">Upload: Datei oder Ordner. Download: Zielordner.</param>
/// <param name="Remote">Upload: Zielverzeichnis. Download: Datei oder Ordner auf dem Server.</param>
/// <param name="RemoteName">Upload: Name im Zielverzeichnis, wenn er nicht der lokale sein soll.</param>
/// <param name="ProfileId">Das Profil des Servers – ein neuer Versuch sucht sich darüber eine Verbindung, die noch steht.</param>
/// <param name="Stamp">Upload einer Datei: wie sie aussah, als der Transfer begann.</param>
/// <param name="Resume">Ein neuer Versuch: so viele Bytes hatte der abgebrochene übertragen.</param>
internal sealed record TransferRequest(
    ulong Session,
    string Server,
    TransferDirection Direction,
    string Name,
    string Local,
    string Remote,
    string? RemoteName = null,
    string? ProfileId = null,
    LocalStamp? Stamp = null,
    long? Resume = null,
    IReadOnlyList<string>? Only = null,
    ulong? TargetSession = null,
    string? TargetProfileId = null,
    long? Limit = null,
    TransferKeep Keep = TransferKeep.None,
    IReadOnlyList<string>? Exclude = null)
{
    /// <summary>
    /// Von Server zu Server: <see cref="Remote"/> ist die Quelle auf dem Server der Sitzung, <see cref="Local"/>
    /// das Zielverzeichnis auf dem Server von <see cref="TargetSession"/>. Angezeigt wird er wie ein Upload zum Ziel.
    /// </summary>
    public bool IsRelay => TargetProfileId is not null;
}

/// <summary>
/// Größe und Änderungszeit einer lokalen Datei. Nur wenn beides beim neuen Versuch noch stimmt, darf ein Upload
/// fortsetzen, was der abgebrochene geschickt hat – sonst stünde auf dem Server der Anfang der alten Fassung vor
/// dem Rest der neuen.
/// </summary>
internal readonly record struct LocalStamp(long Length, DateTime Modified)
{
    /// <summary>Der Stand der Datei; <c>null</c> für einen Ordner und für alles, was sich nicht ansehen lässt.</summary>
    public static LocalStamp? Of(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new LocalStamp(file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Die Transfers des Kerns. Den Stand liefert er als <c>transfer.state</c>-Ereignisse.
/// <para>
/// Was sich noch einmal versuchen lässt, steht auch in einer Datei (<c>transfers.json</c>) und ist nach einem
/// Neustart wieder da – samt dem, was der Kern zum Fortsetzen braucht. Wie weit jede Datei kam, weiß der Kern
/// selbst (<c>trails.json</c>); er behält es nur für die Transfers, die die App ihm beim Start und nach dem Aufräumen
/// nennt (<c>transfer.retain</c>).
/// </para>
/// </summary>
internal sealed class CoreTransferQueue : ITransferQueue
{
    private readonly CoreBackend _backend;
    private readonly string? _file;
    private readonly Lock _gate = new();
    private readonly Lock _writing = new();
    private readonly List<TransferItem> _items = [];
    private readonly Dictionary<string, TransferRequest> _requests = [];
    private long _lastTransfer;
    private bool _suspended;

    // Was der Benutzer angehalten hat: Ihr Ende meldet der Kern wie einen Abriss – in der Liste steht „angehalten“.
    private readonly HashSet<string> _paused = [];

    /// <param name="file">Wohin die Liste gespeichert wird; ohne Datei lebt sie nur, solange die App läuft.</param>
    public CoreTransferQueue(CoreBackend backend, string? file = null)
    {
        _backend = backend;
        _file = file;
    }

    public event Action? Changed;

    public IReadOnlyList<TransferItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public void Cancel(string id)
    {
        if (ulong.TryParse(id, out var transfer))
        {
            // Die Antwort ist gleichgültig: Ist der Transfer schon zu Ende, gibt es nichts abzubrechen.
            _backend.Send(new RequestDto("transfer.cancel") { Transfer = transfer });
        }
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            foreach (var item in _items.Where(item => item.IsFinished))
            {
                _requests.Remove(item.Id);
            }

            _items.RemoveAll(item => item.IsFinished);
        }

        // Was nicht mehr in der Liste steht, setzt niemand fort – der Kern darf vergessen, wie weit es kam.
        Save();
        Retain();
        Changed?.Invoke();
    }

    public void PauseAll()
    {
        lock (_gate)
        {
            _paused.UnionWith(_items.Where(item => !item.IsFinished).Select(item => item.Id));
        }

        _backend.Send(new RequestDto("transfer.suspend"));
    }

    public void Pause(string id)
    {
        if (!ulong.TryParse(id, out var transfer))
        {
            return;
        }

        lock (_gate)
        {
            if (!_items.Any(item => item.Id == id && !item.IsFinished))
            {
                return;
            }

            _paused.Add(id);
        }

        // Kennt der Kern ihn nicht mehr, ist er eben zu Ende gegangen – dann gilt, was er selbst gemeldet hat.
        if (!_backend.Send(new RequestDto("transfer.pause") { Transfer = transfer }).Ok)
        {
            lock (_gate)
            {
                _paused.Remove(id);
            }
        }
    }

    public bool Move(string id, QueueMove move)
    {
        // Wer umzieht und vor wen: „eins nach hinten“ heißt, dass der Nächste vor diesen rückt.
        string moved, before;
        lock (_gate)
        {
            var waiting = _items.Where(item => item.State == TransferState.Queued).Select(item => item.Id).ToList();
            var at = waiting.IndexOf(id);
            if (at < 0 || (move == QueueMove.Down ? at == waiting.Count - 1 : at == 0))
            {
                return false;
            }

            (moved, before) = move switch
            {
                QueueMove.First => (id, waiting[0]),
                QueueMove.Up => (id, waiting[at - 1]),
                _ => (waiting[at + 1], id),
            };
        }

        // Die Reihenfolge gehört dem Kern: Erst wenn er umgestellt hat, zieht die Zeile in der Liste nach. Lehnt er
        // ab, ist einer der beiden inzwischen an der Reihe.
        if (!_backend.Send(new RequestDto("transfer.move") { Transfer = ulong.Parse(moved), Before = ulong.Parse(before) }).Ok)
        {
            return false;
        }

        lock (_gate)
        {
            var from = _items.FindIndex(item => item.Id == moved);
            if (from >= 0)
            {
                var row = _items[from];
                _items.RemoveAt(from);
                var to = _items.FindIndex(item => item.Id == before);
                _items.Insert(to < 0 ? _items.Count : to, row);
            }
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    public void SetLimit(long bytesPerSecond)
        => _backend.Send(new RequestDto("transfer.limit") { BytesPerSecond = Math.Max(0, bytesPerSecond) });

    public void Suspend()
    {
        _backend.Send(new RequestDto("transfer.suspend"));
        lock (_gate)
        {
            _suspended = true;
            for (var i = 0; i < _items.Count; i++)
            {
                if (!_items[i].IsFinished)
                {
                    _items[i] = Interrupted(_items[i]);
                }
            }
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Holt die Liste aus der Datei – beim Start, bevor jemand sie ansieht. Danach erfährt der Kern, welche
    /// Transfers die App noch zeigt; was er von anderen noch weiß, räumt er weg.
    /// </summary>
    internal void Restore()
    {
        if (_file is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var saved in SavedTransfers.Load(_file))
            {
                var id = ((ulong)Interlocked.Increment(ref _lastTransfer)).ToString();
                _items.Add(saved.ToItem(id));
                _requests[id] = saved.ToRequest();
            }
        }

        Retain();
    }

    /// <summary>
    /// Ein Download für „Bearbeiten“ oder zum Ziehen in ein anderes Programm: Sein Ziel liegt in einem Arbeitsordner,
    /// den der nächste Start wegräumt – nach einem Neustart gäbe es nichts fortzusetzen.
    /// </summary>
    private static bool IsScratch(TransferRequest request) => request.Direction == TransferDirection.Download && InWorkFolder(request);

    private static bool InWorkFolder(TransferRequest request)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppDataLocation.EditRoot)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(request.Local).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Ein Transfer, der noch nicht fertig war, als die App endete: gescheitert, mit eigenem Grund.</summary>
    private static TransferItem Interrupted(TransferItem item) => item with { State = TransferState.Failed, Detail = SavedTransfer.InterruptedDetail };

    /// <summary>Sagt dem Kern, welche Transfers die App noch zeigt und vielleicht noch einmal versucht.</summary>
    private void Retain()
    {
        if (_file is null)
        {
            return;
        }

        List<TransferJobDto> jobs;
        lock (_gate)
        {
            jobs = [.. _requests.Values.Select(request => new TransferJobDto(
                request.Direction == TransferDirection.Upload ? "upload" : "download",
                request.Local,
                request.Remote,
                request.RemoteName))];
        }

        _backend.Send(new RequestDto("transfer.retain") { Jobs = jobs });
    }

    /// <summary>
    /// Schreibt alles, was sich noch einmal versuchen lässt, in die Datei. Was gerade läuft oder wartet, steht dort
    /// als unterbrochen: Stürzt die App ab, lässt es sich nach dem nächsten Start wiederholen. Gespeichert wird bei
    /// jedem Wechsel des Zustands, nicht bei jedem Fortschritt.
    /// </summary>
    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        // Stand nehmen und schreiben in einem Zug – sonst überholt ein älterer Stand einen neueren.
        lock (_writing)
        {
            List<SavedTransfer> saving;
            lock (_gate)
            {
                saving =
                [
                    .. _items
                        .Where(item => item.State != TransferState.Completed && _requests.TryGetValue(item.Id, out var request) && !IsScratch(request))
                        .Select(item => SavedTransfer.From(item.IsFinished ? item : Interrupted(item), _requests[item.Id])),
                ];
            }

            SavedTransfers.Write(_file, saving);
        }
    }

    // Hochladen: lokale Quelle, Ordner am Server. Herunterladen und von Server zu Server: Quelle am Server, Zielordner.
    public TransferPaths? PathsOf(string id)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(id, out var request)
                ? request is { Direction: TransferDirection.Upload, IsRelay: false }
                    ? new TransferPaths(request.Local, request.Remote)
                    : new TransferPaths(request.Remote, request.Local)
                : null;
        }
    }

    public bool Retry(string id)
    {
        TransferRequest? request;
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0 || _items[index].State is not (TransferState.Failed or TransferState.Cancelled) || !_requests.Remove(id, out request))
            {
                return false;
            }

            request = Again(request, _items[index]);

            // Der neue Versuch ersetzt die Zeile des alten.
            _items.RemoveAt(index);
        }

        // Die Verbindung von damals gibt es oft nicht mehr – sie ist abgerissen (deshalb der neue Versuch) oder ihr
        // Tab ist zu. Dann läuft der Transfer über eine andere, die zum selben Server steht. Gibt es keine, sagt das
        // die Zeile, statt den Kern nach einer Sitzung zu fragen, die er nicht mehr kennt.
        if (_backend.SessionFor(request.ProfileId, request.Session) is not { } session)
        {
            lock (_gate)
            {
                var transfer = (ulong)Interlocked.Increment(ref _lastTransfer);
                _items.Add(new TransferItem(transfer.ToString(), request.Name, request.Server, request.Direction, TransferState.Failed, 0, Detail: "not_connected: "));
                _requests[transfer.ToString()] = request;
            }

            Save();
            Changed?.Invoke();
            return true;
        }

        // Von Server zu Server braucht es auch zum Ziel eine Verbindung, die steht.
        if (request.IsRelay)
        {
            if (_backend.SessionFor(request.TargetProfileId, request.TargetSession ?? 0) is not { } target)
            {
                lock (_gate)
                {
                    var transfer = (ulong)Interlocked.Increment(ref _lastTransfer);
                    _items.Add(new TransferItem(transfer.ToString(), request.Name, request.Server, request.Direction, TransferState.Failed, 0, Detail: "not_connected: "));
                    _requests[transfer.ToString()] = request;
                }

                Save();
                Changed?.Invoke();
                return true;
            }

            request = request with { TargetSession = target };
        }

        Start(request with { Session = session });
        return true;
    }

    /// <summary>
    /// Was der neue Versuch dem Kern sagt: wie viel der abgebrochene übertragen hat. Der Kern baut dann auf dem auf,
    /// was schon am Ziel liegt, statt von vorn zu beginnen – ob das dort zur Quelle passt, prüft er selbst. Beim
    /// Upload einer einzelnen Datei weiß nur die App, ob die Datei hier noch dieselbe ist; bei einem Ordner und
    /// beim Download merkt sich der Kern den Stand jeder Datei selbst. Von Server zu Server vergleicht er, was auf
    /// den beiden Servern liegt.
    /// </summary>
    private static TransferRequest Again(TransferRequest request, TransferItem stopped)
    {
        // Der Upload einer Datei, die es beim ersten Versuch gab: Hat sie sich inzwischen geändert, beginnt er von
        // vorn, und gemerkt wird der neue Stand.
        var singleUpload = request is { Direction: TransferDirection.Upload, Stamp: not null };
        if (singleUpload && LocalStamp.Of(request.Local) != request.Stamp)
        {
            return request with { Stamp = null, Resume = null };
        }

        if (stopped.Bytes > 0)
        {
            return request with { Resume = stopped.Bytes };
        }

        // Ist die App mitten im Transfer abgestürzt, weiß sie nicht, wie weit er kam – der Kern schon (seine Spur in
        // trails.json, unterwegs mitgeschrieben). Die Zahl sagt ihm nur „baue darauf auf“; beim Upload einer Datei
        // nimmt er die Stelle aus seiner Spur, und fehlt die, prüft er mit 1 genau das erste Byte.
        if (stopped.Detail == SavedTransfer.InterruptedDetail)
        {
            return request with { Resume = request.Resume ?? 1 };
        }

        // Hat der letzte Versuch gar nichts übertragen (es gab keine Verbindung), gilt weiter, was der davor nannte.
        return request;
    }

    /// <summary>Stellt einen Transfer in die Warteschlange des Kerns und liefert seine Kennung.</summary>
    internal string Start(TransferRequest request)
    {
        if (request is { Direction: TransferDirection.Upload, Stamp: null, IsRelay: false })
        {
            request = request with { Stamp = LocalStamp.Of(request.Local) };
        }

        var transfer = (ulong)Interlocked.Increment(ref _lastTransfer);
        var item = new TransferItem(transfer.ToString(), request.Name, request.Server, request.Direction, TransferState.Queued, 0);

        // Vor der Anfrage eintragen: Das erste Ereignis kann schneller sein als die Antwort.
        lock (_gate)
        {
            _items.Add(item);
            _requests[item.Id] = request;
        }

        var response = _backend.Send(new RequestDto("transfer.start")
        {
            Session = request.Session,
            Transfer = transfer,
            Direction = request.IsRelay ? "relay" : request.Direction == TransferDirection.Upload ? "upload" : "download",
            TargetSession = request.TargetSession,
            Local = request.Local,
            Remote = request.Remote,
            Name = request.RemoteName,
            Only = request.Only,
            Exclude = request.Exclude,
            Resume = request.Resume,
            Limit = request.Limit,
            Keep = request.Keep switch
            {
                TransferKeep.Same => "same",
                TransferKeep.Newer => "newer",
                _ => null,
            },
        });
        if (!response.Ok)
        {
            Replace(item.Id, current => current with { State = TransferState.Failed, Detail = $"{response.Error}: {response.Message}" });
        }

        Save();
        Changed?.Invoke();
        return item.Id;
    }

    internal void OnEvent(EventDto message)
    {
        if (message.Transfer is not { } transfer)
        {
            return;
        }

        var (bytes, total) = (message.Bytes ?? 0, message.Total ?? 0);
        var state = message.State switch
        {
            "queued" => TransferState.Queued,
            "running" => TransferState.Running,
            "completed" => TransferState.Completed,
            "cancelled" => TransferState.Cancelled,
            _ => TransferState.Failed,
        };
        var (files, filesDone) = (message.Files ?? 0, message.FilesDone ?? 0);
        var changed = Replace(transfer.ToString(), item => (item with
        {
            Bytes = bytes,
            Total = total,
            Files = files,
            FilesDone = filesDone,
            Progress = TransferItem.ProgressOf(bytes, total, filesDone, files, state == TransferState.Completed),
            ResumedAt = message.Resumed ?? 0,
        }) switch
        {
            // Die App endet: Was jetzt noch kommt, nennt höchstens, wie weit der Transfer kam. Er bleibt
            // unterbrochen – es sei denn, er ist gerade noch fertig geworden.
            var counted when _suspended && state != TransferState.Completed => counted,

            // Vom Benutzer angehalten: Der Kern meldet das Ende wie einen Abriss der Verbindung.
            var counted when state == TransferState.Failed && _paused.Remove(counted.Id) => counted with { State = state, Detail = ITransferQueue.PausedDetail },
            var counted => counted with { State = state, Detail = message.Message },
        });

        // Gespeichert wird, wenn sich der Zustand ändert, nicht bei jedem Fortschritt – und am Ende eines Transfers,
        // auch eines, den die App beim Beenden schon als unterbrochen eingetragen hat: Erst jetzt steht fest, wie
        // weit er kam.
        if (changed || state is not (TransferState.Queued or TransferState.Running))
        {
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Ändert eine Zeile; <c>true</c>, wenn sich dabei ihr Zustand geändert hat.</summary>
    private bool Replace(string id, Func<TransferItem, TransferItem> change)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            var before = _items[index].State;
            _items[index] = change(_items[index]);
            return _items[index].State != before;
        }
    }
}
