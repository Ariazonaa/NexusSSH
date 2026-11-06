using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

// Eigene Farbschemata: aus Dateien anderer Terminals gelesen und neben die mitgelieferten gestellt.
public sealed partial class ShellViewModel
{
    /// <summary>Alle Farbschemata zur Wahl: die mitgelieferten, dahinter die des Benutzers.</summary>
    public IReadOnlyList<TerminalTheme> Themes => BindableList.Of<TerminalTheme>([.. TerminalThemes.All, .. Settings.CustomThemes]);

    /// <summary>
    /// Das eigene Farbschema eines Profils; <c>null</c>, wenn es keines nennt oder es das genannte nicht (mehr) gibt –
    /// dann gilt das allgemein eingestellte.
    /// </summary>
    public TerminalTheme? ThemeOverride(ServerProfile profile)
        => profile.ThemeId is { Length: > 0 } id ? Themes.FirstOrDefault(theme => theme.Id == id) : null;

    /// <summary>Das Farbschema, in dem das Terminal dieses Profils erscheint.</summary>
    public TerminalTheme ThemeFor(ServerProfile profile) => ThemeOverride(profile) ?? TerminalTheme;

    /// <summary>Das Farbschema des aktiven Tabs – danach richtet sich die Farbe der Karte um das Terminal.</summary>
    public TerminalTheme ShownTheme => ActiveSession is { } session ? ThemeFor(session.Profile) : TerminalTheme;

    /// <summary>Ob das gewählte Schema eines des Benutzers ist – nur das lässt sich entfernen.</summary>
    public bool CanRemoveTheme => TerminalThemes.IsCustom(TerminalTheme);

    /// <summary>
    /// Liest ein Farbschema aus dem Text einer Datei (iTerm2, Windows Terminal, Xresources), merkt es sich und wählt
    /// es. Ein Schema desselben Namens wird ersetzt. <c>null</c>, wenn die Datei kein vollständiges Schema ist oder
    /// schon zu viele da sind.
    /// </summary>
    public TerminalTheme? ImportTheme(string? text, string? fileName)
    {
        if (ThemeImport.Parse(text, fileName) is not { } theme)
        {
            return null;
        }

        var existing = Settings.CustomThemes.FindIndex(known => known.Id == theme.Id);
        if (existing >= 0)
        {
            Settings.CustomThemes[existing] = theme;
        }
        else if (Settings.CustomThemes.Count < TerminalThemes.MaxCustom)
        {
            Settings.CustomThemes.Add(theme);
        }
        else
        {
            return null;
        }

        ThemesChanged();
        TerminalTheme = theme;
        return theme;
    }

    /// <summary>Der Editor für ein neues Schema – ausgehend von dem, das gerade gewählt ist.</summary>
    public ThemeEditorViewModel NewTheme()
        => new(TerminalTheme with { Name = L.Get("ThemeCopyName", TerminalTheme.Name) }, isNew: true, Themes);

    /// <summary>Der Editor für das gewählte Schema; <c>null</c>, wenn es ein mitgeliefertes ist – die bleiben, wie sie sind.</summary>
    public ThemeEditorViewModel? EditTheme() => CanRemoveTheme ? new(TerminalTheme, isNew: false, Themes) : null;

    /// <summary>
    /// Merkt sich ein eigenes Schema (ein vorhandenes mit derselben Kennung wird ersetzt) und wählt es. <c>false</c>,
    /// wenn es keines ist, das die Terminal-Seite bekommen darf, oder schon zu viele da sind.
    /// </summary>
    public bool SaveTheme(TerminalTheme? theme)
    {
        if (!ThemeImport.IsValid(theme))
        {
            return false;
        }

        var existing = Settings.CustomThemes.FindIndex(known => known.Id == theme!.Id);
        if (existing >= 0)
        {
            Settings.CustomThemes[existing] = theme!;
        }
        else if (Settings.CustomThemes.Count < TerminalThemes.MaxCustom)
        {
            Settings.CustomThemes.Add(theme!);
        }
        else
        {
            return false;
        }

        // Erst wählen, dann melden: Terminals mit diesem Schema am Profil holen es sich aus der neuen Liste.
        TerminalTheme = theme!;
        ThemesChanged();
        return true;
    }

    /// <summary>Entfernt ein eigenes Schema; war es gewählt, gilt wieder das Standardschema. Mitgelieferte bleiben.</summary>
    public bool RemoveTheme(TerminalTheme? theme)
    {
        if (!TerminalThemes.IsCustom(theme) || Settings.CustomThemes.RemoveAll(known => known.Id == theme!.Id) == 0)
        {
            return false;
        }

        if (TerminalTheme.Id == theme!.Id)
        {
            TerminalTheme = TerminalThemes.All[0];
        }

        ThemesChanged();
        return true;
    }

    /// <summary>Pfad des Bilds hinter dem Terminal; leer = keines. Die Terminal-Seite bekommt es von <c>TerminalHost</c>.</summary>
    public string TerminalBackgroundImage
    {
        get => Settings.TerminalBackgroundImage;
        set
        {
            var path = value?.Trim().Trim('"') ?? string.Empty;
            if (path != Settings.TerminalBackgroundImage)
            {
                Settings.TerminalBackgroundImage = path;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTerminalBackground));
            }
        }
    }

    public bool HasTerminalBackground => Settings.TerminalBackgroundImage.Length > 0;

    /// <summary>Abdunklung des Bilds in Prozent – je höher, desto besser lesbar bleibt der Text.</summary>
    public int TerminalBackgroundDim
    {
        get => Settings.TerminalBackgroundDim;
        set
        {
            var dim = TerminalBackdrop.Dim(value);
            if (dim != Settings.TerminalBackgroundDim)
            {
                Settings.TerminalBackgroundDim = dim;
                OnPropertyChanged();
            }
        }
    }

    // Die Schemata stehen auch als Befehle in der Palette – die Liste entsteht deshalb neu.
    private void ThemesChanged()
    {
        _defaultCommands = BuildCommands();
        ApplyShortcuts();
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(CanRemoveTheme));
    }
}
