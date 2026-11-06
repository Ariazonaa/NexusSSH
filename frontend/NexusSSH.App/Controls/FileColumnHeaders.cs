using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Controls;

/// <summary>
/// Die Kopfzeile über einer Dateiliste (Server und lokal), gebunden an <see cref="FileColumns"/>: ein Klick auf einen
/// Kopf sortiert, ein Rechtsklick wählt die Spalten, an der linken Kante einer Spalte zieht man ihre Breite. Die
/// Spalten stehen wie in den Zeilen: vorn ein Platz für das Symbol, dann der Name über den Rest, danach die übrigen
/// Spalten mit fester Breite, ganz rechts die letzte – deshalb sitzt der Griff links an der Spalte, und ein Zug nach
/// links macht sie breiter.
/// <para>
/// Beim Ziehen ändert sich nur der Kopf; die Breite wird erst beim Loslassen gemerkt (<see cref="FileColumns.SetWidth"/>
/// schreibt jedes Mal die Einstellungen), und die Zeilen der Liste ziehen schon dabei nach (<see cref="WidthDragged"/>). Die Tasten gehen im selben
/// Takt (Pfeil links = breiter, rechts = schmaler, je 8 Pixel; Pos1 = Vorgabe).
/// </para>
/// Reines Code-Steuerelement ohne XAML, wie <see cref="PaneSplitter"/>.
/// </summary>
public sealed partial class FileColumnHeaders : UserControl
{
    private readonly Grid _grid = new();
    private readonly Dictionary<FileColumn, Cell> _cells = [];
    private readonly MenuFlyout _menu = new();
    private FileColumns? _columns;

    public FileColumnHeaders()
    {
        Content = _grid;
        _grid.ContextFlyout = _menu;
        _menu.Opening += (_, _) => FillMenu();
        AutomationProperties.SetName(this, L.Get("ColumnsMenu"));
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach();
    }

    /// <summary>Breite des Platzes für das Symbol vor dem Namen – wie in der Zeilenvorlage der Liste.</summary>
    public double LeadWidth { get; set; } = 14;

    /// <summary>Abstand zwischen den Spalten – wie in der Zeilenvorlage der Liste.</summary>
    public double Spacing { get; set; } = 8;

    /// <summary>Eine Breite wird gerade gezogen (noch nicht gemerkt): die Liste darunter zeigt sie schon in ihren Zeilen.</summary>
    public event EventHandler<(FileColumn Column, double Width)>? WidthDragged;

    /// <summary>Die Spalten, die dieser Kopf zeigt; <c>null</c>, solange es keine gibt.</summary>
    public FileColumns? Columns
    {
        get => _columns;
        set
        {
            if (ReferenceEquals(_columns, value))
            {
                return;
            }

            Detach();
            _columns = value;
            Build();
            if (IsLoaded)
            {
                Attach();
            }
        }
    }

