using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace NexusSSH.App.Controls;

/// <summary>
/// Trennlinie zwischen zwei Bereichen, an der man zieht. Sie kennt das Layout nicht: Sie meldet nur, wie weit seit
/// Beginn gezogen wurde, und das Fenster rechnet daraus die neue Breite (WinUI 3 bringt keinen GridSplitter mit).
/// <para>
/// Sie lässt sich auch mit der Tastatur bedienen: per Tab erreichbar, die Pfeiltasten verschieben sie, Pos1 stellt
/// die Standardgröße wieder her.
/// </para>
/// </summary>
public sealed partial class PaneSplitter : UserControl
{
    // So breit wie die Lücke zwischen zwei Karten: Der Trenner ist die Lücke.
    private static double HitSize => Ui.Gap;

    /// <summary>So weit verschiebt ein Druck auf eine Pfeiltaste, in DIPs.</summary>
    private const double DefaultKeyStep = 16;

    private readonly Rectangle _line = new();
    private Orientation _orientation = Orientation.Vertical;
    private bool _dragging;
    private bool _hovering;
    private double _origin;

    public PaneSplitter()
    {
        // Transparent statt null, sonst trifft der Zeiger nur die 1 px breite Linie.
        var surface = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        surface.Children.Add(_line);
        Content = surface;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ApplyOrientation();
        UpdateLine();

        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => UpdateLine();
        LostFocus += (_, _) => UpdateLine();
    }

    /// <summary>So weit verschiebt ein Druck auf eine Pfeiltaste, in DIPs (die Spaltenköpfe der Dateilisten nehmen 8).</summary>
    public double KeyStep { get; set; } = DefaultKeyStep;

    /// <summary>Beginn des Ziehens – der Empfänger merkt sich die Ausgangsgröße.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Abstand zum Ausgangspunkt in DIPs; nach rechts bzw. unten positiv.</summary>
    public event EventHandler<double>? DragDelta;

    public event EventHandler? DragCompleted;

    /// <summary>Doppelklick oder Pos1: zurück zur Standardgröße.</summary>
    public event EventHandler? ResetRequested;

    /// <summary><see cref="Orientation.Vertical"/> = senkrechte Linie zwischen Spalten, sonst waagerechte zwischen Zeilen.</summary>
    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            _orientation = value;
            ApplyOrientation();
        }
    }

    private void ApplyOrientation()
    {
        var vertical = _orientation == Orientation.Vertical;
        Width = vertical ? HitSize : double.NaN;
        Height = vertical ? double.NaN : HitSize;
        _line.Width = vertical ? 1 : double.NaN;
        _line.Height = vertical ? double.NaN : 1;
        _line.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        _line.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }

        _dragging = true;
        _origin = Position(e);
        UpdateLine();
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            DragDelta?.Invoke(this, Position(e) - _origin);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        UpdateLine();
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    // Ein Tastendruck ist ein kurzes Ziehen: Anfang, ein Schritt, Ende.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var vertical = _orientation == Orientation.Vertical;
        double? step = e.Key switch
        {
            VirtualKey.Left when vertical => -KeyStep,
            VirtualKey.Right when vertical => KeyStep,
            VirtualKey.Up when !vertical => -KeyStep,
            VirtualKey.Down when !vertical => KeyStep,
            _ => null,
        };

        if (step is { } delta)
        {
            DragStarted?.Invoke(this, EventArgs.Empty);
            DragDelta?.Invoke(this, delta);
            DragCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Home)
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    // Gemessen am Fenster, nicht an der Linie: Die Linie wandert beim Ziehen mit.
    private double Position(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        return _orientation == Orientation.Vertical ? point.X : point.Y;
    }

    private void SetHover(bool hovering)
    {
        _hovering = hovering;
        UpdateLine();
    }

    private void UpdateLine()
    {
        var highlighted = _dragging || _hovering || FocusState == FocusState.Keyboard;

        // In Ruhe unsichtbar: Die Karten links und rechts haben ihren eigenen Rand.
        _line.Fill = highlighted ? Ui.ChartBrush : null;
    }
}
