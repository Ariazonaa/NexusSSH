using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

/// <summary>Das Transfer-Protokoll: eine Zeile je beendetem Transfer, nur wenn eingeschaltet, nie eine Formel.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TransferJournalTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly MockTransferQueue _queue = new();

    public TransferJournalTests() => L.Init(AppLanguage.English);

    public void Dispose()
    {
        _queue.Dispose();
        _folder.Dispose();
    }

    private string LogFile => Path.Combine(_folder.Path, "logs", "transfers.csv");

    private string[] Lines() => File.ReadAllLines(LogFile);

    [Fact]
    public void Every_end_gets_one_line_and_only_while_it_is_switched_on()
    {
        var on = false;
        using var journal = new TransferJournal(_queue, LogFile, () => on, separator: ',');

        // Ausgeschaltet entsteht nicht einmal die Datei; und was beim Start schon fertig war (t2), zählt nie.
        var quiet = _queue.Add("quiet.txt", "web01", TransferDirection.Upload, 10);
        _queue.Complete(quiet);
        Assert.False(File.Exists(LogFile));

        on = true;
        var done = _queue.Add("report.pdf", "web01", TransferDirection.Upload, 2048);
        var failed = _queue.Add("dump.sql", "db01", TransferDirection.Download, 500);
        _queue.Advance();
        _queue.Complete(done);
        _queue.Fail(failed, "permission_denied: /var/dump.sql");
        _queue.Advance();

        var lines = Lines();
        Assert.Equal(3, lines.Length);
        Assert.Equal(TransferJournal.Header, lines[0]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(LogFile)[..3]);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d,upload,""web01"",""report.pdf"","""","""",2048,completed,""""$", lines[1]);
        Assert.EndsWith(@",download,""db01"",""dump.sql"","""","""",0,failed,""permission_denied: /var/dump.sql""", lines[2]);

        // „Wiederholen“ und ein zweites Ende: eine zweite Zeile für denselben Transfer – das Protokoll wächst nur.
        Assert.True(_queue.Retry(failed));
        _queue.Cancel(failed);
        Assert.Equal(4, Lines().Length);
        Assert.Contains(",cancelled,", Lines()[3]);

        // Aus der Liste genommen ist kein Ende.
        _queue.ClearFinished();
        Assert.Equal(4, Lines().Length);
    }

    [Fact]
    public void Paused_and_interrupted_are_named_as_such()
    {
        using var journal = new TransferJournal(_queue, LogFile, () => true, separator: ',');

        _queue.PauseAll();

        Assert.Equal(2, Lines().Count(line => line.Contains(",paused,", StringComparison.Ordinal)));
        Assert.Equal("interrupted", TransferJournal.Result(new TransferItem("1", "a", "s", TransferDirection.Upload, TransferState.Failed, 0, Detail: SavedTransfer.InterruptedDetail)));
    }

    [Theory]
    [InlineData("plain.txt", "\"plain.txt\"")]
    [InlineData("=cmd|' /C calc'!A0", "\"'=cmd|' /C calc'!A0\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-x", "\"'-x\"")]
    [InlineData("  =1+1", "\"'  =1+1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("a\"b,c", "\"a\"\"b,c\"")]
    [InlineData(null, "\"\"")]
    public void A_field_is_never_a_formula_and_never_breaks_the_line(string? text, string expected)
        => Assert.Equal(expected, TransferJournal.Field(text));

    [Fact]
    public void A_name_with_a_line_break_or_a_turned_reading_direction_stays_one_harmless_field()
    {
        var field = TransferJournal.Field("two\r\nlines\t\u202Egnp.exe");

        Assert.DoesNotContain(field, character => char.IsControl(character) || character == '\u202E');
        Assert.StartsWith("\"two", field, StringComparison.Ordinal);
        Assert.EndsWith("gnp.exe\"", field, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fields_are_split_where_a_spreadsheet_of_this_computer_splits_them()
    {
        // Excel teilt eine CSV-Datei am Listentrennzeichen der Region: in Deutschland am Semikolon.
        Assert.Equal(';', TransferJournal.LocalSeparator(System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(';', TransferJournal.LocalSeparator(System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal(',', TransferJournal.LocalSeparator(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(',', TransferJournal.LocalSeparator(System.Globalization.CultureInfo.InvariantCulture));

        using (var german = new TransferJournal(_queue, LogFile, () => true, separator: ';'))
        {
            _queue.Complete(_queue.Add("a;b,c.txt", "web01", TransferDirection.Upload, 7));
        }

        Assert.Equal("time;direction;server;name;source;target;bytes;result;detail", Lines()[0]);
        Assert.EndsWith(";upload;\"web01\";\"a;b,c.txt\";\"\";\"\";7;completed;\"\"", Lines()[1]);

        // Die Datei behält ihr Trennzeichen, auch wenn der Rechner inzwischen anders eingestellt ist.
        using var english = new TransferJournal(_queue, LogFile, () => true, separator: ',');
        _queue.Complete(_queue.Add("later.txt", "web01", TransferDirection.Upload, 1));
        Assert.Equal(3, Lines().Length);
        Assert.Contains(";upload;\"web01\";\"later.txt\";", Lines()[2]);
    }

    [Fact]
    public void A_full_file_moves_aside_and_a_new_one_begins()
    {
        using var journal = new TransferJournal(_queue, LogFile, () => true, separator: ',');
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        File.WriteAllBytes(LogFile, new byte[TransferJournal.MaxBytes]);

        _queue.Complete(_queue.Add("a.txt", "web01", TransferDirection.Upload, 1));

        Assert.Equal(TransferJournal.MaxBytes, new FileInfo(journal.Previous).Length);
        Assert.Equal(2, Lines().Length);
        Assert.EndsWith("transfers.1.csv", journal.Previous, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_once()
    {
        // Wo die Datei liegen soll, steht ein Ordner.
        Directory.CreateDirectory(LogFile);
        using var journal = new TransferJournal(_queue, LogFile, () => true, separator: ',');
        List<string> failures = [];
        journal.Failed += failures.Add;

        _queue.Complete(_queue.Add("a.txt", "web01", TransferDirection.Upload, 1));
        _queue.Complete(_queue.Add("b.txt", "web01", TransferDirection.Upload, 1));

        Assert.Single(failures);
    }

    [Fact]
    public void The_core_names_source_and_target_of_each_direction()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h1","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true},{"id":"p2","name":"backup01","host":"h2","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new CoreBackend(transport);
        var from = backend.Connect(backend.GetProfiles()[0]);
        var to = backend.Connect(backend.GetProfiles()[1]);
        from.Terminal.Start();
        to.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");
        transport.Raise("""{"event":"session.state","session":2,"state":"connected","home":"/"}""");
        using var journal = new TransferJournal(backend.Transfers, LogFile, () => true, separator: ',');
        var local = Path.Combine(_folder.Path, "site.tar.gz");

        var up = from.Files!.Upload(local, "/srv/www");
        var down = from.Files.Download("/var/log/app.log", _folder.Path);
        var relay = from.Files.SendTo("/var/www/site.tar.gz", to.Files!, "/srv/backup")!;

        Assert.Equal(new TransferPaths(local, "/srv/www"), backend.Transfers.PathsOf(up));
        Assert.Equal(new TransferPaths("/var/log/app.log", _folder.Path), backend.Transfers.PathsOf(down));
        Assert.Equal(new TransferPaths("/var/www/site.tar.gz", "/srv/backup"), backend.Transfers.PathsOf(relay));
        Assert.Null(backend.Transfers.PathsOf("nope"));

        transport.Raise($$"""{"event":"transfer.state","transfer":{{up}},"state":"completed","bytes":700,"total":700}""");

        var line = Assert.Single(Lines().Skip(1));
        Assert.EndsWith($",upload,\"web01\",\"site.tar.gz\",\"{local}\",\"/srv/www\",700,completed,\"\"", line);
    }

    [Fact]
    public void The_shell_writes_nothing_until_the_user_switches_it_on()
    {
        using var backend = new MockBackend();
        var settings = new AppSettings();
        var shell = new ViewModels.ShellViewModel(backend, new ImmediateDispatcher(), settings, new FakePrompts());
        var queue = (MockTransferQueue)backend.Transfers;
        Assert.False(settings.TransferLog);
        Assert.Null(shell.TransferLogFile);
        Assert.Null(shell.TransferLogFolder());

        shell.UseTransferLog(LogFile);
        queue.Complete(queue.Add("a.txt", "web01", TransferDirection.Upload, 1));
        Assert.False(File.Exists(LogFile));
        Assert.Null(shell.TransferLogFolder());
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith("There is no transfer log yet", StringComparison.Ordinal));

        shell.KeepTransferLog = true;
        queue.Complete(queue.Add("b.txt", "web01", TransferDirection.Upload, 1));

        Assert.True(settings.TransferLog);
        Assert.Equal(2, Lines().Length);
        Assert.Equal(Path.GetDirectoryName(LogFile), shell.TransferLogFolder());
        Assert.Contains(shell.Commands, command => command.Id == "transfers.log");
    }
}
