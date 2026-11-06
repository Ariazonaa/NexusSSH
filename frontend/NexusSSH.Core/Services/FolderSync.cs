using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Wohin ein Abgleich überträgt.</summary>
public enum SyncDirection
{
    Upload,
    Download,
}

/// <summary>Eine Datei, die ein Abgleich übertragen würde.</summary>
/// <param name="RelativePath">Pfad unterhalb der beiden Ordner, mit <c>/</c> getrennt.</param>
/// <param name="IsNew">Am Ziel gibt es sie noch nicht; sonst ist sie dort älter oder anders groß.</param>
/// <param name="LocalPath">Die Datei auf diesem Rechner – Quelle beim Hochladen, Ziel beim Herunterladen.</param>
/// <param name="RemotePath">Die Datei auf dem Server.</param>
public sealed record SyncItem(string RelativePath, long Size, bool IsNew, string LocalPath, string RemotePath);

/// <summary>Eine Datei, die nur am Ziel liegt – ein Abgleich löscht sie nur, wenn das ausdrücklich verlangt ist.</summary>
/// <param name="Path">Am Ziel: beim Hochladen der Pfad auf dem Server, beim Herunterladen der lokale.</param>
public sealed record SyncExtra(string RelativePath, string Path);

/// <summary>Was ein Abgleich täte: die Dateien, die übertragen würden, und wie viele schon gleich sind.</summary>
/// <param name="Extra">Dateien, die es nur am Ziel gibt.</param>
public sealed record SyncPlan(SyncDirection Direction, string LocalFolder, string RemoteFolder, IReadOnlyList<SyncItem> Items, int Unchanged, IReadOnlyList<SyncExtra> Extra)
{
    public int New => Items.Count(item => item.IsNew);

    public int Changed => Items.Count - New;

    public long Bytes => Items.Sum(item => item.Size);
}

