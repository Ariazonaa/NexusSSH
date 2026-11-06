using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Der Betrachter: Text in Seiten und mit Farben, Hex, Bearbeiten samt Konflikt – und das Protokoll zum Kern.</summary>
[Collection(LanguageCollection.Name)]
public sealed class FileViewerTests
{
    private readonly MockRemoteFileSystem _files = new("/etc");
    private readonly FakePrompts _prompts = new();

    public FileViewerTests() => L.Init(AppLanguage.English);

    private async Task<FileViewerViewModel> OpenAsync(string path)
    {
        var viewer = new FileViewerViewModel(_files, _prompts, "web01", path);
        await viewer.LoadAsync();
        return viewer;
    }

    private static string Kinds(string line, SyntaxKind kind)
        => string.Join(" ", SyntaxColors.Spans(line, kind).Where(span => span.Token != SyntaxToken.Text).Select(span => $"{span.Token}:{line.Substring(span.Start, span.Length)}"));

    [Fact]
    public void Lines_are_split_into_what_they_are_and_nothing_is_lost()
    {
        Assert.Equal("Key:listen Number:8080 Comment:# the port", Kinds("listen = 8080  # the port", SyntaxKind.Hash));
        Assert.Equal("Section:[server]", Kinds("[server]", SyntaxKind.Hash));
        Assert.Equal("Key:- image String:\"nginx:1.27\"", Kinds("  - image: \"nginx:1.27\"", SyntaxKind.Hash));
        Assert.Equal("Comment:; old style", Kinds("; old style", SyntaxKind.Hash));
        Assert.Equal("Key:\"port\" Number:8080", Kinds("  \"port\": 8080,", SyntaxKind.Json));
        Assert.Equal("Key:\"name\" String:\"a \\\"b\\\"\"", Kinds("\"name\": \"a \\\"b\\\"\"", SyntaxKind.Json));
        Assert.Equal("Number:42 Comment:// why", Kinds("let x = 42; // why", SyntaxKind.Slash));
        Assert.Equal("Tag:<body String:\"demo\" Tag:> Tag:</body Tag:>", Kinds("<body class=\"demo\">Hello</body>", SyntaxKind.Markup));
        Assert.Equal("Comment:<!-- note -->", Kinds("<!-- note -->", SyntaxKind.Markup));

        // Eine Adresse ist keine Zahl, eine URL kein Schlüssel, und ein # mitten im Wort kein Kommentar.
        Assert.Equal("Key:server", Kinds("server = 10.0.0.1", SyntaxKind.Hash));
        Assert.Equal(string.Empty, Kinds("http://example.org/a#b", SyntaxKind.Hash));

        // Die Stücke decken jede Zeile lückenlos ab.
        foreach (var line in new[] { "listen = 8080  # the port", "  \"port\": 8080,", "<a href='x'>y</a>", "", "   " })
        {
            foreach (var kind in Enum.GetValues<SyntaxKind>())
            {
                var spans = SyntaxColors.Spans(line, kind);
                Assert.Equal(line, string.Concat(spans.Select(span => line.Substring(span.Start, span.Length))));
            }
        }
    }

    [Theory]
    [InlineData("nginx.conf", SyntaxKind.Hash)]
    [InlineData("docker-compose.yml", SyntaxKind.Hash)]
    [InlineData("Dockerfile", SyntaxKind.Hash)]
    [InlineData(".bashrc", SyntaxKind.Hash)]
    [InlineData("package.json", SyntaxKind.Json)]
    [InlineData("index.html", SyntaxKind.Markup)]
    [InlineData("main.rs", SyntaxKind.Slash)]
    [InlineData("notes.txt", SyntaxKind.Plain)]
    [InlineData("photo.raw", SyntaxKind.Plain)]
    public void The_colours_follow_the_name_of_the_file(string name, SyntaxKind expected)
        => Assert.Equal(expected, SyntaxColors.KindOf(name));

    [Fact]
    public async Task A_text_file_is_shown_line_by_line_without_control_characters()
    {
        _files.Content("/etc/app.conf", "# comment\r\nlisten = 8080\n\tcolour \u001b[31mred\n");

        var viewer = await OpenAsync("/etc/app.conf");

        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.Equal("app.conf on web01", viewer.Title);
        Assert.Equal(["# comment", "listen = 8080", "    colour ␛[31mred"], viewer.Lines.Select(line => line.Text));
        Assert.Equal(SyntaxToken.Comment, viewer.Lines[0].Spans[0].Token);
        Assert.False(viewer.CanGoBack || viewer.CanGoOn);
        Assert.True(viewer.CanEdit);
    }

