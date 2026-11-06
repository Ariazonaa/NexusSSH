using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Settings;

/// <summary>
/// Eine wählbare Akzentfarbe der Oberfläche (gewählter Eintrag, Hauptknopf, Fortschritt, Fokus).
/// <see cref="Hex"/> als <c>#rrggbb</c>; <c>null</c> heißt: die Farbe, die in Windows eingestellt ist.
/// </summary>
public sealed record AccentColor(string Id, string Name, string? Hex)
{
    public override string ToString() => Name;
}

/// <summary>
/// Die Kennfarben, die ein Profil tragen kann – damit man Produktion und Test an Tab, Kopfzeile und Kachel
/// unterscheidet. Die Kennungen sind die des Kerns (<c>profiles::COLORS</c>); die erste steht für „keine“.
/// </summary>
public static class ProfileColors
{
    /// <summary>Alle Farben mit ihrem Namen in der Sprache der Oberfläche.</summary>
    public static IReadOnlyList<AccentColor> All => BindableList.Of<AccentColor>(
    [
        new(string.Empty, L.Get("ColorNone"), null),
        new("red", L.Get("AccentRed"), "#e5484d"),
        new("orange", L.Get("AccentOrange"), "#f08c2e"),
        new("yellow", L.Get("ColorYellow"), "#e6c229"),
        new("green", L.Get("AccentGreen"), "#2ea043"),
        new("blue", L.Get("AccentBlue"), "#2f7bff"),
        new("violet", L.Get("AccentViolet"), "#7c4dff"),
    ]);

    /// <summary>Die Farbe mit dieser Kennung; bei einer unbekannten oder ohne Angabe „keine“.</summary>
    public static AccentColor Find(string? id)
    {
        var all = All;
        return all.FirstOrDefault(color => color.Id.Length > 0 && color.Id == id) ?? all[0];
    }

    /// <summary>
    /// Das Quadrat auf der Kachel eines Servers (Entwurf A): Schrift in der Kennfarbe auf einer dunklen Fläche, die
    /// zu ihr passt; ohne Kennfarbe grau auf Grau. Beide als <c>#rrggbb</c>. Grün, Blau und Gelb sind die des
    /// Entwurfs, die übrigen ihnen nachgebildet.
    /// </summary>
    public static (string Fore, string Back) Tile(string? id) => Find(id).Id switch
    {
        "red" => ("#ff7a7f", "#3a1d1f"),
        "orange" => ("#f8a55a", "#3a2614"),
        "yellow" => ("#f5c46b", "#3a2a10"),
        "green" => ("#4ade80", "#1d3a2a"),
        "blue" => ("#5fb3ff", "#1d2a3a"),
        "violet" => ("#b39bff", "#2a1f3a"),
        _ => ("#c8c8d0", "#2a2a2e"),
    };
}

/// <summary>Die mitgelieferten Akzentfarben. Der Hintergrund bleibt schwarz – sie färben nur, was hervorsticht.</summary>
public static class AccentColors
{
    /// <summary>Das Blau aus Entwurf A – Vorgabe für neue Installationen.</summary>
    public const string DefaultId = "sky";

    /// <summary>Die Farbe, die in Windows eingestellt ist.</summary>
    public const string WindowsId = "windows";

    /// <summary>Alle Farben mit ihrem Namen in der Sprache der Oberfläche; die erste ist die von Windows.</summary>
    public static IReadOnlyList<AccentColor> All => BindableList.Of<AccentColor>(
    [
        new(WindowsId, L.Get("AccentWindows"), null),
        new(DefaultId, L.Get("AccentSky"), "#5fb3ff"),
        new("violet", L.Get("AccentViolet"), "#7c4dff"),
        new("blue", L.Get("AccentBlue"), "#2f7bff"),
        new("teal", L.Get("AccentTeal"), "#14b8a6"),
        new("green", L.Get("AccentGreen"), "#2ea043"),
        new("orange", L.Get("AccentOrange"), "#f08c2e"),
        new("red", L.Get("AccentRed"), "#e5484d"),
        new("pink", L.Get("AccentPink"), "#e0569b"),
    ]);

    /// <summary>Die Farbe mit dieser Kennung; bei einer unbekannten die Vorgabe.</summary>
    public static AccentColor Find(string? id)
    {
        var all = All;
        return all.FirstOrDefault(color => color.Id == id) ?? all.First(color => color.Id == DefaultId);
    }
}
