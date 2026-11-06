using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Wie Cursor und Zeilen im Terminal aussehen – aus den Einstellungen, in den Grenzen, die die Terminal-Seite
/// kennt (<c>applyOptions</c> in <c>terminal.js</c> prüft sie noch einmal).
/// </summary>
/// <param name="CursorStyle"><c>block</c>, <c>bar</c> oder <c>underline</c>.</param>
/// <param name="LineHeight">Zeilenabstand als Vielfaches der Schrifthöhe, 1 bis 2.</param>
/// <param name="LetterSpacing">Zusätzlicher Abstand zwischen den Zeichen in Pixeln, 0 bis 8.</param>
/// <param name="Ligatures">Ligaturen der Schrift zeigen (<c>=&gt;</c>, <c>!=</c>), wenn sie welche hat.</param>
public sealed record TerminalLook(string CursorStyle, bool CursorBlink, double LineHeight, int LetterSpacing, bool Ligatures)
{
    public const string Block = "block";
    public const string Bar = "bar";
    public const string Underline = "underline";

    public const double MinLineHeight = 1.0;
    public const double MaxLineHeight = 2.0;
    public const int MaxLetterSpacing = 8;

    /// <summary>Die Formen des Cursors, in der Reihenfolge der Auswahl.</summary>
    public static IReadOnlyList<string> CursorStyles { get; } = [Block, Bar, Underline];

    /// <summary>Eine Form, die es gibt; alles andere ist der Block.</summary>
    public static string Cursor(string? style) => CursorStyles.Contains(style) ? style! : Block;

    /// <summary>Der Zeilenabstand in seinen Grenzen, auf Zehntel gerundet; keine Zahl ist der einfache Abstand.</summary>
    public static double Height(double height)
        => double.IsFinite(height) ? Math.Round(Math.Clamp(height, MinLineHeight, MaxLineHeight), 1) : MinLineHeight;

    public static int Spacing(int pixels) => Math.Clamp(pixels, 0, MaxLetterSpacing);

    public static TerminalLook From(AppSettings settings) => new(
        Cursor(settings.TerminalCursor),
        settings.TerminalCursorBlink,
        Height(settings.TerminalLineHeight),
        Spacing(settings.TerminalLetterSpacing),
        settings.TerminalLigatures);
}