    /// <summary>
    /// Für den Selbsttest: jeden Kopf einmal drücken (wie UI Automation es tut), das Menü der Spalten füllen, eine
    /// Spalte ein- und ausblenden und eine Breite setzen – danach steht alles wieder, wie es war.
    /// </summary>
    internal void ExerciseForSelfTest()
    {
        if (_columns is not { } columns)
        {
            return;
        }

        var (sort, descending) = (columns.Sort, columns.Descending);
        foreach (var cell in _cells.Values)
        {
            if (FrameworkElementAutomationPeer.FromElement(cell.Button)?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            {
                throw new InvalidOperationException("self-test: a column header cannot be invoked");
            }

            invoke.Invoke();
        }

        FillMenu();
        if (_menu.Items.Count == 0)
        {
            throw new InvalidOperationException("self-test: the column menu is empty");
        }

        if (columns.Available.FirstOrDefault(column => column != FileColumn.Name) is var other && other != FileColumn.Name)
        {
            var before = columns.WidthOf(other);
            var visible = columns.IsVisible(other);
            columns.Toggle(other);
            columns.Toggle(other);
            columns.SetWidth(other, before + 10);
            if (columns.IsVisible(other) != visible || columns.WidthOf(other) != before + 10)
            {
                throw new InvalidOperationException("self-test: a column did not take its width");
            }

            columns.SetWidth(other, before);
        }

        columns.Sort = sort;
        columns.Descending = descending;
    }

    private void Attach()
    {
        if (_columns is { } columns)
        {
            columns.Changed -= OnColumnsChanged;
            columns.Changed += OnColumnsChanged;
            Refresh();
        }
    }

    private void Detach()
    {
        if (_columns is { } columns)
        {
            columns.Changed -= OnColumnsChanged;
        }
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => Refresh();

    // Je Spalte einmal ein Kopf mit Griff; was zu sehen ist und wie breit, setzt Refresh.
    private void Build()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _cells.Clear();
        // Der Abstand steht links an jeder Spalte statt als ColumnSpacing: Eine ausgeblendete Spalte kostet sonst ihre Lücke.
        if (_columns is not { } columns)
        {
            return;
        }

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeadWidth) });
        foreach (var column in columns.Available)
        {
            var cell = new Cell(column);
            var index = _grid.ColumnDefinitions.Count;
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = column == FileColumn.Name ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(cell.Root, index);
            cell.Root.Margin = new Thickness(Spacing, 0, 0, 0);
            _grid.Children.Add(cell.Root);
            cell.Button.Click += (_, _) => _columns?.SortBy(column);
            if (cell.Handle is { } handle)
            {
                Wire(cell, handle);
            }

            _cells[column] = cell;
        }

        Refresh();
    }

    // Zieht man an der linken Kante, zählt der Zug nach links als „breiter“. Gemerkt wird erst beim Loslassen.
    private void Wire(Cell cell, PaneSplitter handle)
    {
        var start = 0.0;
        handle.KeyStep = 8;
        handle.DragStarted += (_, _) => start = _columns?.WidthOf(cell.Column) ?? 0;
        handle.DragDelta += (_, delta) =>
        {
            cell.Live = Math.Clamp(start - delta, FileColumns.MinWidth, FileColumns.MaxWidth);
            cell.Root.Width = cell.Live.Value;
            WidthDragged?.Invoke(this, (cell.Column, cell.Live.Value));
        };
        handle.DragCompleted += (_, _) =>
        {
            if (cell.Live is { } width)
            {
                cell.Live = null;
                _columns?.SetWidth(cell.Column, width);
            }
        };
        handle.ResetRequested += (_, _) => _columns?.SetWidth(cell.Column, FileColumns.DefaultWidth(cell.Column));
    }

    private void Refresh()
    {
        if (_columns is not { } columns)
        {
            return;
        }

        var sorted = FileColumns.ColumnOf(columns.Sort);
        foreach (var (column, cell) in _cells)
        {
            cell.Root.Visibility = Ui.VisibleIf(columns.IsVisible(column));
            if (column != FileColumn.Name && cell.Live is null)
            {
                cell.Root.Width = columns.WidthOf(column);
            }

            var title = TitleOf(column);
            var arrow = column != sorted ? string.Empty : columns.Descending ? "  ↓" : "  ↑";
            cell.Button.Content = title + arrow;
            var state = column != sorted ? string.Empty : ", " + L.Get(columns.Descending ? "SortedDescending" : "SortedAscending");
            AutomationProperties.SetName(cell.Button, title + state);
            if (cell.Handle is { } handle)
            {
                AutomationProperties.SetName(handle, L.Get("ColumnWidthOf", title));
            }
        }
    }

    // Die Spalten zum Wählen: alle außer dem Namen, der immer da ist. Angekreuzt ist, was zu sehen ist.
    private void FillMenu()
    {
        _menu.Items.Clear();
        if (_columns is not { } columns)
        {
            return;
        }

        foreach (var column in columns.Available.Where(column => column != FileColumn.Name))
        {
            var item = new ToggleMenuFlyoutItem { Text = TitleOf(column), IsChecked = columns.IsVisible(column) };

            // Wie bei einem Kästchen: Maßgeblich ist der Zustand des Eintrags, nicht der Klick selbst.
            item.Click += (_, _) =>
            {
                if (item.IsChecked != columns.IsVisible(column))
                {
                    columns.Toggle(column);
                }
            };
            _menu.Items.Add(item);
        }
    }

    private static string TitleOf(FileColumn column) => column switch
    {
        FileColumn.Size => L.Get("ColumnSize"),
        FileColumn.Modified => L.Get("ColumnModified"),
        FileColumn.Permissions => L.Get("ColumnPermissions"),
        FileColumn.Owner => L.Get("ColumnOwner"),
        FileColumn.Group => L.Get("ColumnGroup"),
        FileColumn.Type => L.Get("ColumnType"),
        _ => L.Get("ColumnName"),
    };

    // Ein Kopf: der Knopf und – bis auf den Namen – der Griff an der linken Kante.
    private sealed class Cell
    {
        public Cell(FileColumn column)
        {
            Column = column;
            Button = new HyperlinkButton
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = column == FileColumn.Size ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Root = new Grid();
            Root.Children.Add(Button);
            if (column != FileColumn.Name)
            {
                // Der Griff liegt über der Lücke links der Spalte.
                Handle = new PaneSplitter
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(-Ui.Gap, 0, 0, 0),
                };
                Root.Children.Add(Handle);
            }
        }

        public FileColumn Column { get; }

        public HyperlinkButton Button { get; }

        public PaneSplitter? Handle { get; }

        public Grid Root { get; }

        /// <summary>Die Breite, die beim Ziehen gerade gilt; sonst <c>null</c>.</summary>
        public double? Live { get; set; }
    }
}
