using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Schickt Dateien und Ordner von einem Server zu einem anderen, ohne dass der Benutzer sie dazwischen selbst
/// herunter- und wieder hochlädt. Der Weg führt trotzdem über diesen Rechner: Jeder Eintrag wird in einen eigenen
/// Zwischenordner geholt und von dort zum Ziel geschickt – zwei gewöhnliche Transfers aus der Warteschlange, mit
/// allem, was die können (Fortschritt, Abbrechen, Wiederholen). Der Zwischenordner verschwindet, sobald der Upload
/// fertig oder abgebrochen ist; nach einem gescheiterten bleibt er bis zum Ende der App, damit „Wiederholen“ noch
/// etwas zum Hochladen findet.
/// </summary>
public sealed class ServerRelay : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Hop> _hops = [];
    private readonly ITransferQueue _queue;
    private readonly DropFetcher _folders;

    public ServerRelay(ITransferQueue queue, DropFetcher folders)
    {
        (_queue, _folders) = (queue, folders);
        queue.Changed += OnTransfersChanged;
    }

    /// <summary>Der Upload zum Ziel ließ sich nicht beginnen: Name, Zielserver und warum (englisch). Beliebiger Thread.</summary>
    public event Action<string, string, string>? Failed;

    /// <summary>
    /// Holt <paramref name="remotePath"/> von <paramref name="from"/> und schickt es danach in
    /// <paramref name="targetDirectory"/> auf <paramref name="to"/>. Was mit einem Eintrag gleichen Namens dort
    /// geschieht, fragt die Oberfläche vorher: Er wird ersetzt, bleibt nach <paramref name="keep"/> stehen, oder
    /// das Neue kommt als <paramref name="targetName"/> daneben. <c>false</c>, wenn sich aus dem Namen hier kein
    /// Dateiname machen lässt.
    /// </summary>
    public bool Send(
        IRemoteFileSystem from,
        string remotePath,
        IRemoteFileSystem to,
        string targetDirectory,
        string targetServer,
        string? targetName = null,
        TransferKeep keep = TransferKeep.None,
        IReadOnlyList<string>? exclude = null)
    {
        var folder = _folders.NewFolder();
        if (from.LocalTarget(remotePath, folder) is not { } local)
        {
            WorkFolders.Remove(folder);
            return false;
        }

        // Vor dem Start eintragen: Das Ende des Downloads kann schneller gemeldet werden, als Download() zurückkehrt.
        var hop = new Hop(folder, local, targetName ?? RemotePath.Name(remotePath), to, targetDirectory, targetServer, keep);
        lock (_gate)
        {
            _hops.Add(hop);
        }

        hop.Download = from.Download(remotePath, folder, exclude: exclude);
        OnTransfersChanged();
        return true;
    }

    public void Dispose() => _queue.Changed -= OnTransfersChanged;

    // Beliebiger Thread: Ein angekommener Download geht weiter zum Ziel; ein gescheiterter hinterlässt nichts.
    private void OnTransfersChanged()
    {
        var items = _queue.Items;
        List<Hop> arrived = [];
        List<string> done = [];
        lock (_gate)
        {
            foreach (var hop in _hops.ToList())
            {
                var forwarded = hop.Upload is not null;
                var item = items.FirstOrDefault(item => item.Id == (hop.Upload ?? hop.Download));
                switch (item?.State)
                {
                    // Der Download ist da. Wird der Upload gerade begonnen, ist hier nichts mehr zu tun.
                    case TransferState.Completed when !forwarded:
                        if (!hop.Forwarding)
                        {
                            hop.Forwarding = true;
                            arrived.Add(hop);
                        }

                        break;

                    case TransferState.Completed or TransferState.Cancelled:
                    case TransferState.Failed when !forwarded:
                        _hops.Remove(hop);
                        done.Add(hop.Folder);
                        break;

                    case TransferState.Failed:
                        // Der Ordner bleibt: „Wiederholen“ lädt aus ihm hoch.
                        _hops.Remove(hop);
                        break;
                }
            }
        }

        foreach (var folder in done)
        {
            WorkFolders.Remove(folder);
        }

        foreach (var hop in arrived)
        {
            try
            {
                // Mit dem Namen des Servers: Der lokale kann ein anderer sein (a:b wird unter Windows zu a_b).
                hop.Upload = hop.To.Upload(hop.Local, hop.TargetDirectory, hop.Name, hop.Keep);
            }
            catch (Exception e) when (e is RemoteFileException or BackendException or IOException)
            {
                lock (_gate)
                {
                    _hops.Remove(hop);
                }

                WorkFolders.Remove(hop.Folder);
                Failed?.Invoke(hop.Name, hop.TargetServer, e.Message);
            }
        }

        // Ein Upload, der schon beim Einreihen endete, hat sich gemeldet, bevor seine Kennung hier stand.
        if (arrived.Count > 0)
        {
            OnTransfersChanged();
        }
    }

    private sealed record Hop(string Folder, string Local, string Name, IRemoteFileSystem To, string TargetDirectory, string TargetServer, TransferKeep Keep)
    {
        public string? Download { get; set; }

        public string? Upload { get; set; }

        /// <summary>Der Upload wird gerade begonnen – damit ein zweites Ereignis ihn nicht noch einmal beginnt.</summary>
        public bool Forwarding { get; set; }
    }
}
