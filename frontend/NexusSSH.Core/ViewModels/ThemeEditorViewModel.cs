using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Farbe eines Farbschemas im Editor: wofür sie steht und ihr Wert als <c>#rrggbb</c>.</summary>
public sealed partial class ThemeColorSlot : ObservableObject
{
    public ThemeColorSlot(string label, string hex)
    {
        Label = label;
        Hex = hex;
    }

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Hex { get; set; }

    /// <summary>Diese Farbe zeigt der Farbwähler gerade.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Name und Wert – Tooltip und Name für Screenreader.</summary>
    public string Summary => $"{Label}, {Hex}";

    public override string ToString() => Summary;
}

/// <summary>
/// Formular zum Anlegen oder Ändern eines eigenen Farbschemas: ein Name und zwanzig Farben. Was herauskommt,
/// erfüllt dieselben Regeln wie ein importiertes Schema (<see cref="ThemeImport.IsValid"/>) – seine Farben erreichen
/// die Terminal-Seite.
/// </summary>
public sealed partial class ThemeEditorViewModel : ObservableObject
{
    private readonly TerminalTheme _source;
    private readonly IReadOnlyList<TerminalTheme> _existing;

    /// <param name="source">Das Schema, von dem ausgegangen wird – das zu ändernde, oder die Vorlage für ein neues.</param>
    /// <param name="isNew">Ein neues Schema entsteht; sonst wird <paramref name="source"/> geändert und behält seine Kennung.</param>
    /// <param name="existing">Alle Schemata – ein neues darf keinem eigenen den Namen nehmen.</param>
    public ThemeEditorViewModel(TerminalTheme source, bool isNew, IReadOnlyList<TerminalTheme> existing)
    {
        _source = source;
        _existing = existing;
        IsNew = isNew;
        Name = source.Name;

        string[] ansi = [L.Get("ThemeBlack"), L.Get("ThemeRed"), L.Get("ThemeGreen"), L.Get("ThemeYellow"), L.Get("ThemeBlue"), L.Get("ThemeMagenta"), L.Get("ThemeCyan"), L.Get("ThemeWhite")];
        Slots = BindableList.Of<ThemeColorSlot>(
        [
            new ThemeColorSlot(L.Get("ThemeBackground"), source.Background),
            new ThemeColorSlot(L.Get("ThemeForeground"), source.Foreground),
            new ThemeColorSlot(L.Get("ThemeCursor"), source.Cursor),
            new ThemeColorSlot(L.Get("ThemeSelection"), source.Selection),
            .. ansi.Select((name, index) => new ThemeColorSlot(name, source.Ansi[index])),
            .. ansi.Select((name, index) => new ThemeColorSlot(L.Get("ThemeBright", name), source.Ansi[index + 8])),
        ]);
        Selected = Slots[0];
        Selected.IsSelected = true;
    }

    public bool IsNew { get; }

    public string Title => IsNew ? L.Get("ThemeNewTitle") : L.Get("ThemeEditTitle");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(IsValid))]
    public partial string Name { get; set; }

    /// <summary>Hintergrund, Schrift, Cursor, Markierung, dann die sechzehn ANSI-Farben.</summary>
    public IReadOnlyList<ThemeColorSlot> Slots { get; }

    /// <summary>Die Farbe, die der Farbwähler zeigt.</summary>
    [ObservableProperty]
    public partial ThemeColorSlot Selected { get; private set; }

    /// <summary>Eine Farbe hat sich geändert – die Vorschau zeichnet neu.</summary>
    public event Action? Changed;

    public void Select(ThemeColorSlot slot)
    {
        if (!Slots.Contains(slot) || ReferenceEquals(slot, Selected))
        {
            return;
        }

        Selected.IsSelected = false;
        Selected = slot;
        slot.IsSelected = true;
    }

    /// <summary>Setzt die gewählte Farbe. Angenommen wird nur <c>#rrggbb</c>; alles andere ändert nichts.</summary>
    public bool SetColor(string? hex)
    {
        var color = hex?.Trim().ToLowerInvariant();
        if (color is not { Length: 7 } || color[0] != '#' || !color.Skip(1).All(char.IsAsciiHexDigit))
        {
            return false;
        }

        if (Selected.Hex != color)
        {
            Selected.Hex = color;
            Changed?.Invoke();
        }

        return true;
    }

    private string? CleanName => ThemeImport.CleanName(Name);

    /// <summary>Fehlertext zum Namen; leer, wenn er passt.</summary>
    public string NameError
    {
        get
        {
            if (CleanName is not { } name)
            {
                return L.Get("ThemeNameMissing");
            }

            // Ein neues Schema mit dem Namen eines vorhandenen würde es ersetzen – das wäre ein Versehen.
            return IsNew && _existing.Any(theme => theme.Id == ThemeImport.IdOf(name) || theme.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
                ? L.Get("ThemeNameTaken")
                : string.Empty;
        }
    }

    public bool IsValid => NameError.Length == 0;

    /// <summary>Das Schema aus den Eingaben. Ein geändertes behält seine Kennung – Profile verweisen auf sie.</summary>
    public TerminalTheme ToTheme()
    {
        var name = CleanName ?? _source.Name;
        return new TerminalTheme(
            IsNew ? ThemeImport.IdOf(name) : _source.Id,
            name,
            Slots[0].Hex,
            Slots[1].Hex,
            Slots[2].Hex,
            Slots[3].Hex,
            [.. Slots.Skip(4).Select(slot => slot.Hex)]);
    }
}
