namespace NexusSSH.Core.Terminals;

/// <summary>
/// Farbschema des Terminals (Spec §5, „Terminal Themes“). Farben als <c>#rrggbb</c>; <see cref="Ansi"/> sind die
/// 16 ANSI-Farben in der üblichen Reihenfolge (schwarz, rot, grün, gelb, blau, magenta, cyan, weiß, dann die
/// hellen Varianten).
/// </summary>
public sealed record TerminalTheme(
    string Id,
    string Name,
    string Background,
    string Foreground,
    string Cursor,
    string Selection,
    IReadOnlyList<string> Ansi)
{
    public override string ToString() => Name;
}

/// <summary>Die mitgelieferten Farbschemata. Alle sind dunkel – die App ist es auch.</summary>
public static class TerminalThemes
{
    public const string DefaultId = "black";

    public const int DefaultFontSize = 14;
    public const int MinFontSize = 8;
    public const int MaxFontSize = 32;

    /// <summary>Schriften zur Auswahl; die erste ist der Standard. Jede eigene, auf dem Rechner installierte geht auch.</summary>
    public static IReadOnlyList<string> Fonts { get; } = BindableList.Of<string>(["Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"]);

    /// <summary>
    /// Der Name einer Schrift, wie er an die Terminal-Seite gehen darf: nur Buchstaben, Ziffern, Leerzeichen, Punkt
    /// und Bindestrich – er landet dort in einer CSS-Angabe. Alles andere ergibt die Standardschrift.
    /// </summary>
    public static string Font(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= 64 && trimmed.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '.') ? trimmed : Fonts[0];
    }

    public static IReadOnlyList<TerminalTheme> All { get; } = BindableList.Of<TerminalTheme>(
    [
        // Campbell auf reinem Schwarz, wie das Fenster darum (auf OLED leuchtet dort kein Pixel)
        new(
            DefaultId,
            "Black",
            "#000000",
            "#cccccc",
            "#ffffff",
            "#5a5a5a",
            [
                "#000000", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc",
                "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2",
            ]),

        // Standardschema von Windows Terminal
        new(
            "campbell",
            "Campbell",
            "#0c0c0c",
            "#cccccc",
            "#ffffff",
            "#5a5a5a",
            [
                "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc",
                "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2",
            ]),
        new(
            "one-half-dark",
            "One Half Dark",
            "#282c34",
            "#dcdfe4",
            "#ffffff",
            "#474e5d",
            [
                "#282c34", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#dcdfe4",
                "#5a6374", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#dcdfe4",
            ]),
        new(
            "dracula",
            "Dracula",
            "#282a36",
            "#f8f8f2",
            "#f8f8f2",
            "#44475a",
            [
                "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
                "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff",
            ]),
        new(
            "nord",
            "Nord",
            "#2e3440",
            "#d8dee9",
            "#d8dee9",
            "#434c5e",
            [
                "#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0",
                "#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4",
            ]),
        new(
            "gruvbox-dark",
            "Gruvbox Dark",
            "#282828",
            "#ebdbb2",
            "#ebdbb2",
            "#504945",
            [
                "#282828", "#cc241d", "#98971a", "#d79921", "#458588", "#b16286", "#689d6a", "#a89984",
                "#928374", "#fb4934", "#b8bb26", "#fabd2f", "#83a598", "#d3869b", "#8ec07c", "#ebdbb2",
            ]),
        new(
            "solarized-dark",
            "Solarized Dark",
            "#002b36",
            "#839496",
            "#93a1a1",
            "#073642",
            [
                "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
                "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3",
            ]),
        new(
            "tango-dark",
            "Tango Dark",
            "#000000",
            "#d3d7cf",
            "#ffffff",
            "#4d4d4d",
            [
                "#000000", "#cc0000", "#4e9a06", "#c4a000", "#3465a4", "#75507b", "#06989a", "#d3d7cf",
                "#555753", "#ef2929", "#8ae234", "#fce94f", "#729fcf", "#ad7fa8", "#34e2e2", "#eeeeec",
            ]),
    ]);

    /// <summary>Das Schema mit dieser Kennung; eine unbekannte ergibt das Standardschema.</summary>
    public static TerminalTheme Find(string? id) => Find(id, null);

    /// <summary>
    /// Das Schema mit dieser Kennung unter den mitgelieferten und den eigenen des Benutzers (<paramref name="custom"/>);
    /// eine unbekannte ergibt das Standardschema.
    /// </summary>
    public static TerminalTheme Find(string? id, IReadOnlyList<TerminalTheme>? custom)
        => All.Concat(custom ?? []).FirstOrDefault(theme => theme.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Ein Schema, das der Benutzer importiert hat – nur solche lassen sich entfernen.</summary>
    public static bool IsCustom(TerminalTheme? theme) => theme is not null && theme.Id.StartsWith("custom-", StringComparison.Ordinal);

    /// <summary>So viele eigene Schemata merkt sich die App.</summary>
    public const int MaxCustom = 50;
}
