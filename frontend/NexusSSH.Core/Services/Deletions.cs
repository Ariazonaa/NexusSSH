namespace NexusSSH.Core.Services;

/// <summary>Wo ein Löschvorgang steht.</summary>
public enum DeletionState
{
    /// <summary>Er löscht noch.</summary>
    Running,

    /// <summary>Alles ist weg.</summary>
    Done,

    /// <summary>Ein Eintrag ließ sich nicht löschen; was davor weg war, bleibt weg.</summary>
    Failed,

    /// <summary>Auf Wunsch angehalten, nach dem Eintrag, der gerade dran war.</summary>
    Cancelled,
}

/// <summary>
/// Wie weit ein Löschvorgang ist: so viele Einträge sind schon weg (Dateien, Verweise und Ordner). Bei
/// <see cref="DeletionState.Failed"/> nennen <see cref="Code"/> und <see cref="Message"/> den Grund – die Meldung
/// kommt vom Server und ist fremder Text.
/// </summary>
public readonly record struct DeletionStatus(DeletionState State, long Removed, string? Code = null, string? Message = null)
{
    public bool IsFinished => State != DeletionState.Running;
}

/// <summary>Ein laufender Löschvorgang. <see cref="Cancel"/> hält ihn nach dem aktuellen Eintrag an; was weg ist, bleibt weg.</summary>
public interface IDeletion
{
    void Cancel();
}

/// <summary>Löschvorgänge ohne eigenen Weg im Backend und auf diesem Rechner.</summary>
public static class Deletions
{
    /// <summary>
    /// Ein Löschvorgang aus einzelnen <c>DeleteAsync</c>: die Pfade nacheinander, gezählt wird je Pfad. Für Backends,
    /// die keinen eigenen Löschvorgang kennen (<see cref="IRemoteFileSystem.StartDelete"/>).
    /// </summary>
    public static IDeletion OneByOne(Func<string, Task> delete, IReadOnlyList<string> paths, Action<DeletionStatus> changed)
    {
        var run = new Stop();
        _ = RunAsync();
        return run;

        async Task RunAsync()
        {
            changed(new DeletionStatus(DeletionState.Running, 0));
            long removed = 0;
            foreach (var path in paths)
            {
                if (run.IsSet)
                {
                    changed(new DeletionStatus(DeletionState.Cancelled, removed));
                    return;
                }

                try
                {
                    await delete(path);
                }
                catch (RemoteFileException e)
                {
                    changed(new DeletionStatus(DeletionState.Failed, removed, e.Code, e.Message));
                    return;
                }

                changed(new DeletionStatus(DeletionState.Running, ++removed));
            }

            changed(new DeletionStatus(DeletionState.Done, removed));
        }
    }

    /// <summary>Löscht über das Backend und wartet auf das Ende – für einen Browser, dem keine Transfer-Liste zusieht.</summary>
    public static Task<DeletionStatus> RunAsync(IRemoteFileSystem files, IReadOnlyList<string> paths)
    {
        var finished = new TaskCompletionSource<DeletionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.StartDelete(paths, status =>
        {
            if (status.IsFinished)
            {
                finished.TrySetResult(status);
            }
        });
        return finished.Task;
    }

    /// <summary>
    /// Löscht Dateien und Ordner dieses Rechners endgültig, ohne Papierkorb – neben der Oberfläche, gezählt je Eintrag.
    /// <paramref name="changed"/> kommt von einem anderen Thread: gleich zu Beginn, unterwegs höchstens alle
    /// <see cref="Report"/>, am Ende genau einmal.
    /// </summary>
    public static IDeletion Local(IReadOnlyList<string> paths, Action<DeletionStatus> changed)
    {
        var run = new Stop();
        _ = Task.Run(() =>
        {
            changed(new DeletionStatus(DeletionState.Running, 0));
            var last = Environment.TickCount64;
            changed(DeleteLocally(paths, () => run.IsSet, removed =>
            {
                if (Environment.TickCount64 - last >= (long)Report.TotalMilliseconds)
                {
                    last = Environment.TickCount64;
                    changed(new DeletionStatus(DeletionState.Running, removed));
                }
            }));
        });
        return run;
    }

    /// <summary>So oft meldet ein Löschvorgang auf diesem Rechner höchstens seinen Stand – wie der Kern.</summary>
    public static readonly TimeSpan Report = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Der eigentliche Lauf von <see cref="Local"/>: Pfad für Pfad, ein Ordner erst geleert, dann entfernt. Ein
    /// Verweis (Symlink, Junction) wird entfernt, nie betreten – sonst löschte das, worauf er zeigt.
    /// <paramref name="stopped"/> wird vor jedem Eintrag gefragt, <paramref name="removed"/> nach jedem gerufen.
    /// </summary>
    internal static DeletionStatus DeleteLocally(IReadOnlyList<string> paths, Func<bool> stopped, Action<long> removed)
    {
        long count = 0;
        try
        {
            foreach (var path in paths)
            {
                var pending = new Stack<(string Path, bool Emptied)>();
                pending.Push((path, false));
                while (pending.TryPop(out var current))
                {
                    if (stopped())
                    {
                        return new DeletionStatus(DeletionState.Cancelled, count);
                    }

                    var info = new FileInfo(current.Path);
                    var attributes = info.Attributes;
                    if ((int)attributes == -1)
                    {
                        throw new FileNotFoundException(null, current.Path);
                    }

                    var directory = attributes.HasFlag(FileAttributes.Directory);
                    if (directory && !current.Emptied && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push((current.Path, true));
                        foreach (var child in Directory.EnumerateFileSystemEntries(current.Path))
                        {
                            pending.Push((child, false));
                        }

                        continue;
                    }

                    // Ein Verweis auf einen Ordner geht unter Windows wie ein leerer Ordner, sonst wie eine Datei.
                    if (directory && (OperatingSystem.IsWindows() || !attributes.HasFlag(FileAttributes.ReparsePoint)))
                    {
                        Directory.Delete(current.Path, recursive: false);
                    }
                    else
                    {
                        File.Delete(current.Path);
                    }

                    removed(++count);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new DeletionStatus(DeletionState.Failed, count, e is FileNotFoundException or DirectoryNotFoundException ? "not_found" : "failed", e.Message);
        }

        return new DeletionStatus(DeletionState.Done, count);
    }

    private sealed class Stop : IDeletion
    {
        private volatile bool _set;

        public bool IsSet => _set;

        public void Cancel() => _set = true;
    }
}
