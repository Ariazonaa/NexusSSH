namespace NexusSSH.Core.ViewModels;

// Die Knöpfe rechts in der Titelleiste (Entwurf A, 2026-10-28): Sie ersetzen die Kopfzeile, die bis
// dahin über dem Terminal stand. Dateien und die Spalte rechts sind Schalter, Suchen ist ein Befehl; was die Kopfzeile
// sonst zum Teilen hatte (rechts, unten, an alle senden, zwei Spalten), steht im Menü des Knopfs „Teilen“. Trennen,
// Erneut verbinden und „Als Server speichern“ haben das Menü des Reiters und die Palette.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Die Befehle im Menü „Teilen“ der Titelleiste, in dieser Reihenfolge. Die Schalter darunter
    /// (<see cref="SplitMenuState"/>) stehen hinter einer Linie.
    /// </summary>
    public static IReadOnlyList<string> SplitMenuIds { get; } =
    [
        "terminal.splitRight",
        "terminal.splitDown",
        "terminal.unsplit",
        "terminal.equalize",
        "terminal.zoomPane",
        "terminal.broadcast",
        "view.dualFiles",
    ];

    /// <summary>Ob ein Eintrag des Menüs „Teilen“ ein Schalter ist und wie er steht; <c>null</c> für einen Befehl.</summary>
    public bool? SplitMenuState(string id) => id switch
    {
        "terminal.broadcast" => BroadcastInput,
        "view.dualFiles" => IsDualFiles,
        _ => null,
    };
}
