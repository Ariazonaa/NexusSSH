using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NexusSSH.Core.ViewModels;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NexusSSH.App.Controls;

/// <summary>
/// Der Rahmen mit der Maus in einer Dateiliste, wie im Explorer: Wer neben Symbol und Name drückt (oder in der freien
/// Fläche) und zieht, zieht einen Rahmen auf; was er berührt, ist gewählt. Was das bedeutet, entscheidet
/// <see cref="Marquee"/> in Core – hier wird nur gemessen, gezeichnet und die Auswahl der Liste gesetzt (die sieht
/// auch eine Sprachausgabe).
/// </summary>
/// <remarks>
/// Die Zeilenvorlage einer Liste nennt ihren Namen als <see cref="TextBlock"/> in Spalte 1 ihres <see cref="Grid"/>,
/// davor das Symbol – so stehen beide Dateilisten. Die Liste behält ihr eigenes Ziehen: Ihr
/// <c>DragItemsStarting</c> fragt <see cref="ClaimsPress"/> und bricht ab, wenn der Druck dem Rahmen gehört.
/// Gemessen wird in Maßen des Panels der Zeilen: Es rollt mit dem Inhalt, der Anfang des Rahmens bleibt so an seiner
/// Zeile, während die Liste rollt.
/// </remarks>
internal sealed class ListMarquee
{
    private readonly ListViewBase _list;
    private readonly Canvas _layer;
    private readonly Rectangle _frame = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly DispatcherQueueTimer _scroll;

    // Der laufende Druck: wo (Liste und Panel), worauf, mit welcher Taste.
    private uint? _pointerId;
    private Pointer? _pointer;
    private Point _pressed;
    private Point _start;
    private Point _last;
    private bool _onEmpty;
    private bool _claims;
    private bool _active;
    private MarqueeMode _mode;
    private HashSet<int> _before = [];
    private HashSet<int> _target = [];

    // Der zuletzt angeklickte Eintrag – von dort wächst die Auswahl mit Umschalt.
    private int _anchor = -1;

    // Ob die Liste Einträge ziehen durfte, bevor der Rahmen den Druck nahm; null, solange er es ihr nicht verbietet.
    private bool? _couldDrag;

