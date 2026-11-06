using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>Ein Eintrag im lokalen Ordner – oder ein Laufwerk, ganz oben.</summary>
public sealed record LocalEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset? Modified, bool IsDrive = false)
{
    public string SizeText => IsDirectory ? string.Empty : Format.FileSize(Size, isDirectory: false);

    public string ModifiedText => Modified is { } time ? Format.DateAndTime(time) : string.Empty;

    /// <summary>Die Art für die Spalte „Typ“ (<see cref="Format.EntryType(LocalEntry)"/>).</summary>
    public string TypeText => Format.EntryType(this);

    /// <summary>
    /// Der Name, wie er angezeigt wird (<see cref="Format.Visible"/>): Eine Datei kann von einem Server stammen oder
    /// aus einem Archiv – Zeichen, die die Schreibrichtung umkehren, sollen sie nicht als etwas anderes ausgeben.
    /// </summary>
    public string DisplayName => Format.Visible(Name);

    /// <summary>Der Name – die Liste liest ihn Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Der lokale Ordner neben dem Dateibrowser des Servers (Ansicht „Zwei Spalten“): eine Liste, hinein und hinaus –
/// was hier gewählt ist, lädt die Ansicht zum Server hoch, und was vom Server kommt, landet im angezeigten Ordner.
/// Dazu das Nötigste zum Aufräumen: neuer Ordner, umbenennen, löschen (über <see cref="Recycle"/> in den
/// Papierkorb, wo es einen gibt).
/// </summary>
public sealed partial class LocalBrowserViewModel : ObservableObject
{
    /// <summary>Ganz oben: die Laufwerke dieses Rechners.</summary>
    public const string Computer = "";

