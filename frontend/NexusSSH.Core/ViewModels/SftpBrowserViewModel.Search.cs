using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Ein Treffer der Suche auf dem Server. <see cref="Name"/> und <see cref="Location"/> sind zum Anzeigen (ohne
/// Steuerzeichen), <see cref="Path"/> ist der Pfad, wie der Server ihn nennt.
/// </summary>
public sealed record FileHit(string Path, string Name, string Location, bool IsDirectory, long Size)
{
    /// <summary>Name und Ort – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Name}, {Location}";
}

// Suche nach Namen unter dem angezeigten Verzeichnis. Die Treffer stehen in einer eigenen Liste; ein Klick
// wechselt in den Ordner des Treffers und wählt ihn dort aus – so bleiben alle Dateiaktionen bei der einen Liste.
public sealed partial class SftpBrowserViewModel
{
    /// <summary>So viele Treffer liefert der Kern höchstens (<c>sftp::FIND_HITS</c>).</summary>
    public const int MaxHits = 500;

    // Zählt die Suchen: Nur die Antwort auf die letzte wird gezeigt.
    private int _searches;

    public ObservableCollection<FileHit> Hits { get; } = [];

    /// <summary>Das Suchfeld samt Trefferliste liegt über der Dateiliste.</summary>
    [ObservableProperty]
    public partial bool IsSearchOpen { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Wie viele Treffer es gibt – oder warum die Suche scheiterte.</summary>
    [ObservableProperty]
    public partial string SearchStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    public void OpenSearch() => IsSearchOpen = IsAvailable;

    /// <summary>Schließt die Suche und verwirft die Treffer; eine laufende Suche zeigt nichts mehr.</summary>
    public void CloseSearch()
    {
        _searches++;
        IsSearchOpen = false;
        IsSearching = false;
        SearchText = string.Empty;
        SearchStatus = string.Empty;
        Hits.Clear();
    }

    /// <summary>Sucht unter dem angezeigten Verzeichnis nach Einträgen, deren Name <see cref="SearchText"/> enthält.</summary>
    public async Task SearchAsync()
    {
        var search = ++_searches;
        var text = SearchText.Trim();
        Hits.Clear();
        SearchStatus = string.Empty;
        if (_files is not { } files || text.Length == 0)
        {
            IsSearching = false;
            return;
        }

        IsSearching = true;
        var root = CurrentPath;
        string status;
        IReadOnlyList<RemoteTreeEntry> found = [];
        try
        {
            var result = await files.FindAsync(root, text);
            found = result.Hits;
            status = found.Count switch
            {
                0 => L.Get("HitNone"),
                1 => L.Get("HitCountOne"),
                >= MaxHits => L.Get("HitLimit", found.Count),
                var count => L.Get("HitCount", count),
            };

            // Die Suche hat nicht alles gesehen: Das muss dastehen – „nichts gefunden“ hieße sonst „gibt es nicht“.
            if (result.StoppedAfter is { } seen)
            {
                status = L.Get("HitPartial", status.TrimEnd('.'), seen.ToString("N0", L.Culture));
            }
        }
        catch (RemoteFileException e)
        {
            status = Describe(e);
        }

        // Inzwischen wurde neu gesucht, geschlossen oder die Verbindung gewechselt.
        if (search != _searches)
        {
            return;
        }

        foreach (var hit in found)
        {
            var parent = RemotePath.Parent(hit.RemotePath) ?? RemotePath.Root;
            Hits.Add(new FileHit(hit.RemotePath, Format.Visible(RemotePath.Name(hit.RemotePath)), Format.Visible(parent), hit.IsDirectory, hit.Size));
        }

        SearchStatus = status;
        IsSearching = false;
    }

    /// <summary>
    /// Packt Einträge des angezeigten Verzeichnisses dort zu einem Archiv – auf dem Server. Der Name bekommt
    /// <c>.tar.gz</c>, wenn er keine Endung eines Archivs trägt. <c>false</c>, wenn es scheitert (der Grund steht in
    /// <see cref="Error"/>), etwa weil das Konto keine Befehle ausführen darf.
    /// </summary>
    public Task<bool> PackAsync(IReadOnlyList<RemoteEntry> entries, string archiveName)
    {
        var name = ArchiveName(archiveName);
        var names = entries.Select(entry => entry.Name).ToList();
        return names.Count == 0
            ? Task.FromResult(false)
            : ChangeAsync(files => files.PackAsync(CurrentPath, names, name), name);
    }

    /// <summary>Entpackt ein Archiv in das angezeigte Verzeichnis, ohne Vorhandenes zu überschreiben.</summary>
    public Task<bool> UnpackAsync(RemoteEntry archive)
        => ChangeAsync(files => files.UnpackAsync(CurrentPath, archive.Name));

    /// <summary>
    /// Vergleicht einen lokalen Ordner mit dem angezeigten und errechnet, was ein Abgleich übertrüge – ohne etwas
    /// zu übertragen. <c>null</c>, wenn das scheitert; der Grund steht in <see cref="Error"/>.
    /// </summary>
    /// <param name="remoteFolder">Der Ordner am Server; ohne Angabe der angezeigte.</param>
    public async Task<SyncPlan?> PlanSyncAsync(string localFolder, SyncDirection direction, bool byContent = false, IReadOnlyList<string>? exclude = null, string? remoteFolder = null)
    {
        if (_files is not { } files)
        {
            return null;
        }

        try
        {
            return await FolderSync.PlanAsync(files, localFolder, remoteFolder ?? CurrentPath, direction, byContent, exclude);
        }
        catch (RemoteFileException e)
        {
            Error = Describe(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = L.Get("SyncLocalUnreadable", e.Message);
        }

        return null;
    }

    /// <summary>
    /// Überträgt, was der Plan nennt, und lädt die Liste neu. Mit <paramref name="deleteExtra"/> wird vorher
    /// gelöscht, was nur am Ziel liegt – das verlangt der Benutzer ausdrücklich, nie die App.
    /// </summary>
    public Task<bool> StartSyncAsync(SyncPlan plan, bool deleteExtra = false)
        => ChangeAsync(async files =>
        {
            try
            {
                await FolderSync.StartAsync(files, plan, deleteExtra);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Eine lokale Datei ließ sich nicht löschen oder ein Ordner nicht anlegen.
                throw new RemoteFileException(e.Message, "local_failed");
            }
        });

    /// <summary>Der Plan in einem Satz: wie viele Dateien neu und geändert sind, ihre Größe, wie viele schon gleich sind.</summary>
    public static string Describe(SyncPlan plan)
        => plan.Items.Count == 0
            ? L.Get("SyncNothing", plan.Unchanged)
            : plan.Direction == SyncDirection.Upload
                ? L.Get("SyncSummaryUpload", plan.New, plan.Changed, Format.FileSize(plan.Bytes, isDirectory: false), plan.Unchanged)
                : L.Get("SyncSummaryDownload", plan.New, plan.Changed, Format.FileSize(plan.Bytes, isDirectory: false), plan.Unchanged);

    /// <summary>Ob sich ein Eintrag entpacken lässt – nach seinem Namen.</summary>
    public static bool IsArchive(RemoteEntry entry)
        => !entry.IsDirectory && ArchiveEndings.Any(ending => entry.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));

    /// <summary>Der Name eines neuen Archivs: wie eingetippt, mit <c>.tar.gz</c>, wenn die Endung fehlt.</summary>
    public static string ArchiveName(string typed)
    {
        var name = typed.Trim();
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".tar.gz";
    }

    private static readonly string[] ArchiveEndings = [".tar.gz", ".tgz", ".tar", ".tar.bz2", ".tbz2", ".tar.xz", ".txz", ".zip"];

    /// <summary>
    /// Zeigt einen Pfad, von dem nicht feststeht, ob er Ordner oder Datei ist (er wurde im Terminal angeklickt):
    /// erst sein Ordner, dann – ist er selbst ein Ordner – er. Eine Datei bleibt in ihrem Ordner ausgewählt.
    /// <c>false</c>, wenn es ihn nicht gibt; die Ansicht steht dann, wo sie hinkam.
    /// </summary>
    public async Task<bool> ShowPathAsync(string path)
    {
        if (RemotePath.Parent(path) is not { } parent)
        {
            return await GoAsync(RemotePath.Root);
        }

        if (!await GoAsync(parent))
        {
            return false;
        }

        // Unter allen Einträgen suchen, auch den ausgeblendeten: Ein Pfad wie ~/.config ist nicht versteckt gemeint.
        var name = RemotePath.Name(path);
        var entry = _all.FirstOrDefault(candidate => candidate.Name == name);
        if (entry is { IsDirectory: true })
        {
            return await GoAsync(path);
        }

        Select(name);
        return entry is not null;

        // Ob die Ansicht dort ankam – scheitert das Laden, bleibt sie, wo sie war.
        async Task<bool> GoAsync(string target)
        {
            await NavigateToAsync(target);
            return CurrentPath == target;
        }
    }

    /// <summary>
    /// Zeigt einen Treffer in der Dateiliste: wechselt in seinen Ordner und wählt ihn aus. Die Suche schließt
    /// dabei. <c>false</c>, wenn es den Eintrag dort nicht (mehr) gibt.
    /// </summary>
    public async Task<bool> RevealAsync(FileHit hit)
    {
        CloseSearch();
        await NavigateToAsync(RemotePath.Parent(hit.Path) ?? RemotePath.Root);
        var name = RemotePath.Name(hit.Path);
        Select(name);
        return SelectedEntry is not null;
    }

    // Wählt einen Eintrag der Liste aus. Verdeckt ihn der Filter, endet der: Wer einen Pfad nennt, will ihn sehen.
    private void Select(string name)
    {
        if (IsFiltered && _all.Any(entry => entry.Name == name) && Entries.All(entry => entry.Name != name))
        {
            Filter = string.Empty;
        }

        SelectedEntry = Entries.FirstOrDefault(entry => entry.Name == name);
    }
}
