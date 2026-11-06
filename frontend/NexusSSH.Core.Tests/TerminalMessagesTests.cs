using System.Text;
using System.Text.Json;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

public sealed class TerminalMessagesTests
{
    [Fact]
    public void Host_messages_use_the_names_terminal_js_expects()
    {
        Assert.Equal("""{"type":"open","id":"a1"}""", TerminalMessages.Open("a1"));
        Assert.Equal("""{"type":"close","id":"a1"}""", TerminalMessages.Close("a1"));
        Assert.Equal("""{"type":"clear","id":"a1"}""", TerminalMessages.Clear("a1"));
        Assert.Equal("""{"type":"findClear","id":"a1"}""", TerminalMessages.FindClear("a1"));
    }

    [Fact]
    public void The_layout_is_a_tree_with_shares_the_active_and_the_zoomed_pane()
    {
        SplitNode<string> tree = new SplitBranch<string>(
            true,
            [
                new SplitLeaf<string>("a1"),
                new SplitBranch<string>(false, [new SplitLeaf<string>("b2"), new SplitLeaf<string>("c3")], [0.6, 0.4]),
            ],
            [0.5, 0.5]);
        Assert.Equal(
            """{"type":"layout","id":"b2","tree":{"dir":"row","shares":[0.5,0.5],"items":[{"id":"a1"},{"dir":"column","shares":[0.6,0.4],"items":[{"id":"b2"},{"id":"c3"}]}]},"zoom":"b2","fixed":true}""",
            TerminalMessages.Layout(tree, "b2", "b2", @fixed: true));
        Assert.Equal(
            """{"type":"layout","id":"a1","tree":{"id":"a1"}}""",
            TerminalMessages.Layout(new SplitLeaf<string>("a1"), "a1", null, @fixed: false));

        // Ohne Sitzung zeigt die Seite nichts.
        Assert.Equal("""{"type":"layout"}""", TerminalMessages.Layout(null, null, null, @fixed: false));
    }

    [Fact]
    public void Shares_and_moves_from_the_page_are_read_and_anything_odd_is_dropped()
    {
        var shares = Assert.IsType<WebMessage.Shares>(TerminalMessages.Parse("""{"type":"shares","id":"a1","path":[1,0],"shares":[0.3,0.7]}"""));
        Assert.Equal("a1", shares.Id);
        Assert.Equal([1, 0], shares.Path);
        Assert.Equal([0.3, 0.7], shares.Values);
        Assert.Empty(Assert.IsType<WebMessage.Shares>(TerminalMessages.Parse("""{"type":"shares","id":"a1","path":[],"shares":[0.5,0.5]}""")).Path);

        Assert.Equal(new WebMessage.Moved("a1", "b2", PaneSide.Left), TerminalMessages.Parse("""{"type":"move","id":"a1","target":"b2","side":"left"}"""));
        Assert.Equal(PaneSide.Bottom, Assert.IsType<WebMessage.Moved>(TerminalMessages.Parse("""{"type":"move","id":"a1","target":"b2","side":"bottom"}""")).Side);

        var deep = string.Join(",", Enumerable.Repeat(0, SplitTree.MaxDepth + 1));
        var many = string.Join(",", Enumerable.Repeat("0.05", 17));
        string[] odd =
        [
            """{"type":"shares","id":"a1","path":[-1],"shares":[0.5,0.5]}""",
            """{"type":"shares","id":"a1","path":[99],"shares":[0.5,0.5]}""",
            $$"""{"type":"shares","id":"a1","path":[{{deep}}],"shares":[0.5,0.5]}""",
            """{"type":"shares","id":"a1","path":[0],"shares":[1e309,0.5]}""",
            """{"type":"shares","id":"a1","path":[0],"shares":[1]}""",
            $$"""{"type":"shares","id":"a1","path":[0],"shares":[{{many}}]}""",
            """{"type":"shares","id":"a1","path":[0],"shares":[0.5,null]}""",
            """{"type":"shares","id":"a1","path":[null],"shares":[0.5,0.5]}""",
            """{"type":"shares","id":"a1","shares":[0.5,0.5]}""",
            """{"type":"shares","id":"a1","path":[0]}""",
            """{"type":"shares","path":[0],"shares":[0.5,0.5]}""",
            """{"type":"move","id":"a1","target":"b2","side":"middle"}""",
            """{"type":"move","id":"a1","target":"b2"}""",
            """{"type":"move","id":"a1","side":"left"}""",
            """{"type":"move","target":"b2","side":"left"}""",
            """{"type":"move","id":"","target":"b2","side":"left"}""",
        ];
        foreach (var json in odd)
        {
            Assert.Null(TerminalMessages.Parse(json));
        }
    }

    [Fact]
    public void Output_travels_as_base64_so_split_utf8_survives()
    {
        var half = Encoding.UTF8.GetBytes("ä")[..1];

        Assert.Equal("""{"type":"write","id":"a1","data":"ww=="}""", TerminalMessages.Write("a1", half));
    }

