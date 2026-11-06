using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Das Bild hinter dem Terminal: von Haus aus keines, und nur ein echtes Bild erreicht die Terminal-Seite.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalBackdropTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    public TerminalBackdropTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Out_of_the_box_there_is_no_image()
    {
        var shell = TestShell.Create(out _);

        Assert.Equal(string.Empty, shell.TerminalBackgroundImage);
        Assert.False(shell.HasTerminalBackground);
        Assert.Equal(TerminalBackdrop.DefaultDim, shell.TerminalBackgroundDim);
    }

    [Fact]
    public void The_kind_is_told_from_the_first_bytes_not_from_a_name()
    {
        Assert.Equal("png", TerminalBackdrop.Kind(Png));
        Assert.Equal("jpeg", TerminalBackdrop.Kind([0xFF, 0xD8, 0xFF, 0xE0, 0]));
        Assert.Equal("webp", TerminalBackdrop.Kind([.. "RIFF"u8, 1, 2, 3, 4, .. "WEBP"u8, 0]));
        Assert.Equal("bmp", TerminalBackdrop.Kind([.. "BM"u8, .. new byte[12]]));
        Assert.Null(TerminalBackdrop.Kind("<svg onload=alert(1)>"u8));
        Assert.Null(TerminalBackdrop.Kind("MZ"u8));
        Assert.Null(TerminalBackdrop.Kind([]));
    }

    [Fact]
    public void Only_an_image_of_bearable_size_becomes_an_address_for_the_page()
    {
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), TerminalBackdrop.DataUrl(Png));
        Assert.Null(TerminalBackdrop.DataUrl("not an image"u8));
        Assert.Null(TerminalBackdrop.DataUrl([]));

        var huge = new byte[TerminalBackdrop.MaxBytes + 1];
        Png.CopyTo(huge, 0);
        Assert.Null(TerminalBackdrop.DataUrl(huge));
    }

    [Fact]
    public void The_message_carries_image_and_dimming_or_clears_both()
    {
        var with = JsonDocument.Parse(TerminalMessages.Backdrop(TerminalBackdrop.DataUrl(Png), 250)).RootElement;
        Assert.Equal("backdrop", with.GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", with.GetProperty("data").GetString());
        Assert.Equal(TerminalBackdrop.MaxDim, with.GetProperty("dim").GetInt32());

        var without = JsonDocument.Parse(TerminalMessages.Backdrop(null, 40)).RootElement;
        Assert.Equal(string.Empty, without.GetProperty("data").GetString());
    }

    [Fact]
    public void Path_and_dimming_are_tidied_and_reported_once()
    {
        var shell = TestShell.Create(out _);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.TerminalBackgroundImage = "  \"C:\\Bilder\\wald.png\"  ";
        shell.TerminalBackgroundImage = "C:\\Bilder\\wald.png";
        shell.TerminalBackgroundDim = 400;

        Assert.Equal("C:\\Bilder\\wald.png", shell.Settings.TerminalBackgroundImage);
        Assert.True(shell.HasTerminalBackground);
        Assert.Equal(TerminalBackdrop.MaxDim, shell.Settings.TerminalBackgroundDim);
        Assert.Equal(1, changed.Count(name => name == nameof(shell.TerminalBackgroundImage)));

        var settings = new AppSettings { TerminalBackgroundDim = -5 };
        settings.Sanitize();
        Assert.Equal(0, settings.TerminalBackgroundDim);
    }
}
