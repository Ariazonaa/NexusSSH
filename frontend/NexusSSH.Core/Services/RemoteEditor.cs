using System.Diagnostics;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Was <see cref="RemoteEditor"/> vom Rechner braucht: einen Editor starten und eine Datei beobachten.</summary>
public interface IEditorHost
{
    /// <summary>Öffnet die lokale Datei im Editor. <c>false</c>, wenn er sich nicht starten ließ.</summary>
    bool Launch(string localPath);

    /// <summary>Meldet jedes Speichern der Datei, bis das Ergebnis fallen gelassen wird. Beliebiger Thread.</summary>
    IDisposable Watch(string localPath, Action saved);

    /// <summary>Öffnet zwei lokale Dateien im eingestellten Vergleichsprogramm.</summary>
    CompareOutcome Compare(string left, string right) => CompareOutcome.NoProgram;
}

public enum CompareOutcome
{
    Started,

    /// <summary>Es ist kein Vergleichsprogramm eingestellt.</summary>
    NoProgram,

    /// <summary>Eingestellt ist kein Programm, sondern ein Skript (<c>.cmd</c>, <c>.bat</c>) – das wird nicht gestartet.</summary>
    NotAProgram,

    /// <summary>Das Programm ließ sich nicht starten.</summary>
    Failed,
}

public enum EditOutcome
{
    /// <summary>Die Datei wird geholt und dann geöffnet.</summary>
    Started,

    /// <summary>Sie ist schon offen; der Editor wurde noch einmal gestartet.</summary>
    AlreadyOpen,

    /// <summary>Zu groß, um sie zum Bearbeiten zu holen.</summary>
    TooLarge,

    /// <summary>Aus dem Namen lässt sich auf diesem Rechner kein Dateiname machen.</summary>
    BadName,
}

/// <summary>
/// Dateien des Servers im lokalen Editor bearbeiten (wie bei MobaXterm): Die Datei wird in einen eigenen
/// Arbeitsordner geholt, im Editor geöffnet und nach jedem Speichern wieder hochgeladen.
/// <para>
/// Geöffnet wird immer der eingestellte Editor mit der Datei als Argument – nie die Datei selbst. Was der Server
/// liefert, wird so nie ausgeführt, auch wenn es <c>.bat</c> oder <c>.lnk</c> heißt.
/// </para>
/// </summary>
public sealed class RemoteEditor : IDisposable
{
    /// <summary>Größere Dateien werden nicht zum Bearbeiten geholt; dafür gibt es den Download.</summary>
    public const long MaxSize = 32L * 1024 * 1024;

    private const string FolderPrefix = "edit-";

    private readonly Lock _gate = new();
    private readonly List<Edit> _edits = [];
    private readonly ITransferQueue _queue;
    private readonly IEditorHost _host;
    private readonly string _workDirectory;

    public RemoteEditor(ITransferQueue queue, IEditorHost host, string workDirectory)
    {
        _queue = queue;
        _host = host;
        _workDirectory = workDirectory;
        queue.Changed += OnTransfersChanged;
    }

    /// <summary>Die Datei ist geholt und im Editor offen: Name und Server.</summary>
    public event Action<string, string>? Opened;

    /// <summary>Die Datei wurde gespeichert und geht zurück zum Server: Name und Server.</summary>
    public event Action<string, string>? Uploading;

    /// <summary>Etwas ging schief: Name, Server und was (englisch).</summary>
    public event Action<string, string, string>? Failed;

    /// <summary>
    /// Die Datei hat sich am Server geändert, seit sie geholt wurde, und der Benutzer wollte sie nicht
    /// überschreiben: Name und Server. Die lokale Kopie bleibt, wie sie gespeichert wurde.
    /// </summary>
    public event Action<string, string>? Kept;

    /// <summary>
    /// Fragt, ob eine Datei überschrieben werden soll, die sich am Server inzwischen geändert hat (Name, Server).
    /// Ohne diese Frage wird immer hochgeladen. „Nicht gefragt“ heißt hier wie überall nicht „zugestimmt“: Wer
    /// fragt und keine Antwort bekommt, antwortet mit <c>false</c>.
    /// </summary>
    public Func<string, string, Task<bool>>? OverwriteChanged { get; set; }

    /// <summary>
    /// Der Arbeitsordner dieses Prozesses unter <paramref name="root"/>. Jeder Prozess hat seinen eigenen, damit
    /// zwei Fenster der App sich beim Aufräumen nicht die Dateien nehmen.
    /// </summary>
    public static string WorkDirectory(string root) => WorkFolders.Of(root, FolderPrefix);

