namespace NexusSSH.Core.Services;

/// <summary>
/// Findet heraus, in welchen Ordner der Explorer etwas abgelegt hat – ohne Erweiterung der Shell, wie WinSCP: Dem
/// Explorer wird statt der Einträge des Servers ein leerer Merker-Ordner mit eindeutigem Namen angeboten
/// (<see cref="Marker"/>). Er kopiert ihn sofort, ohne eigenen Dialog; wo die Kopie entsteht, melden die Wächter der
/// Laufwerke. Dort lädt die App die Einträge dann selbst herunter – mit ihrer Warteschlange, ohne die Grenzen einer
/// Liste virtueller Dateien (Länge der Pfade, Anzahl) und ohne den Kopierdialog des Explorers, der wartet, bis
/// geladen ist.
/// <para>
/// Die Wächter laufen ab dem Anfang des Ziehens: Der Explorer kopiert, sobald losgelassen ist, oft schon, bevor das
/// Ziehen zurückkehrt. Ein Wächter, der nicht anspringt (Laufwerk nicht bereit, keine Rechte), fehlt eben.
/// </para>
/// </summary>
public sealed class DropLanding : IDisposable
{
    /// <summary>So beginnt der Name jedes Merkers – bleibt einer liegen, sieht man, woher er kommt.</summary>
    public const string MarkerPrefix = "NexusSSH-";

    private readonly string _home;
    private readonly string _name;
    private readonly TaskCompletionSource<string> _landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _disposed;

    /// <param name="home">Ein leerer Ordner, der dieser Ablage gehört; darin entsteht der Merker. Er verschwindet mit <see cref="Dispose"/>.</param>
    /// <param name="roots">
    /// Wo der Merker landen kann; ohne Angabe die Wurzeln der Laufwerke (<see cref="Drives"/>). Gefragt erst im
    /// Hintergrund – ein Netzlaufwerk, das nicht antwortet, hält so niemanden auf.
    /// </param>
    public DropLanding(string home, Func<IEnumerable<string>>? roots = null)
    {
        _home = home;
        _name = MarkerPrefix + Guid.NewGuid().ToString("N")[..12];
        Marker = Path.Combine(home, _name);
        Directory.CreateDirectory(Marker);
        Ready = Task.Run(() => Watch([.. (roots ?? Drives)()]));
    }

    /// <summary>Der Merker: ein leerer Ordner, den der Explorer statt der Einträge kopiert.</summary>
    public string Marker { get; }

    /// <summary>Erfüllt, sobald die Wächter laufen (ein Netzlaufwerk kann dauern).</summary>
    public Task Ready { get; }

    /// <summary>Die Wurzeln der Laufwerke, auf denen etwas landen kann – nur bereite (eine leere Kartenleser-Fassung nicht).</summary>
    public static IReadOnlyList<string> Drives()
    {
        List<string> roots = [];
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Ein Laufwerk, das nicht antwortet, fällt weg.
            }
        }

        return roots;
    }

    /// <summary>
    /// Wartet, bis eine Kopie des Merkers auftaucht, und liefert den Ordner, in dem sie liegt. <c>null</c>, wenn
    /// innerhalb von <paramref name="patience"/> keine kam – dann lag das Ziel nicht in einem Ordner (ein Archiv,
    /// ein Telefon) oder auf einem Laufwerk ohne Wächter. Die Kopie bleibt, bis <see cref="RemoveCopyAsync"/> sie
    /// entfernt: Der Explorer greift nach dem Anlegen noch auf sie zu (Attribute, Auswahl in der Ansicht) – fehlt
    /// sie dann, meldet er „Der Pfad ist nicht verfügbar“ und hält das Ablegen an, bis jemand OK drückt.
    /// </summary>
    public async Task<string?> TargetAsync(TimeSpan patience)
    {
        var landed = _landed.Task;
        return await Task.WhenAny(landed, Task.Delay(patience)).ConfigureAwait(false) == landed
            ? Path.GetDirectoryName(landed.Result)
            : null;
    }

    /// <summary>Entfernt die Kopie des Merkers – erst, wenn der Explorer mit dem Ablegen fertig ist.</summary>
    public Task RemoveCopyAsync() => _landed.Task.IsCompletedSuccessfully ? RemoveAsync(_landed.Task.Result) : Task.CompletedTask;

    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_gate)
        {
            _disposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        WorkFolders.Remove(_home);
    }

    private void Watch(List<string> roots)
    {
        foreach (var root in roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root, _name)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnCreated;
                watcher.Renamed += OnCreated;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        watcher.Dispose();
                        return;
                    }

                    _watchers.Add(watcher);
                }

                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or ObjectDisposedException)
            {
                // Dieses Laufwerk lässt sich nicht beobachten; auf ihm wird der Merker nicht gefunden.
                watcher?.Dispose();
            }
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        // Der Merker selbst liegt unter einer der Wurzeln; er zählt nicht, nur eine Kopie.
        if (string.Equals(Path.GetFileName(e.FullPath), _name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFullPath(e.FullPath), Path.GetFullPath(Marker), StringComparison.OrdinalIgnoreCase))
        {
            _ = _landed.TrySetResult(e.FullPath);
        }
    }

    // Die Kopie ist leer; der Explorer hält sie aber womöglich noch einen Augenblick offen.
    private static async Task RemoveAsync(string copy)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(copy))
                {
                    Directory.Delete(copy);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }
}
