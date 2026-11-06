using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>Die Spalten einer Dateiliste, in der Reihenfolge, in der sie stehen.</summary>
public enum FileColumn
{
    Name,
    Size,
    Modified,
    Permissions,
    Owner,
    Group,
    Type,
}

/// <summary>
/// Die Spalten einer Dateiliste: wonach sortiert ist, welche zu sehen sind und wie breit sie sind – ein Modell für
/// beide Listen (<see cref="ShellViewModel.RemoteColumns"/>, <see cref="ShellViewModel.LocalColumns"/>), ohne etwas
/// von der Oberfläche zu wissen. Der Name ist immer da; die lokale Liste kennt weder Rechte noch Besitzer noch
/// Gruppe. Jede Änderung meldet <see cref="Changed"/>; wohin sie gespeichert wird, weiß der Besitzer
/// (<see cref="ToSet"/>).
/// </summary>
public sealed class FileColumns : ObservableObject
{
    /// <summary>Schmaler lässt sich keine Spalte ziehen, in Pixeln.</summary>
    public const double MinWidth = 40;

    /// <summary>Breiter auch nicht.</summary>
    public const double MaxWidth = 480;

    private static readonly FileColumn[] All = [.. Enum.GetValues<FileColumn>()];

    private readonly bool _remote;
    private readonly HashSet<FileColumn> _visible;
    private readonly Dictionary<FileColumn, double> _widths = [];
    private FileSort _sort;
    private bool _descending;

    /// <summary>
    /// Baut die Spalten einer Liste aus dem, was gespeichert war. Was darin nicht stimmt (eine Spalte, die es nicht
    /// gibt oder die dieser Liste fehlt, eine Breite außerhalb der Grenzen), wird übergangen – die Datei kann von
    /// Hand geändert sein.
    /// </summary>
    public FileColumns(bool remote, ColumnSet? saved, FileSort sort, bool descending)
    {
        _remote = remote;
        var set = Clean(saved, remote);
        _visible = [];
        foreach (var name in set.Visible)
        {
            if (TryParse(name, out var column))
            {
                _visible.Add(column);
            }
        }

        foreach (var (name, width) in set.Widths)
        {
            if (TryParse(name, out var column))
            {
                _widths[column] = width;
            }
        }

        _sort = Clean(sort, remote);
        _descending = descending;
        Available = BindableList.Of(All.Where(column => IsAvailable(column, remote)));
    }

    /// <summary>Die Spalten des Servers aus den Einstellungen – ohne gespeicherte Wahl gelten die alten Schalter.</summary>
    public static FileColumns Remote(AppSettings settings)
        => new(remote: true, settings.RemoteColumns ?? FromSwitches(settings.ShowFileDates, settings.ShowFilePermissions), settings.FileSort, settings.FileSortDescending);

    /// <summary>Die Spalten der lokalen Liste aus den Einstellungen.</summary>
    public static FileColumns Local(AppSettings settings)
        => new(remote: false, settings.LocalColumns, settings.LocalSort, settings.LocalSortDescending);

    /// <summary>
    /// Etwas an den Spalten hat sich geändert: Sortierung, Sichtbarkeit oder eine Breite. Wer sie speichert oder
    /// eine Liste neu sortiert, hört hier zu.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Die Spalten, die diese Liste kennt, in ihrer Reihenfolge – auch die gerade ausgeblendeten.</summary>
    public IReadOnlyList<FileColumn> Available { get; }

    /// <summary>
    /// Wonach die Liste sortiert ist. Das hängt nicht an der Anzeige: Wer eine Spalte ausblendet, nach der sortiert
    /// ist, behält die Reihenfolge. Eine Spalte, die diese Liste nicht hat, nimmt sie nicht an.
    /// </summary>
    public FileSort Sort
    {
        get => _sort;
        set => SetSort(value, _descending);
    }