    /// <summary>
    /// Entfernt Arbeitsordner von Prozessen, die es nicht mehr gibt – nach einem Absturz blieben die geholten
    /// Dateien sonst liegen. <paramref name="isRunning"/> sagt, ob ein Prozess mit dieser Nummer läuft.
    /// </summary>
    public static void RemoveStale(string root, Func<int, bool> isRunning) => WorkFolders.RemoveStale(root, FolderPrefix, isRunning);

    /// <summary>Holt die Datei und öffnet sie im Editor.</summary>
    /// <param name="modified">
    /// Wann die Datei am Server zuletzt geändert wurde, wie die Liste es nennt – daran (und an der Größe) ist vor
    /// dem Hochladen zu sehen, ob sie inzwischen jemand anders geändert hat. Ohne Angabe wird nicht verglichen.
    /// </param>
    public EditOutcome Open(IRemoteFileSystem files, string server, string remotePath, long size, DateTimeOffset? modified = null)
    {
        if (size > MaxSize)
        {
            return EditOutcome.TooLarge;
        }

        Edit? existing;
        lock (_gate)
        {
            existing = _edits.FirstOrDefault(edit => edit.Server == server && edit.RemotePath == remotePath);
        }

        if (existing is not null)
        {
            // Schon geholt: den Editor noch einmal nach vorn holen. Noch unterwegs: einfach abwarten.
            if (existing.Watch is not null)
            {
                _host.Launch(existing.LocalPath);
            }

            return EditOutcome.AlreadyOpen;
        }

        // Jede Datei bekommt ihren eigenen Ordner: Zwei Dateien gleichen Namens aus verschiedenen Verzeichnissen
        // kommen sich so nicht in die Quere.
        var folder = Path.Combine(_workDirectory, Guid.NewGuid().ToString("N"));

        // Der Name kommt vom Server. Wie die Datei hier heißt, legt das Backend fest – ein Name mit Doppelpunkt
        // etwa bekommt unter Windows einen anderen. Sie muss in ihrem Ordner liegen, nirgends sonst.
        if (files.LocalTarget(remotePath, folder) is not { } local || Path.GetDirectoryName(local) != folder)
        {
            return EditOutcome.BadName;
        }

        Directory.CreateDirectory(folder);
        var edit = new Edit(files, server, remotePath, local) { Stamp = modified is { } time ? (size, time) : null };

        // Vor dem Start eintragen: Das Ende des Downloads kann schneller gemeldet werden, als Download() zurückkehrt.
        lock (_gate)
        {
            _edits.Add(edit);
        }

        // Mit der Grenze, die eben an der Liste geprüft wurde: Die nennt nur, was der Server behauptet.
        edit.Download = files.Download(remotePath, folder, MaxSize);
        OnTransfersChanged();
        return EditOutcome.Started;
    }

