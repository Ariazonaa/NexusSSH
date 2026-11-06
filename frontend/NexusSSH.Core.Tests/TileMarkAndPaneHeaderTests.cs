using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Eigene Symbole und Buchstaben auf den Kacheln, und die Kopfzeile je Terminal in der geteilten Ansicht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TileMarkAndPaneHeaderTests
{
    public TileMarkAndPaneHeaderTests() => L.Init(AppLanguage.English);

    private static ShellViewModel Shell()
    {
        var shell = TestShell.Create(out _);
        foreach (var (name, group) in new[] { ("shop-web", "Acme/Shop"), ("shop-prod", "Acme/Shop/Prod"), ("mail", "Acme/Mail"), ("loose", string.Empty) })
        {
            Assert.NotNull(shell.SaveProfile(new ServerProfile(string.Empty, name, $"{name}.example.org", 22, "root", group, [])));
        }

        return shell;
    }

    private static ServerProfile Named(ShellViewModel shell, string name) => shell.Profiles.Single(profile => profile.Name == name);

    private static ProfileGroup Group(ShellViewModel shell, string path) => shell.ProfileGroups.Single(group => group.Path == path);

    [Theory]
    [InlineData("DB", "DB")]
    [InlineData(" a ", "a")]
    [InlineData("7", "7")]
    [InlineData("Äß", "Äß")]
    [InlineData("icon:cloud", "icon:cloud")]
    [InlineData("ABC", null)]
    [InlineData("a!", null)]
    [InlineData("<b", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("icon:nothing", null)]
    [InlineData("icon:", null)]
    [InlineData(null, null)]
    public void A_mark_is_one_of_the_symbols_or_up_to_two_letters(string? mark, string? expected)
        => Assert.Equal(expected, TileMarks.Normalize(mark));

    [Theory]
    // Nichts gewählt, kein System: der Anfangsbuchstabe, bei einem lokalen Terminal dessen Symbol.
    [InlineData(null, null, null, "web01", false, TileFace.Letters, "W")]
    [InlineData(null, null, null, "pi-hole", false, TileFace.Letters, "P")]
    [InlineData(null, null, null, "_ölserver", false, TileFace.Letters, "Ö")]
    [InlineData(null, null, null, "7zip", false, TileFace.Letters, "7")]
    [InlineData(null, null, null, "---", false, TileFace.Symbol, "")]
    [InlineData(null, null, null, "PowerShell", true, TileFace.Terminal, "")]
    [InlineData("no mark", null, "<b>", "mail", false, TileFace.Letters, "M")]
    // Das eigene Zeichen gewinnt – vor dem System, vor der Gruppe und vor dem lokalen Terminal.
    [InlineData("DB", null, null, "web01", false, TileFace.Letters, "DB")]
    [InlineData("DB", null, null, "PowerShell", true, TileFace.Letters, "DB")]
    [InlineData("icon:cloud", null, null, "web01", false, TileFace.Symbol, "icon:cloud")]
    [InlineData("icon:cloud", null, null, "PowerShell", true, TileFace.Symbol, "icon:cloud")]
    [InlineData("DB", "debian", "icon:cloud", "db02", false, TileFace.Letters, "DB")]
    [InlineData("icon:home", "ubuntu", "G", "web01", false, TileFace.Symbol, "icon:home")]
    // Sonst das Logo des Systems, auch vor dem Zeichen der Gruppe.
    [InlineData(null, "debian", null, "db02", false, TileFace.System, "debian")]
    [InlineData(null, "raspbian", "icon:home", "pi-hole", false, TileFace.System, "raspbian")]
    [InlineData("<b>", "ubuntu", "K", "web01", false, TileFace.System, "ubuntu")]
    // Ein System ohne Logo ist keines: Dann gilt das Zeichen der Gruppe, sonst der Buchstabe.
    [InlineData(null, "windows", "icon:storage", "nas", false, TileFace.Symbol, "icon:storage")]
    [InlineData(null, "windows", "KA", "nas", false, TileFace.Letters, "KA")]
    [InlineData(null, "windows", null, "nas", false, TileFace.Letters, "N")]
    [InlineData(null, null, "icon:shield", "fw", false, TileFace.Symbol, "icon:shield")]
    // Das Zeichen der Gruppe steht auch vor dem Symbol des lokalen Terminals.
    [InlineData(null, null, "icon:code", "PowerShell", true, TileFace.Symbol, "icon:code")]
    [InlineData(null, null, "nonsense!", "PowerShell", true, TileFace.Terminal, "")]
    public void The_square_of_a_tile_shows_the_own_mark_then_the_system_then_the_group_then_a_terminal_then_the_initial(
        string? mark, string? system, string? group, string name, bool local, TileFace kind, string text)
        => Assert.Equal((kind, text), TileMarks.Face(mark, system, group, name, local));

    [Fact]
    public void Every_known_system_has_a_logo_and_nothing_else_has_one()
    {
        Assert.All(ServerSystems.Ids, id => Assert.True(TileMarks.HasLogo(id), id));
        Assert.False(TileMarks.HasLogo(null));
        Assert.False(TileMarks.HasLogo(string.Empty));
        Assert.False(TileMarks.HasLogo("Debian"));
        Assert.False(TileMarks.HasLogo("windows"));
    }

    [Theory]
    [InlineData(1110, 4, 277)]
    [InlineData(880, 4, 220)]
    [InlineData(879, 3, 293)]
    [InlineData(500, 2, 250)]
    [InlineData(150, 1, 150)]
    [InlineData(0, 1, HomeTiles.MinSlot)]
    [InlineData(double.NaN, 1, HomeTiles.MinSlot)]
    public void The_tiles_of_the_home_page_fill_the_width_in_up_to_four_columns(double width, int columns, double slot)
        => Assert.Equal((columns, slot), (HomeTiles.Columns(width), HomeTiles.Slot(width)));

    [Fact]
    public void The_square_of_a_tile_takes_the_colours_of_the_draft_and_one_for_every_profile_colour()
    {
        Assert.Equal(("#4ade80", "#1d3a2a"), ProfileColors.Tile("green"));
        Assert.Equal(("#5fb3ff", "#1d2a3a"), ProfileColors.Tile("blue"));
        Assert.Equal(("#f5c46b", "#3a2a10"), ProfileColors.Tile("yellow"));
        Assert.Equal(("#c8c8d0", "#2a2a2e"), ProfileColors.Tile(null));
        Assert.Equal(ProfileColors.Tile(null), ProfileColors.Tile("purple"));

        var coloured = ProfileColors.All.Where(color => color.Hex is not null).Select(color => ProfileColors.Tile(color.Id)).ToList();
        Assert.Equal(coloured.Count, coloured.Distinct().Count());
        Assert.DoesNotContain(ProfileColors.Tile(null), coloured);
        Assert.All(coloured.SelectMany(pair => new[] { pair.Fore, pair.Back }), hex => Assert.Matches("^#[0-9a-f]{6}$", hex));
    }

    [Fact]
    public void Every_symbol_has_a_name_and_a_glyph_and_round_trips()
    {
        Assert.All(TileMarks.Symbols, symbol =>
        {
            Assert.Same(symbol, TileMarks.Symbol(TileMarks.OfSymbol(symbol)));
            Assert.Equal(1, symbol.Glyph.Length);
            Assert.False(string.IsNullOrWhiteSpace(symbol.Name));
            Assert.Equal(string.Empty, TileMarks.Letters(TileMarks.OfSymbol(symbol)));
        });
        Assert.Equal(TileMarks.Symbols.Count, TileMarks.Symbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.Equal(TileMarks.Symbols.Count, TileMarks.Symbols.Select(symbol => symbol.Name).Distinct().Count());
        Assert.Equal("DB", TileMarks.Letters("DB"));
        Assert.Null(TileMarks.Symbol("DB"));
    }

    [Fact]
    public void A_server_knows_its_own_mark_and_apart_from_it_that_of_the_nearest_group_above()
    {
        var shell = Shell();
        Assert.All(shell.Profiles, profile => Assert.Null(profile.Mark));
        Assert.All(shell.Profiles, profile => Assert.Null(profile.GroupMark));

        shell.SetGroupMark(Group(shell, "Acme/Shop"), "icon:shop");
        shell.SetMark(Named(shell, "shop-web"), "W1");
        shell.SetMark(Named(shell, "loose"), "icon:home");

        Assert.Equal(("W1", "icon:shop"), (Named(shell, "shop-web").Mark, Named(shell, "shop-web").GroupMark));
        Assert.Equal((null, "icon:shop"), (Named(shell, "shop-prod").Mark, Named(shell, "shop-prod").GroupMark));
        Assert.Equal((null, null), (Named(shell, "mail").Mark, Named(shell, "mail").GroupMark));
        Assert.Equal(("icon:home", null), (Named(shell, "loose").Mark, Named(shell, "loose").GroupMark));
        Assert.Equal("icon:shop", shell.OwnGroupMark(Group(shell, "Acme/Shop")));
        Assert.Null(shell.OwnMark(Named(shell, "shop-prod")));

        // Ohne eigenes Zeichen bleibt das der Gruppe; Unsinn ist dasselbe wie keines.
        shell.SetMark(Named(shell, "shop-web"), null);
        Assert.Equal((null, "icon:shop"), (Named(shell, "shop-web").Mark, Named(shell, "shop-web").GroupMark));
        shell.SetGroupMark(Group(shell, "Acme/Shop"), "<script>");
        Assert.Null(Named(shell, "shop-web").GroupMark);
        Assert.Empty(shell.Settings.GroupMarks);

        // Eine Sitzung nimmt das Zeichen mit ihrem Profil mit – für die Kopfzeile über dem Terminal.
        Assert.Equal("icon:home", shell.OpenSession(Named(shell, "loose")).Profile.Mark);
    }

    [Fact]
    public void The_system_of_a_server_comes_between_its_own_mark_and_that_of_its_group()
    {
        var shell = Shell();
        shell.SetGroupMark(Group(shell, "Acme/Shop"), "icon:shop");
        Assert.NotNull(shell.SaveProfile(Named(shell, "shop-prod") with { System = "debian" }));
        Assert.NotNull(shell.SaveProfile(Named(shell, "shop-web") with { System = "ubuntu" }));
        shell.SetMark(Named(shell, "shop-web"), "SW");

        (TileFace, string) FaceOf(string name)
        {
            var profile = Named(shell, name);
            return TileMarks.Face(profile.Mark, profile.System, profile.GroupMark, profile.Name, profile.IsLocal);
        }

        Assert.Equal((TileFace.Letters, "SW"), FaceOf("shop-web"));
        Assert.Equal((TileFace.System, "debian"), FaceOf("shop-prod"));
        Assert.Equal((TileFace.Letters, "M"), FaceOf("mail"));

        // Ohne bekanntes System erbt der Server das Zeichen der Gruppe wieder.
        Assert.NotNull(shell.SaveProfile(Named(shell, "shop-prod") with { System = null }));
        Assert.Equal((TileFace.Symbol, "icon:shop"), FaceOf("shop-prod"));
    }

    [Fact]
    public void The_heading_of_a_group_carries_its_mark_and_colour_and_the_other_sections_none()
    {
        var shell = Shell();

        // „Acme“ hat keine eigenen Server – es steht nur als Abschnitt ganz oben.
        var acme = shell.HomeGroups.Single(group => group.Path == "Acme");
        shell.SetGroupMark(acme, "AC");
        shell.SetGroupColor(acme, "blue");
        shell.SetGroupMark(Group(shell, "Acme/Shop"), "icon:shop");
        Assert.NotNull(shell.SaveProfile(Named(shell, "loose") with { IsFavorite = true }));
        Assert.NotNull(shell.SaveProfile(new ServerProfile(string.Empty, "other", "other.example.org", 22, "root", string.Empty, [])));

        // Ganz oben: Acme mit Zeichen und Farbe, Favoriten und „Ohne Gruppe“ ohne.
        var top = shell.HomeGroups.ToDictionary(group => group.Kind == GroupKind.Group ? group.Path : group.Kind.ToString());
        Assert.Equal(("AC", "blue", true), (top["Acme"].Mark, top["Acme"].Color, top["Acme"].HasMark));
        Assert.Equal((null, null, false), (top["Favorites"].Mark, top["Favorites"].Color, top["Favorites"].HasMark));
        Assert.Equal((null, null), (top["Ungrouped"].Mark, top["Ungrouped"].Color));

        // In Acme: Shop mit seinem eigenen Zeichen und der geerbten Farbe, Mail erbt beides.
        shell.OpenHomeFolder("Acme");
        var inner = shell.HomeGroups.Where(group => group.Kind == GroupKind.Group).ToDictionary(group => group.Path);
        Assert.Equal(("icon:shop", "blue"), (inner["Acme/Shop"].Mark, inner["Acme/Shop"].Color));
        Assert.Equal(("AC", "blue"), (inner["Acme/Mail"].Mark, inner["Acme/Mail"].Color));

        // Gefiltert über alle Ebenen: dieselben Zeichen.
        shell.HomeFilter = "shop";
        Assert.Contains(shell.HomeGroups, group => group.Path == "Acme/Shop/Prod" && group.Mark == "icon:shop" && group.Color == "blue");
    }

    [Fact]
    public void Marks_of_what_is_gone_are_forgotten_and_odd_ones_never_loaded()
    {
        var shell = Shell();
        var mail = Named(shell, "mail");
        shell.SetMark(mail, "M");
        shell.SetGroupMark(Group(shell, "Acme/Mail"), "icon:mail");

        shell.DeleteProfile(mail);

        Assert.Empty(shell.Settings.ServerMarks);
        Assert.Empty(shell.Settings.GroupMarks);

        var settings = new AppSettings
        {
            ServerMarks = new() { ["a"] = "icon:cloud", ["b"] = "javascript:1", [string.Empty] = "X", ["c"] = " q " },
            GroupMarks = new() { ["Ops"] = "OPS!", ["Dev"] = "D" },
        }.Sanitize();
        Assert.Equal(new Dictionary<string, string> { ["a"] = "icon:cloud", ["c"] = "q" }, settings.ServerMarks);
        Assert.Equal(new Dictionary<string, string> { ["Dev"] = "D" }, settings.GroupMarks);
    }

    [Fact]
    public void The_header_of_a_terminal_gets_a_clean_short_name_and_a_known_state()
    {
        var message = JsonDocument.Parse(TerminalMessages.Label("s1", "web01\u001b[31m‮evil\n", ConnectionState.Authenticating, "Take out")).RootElement;
        Assert.Equal("label", message.GetProperty("type").GetString());
        Assert.Equal("s1", message.GetProperty("id").GetString());
        Assert.Equal("connecting", message.GetProperty("state").GetString());
        Assert.Equal("Take out", message.GetProperty("hint").GetString());
        var name = message.GetProperty("data").GetString()!;
        Assert.StartsWith("web01", name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, character => char.IsControl(character) || character == '‮');

        var longName = JsonDocument.Parse(TerminalMessages.Label("s1", new string('x', 500), ConnectionState.Connected, null)).RootElement;
        Assert.Equal(TerminalMessages.MaxLabelLength, longName.GetProperty("data").GetString()!.Length);
        Assert.Equal("connected", longName.GetProperty("state").GetString());
        Assert.False(longName.TryGetProperty("hint", out _));

        Assert.All(
            new[] { ConnectionState.Idle, ConnectionState.Closed },
            state => Assert.Equal("closed", JsonDocument.Parse(TerminalMessages.Label("s", "n", state, null)).RootElement.GetProperty("state").GetString()));

        Assert.Equal(new WebMessage.PaneClosed("b2"), TerminalMessages.Parse("""{"type":"unsplit","id":"b2"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"unsplit"}"""));
    }

    [Fact]
    public void Taking_a_terminal_out_of_the_split_leaves_the_others_on_screen()
    {
        var shell = TestShell.Create(out var backend);
        var first = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: true)!;
        Assert.Equal([first, second, third], shell.Layout.Panes);

        // Nicht das aktive: Es bleibt aktiv, das andere geht.
        shell.Unsplit(first);
        Assert.Equal([second, third], shell.Layout.Panes);
        Assert.Same(third, shell.ActiveSession);
        Assert.False(first.IsSplit);
        Assert.Contains(first, shell.Sessions);

        // Das aktive: Zu sehen bleibt, was übrig ist – hier das letzte, und das ist dann keine Teilung mehr.
        shell.Unsplit(third);
        Assert.Same(second, shell.ActiveSession);
        Assert.Equal([second], shell.Layout.Panes);
        Assert.False(second.IsSplit);
        Assert.Equal(3, shell.Sessions.Count);

        // Was nicht geteilt ist, lässt sich nicht herausnehmen.
        shell.Unsplit(second);
        Assert.Equal([second], shell.Layout.Panes);
    }
}
