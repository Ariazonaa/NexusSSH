using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public sealed class MockTerminalSessionTests
{
    private const string Prompt = "\e[1;32mubuntu@web01\e[0m:\e[1;34m~\e[0m$ ";

    private readonly StringBuilder _output = new();
    private readonly MockTerminalSession _session;

    public MockTerminalSessionTests()
    {
        L.Init(AppLanguage.English);
        var profile = new ServerProfile("web01", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);
        var stats = new ServerStats(true, 1, 2, 3, "Ubuntu 24.04.1 LTS", "6.8.0-45-generic", TimeSpan.FromDays(32));
        var files = new MockRemoteFileSystem("/home/ubuntu")
            .File("/home/ubuntu/notes.txt", 1, hoursAgo: 1)
            .File("/home/ubuntu/scripts/deploy.sh", 1, hoursAgo: 1);
        _session = new MockTerminalSession(profile, stats, files);
        _session.Output += data => _output.Append(Encoding.UTF8.GetString(data.Span));
    }

    private string Type(string text)
    {
        _output.Clear();
        _session.Write(Encoding.UTF8.GetBytes(text));
        return _output.ToString();
    }

    [Fact]
    public void Nothing_happens_before_start()
    {
        Assert.Equal(string.Empty, Type("ls\r"));
    }

    [Fact]
    public void Start_greets_once_and_ends_with_the_prompt()
    {
        _session.Start();
        var first = _output.ToString();
        _session.Start();

        Assert.StartsWith("Welcome to Ubuntu 24.04.1 LTS", first);
        Assert.EndsWith(Prompt, first);
        Assert.Equal(first, _output.ToString());
    }

    [Fact]
    public void Typed_characters_are_echoed_and_enter_runs_the_command()
    {
        _session.Start();

        Assert.Equal("who", Type("who"));
        Assert.Equal("ami\r\nubuntu\r\n" + Prompt, Type("ami\r"));
    }

    [Fact]
    public void Backspace_removes_the_last_character_and_stops_at_the_prompt()
    {
        _session.Start();

        Assert.Equal("pwx\b \bd", Type("pwx\x7f" + "d"));
        Assert.Equal("\r\n/home/ubuntu\r\n" + Prompt, Type("\r"));
        Assert.Equal(string.Empty, Type("\x7f\x7f"));
    }

    [Fact]
    public void Ls_lists_the_home_directory_with_directories_highlighted()
    {
        _session.Start();

        Assert.Equal("ls\r\n\e[1;34mscripts\e[0m\r\nnotes.txt\r\n" + Prompt, Type("ls\r"));
    }

    [Fact]
    public void Unknown_commands_answer_like_bash()
    {
        _session.Start();

        Assert.Contains("bash: vim: command not found", Type("vim /etc/hosts\r"));
    }

    [Fact]
    public void Arrow_keys_are_swallowed_instead_of_printed()
    {
        _session.Start();

        Assert.Equal("ab", Type("a\e[A\e[D\eOPb"));
    }

    [Fact]
    public void Ctrl_c_discards_the_line()
    {
        _session.Start();
        Type("sudo rm -rf");

        Assert.Equal("^C\r\n" + Prompt, Type("\x03"));
        Assert.Equal("\r\n" + Prompt, Type("\r"));
    }

    [Fact]
    public void A_multibyte_character_split_across_two_writes_arrives_whole()
    {
        _session.Start();
        _output.Clear();
        var bytes = Encoding.UTF8.GetBytes("ä");

        _session.Write(bytes.AsMemory(0, 1));
        Assert.Equal(string.Empty, _output.ToString());

        _session.Write(bytes.AsMemory(1, 1));
        Assert.Equal("ä", _output.ToString());
    }
}
