namespace NexusSSH.Core.Services;

/// <summary>Arbeitsordner im Temp-Verzeichnis, einer je Prozess – für Dateien, die nur vorübergehend hier liegen.</summary>
internal static class WorkFolders
{
    /// <summary>
    /// Der Ordner dieses Prozesses unter <paramref name="root"/>. Jeder Prozess hat seinen eigenen, damit zwei
    /// Fenster der App sich beim Aufräumen nicht die Dateien nehmen.
    /// </summary>
    public static string Of(string root, string prefix) => Path.Combine(root, prefix + Environment.ProcessId);

    /// <summary>
    /// Entfernt die Ordner von Prozessen, die es nicht mehr gibt – nach einem Absturz blieben die geholten Dateien
    /// sonst liegen. <paramref name="isRunning"/> sagt, ob ein Prozess mit dieser Nummer läuft.
    /// </summary>
    public static void RemoveStale(string root, string prefix, Func<int, bool> isRunning)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, prefix + "*"))
            {
                if (int.TryParse(Path.GetFileName(folder).AsSpan(prefix.Length), out var process) && !isRunning(process))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nichts da oder gesperrt: dann eben beim nächsten Start.
        }
    }

    /// <summary>Löscht den Ordner samt Inhalt; ein gesperrter Rest stört nicht.</summary>
    public static void Remove(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Der Zwischenordner für alles, was ein anderes Programm aus dem Dateibrowser bekommt (Ziehen, „Kopieren“): Die
/// Dateien werden erst hierher geladen und von hier weitergereicht (<see cref="VirtualDrop"/>). Jeder Abruf bekommt
/// seinen eigenen Unterordner; der ganze Ordner verschwindet mit dem Ende der App.
/// </summary>
public sealed class DropFetcher(string workDirectory) : IDisposable
{
    private const string FolderPrefix = "drag-";

    /// <summary>Der Zwischenordner dieses Prozesses unter <paramref name="root"/>.</summary>
    public static string WorkDirectory(string root) => WorkFolders.Of(root, FolderPrefix);

    /// <summary>Entfernt Zwischenordner von Prozessen, die es nicht mehr gibt.</summary>
    public static void RemoveStale(string root, Func<int, bool> isRunning) => WorkFolders.RemoveStale(root, FolderPrefix, isRunning);

    /// <summary>Ein neuer, leerer Ordner im Zwischenordner – wer ihn bekommt, entfernt ihn auch wieder.</summary>
    public string NewFolder()
    {
        var folder = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Die geholten Kopien sollen nicht liegen bleiben.</summary>
    public void Dispose() => WorkFolders.Remove(workDirectory);
}
