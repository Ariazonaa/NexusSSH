using System.Text;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary><c>TERM</c>, Umgebungsvariablen und Zeichensatz je Profil.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalEnvironmentTests
{
    private static readonly ServerProfile Plain = new("p", "plain", "host.example.org", 22, "me", "", []);

    [Fact]
    public void A_single_byte_charset_becomes_utf8_for_the_terminal()
    {
        // „Größe €“ in den drei Zeichensätzen; Steuerfolgen (ESC) bleiben, wie sie sind.
        Assert.Equal("\e[1mGröße\e[0m", Encoding.UTF8.GetString(TerminalCharset.Decode(TerminalCharset.Latin1, [0x1B, 0x5B, 0x31, 0x6D, 0x47, 0x72, 0xF6, 0xDF, 0x65, 0x1B, 0x5B, 0x30, 0x6D])));
        Assert.Equal("€ Œ", Encoding.UTF8.GetString(TerminalCharset.Decode(TerminalCharset.Latin9, [0xA4, 0x20, 0xBC])));
        Assert.Equal("€ „x“ –", Encoding.UTF8.GetString(TerminalCharset.Decode(TerminalCharset.Windows1252, [0x80, 0x20, 0x84, 0x78, 0x93, 0x20, 0x96])));

        // Dasselbe Byte ist in Latin-1 ein Währungszeichen und in Latin-9 der Euro.
        Assert.Equal("¤", Encoding.UTF8.GetString(TerminalCharset.Decode(TerminalCharset.Latin1, [0xA4])));

        // Jedes Byte ergibt genau ein Zeichen – nichts geht verloren, nichts wird ersetzt.
        var all = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        foreach (var id in TerminalCharset.Ids.OfType<string>())
        {
            var text = Encoding.UTF8.GetString(TerminalCharset.Decode(id, all));
            Assert.Equal(256, text.Length);
            Assert.DoesNotContain('�', text);
            Assert.Equal(256, text.Distinct().Count());

            // Und zurück: Was der Server schickte, lässt sich genau so wieder tippen.
            Assert.Equal(all, new TerminalCharset.Input(id).Encode(Encoding.UTF8.GetBytes(text)));
        }
    }

    [Fact]
    public void Typed_text_becomes_one_byte_per_character_even_when_it_arrives_in_pieces()
    {
        var input = new TerminalCharset.Input(TerminalCharset.Latin1);
        Assert.Equal([0x47, 0x72, 0xFC, 0xDF, 0x65, 0x0D], input.Encode(Encoding.UTF8.GetBytes("Grüße\r")));

        // Was es im Zeichensatz nicht gibt, wird ein Fragezeichen – auch ein Zeichen aus zwei Hälften nur eines.
        Assert.Equal("a?b?c"u8.ToArray(), input.Encode(Encoding.UTF8.GetBytes("a€b😀c")));
        Assert.Equal([0x80], new TerminalCharset.Input(TerminalCharset.Windows1252).Encode(Encoding.UTF8.GetBytes("€")));

        // Die Grenze eines Stücks liegt mitten im „ü“: Das Zeichen kommt mit dem nächsten Stück.
        var bytes = Encoding.UTF8.GetBytes("xüy");
        Assert.Equal("x"u8.ToArray(), input.Encode(bytes.AsSpan(0, 2)));
        Assert.Equal([0xFC, 0x79], input.Encode(bytes.AsSpan(2)));
    }

    [Fact]
    public void A_session_converts_both_ways_and_leaves_utf8_servers_alone()
    {
        TestShell.Create(out _);
        var old = Plain with { Charset = TerminalCharset.Latin1 };
        var backend = new ScriptedBackend(old, Plain);

        (SessionViewModel Session, List<byte> Shown) Open(ServerProfile profile)
        {
            var session = new SessionViewModel(profile, backend, new ImmediateDispatcher(), new FakePrompts());
            List<byte> shown = [];
            session.Output += data => shown.AddRange(data.ToArray());
            session.Start();
            backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
            shown.Clear();
            return (session, shown);
        }

        var (session, shown) = Open(old);
        backend.Last.EmitBytes([0x47, 0x72, 0xFC, 0xDF, 0x65]);
        Assert.Equal("Grüße", Encoding.UTF8.GetString([.. shown]));
        session.Send("ä");
        Assert.Equal([0xE4], backend.Last.RawInput[^1]);

        // Eine neue Verbindung beginnt ohne den Rest eines angefangenen Zeichens der alten.
        session.Write(new byte[] { 0xC3 });
        session.Reconnect();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        session.Send("a");
        Assert.Equal("a"u8.ToArray(), backend.Last.RawInput[^1]);

        // Ohne Angabe geht alles unverändert durch.
        var (other, plain) = Open(Plain);
        backend.Last.Emit("Grüße");
        Assert.Equal("Grüße", Encoding.UTF8.GetString([.. plain]));
        other.Send("ä");
        Assert.Equal(Encoding.UTF8.GetBytes("ä"), backend.Last.RawInput[^1]);
    }

    [Theory]
    [InlineData("xterm-256color", true)]
    [InlineData("vt100", true)]
    [InlineData("screen.xterm-256color", true)]
    [InlineData("vt220+x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-xterm", false)]
    [InlineData("a b", false)]
    [InlineData("xterm;reboot", false)]
    [InlineData("xtermä", false)]
    public void Only_terminfo_like_names_are_terminal_names(string? name, bool expected)
        => Assert.Equal(expected, ProfileEnv.IsTerminalName(name));

    [Fact]
    public void Environment_lines_are_read_and_what_is_no_variable_is_named()
    {
        var variables = ProfileEnv.Parse("LANG=de_DE.UTF-8\r\n\n  LC_ALL = C \nEMPTY=\nA=b=c\nkein variable\n1ST=x\nLANG=doppelt\n=x\nNAME WITH SPACE=x", out var rejected);

        Assert.Equal(
            [new ProfileEnv("LANG", "de_DE.UTF-8"), new ProfileEnv("LC_ALL", " C"), new ProfileEnv("EMPTY", ""), new ProfileEnv("A", "b=c")],
            variables);
        Assert.Equal(["kein variable", "1ST=x", "LANG=doppelt", "=x", "NAME WITH SPACE=x"], rejected);
        Assert.Equal("LANG=de_DE.UTF-8\nLC_ALL= C\nEMPTY=\nA=b=c", ProfileEnv.Format(variables));

        // Mehr als die Obergrenze: Der Rest wird genannt, nicht still verschluckt.
        ProfileEnv.Parse(string.Join('\n', Enumerable.Range(0, ProfileEnv.Max + 2).Select(index => $"V{index}=x")), out var over);
        Assert.Equal(2, over.Count);
        Assert.False(new ProfileEnv("X", new string('a', ProfileEnv.MaxValue + 1)).IsValid);
        Assert.False(new ProfileEnv("X", "a\u0007b").IsValid);
    }

    [Fact]
    public void The_editor_keeps_the_three_settings_and_blocks_what_cannot_be_saved()
    {
        TestShell.Create(out _);
        var editor = new ProfileEditorViewModel(null, []) { Host = "h", User = "u" };
        Assert.Equal((string.Empty, string.Empty, 0), (editor.Term, editor.EnvText, editor.CharsetIndex));
        var empty = editor.ToProfile();
        Assert.Equal((null, null, null), (empty.Term, empty.Env, empty.Charset));

        editor.Term = " vt100 ";
        editor.EnvText = "LANG=de_DE.ISO-8859-1\nLC_ALL=C";
        editor.CharsetIndex = Array.IndexOf(TerminalCharset.Ids, TerminalCharset.Latin9);
        Assert.True(editor.IsValid);
        var profile = editor.ToProfile();
        Assert.Equal(("vt100", TerminalCharset.Latin9), (profile.Term, profile.Charset));
        Assert.Equal([new ProfileEnv("LANG", "de_DE.ISO-8859-1"), new ProfileEnv("LC_ALL", "C")], profile.Env);

        // Das Übliche steht nicht im Profil.
        editor.Term = ProfileEnv.DefaultTerm;
        Assert.Null(editor.ToProfile().Term);

        editor.Term = "vt 100";
        Assert.False(editor.IsValid);
        Assert.NotEqual(string.Empty, editor.TermError);
        editor.Term = string.Empty;
        editor.EnvText = "LANG=C\nunsinn";
        Assert.False(editor.IsValid);
        Assert.Contains("unsinn", editor.EnvError, StringComparison.Ordinal);

        // Beim Bearbeiten steht da, was das Profil nennt.
        editor = new ProfileEditorViewModel(profile, [profile]);
        Assert.Equal(("vt100", "LANG=de_DE.ISO-8859-1\nLC_ALL=C", 2), (editor.Term, editor.EnvText, editor.CharsetIndex));
    }

    [Fact]
    public void The_core_gets_terminal_environment_and_charset_with_the_profile()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] =
            """{"ok":true,"profile":{"id":"p","name":"plain","host":"host.example.org","port":22,"user":"me","group":"","tags":[],"favorite":false,"use_agent":true,"term":"vt100","env":[{"name":"LANG","value":"C"}],"charset":"latin1"}}""";
        var backend = new CoreBackend(transport);

        var saved = backend.Save(Plain with { Term = "vt100", Env = [new ProfileEnv("LANG", "C")], Charset = TerminalCharset.Latin1 });
        var sent = transport.Sent("profiles.save").Last().GetProperty("profile");
        Assert.Equal(("vt100", "latin1"), (sent.GetProperty("term").GetString(), sent.GetProperty("charset").GetString()));
        var variable = Assert.Single(sent.GetProperty("env").EnumerateArray());
        Assert.Equal(("LANG", "C"), (variable.GetProperty("name").GetString(), variable.GetProperty("value").GetString()));
        Assert.Equal(("vt100", TerminalCharset.Latin1), (saved.Term, saved.Charset));
        Assert.Equal([new ProfileEnv("LANG", "C")], saved.Env);

        // Ohne Angaben fehlen die Felder ganz.
        backend.Save(Plain);
        sent = transport.Sent("profiles.save").Last().GetProperty("profile");
        Assert.False(sent.TryGetProperty("term", out _) || sent.TryGetProperty("env", out _) || sent.TryGetProperty("charset", out _));
    }
}
