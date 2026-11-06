using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

public class LoadHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));

    private static LoadSample At(double seconds, double cpu = 10, double memory = 40)
        => new(Start.AddSeconds(seconds), cpu, memory, 4000, 250, 0.5, 1024, 2048);

    [Fact]
    public void A_period_shows_only_its_measurements_and_the_whole_session_all()
    {
        var history = new LoadHistory();
        for (var second = 0; second <= 600; second += 3)
        {
            history.Add(At(second));
        }

        var now = Start.AddSeconds(600);
        Assert.Equal(201, history.Within(null, now).Count);
        Assert.Equal(101, history.Within(TimeSpan.FromMinutes(5), now).Count);
        Assert.Empty(history.Within(TimeSpan.FromMinutes(5), now.AddHours(1)));
    }

    [Fact]
    public void The_curve_runs_from_the_start_of_the_period_to_now_and_breaks_where_nothing_was_measured()
    {
        var history = new LoadHistory();
        foreach (var second in new[] { 0, 3, 6, 9, 12, 100, 103, 106 })
        {
            history.Add(At(second, cpu: second < 50 ? 0 : 100));
        }

        var lines = history.Lines(null, Start.AddSeconds(106), sample => sample.Cpu, width: 106, height: 50);

        Assert.Equal(2, lines.Count);
        Assert.Equal((0.0, 50.0), lines[0][0]);
        Assert.Equal(12.0, lines[0][^1].X);
        Assert.Equal((100.0, 0.0), lines[1][0]);
        Assert.Equal(106.0, lines[1][^1].X);
    }

    [Fact]
    public void More_measurements_than_columns_keep_their_peaks()
    {
        var history = new LoadHistory();
        for (var second = 0; second < 1000; second++)
        {
            history.Add(At(second, cpu: second == 500 ? 100 : 5));
        }

        var line = Assert.Single(history.Lines(null, Start.AddSeconds(999), sample => sample.Cpu, width: 100, height: 100));

        Assert.True(line.Count <= 101);
        Assert.Contains(line, point => point.Y == 0);
    }

    [Fact]
    public void A_long_session_stays_bounded_keeps_its_span_and_its_newest_measurements_exact()
    {
        var history = new LoadHistory();
        for (var index = 0; index < (LoadHistory.MaxSamples * 3) + 7; index++)
        {
            history.Add(At(index * 3, cpu: index % 2 == 0 ? 20 : 40));
        }

        Assert.InRange(history.Count, LoadHistory.MaxSamples / 2, LoadHistory.MaxSamples);
        // Das Älteste rückt beim Zusammenlegen in die Mitte seiner Nachbarn – es bleibt am Anfang der Sitzung.
        Assert.True(history.Samples[0].Time < Start.AddMinutes(15));
        Assert.Equal(Start.AddSeconds(((LoadHistory.MaxSamples * 3) + 6) * 3), history.Samples[^1].Time);
        Assert.Equal(30, history.Samples[0].Cpu);
        Assert.Contains(history.Samples[^1].Cpu, new[] { 20.0, 40.0 });
        Assert.True(history.Samples.Zip(history.Samples.Skip(1)).All(pair => pair.First.Time < pair.Second.Time));
    }

    [Fact]
    public void A_clock_that_jumps_back_does_not_reorder_the_history()
    {
        var history = new LoadHistory();
        history.Add(At(10));
        history.Add(At(5));

        Assert.True(history.Samples[1].Time > history.Samples[0].Time);
    }

    [Fact]
    public void The_csv_is_the_same_in_every_language()
    {
        var history = new LoadHistory();
        history.Add(new LoadSample(Start, 12.34, 45.6, 4_000_000_000, 0, 0.5, 1024, 2048));
        history.Add(new LoadSample(Start.AddSeconds(3), 100, 50, 1, 2, null, 0, 0));

        Assert.Equal(
            "time,cpu_percent,memory_percent,memory_used_bytes,swap_used_bytes,load_1min,net_received_bytes_per_s,net_sent_bytes_per_s\r\n"
            + "2026-10-10T12:00:00.000+02:00,12.3,45.6,4000000000,0,0.5,1024,2048\r\n"
            + "2026-10-10T12:00:03.000+02:00,100,50,1,2,,0,0\r\n",
            history.ToCsv());
    }

    [Theory]
    [InlineData("0.68 0.57 0.49", 0.68)]
    [InlineData("12", 12.0)]
    [InlineData("", null)]
    [InlineData("n/a 1 2", null)]
    [InlineData(null, null)]
    public void The_first_load_is_read_when_it_is_a_number(string? text, double? expected)
        => Assert.Equal(expected, LoadHistory.FirstLoad(text));

    [Theory]
    [InlineData("web01", "web01")]
    [InlineData("a/b\\c:d*e", "abcde")]
    [InlineData("../..", "server")]
    public void The_file_name_takes_only_what_can_be_a_name(string server, string expected)
        // Die Zeit im Namen ist die Ortszeit dieses Rechners – in der Zeitzone des Users 12:00, in der CI 10:00.
        => Assert.Equal($"{expected}-load-{Start.ToLocalTime():yyyy-MM-dd-HHmm}", LoadHistoryFile.Name(server, Start));

    [Fact]
    public void The_inspector_keeps_the_history_of_the_session_and_the_shell_writes_it()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.Profiles.GetProfiles()[0]);
        session.Start();
        shell.IsSnippetsVisible = true;
        var path = Path.Combine(Path.GetTempPath(), $"nexusssh-load-{Guid.NewGuid():N}.csv");
        try
        {
            Assert.True(shell.SaveLoadHistory(session, path));
            Assert.StartsWith("time,cpu_percent", File.ReadAllText(path));
            Assert.Equal(session.Inspector.History.Count + 1, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
