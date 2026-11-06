using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

public class RichCopyTests
{
    private static readonly IReadOnlyList<CopyRun> Runs =
    [
        new("ubuntu@web01", "#16c60c", null, Bold: true, Italic: false, Underline: false),
        new(":~$ ls <a> & \"b\"\n", null, null, false, false, false),
        new("Zeile zwei\tä€😀", "#e74856", "#1e1e1e", false, Italic: true, Underline: true),
    ];

    [Fact]
    public void Html_carries_the_colours_escapes_the_text_and_keeps_the_line_break()
    {
        var html = RichCopy.Html(Runs, "Cascadia Mono", 14, "#cccccc", "#000000");

        Assert.StartsWith("<div style=\"font-family:'Cascadia Mono',Consolas,monospace;font-size:14px;white-space:pre;color:#cccccc;background-color:#000000;", html);
        Assert.Contains("<span style=\"color:#16c60c;font-weight:bold;\">ubuntu@web01</span>", html);
        Assert.Contains(":~$ ls &lt;a&gt; &amp; &quot;b&quot;<br>", html);
        Assert.Contains("<span style=\"color:#e74856;background-color:#1e1e1e;font-style:italic;text-decoration:underline;\">Zeile zwei\tä€😀</span>", html);
        Assert.EndsWith("</div>", html);
    }

    [Fact]
    public void The_clipboard_form_of_html_names_its_parts_in_bytes()
    {
        var fragment = "<div>漢字 ä€😀</div>";
        var bytes = RichCopy.ClipboardHtml(fragment);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        int Number(string name) => int.Parse(text.Substring(text.IndexOf(name + ":", StringComparison.Ordinal) + name.Length + 1, 10), System.Globalization.CultureInfo.InvariantCulture);
        string Part(int from, int to) => System.Text.Encoding.UTF8.GetString(bytes, from, to - from);

        Assert.StartsWith("Version:0.9\r\nStartHTML:", text);
        Assert.Equal(fragment, Part(Number("StartFragment"), Number("EndFragment")));
        Assert.Equal("<html><body><!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>", Part(Number("StartHTML"), Number("EndHTML")));
        Assert.Equal(bytes.Length, Number("EndHTML"));
    }

    [Fact]
    public void Rtf_has_a_colour_table_and_writes_everything_beyond_ascii_as_numbers()
    {
        var rtf = RichCopy.Rtf(Runs, "Cascadia Mono", 14, "#cccccc", "#000000");

        Assert.StartsWith(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fmodern Cascadia Mono;}}{\colortbl;\red204\green204\blue204;\red0\green0\blue0;\red22\green198\blue12;\red231\green72\blue86;\red30\green30\blue30;}\f0\fs21 ", rtf);
        Assert.Contains(@"{\cf3\chshdng0\chcbpat2\cb2\b ubuntu@web01}", rtf);
        Assert.Contains(@"\line ", rtf);
        Assert.Contains(@"{\cf4\chshdng0\chcbpat5\cb5\i\ul Zeile zwei\tab \u228?\u" + "8364?" + @"\u-10179?\u-8704?}", rtf);
        Assert.True(rtf.All(character => character < 0x80));
        Assert.Equal(rtf.Count(character => character == '{'), rtf.Count(character => character == '}'));
    }

    [Fact]
    public void Braces_and_backslashes_of_the_text_cannot_end_the_rtf()
    {
        var rtf = RichCopy.Rtf([new(@"a}{\rtf1 evil}\par", null, null, false, false, false)], "x", 14, "#cccccc", "#000000");

        Assert.Contains(@"a\}\{\\rtf1 evil\}\\par}", rtf);
    }

    [Fact]
    public void What_the_page_sends_is_cleaned_before_anything_is_built_from_it()
    {
        var runs = RichCopy.Clean(
        [
            ("ok", "#ABCDEF", "red", 7),
            ("\u001b[31mrot\u0007‮", "#12345", "url(x)", 0),
            (null, null, null, 0),
            (string.Empty, null, null, 0),
            ("\u0000", null, null, 0),
        ]);

        Assert.Equal(
            [new CopyRun("ok", "#abcdef", null, true, true, true), new CopyRun("[31mrot", null, null, false, false, false)],
            runs);
        Assert.Null(RichCopy.Clean(null));
        Assert.Null(RichCopy.Clean([(null, null, null, 0)]));
    }

    [Fact]
    public void The_amount_is_bounded()
    {
        var many = RichCopy.Clean(Enumerable.Range(0, RichCopy.MaxRuns + 500).Select(_ => ((string?)"x", (string?)null, (string?)null, 0)));
        Assert.Equal(RichCopy.MaxRuns, many!.Count);

        var long_ = RichCopy.Clean([(new string('a', RichCopy.MaxLength - 1), null, null, 0), ("bcdef", null, null, 0), ("more", null, null, 0)]);
        Assert.Equal(RichCopy.MaxLength, RichCopy.Plain(long_!).Length);
    }

    [Fact]
    public void A_font_name_cannot_carry_markup()
    {
        var html = RichCopy.Html([new("x", null, null, false, false, false)], "Evil';}</div><script>", 14, "#cccccc", "#000000");

        Assert.DoesNotContain("<script", html);
        Assert.Contains("font-family:'Evildivscript'", html);
    }

    [Fact]
    public void The_page_answers_a_copy_request_with_checked_pieces()
    {
        var rich = Assert.IsType<WebMessage.Copied>(TerminalMessages.Parse(
            """{"type":"copied","id":"s1","key":"rich","data":"ab\ncd","fore":"#CCCCCC","back":"javascript:1","runs":[{"t":"ab","f":"#ff0000","b":null,"s":1},{"t":"\n","f":null,"b":null,"s":0},null,{"t":"cd","f":"nope","s":2}]}"""));
        Assert.False(rich.OneLine);
        Assert.Equal("ab\ncd", rich.Content);
        Assert.Equal("#cccccc", rich.Foreground);
        Assert.Null(rich.Background);
        Assert.Equal(
            [new CopyRun("ab", "#ff0000", null, true, false, false), new CopyRun("\n", null, null, false, false, false), new CopyRun("cd", null, null, false, true, false)],
            rich.Runs);

        var line = Assert.IsType<WebMessage.Copied>(TerminalMessages.Parse("""{"type":"copied","id":"s1","key":"line","data":"a\r\nb\nc\u001b"}"""));
        Assert.True(line.OneLine);
        Assert.Equal("abc", line.Content);

        Assert.Null(TerminalMessages.Parse("""{"type":"copied","id":"s1","key":"other","data":"x"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"copied","id":"","key":"line","data":"x"}"""));
    }

    [Fact]
    public void The_requests_to_the_page_name_the_terminal_and_the_kind()
    {
        Assert.Equal("""{"type":"copy","id":"s1","data":"rich"}""", TerminalMessages.CopyRich("s1"));
        Assert.Equal("""{"type":"copy","id":"s1","data":"line"}""", TerminalMessages.CopyLine("s1"));
    }
}
