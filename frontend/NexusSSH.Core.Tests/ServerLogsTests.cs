using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Seite mit den Protokollen eines Servers: was gefragt wird, was gezeigt wird, wann sie schließt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerLogsTests
{
    public ServerLogsTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, SessionViewModel Session) Connected()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.Profiles.GetProfiles()[0]);
        session.Start();
        return (shell, session);
    }

    [Fact]
    public async Task The_page_opens_for_the_active_session_and_shows_the_journal()
    {
        var (shell, session) = Connected();
        Assert.True(shell.CanShowLogs);
        shell.IsSettingsOpen = true;

        var logs = shell.ShowLogs()!;
        await logs.LoadAsync();

        Assert.Same(logs, shell.Logs);
        Assert.Same(session, logs.Session);
        Assert.False(shell.IsSettingsOpen);
        Assert.True(logs.IsJournal);
        Assert.Equal(LogSince.Day, logs.Since);
        Assert.Equal(6, logs.Shown.Count);
        Assert.Equal("6 lines", logs.Status);
        Assert.False(logs.HasError);
        Assert.StartsWith("Logs: ", logs.Title);

        // Älteste zuerst; die Schwere bestimmt die Farbe und steht für Screenreader im Namen der Zeile.
        Assert.True(logs.Shown[0].Time < logs.Shown[^1].Time);
        var error = logs.Shown.Single(line => line.Priority == 3);
        Assert.Equal(UsageLevel.Critical, error.Level);
        Assert.StartsWith("Error: ", error.ToString());
        Assert.Equal(UsageLevel.Warning, logs.Shown.Single(line => line.Priority == 4).Level);
        Assert.Contains("postgres: could not open file", error.Text);
    }

    [Fact]
    public async Task The_server_filters_by_period_severity_and_unit_and_the_page_by_text()
    {
        var (shell, _) = Connected();
        var logs = shell.ShowLogs()!;

        logs.Since = LogSince.Hour;
        await logs.LoadAsync();
        Assert.Equal(5, logs.Shown.Count);

        logs.PriorityChoice = 2;
        await logs.LoadAsync();
        Assert.Equal(["nginx", "postgres"], logs.Shown.Select(line => line.Source));

        logs.PriorityChoice = 0;
        logs.Unit = " sshd.service ";
        await logs.LoadAsync();
        Assert.Equal("sshd", Assert.Single(logs.Shown).Source);

        // Der Textfilter liest nicht neu: Er wirkt auf das, was da ist – jedes Wort muss vorkommen, gleich wie geschrieben.
        logs.Unit = string.Empty;
        logs.Since = LogSince.All;
        await logs.LoadAsync();
        logs.Filter = "  FILE postgres ";
        Assert.Equal("postgres", Assert.Single(logs.Shown).Source);
        Assert.Equal("1 of 6 lines", logs.Status);
        Assert.Contains("could not open file", logs.CopyText());
        logs.Filter = "nothing like this";
        Assert.Empty(logs.Shown);
        logs.Filter = string.Empty;
        Assert.Equal(6, logs.Shown.Count);
    }

    [Fact]
    public async Task What_is_no_unit_name_never_reaches_the_server()
    {
        var (shell, session) = Connected();
        var monitor = new CountingMonitor();
        session.Inspector.Attach(monitor);
        var logs = shell.ShowLogs()!;
        await logs.LoadAsync();
        var before = monitor.Asked.Count;

        foreach (var unit in new[] { "a b", "x; rm -rf /", "$(id)", "-u", "a'b", "*" })
        {
            logs.Unit = unit;
            await logs.LoadAsync();

            Assert.True(logs.HasError, unit);
            Assert.Empty(logs.Shown);
        }

        Assert.Equal(before, monitor.Asked.Count);
        Assert.True(LogQuery.IsUnitName("getty@tty1.service"));
        Assert.False(LogQuery.IsUnitName(string.Empty));
    }

    [Fact]
    public async Task A_file_is_read_without_the_filters_of_the_journal_and_failures_are_explained()
    {
        var (shell, session) = Connected();
        var monitor = new CountingMonitor();
        session.Inspector.Attach(monitor);
        var logs = shell.ShowLogs()!;

        logs.Source = new LogSource("/var/log/syslog", "syslog");
        logs.Lines = 200;
        await logs.LoadAsync();

        Assert.False(logs.IsJournal);
        Assert.Equal(new LogQuery("/var/log/syslog", LogSince.Day, null, string.Empty, 200), monitor.Asked[^1]);
        Assert.Equal("from the file", Assert.Single(logs.Shown).Message);

        // Kein Journal am Server (BusyBox, BSD): ein Satz, der sagt, was trotzdem geht.
        monitor.Failure = new RemoteFileException("the server has no journalctl", "no_journal");
        logs.Source = logs.Sources[0];
        await logs.LoadAsync();
        Assert.Contains("no journal", logs.Error);
        Assert.Empty(logs.Shown);
        Assert.Empty(logs.Status);

        monitor.Failure = new RemoteFileException("Permission denied", "command_failed");
        await logs.LoadAsync();
        Assert.Contains("command", logs.Error);

        // Das Konto sieht nur seine eigenen Einträge: Die Zeilen kommen, der Hinweis dazu.
        (monitor.Failure, monitor.Limited) = (null, true);
        await logs.LoadAsync();
        Assert.True(logs.IsLimited);
        Assert.False(logs.HasError);
    }

    [Fact]
    public async Task Only_readable_text_files_below_var_log_are_offered()
    {
        var (shell, session) = Connected();
        var logs = new ServerLogsViewModel(session);
        var files = new Mock.MockRemoteFileSystem("/home/u")
            .File("/var/log/syslog", 100, 1)
            .File("/var/log/syslog.2.gz", 50, 30)
            .File("/var/log/empty.log", 0, 1)
            .File("/var/log/wtmp", 900, 1)
            .File("/var/log/old.log.xz", 900, 1)
            .Special("/var/log/pipe.fifo")
            .File("/var/log/evil\u001b[31m.log", 5, 1)
            .File("/var/log/nginx/error.log", 10, 1)
            .File("/var/log/nginx/access.log", 10, 1)
            .File("/var/log/.hidden/x.log", 10, 1);
        session.Files.Attach(files);

        await logs.LoadSourcesAsync();

        Assert.Equal(["Journal (systemd)", "syslog", "syslog.2.gz", "nginx/access.log", "nginx/error.log"], logs.Sources.Select(source => source.Name));
        Assert.Equal("/var/log/nginx/error.log", logs.Sources[^1].Path);
        Assert.Same(logs.Sources[0], logs.Source);

        // Ohne /var/log bleibt es beim Journal.
        session.Files.Attach(new Mock.MockRemoteFileSystem("/home/u"));
        await logs.LoadSourcesAsync();
        Assert.Single(logs.Sources);
        _ = shell;
    }

    [Fact]
    public void The_page_closes_with_its_session_and_makes_way_for_other_pages()
    {
        var (shell, session) = Connected();
        Assert.NotNull(shell.ShowLogs());

        shell.IsHelpOpen = true;
        Assert.Null(shell.Logs);

        Assert.NotNull(shell.ShowLogs());
        Assert.False(shell.IsHelpOpen);
        shell.CloseSession(session);
        Assert.Null(shell.Logs);

        // Ohne Sitzung gibt es nichts zu lesen – der Befehl steht dann auch nicht zur Wahl.
        Assert.False(shell.CanShowLogs);
        Assert.Null(shell.ShowLogs());
        Assert.False(shell.Commands.Single(command => command.Id == "server.logs").IsAvailable);
    }

    private sealed class CountingMonitor : IServerMonitor
    {
        public List<LogQuery> Asked { get; } = [];

        public RemoteFileException? Failure { get; set; }

        public bool Limited { get; set; }

        public ServerStats? Current => null;

        public bool IsUnavailable => false;

#pragma warning disable CS0067 // In diesem Test meldet die Überwachung nichts.
        public event Action? Changed;
#pragma warning restore CS0067

        public void SetActive(bool active)
        {
        }

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public Task<ServerLog> LogsAsync(LogQuery query)
        {
            Asked.Add(query);
            return Failure is { } failure
                ? Task.FromException<ServerLog>(failure)
                : Task.FromResult(new ServerLog([new ServerLogLine(null, null, string.Empty, query.Path is null ? "from the journal" : "from the file")], Limited));
        }
    }
}
