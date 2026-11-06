using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace NexusSSH.App.Controls;

/// <summary>Die Symbole der Oberfläche (Entwurf A).</summary>
public enum LineIconKind
{
    Home,
    Folder,
    FolderPlus,
    Snippets,
    Split,
    Search,
    Up,
    Refresh,
    Download,
    Upload,
    Delete,
    Edit,
    File,
    Servers,
    Key,
    Terminal,
    Tunnel,
    ChevronDown,
    Settings,
    ChevronRight,
    ArrowRight,
}

/// <summary>
/// Ein Linien-Symbol aus Entwurf A: gezeichnet im 24er-Raster mit Strich 2 und runden Enden, so groß wie
/// <see cref="Size"/> (der Strich wird mit verkleinert, wie im Bild). Die Farbe ist der Vordergrund;
/// <see cref="Filled"/> füllt die Form statt sie zu umranden (Ordner in der Dateiliste).
/// </summary>
public sealed partial class LineIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(LineIconKind), typeof(LineIcon),
        new PropertyMetadata(LineIconKind.Home, (d, _) => ((LineIcon)d).Draw()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LineIcon),
        new PropertyMetadata(16.0, (d, _) => ((LineIcon)d).Resize()));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(LineIcon),
        new PropertyMetadata(false, (d, _) => ((LineIcon)d).Paint()));

    private readonly Path _path = new()
    {
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public LineIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_path);
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        IsTabStop = false;
        Resize();
        Draw();
        Paint();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Paint());
        Loaded += (_, _) => Paint();
    }

    public LineIconKind Kind
    {
        get => (LineIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>Für den Selbsttest: ob die Form gelesen wurde – das geschieht erst zur Laufzeit, auch unter AOT.</summary>
    internal bool HasShape => _path.Data is not null;

    private void Resize() => Width = Height = Size;

    // Eine Geometrie je Symbol und Element: WinUI teilt eine Geometrie nicht zwischen zwei Pfaden.
    private void Draw() => _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), DataOf(Kind));

    private void Paint()
    {
        _path.Stroke = Filled ? null : Foreground;
        _path.Fill = Filled ? Foreground : null;
    }

    /// <summary>
    /// Die Pfade aus den Bildern, ausgeschrieben (keine verkürzten Befehle); Rechtecke mit Rundung und Kreise als
    /// Bögen.
    /// </summary>
    public static string DataOf(LineIconKind kind) => kind switch
    {
        LineIconKind.Home => "M3,11 L12,4 L21,11 M5,10 L5,20 L19,20 L19,10",
        LineIconKind.Folder => "M3,6 L10,6 L12,8 L21,8 L21,19 L3,19 Z",
        LineIconKind.FolderPlus => "M3,6 L10,6 L12,8 L21,8 L21,19 L3,19 Z M12,11 L12,16 M9.5,13.5 L14.5,13.5",
        LineIconKind.Snippets => "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17",
        LineIconKind.Split => "M5,4 L19,4 A2,2 0 0 1 21,6 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 L3,6 A2,2 0 0 1 5,4 Z M12,4 L12,20",
        LineIconKind.Search => "M4,11 A7,7 0 1 1 18,11 A7,7 0 1 1 4,11 Z M20,20 L16,16",
        LineIconKind.Up => "M12,19 L12,5 M5,12 L12,5 L19,12",
        LineIconKind.Refresh => "M20,11 A8,8 0 1 0 17.7,16.7 M20,4 L20,11 L13,11",
        LineIconKind.Download => "M12,4 L12,15 M7,10 L12,15 L17,10 M5,20 L19,20",
        LineIconKind.Upload => "M12,15 L12,4 M7,9 L12,4 L17,9 M5,20 L19,20",
        LineIconKind.Delete => "M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M6,7 L7,20 L17,20 L18,7",
        LineIconKind.Edit => "M4,20 L8,20 L19,9 L15,5 L4,16 Z",
        LineIconKind.File => "M6,3 L14,3 L18,7 L18,21 L6,21 Z",
        LineIconKind.Servers => "M5,4 L19,4 A2,2 0 0 1 21,6 L21,9 A2,2 0 0 1 19,11 L5,11 A2,2 0 0 1 3,9 L3,6 A2,2 0 0 1 5,4 Z M5,13 L19,13 A2,2 0 0 1 21,15 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 L3,15 A2,2 0 0 1 5,13 Z",
        LineIconKind.Key => "M4,15 A4,4 0 1 1 12,15 A4,4 0 1 1 4,15 Z M11,12 L19,4 M16,7 L19,10",
        LineIconKind.Terminal => "M4,17 L10,12 L4,7 M12,19 L20,19",
        LineIconKind.Tunnel => "M4,8 L16,8 M13,5 L16,8 L13,11 M20,16 L8,16 M11,13 L8,16 L11,19",
        LineIconKind.ChevronDown => "M6,9 L12,15 L18,9",
        LineIconKind.ChevronRight => "M9,6 L15,12 L9,18",
        LineIconKind.ArrowRight => "M5,12 L19,12 M12,5 L19,12 L12,19",
        LineIconKind.Settings => "M9,12 A3,3 0 1 1 15,12 A3,3 0 1 1 9,12 Z M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5",
        _ => "",
    };
}
