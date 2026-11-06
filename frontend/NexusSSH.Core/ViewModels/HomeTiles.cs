namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Wie die Kacheln der Startseite die Breite füllen (Entwurf A): vier Spalten, solange jede wenigstens
/// <see cref="MinSlot"/> breit bleibt, sonst weniger. Ein Platz ist die Kachel samt ihrem Abstand.
/// </summary>
public static class HomeTiles
{
    /// <summary>So viele Spalten stehen höchstens nebeneinander.</summary>
    public const int MaxColumns = 4;

    /// <summary>Schmaler wird ein Platz nicht – dann rückt eine Spalte weg.</summary>
    public const double MinSlot = 220;

    /// <summary>Die Zahl der Spalten für diese Breite; mindestens eine.</summary>
    public static int Columns(double width)
        => double.IsFinite(width) && width > 0 ? Math.Clamp((int)(width / MinSlot), 1, MaxColumns) : 1;

    /// <summary>Die Breite eines Platzes, ganzzahlig abgerundet – so passen alle Spalten sicher in die Zeile.</summary>
    public static double Slot(double width) => double.IsFinite(width) && width > 0 ? Math.Floor(width / Columns(width)) : MinSlot;
}
