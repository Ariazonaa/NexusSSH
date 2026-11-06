using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Cursor, Abstände und Ligaturen des Terminals – und das Bereinigen beim Einfügen als Einstellung.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalLookTests
{
    public TerminalLookTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Out_of_the_box_the_terminal_looks_as_it_always_did_and_cleans_what_is_pasted()
    {
        var settings = new AppSettings();

        Assert.Equal(new TerminalLook("block", true, 1.0, 0, true), TerminalLook.From(settings));
        Assert.True(settings.PasteClean);
        Assert.False(settings.PasteTabs);
    }

    [Theory]
    [InlineData("bar", "bar")]
    [InlineData("underline", "underline")]
    [InlineData("beam", "block")]
    [InlineData(null, "block")]
    public void Only_cursor_shapes_that_exist_reach_the_page(string? wanted, string expected)
        => Assert.Equal(expected, TerminalLook.Cursor(wanted));

    [Fact]
    public void Spacing_stays_within_what_the_page_accepts()
    {
        Assert.Equal(1.0, TerminalLook.Height(0.2));
        Assert.Equal(2.0, TerminalLook.Height(9));
        Assert.Equal(1.3, TerminalLook.Height(1.3049));
        Assert.Equal(1.0, TerminalLook.Height(double.NaN));
        Assert.Equal(0, TerminalLook.Spacing(-3));
        Assert.Equal(8, TerminalLook.Spacing(40));
    }

    [Fact]
    public void A_settings_file_edited_by_hand_is_brought_back_into_range()
    {
        var settings = new AppSettings { TerminalCursor = "wobble", TerminalLineHeight = 7, TerminalLetterSpacing = -2 };

        settings.Sanitize();

        Assert.Equal(("block", 2.0, 0), (settings.TerminalCursor, settings.TerminalLineHeight, settings.TerminalLetterSpacing));
    }

    [Fact]
    public void Changing_a_setting_tells_the_page_once_and_nothing_when_nothing_changed()
    {
        var shell = TestShell.Create(out _);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.TerminalCursor = "bar";
        shell.TerminalCursor = "bar";
        shell.TerminalCursor = "nonsense";
        Assert.Equal("block", shell.TerminalCursor);
        shell.TerminalLineHeight = 1.5;
        shell.TerminalLetterSpacing = 99;
        shell.TerminalLigatures = false;
        shell.TerminalCursorBlink = false;
        shell.PasteTabs = true;
        shell.PasteClean = true;

        Assert.Equal(new TerminalLook("block", false, 1.5, 8, false), shell.TerminalLook);
        Assert.Equal(7, changed.Count(name => name == nameof(shell.TerminalLook)));
        Assert.Equal(2, changed.Count(name => name == nameof(shell.TerminalCursor)));
        Assert.DoesNotContain(nameof(shell.PasteClean), changed);
    }

    [Fact]
    public void The_page_gets_look_and_paste_rules_under_the_names_it_reads()
    {
        var look = new TerminalLook("underline", false, 1.4, 2, true);

        var message = JsonDocument.Parse(TerminalMessages.Options(TerminalThemes.All[0], 14, [], look: look, pasteClean: false, pasteTabs: true)).RootElement;

        var sent = message.GetProperty("look");
        Assert.Equal("underline", sent.GetProperty("cursorStyle").GetString());
        Assert.False(sent.GetProperty("cursorBlink").GetBoolean());
        Assert.Equal(1.4, sent.GetProperty("lineHeight").GetDouble());
        Assert.Equal(2, sent.GetProperty("letterSpacing").GetInt32());
        Assert.True(sent.GetProperty("ligatures").GetBoolean());
        Assert.False(message.GetProperty("pasteClean").GetBoolean());
        Assert.True(message.GetProperty("pasteTabs").GetBoolean());

        // Ohne Angabe schickt die App keine Form: Die Seite bleibt bei dem, was sie hat.
        Assert.False(JsonDocument.Parse(TerminalMessages.Options(TerminalThemes.All[0], 14, [])).RootElement.TryGetProperty("look", out _));
    }

    [Fact]
    public void Images_from_the_server_are_off_until_the_user_switches_them_on()
    {
        Assert.False(new AppSettings().TerminalImages);
        Assert.False(JsonDocument.Parse(TerminalMessages.Options(TerminalThemes.All[0], 14, [])).RootElement.GetProperty("images").GetBoolean());

        var shell = TestShell.Create(out _);
        var told = 0;
        shell.PropertyChanged += (_, e) => told += e.PropertyName == nameof(shell.TerminalLook) ? 1 : 0;
        shell.TerminalImages = true;

        Assert.True(shell.Settings.TerminalImages);
        Assert.Equal(1, told);
        Assert.True(JsonDocument.Parse(TerminalMessages.Options(TerminalThemes.All[0], 14, [], images: shell.TerminalImages)).RootElement.GetProperty("images").GetBoolean());
    }
}
