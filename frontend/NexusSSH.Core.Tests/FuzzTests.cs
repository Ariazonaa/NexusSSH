using System.Diagnostics;
using System.Text;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Zufallstests der Eingänge von außen: Jeder Leser bekommt viele verbogene Fassungen echter Eingaben und darf
/// nichts anderes werfen, als er verspricht, und sich nicht festrechnen. Der Zufall ist fest – derselbe Lauf prüft
/// immer dieselben Eingaben. Das Gegenstück im Kern steht in <c>backend/nexus-core/tests/fuzz.rs</c>.
/// </summary>
public class FuzzTests
{
    // NEXUS_FUZZ_ROUNDS setzt die Zahl der Runden je Leser herauf – wie bei den Zufallstests des Kerns.
    private static readonly int Rounds = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_FUZZ_ROUNDS"), out var rounds) && rounds > 0 ? rounds : 3000;

    // So lange darf ein Leser für eine Eingabe von ein paar Kilobyte höchstens brauchen.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    // Zeichenfolgen, die Leser gern aus dem Tritt bringen.
    private static readonly string[] Spice =
    [
        "\0", "\n", "\r\n", " ", "\t", "\"", "'", "\\", "{", "}", "[", "]", ":", ",", "<", ">", "/>", "</", "&", "&#x0;", "<!DOCTYPE x [<!ENTITY a \"aaaa\">]>", "&a;",
        "=", "#", ";", "*", "?", "!", "%", "-1", "0", "2147483648", "9223372036854775808", "1e999", "null", "true", "\u202e", "\ud800", "\uffff", "\u001b[31m",
        "#ff0000", "#gggggg", "file:///c:/x", "\\\\server\\share", "https://a@b/", "C:\\", "../",
    ];

    private static string Mutate(Random random, string seed, IReadOnlyList<string> others)
    {
        var text = new StringBuilder(seed);
        for (var change = random.Next(1, 7); change > 0; change--)
        {
            var at = random.Next(text.Length + 1);
            switch (random.Next(8))
            {
                case 0 when text.Length > 0:
                    text[Math.Min(at, text.Length - 1)] = (char)random.Next(0x10000);
                    break;
                case 1:
                    text.Length = at;
                    break;
                case 2:
                    text.Insert(at, Spice[random.Next(Spice.Length)]);
                    break;
                case 3 when text.Length > 0:
                    {
                        // Ein Stück vervielfachen – so entstehen lange Zeilen, tiefe Schachtelung und viele Wiederholungen.
                        var from = random.Next(text.Length);
                        var piece = text.ToString(from, Math.Min(1 + random.Next(24), text.Length - from));
                        text.Insert(at, piece, 1 + random.Next(60));
                        break;
                    }

                case 4 when text.Length > 0:
                    {
                        var from = random.Next(text.Length);
                        text.Remove(from, Math.Min(1 + random.Next(16), text.Length - from));
                        break;
                    }

                case 5:
                    {
                        var other = others[random.Next(others.Count)];
                        if (other.Length > 0)
                        {
                            var from = random.Next(other.Length);
                            text.Insert(at, other.AsSpan(from, Math.Min(1 + random.Next(40), other.Length - from)));
                        }

                        break;
                    }

                case 6 when text.Length > 0:
                    // Ein Zeichen in seinen Nachbarn verwandeln: aus „22“ wird „23“, aus „"“ ein „#“.
                    at = Math.Min(at, text.Length - 1);
                    text[at] = (char)(text[at] + random.Next(-1, 2));
                    break;
                default:
                    text.Insert(at, (char)random.Next(0x20, 0x7f));
                    break;
            }
        }

        return text.Length > 16 * 1024 ? text.ToString(0, 16 * 1024) : text.ToString();
    }