    private ListMarquee(ListViewBase list, Canvas layer)
    {
        _list = list;
        _layer = layer;
        layer.Children.Add(_frame);
        _scroll = list.DispatcherQueue.CreateTimer();
        _scroll.Interval = TimeSpan.FromMilliseconds(16);
        _scroll.Tick += (_, _) => OnScrollTick();

        // Auch was die Zeilen schon behandelt haben: Sie fangen Drücken und Bewegen für Auswahl und Ziehen ab.
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        list.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled), handledEventsToo: true);
        list.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnLost), handledEventsToo: true);
        list.PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Hängt den Rahmen an <paramref name="list"/>; gezeichnet wird auf <paramref name="layer"/>, das über ihr liegt.</summary>
    public static ListMarquee Attach(ListViewBase list, Canvas layer) => new(list, layer);

    /// <summary>Ob der Druck gerade dem Rahmen gehört – dann zieht die Liste keine Einträge.</summary>
    public bool ClaimsPress => _claims;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_active)
        {
            return;
        }

        // Ging ein Loslassen verloren, darf die Liste spätestens jetzt wieder ziehen.
        Reset();
        var point = e.GetCurrentPoint(_list);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed
            || InScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var item = Container(e.OriginalSource as DependencyObject);
        var control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (item is not null && !shift)
        {
            _anchor = _list.IndexFromContainer(item);
        }

        var claims = item is null;
        if (item is not null)
        {
            var (left, right) = NameSpan(item);
            claims = Marquee.StartsHere(onItem: true, e.GetCurrentPoint(item).Position.X, left, right);
        }

        if (!claims || _list.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        // Sonst erkennt die Liste in der Bewegung ihr eigenes Ziehen, nimmt der Maus den Fang und beendet den Rahmen,
        // kaum dass er begonnen hat (gesehen 2026-11-16 in der Sandbox).
        _couldDrag ??= _list.CanDragItems;
        _list.CanDragItems = false;
        _claims = true;
        _pointerId = e.Pointer.PointerId;
        _pointer = e.Pointer;
        _pressed = point.Position;
        _last = point.Position;
        _start = e.GetCurrentPoint(panel).Position;
        _onEmpty = item is null;
        _mode = Marquee.ModeFor(control, shift);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        _last = e.GetCurrentPoint(_list).Position;
        if (!_active)
        {
            if (!Marquee.Begins(_last.X - _pressed.X, _last.Y - _pressed.Y))
            {
                return;
            }

            Begin(e.Pointer);
        }

        Update();
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId)
        {
            return;
        }

        if (_active)
        {
            _last = e.GetCurrentPoint(_list).Position;
            Update();
            End();
            e.Handled = true;
        }
        else if (_onEmpty && _mode == MarqueeMode.Replace)
        {
            // Ein Klick ins Leere hebt die Auswahl auf – wie im Explorer.
            _list.SelectedItems.Clear();
        }

        Reset();
    }

    // Die Liste nahm der Maus den Fang (ihr Ziehen) – dann holt der Rahmen ihn zurück. Geht das nicht mehr (losgelassen
    // in einem anderen Fenster, Alt+Tab), endet der Rahmen, die Auswahl bleibt.
    private void OnLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_active || _pointerId != e.Pointer.PointerId)
        {
            return;
        }

        _ = _list.DispatcherQueue.TryEnqueue(() =>
        {
            if (_active && (_pointer is not { } pointer || !_list.CapturePointer(pointer)))
            {
                End();
                Reset();
            }
        });
    }

    // Windows hat die Eingabe abgebrochen: Der Rahmen endet, die Auswahl bleibt.
    private void OnCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            if (_active)
            {
                End();
            }

            Reset();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_active && e.Key == VirtualKey.Escape)
        {
            _target = _before;
            Apply(_target);
            End();
            Reset();
            e.Handled = true;
        }
    }

    private void Begin(Pointer pointer)
    {
        _active = true;
        _before = SelectedIndices();
        _target = [.. _before];

        // Die Zeile unter der Maus hielte die Maus sonst fest und wählte sich beim Loslassen allein.
        _ = _list.CapturePointer(pointer);
        _ = _list.Focus(FocusState.Pointer);

        // Was die App selbst zeichnet, nimmt die Farbe für Gezeichnetes (im Kontrastdesign die für Hervorgehobenes).
        var stroke = Ui.ChartBrush;
        _frame.Stroke = stroke;
        _frame.Fill = stroke is SolidColorBrush solid
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, solid.Color.R, solid.Color.G, solid.Color.B))
            : null;
        _frame.Visibility = Visibility.Visible;
        _scroll.Start();
    }

    private void End()
    {
        _active = false;
        _scroll.Stop();
        _frame.Visibility = Visibility.Collapsed;
        _list.ReleasePointerCaptures();

        // Hat die Zeile unter dem Anfang beim Loslassen doch noch sich allein gewählt, gilt trotzdem der Rahmen.
        var final = _target;
        _ = _list.DispatcherQueue.TryEnqueue(() => Apply(final));
    }

    private void Reset()
    {
        _pointerId = null;
        _pointer = null;
        _claims = false;
        _onEmpty = false;
        if (_couldDrag is { } could)
        {
            _list.CanDragItems = could;
            _couldDrag = null;
        }
    }

    private void OnScrollTick()
    {
        if (!_active || Descendant<ScrollViewer>(_list) is not { } viewer)
        {
            return;
        }

        var step = Marquee.ScrollStep(_last.Y, _list.ActualHeight);
        if (step != 0)
        {
            _ = viewer.ChangeView(null, viewer.VerticalOffset + step, null, disableAnimation: true);
            Update();
        }
    }

    // Wählt, was der Rahmen gerade berührt, und zeichnet ihn.
    private void Update()
    {
        if (_list.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        var current = _list.TransformToVisual(panel).TransformPoint(_last);
        var rows = Measure(panel) is var (top, pitch)
            ? Marquee.Rows(_start.Y, current.Y, top, pitch, _list.Items.Count)
            : null;
        _target = Marquee.Selection(_mode, _before, rows, _anchor < _list.Items.Count ? _anchor : -1);
        Apply(_target);

        // Gezeichnet wird nur, was von der Liste zu sehen ist: Der Anfang kann schon weggerollt sein.
        var from = Clamp(panel.TransformToVisual(_layer).TransformPoint(_start));
        var to = Clamp(_list.TransformToVisual(_layer).TransformPoint(_last));
        Canvas.SetLeft(_frame, Math.Min(from.X, to.X));
        Canvas.SetTop(_frame, Math.Min(from.Y, to.Y));
        _frame.Width = Math.Abs(to.X - from.X);
        _frame.Height = Math.Abs(to.Y - from.Y);
    }

    private Point Clamp(Point point)
        => new(Math.Clamp(point.X, 0, Math.Max(0, _layer.ActualWidth)), Math.Clamp(point.Y, 0, Math.Max(0, _layer.ActualHeight)));

    // Wo Zeile 0 im Panel läge und wie weit die Zeilen auseinanderstehen – aus den Zeilen, die es gerade gibt
    // (die Liste baut nur, was zu sehen ist, und ein wenig drumherum).
    private (double Top, double Pitch)? Measure(Panel panel)
    {
        (int Index, double Y, FrameworkElement Row)? low = null;
        (int Index, double Y)? high = null;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement row)
            {
                continue;
            }

            var index = _list.IndexFromContainer(row);
            if (index < 0)
            {
                continue;
            }

            var y = row.TransformToVisual(panel).TransformPoint(default).Y - row.Margin.Top;
            if (low is null || index < low.Value.Index)
            {
                low = (index, y, row);
            }

            if (high is null || index > high.Value.Index)
            {
                high = (index, y);
            }
        }

        if (low is not var (lowIndex, lowY, lowRow))
        {
            return null;
        }

        var pitch = high is var (highIndex, highY) && highIndex > lowIndex
            ? (highY - lowY) / (highIndex - lowIndex)
            : lowRow.ActualHeight + lowRow.Margin.Top + lowRow.Margin.Bottom;
        return (lowY - (lowIndex * pitch), pitch);
    }

    // Setzt die Auswahl der Liste auf genau diese Zeilen – in zusammenhängenden Stücken, nur was sich ändert.
    private void Apply(HashSet<int> target)
    {
        var count = _list.Items.Count;
        var current = SelectedIndices();
        foreach (var (first, length) in Runs(current.Where(index => !target.Contains(index))))
        {
            _list.DeselectRange(new ItemIndexRange(first, (uint)length));
        }

        foreach (var (first, length) in Runs(target.Where(index => index < count && !current.Contains(index))))
        {
            _list.SelectRange(new ItemIndexRange(first, (uint)length));
        }
    }

    private HashSet<int> SelectedIndices()
    {
        HashSet<int> indices = [];
        foreach (var range in _list.SelectedRanges)
        {
            for (var index = range.FirstIndex; index <= range.LastIndex; index++)
            {
                indices.Add(index);
            }
        }

        return indices;
    }

    private static IEnumerable<(int First, int Length)> Runs(IEnumerable<int> indices)
    {
        int? first = null;
        var previous = 0;
        foreach (var index in indices.Order())
        {
            if (first is not null && index == previous + 1)
            {
                previous = index;
                continue;
            }

            if (first is { } done)
            {
                yield return (done, previous - done + 1);
            }

            first = index;
            previous = index;
        }

        if (first is { } last)
        {
            yield return (last, previous - last + 1);
        }
    }

    // Von Symbol bis Ende des Namens, in Maßen der Zeile – der Name ist so breit wie sein Text, nicht wie die Spalte.
    private static (double Left, double Right) NameSpan(SelectorItem item)
    {
        if (item.ContentTemplateRoot is not Grid root)
        {
            return (0, double.MaxValue);
        }

        var left = root.TransformToVisual(item).TransformPoint(default).X;
        if (root.Children.OfType<TextBlock>().FirstOrDefault(text => Grid.GetColumn(text) == 1) is not { } name)
        {
            return (left, double.MaxValue);
        }

        var width = Math.Min(name.ActualWidth, Math.Max(0, name.DesiredSize.Width - name.Margin.Left - name.Margin.Right));
        return (left, name.TransformToVisual(item).TransformPoint(default).X + width);
    }

    private SelectorItem? Container(DependencyObject? source)
    {
        for (var node = source; node is not null && node != _list; node = VisualTreeHelper.GetParent(node))
        {
            if (node is SelectorItem item)
            {
                return item;
            }
        }

        return null;
    }

    private bool InScrollBar(DependencyObject? source)
    {
        for (var node = source; node is not null && node != _list; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private static T? Descendant<T>(DependencyObject parent)
        where T : class
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
