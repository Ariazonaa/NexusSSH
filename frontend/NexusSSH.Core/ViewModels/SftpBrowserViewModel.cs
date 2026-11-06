using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Remote-Dateibrowser einer Sitzung (Spec §4): aktueller Pfad, Inhalt und Verlauf wie im Explorer, daneben der Baum
/// der Verzeichnisse (<c>SftpBrowserViewModel.Tree.cs</c>).
/// </summary>
public sealed partial class SftpBrowserViewModel : ObservableObject
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private IRemoteFileSystem? _files;

    // Alles, was der Server für das Verzeichnis geliefert hat – auch das, was gerade ausgeblendet ist.
    private IReadOnlyList<RemoteEntry> _all = [];

    public SftpBrowserViewModel(IRemoteFileSystem? files = null)
    {
        CurrentPath = RemotePath.Root;
        Attach(files);
    }

    /// <summary><c>false</c>, solange keine Verbindung steht oder der Server kein SFTP anbietet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    public partial bool IsAvailable { get; private set; }

    public bool IsUnavailable => !IsAvailable;

    private string? _unavailableReason;

    /// <summary>Was an Stelle des Browsers steht, solange er nicht zu benutzen ist: kein SFTP – oder die Verbindung darunter ist weg.</summary>
    public string UnavailableText => _unavailableReason ?? L.Get("SftpUnavailable");

    /// <summary>Sagt, warum der Browser gerade nichts zeigt (die Verbindung unter einem Mosh-Terminal ist weg); <c>null</c> = der übliche Grund.</summary>
    public void SetUnavailableReason(string? reason)
    {
        _unavailableReason = reason;
        OnPropertyChanged(nameof(UnavailableText));
    }

    /// <summary>
    /// Was die Liste zeigt. Bei jedem Füllen eine neue Liste statt Eintrag für Eintrag in die alte: Die Ansicht
    /// bekäme sonst je Eintrag eine Meldung – bei 100 000 Einträgen stand sie dafür spürbar still und hielt für
    /// jeden einzelnen ein Stück Speicher fest (gemessen 2026-10-10). Wer die Liste braucht, liest sie hier jedes
    /// Mal neu und merkt sie sich nicht.
    /// </summary>
    public ObservableCollection<RemoteEntry> Entries
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    = [];

    public ObservableCollection<PathSegment> Segments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentName))]
    public partial string CurrentPath { get; private set; }

    /// <summary>Name des angezeigten Verzeichnisses für den Kopf des Bereichs; ganz oben <c>/</c>.</summary>
    public string CurrentName => Format.Visible(RemotePath.Segments(CurrentPath).LastOrDefault()?.Name ?? RemotePath.Root);

    /// <summary>Wie viele Einträge die Liste zeigt, in Worten – die Fußzeile des Bereichs.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial RemoteEntry? SelectedEntry { get; set; }

    /// <summary>Meldung zur letzten gescheiterten Aktion; <c>null</c>, wenn die letzte gelang.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Ob Einträge gezeigt werden, deren Name mit einem Punkt beginnt.</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; } = true;

    /// <summary>
    /// Zeigt nur Einträge, deren Name diesen Text enthält (Groß und Klein gleich). Gilt für das angezeigte
    /// Verzeichnis und endet mit dem Wechsel in ein anderes; den Server fragt der Filter nichts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    public partial string Filter { get; set; } = string.Empty;

    public bool IsFiltered => Filter.Length > 0;

    /// <summary>Wonach die Liste sortiert ist; Ordner stehen vor den Dateien, nur nach der Art stehen Ordner-Verweise bei den Verweisen.</summary>
    [ObservableProperty]
    public partial FileSort Sort { get; set; }

    /// <summary>Umgekehrte Reihenfolge: Z–A, groß zuerst, neu zuerst.</summary>
    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>Eine Datei wurde geöffnet (Doppelklick, Enter): dieser Browser, der Eintrag und sein voller Pfad.</summary>
    public event Action<SftpBrowserViewModel, RemoteEntry, string>? FileOpened;

    /// <summary>Das Dateisystem der aktuellen Verbindung, z. B. für Transfers außerhalb der Liste.</summary>
    public IRemoteFileSystem? FileSystem => _files;

    public bool HasError => Error is not null;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => RemotePath.Parent(CurrentPath) is not null;

    /// <summary>Das Startverzeichnis der Verbindung – wofür <c>~</c> steht.</summary>
    public string HomePath => _files?.InitialPath ?? RemotePath.Root;

    /// <summary>Hängt den Browser an das Dateisystem einer (neuen) Verbindung und beginnt in dessen Startverzeichnis.</summary>
    public void Attach(IRemoteFileSystem? files)
    {
        _files = files;
        CloseSearch();
        _back.Clear();
        _forward.Clear();
        _all = [];
        Filter = string.Empty;
        Entries = [];
        Segments.Clear();
        Error = null;
        IsEmpty = false;
        CountText = string.Empty;
        CurrentPath = files?.InitialPath ?? RemotePath.Root;
        ResetTree();
        IsAvailable = files is not null;
        IsRoot = files?.IsElevated ?? false;
        HistoryChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Lädt das Startverzeichnis. Getrennt von <see cref="Attach"/>, weil es über das Netz geht.</summary>
    public Task LoadAsync() => ShowAsync(CurrentPath);

    /// <summary>
    /// Hängt den Browser an die Dateien einer Verbindung, die zurückkam (unter einem Mosh-Terminal), und liest erst das
    /// Verzeichnis, in dem er stand – gibt es das nicht mehr, das Startverzeichnis.
    /// </summary>
    public async Task ReattachAsync(IRemoteFileSystem? files, string? path)
    {
        Attach(files);
        if (files is null)
        {
            return;
        }

        if (path is { Length: > 0 } && path != CurrentPath && await ShowAsync(path))
        {
            return;
        }

        await ShowAsync(CurrentPath);
    }

    /// <summary>Wechselt in ein Verzeichnis und merkt sich das bisherige im Verlauf.</summary>
    public async Task NavigateToAsync(string path)
    {
        var target = RemotePath.Normalize(path);
        if (target == CurrentPath)
        {
            await ShowAsync(target);
            return;
        }

        var previous = CurrentPath;
        if (await ShowAsync(target))
        {
            _back.Push(previous);
            _forward.Clear();
            HistoryChanged();
        }
    }

    /// <summary>
    /// Wechselt in ein eingetipptes Verzeichnis. Ohne führenden Schrägstrich gilt der Pfad ab dem angezeigten
    /// Verzeichnis, <c>~</c> steht für das Startverzeichnis der Sitzung.
    /// </summary>
    public Task GoToAsync(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0 || _files is not { } files)
        {
            return Task.CompletedTask;
        }

        var target = text switch
        {
            "~" => files.InitialPath,
            _ when text.StartsWith("~/", StringComparison.Ordinal) => RemotePath.Combine(files.InitialPath, text[2..]),
            _ when text.StartsWith('/') => text,
            _ => RemotePath.Combine(CurrentPath, text),
        };
        return NavigateToAsync(target);
    }

    /// <summary>Öffnet einen Eintrag der Liste: in einen Ordner wechseln, eine Datei zum Bearbeiten melden.</summary>
    [RelayCommand]
    private Task OpenAsync(RemoteEntry? entry)
    {
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        if (entry.IsDirectory)
        {
            return NavigateToAsync(PathOf(entry));
        }

        // Eine Pipe oder ein Gerät zu öffnen, hielte den SFTP-Server an – der Kern lehnte es ohnehin ab.
        if (entry.IsSpecial)
        {
            Error = L.Get("SftpNotAFile");
            return Task.CompletedTask;
        }

        FileOpened?.Invoke(this, entry, PathOf(entry));
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        var target = _back.Peek();
        var previous = CurrentPath;
        if (await ShowAsync(target))
        {
            _back.Pop();
            _forward.Push(previous);
            HistoryChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        var target = _forward.Peek();
        var previous = CurrentPath;
        if (await ShowAsync(target))
        {
            _forward.Pop();
            _back.Push(previous);
            HistoryChanged();
        }
    }

    // Die Rücktaste ruft den Befehl auch an der Wurzel (Execute fragt CanExecute nicht) – dort gibt es nichts darüber.
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task GoUpAsync() =>
        RemotePath.Parent(CurrentPath) is { } parent ? NavigateToAsync(parent) : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => ShowAsync(CurrentPath);

    /// <summary>Blendet die Fehlermeldung aus.</summary>
    [RelayCommand]
    private void DismissError() => Error = null;

    /// <summary>Voller Pfad eines Eintrags der aktuellen Liste.</summary>
    public string PathOf(RemoteEntry entry) => RemotePath.Combine(CurrentPath, entry.Name);

    /// <summary>
    /// Wohin ein Link der Liste zeigt, als voller Pfad: das Ziel aus dem Link, vom Ordner des Links aus aufgelöst
    /// (der angezeigte), mit <c>.</c> und <c>..</c> – ohne den Server zu fragen. Das rechnet nur mit dem Text des
    /// Pfads: Wurde der Ordner über einen Link betreten, meint ein <c>..</c> im Ziel dessen echten Elternordner, hier
    /// aber den des Pfads. <c>null</c>, wenn der Eintrag kein Ziel nennt; <see cref="WithTargetAsync"/> holt es dann nach.
    /// </summary>
    public string? TargetOf(RemoteEntry entry) => entry.LinkTarget is { Length: > 0 } target
        ? target.StartsWith('/') ? RemotePath.Normalize(target) : RemotePath.Combine(CurrentPath, target)
        : null;

    /// <summary>
    /// Der Link mit seinem Ziel: Nennt die Liste es nicht (Dateizugriff über Befehle), fragt der Server nach
    /// (<see cref="IRemoteFileSystem.ReadLinkAsync"/>). Sagt er es auch dann nicht (DSM), bleibt der Eintrag, wie er
    /// ist – das ist kein Fehler, die Eigenschaften schreiben dann „unbekannt“. Alles, was kein Link ist, bleibt auch.
    /// </summary>
    public async Task<RemoteEntry> WithTargetAsync(RemoteEntry entry)
    {
        if (!entry.IsLink || entry.LinkTarget is { Length: > 0 } || _files is not { } files)
        {
            return entry;
        }

        try
        {
            return await files.ReadLinkAsync(PathOf(entry)) is { Length: > 0 } target ? entry with { LinkTarget = target } : entry;
        }
        catch (RemoteFileException)
        {
            return entry;
        }
    }

    /// <summary>
    /// Zeigt, wohin ein Link zeigt: wechselt in den Ordner des Ziels und wählt es aus (<see cref="ShowPathAsync"/>).
    /// <c>false</c>, wenn der Eintrag kein Ziel nennt, ins Leere zeigt oder die Ansicht es nicht erreicht.
    /// </summary>
    public async Task<bool> GoToTargetAsync(RemoteEntry entry)
        => entry is { IsDangling: false } && TargetOf(entry) is { } path && await ShowPathAsync(path);

    /// <summary>
    /// Die Einträge, die sich übertragen lassen, wie ein anderes Programm sie bekommt (Kopieren, Ziehen aus der
    /// Liste): mit vollem Pfad – er steht damit fest, auch wenn der Browser weiterzieht – und dem Namen, den das
    /// Backend ihnen auf diesem Rechner gibt.
    /// </summary>
    public IReadOnlyList<DropItem> DropItems(IEnumerable<RemoteEntry> entries)
    {
        if (_files is not { } files)
        {
            return [];
        }

        var items = new List<DropItem>();
        foreach (var entry in entries.Where(entry => entry.IsTransferable))
        {
            var path = PathOf(entry);
            if (Path.GetFileName(files.LocalTarget(path, Path.GetTempPath())) is { Length: > 0 } name)
            {
                items.Add(new DropItem(path, name, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size, entry.Modified));
            }
        }

        return items;
    }

    /// <summary>Welche der Namen es im aktuellen Verzeichnis schon gibt – für die Frage „überschreiben?“ vor einem Upload.</summary>
    public IReadOnlyList<string> ExistingNames(IEnumerable<string> names)
        => [.. names.Where(name => _all.Any(entry => entry.Name == name))];

    /// <summary>Alle Namen im aktuellen Verzeichnis – wer etwas daneben legen will, sucht sich damit einen freien.</summary>
    public HashSet<string> TakenNames() => _all.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Was ein Download dieser Einträge nach <paramref name="localDirectory"/> überschriebe, weil es dort schon
    /// liegt – für die Frage vor dem Start. Genannt wird der lokale Name; ihn legt das Backend fest.
    /// </summary>
    public IReadOnlyList<string> ExistingLocally(IEnumerable<RemoteEntry> entries, string localDirectory)
        => _files is not { } files
            ? []
            : [.. entries
                .Where(entry => entry.IsTransferable)
                .Select(entry => files.LocalTarget(PathOf(entry), localDirectory))
                .OfType<string>()
                .Where(target => File.Exists(target) || Directory.Exists(target))
                .Select(target => Path.GetFileName(target))];

    /// <summary>Legt im aktuellen Verzeichnis einen Ordner an. <c>false</c>, wenn das scheitert; der Grund steht in <see cref="Error"/>.</summary>
    public Task<bool> CreateFolderAsync(string name)
        => ChangeAsync(files => files.CreateDirectoryAsync(RemotePath.Combine(CurrentPath, name.Trim())), name);

    /// <summary>Legt im aktuellen Verzeichnis eine leere Datei an. <c>false</c>, wenn das scheitert – auch, weil es den Namen schon gibt.</summary>
    public Task<bool> CreateFileAsync(string name)
        => ChangeAsync(files => files.CreateFileAsync(RemotePath.Combine(CurrentPath, name.Trim())), name);

    /// <summary>
    /// Legt im aktuellen Verzeichnis einen symbolischen Link auf einen Eintrag an. <c>false</c>, wenn das scheitert –
    /// auch, weil es den Namen schon gibt. Unter Windows braucht das ein Recht, das meist nur Administratoren haben;
    /// ohne sagt der Server nur „Permission denied“ – der Satz dazu nennt den Grund.
    /// </summary>
    public Task<bool> CreateLinkAsync(RemoteEntry target, string name)
        => ChangeAsync(
            files => files.CreateLinkAsync(RemotePath.Combine(CurrentPath, name.Trim()), PathOf(target)),
            name,
            error => error.Code == "permission_denied" && IsWindowsServer ? L.Get("SftpLinkDeniedWindows") : null);

    /// <summary>Gibt einem Eintrag einen neuen Namen im selben Verzeichnis.</summary>
    public async Task<bool> RenameAsync(RemoteEntry entry, string newName)
    {
        var (from, to) = (PathOf(entry), RemotePath.Combine(CurrentPath, newName.Trim()));
        var done = await ChangeAsync(files => files.RenameAsync(from, to), newName);
        if (done)
        {
            Remember([(from, to)]);
        }

        return done;
    }

    /// <summary>Setzt die Zugriffsrechte eines Eintrags (wie <c>chmod</c>) und lädt die Liste neu.</summary>
    public Task<bool> SetPermissionsAsync(RemoteEntry entry, int mode)
        => ChangeAsync(files => files.SetPermissionsAsync(PathOf(entry), mode));

    /// <summary>
    /// Übernimmt, was im Eigenschaften-Dialog geändert wurde: Besitzer und Gruppe (wie <c>chown</c>), dann die Rechte
    /// (wie <c>chmod</c>) – in dieser Reihenfolge, weil ein Wechsel des Besitzers setuid und setgid löschen kann.
    /// </summary>
    public Task<bool> ApplyAsync(RemoteEntry entry, PropertyChange change)
        => ChangeAsync(async files =>
        {
            var path = PathOf(entry);
            if (change.Owner is not null || change.Group is not null)
            {
                await files.SetOwnerAsync(path, change.Owner, change.Group, change.Recursive);
            }

            if (change.Mode is { } mode)
            {
                await files.SetPermissionsAsync(path, mode, change.Recursive);
            }
        });

    /// <summary>Löscht die Einträge, Ordner samt Inhalt. Bricht beim ersten Fehler ab; was bis dahin gelöscht ist, bleibt gelöscht.</summary>
    public Task<bool> DeleteAsync(IReadOnlyList<RemoteEntry> entries)
        => ChangeAsync(async files =>
        {
            foreach (var entry in entries)
            {
                await files.DeleteAsync(PathOf(entry));
            }
        });

    /// <summary>Stellt lokale Dateien und Ordner zum Hochladen ins aktuelle Verzeichnis in die Warteschlange.</summary>
    /// <param name="rule">
    /// Was mit dem geschieht, was es hier schon gibt – einmal für alles: überschreiben, stehen lassen, was gleich
    /// oder nicht älter ist (das entscheidet das Backend je Datei, auch in Ordnern), oder das Neue umbenennen.
    /// </param>
    public void Upload(IEnumerable<string> localPaths, ExistingRule rule = ExistingRule.Overwrite, IReadOnlyList<string>? exclude = null)
    {
        if (_files is not { } files)
        {
            return;
        }

        var taken = TakenNames();
        foreach (var path in localPaths)
        {
            // „Umbenennen“: Was es schon gibt, bleibt; das Neue kommt daneben, unter einem freien Namen.
            string? other = null;
            if (rule == ExistingRule.Rename && Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name && taken.Contains(name))
            {
                other = RenamePlan.CopyName(name, taken);
                taken.Add(other);
            }

            files.Upload(path, CurrentPath, other, rule.Keep(), exclude);
        }
    }

    /// <summary>
    /// Stellt Einträge der aktuellen Liste zum Herunterladen in einen lokalen Ordner in die Warteschlange – ohne
    /// die, die sich nicht übertragen lassen (<see cref="RemoteEntry.IsTransferable"/>).
    /// </summary>
    /// <param name="rule">Wie beim Hochladen: was mit dem geschieht, was es im lokalen Ordner schon gibt.</param>
    public void Download(IEnumerable<RemoteEntry> entries, string localDirectory, ExistingRule rule = ExistingRule.Overwrite, IReadOnlyList<string>? exclude = null)
    {
        if (_files is not { } files)
        {
            return;
        }

        HashSet<string>? taken = null;
        foreach (var entry in entries.Where(entry => entry.IsTransferable))
        {
            string? other = null;
            if (rule == ExistingRule.Rename
                && files.LocalTarget(PathOf(entry), localDirectory) is { } target
                && (File.Exists(target) || Directory.Exists(target)))
            {
                // Was im Ordner liegt, einmal lesen; unter Windows unterscheiden sich Namen nicht nach der Schreibung.
                taken ??= NamesIn(localDirectory);
                other = RenamePlan.CopyName(Path.GetFileName(target), taken);
                taken.Add(other);
            }

            files.Download(PathOf(entry), localDirectory, keep: rule.Keep(), localName: other, exclude: exclude);
        }
    }

    /// <summary>
    /// Was die Ausschlüsse in den gewählten Ordnern des Servers auslassen würden – für die Rückfrage vor dem
    /// Transfer. Gefragt wird der Server je Ordner einmal; in einen ausgelassenen Ordner sieht er nicht hinein.
    /// Antwortet er nicht, gibt es für diesen Ordner keine Treffer: Der Transfer selbst meldet, was nicht geht.
    /// </summary>
    public async Task<IReadOnlyList<ExcludedItem>> FindExcludedAsync(TransferExcludes rules, IEnumerable<RemoteEntry> entries)
    {
        List<ExcludedItem> found = [];
        if (_files is not { } files || rules.IsEmpty)
        {
            return found;
        }

        foreach (var entry in entries.Where(entry => entry is { IsDirectory: true, IsTransferable: true }))
        {
            var root = PathOf(entry).TrimEnd('/');
            try
            {
                foreach (var skipped in await files.TreeAsync(root, rules.Patterns, excluded: true))
                {
                    // Der Kern hat nach denselben Mustern ausgelassen; hier steht nur noch, nach welchen – der
                    // Eintrag steht unter jedem, auf das er passt.
                    if (skipped.RemotePath.Length > root.Length + 1)
                    {
                        var relative = skipped.RemotePath[(root.Length + 1)..];
                        found.AddRange(rules.PatternsFor(relative).Select(pattern => new ExcludedItem(pattern, $"{entry.Name}/{relative}", skipped.IsDirectory)));
                    }
                }
            }
            catch (RemoteFileException)
            {
            }
        }

        return found;
    }

    private static HashSet<string> NamesIn(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Select(path => Path.GetFileName(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Führt eine Änderung aus und lädt danach neu – auch nach einem Fehler, denn ein Teil kann schon geschehen sein.
    /// <summary>
    /// Wie viel Platz ein Ordner samt Inhalt auf dem Server belegt – als Text für die Eigenschaften: die Größe, oder
    /// warum sie sich nicht ermitteln ließ (kein Konto für Befehle, der Server nennt keine Zahl). Gefragt wird nur
    /// auf Wunsch: Bei einem großen Baum rechnet der Server eine Weile.
    /// </summary>
    public async Task<string> FolderSizeTextAsync(RemoteEntry entry)
    {
        if (_files is not { } files)
        {
            return L.Get("FolderSizeUnknown");
        }

        try
        {
            return await files.FolderSizeAsync(PathOf(entry)) is { } size
                ? $"{Format.FileSize(size, isDirectory: false)}  ({size.ToString("N0", L.Culture)} B)"
                : L.Get("FolderSizeUnknown");
        }
        catch (RemoteFileException e)
        {
            return Describe(e);
        }
    }

    // `describe` nennt für einen Fehler einen eigenen Satz; ohne (oder bei `null`) gilt der gewöhnliche.
    private async Task<bool> ChangeAsync(Func<IRemoteFileSystem, Task> change, string? newName = null, Func<RemoteFileException, string?>? describe = null)
    {
        if (_files is not { } files)
        {
            return false;
        }

        if (newName is not null && !RemotePath.IsValidName(newName.Trim()))
        {
            Error = L.Get("SftpInvalidName");
            return false;
        }

        string? failure = null;
        try
        {
            await change(files);
        }
        catch (RemoteFileException e)
        {
            failure = describe?.Invoke(e) ?? Describe(e);
        }

        // Kann die Liste nicht neu geladen werden, ist das der wichtigere Fehler – er bleibt stehen.
        if (await ShowAsync(CurrentPath) && failure is not null)
        {
            Error = failure;
        }

        return failure is null;
    }

    // Die Meldung des Servers ist englisch; sie steht als Einzelheit hinter dem übersetzten Satz.
    private static string Describe(RemoteFileException error) => FailureText.Describe(error.Code, error.Message);

    // Erst laden, dann umschalten: Schlägt das Laden fehl, bleibt die bisherige Ansicht samt Pfad stehen.
    private async Task<bool> ShowAsync(string path)
    {
        if (_files is not { } files)
        {
            return false;
        }

        IReadOnlyList<RemoteEntry> entries;
        try
        {
            entries = await ListAsync(files, path);
        }
        catch (RemoteFileException e)
        {
            Error = Describe(e);
            return false;
        }

        Error = null;
        var moved = path != CurrentPath;
        CurrentPath = path;
        _all = entries;
        if (moved && IsFiltered)
        {
            // Der Filter galt dem Verzeichnis davor; das Leeren füllt die Liste gleich mit.
            Filter = string.Empty;
        }
        else
        {
            Fill();
        }

        Segments.Clear();
        foreach (var segment in RemotePath.Segments(path))
        {
            Segments.Add(segment);
        }

        GoUpCommand.NotifyCanExecuteChanged();
        await SyncTreeAsync(path, entries);
        return true;
    }

    /// <summary>
    /// Die Einträge eines Verzeichnisses für das Ergänzen im Terminal – ohne den Browser zu bewegen. Zeigt er das
    /// Verzeichnis gerade, kommt die Antwort aus dem, was er schon gelesen hat. <c>null</c>, wenn es keine
    /// Dateiverbindung gibt oder der Server nicht antwortet.
    /// </summary>
    internal async Task<IReadOnlyList<RemoteEntry>?> NamesAsync(string path)
    {
        if (_files is not { } files)
        {
            return null;
        }

        if (CurrentPath == path && _all.Count > 0)
        {
            return _all;
        }

        try
        {
            return await ListAsync(files, path);
        }
        catch (RemoteFileException)
        {
            return null;
        }
    }

    // Antwortet der Server auf eine Anfrage nicht mehr, hängt oft nur der Kanal für Dateien (ein totes Netzlaufwerk
    // auf dem Server) – jede weitere Anfrage bliebe darauf ebenfalls liegen. Die Verbindung selbst steht: Ein neuer
    // Kanal darauf macht den Dateibrowser wieder benutzbar, ohne neue Anmeldung.
    private static async Task<IReadOnlyList<RemoteEntry>> ListAsync(IRemoteFileSystem files, string path)
    {
        try
        {
            return await files.ListAsync(path);
        }
        catch (RemoteFileException e) when (e.Code == "timeout")
        {
            // Dieses Verzeichnis antwortet nicht – es gleich noch einmal zu versuchen, hieße nur wieder warten.
            // Der neue Kanal ist für das, was der Benutzer als Nächstes tut.
            try
            {
                await files.ReopenAsync();
            }
            catch (RemoteFileException)
            {
            }

            throw;
        }
        catch (RemoteFileException e) when (e.Code == "connection")
        {
            // Der Kanal ist zu: neu öffnen und noch einmal fragen. Geht das nicht, ist die Verbindung selbst weg.
            await files.ReopenAsync();
            return await files.ListAsync(path);
        }
    }

    // Füllt die Liste aus dem, was der Server geliefert hat – ohne die versteckten Einträge, wenn sie aus sind.
    private void Fill()
    {
        // Nach jedem Laden: Ein neu geöffneter Kanal (nach einem Abriss) ist wieder der des Benutzers.
        IsRoot = _files?.IsElevated ?? false;
        SelectedEntry = null;
        var shown = _all.Where(entry => ShowHidden || !entry.IsHidden).ToList();
        Entries = new ObservableCollection<RemoteEntry>(Sorted(shown.Where(Matches)));

        // „Leer“ meint das Verzeichnis – filtert der Benutzer alles weg, sagt das die Fußzeile.
        IsEmpty = shown.Count == 0;
        CountText = (IsFiltered, Entries.Count) switch
        {
            (true, var count) => L.Get("EntryCountFiltered", count, shown.Count),
            (false, 1) => L.Get("EntryCountOne"),
            (false, var count) => L.Get("EntryCount", count),
        };
    }

    // Verglichen wird mit dem Namen, wie er angezeigt wird – so findet man, was man sieht.
    private bool Matches(RemoteEntry entry)
        => !IsFiltered || entry.DisplayName.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) || entry.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    partial void OnFilterChanged(string value) => Fill();

    // Ordner stehen immer vor den Dateien. Nach Name ist die Reihenfolge des Servers (natürlich sortiert, `Format.NameOrder`);
    // nach jeder anderen Spalte bleibt sie bei gleichen Werten erhalten – OrderBy ist stabil. Rechte ohne Angabe
    // stehen vor allen anderen, Besitzer und Gruppe (der Name oder die Nummer) und die Art wie Namen verglichen.
    // Ein Link auf einen Ordner steht bei den Ordnern, weil man ihn betreten kann – nur nach der Art nicht: Dort
    // stehen vorn die echten Ordner, und alle Links beieinander (auch absteigend).
    private IEnumerable<RemoteEntry> Sorted(IEnumerable<RemoteEntry> entries)
    {
        Func<RemoteEntry, bool> first = Sort == FileSort.Type ? entry => entry.IsDirectory && !entry.IsLink : entry => entry.IsDirectory;
        var foldersFirst = (SortDescending && Sort == FileSort.Name ? entries.Reverse() : entries).OrderByDescending(first);
        return (Sort, SortDescending) switch
        {
            (FileSort.Size, false) => foldersFirst.ThenBy(entry => entry.Size),
            (FileSort.Size, true) => foldersFirst.ThenByDescending(entry => entry.Size),
            (FileSort.Modified, false) => foldersFirst.ThenBy(entry => entry.Modified),
            (FileSort.Modified, true) => foldersFirst.ThenByDescending(entry => entry.Modified),
            (FileSort.Permissions, false) => foldersFirst.ThenBy(entry => entry.Permissions),
            (FileSort.Permissions, true) => foldersFirst.ThenByDescending(entry => entry.Permissions),
            (FileSort.Owner, false) => foldersFirst.ThenBy(entry => entry.Owner ?? string.Empty, Format.NameOrder),
            (FileSort.Owner, true) => foldersFirst.ThenByDescending(entry => entry.Owner ?? string.Empty, Format.NameOrder),
            (FileSort.Group, false) => foldersFirst.ThenBy(entry => entry.Group ?? string.Empty, Format.NameOrder),
            (FileSort.Group, true) => foldersFirst.ThenByDescending(entry => entry.Group ?? string.Empty, Format.NameOrder),
            (FileSort.Type, false) => foldersFirst.ThenBy(Format.EntryType, Format.NameOrder),
            (FileSort.Type, true) => foldersFirst.ThenByDescending(Format.EntryType, Format.NameOrder),
            _ => foldersFirst,
        };
    }

    partial void OnSortChanged(FileSort value) => Fill();

    partial void OnSortDescendingChanged(bool value) => Fill();

    partial void OnShowHiddenChanged(bool value)
    {
        Fill();
        RefilterTree();
    }

    private void HistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }
}