    [Fact]
    public void Options_carry_the_theme_in_xterm_names_the_font_size_and_the_shortcuts()
    {
        var theme = TerminalThemes.Find("dracula");

        var message = JsonDocument.Parse(TerminalMessages.Options(theme, 16, [new TerminalShortcut("KeyF", true, "terminal.find")])).RootElement;

        Assert.Equal("options", message.GetProperty("type").GetString());
        Assert.Equal(16, message.GetProperty("fontSize").GetInt32());
        var colors = message.GetProperty("theme");
        Assert.Equal("#282a36", colors.GetProperty("background").GetString());
        Assert.Equal("#282a36", colors.GetProperty("cursorAccent").GetString());
        Assert.Equal("#44475a", colors.GetProperty("selectionBackground").GetString());
        Assert.Equal("#ff5555", colors.GetProperty("red").GetString());
        Assert.Equal("#ffffff", colors.GetProperty("brightWhite").GetString());
        var shortcut = Assert.Single(message.GetProperty("shortcuts").EnumerateArray());
        Assert.Equal("KeyF", shortcut.GetProperty("code").GetString());
        Assert.True(shortcut.GetProperty("shift").GetBoolean());
        Assert.Equal("terminal.find", shortcut.GetProperty("id").GetString());
    }

    [Fact]
    public void Search_messages_carry_the_query_and_its_options()
    {
        Assert.Equal(
            """{"type":"find","id":"a1","data":"error","matchCase":true,"backwards":false,"incremental":true}""",
            TerminalMessages.Find("a1", "error", matchCase: true, backwards: false, incremental: true));
        Assert.Equal("""{"type":"grep","data":"error","matchCase":false}""", TerminalMessages.Grep("error", matchCase: false));
        Assert.Equal(
            """{"type":"reveal","id":"a1","data":"error","matchCase":false,"row":42}""",
            TerminalMessages.Reveal("a1", 42, "error", matchCase: false));
    }

    [Fact]
    public void Parses_ready_input_resize_and_shortcut()
    {
        Assert.IsType<WebMessage.Ready>(TerminalMessages.Parse("""{"type":"ready"}"""));

        var input = Assert.IsType<WebMessage.Input>(TerminalMessages.Parse("""{"type":"input","id":"a1","data":"ls\r"}"""));
        Assert.Equal("a1", input.Id);
        Assert.Equal("ls\r", Encoding.UTF8.GetString(input.Data));

        var resize = Assert.IsType<WebMessage.Resize>(TerminalMessages.Parse("""{"type":"resize","id":"a1","cols":120,"rows":30}"""));
        Assert.Equal((120, 30), (resize.Columns, resize.Rows));

        var shortcut = Assert.IsType<WebMessage.Shortcut>(TerminalMessages.Parse("""{"type":"shortcut","key":"terminal.find"}"""));
        Assert.Equal("terminal.find", shortcut.Key);
    }

    [Fact]
    public void Parses_focus_and_search_results()
    {
        var focused = Assert.IsType<WebMessage.Focused>(TerminalMessages.Parse("""{"type":"focus","id":"b2"}"""));
        Assert.Equal("b2", focused.Id);

        var found = Assert.IsType<WebMessage.Found>(TerminalMessages.Parse("""{"type":"found","id":"a1","index":2,"count":12}"""));
        Assert.Equal((2, 12), (found.Index, found.Count));

        // xterm meldet −1, wenn es die Nummer der Fundstelle nicht kennt (sehr viele Treffer).
        var unknown = Assert.IsType<WebMessage.Found>(TerminalMessages.Parse("""{"type":"found","id":"a1","index":-1,"count":2000}"""));
        Assert.Equal(-1, unknown.Index);

        var hits = Assert.IsType<WebMessage.Hits>(TerminalMessages.Parse(
            """{"type":"hits","data":"error","items":[{"id":"a1","row":7,"text":"error: disk full"},{"id":"b2","row":0,"text":"no error"}],"more":true}"""));
        Assert.Equal("error", hits.Query);
        Assert.True(hits.More);
        Assert.Equal([new TerminalHit("a1", 7, "error: disk full"), new TerminalHit("b2", 0, "no error")], hits.Items);
    }