    /// <summary>Umgekehrte Reihenfolge: Z–A, groß zuerst, neu zuerst.</summary>
    public bool Descending
    {
        get => _descending;
        set => SetSort(_sort, value);
    }

    /// <summary>Ob die Spalte zu sehen ist. Der Name immer, eine Spalte, die diese Liste nicht hat, nie.</summary>
    public bool IsVisible(FileColumn column) => column == FileColumn.Name || _visible.Contains(column);

    /// <summary>Zeigt die Spalte oder blendet sie aus. Der Name lässt sich nicht ausblenden.</summary>
    public void Toggle(FileColumn column)
    {
        if (column == FileColumn.Name || !IsAvailable(column, _remote))
        {
            return;
        }

        if (!_visible.Remove(column))
        {
            _visible.Add(column);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Die Breite der Spalte in Pixeln: die gewählte, sonst die von Haus aus (<see cref="DefaultWidth"/>).</summary>
    public double WidthOf(FileColumn column) => _widths.TryGetValue(column, out var width) ? width : DefaultWidth(column);

    /// <summary>Merkt die Breite, geklemmt auf <see cref="MinWidth"/> bis <see cref="MaxWidth"/>; keine Zahl wird übergangen.</summary>
    public void SetWidth(FileColumn column, double width)
    {
        if (!IsAvailable(column, _remote) || !double.IsFinite(width))
        {
            return;
        }

        width = Math.Clamp(width, MinWidth, MaxWidth);
        if (_widths.TryGetValue(column, out var before) && before == width)
        {
            return;
        }

        _widths[column] = width;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ein Klick auf einen Spaltenkopf: eine andere Spalte sortiert aufsteigend, dieselbe noch einmal dreht die
    /// Richtung um (wie die Tabelle auf der Startseite).
    /// </summary>
    public void SortBy(FileColumn column)
    {
        if (IsAvailable(column, _remote))
        {
            var sort = SortOf(column);
            SetSort(sort, sort == _sort && !_descending);
        }
    }

    /// <summary>Der Stand zum Speichern (<see cref="AppSettings.RemoteColumns"/>, <see cref="AppSettings.LocalColumns"/>).</summary>
    public ColumnSet ToSet() => new(
        [.. All.Where(column => column != FileColumn.Name && _visible.Contains(column)).Select(NameOf)],
        _widths.ToDictionary(entry => NameOf(entry.Key), entry => entry.Value));

    /// <summary>Wie breit eine Spalte von Haus aus ist, in Pixeln.</summary>
    public static double DefaultWidth(FileColumn column) => column switch
    {
        FileColumn.Name => 240,
        FileColumn.Size => 80,
        FileColumn.Modified => 150,
        FileColumn.Permissions => 100,
        FileColumn.Owner => 90,
        FileColumn.Group => 90,
        FileColumn.Type => 140,
        _ => 100,
    };

    /// <summary>Ob die Liste des Servers (<paramref name="remote"/>) bzw. die lokale diese Spalte hat.</summary>
    public static bool IsAvailable(FileColumn column, bool remote)
        => remote ? Enum.IsDefined(column) : column is FileColumn.Name or FileColumn.Size or FileColumn.Modified or FileColumn.Type;

    /// <summary>Die Sortierung nach dieser Spalte.</summary>
    public static FileSort SortOf(FileColumn column) => column switch
    {
        FileColumn.Size => FileSort.Size,
        FileColumn.Modified => FileSort.Modified,
        FileColumn.Permissions => FileSort.Permissions,
        FileColumn.Owner => FileSort.Owner,
        FileColumn.Group => FileSort.Group,
        FileColumn.Type => FileSort.Type,
        _ => FileSort.Name,
    };

    /// <summary>Die Spalte, nach der sortiert wird – umgekehrt zu <see cref="SortOf"/>.</summary>
    public static FileColumn ColumnOf(FileSort sort) => sort switch
    {
        FileSort.Size => FileColumn.Size,
        FileSort.Modified => FileColumn.Modified,
        FileSort.Permissions => FileColumn.Permissions,
        FileSort.Owner => FileColumn.Owner,
        FileSort.Group => FileColumn.Group,
        FileSort.Type => FileColumn.Type,
        _ => FileColumn.Name,
    };

    /// <summary>
    /// Bringt eine gespeicherte Wahl in Ordnung: Nur bekannte Spalten dieser Liste, ohne den Namen und ohne Doppelte,
    /// in der Reihenfolge der Spalten; Breiten zwischen <see cref="MinWidth"/> und <see cref="MaxWidth"/>, keine
    /// ohne Zahl. Fehlt die Liste der sichtbaren Spalten ganz, gilt die Vorgabe; eine leere ist eine Wahl.
    /// </summary>
    public static ColumnSet Clean(ColumnSet? set, bool remote)
    {
        var visible = set?.Visible is { } names
            ? All.Where(column => column != FileColumn.Name && IsAvailable(column, remote) && names.Contains(NameOf(column), StringComparer.Ordinal))
            : Default(remote);
        var widths = new Dictionary<string, double>();
        foreach (var (name, width) in set?.Widths ?? [])
        {
            if (TryParse(name, out var column) && IsAvailable(column, remote) && double.IsFinite(width))
            {
                widths[NameOf(column)] = Math.Clamp(width, MinWidth, MaxWidth);
            }
        }

        return new ColumnSet([.. visible.Select(NameOf)], widths);
    }

    /// <summary>Eine Sortierung, die diese Liste hat – sonst der Name.</summary>
    public static FileSort Clean(FileSort sort, bool remote)
        => Enum.IsDefined(sort) && IsAvailable(ColumnOf(sort), remote) ? sort : FileSort.Name;

    /// <summary>
    /// Die Spalten einer Datei aus der Zeit vor der Wahl der Spalten: Größe wie immer, dazu Datum und Rechte, wenn
    /// deren Schalter an war.
    /// </summary>
    public static ColumnSet FromSwitches(bool dates, bool permissions)
    {
        var visible = new List<string> { NameOf(FileColumn.Size) };
        if (dates)
        {
            visible.Add(NameOf(FileColumn.Modified));
        }

        if (permissions)
        {
            visible.Add(NameOf(FileColumn.Permissions));
        }

        return new ColumnSet(visible, []);
    }

    // Was eine Liste von Haus aus zeigt: wie bisher – die Größe, und links auch das Datum.
    private static FileColumn[] Default(bool remote) => remote ? [FileColumn.Size] : [FileColumn.Size, FileColumn.Modified];

    private void SetSort(FileSort sort, bool descending)
    {
        if (Clean(sort, _remote) != sort || (sort, descending) == (_sort, _descending))
        {
            return;
        }

        var (before, wasDescending) = (_sort, _descending);
        (_sort, _descending) = (sort, descending);
        if (sort != before)
        {
            OnPropertyChanged(nameof(Sort));
        }

        if (descending != wasDescending)
        {
            OnPropertyChanged(nameof(Descending));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Die Namen von Hand statt aus der Aufzählung: Sie stehen in der Einstellungsdatei und dürfen sich nie ändern.
    private static string NameOf(FileColumn column) => column switch
    {
        FileColumn.Name => nameof(FileColumn.Name),
        FileColumn.Size => nameof(FileColumn.Size),
        FileColumn.Modified => nameof(FileColumn.Modified),
        FileColumn.Permissions => nameof(FileColumn.Permissions),
        FileColumn.Owner => nameof(FileColumn.Owner),
        FileColumn.Group => nameof(FileColumn.Group),
        FileColumn.Type => nameof(FileColumn.Type),
        _ => string.Empty,
    };

    // Nur der Name genau so – kein „owner“, keine Nummer wie bei Enum.TryParse.
    private static bool TryParse(string? name, out FileColumn column)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                column = candidate;
                return true;
            }
        }

        column = FileColumn.Name;
        return false;
    }
}