/// <summary>
/// Gleicht einen lokalen Ordner mit einem auf dem Server ab: Übertragen wird nur, was am Ziel fehlt, anders groß
/// oder älter ist als an der Quelle – in eine Richtung. Was nur am Ziel liegt, bleibt dort, außer der Benutzer
/// verlangt ausdrücklich, es zu löschen. Erst wird errechnet, was geschähe (<see cref="PlanAsync"/>, das sieht der
/// Benutzer), dann übertragen (<see cref="StartAsync"/>).
/// </summary>
public static class FolderSync
{
    /// <summary>Mehr Dateien vergleicht ein Abgleich nicht; darüber scheitert er mit <c>too_many</c>.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>
    /// So viel darf die Uhrzeit des Ziels hinter der der Quelle liegen, ohne dass die Datei als geändert gilt:
    /// Dateisysteme runden verschieden (FAT auf zwei Sekunden).
    /// </summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Vergleicht die beiden Ordner. Wirft <see cref="RemoteFileException"/>, wenn sich der Ordner des Servers
    /// nicht erfassen lässt oder es zu viele Dateien sind, und <see cref="IOException"/> bzw.
    /// <see cref="UnauthorizedAccessException"/>, wenn der lokale sich nicht lesen lässt.
    /// </summary>
    /// <param name="byContent">
    /// Nach Inhalt vergleichen statt nach Größe und Zeit: Gleich große Dateien gelten nur dann als gleich, wenn ihre
    /// Prüfsummen (SHA-256) übereinstimmen – die Uhrzeit zählt dann nicht. Braucht ein Konto, das Befehle ausführen
    /// darf, und liest jede dieser Dateien auf beiden Seiten einmal ganz.
    /// </param>
    /// <param name="exclude">
    /// Muster, die der Abgleich auslässt (<see cref="TransferExcludes"/>) – auf beiden Seiten: Was ausgelassen wird,
    /// gilt weder als fehlend noch als „nur am Ziel“, wird also weder übertragen noch gelöscht.
    /// </param>
    public static async Task<SyncPlan> PlanAsync(IRemoteFileSystem files, string localFolder, string remoteFolder, SyncDirection direction, bool byContent = false, IReadOnlyList<string>? exclude = null)
    {
        var rules = new TransferExcludes(exclude);
        // Der ganze Baum, gefiltert wird hier: Beide Seiten müssen nach derselben Regel ausgelassen werden – sagte
        // der Kern bei einem Namen etwas anderes als die App, gälte eine Datei als „nur am Ziel“ und würde gelöscht.
        var remote = Remote(await files.TreeAsync(remoteFolder), remoteFolder, rules);
        var local = await Task.Run(() => Local(localFolder, rules));
        var same = byContent ? await SameContentAsync(files, local, remote) : null;

        var items = new List<SyncItem>();
        var extra = new List<SyncExtra>();
        var unchanged = 0;
        if (direction == SyncDirection.Upload)
        {
            foreach (var (path, file) in local)
            {
                var known = remote.TryGetValue(path, out var there);
                if (same is not null ? same.Contains(path) : known && there!.Size == file.Length && file.LastWriteTimeUtc <= there.Modified.UtcDateTime + Tolerance)
                {
                    unchanged++;
                    continue;
                }

                items.Add(new SyncItem(path, file.Length, !known, file.FullName, known ? there!.RemotePath : RemotePath.Combine(remoteFolder, path)));
            }

            extra.AddRange(remote.Where(entry => !local.ContainsKey(entry.Key)).Select(entry => new SyncExtra(entry.Key, entry.Value.RemotePath)));
        }
        else
        {
            foreach (var (path, entry) in remote)
            {
                var known = local.TryGetValue(path, out var here);
                if (same is not null ? same.Contains(path) : known && here!.Length == entry.Size && entry.Modified.UtcDateTime <= here.LastWriteTimeUtc + Tolerance)
                {
                    unchanged++;
                    continue;
                }

                var target = known ? here!.FullName : Path.Combine(localFolder, path.Replace('/', Path.DirectorySeparatorChar));
                items.Add(new SyncItem(path, entry.Size, !known, target, entry.RemotePath));
            }

            extra.AddRange(local.Where(file => !remote.ContainsKey(file.Key)).Select(file => new SyncExtra(file.Key, file.Value.FullName)));
        }

        return new SyncPlan(
            direction,
            localFolder,
            remoteFolder,
            [.. items.OrderBy(item => item.RelativePath, StringComparer.Ordinal)],
            unchanged,
            [.. extra.OrderBy(item => item.RelativePath, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Überträgt die Dateien des Plans – wenn das Backend es kann, als einen Transfer mit gemeinsamem Fortschritt,
    /// sonst Datei für Datei. Vorhandene Dateien am Ziel werden ersetzt; dass der Benutzer das will, hat er mit dem
    /// Plan bestätigt. Mit <paramref name="deleteExtra"/> wird vorher gelöscht, was nur am Ziel liegt
    /// (<see cref="SyncPlan.Extra"/>) – nie von selbst.
    /// </summary>
    public static async Task StartAsync(IRemoteFileSystem files, SyncPlan plan, bool deleteExtra = false)
    {
        if (deleteExtra)
        {
            foreach (var extra in plan.Extra)
            {
                if (plan.Direction == SyncDirection.Upload)
                {
                    await files.DeleteAsync(extra.Path);
                }
                else
                {
                    File.Delete(extra.Path);
                }
            }
        }

        if (plan.Items.Count == 0)
        {
            return;
        }

        if (plan.Direction == SyncDirection.Download)
        {
            // Als ein Transfer geht, was unter dem Ordner des Servers so heißt wie hier; die Pfade nennt der Server.
            var prefix = plan.RemoteFolder.TrimEnd('/') + "/";
            if (plan.Items.All(item => item.RemotePath.StartsWith(prefix, StringComparison.Ordinal))
                && files.DownloadSome(plan.RemoteFolder, plan.LocalFolder, [.. plan.Items.Select(item => item.RemotePath[prefix.Length..])]) is not null)
            {
                return;
            }

            foreach (var item in plan.Items)
            {
                var directory = Path.GetDirectoryName(item.LocalPath)!;
                Directory.CreateDirectory(directory);
                files.Download(item.RemotePath, directory);
            }

            return;
        }

        // Als ein Transfer geht, was auf dem Server so heißt wie hier (ein Name, den dieser Rechner anders schreiben
        // musste, hieße sonst dort plötzlich wie hier).
        if (plan.Items.All(item => item.RemotePath == RemotePath.Combine(plan.RemoteFolder, item.RelativePath))
            && files.UploadSome(plan.LocalFolder, plan.RemoteFolder, [.. plan.Items.Select(item => item.RelativePath)]) is not null)
        {
            return;
        }

        // Welche Ordner es auf dem Server schon gibt, sagt eine neue Aufnahme: Zwischen Plan und Start kann Zeit liegen.
        var existing = (await files.TreeAsync(plan.RemoteFolder)).Where(entry => entry.IsDirectory).Select(entry => entry.RemotePath).ToHashSet(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            var directory = RemotePath.Parent(item.RemotePath) ?? RemotePath.Root;
            foreach (var missing in Missing(directory, existing))
            {
                await files.CreateDirectoryAsync(missing);
                existing.Add(missing);
            }

            files.Upload(item.LocalPath, directory);
        }
    }

    // Die Pfade, deren Inhalt auf beiden Seiten derselbe ist. Verglichen wird nur, was gleich groß ist – alles
    // andere ist ohnehin verschieden. Nennt der Server für keine Datei eine Summe, fehlt ihm das Programm dafür:
    // Dann wäre „alles übertragen“ die falsche Antwort, also scheitert der Vergleich.
    private static async Task<HashSet<string>> SameContentAsync(IRemoteFileSystem files, Dictionary<string, FileInfo> local, Dictionary<string, RemoteTreeEntry> remote)
    {
        var candidates = local.Where(file => remote.TryGetValue(file.Key, out var there) && there.Size == file.Value.Length).Select(file => file.Key).ToList();
        var same = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count == 0)
        {
            return same;
        }

        var sums = await files.ChecksumsAsync([.. candidates.Select(path => remote[path].RemotePath)]);
        if (sums.All(sum => sum is null))
        {
            throw new RemoteFileException("the server names no checksums (sha256sum, shasum, sha256, openssl)", "command_failed");
        }

        await Task.Run(async () =>
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (index < sums.Count && sums[index] is { } sum && await Checksums.OfFileAsync(local[candidates[index]].FullName) == sum)
                {
                    same.Add(candidates[index]);
                }
            }
        });
        return same;
    }

    // Die Ordner auf dem Weg zu `directory`, die es noch nicht gibt – der oberste zuerst.
    private static List<string> Missing(string directory, HashSet<string> existing)
    {
        var missing = new List<string>();
        for (var current = directory; current is not null && current != RemotePath.Root && !existing.Contains(current); current = RemotePath.Parent(current))
        {
            missing.Insert(0, current);
        }

        return missing;
    }

    // Die Dateien des Servers nach ihrem Pfad unterhalb des Ordners, so wie sie auf diesem Rechner hießen.
    private static Dictionary<string, RemoteTreeEntry> Remote(IReadOnlyList<RemoteTreeEntry> tree, string remoteFolder, TransferExcludes rules)
    {
        if (tree.Count == 0 || !tree[0].IsDirectory)
        {
            throw new RemoteFileException($"{remoteFolder} is not a directory", "not_a_directory");
        }

        // Jeder lokale Pfad beginnt mit dem Namen des Ordners selbst; der gehört nicht zum Pfad darunter.
        var top = Slashes(tree[0].LocalPath).Length + 1;
        var files = new Dictionary<string, RemoteTreeEntry>(StringComparer.Ordinal);
        foreach (var entry in tree.Where(entry => !entry.IsDirectory))
        {
            var path = Slashes(entry.LocalPath);
            if (path.Length > top && !rules.SkipsAnyLevel(path[top..]))
            {
                files[path[top..]] = entry;
            }
        }

        return files;
    }

    // Die lokalen Dateien nach ihrem Pfad unterhalb des Ordners. Verknüpfungen bleiben aus: Sie könnten im Kreis
    // oder aus dem Ordner hinaus führen. Ein ausgelassener Ordner wird nicht betreten – er kann riesig sein
    // (`node_modules`) oder sich nicht lesen lassen, und beides ginge den Abgleich nichts an.
    private static Dictionary<string, FileInfo> Local(string folder, TransferExcludes rules)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        Stack<(DirectoryInfo Directory, string Relative)> open = new([(new DirectoryInfo(folder), string.Empty)]);
        while (open.Count > 0)
        {
            var (directory, prefix) = open.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                var relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
                if (rules.Skips(relative))
                {
                    continue;
                }

                if (entry is DirectoryInfo below)
                {
                    open.Push((below, relative));
                    continue;
                }

                if (files.Count >= MaxFiles)
                {
                    throw new RemoteFileException($"more than {MaxFiles} files", "too_many");
                }

                files[relative] = (FileInfo)entry;
            }
        }

        return files;
    }

    private static string Slashes(string path) => path.Replace('\\', '/').TrimEnd('/');
}
