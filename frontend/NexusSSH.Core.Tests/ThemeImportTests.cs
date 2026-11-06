using System.Globalization;
using System.Text;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Farbschemata aus Dateien anderer Terminals: iTerm2, Windows Terminal, Xresources.</summary>
public sealed class ThemeImportTests
{
    private static readonly string[] Ansi =
    [
        "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
        "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3",
    ];

    private static readonly string[] WindowsTerminalNames =
    [
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    private static string WindowsTerminal(string? name = "Solarized Mine", bool withExtras = true)
    {
        var text = new StringBuilder("{\n  // ein Kommentar, wie Windows Terminal ihn erlaubt\n");
        if (name is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"  \"name\": \"{name}\",\n");
        }

        text.Append("  \"background\": \"#002B36\",\n  \"foreground\": \"#839496\",\n");
        if (withExtras)
        {
            text.Append("  \"cursorColor\": \"#FFFFFF\",\n  \"selectionBackground\": \"#073642\",\n");
        }

        for (var index = 0; index < 16; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"  \"{WindowsTerminalNames[index]}\": \"{Ansi[index].ToUpperInvariant()}\",\n");
        }

        return text.Append('}').ToString();
    }

    private static string ITerm()
    {
        static string Color(string key, string hex)
        {
            double Share(int offset) => Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;
            return string.Create(CultureInfo.InvariantCulture, $"<key>{key}</key><dict><key>Alpha Component</key><real>1</real><key>Blue Component</key><real>{Share(5)}</real><key>Color Space</key><string>sRGB</string><key>Green Component</key><real>{Share(3)}</real><key>Red Component</key><real>{Share(1)}</real></dict>\n");
        }

        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>\n");
        for (var index = 0; index < 16; index++)
        {
            text.Append(Color($"Ansi {index} Color", Ansi[index]));
        }

        text.Append(Color("Background Color", "#002b36")).Append(Color("Foreground Color", "#839496")).Append(Color("Cursor Color", "#ffffff")).Append(Color("Selection Color", "#073642"));
        text.Append(Color("Bold Color", "#93a1a1"));
        return text.Append("</dict></plist>").ToString();
    }

    private static string Xresources()
    {
        var text = new StringBuilder("! Solarized dark\n#define S_base03 #002b36\n*background: #002b36\nURxvt*foreground:   #839496\n*.cursorColor: #fff\n*fading: 40\n");
        for (var index = 0; index < 16; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"*.color{index}: {Ansi[index]}\n");
        }

        return text.ToString();
    }

    [Fact]
    public void A_windows_terminal_scheme_is_read_with_its_name()
    {
        var theme = ThemeImport.Parse(WindowsTerminal(), "datei.json");

        Assert.NotNull(theme);
        Assert.Equal(("custom-solarized-mine", "Solarized Mine"), (theme.Id, theme.Name));
        Assert.Equal(("#002b36", "#839496", "#ffffff", "#073642"), (theme.Background, theme.Foreground, theme.Cursor, theme.Selection));
        Assert.Equal(Ansi, theme.Ansi);
        Assert.True(ThemeImport.IsValid(theme));
    }

    [Fact]
    public void A_settings_file_of_windows_terminal_yields_its_first_scheme()
    {
        var theme = ThemeImport.Parse($"{{ \"profiles\": {{}}, \"schemes\": [ {WindowsTerminal("First")}, {WindowsTerminal("Second")} ] }}", null);

        Assert.Equal("First", theme?.Name);
    }

    [Fact]
    public void An_iterm_file_is_read_and_named_after_the_file()
    {
        var theme = ThemeImport.Parse(ITerm(), Path.Combine("Downloads", "Solarized Dark.itermcolors"));

        Assert.NotNull(theme);
        Assert.Equal(("custom-solarized-dark", "Solarized Dark"), (theme.Id, theme.Name));
        Assert.Equal(("#002b36", "#839496", "#ffffff", "#073642"), (theme.Background, theme.Foreground, theme.Cursor, theme.Selection));
        Assert.Equal(Ansi, theme.Ansi);
    }

    [Fact]
    public void Xresources_are_read_whatever_stands_before_the_name()
    {
        var theme = ThemeImport.Parse(Xresources(), "solarized.Xresources");

        Assert.NotNull(theme);
        Assert.Equal(("#002b36", "#839496", "#ffffff"), (theme.Background, theme.Foreground, theme.Cursor));
        Assert.Equal(Ansi, theme.Ansi);
        Assert.Equal("solarized", theme.Name);
    }

    [Fact]
    public void What_the_file_does_not_say_is_derived_from_what_it_says()
    {
        var theme = ThemeImport.Parse(WindowsTerminal(name: null, withExtras: false), null);

        Assert.NotNull(theme);
        Assert.Equal("Imported", theme.Name);
        Assert.Equal(theme.Foreground, theme.Cursor);

        // Die Markierung liegt zwischen Hintergrund und Schrift.
        Assert.Equal("#2b4e56", theme.Selection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a scheme at all")]
    [InlineData("{ \"name\": \"half\", \"background\": \"#000000\", \"foreground\": \"#ffffff\" }")]
    [InlineData("<plist><dict><key>Background Color</key><dict></dict></dict></plist>")]
    [InlineData("<plist><dict>")]
    [InlineData("{ broken")]
    public void A_file_that_is_no_complete_scheme_yields_nothing(string text)
        => Assert.Null(ThemeImport.Parse(text, "x"));

    [Fact]
    public void Only_plain_colours_get_through()
    {
        // Eine „Farbe“, die keine ist, macht das Schema unvollständig – sie käme sonst bis in die Terminal-Seite.
        Assert.Null(ThemeImport.Parse(WindowsTerminal().Replace("#002B36", "red; background: url(x)", StringComparison.Ordinal), "x"));

        Assert.Equal("#aabbcc", ThemeImport.Color("#AABBCC"));
        Assert.Equal("#aabbcc", ThemeImport.Color("#abc"));
        Assert.Equal("#0a0b0c", ThemeImport.Color("rgb:0a/0B/0c"));
        Assert.Null(ThemeImport.Color("#12345"));
        Assert.Null(ThemeImport.Color("blue"));
        Assert.Null(ThemeImport.Color(null));
    }

    [Fact]
    public void A_name_from_a_file_is_cleaned_and_cut()
    {
        var theme = ThemeImport.Parse(WindowsTerminal(name: "  <b>Sehr\\u0007 langer Name, der kein Ende nimmt und immer weitergeht</b>  "), null);

        Assert.NotNull(theme);
        Assert.DoesNotContain('<', theme.Name);
        Assert.DoesNotContain('\u0007', theme.Name);
        Assert.True(theme.Name.Length <= ThemeImport.MaxName);
        Assert.StartsWith("custom-", theme.Id, StringComparison.Ordinal);
        Assert.True(ThemeImport.IsValid(theme));
    }

    [Fact]
    public void A_stored_scheme_is_checked_before_it_is_used()
    {
        var good = ThemeImport.Parse(WindowsTerminal(), null)!;

        Assert.True(ThemeImport.IsValid(good));
        Assert.False(ThemeImport.IsValid(null));
        Assert.False(ThemeImport.IsValid(good with { Id = "black" }));
        Assert.False(ThemeImport.IsValid(good with { Background = "javascript:alert(1)" }));
        Assert.False(ThemeImport.IsValid(good with { Ansi = [.. good.Ansi.Take(8)] }));
        Assert.False(ThemeImport.IsValid(good with { Name = "x\u0000" }));
    }

    [Fact]
    public void A_file_that_is_too_large_is_not_read()
        => Assert.Null(ThemeImport.Parse(WindowsTerminal() + new string(' ', ThemeImport.MaxLength), "x"));
}