    [Fact]
    public async Task A_large_file_is_read_page_by_page_and_pages_end_at_a_line()
    {
        var line = new string('x', 99) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, 6000));
        _files.Content("/etc/big.log", content);

        var viewer = await OpenAsync("/etc/big.log");

        // Die erste Seite: höchstens 256 KiB, und sie endet mit einer ganzen Zeile.
        Assert.True(viewer.CanGoOn);
        Assert.False(viewer.CanGoBack);
        var first = viewer.Lines.Count;
        Assert.Equal(FileViewerViewModel.TextPage / 100, first);
        Assert.All(viewer.Lines, shown => Assert.Equal(99, shown.Text.Length));
        Assert.StartsWith("Bytes 0 – 262,100 of ", viewer.Status);

        await viewer.GoOnAsync();
        await viewer.GoOnAsync();
        Assert.False(viewer.CanGoOn);
        Assert.Equal(6000 - 2 * first, viewer.Lines.Count);

        await viewer.GoBackAsync();
        await viewer.GoBackAsync();
        Assert.False(viewer.CanGoBack);
        Assert.Equal(first, viewer.Lines.Count);
    }

    [Fact]
    public async Task What_is_not_text_is_shown_as_hex_and_text_can_be_shown_as_hex_too()
    {
        _files.Content("/etc/blob.bin", [0x7f, (byte)'E', (byte)'L', (byte)'F', 0, 1, 2, 0xff, .. new byte[12]]);
        var binary = await OpenAsync("/etc/blob.bin");

        Assert.Equal(ViewerMode.Hex, binary.Mode);
        Assert.Equal("00000000  7F 45 4C 46 00 01 02 FF  00 00 00 00 00 00 00 00  .ELF............", binary.HexText.Split('\n')[0]);
        Assert.Equal("00000010  00 00 00 00                                       ....", binary.HexText.Split('\n')[1]);
        Assert.False(binary.CanEdit);

        _files.Content("/etc/a.txt", "abc");
        var text = await OpenAsync("/etc/a.txt");
        await text.ToggleHexAsync();
        Assert.Equal("00000000  61 62 63                                          abc", text.HexText);
        await text.ToggleHexAsync();
        Assert.Equal(["abc"], text.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task Editing_keeps_line_endings_and_the_byte_order_mark_and_saves_to_the_server()
    {
        _files.Content("/etc/win.ini", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("a=1\r\nb=ü\r\n")]);
        var viewer = await OpenAsync("/etc/win.ini");

        Assert.True(await viewer.EditAsync());
        Assert.Equal(ViewerMode.Edit, viewer.Mode);
        Assert.Equal("a=1\nb=ü\n", viewer.EditText);
        Assert.False(viewer.IsDirty);

        // Das Eingabefeld führt Zeilen mit \r – das ist keine Änderung.
        viewer.EditText = "a=1\rb=ü\r";
        Assert.False(viewer.IsDirty);
        viewer.EditText = "a=2\rb=ü\rc=3\r";
        Assert.True(viewer.IsDirty);

        Assert.True(await viewer.SaveAsync());

        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("a=2\r\nb=ü\r\nc=3\r\n")], _files.ContentOf("/etc/win.ini"));
        Assert.False(viewer.IsDirty);
        Assert.StartsWith("Saved at ", viewer.Status);
        Assert.Empty(_prompts.Confirmed);

        // Zurück zur Ansicht: Dort steht, was jetzt auf dem Server liegt.
        await viewer.LeaveEditorAsync();
        Assert.Equal(["a=2", "b=ü", "c=3"], viewer.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task A_file_changed_on_the_server_meanwhile_is_only_overwritten_on_request()
    {
        _files.Content("/etc/app.conf", "one\n", hoursAgo: 5);
        var viewer = await OpenAsync("/etc/app.conf");
        await viewer.EditAsync();
        viewer.EditText = "mine\n";

        _files.Content("/etc/app.conf", "theirs\n", hoursAgo: 0);
        Assert.False(await viewer.SaveAsync());
        Assert.Equal("theirs\n", Encoding.UTF8.GetString(_files.ContentOf("/etc/app.conf")));
        Assert.Equal("Not saved: the file has changed on the server.", viewer.Status);
        Assert.True(viewer.IsDirty);

        _prompts.Confirm = true;
        Assert.True(await viewer.SaveAsync());
        Assert.Equal("mine\n", Encoding.UTF8.GetString(_files.ContentOf("/etc/app.conf")));
        Assert.Equal(2, _prompts.Confirmed.Count);
    }

    [Fact]
    public async Task What_cannot_be_written_back_unchanged_is_not_edited_here()
    {
        // Kein UTF-8: zum Ansehen als westeuropäischer Text, zum Bearbeiten nicht.
        _files.Content("/etc/latin.txt", [(byte)'a', 0xE4, (byte)'\n']);
        var latin = await OpenAsync("/etc/latin.txt");
        Assert.Equal(["aä"], latin.Lines.Select(line => line.Text));
        Assert.False(latin.CanEdit);
        Assert.StartsWith("Not UTF-8 text", latin.EditObstacle);

        _files.Content("/etc/huge.conf", new string('a', (int)FileViewerViewModel.MaxEditSize + 1));
        var huge = await OpenAsync("/etc/huge.conf");
        Assert.False(huge.CanEdit);
        Assert.StartsWith("Too large to edit here", huge.EditObstacle);
        Assert.False(await huge.EditAsync());
    }

    [Fact]
    public async Task A_missing_file_and_an_empty_one_say_so()
    {
        var missing = await OpenAsync("/etc/nothing");
        Assert.Equal(ViewerMode.Problem, missing.Mode);
        Assert.Contains("No such file", missing.Status, StringComparison.Ordinal);

        _files.Content("/etc/empty", string.Empty);
        var empty = await OpenAsync("/etc/empty");
        Assert.Equal(ViewerMode.Text, empty.Mode);
        Assert.Equal("The file is empty.", empty.Status);
    }

    [Fact]
    public async Task Images_and_pdf_are_fetched_as_a_copy_when_that_is_possible()
    {
        _files.File("/etc/logo.png", 1000, hoursAgo: 1).File("/etc/manual.pdf", 2000, hoursAgo: 1);
        var fetched = new List<string>();
        Task<(string?, string?)> Fetch(string path)
        {
            fetched.Add(path);
            return Task.FromResult<(string?, string?)>((@"C:\work\copy", null));
        }

        var image = new FileViewerViewModel(_files, _prompts, "web01", "/etc/logo.png", Fetch);
        await image.LoadAsync();
        var pdf = new FileViewerViewModel(_files, _prompts, "web01", "/etc/manual.pdf", Fetch);
        await pdf.LoadAsync();

        Assert.Equal((ViewerMode.Image, ViewerMode.Pdf), (image.Mode, pdf.Mode));
        Assert.Equal(@"C:\work\copy", image.LocalPath);
        Assert.Equal(["/etc/logo.png", "/etc/manual.pdf"], fetched);
        Assert.False(image.CanEdit || image.CanToggleHex);
    }

    [Fact]
    public async Task The_shell_opens_the_viewer_and_asks_before_unsaved_text_is_dropped()
    {
        var backend = new MockBackend();
        var prompts = new FakePrompts();
        using var shell = new ShellViewModel(backend, new ImmediateDispatcher(), new Settings.AppSettings(), prompts);
        var session = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        session.Start();
        await session.Files.LoadAsync();
        var file = session.Files.Entries.First(entry => !entry.IsDirectory);

        shell.OpenViewer(session, file);
        var viewer = shell.Viewer!;
        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.NotEmpty(viewer.Lines);

        await viewer.EditAsync();
        viewer.EditText += "more\n";

        // Ohne Zustimmung bleibt der Editor offen; mit ihr schließt er.
        Assert.False(await shell.CloseViewerAsync());
        Assert.NotNull(shell.Viewer);
        prompts.Confirm = true;
        Assert.True(await shell.CloseViewerAsync());
        Assert.Null(shell.Viewer);

        // Ein Ordner hat keinen Inhalt zum Ansehen.
        shell.OpenViewer(session, session.Files.Entries.First(entry => entry.IsDirectory));
        Assert.Null(shell.Viewer);
    }

    [Fact]
    public async Task The_core_is_asked_to_read_a_part_and_to_write_a_file()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        var files = connection.Files!;

        var reading = files.ReadAsync("/etc/app.conf", 16, 4096);
        var read = Assert.Single(transport.Sent("sftp.read"));
        Assert.Equal(("/etc/app.conf", 16, 4096), (read.GetProperty("path").GetString(), read.GetProperty("offset").GetInt64(), read.GetProperty("length").GetInt64()));
        transport.Raise($$"""{"event":"sftp.result","request":{{read.GetProperty("request").GetUInt64()}},"ok":true,"data":"aGk=","size":1234}""");
        var part = await reading;
        Assert.Equal(("hi", 1234L), (Encoding.UTF8.GetString(part.Data), part.TotalSize));

        var writing = files.WriteAsync("/etc/app.conf", Encoding.UTF8.GetBytes("neu"));
        var write = Assert.Single(transport.Sent("sftp.write"));
        Assert.Equal(("/etc/app.conf", "bmV1"), (write.GetProperty("path").GetString(), write.GetProperty("data").GetString()));
        transport.Raise($$"""{"event":"sftp.result","request":{{write.GetProperty("request").GetUInt64()}},"ok":true}""");
        await writing;
    }
}
