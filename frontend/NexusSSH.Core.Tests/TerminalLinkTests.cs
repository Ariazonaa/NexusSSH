using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Web-Adressen und Pfade, die im Terminal angeklickt werden – sie stehen in der Ausgabe eines Servers.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalLinkTests
{
    public TerminalLinkTests() => L.Init(AppLanguage.English);

    private static string Message(string type, string data) => JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = type, ["id"] = "t1", ["data"] = data });

    [Theory]
    [InlineData("https://example.org/docs?page=2#top")]
    [InlineData("http://10.0.1.10:8080/")]
    [InlineData("https://xn--bcher-kva.example/")]
    public void A_web_address_is_passed_on_as_one(string address)
    {
        var link = Assert.IsType<WebMessage.LinkActivated>(TerminalMessages.Parse(Message("link", address)));

        Assert.Equal(("t1", new Uri(address)), (link.Id, link.Link));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.org/file")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\evil.example\share\run.exe")]
    [InlineData("https://bank.example@evil.example/login")]
    [InlineData("https://user:secret@example.org/")]
    [InlineData("https:///nohost")]
    [InlineData("https://example.org/a b")]
    [InlineData("https://example.org/\u0007")]
    [InlineData("example.org")]
    [InlineData("")]
    public void Anything_that_is_no_plain_web_address_is_dropped(string address)
    {
        Assert.Null(TerminalMessages.WebLink(address));
        Assert.Null(TerminalMessages.Parse(Message("link", address)));
    }

    [Fact]
    public void An_overlong_address_is_dropped_and_a_path_must_look_like_one_on_the_server()
    {
        Assert.Null(TerminalMessages.WebLink("https://example.org/" + new string('a', TerminalMessages.MaxLinkLength)));

        var path = Assert.IsType<WebMessage.PathActivated>(TerminalMessages.Parse(Message("path", "/var/log/nginx/error.log")));
        Assert.Equal(("t1", "/var/log/nginx/error.log"), (path.Id, path.Path));

        // Relativ und ab dem Startverzeichnis geht auch – wohin das führt, entscheidet die App.
        Assert.Equal("./src/main.rs", Assert.IsType<WebMessage.PathActivated>(TerminalMessages.Parse(Message("path", "./src/main.rs"))).Path);
        Assert.Equal("~/.config", Assert.IsType<WebMessage.PathActivated>(TerminalMessages.Parse(Message("path", "~/.config"))).Path);
        Assert.Null(TerminalMessages.Parse(Message("path", "")));
        Assert.Null(TerminalMessages.Parse(Message("path", "file://server/share")));
        Assert.Null(TerminalMessages.Parse(Message("path", "/tmp/\u001b[2J")));
        Assert.Null(TerminalMessages.Parse(Message("path", @"C:\Windows")));
    }

    [Fact]
    public async Task A_relative_path_counts_from_the_directory_of_the_shell_or_else_from_the_one_shown()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        await session.Files.LoadAsync();
        var home = session.Files.HomePath;
        Assert.Equal("/var/www", home);

        // Ohne Meldung der Shell: ab dem Verzeichnis, das der Dateibrowser zeigt.
        Assert.True(await shell.OnTerminalPath(session, "./html"));
        Assert.Equal("/var/www/html", session.Files.CurrentPath);
        Assert.True(await shell.OnTerminalPath(session, "../nginx.conf"));
        Assert.Equal(("/var/www", "nginx.conf"), (session.Files.CurrentPath, session.Files.SelectedEntry?.Name));

        // Die Shell hat ihr Verzeichnis gemeldet (OSC 7): Dann gilt es – auch wenn der Dateibrowser ihr nicht folgt.
        shell.OnTerminalDirectory(session, "/var/www/html/");
        Assert.Equal("/var/www/html", session.ShellDirectory);
        Assert.Equal("/var/www", session.Files.CurrentPath);
        Assert.True(await shell.OnTerminalPath(session, "index.html"));
        Assert.Equal(("/var/www/html", "index.html"), (session.Files.CurrentPath, session.Files.SelectedEntry?.Name));

        // „~“ ist das Startverzeichnis, egal wo die Shell steht.
        Assert.True(await shell.OnTerminalPath(session, "~/html"));
        Assert.Equal("/var/www/html", session.Files.CurrentPath);
        Assert.True(await shell.OnTerminalPath(session, "~"));
        Assert.Equal(home, session.Files.CurrentPath);
    }

    [Fact]
    public async Task A_clicked_path_opens_a_folder_and_selects_a_file_in_the_file_browser()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        await session.Files.LoadAsync();
        shell.IsSftpVisible = false;

        // Ein Ordner: Der Dateibrowser steht danach darin – und ist wieder zu sehen.
        Assert.True(await shell.OnTerminalPath(session, "/var/www/html"));
        Assert.Equal("/var/www/html", session.Files.CurrentPath);
        Assert.True(shell.IsSftpVisible);

        // Eine Datei: ihr Ordner, und sie ist ausgewählt.
        Assert.True(await shell.OnTerminalPath(session, "/var/www/nginx.conf"));
        Assert.Equal(("/var/www", "nginx.conf"), (session.Files.CurrentPath, session.Files.SelectedEntry?.Name));

        // Was es nicht gibt, steht im Protokoll; die Ansicht bleibt, wo sie hinkam.
        Assert.False(await shell.OnTerminalPath(session, "/var/www/missing.txt"));
        Assert.Contains(shell.Problems, entry => entry.Text == $"/var/www/missing.txt does not exist on {session.Profile.Name}, or it cannot be opened.");
        Assert.False(await shell.OnTerminalPath(session, "/nowhere/at/all"));

        Assert.True(await shell.OnTerminalPath(session, "/"));
        Assert.Equal("/", session.Files.CurrentPath);

        // Eine geöffnete Adresse steht im Protokoll.
        shell.OnTerminalLink(session, new Uri("https://example.org/docs"));
        Assert.Contains(shell.Output, entry => entry.Text == $"https://example.org/docs opened (from the terminal of {session.Profile.Name}).");
    }

    [Fact]
    public void The_page_gets_the_hint_for_links()
    {
        var shell = TestShell.Create(out _);

        var message = JsonDocument.Parse(TerminalMessages.Options(shell.TerminalTheme, 14, [], linkHint: "Ctrl+click opens")).RootElement;

        Assert.Equal("Ctrl+click opens", message.GetProperty("linkHint").GetString());
    }
}
