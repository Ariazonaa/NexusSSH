using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Eine Datei des Servers mitlesen: Zeilen, Filter, Hervorhebung, Anhalten – und das Protokoll zum Kern.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FileFollowTests
{
    private const string Log = "/var/log/app.log";

    private readonly MockRemoteFileSystem _files = new MockRemoteFileSystem("/var/log").File(Log, 100, hoursAgo: 1);
    private readonly FileFollowViewModel _follow = new(new ImmediateDispatcher());
    private readonly List<bool> _changes = [];

    public FileFollowTests()
    {
        L.Init(AppLanguage.English);
        _follow.LinesChanged += _changes.Add;
    }

    private IEnumerable<string> Shown => _follow.Shown.Select(line => line.Text);

    [Fact]
    public void Following_shows_the_end_of_the_file_and_then_what_is_added()
    {
        Assert.True(_follow.Start(_files, Log, "web01"));

        Assert.True(_follow.IsFollowing);
        Assert.Equal("app.log on web01", _follow.Title);
        Assert.Equal(["[app.log] service started", "[app.log] listening on :8080"], Shown);

        _files.Append(Log, "GET /index.html 200\nGET /missing 404\n");

        Assert.Equal(4, _follow.Shown.Count);
        Assert.Equal(["GET /index.html 200", "GET /missing 404"], _follow.Added.Select(line => line.Text));
        // Nur angehängt: Die Ansicht muss nicht neu aufgebaut werden.
        Assert.True(_changes[^1]);
    }

    [Fact]
    public void A_line_counts_only_once_its_end_has_arrived_and_control_characters_are_dropped()
    {
        _follow.Start(_files, Log, "web01");
        _follow.Clear();

        _files.Append(Log, "half a li");
        Assert.Empty(Shown);
        _files.Append(Log, "ne\r\ncolour \u001b[31mred\u001b[0m\tend\n");

        Assert.Equal(["half a line", "colour [31mred[0m    end"], Shown);
    }

    [Fact]
    public void The_filter_shows_matching_lines_only_and_the_highlight_marks_them()
    {
        _follow.Start(_files, Log, "web01");
        _files.Append(Log, "GET /a 200\nGET /b 500\nPOST /c 200\n");

        _follow.Filter = "get";
        Assert.Equal(["GET /a 200", "GET /b 500"], Shown);
        // Ein anderer Filter heißt: alles neu zeichnen.
        Assert.False(_changes[^1]);

        _follow.Highlight = " 500";
        Assert.Equal([false, true], _follow.Shown.Select(line => line.Highlighted));

        // Was dazukommt und nicht passt, erscheint nicht – ist aber da, sobald der Filter fällt.
        _files.Append(Log, "POST /d 500\n");
        Assert.Empty(_follow.Added);
        _follow.Filter = string.Empty;
        Assert.Contains("POST /d 500", Shown);
    }

    [Fact]
    public void While_paused_new_lines_are_kept_but_the_display_stands_still()
    {
        _follow.Start(_files, Log, "web01");
        _follow.IsPaused = true;
        var before = _changes.Count;

        _files.Append(Log, "while paused\n");
        Assert.Equal(before, _changes.Count);

        _follow.IsPaused = false;
        Assert.Contains("while paused", Shown);
        Assert.False(_changes[^1]);
    }

    [Fact]
    public void Only_the_last_lines_are_kept_and_an_endless_line_is_cut()
    {
        _follow.Start(_files, Log, "web01");

        _files.Append(Log, string.Concat(Enumerable.Range(0, FileFollowViewModel.MaxLines + 10).Select(number => $"line {number}\n")));
        Assert.Equal(FileFollowViewModel.MaxLines, _follow.Shown.Count);
        Assert.Equal($"line {FileFollowViewModel.MaxLines + 9}", _follow.Shown[^1].Text);
        // Vorn ist etwas weggefallen: neu zeichnen.
        Assert.False(_changes[^1]);

        _files.Append(Log, new string('x', FileFollowViewModel.MaxLineLength * 3) + "\n");
        Assert.Equal(FileFollowViewModel.MaxLineLength + 2, _follow.Shown[^1].Text.Length);
    }

    [Fact]
    public void Stopping_ends_it_and_a_file_that_cannot_be_followed_says_why()
    {
        _follow.Start(_files, Log, "web01");
        _follow.Stop();

        Assert.False(_follow.IsFollowing);
        Assert.Equal("Stopped.", _follow.Status);
        var shown = _follow.Shown.Count;
        _files.Append(Log, "after the end\n");
        Assert.Equal(shown, _follow.Shown.Count);

        Assert.False(_follow.Start(_files, "/var/log/missing.log", "web01"));
        Assert.False(_follow.IsFollowing);
        Assert.StartsWith("Ended: tail: cannot open", _follow.Status);
    }

    [Fact]
    public void Starting_another_file_ends_the_first()
    {
        _files.File("/var/log/other.log", 5, hoursAgo: 1);
        _follow.Start(_files, Log, "web01");
        _follow.Start(_files, "/var/log/other.log", "web01");

        _files.Append(Log, "from the first\n");

        Assert.Equal("other.log on web01", _follow.Title);
        Assert.DoesNotContain("from the first", Shown);
    }

    [Fact]
    public void The_core_is_asked_to_follow_and_to_stop()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        var (text, ends) = (new List<string>(), new List<string?>());

        var following = connection.Files!.Follow("/var/log/app.log", text.Add, ends.Add);

        var start = Assert.Single(transport.Sent("follow.start"));
        var number = start.GetProperty("follow").GetUInt64();
        Assert.Equal((1UL, "/var/log/app.log"), (start.GetProperty("session").GetUInt64(), start.GetProperty("path").GetString()));
        transport.Raise($$"""{"event":"follow.data","follow":{{number}},"text":"one\n"}""");
        transport.Raise($$"""{"event":"follow.data","follow":{{number + 1}},"text":"not mine\n"}""");
        Assert.Equal(["one\n"], text);

        // Wer aufhört, hört nichts mehr – auch nicht das Ende, das der Kern danach meldet.
        following!.Dispose();
        Assert.Equal(number, Assert.Single(transport.Sent("follow.stop")).GetProperty("follow").GetUInt64());
        transport.Raise($$"""{"event":"follow.end","follow":{{number}}}""");
        Assert.Empty(ends);

        // Endet es von selbst, kommt der Grund an.
        connection.Files.Follow("/nothing", text.Add, ends.Add);
        var second = transport.Sent("follow.start").Last().GetProperty("follow").GetUInt64();
        transport.Raise($$"""{"event":"follow.end","follow":{{second}},"error":"command_failed","message":"tail: cannot open"}""");
        Assert.Contains("tail: cannot open", Assert.Single(ends));
    }
}