    public LocalBrowserViewModel(string? start = null)
    {
        Show(start is { Length: > 0 } && Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Was die Liste zeigt – bei jedem Wechsel eine neue Liste, wie bei der des Servers (<see cref="SftpBrowserViewModel.Entries"/>).</summary>
    public ObservableCollection<LocalEntry> Entries
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

    /// <summary>Der angezeigte Ordner; leer für die Liste der Laufwerke.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(IsFolder))]
    public partial string CurrentPath { get; private set; } = Computer;

    /// <summary>Warum der letzte Wechsel nicht ging; <c>null</c>, wenn er ging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Ob Verstecktes und Systemdateien gezeigt werden.</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>Wonach die Liste sortiert ist – wie die des Servers; Ordner stehen vor den Dateien, nur nach der Art stehen Ordner-Verweise bei den Verweisen.</summary>
    [ObservableProperty]
    public partial FileSort Sort { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>
    /// Legt Dateien und Ordner in den Papierkorb und sagt, ob das ging. Setzt die Oberfläche, wo es einen
    /// Papierkorb gibt; ohne sie löscht <see cref="Delete"/> endgültig.
    /// </summary>
    public Func<IReadOnlyList<string>, bool>? Recycle { get; set; }

    public bool HasError => Error is not null;

    public bool CanGoUp => CurrentPath != Computer;

    /// <summary>Ein Ordner ist zu sehen (nicht die Laufwerke) – dorthin lässt sich etwas herunterladen.</summary>
    public bool IsFolder => CurrentPath != Computer;

    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    /// <summary>Ob es einen Ordner davor gibt (Alt+←) – wie im Dateibrowser des Servers.</summary>
    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Wechselt in einen Ordner. Scheitert das, bleibt die Ansicht, wo sie war, und <see cref="Error"/> sagt, warum. Ein neuer Weg verwirft das „vor“; derselbe Ordner noch einmal ist kein Schritt.</summary>
    public bool NavigateTo(string path)
    {
        var previous = CurrentPath;
        if (!Show(path))
        {
            return false;
        }

        if (CurrentPath != previous)
        {
            _back.Push(previous);
            _forward.Clear();
            HistoryChanged();
        }

        return true;
    }

    /// <summary>Zum Ordner davor.</summary>
    public void GoBack()
    {
        if (CanGoBack)
        {
            var previous = CurrentPath;
            if (Show(_back.Peek()))
            {
                _back.Pop();
                _forward.Push(previous);
                HistoryChanged();
            }
        }
    }

    /// <summary>Wieder vor, nach einem Schritt zurück.</summary>
    public void GoForward()
    {
        if (CanGoForward)
        {
            var previous = CurrentPath;
            if (Show(_forward.Peek()))
            {
                _forward.Pop();
                _back.Push(previous);
                HistoryChanged();
            }
        }
    }

    private void HistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private bool Show(string path)
    {
        try
        {
            var (target, entries) = path == Computer ? (Computer, Drives()) : List(Path.GetFullPath(path));
            Entries = new ObservableCollection<LocalEntry>(entries);

            CurrentPath = target;
            Error = null;
            CountText = Entries.Count == 1 ? L.Get("EntryCountOne") : L.Get("EntryCount", Entries.Count);
            Segments.Clear();
            foreach (var segment in SegmentsOf(target))
            {
                Segments.Add(segment);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Error = e.Message;
            return false;
        }
    }

    /// <summary>Eine Ebene höher; über dem obersten Ordner eines Laufwerks stehen die Laufwerke.</summary>
    public void GoUp()
    {
        if (CanGoUp)
        {
            NavigateTo(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(CurrentPath)) ?? Computer);
        }
    }

    /// <summary>Liest den angezeigten Ordner neu – etwa nachdem ein Download dort angekommen ist.</summary>
    public void Refresh() => NavigateTo(CurrentPath);

    /// <summary>Öffnet einen Ordner oder ein Laufwerk; eine Datei bleibt, wo sie ist.</summary>
    public void Open(LocalEntry? entry)
    {
        if (entry is { IsDirectory: true })
        {
            NavigateTo(entry.FullPath);
        }
    }

    /// <summary>
    /// Ob sich der Text als Name einer Datei oder eines Ordners auf diesem Rechner eignet: nicht leer, kein Pfad,
    /// nicht <c>.</c> oder <c>..</c>, nichts, was das Dateisystem hier nicht als Name kennt.
    /// </summary>
    public static bool IsValidName(string name)
        => name.Trim() is { Length: > 0 } trimmed
            && trimmed is not ("." or "..")
            && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !trimmed.Contains(Path.DirectorySeparatorChar)
            && !trimmed.Contains(Path.AltDirectorySeparatorChar)
            // Auch unter Linux kein `\`: Dort wäre es ein Name, unter Windows (und nach einem Transfer dorthin) ein Pfad.
            && !trimmed.Contains('\\');

    /// <summary>Legt im angezeigten Ordner einen Ordner an. Scheitert das, sagt <see cref="Error"/>, warum.</summary>
    public bool CreateFolder(string name)
        => IsFolder && IsValidName(name) && Change(() =>
        {
            var path = Path.Combine(CurrentPath, name.Trim());
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException(L.Get("LocalNameTaken", name.Trim()));
            }

            Directory.CreateDirectory(path);
        });

    /// <summary>Benennt einen Eintrag um – im selben Ordner; nie über etwas hinweg, das schon so heißt.</summary>
    public bool Rename(LocalEntry entry, string name)
        => !entry.IsDrive && IsValidName(name) && Change(() =>
        {
            var target = Path.Combine(Path.GetDirectoryName(entry.FullPath) ?? CurrentPath, name.Trim());

            // Nur die Schreibweise ändern (a.txt → A.txt) ist erlaubt, obwohl es „das Ziel“ dann schon gibt.
            var sameEntry = string.Equals(Path.GetFullPath(target), Path.GetFullPath(entry.FullPath), StringComparison.OrdinalIgnoreCase);
            if (!sameEntry && (Directory.Exists(target) || File.Exists(target)))
            {
                throw new IOException(L.Get("LocalNameTaken", name.Trim()));
            }

            if (entry.IsDirectory)
            {
                Directory.Move(entry.FullPath, target);
            }
            else
            {
                File.Move(entry.FullPath, target);
            }
        });

    /// <summary>
    /// Löscht Einträge: in den Papierkorb, wenn die Oberfläche einen kennt (<see cref="Recycle"/>), sonst endgültig –
    /// Ordner samt Inhalt (<paramref name="forever"/>: auch dort endgültig, Umschalt+Entf). Laufwerke nie.
    /// </summary>
    public bool Delete(IEnumerable<LocalEntry> entries, bool forever = false)
    {
        var wanted = entries.Where(entry => !entry.IsDrive).ToList();
        return wanted.Count > 0 && Change(() =>
        {
            if (!forever && Recycle is { } recycle)
            {
                if (!recycle([.. wanted.Select(entry => entry.FullPath)]))
                {
                    throw new IOException(L.Get("LocalDeleteFailed"));
                }

                return;
            }

            foreach (var entry in wanted)
            {
                if (entry.IsDirectory)
                {
                    Directory.Delete(entry.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(entry.FullPath);
                }
            }
        });
    }

    // Etwas am Ordner ändern und ihn danach neu lesen – auch wenn es scheiterte: Ein Teil kann geschehen sein.
    private bool Change(Action action)
    {
        string? failure = null;
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            failure = e.Message;
        }

        Refresh();
        if (failure is not null)
        {
            Error = failure;
        }

        return failure is null;
    }

    partial void OnShowHiddenChanged(bool value) => Refresh();

    partial void OnSortChanged(FileSort value) => Refresh();

    partial void OnSortDescendingChanged(bool value) => Refresh();

    private (string Path, List<LocalEntry> Entries) List(string path)
    {
        var directory = new DirectoryInfo(path);
        var byName = directory
            .EnumerateFileSystemInfos()
            .Where(info => ShowHidden || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(info => new LocalEntry(
                info.Name,
                info.FullName,
                info is DirectoryInfo,
                info is FileInfo file ? file.Length : 0,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
            .OrderBy(entry => entry.Name, Format.NameOrder);
        return (directory.FullName, [.. Sorted(byName)]);
    }

    // Ordner stehen immer vor den Dateien; bei Gleichstand (Größe, Zeit, Art) entscheidet der Name. Rechte, Besitzer
    // und Gruppe gibt es hier nicht – eine solche Sortierung nimmt die Liste nicht an (FileColumns), und käme sie doch
    // an, bliebe es beim Namen.
    private IEnumerable<LocalEntry> Sorted(IEnumerable<LocalEntry> byName)
    {
        var foldersFirst = (SortDescending && Sort == FileSort.Name ? byName.Reverse() : byName).OrderByDescending(entry => entry.IsDirectory);
        return (Sort, SortDescending) switch
        {
            (FileSort.Size, false) => foldersFirst.ThenBy(entry => entry.Size),
            (FileSort.Size, true) => foldersFirst.ThenByDescending(entry => entry.Size),
            (FileSort.Modified, false) => foldersFirst.ThenBy(entry => entry.Modified),
            (FileSort.Modified, true) => foldersFirst.ThenByDescending(entry => entry.Modified),
            (FileSort.Type, false) => foldersFirst.ThenBy(Format.EntryType, Format.NameOrder),
            (FileSort.Type, true) => foldersFirst.ThenByDescending(Format.EntryType, Format.NameOrder),
            _ => foldersFirst,
        };
    }

    private static List<LocalEntry> Drives()
        => [.. DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new LocalEntry(drive.Name, drive.RootDirectory.FullName, IsDirectory: true, 0, null, IsDrive: true))];

    // Der Weg zum Ordner als Brotkrumen: vorn „Dieser Rechner“, dann das Laufwerk (oder „/“) und jeder Ordner.
    private static List<PathSegment> SegmentsOf(string path)
    {
        var segments = new List<PathSegment> { new(L.Get("LocalComputer"), Computer) };
        if (path == Computer)
        {
            return segments;
        }

        var parts = new List<string>();
        for (var current = Path.TrimEndingDirectorySeparator(path); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
        {
            parts.Add(current);
        }

        parts.Reverse();
        segments.AddRange(parts.Select(part => new PathSegment(
            Path.GetFileName(part) is { Length: > 0 } name ? name : Path.GetPathRoot(part) ?? part,
            part)));
        return segments;
    }
}
