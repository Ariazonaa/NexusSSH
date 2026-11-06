namespace NexusSSH.Core.Models;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Ein Dateitransfer im Transfer Center (Spec §7). <see cref="Progress"/> liegt in 0–1.
/// <see cref="Detail"/> ist die englische Meldung des Backends zu einem Fehler. <see cref="ResumedAt"/> nennt, wie
/// viel von einem abgebrochenen Versuch schon am Ziel war und nicht noch einmal übertragen wurde (0 = alles von
/// vorn): bei einer Datei die Stelle, an der es weiterging, bei einem Ordner auch die Dateien, die vollständig da
/// waren. <see cref="Files"/> und <see cref="FilesDone"/> zählen Dateien – daran misst sich der Fortschritt, wenn
/// es keine Bytes gibt (ein Ordner aus lauter leeren Dateien).
/// </summary>
public sealed record TransferItem(
    string Id,
    string FileName,
    string Server,
    TransferDirection Direction,
    TransferState State,
    double Progress,
    long Bytes = 0,
    long Total = 0,
    string? Detail = null,
    long ResumedAt = 0,
    long Files = 0,
    long FilesDone = 0)
{
    public bool IsFinished => State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;

    /// <summary>Der Fortschritt in 0–1: nach Bytes, ohne Bytes nach Dateien, ohne beides erst am Ende voll.</summary>
    public static double ProgressOf(long bytes, long total, long filesDone, long files, bool completed) => (total, files) switch
    {
        ( > 0, _) => Math.Clamp((double)bytes / total, 0, 1),
        (_, > 0) => Math.Clamp((double)filesDone / files, 0, 1),
        _ => completed ? 1 : 0,
    };
}