    public void Dispose()
    {
        _queue.Changed -= OnTransfersChanged;
        lock (_gate)
        {
            foreach (var edit in _edits)
            {
                edit.Watch?.Dispose();
            }

            _edits.Clear();
        }

        // Die geholten Kopien sollen nicht liegen bleiben; ein gesperrter Rest stört nicht.
        try
        {
            if (Directory.Exists(_workDirectory))
            {
                Directory.Delete(_workDirectory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Beliebiger Thread: ein fertiger Download wird geöffnet, ein gescheiterter vergessen.
    private void OnTransfersChanged()
    {
        var items = _queue.Items;
        List<Edit> ready = [];
        List<(Edit Edit, string Detail)> failed = [];
        lock (_gate)
        {
            // Ein Upload ist durch: Wie die Datei am Server jetzt aussieht, ist der neue Stand zum Vergleichen.
            foreach (var edit in _edits.Where(edit => edit.Upload is not null))
            {
                if (items.FirstOrDefault(item => item.Id == edit.Upload) is { State: TransferState.Completed or TransferState.Failed or TransferState.Cancelled })
                {
                    edit.Upload = null;
                    _ = RefreshAsync(edit);
                }
            }

            foreach (var edit in _edits.Where(edit => edit is { Download: not null, Watch: null, Launching: false }).ToList())
            {
                switch (items.FirstOrDefault(item => item.Id == edit.Download))
                {
                    case { State: TransferState.Completed }:
                        edit.Launching = true;
                        ready.Add(edit);
                        break;

                    case { State: TransferState.Failed or TransferState.Cancelled } item:
                        _edits.Remove(edit);
                        failed.Add((edit, item.Detail is { Length: > 0 } detail ? FailureText.OfTransfer(detail) : item.State.ToString()));
                        break;
                }
            }
        }

        foreach (var (edit, detail) in failed)
        {
            Failed?.Invoke(edit.Name, edit.Server, detail);
        }

        foreach (var edit in ready)
        {
            if (!_host.Launch(edit.LocalPath))
            {
                lock (_gate)
                {
                    _edits.Remove(edit);
                }

                Failed?.Invoke(edit.Name, edit.Server, "the editor could not be started");
                continue;
            }

            var watch = _host.Watch(edit.LocalPath, () => _ = UploadAsync(edit));
            lock (_gate)
            {
                edit.Watch = watch;
            }

            Opened?.Invoke(edit.Name, edit.Server);
        }
    }

    private async Task UploadAsync(Edit edit)
    {
        // Hat jemand die Datei am Server geändert, seit sie geholt (oder zuletzt hochgeladen) wurde? Dann entscheidet
        // der Benutzer. Verglichen wird nur, wenn der Stand bekannt ist: Während ein eigener Upload läuft, ist er es
        // nicht – der ändert die Datei ja gerade selbst.
        (long Size, DateTimeOffset Modified)? known;
        lock (_gate)
        {
            if (edit.Asking)
            {
                return;
            }

            known = edit.Stamp;
            edit.Asking = OverwriteChanged is not null && known is not null;
        }

        if (OverwriteChanged is { } ask && known is { } stamp)
        {
            try
            {
                if (await StampAsync(edit).ConfigureAwait(false) != stamp && !await ask(edit.Name, edit.Server).ConfigureAwait(false))
                {
                    Kept?.Invoke(edit.Name, edit.Server);
                    return;
                }
            }
            catch (RemoteFileException)
            {
                // Nachsehen ging nicht (Verbindung weg, Verzeichnis nicht lesbar): Dann scheitert auch der Upload
                // und sagt, warum.
            }
            finally
            {
                lock (_gate)
                {
                    edit.Asking = false;
                }
            }
        }

        Uploading?.Invoke(edit.Name, edit.Server);
        lock (_gate)
        {
            edit.Stamp = null;
        }

        // Zurück unter dem Namen, den die Datei auf dem Server hat – hier kann sie anders heißen.
        var upload = edit.Files.Upload(edit.LocalPath, RemotePath.Parent(edit.RemotePath) ?? RemotePath.Root, RemotePath.Name(edit.RemotePath));
        lock (_gate)
        {
            edit.Upload = upload;
        }

        OnTransfersChanged();
    }

    // Größe und Änderungszeit der Datei am Server, wie die Liste ihres Verzeichnisses sie nennt; `null`, wenn es
    // sie dort nicht mehr gibt.
    private static async Task<(long Size, DateTimeOffset Modified)?> StampAsync(Edit edit)
    {
        var name = RemotePath.Name(edit.RemotePath);
        var entries = await edit.Files.ListAsync(RemotePath.Parent(edit.RemotePath) ?? RemotePath.Root).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => entry.Name == name) is { } entry ? (entry.Size, entry.Modified) : null;
    }

    private async Task RefreshAsync(Edit edit)
    {
        try
        {
            var stamp = await StampAsync(edit).ConfigureAwait(false);
            lock (_gate)
            {
                // Nur wenn inzwischen kein weiterer Upload läuft – sonst wäre der Stand schon wieder überholt.
                if (edit.Upload is null)
                {
                    edit.Stamp = stamp;
                }
            }
        }
        catch (RemoteFileException)
        {
            // Ohne Stand wird beim nächsten Speichern nicht verglichen – wie vor dieser Prüfung.
        }
    }

    private sealed class Edit(IRemoteFileSystem files, string server, string remotePath, string localPath)
    {
        public IRemoteFileSystem Files { get; } = files;

        public string Server { get; } = server;

        public string RemotePath { get; } = remotePath;

        public string LocalPath { get; } = localPath;

        /// <summary>Der Name auf dem Server, wie er angezeigt wird.</summary>
        public string Name => Format.Visible(Core.RemotePath.Name(RemotePath));

        /// <summary>Kennung des Downloads, der die Datei holt.</summary>
        public string? Download { get; set; }

        public bool Launching { get; set; }

        /// <summary>Kennung des Uploads, der gerade läuft.</summary>
        public string? Upload { get; set; }

        /// <summary>Größe und Änderungszeit am Server, wie zuletzt gesehen; <c>null</c>: unbekannt, es wird nicht verglichen.</summary>
        public (long Size, DateTimeOffset Modified)? Stamp { get; set; }

        /// <summary>Der Benutzer wird gerade gefragt; ein weiteres Speichern wartet auf seine Antwort.</summary>
        public bool Asking { get; set; }

        /// <summary>Gesetzt, sobald die Datei im Editor offen ist.</summary>
        public IDisposable? Watch { get; set; }
    }
}

/// <summary>
/// Der echte Rechner: startet den eingestellten Editor als Prozess und beobachtet die Datei mit dem Dateisystem.
/// </summary>
public sealed class ProcessEditorHost(Func<string> editorPath, Func<string>? comparePath = null) : IEditorHost
{
    /// <summary>
    /// Die Argumente für das Vergleichsprogramm: die beiden Dateien – bei VS Code mit <c>--diff</c> davor, sonst
    /// öffnete es sie nur nebeneinander. WinMerge, Beyond Compare, Meld und KDiff3 nehmen zwei Pfade.
    /// </summary>
    public static IReadOnlyList<string> CompareArguments(string program, string left, string right)
        => Path.GetFileNameWithoutExtension(program).ToLowerInvariant() is "code" or "code - insiders" or "codium" or "vscodium"
            ? ["--diff", left, right]
            : [left, right];

    /// <summary>
    /// Ein Skript für die Eingabeaufforderung. Das startet Windows über <c>cmd.exe</c>, und dort sind Zeichen wie
    /// <c>&amp;</c> und <c>%</c> in einem Dateinamen Befehle – die Namen kommen aber vom Server.
    /// </summary>
    public static bool IsScript(string program)
        => Path.GetExtension(program).ToLowerInvariant() is ".cmd" or ".bat" or ".ps1" or ".vbs" or ".js" or ".lnk";

    public CompareOutcome Compare(string left, string right)
    {
        var program = comparePath?.Invoke();
        if (string.IsNullOrWhiteSpace(program))
        {
            return CompareOutcome.NoProgram;
        }

        if (IsScript(program))
        {
            return CompareOutcome.NotAProgram;
        }

        try
        {
            // Wie beim Editor: Gestartet wird das Programm, die Dateien sind nur Argumente.
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in CompareArguments(program, left, right))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            return process is not null ? CompareOutcome.Started : CompareOutcome.Failed;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return CompareOutcome.Failed;
        }
    }

    /// <summary>Editor, wenn nichts eingestellt ist. Unter Windows überall vorhanden.</summary>
    public const string DefaultEditor = "notepad.exe";

    /// <summary>
    /// Ob sich ein Pfad einem Skript (<c>code.cmd</c>) als Argument geben lässt: nur Buchstaben, Ziffern, Leerzeichen
    /// und <c>. _ - \ / :</c>. Ein Skript startet Windows über <c>cmd.exe</c>, und dort sind <c>&amp;</c>,
    /// <c>%</c>, <c>^</c> oder Klammern im Namen Befehle – der Name aber kommt vom Server.
    /// </summary>
    public static bool IsPlainPath(string path)
        => path.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-' or '\\' or '/' or ':');

    public bool Launch(string localPath)
    {
        var editor = string.IsNullOrWhiteSpace(editorPath()) ? DefaultEditor : editorPath();

        // Ein Skript als Editor nur mit einem Namen ohne Sonderzeichen; und nur eine Datei – hat der Server statt
        // ihrer ein Verzeichnis geliefert, bekommt der Editor es nicht als Ordner zu öffnen.
        if ((IsScript(editor) && !IsPlainPath(localPath)) || !File.Exists(localPath))
        {
            return false;
        }

        try
        {
            // UseShellExecute bleibt aus und die Datei ist nur ein Argument: Gestartet wird der Editor, nie das,
            // was der Server geliefert hat.
            var start = new ProcessStartInfo(editor) { UseShellExecute = false };
            start.ArgumentList.Add(localPath);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public IDisposable Watch(string localPath, Action saved) => new SaveWatcher(localPath, saved);

    /// <summary>
    /// Meldet ein Speichern erst, wenn die Datei kurz zur Ruhe gekommen ist: Editoren schreiben in mehreren
    /// Schritten (leeren, schreiben, umbenennen), und jeder davon löst ein Ereignis aus.
    /// </summary>
    private sealed class SaveWatcher : IDisposable
    {
        private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;

        public SaveWatcher(string path, Action saved)
        {
            _timer = new Timer(_ => saved());
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => Touch();
            _watcher.Created += (_, _) => Touch();
            _watcher.Renamed += (_, _) => Touch();
            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _timer.Dispose();
        }

        private void Touch()
        {
            try
            {
                _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Das Ereignis kam, während aufgeräumt wurde.
            }
        }
    }
}
