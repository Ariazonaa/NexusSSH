using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Die Zeile unten im Fenster (statt der Statusleiste): was ihre Zahlen sagen und was ihre Knöpfe tun – dazu, was
/// „Trennen“ im Menü des Reiters tut.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class StatusBarTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);

    public StatusBarTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Transfers_that_run_or_wait_are_counted_until_they_are_done()
    {
        // Das Mock-Backend beginnt mit einem laufenden, einem fertigen und einem wartenden Transfer.
        var shell = TestShell.Create(out var backend);
        var queue = (MockTransferQueue)backend.Transfers;
        Assert.Equal(2, shell.TransferCount);

        queue.Complete("t1");
        Assert.Equal(1, shell.TransferCount);

        queue.Complete("t3");
        Assert.Equal(0, shell.TransferCount);
    }

    [Fact]
    public void Showing_the_transfers_opens_the_bottom_panel_and_asks_for_its_tab()
    {
        var shell = TestShell.Create(out _);
        shell.IsBottomPanelVisible = false;
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        shell.ShowTransfers();

        Assert.True(shell.IsBottomPanelVisible);
        Assert.Equal(BottomTab.Transfers, shell.BottomTab);
        Assert.Equal([UiRequest.ShowTransfers], requests);
    }

    [Fact]
    public void The_transfers_button_shows_the_transfers_and_hides_them_again()
    {
        var shell = TestShell.Create(out _);
        shell.IsBottomPanelVisible = false;

        shell.ToggleTransfers();
        Assert.True(shell.ShowsBottom);
        Assert.Equal(BottomTab.Transfers, shell.BottomTab);

        shell.ToggleTransfers();
        Assert.False(shell.IsBottomPanelVisible);

        // Zeigt der Bereich gerade etwas anderes, holt der Knopf die Transfers nach vorn, statt ihn zu schließen.
        shell.IsBottomPanelVisible = true;
        shell.BottomTab = BottomTab.Output;
        shell.ToggleTransfers();
        Assert.True(shell.IsBottomPanelVisible);
        Assert.Equal(BottomTab.Transfers, shell.BottomTab);
    }

    [Fact]
    public void Problems_are_counted_and_their_button_is_there_only_while_there_are_some()
    {
        var shell = TestShell.Create(out _);
        shell.IsBottomPanelVisible = false;
        Assert.Equal((0, false), (shell.ProblemCount, shell.HasProblems));

        shell.Log("first", LogSeverity.Error);
        shell.Log("second", LogSeverity.Warning);
        shell.Log("only output", LogSeverity.Info);
        Assert.Equal((2, true), (shell.ProblemCount, shell.HasProblems));

        shell.ToggleProblems();
        Assert.True(shell.IsBottomPanelVisible);
        Assert.Equal(BottomTab.Problems, shell.BottomTab);
        shell.ToggleProblems();
        Assert.False(shell.IsBottomPanelVisible);

        shell.ClearProblems();
        Assert.Equal((0, false), (shell.ProblemCount, shell.HasProblems));
    }

    [Fact]
    public void Details_are_offered_only_for_a_server_that_can_be_measured()
    {
        var shell = TestShell.Create(out var backend);
        Assert.False(shell.CanShowDetails);

        var web = shell.OpenSession(backend.GetProfiles()[0]);
        web.Start();
        Assert.True(shell.CanShowDetails);
        Assert.True(shell.ShowsMonitorBar);

        // Auch unter „zwei Spalten“: Die Zeile gilt dem ganzen Fenster.
        shell.IsDualFiles = true;
        Assert.True(shell.ShowsMonitorBar);
        shell.IsDualFiles = false;

        // Ein lokales Terminal hat keinen Server, die Startseite keine Sitzung.
        Assert.NotNull(shell.OpenLocalTerminal("cmd"));
        Assert.False(shell.CanShowDetails);
        Assert.False(shell.ShowsMonitorBar);
        shell.ShowHome();
        Assert.False(shell.CanShowDetails);
    }

    [Fact]
    public void The_values_of_the_bottom_row_read_short()
    {
        Assert.Equal("2.1 / 8 GB", Format.UsedOfTotal(2_254_857_830, 8L << 30));
        Assert.Equal("2.1 / 7.8 GB", Format.UsedOfTotal(2_254_857_830, 8_375_000_000));
        Assert.Equal("512 / 900 MB", Format.UsedOfTotal(512L << 20, 900L << 20));
        Assert.Equal("↓ 1.2 ↑ 0.3 MB/s", Format.NetworkShort(1_258_291, 314_573));
        Assert.Equal("↓ 0 ↑ 0 B/s", Format.NetworkShort(0, 0));
        Assert.Equal("up 12 days", Format.UptimeSince(TimeSpan.FromDays(12.4)));
        Assert.Equal("up 1 day", Format.UptimeSince(TimeSpan.FromHours(30)));
        Assert.Equal("up 5 h", Format.UptimeSince(TimeSpan.FromHours(5.5)));
    }

    [Fact]
    public void Home_shows_the_servers_without_closing_a_session()
    {
        var backend = new ScriptedBackend(Profile);
        var shell = TestShell.Create(backend);
        Assert.False(shell.RunCommand("view.home"));

        var first = shell.OpenSession(Profile);
        var second = shell.OpenSession(Profile);

        Assert.True(shell.RunCommand("view.home"));

        Assert.Null(shell.ActiveSession);
        Assert.True(shell.HasNoActiveSession);
        Assert.Equal([first, second], shell.Sessions);
        Assert.Empty(shell.Layout.Panes);
        Assert.DoesNotContain(backend.Connections, connection => connection.Disposed);

        // Mit der Tastatur zurück: vorwärts zum ersten Tab, rückwärts zum letzten.
        Assert.True(shell.RunCommand("session.next"));
        Assert.Same(first, shell.ActiveSession);
        shell.ShowHome();
        Assert.True(shell.RunCommand("session.previous"));
        Assert.Same(second, shell.ActiveSession);
        Assert.Equal([second], shell.Layout.Panes);

        // Von der Startseite aus lässt sich ein Tab schließen, ohne dass ein anderer aktiv wird.
        shell.ShowHome();
        shell.CloseSession(first);
        Assert.Null(shell.ActiveSession);
        Assert.Equal([second], shell.Sessions);
    }

    [Fact]
    public void The_file_pane_names_its_folder_and_counts_what_it_shows()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        Assert.Equal(string.Empty, session.Files.CountText);

        session.Start();

        Assert.Equal(RemotePath.Segments(session.Files.CurrentPath)[^1].Name, session.Files.CurrentName);
        Assert.NotEmpty(session.Files.Entries);
        Assert.Equal($"{session.Files.Entries.Count} entries", session.Files.CountText);

        // Ganz oben heißt das Verzeichnis „/“.
        Assert.Equal("/", new SftpBrowserViewModel().CurrentName);
    }

    [Fact]
    public void The_history_records_when_a_connection_stood_how_long_and_how_it_ended()
    {
        var backend = new ScriptedBackend(Profile);
        var settings = new AppSettings();
        L.Init(AppLanguage.English);
        var shell = new ShellViewModel(backend, new ImmediateDispatcher(), settings, new FakePrompts());
        var clock = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        shell.Now = () => clock;

        // Eine Verbindung, die nie stand, steht nicht im Verlauf.
        var session = shell.OpenSession(Profile);
        session.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        Assert.Empty(shell.History);

        session.Reconnect();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        clock = clock.AddMinutes(12);
        backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));

        var lost = Assert.Single(shell.History);
        Assert.Equal(("web01", "ubuntu@10.0.1.10", 720, "Connection lost."), (lost.Server, lost.Address, lost.Seconds, lost.End));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero), lost.Started);

        // Wieder verbunden und der Tab geschlossen: ein zweiter Eintrag, der jüngste zuerst.
        session.Reconnect();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        clock = clock.AddSeconds(30);
        shell.CloseSession(session);
        Assert.Equal(["Tab closed.", "Connection lost."], shell.History.Select(record => record.End));
        Assert.Equal(30, shell.History[0].Seconds);

        // Er steht in den Einstellungen dieses Rechners und kommt mit dem nächsten Start wieder.
        Assert.Equal(shell.History, settings.History);
        var again = new ShellViewModel(new ScriptedBackend(Profile), new ImmediateDispatcher(), settings.Sanitize(), new FakePrompts());
        Assert.Equal(2, again.History.Count);

        again.ClearHistory();
        Assert.Empty(again.History);
        Assert.Empty(settings.History);
    }

    [Fact]
    public void Known_servers_are_listed_on_request_and_forgotten_one_by_one()
    {
        var shell = TestShell.Create(out _);

        // Gelesen wird erst, wenn die Einstellungen sie zeigen.
        Assert.Empty(shell.KnownHosts);
        shell.LoadKnownHosts();
        Assert.Equal(["10.0.1.10", "[10.0.2.10]:2222"], shell.KnownHosts.Select(host => host.Name));
        Assert.Equal("10.0.1.10, ssh-ed25519", shell.KnownHosts[0].ToString());

        shell.ForgetHost(shell.KnownHosts[0]);

        Assert.Equal(["[10.0.2.10]:2222"], shell.KnownHosts.Select(host => host.Name));
        Assert.Contains(shell.Output, entry => entry.Text.StartsWith("Forgot the host key of 10.0.1.10", StringComparison.Ordinal));
    }

    [Fact]
    public void The_core_is_asked_for_known_servers_and_told_which_one_to_forget()
    {
        var transport = new FakeTransport();
        transport.Responses["hostkeys.list"] =
            """{"ok":true,"host_keys":[{"name":"[example.org]:2222","algorithm":"ssh-ed25519","fingerprint":"SHA256:abc"}]}""";
        using var backend = new CoreBackend(transport);

        var host = Assert.Single(backend.KnownHosts());
        Assert.Equal(new KnownHost("[example.org]:2222", "ssh-ed25519", "SHA256:abc"), host);

        Assert.True(backend.ForgetHost(host.Name));
        Assert.Equal("[example.org]:2222", Assert.Single(transport.Sent("hostkeys.forget")).GetProperty("name").GetString());

        transport.Responses["hostkeys.forget"] = """{"ok":false,"error":"not_found","message":"x"}""";
        Assert.False(backend.ForgetHost("x"));
    }

    [Fact]
    public void Durations_read_roughly_and_the_history_keeps_only_so_many_entries()
    {
        Assert.Equal(("45 s", "12 min", "3 h 05 min"), (Format.Duration(45), Format.Duration(720), Format.Duration(11_100)));

        var many = Enumerable.Range(0, AppSettings.MaxHistory + 20)
            .Select(index => new ConnectionRecord($"s{index}", "a@b", DateTimeOffset.UnixEpoch, 1, "x"))
            .Append(new ConnectionRecord(string.Empty, "a@b", DateTimeOffset.UnixEpoch, 1, "x"))
            .ToList();
        var settings = new AppSettings { History = many }.Sanitize();
        Assert.Equal(AppSettings.MaxHistory, settings.History.Count);
        Assert.Equal("s0", settings.History[0].Server);
    }

    [Fact]
    public void Disconnect_ends_the_connection_of_the_active_tab_and_leaves_the_tab_open()
    {
        var backend = new ScriptedBackend(Profile);
        var shell = TestShell.Create(backend);
        Assert.False(shell.RunCommand("session.disconnect"));

        var session = shell.OpenSession(Profile);

        // Noch nicht gestartet: Es gibt nichts zu trennen.
        Assert.False(shell.RunCommand("session.disconnect"));

        session.Start();
        var connection = backend.Last;
        connection.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.False(shell.RunCommand("session.reconnect"));

        Assert.True(shell.RunCommand("session.disconnect"));

        Assert.True(connection.Disposed);
        Assert.True(session.IsClosed);
        Assert.Same(session, Assert.Single(shell.Sessions));
        Assert.Same(session, shell.ActiveSession);
        Assert.Contains(shell.Output, entry => entry.Text == "web01: Connection closed.");
        Assert.Empty(shell.Problems);

        // Getrennt ist getrennt – weiter geht es mit „Erneut verbinden“.
        Assert.False(shell.RunCommand("session.disconnect"));
        Assert.True(shell.RunCommand("session.reconnect"));
        Assert.NotSame(connection, backend.Last);
        Assert.True(backend.Last.Started);
    }
}
