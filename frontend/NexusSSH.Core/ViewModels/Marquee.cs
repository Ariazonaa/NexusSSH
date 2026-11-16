namespace NexusSSH.Core.ViewModels;

/// <summary>Wie ein Rahmen mit der Maus die bisherige Auswahl behandelt – nach den Tasten beim Drücken.</summary>
public enum MarqueeMode
{
    /// <summary>Ohne Taste: Der Rahmen ersetzt die Auswahl.</summary>
    Replace,

    /// <summary>Mit Strg: Was der Rahmen trifft, kommt zur Auswahl dazu.</summary>
    Add,

    /// <summary>Mit Umschalt: Die Auswahl wächst vom Anker bis zu dem, was der Rahmen trifft.</summary>
    Extend,
}

/// <summary>
/// Ein Rahmen, mit der Maus in einer Dateiliste aufgezogen, wie im Explorer (Detailansicht). Gezogen wird auf Symbol
/// oder Name ein Eintrag (Ziehen und Ablegen), daneben in der Zeile oder in der freien Fläche ein Rahmen; was er
/// berührt, ist gewählt, schon während des Ziehens. Die Zeilen gehen über die ganze Breite der Liste – welche er
/// trifft, entscheidet allein die Höhe. Alle Maße in Pixeln der Oberfläche (DIPs).
/// </summary>
public static class Marquee
{
    /// <summary>So weit muss sich die Maus bewegen, bevor ein Rahmen entsteht (wie <c>SM_CXDRAG</c> von Windows).</summary>
    public const double DragDistance = 4;

    /// <summary>So nah am oberen oder unteren Rand der Liste beginnt sie mitzurollen.</summary>
    public const double ScrollEdge = 24;

    /// <summary>Höchstens so weit rollt die Liste in einem Schritt (etwa 60 Schritte je Sekunde).</summary>
    public const double MaxScrollStep = 40;

    /// <summary>Ob die Maus seit dem Drücken weit genug gewandert ist, dass ein Rahmen entsteht.</summary>
    public static bool Begins(double dx, double dy) => Math.Abs(dx) > DragDistance || Math.Abs(dy) > DragDistance;

    /// <summary>
    /// Ob ein Druck einem Rahmen gehört statt dem Ziehen eines Eintrags: in der freien Fläche immer, in einer Zeile
    /// nur außerhalb von Symbol und Name (<paramref name="nameLeft"/> bis <paramref name="nameRight"/>, Maße in der
    /// Zeile).
    /// </summary>
    public static bool StartsHere(bool onItem, double x, double nameLeft, double nameRight)
        => !onItem || x < nameLeft || x > nameRight;

    /// <summary>Strg geht vor Umschalt – wie beim Klicken.</summary>
    public static MarqueeMode ModeFor(bool control, bool shift)
        => control ? MarqueeMode.Add : shift ? MarqueeMode.Extend : MarqueeMode.Replace;

    /// <summary>
    /// Die Zeilen, die ein Rahmen von <paramref name="y1"/> bis <paramref name="y2"/> berührt (in der Höhe der
    /// Zeilen gemessen: Zeile <c>i</c> liegt bei <c>top + i·pitch</c>); <c>null</c>, wenn er keine trifft.
    /// </summary>
    public static (int First, int Last)? Rows(double y1, double y2, double top, double pitch, int count)
    {
        if (count <= 0 || !(pitch > 0) || !double.IsFinite(y1) || !double.IsFinite(y2) || !double.IsFinite(top))
        {
            return null;
        }

        var low = Math.Min(y1, y2) - top;
        var high = Math.Max(y1, y2) - top;
        if (high < 0 || low >= count * pitch)
        {
            return null;
        }

        var first = (int)Math.Clamp(Math.Floor(low / pitch), 0, count - 1);
        var last = (int)Math.Clamp(Math.Floor(high / pitch), 0, count - 1);
        return (first, last);
    }

    /// <summary>
    /// Was gewählt ist, während der Rahmen <paramref name="rows"/> berührt. <paramref name="before"/> ist die
    /// Auswahl beim Drücken, <paramref name="anchor"/> der zuletzt angeklickte Eintrag (−1 ohne) – nur für
    /// <see cref="MarqueeMode.Extend"/>; ohne Anker wächst die Auswahl um den Rahmen allein.
    /// </summary>
    public static HashSet<int> Selection(MarqueeMode mode, IEnumerable<int> before, (int First, int Last)? rows, int anchor)
    {
        HashSet<int> selected = mode == MarqueeMode.Replace ? [] : [.. before];
        if (rows is not var (first, last))
        {
            return selected;
        }

        if (mode == MarqueeMode.Extend && anchor >= 0)
        {
            first = Math.Min(first, anchor);
            last = Math.Max(last, anchor);
        }

        for (var index = first; index <= last; index++)
        {
            selected.Add(index);
        }

        return selected;
    }

    /// <summary>
    /// Wie weit die Liste in einem Schritt rollt, wenn die Maus bei <paramref name="y"/> steht (in der Liste
    /// gemessen, <paramref name="height"/> hoch): am Rand und darüber hinaus nach oben (negativ) oder unten, schneller,
    /// je weiter draußen; sonst 0.
    /// </summary>
    public static double ScrollStep(double y, double height)
    {
        if (!(height > 0) || !double.IsFinite(y))
        {
            return 0;
        }

        // In einer sehr niedrigen Liste bliebe sonst kein Platz, an dem sie stillsteht.
        var edge = Math.Min(ScrollEdge, height / 4);
        if (y < edge)
        {
            return -Speed(edge - y);
        }

        return y > height - edge ? Speed(y - (height - edge)) : 0;

        static double Speed(double depth) => Math.Min(MaxScrollStep, 2 + (depth / 2));
    }
}