    [Fact]
    public void Hits_are_cleaned_and_capped_because_they_are_server_output()
    {
        var items = string.Join(",", Enumerable.Range(0, TerminalMessages.MaxHits + 20).Select(row => $$"""{"id":"a1","row":{{row}},"text":"x"}"""));
        var many = Assert.IsType<WebMessage.Hits>(TerminalMessages.Parse($$"""{"type":"hits","data":"x","items":[{{items}}]}"""));
        Assert.Equal(TerminalMessages.MaxHits, many.Items.Count);
        Assert.True(many.More, "abgeschnitten heißt: es gab mehr");

        var long_ = new string('a', TerminalMessages.MaxHitLength + 50);
        var cleaned = Assert.IsType<WebMessage.Hits>(TerminalMessages.Parse(
            $$"""{"type":"hits","data":"a","items":[{"id":"a1","row":1,"text":"bell\u0007 and escape\u001b[31m"},{"id":"a1","row":2,"text":"{{long_}}"},{"id":"","row":3,"text":"ohne Sitzung"},{"id":"a1","row":-1,"text":"negative Zeile"},{"id":"a1","row":4}]}"""));
        Assert.Equal(2, cleaned.Items.Count);
        Assert.Equal("bell and escape[31m", cleaned.Items[0].Text);
        Assert.Equal(TerminalMessages.MaxHitLength, cleaned.Items[1].Text.Length);
        Assert.False(cleaned.More);
    }

    [Fact]
    public void The_scrollback_is_asked_for_by_session()
    {
        Assert.Equal("""{"type":"dump","id":"a1"}""", TerminalMessages.Dump("a1"));
    }

    [Fact]
    public void Parses_acknowledgements_for_flow_control()
    {
        var acknowledged = Assert.IsType<WebMessage.Acknowledged>(TerminalMessages.Parse("""{"type":"ack","id":"a1","count":262144}"""));

        Assert.Equal(("a1", 262144), (acknowledged.Id, acknowledged.Count));
    }

    [Fact]
    public void The_text_of_a_terminal_keeps_lines_and_tabs_and_loses_every_other_control_character()
    {
        var plain = Assert.IsType<WebMessage.Text>(TerminalMessages.Parse("""{"type":"text","id":"a1","data":"$ ls\n\tübung.txt 日本"}"""));
        Assert.Equal(("a1", "$ ls\n\tübung.txt 日本"), (plain.Id, plain.Data));

        // Der Text ist Ausgabe eines Servers und wird eine Datei: keine Escape-Folgen, kein Wagenrücklauf, kein NUL.
        var cleaned = Assert.IsType<WebMessage.Text>(TerminalMessages.Parse("""{"type":"text","id":"a1","data":"a\u001b[31mb\r\nc\u0000d\u0007\u009b"}"""));
        Assert.Equal("a[31mb\ncd", cleaned.Data);

        var empty = Assert.IsType<WebMessage.Text>(TerminalMessages.Parse("""{"type":"text","id":"a1","data":""}"""));
        Assert.Equal(string.Empty, empty.Data);
    }

    [Fact]
    public void A_text_longer_than_the_page_may_send_is_dropped()
    {
        var huge = new string('x', TerminalMessages.MaxDumpLength + 1);

        Assert.Null(TerminalMessages.Parse($$"""{"type":"text","id":"a1","data":"{{huge}}"}"""));
    }

    [Theory]
    [InlineData("""{"type":"ack","id":"a1"}""")] // ohne Zahl
    [InlineData("""{"type":"ack","id":"a1","count":0}""")]
    [InlineData("""{"type":"ack","id":"a1","count":-5}""")]
    [InlineData("""{"type":"ack","id":"a1","count":1.5}""")]
    [InlineData("""{"type":"ack","id":"a1","count":99999999999}""")]
    [InlineData("""{"type":"ack","count":10}""")]
    [InlineData("""{"type":"text","id":"a1"}""")]
    [InlineData("""{"type":"text","data":"x"}""")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"type":"launch-missiles"}""")]
    [InlineData("""{"type":"input","data":"x"}""")] // ohne Sitzung
    [InlineData("""{"type":"input","id":"","data":"x"}""")]
    [InlineData("""{"type":"input","id":"a1"}""")] // ohne Daten
    [InlineData("""{"type":"resize","id":"a1","cols":0,"rows":24}""")]
    [InlineData("""{"type":"resize","id":"a1","cols":80,"rows":100000}""")]
    [InlineData("""{"type":"resize","id":"a1","cols":"80","rows":24}""")]
    [InlineData("""{"type":"resize","id":"a1","cols":80}""")]
    [InlineData("""{"type":"shortcut"}""")]
    [InlineData("""{"type":"shortcut","key":"0123456789012345678901234567890123456789x"}""")] // länger als jede Befehlskennung
    [InlineData("""{"type":"focus"}""")]
    [InlineData("""{"type":"found","id":"a1","index":0}""")]
    [InlineData("""{"type":"found","id":"a1","index":-2,"count":1}""")]
    [InlineData("""{"type":"found","id":"a1","index":0,"count":-1}""")]
    [InlineData("""{"type":"hits","items":[]}""")] // ohne Suchtext
    [InlineData("""{"type":"hits","data":"x"}""")] // ohne Liste
    public void Anything_malformed_is_dropped(string json)
        => Assert.Null(TerminalMessages.Parse(json));
}