    // Lässt einen Leser mit seinen echten Eingaben, jedem abgeschnittenen Anfang und vielen verbogenen Fassungen laufen.
    private static void Hammer(string[] seeds, Action<string> read)
    {
        var random = new Random(20261010);
        void Check(string input)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                read(input);
            }
            catch (Exception e)
            {
                Assert.Fail($"{e.GetType().Name}: {e.Message} bei der Eingabe {Quoted(input)}");
            }

            Assert.True(watch.Elapsed < Patience, $"zu langsam ({watch.Elapsed}) bei der Eingabe {Quoted(input)}");
        }

        Check(string.Empty);
        foreach (var seed in seeds)
        {
            Check(seed);
            for (var end = 0; end < Math.Min(seed.Length, 400); end++)
            {
                Check(seed[..end]);
            }
        }

        for (var round = 0; round < Rounds; round++)
        {
            Check(Mutate(random, seeds[round % seeds.Length], seeds));
        }
    }

    private static string Quoted(string input) => System.Text.Json.JsonSerializer.Serialize(input.Length > 2000 ? input[..2000] : input);

    [Fact]
    public void A_message_of_the_terminal_page_never_throws()
        => Hammer(
            [
                """{"type":"ready"}""",
                """{"type":"input","id":"s1","data":"ls -la\r"}""",
                """{"type":"resize","id":"s1","cols":120,"rows":40}""",
                """{"type":"shortcut","key":"view.nextPane"}""",
                """{"type":"confirmPaste","id":"s1","data":"rm -rf /\nexit"}""",
                """{"type":"title","id":"s1","data":"vim ~/notes"}""",
                """{"type":"cwd","id":"s1","data":"/srv/www"}""",
                """{"type":"link","id":"s1","data":"https://example.org/a?b=c#d"}""",
                """{"type":"path","id":"s1","data":"~/logs/app.log"}""",
                """{"type":"ack","id":"s1","count":4096}""",
                """{"type":"text","id":"s1","data":"line one\nline two\u001b[0m"}""",
                """{"type":"snapshot","id":"s1","data":"line one\r\n\u001b[31mred\u001b[?1049h\u001b[H"}""",
                """{"type":"found","id":"s1","index":-1,"count":0}""",
                """{"type":"shares","id":"s1","path":[1,0],"shares":[0.3,0.7]}""",
                """{"type":"move","id":"s1","target":"s2","side":"left"}""",
                """{"type":"notify","id":"s1","title":"Build","body":"3 Fehler; \u001b[31m 2 Warnungen ‮"}""",
                """{"type":"hits","data":"error","more":true,"items":[{"id":"s1","row":12,"text":"an error here"},{"id":"","row":-1,"text":null},null]}""",
            ],
            json =>
            {
                switch (TerminalMessages.Parse(json))
                {
                    case WebMessage.LinkActivated link:
                        // Was als Adresse durchgeht, ist eine gewöhnliche Web-Adresse – sonst nichts.
                        Assert.Contains(link.Link.Scheme, new[] { "http", "https" });
                        Assert.Empty(link.Link.UserInfo);
                        break;
                    case WebMessage.Notice notice:
                        // Was als Meldung durchgeht, ist kurz, ohne Steuerzeichen und nicht leer.
                        Assert.InRange(notice.Title.Length, 0, TerminalMessages.MaxNoticeTitleLength);
                        Assert.InRange(notice.Body.Length, 0, TerminalMessages.MaxNoticeBodyLength);
                        Assert.DoesNotContain((notice.Title + notice.Body).AsEnumerable(), Format.IsInvisible);
                        Assert.NotEqual(0, notice.Title.Length + notice.Body.Length);
                        break;
                    case WebMessage.Shares shares:
                        // Was als Anteile durchgeht, ist endlich und passt in den Baum.
                        Assert.InRange(shares.Path.Count, 0, SplitTree.MaxDepth);
                        Assert.All(shares.Path, step => Assert.InRange(step, 0, SplitTree.MaxPanes - 1));
                        Assert.InRange(shares.Values.Count, 2, SplitTree.MaxPanes);
                        Assert.All(shares.Values, value => Assert.True(double.IsFinite(value)));
                        break;
                }
            });

    [Fact]
    public void An_imported_colour_scheme_never_throws_and_only_carries_plain_colours()
        => Hammer(
            [
                """{"name":"Night","background":"#101014","foreground":"#e6e6e6","cursor":"#ffcc00","selectionBackground":"#334455","black":"#000000","red":"#ff5555","green":"#50fa7b","yellow":"#f1fa8c","blue":"#6272a4","magenta":"#ff79c6","cyan":"#8be9fd","white":"#f8f8f2","brightBlack":"#44475a","brightRed":"#ff6e6e","brightGreen":"#69ff94","brightYellow":"#ffffa5","brightBlue":"#d6acff","brightMagenta":"#ff92df","brightCyan":"#a4ffff","brightWhite":"#ffffff"}""",
                "[colors.primary]\nbackground = '#1d1f21'\nforeground = \"#c5c8c6\"\n[colors.normal]\nblack = '0x1d1f21'\nred = '#cc6666'\n[colors.bright]\nwhite = '#ffffff'\n",
                "<?xml version=\"1.0\"?>\n<plist version=\"1.0\"><dict><key>Ansi 0 Color</key><dict><key>Red Component</key><real>0.1</real><key>Green Component</key><real>0.2</real><key>Blue Component</key><real>0.3</real></dict><key>Background Color</key><dict><key>Red Component</key><real>0</real></dict></dict></plist>",
                "[Night]\nColorTable00=16,16,20\nColorTable07=230,230,230\nForegroundColour=230,230,230\nBackgroundColour=#101014\n",
            ],
            text =>
            {
                if (ThemeImport.Parse(text, "fuzz") is { } theme)
                {
                    Assert.True(ThemeImport.IsValid(theme), $"ein Farbschema mit unsauberen Farben kam durch: {Quoted(text)}");
                }
            });

    [Fact]
    public void An_export_file_fails_only_with_its_own_exception_and_plans_without_throwing()
        => Hammer(
            [
                """{"format":"nexusssh-export","version":1,"app":"0.21.0","profiles":[{"id":"a-0","name":"web","host":"web.example.org","port":22,"user":"deploy","group":"Kunde/Projekt","tags":["prod","eu"],"keyPath":"~/.ssh/id_web","jump":"b-0","protocol":"ssh","theme":"night","color":"#ff0000"},{"id":"b-0","name":"gate","host":"192.0.2.1","port":2200,"user":"ops"}],"commands":[{"name":"disk","text":"df -h"}],"themes":[{"id":"night","name":"Night","background":"#000000","foreground":"#ffffff"}],"settings":{"theme":"night","fontSize":14,"shortcuts":{"view.nextPane":"Ctrl+F6"}}}""",
                """{"format":"nexusssh-export","version":99,"app":"9.0"}""",
                """{"format":"something-else","version":1}""",
            ],
            json =>
            {
                ExportFile file;
                try
                {
                    file = DataExchange.Parse(json);
                }
                catch (ExchangeException)
                {
                    return;
                }

                DataExchange.Plan(file, [], new AppSettings(), "C:\\Users\\me", keyExists: _ => true);
            });

    [Fact]
    public void A_file_of_another_program_never_throws()
    {
        string[] names = ["config", "confCons.xml", "servers.rtsz", @"C:\Sessions\web01.ini", "export.xml", "MobaXterm.ini", "x.remmina", "core.xsh", ""];
        var round = 0;
        Hammer(
            [
                "# exported\nHost web\n    HostName web01.example.org\n    User deploy\n    Port 2222\n    IdentityFile /keys/id_web\nHost db\n  HostName=10.0.0.5\nHost !app.internal *.internal jump?\n    User ops\nMatch host x\nHost *\n    User fallback\n",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\"><Node Name=\"Customers\" Type=\"Container\"><Node Name=\"acme-web\" Type=\"Connection\" Hostname=\"web.acme.example\" Protocol=\"SSH2\" Port=\"22\" Username=\"root\" Password=\"AAAA\" /><Node Name=\"switch\" Type=\"Connection\" Hostname=\"10.0.0.2\" Protocol=\"Telnet\" Port=\"23\" /></Node></mrng:Connections>",
                "<RTSZDocument><RoyalFolder><ID>root</ID><Name>Connections</Name></RoyalFolder><RoyalFolder><ID>f1</ID><Name>Production</Name><ParentID>root</ParentID></RoyalFolder><RoyalSSHConnection><ID>c1</ID><Name>db</Name><URI>db.example</URI><Port>2222</Port><ParentID>f1</ParentID><CredentialUsername>postgres</CredentialUsername></RoyalSSHConnection><RoyalFolder><ID>f1</ID><ParentID>f1</ParentID></RoyalFolder></RTSZDocument>",
                "S:\"Protocol Name\"=SSH2\r\nS:\"Hostname\"=web01.example.org\r\nS:\"Username\"=deploy\r\nD:\"[SSH2] Port\"=000008ae\r\nS:\"Password V2\"=02:abc\r\n",
                "<VanDyke version=\"3.0\"><key name=\"Sessions\"><key name=\"Lab\"><key name=\"lab-01\"><string name=\"Hostname\">lab01</string><string name=\"Protocol Name\">SSH2</string><dword name=\"[SSH2] Port\">22</dword></key></key></key></VanDyke>",
                "[Bookmarks]\nSubRep=\nImgNum=42\nweb=#109#0%web.example.org%22%deploy%%-1%-1%%%%%0%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0# #-1\n[Bookmarks_1]\nSubRep=Kunde\\Projekt\ndb=#109#0%10.0.0.5%2200%[me]%\n",
                "[remmina]\nname=box\nprotocol=SSH\nserver=box.example.org:2222\nusername=me\nssh_privatekey=/home/me/.ssh/id\ngroup=Lab/East\n",
                "[CONNECTION]\nHost=core.example.org\nPort=22\nProtocol=SSH\n[CONNECTION:AUTHENTICATION]\nUserName=admin\nUserKey=id_rsa\n",
            ],
            text =>
            {
                var name = names[round++ % names.Length];
                ForeignFiles.Read(name, Encoding.UTF8.GetBytes(text), "me");
                ForeignFiles.Read(name, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(), "me");
            });
    }

    [Fact]
    public void The_answer_of_the_update_check_never_throws()
        => Hammer(
            [
                """{"tag_name":"v0.22.0","name":"NexusSSH 0.22.0","draft":false,"prerelease":false,"html_url":"https://example.org/releases/v0.22.0","body":"notes","assets":[{"name":"NexusSSH-Setup-0.22.0.exe","browser_download_url":"https://example.org/a.exe","size":123}]}""",
                """{"tag_name":"v999999999999.1","draft":true,"prerelease":null,"html_url":"file:///c:/x"}""",
            ],
            json => UpdateCheck.Parse(json, new Version(0, 21, 0)));

    [Fact]
    public void Colouring_a_line_of_a_file_never_throws_and_stays_inside_the_line()
    {
        var kinds = Enum.GetValues<SyntaxKind>();
        Hammer(
            [
                "# comment \"with quotes\" and key = 'value' 123 0x1f",
                "const text = \"a \\\" b\"; // done /* open",
                "{ \"key\": [1, 2.5e3, true, null, \"text \\u0041\"] }",
                "<a href=\"x\" b='y'>text &amp; more</a><!-- comment --><![CDATA[ x ]]>",
                "key: \"value\" # yaml 'single' -- sql /* block */ 'it''s'",
            ],
            line =>
            {
                foreach (var kind in kinds)
                {
                    var at = 0;
                    foreach (var span in SyntaxColors.Spans(line, kind))
                    {
                        // Die Stücke liegen lückenlos hintereinander – der Betrachter schneidet die Zeile danach.
                        Assert.Equal(at, span.Start);
                        Assert.True(span.Length >= 0 && span.Start + span.Length <= line.Length);
                        at += span.Length;
                    }
                }
            });
    }

    [Fact]
    public void The_os_release_file_of_a_server_never_throws()
        => Hammer(
            [
                "PRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\nNAME=\"Debian GNU/Linux\"\nVERSION_ID=\"13\"\nID=debian\n",
                "NAME='Ubuntu'\r\nPRETTY_NAME=\r\nID_LIKE=debian\r\n=\n\"\n",
            ],
            text => ServerSystems.FromOsRelease(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_message_of_an_mcp_program_never_throws()
    {
        var server = new Mcp.McpServer((_, _) => Task.FromResult(new Mcp.McpResult("ok")));
        Hammer(
            [
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                """{"jsonrpc":"2.0","id":"a","method":"tools/call","params":{"name":"read_file","arguments":{"server":"web01","path":"/etc/hosts"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"x"}}""",
            ],
            text => server.HandleAsync(text, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void A_call_through_the_pipe_never_throws()
        => Hammer(
            ["""{"tool":"run_command","arguments":{"server":"web01","command":"id"}}""", """{"tool":"x","arguments":{}}"""],
            text => Mcp.McpPipe.ParseCall(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void The_output_of_a_typed_command_never_throws()
        => Hammer(
            ["echo\r\nout\r\n\u001b]7777;nexus;ab12;0\u0007$ ", "\u001b[?1049h\u001b]7777;nexus;ab12;\u0007\u001b[?47l"],
            text =>
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var command = new TerminalCommand("ls", "ab12");
                command.Feed(bytes);
                _ = command.Result(timedOut: true);
                new AlternateScreen().Feed(bytes);
            });

    [Fact]
    public void Text_of_a_server_for_an_ai_program_never_throws()
        => Hammer(
            ["plain\r\n\u001b[31mred\u001b[0m\ttab", "\u001b]0;title\u0007\u001b\\\u001bP\u009b‮"],
            text => Format.VisibleText(text));

    [Fact]
    public void Server_output_for_the_transcript_never_throws_however_it_is_cut()
    {
        byte[][] seeds =
        [
            "plain \u001b[31mred\u001b[0m \u001b]0;title\a text \u001b]8;;https://x\u001b\\link\u001b]8;;\u001b\\ \u001bP1$r0m\u001b\\ ä€😀\r\n"u8.ToArray(),
            [0x1b, 0x5b, 0x33, 0x38, 0x3b, 0x35, 0x3b, 0x31, 0x6d, 0xc3, 0xe2, 0x82, 0x1b, 0x18, 0x1a, 0x9b, 0x9d, 0x07, 0xf0, 0x9f, 0x98, 0x80, 0x1b, 0x5d, 0x1b, 0x1b, 0x5c],
        ];
        var random = new Random(20261010);
        for (var round = 0; round < Rounds * 4; round++)
        {
            var data = (byte[])seeds[round % seeds.Length].Clone();
            for (var change = random.Next(4); change > 0 && data.Length > 0; change--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            // Einmal am Stück und einmal zerschnitten: Wo das Netz trennt, darf an der Mitschrift nichts ändern.
            using var whole = new MemoryStream();
            new PlainText().Append(data, whole);

            using var pieces = new MemoryStream();
            var reader = new PlainText();
            for (var at = 0; at < data.Length;)
            {
                var size = Math.Min(1 + random.Next(5), data.Length - at);
                reader.Append(data.AsSpan(at, size), pieces);
                at += size;
            }

            Assert.True(whole.ToArray().AsSpan().SequenceEqual(pieces.ToArray()), $"die Stückelung ändert die Mitschrift: {Convert.ToHexString(data)}");
            Assert.DoesNotContain((byte)0x1b, whole.ToArray());
        }
    }
}
