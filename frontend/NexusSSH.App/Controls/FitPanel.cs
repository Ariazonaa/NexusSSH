using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NexusSSH.App.Controls;

/// <summary>
/// Stellt seine Kinder nebeneinander und zeigt nur die, die ganz hineinpassen – von vorn an; das erste, das nicht
/// mehr passt, fällt samt allen danach weg. Für die Leiste mit der Auslastung unter dem Terminal: In einem schmalen
/// Fenster schnitt ein <see cref="StackPanel"/> einen Wert mitten im Text ab („↓ 146 KB/“), und alles dahinter
/// fehlte ohne Hinweis (gefunden 2026-10-24 im Gast).
/// </summary>
public sealed partial class FitPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var fits = true;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var size = child.DesiredSize;

            // Was nicht ganz passt, zählt nicht mit – auch nicht für die Höhe.
            fits = fits && width + size.Width <= availableSize.Width;
            if (fits)
            {
                width += size.Width;
                height = Math.Max(height, size.Height);
            }
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var fits = true;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            fits = fits && x + size.Width <= finalSize.Width;
            if (fits)
            {
                child.Arrange(new Rect(x, 0, size.Width, finalSize.Height));
                x += size.Width;
            }
            else
            {
                // Ohne Fläche ist es nicht zu sehen; UI Automation nennt es weiter, nur außerhalb des Bildes.
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }
}
