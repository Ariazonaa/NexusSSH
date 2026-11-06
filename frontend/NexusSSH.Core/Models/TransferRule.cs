namespace NexusSSH.Core.Models;

/// <summary>
/// Welche Dateien ein Transfer am Ziel stehen lässt, statt sie zu überschreiben – für jede Datei einzeln, auch tief
/// in einem Ordner. Verglichen werden Größe und Änderungszeit, wie Quelle und Ziel sie nennen.
/// </summary>
public enum TransferKeep
{
    /// <summary>Nichts bleibt: Was es am Ziel gibt, wird überschrieben.</summary>
    None,

    /// <summary>Was gleich groß ist und am Ziel nicht älter als die Quelle – es wurde schon einmal übertragen.</summary>
    Same,

    /// <summary>Was am Ziel nicht älter ist als die Quelle: Übertragen wird nur Neueres.</summary>
    Newer,
}

/// <summary>
/// Was geschieht, wenn es am Ziel eines Transfers schon etwas mit demselben Namen gibt. Der Benutzer wählt es in
/// der Rückfrage – einmal für alles, was er gerade überträgt – oder legt es in den Einstellungen fest.
/// </summary>
public enum ExistingRule
{
    /// <summary>Jedes Mal fragen.</summary>
    Ask,

    /// <summary>Überschreiben.</summary>
    Overwrite,

    /// <summary>Überspringen, was gleich groß und nicht älter ist; den Rest überschreiben.</summary>
    SkipSame,

    /// <summary>Nur übertragen, was neuer ist als am Ziel.</summary>
    Newer,

    /// <summary>Beide behalten: Das Neue bekommt einen eigenen Namen („name (2).txt“).</summary>
    Rename,
}

public static class ExistingRules
{
    /// <summary>Was der Kern für diese Regel am Ziel stehen lässt.</summary>
    public static TransferKeep Keep(this ExistingRule rule) => rule switch
    {
        ExistingRule.SkipSame => TransferKeep.Same,
        ExistingRule.Newer => TransferKeep.Newer,
        _ => TransferKeep.None,
    };
}
