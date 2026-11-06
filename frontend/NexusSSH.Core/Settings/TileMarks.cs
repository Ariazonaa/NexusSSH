using System.Globalization;
using System.Text;
using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Settings;

/// <summary>Ein Symbol zur Auswahl für die Kachel eines Servers: Kennung und Zeichen aus den Symbolen von Windows.</summary>
public sealed record TileSymbol(string Id, string Glyph)
{
    /// <summary>Der Name in der Sprache der Oberfläche.</summary>
    public string Name => Id switch
    {
        "web" => L.Get("TileWeb"),
        "cloud" => L.Get("TileCloud"),
        "home" => L.Get("TileHome"),
        "mail" => L.Get("TileMail"),
        "shield" => L.Get("TileShield"),
        "storage" => L.Get("TileStorage"),
        "network" => L.Get("TileNetwork"),
        "code" => L.Get("TileCode"),
        "media" => L.Get("TileMedia"),
        "shop" => L.Get("TileShop"),
        "tools" => L.Get("TileTools"),
        _ => L.Get("TileCamera"),
    };

    public override string ToString() => Name;
}

/// <summary>
/// Was der Benutzer selbst auf die Kachel eines Servers oder einer Gruppe setzt: eines der Symbole hier
/// (<c>icon:cloud</c>) oder ein, zwei Buchstaben (<c>DB</c>). Gespeichert in den Einstellungen dieses Rechners,
/// nicht im Profil; alles andere ist kein Zeichen und fällt beim Laden weg.
/// </summary>
public static class TileMarks
{
    private const string Prefix = "icon:";

    /// <summary>Höchstens so viele Buchstaben passen in die Kachel.</summary>
    public const int MaxLetters = 2;

    public static IReadOnlyList<TileSymbol> Symbols { get; } =
    [
        new("web", ""),
        new("cloud", ""),
        new("home", ""),
        new("mail", ""),
        new("shield", ""),
        new("storage", ""),
        new("network", ""),
        new("code", ""),
        new("media", ""),
        new("shop", ""),
        new("tools", ""),
        new("camera", ""),
    ];

    /// <summary>Das Zeichen für eines der Symbole, wie es gespeichert wird.</summary>
    public static string OfSymbol(TileSymbol symbol) => Prefix + symbol.Id;

    /// <summary>Das Symbol, das ein Zeichen nennt; <c>null</c> bei Buchstaben und bei allem Unbekannten.</summary>
    public static TileSymbol? Symbol(string? mark)
        => mark is not null && mark.StartsWith(Prefix, StringComparison.Ordinal)
            ? Symbols.FirstOrDefault(symbol => symbol.Id == mark[Prefix.Length..])
            : null;

    /// <summary>Die Buchstaben eines Zeichens; leer bei einem Symbol und bei allem, was keines ist.</summary>
    public static string Letters(string? mark) => Symbol(mark) is null && IsLetters(mark) ? mark!.Trim() : string.Empty;

    /// <summary>Ob sich ein Text als Buchstaben auf eine Kachel setzen lässt: ein oder zwei Buchstaben oder Ziffern.</summary>
    public static bool IsLetters(string? text)
    {
        if (text?.Trim() is not { Length: > 0 } trimmed)
        {
            return false;
        }

        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(trimmed);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (++count > MaxLetters || !Rune.TryGetRuneAt(element, 0, out var rune) || !Rune.IsLetterOrDigit(rune))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ein Zeichen, wie es gespeichert wird; <c>null</c> für alles, was keines ist.</summary>
    public static string? Normalize(string? mark)
        => Symbol(mark) is { } symbol ? OfSymbol(symbol) : IsLetters(mark) ? mark!.Trim() : null;

    /// <summary>
    /// Was im Quadrat der Kachel steht, der Reihe nach: das eigene Zeichen des Servers, sonst das Logo seines
    /// Betriebssystems (wenn es bekannt ist und eines hat), sonst das Zeichen seiner Gruppe, sonst bei einem lokalen
    /// Terminal das Symbol dafür, sonst der Anfangsbuchstabe des Namens. Was der Benutzer dem einzelnen Server gibt,
    /// gewinnt also; das Logo schlägt nur das geerbte Zeichen. <c>Text</c> nennt bei Buchstaben die Buchstaben, bei
    /// einem Symbol das Zeichen (<c>icon:cloud</c>; leer für das gewöhnliche), beim System dessen Kennung.
    /// </summary>
    /// <param name="own">Das eigene Zeichen des Servers (<see cref="Models.ServerProfile.Mark"/>).</param>
    /// <param name="system">Das erkannte Betriebssystem (<see cref="Models.ServerProfile.System"/>).</param>
    /// <param name="group">Das Zeichen seiner Gruppe oder der nächsten darüber (<see cref="Models.ServerProfile.GroupMark"/>).</param>
    public static (TileFace Kind, string Text) Face(string? own, string? system, string? group, string name, bool local)
    {
        if (Marked(own) is { } face)
        {
            return face;
        }

        if (HasLogo(system))
        {
            return (TileFace.System, system!);
        }

        if (Marked(group) is { } inherited)
        {
            return inherited;
        }

        if (local)
        {
            return (TileFace.Terminal, string.Empty);
        }

        return Initial(name) is { Length: > 0 } initial ? (TileFace.Letters, initial) : (TileFace.Symbol, string.Empty);
    }

    // Was ein Zeichen auf die Kachel setzt; null, wenn es keines ist.
    private static (TileFace Kind, string Text)? Marked(string? mark)
        => Symbol(mark) is { } symbol ? (TileFace.Symbol, OfSymbol(symbol))
            : Letters(mark) is { Length: > 0 } letters ? (TileFace.Letters, letters)
            : null;

    /// <summary>Ob es zu einer Kennung des Betriebssystems ein Logo gibt (<see cref="ServerSystemIcons"/>).</summary>
    public static bool HasLogo(string? system) => ServerSystems.Find(system) is not null && ServerSystemIcons.Find(system) is not null;

    /// <summary>Der erste Buchstabe oder die erste Ziffer eines Namens, groß; leer, wenn er keine hat.</summary>
    public static string Initial(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return Rune.ToUpperInvariant(rune).ToString();
            }
        }

        return string.Empty;
    }
}

/// <summary>Die Art dessen, was im Quadrat einer Kachel steht (<see cref="TileMarks.Face"/>).</summary>
public enum TileFace
{
    Letters,
    Symbol,
    Terminal,

    /// <summary>Das Logo des Betriebssystems in seiner Markenfarbe.</summary>
    System,
}
