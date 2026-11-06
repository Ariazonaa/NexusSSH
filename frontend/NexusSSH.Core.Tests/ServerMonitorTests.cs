using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Server-Inspektor (Spec §6): Auslastung vom Kern bis in die Anzeige – und nur, solange jemand hinsieht.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerMonitorTests
{
    private const string Profile = """{"id":"p1","name":"web01","host":"10.0.1.10","port":22,"user":"ubuntu","group":"","tags":[],"favorite":false,"use_agent":true}""";

    private const string Stats =
        """{"event":"monitor.stats","session":1,"cpu":30,"memory_used":2147483648,"memory_total":8589934592,"disk_used":30064771072,"disk_total":107374182400,"os":"Ubuntu 24.04.1 LTS","kernel":"Linux 6.8.0-45-generic","uptime":2764800}""";

    private static (IServerConnection Connection, FakeTransport Transport) Connected()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = $$"""{"ok":true,"profiles":[{{Profile}}]}""";
        var backend = new CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        Assert.Null(connection.Monitor);

        transport.Raise("""{"event":"session.state","session":1,"state":"connected"}""");
        return (connection, transport);
    }

    [Fact]
    public void Memory_parts_and_swap_come_with_the_measurement_and_stay_within_what_is_possible()
    {
        var (connection, transport) = Connected();
        var monitor = Assert.IsAssignableFrom<IServerMonitor>(connection.Monitor);

        transport.Raise("""{"event":"monitor.stats","session":1,"cpu":5,"memory_used":3000,"memory_total":8000,"memory_cache":2000,"memory_free":600,"swap_used":250,"swap_total":2000,"disk_used":0,"disk_total":0,"os":"","kernel":"","uptime":0}""");
        var parts = Assert.IsType<MemoryParts>(monitor.Current!.Memory);
        Assert.Equal((2000L, 600L, 250L, 2000L), (parts.Cache, parts.Free, parts.SwapUsed, parts.SwapTotal));
        Assert.True(parts.HasSplit);
        Assert.Equal(12.5, parts.SwapPercent);

        // Ohne Aufteilung und ohne Auslagerung; und ein Server, der mehr belegt nennt, als es gibt.
        transport.Raise("""{"event":"monitor.stats","session":1,"cpu":5,"memory_used":1,"memory_total":4,"swap_used":9,"swap_total":0,"disk_used":0,"disk_total":0,"os":"","kernel":"","uptime":0}""");
        parts = monitor.Current!.Memory!;
        Assert.False(parts.HasSplit);
        Assert.False(parts.HasSwap);
        Assert.Equal(0, parts.SwapUsed);
    }

    [Fact]
    public void The_core_measures_only_while_asked_to()
    {
        var (connection, transport) = Connected();
        var monitor = Assert.IsAssignableFrom<IServerMonitor>(connection.Monitor);
        Assert.Null(monitor.Current);
        Assert.Empty(transport.Sent("monitor.set"));

        monitor.SetActive(true);
        monitor.SetActive(true);
        monitor.SetActive(false);

        Assert.Equal(
            [(1UL, true), (1UL, false)],
            transport.Sent("monitor.set").Select(request => (request.GetProperty("session").GetUInt64(), request.GetProperty("enabled").GetBoolean())));
    }

    [Fact]
    public void A_measurement_becomes_percentages_and_amounts()
    {
        var (connection, transport) = Connected();
        var monitor = connection.Monitor!;
        var changes = 0;
        monitor.Changed += () => changes++;

        transport.Raise(Stats);

        var stats = Assert.IsType<ServerStats>(monitor.Current);
        Assert.Equal(30, stats.CpuPercent);
        Assert.Equal(25, stats.MemoryPercent);
        Assert.Equal(28, stats.DiskPercent);
        Assert.Equal(8589934592, stats.MemoryTotal);
        Assert.Equal("Ubuntu 24.04.1 LTS", stats.OsName);
        Assert.Equal("Linux 6.8.0-45-generic", stats.Kernel);
        Assert.Equal(TimeSpan.FromDays(32), stats.Uptime);
        Assert.False(monitor.IsUnavailable);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_server_without_values_is_flagged_and_an_unknown_disk_is_not_a_division_by_zero()
    {
        var (connection, transport) = Connected();
        var monitor = connection.Monitor!;

        transport.Raise("""{"event":"monitor.stats","session":1,"cpu":5,"memory_used":1,"memory_total":4,"disk_used":0,"disk_total":0,"os":"","kernel":"","uptime":0}""");
        Assert.Equal(0, monitor.Current!.DiskPercent);

        transport.Raise("""{"event":"monitor.unavailable","session":1,"message":"the server does not provide /proc"}""");
        Assert.True(monitor.IsUnavailable);
        Assert.Equal("the server does not provide /proc", monitor.UnavailableDetail);

        // Der Grund landet im Protokoll – ohne Steuerzeichen.
        transport.Raise("""{"event":"monitor.unavailable","session":1,"message":"bad\u001b[31m\nline"}""");
        Assert.DoesNotContain(monitor.UnavailableDetail!, char.IsControl);
    }

    [Fact]
    public void Measurements_of_another_or_an_ended_session_go_nowhere()
    {
        var (connection, transport) = Connected();
        var monitor = connection.Monitor!;

        transport.Raise(Stats.Replace("\"session\":1", "\"session\":7"));
        Assert.Null(monitor.Current);

        transport.Raise("""{"event":"session.state","session":1,"state":"closed","reason":"remote"}""");
        Assert.Null(connection.Monitor);
        transport.Raise(Stats);
        Assert.Null(monitor.Current);
    }

    [Fact]
    public void The_inspector_shows_waiting_values_and_unavailable()
    {
        L.Init(AppLanguage.German);
        var (connection, transport) = Connected();
        var inspector = new ServerInspectorViewModel(new ServerProfile("p1", "web01", "h", 22, "u", "", []), new ImmediateDispatcher());

        inspector.Attach(connection.Monitor);
        Assert.False(inspector.HasMetrics);
        Assert.False(inspector.IsMeasuring, "niemand sieht hin, also wird auch nicht gemessen");
        Assert.Empty(transport.Sent("monitor.set"));

        inspector.SetWatched(true);
        Assert.True(inspector.IsMeasuring);
        Assert.Single(transport.Sent("monitor.set"));

        transport.Raise(Stats);
        Assert.True(inspector.HasMetrics);
        Assert.False(inspector.IsMeasuring);
        Assert.Equal("30 %", inspector.CpuText);
        Assert.Equal("25 % · 2,0 GB / 8,0 GB", inspector.MemoryText);
        Assert.Equal("28 % · 28,0 GB / 100 GB", inspector.DiskText);
        Assert.Equal("32 Tage", inspector.UptimeText);

        transport.Raise("""{"event":"monitor.unavailable","session":1,"message":"x"}""");
        Assert.True(inspector.IsUnavailable);
        Assert.False(inspector.HasMetrics);
        Assert.Equal(L.Get("InspectorUnavailable"), inspector.UnavailableText);

        // Kein Kanal mehr frei: Das lässt sich am Server ändern, also steht da, woran es liegt.
        transport.Raise("""{"event":"monitor.unavailable","session":1,"message":"the server allows no further channel on this connection (MaxSessions in its sshd_config)"}""");
        Assert.Equal(L.Get("ExecNoChannel"), inspector.UnavailableText);

        // Verstummt, nachdem er schon Werte lieferte: Der Kern versucht es neu – das steht da, und mit den
        // nächsten Werten ist alles wieder wie vorher.
        transport.Raise("""{"event":"monitor.unavailable","session":1,"message":"the server stopped answering","retrying":true}""");
        Assert.True(inspector.IsUnavailable);
        Assert.Equal(L.Get("InspectorSilent"), inspector.UnavailableText);
        transport.Raise(Stats);
        Assert.False(inspector.IsUnavailable);
        Assert.True(inspector.HasMetrics);
        Assert.False(connection.Monitor!.IsRetrying);

        // Abgehängt (Verbindung zu Ende) hört das Messen auf.
        inspector.Attach(null);
        Assert.False(inspector.IsUnavailable);
        Assert.False(inspector.IsMeasuring);
        Assert.False(transport.Sent("monitor.set").Last().GetProperty("enabled").GetBoolean());
        L.Init(AppLanguage.English);
    }

    [Fact]
    public void Cores_the_parts_of_the_load_and_all_file_systems_reach_the_inspector()
    {
        var (connection, transport) = Connected();
        var inspector = new ServerInspectorViewModel(new ServerProfile("p1", "web01", "h", 22, "u", "", []), new ImmediateDispatcher());
        inspector.Attach(connection.Monitor);
        inspector.SetWatched(true);

        // Ohne die neuen Angaben (älterer Stand, BSD ohne Kerne) bleibt alles leer.
        transport.Raise(Stats);
        Assert.Equal((false, false, false, true, false), (inspector.HasCores, inspector.HasSplit, inspector.HasDisks, inspector.ShowsRootDisk, inspector.HasCpuDetails));

        var detailed = Stats.TrimEnd('}')
            + ""","cores":[80,10,250,-3],"cpu_user":25,"cpu_system":15,"cpu_iowait":10,"cpu_steal":0,"disks":["""
            + """{"mount":"/","device":"/dev/sda1","used":30064771072,"total":107374182400,"inodes_used":250000,"inodes_total":6400000},"""
            + """{"mount":"/srv/da\u001bta","device":"/dev/sdb1","used":94,"total":100},"""
            + """{"mount":"/home","device":"/dev/sdc1","used":50,"total":100,"inodes_used":99,"inodes_total":100},"""
            + """{"mount":"","device":"x","used":1,"total":2},{"mount":"/empty","device":"y","used":0,"total":0}]}""";
        transport.Raise(detailed);

        // Werte außerhalb von 0–100 sind keine: Der Kern ist die Quelle, aber die Anzeige rechnet damit.
        Assert.Equal([80, 10, 100, 0], inspector.Cores.Select(core => core.Percent));
        Assert.Equal("Core 2: 100%", inspector.Cores[2].Text);
        Assert.Equal("Cores: 80%, 10%, 100%, 0%", inspector.CoresText);
        Assert.True(inspector.HasCores);
        Assert.True(inspector.HasCpuDetails, "Kerne und Aufteilung stehen unter der Überschrift CPU");
        Assert.False(inspector.ShowsRootDisk, "die Liste nennt / selbst");

        // Gestohlenes gibt es hier nicht – dann steht es auch nicht da.
        Assert.Equal("user 25% · system 15% · I/O wait 10%", inspector.SplitText);

        // Ohne Namen oder ohne Größe ist es kein Dateisystem; ein Steuerzeichen im Namen wird zu seinem sichtbaren Zeichen.
        Assert.Equal(["/", "/srv/da\u241Bta", "/home"], inspector.Disks.Select(disk => disk.Mount));
        Assert.Equal(
            [UsageLevel.Normal, UsageLevel.Warning, UsageLevel.Critical],
            inspector.Disks.Select(disk => disk.Level));
        Assert.Equal("28.0 GB / 100 GB · inodes 4%", inspector.Disks[0].Detail);
        Assert.Equal("94 B / 100 B", inspector.Disks[1].Detail);

        // Volle Inodes sind so dringend wie voller Platz – auch wenn der Platz zur Hälfte frei ist.
        Assert.Equal((50, 99), (inspector.Disks[2].Percent, inspector.Disks[2].InodePercent));

        // Dieselbe Messung noch einmal baut die Listen nicht neu.
        var (cores, disks) = (inspector.Cores, inspector.Disks);
        transport.Raise(detailed);
        Assert.Same(cores, inspector.Cores);
        Assert.Same(disks, inspector.Disks);

        // Ein einzelner Kern ist keine Liste wert.
        transport.Raise(Stats.TrimEnd('}') + ""","cores":[30]}""");
        Assert.Equal((false, false, false, false), (inspector.HasCores, inspector.HasSplit, inspector.HasDisks, inspector.HasCpuDetails));
    }

    [Fact]
    public void What_the_machine_is_and_its_clock_reach_the_inspector()
    {
        var (connection, transport) = Connected();
        var inspector = new ServerInspectorViewModel(new ServerProfile("p1", "web01", "h", 22, "u", "", []), new ImmediateDispatcher());
        inspector.Attach(connection.Monitor);
        inspector.SetWatched(true);

        // Ohne die Angaben steht keine der Zeilen da.
        transport.Raise(Stats);
        Assert.Equal((false, false, false, false), (inspector.HasMachine, inspector.HasProcessor, inspector.HasVirtualization, inspector.HasClock));

        // Die Uhr des Servers geht zwei Minuten vor; er steht in Indien.
        var ahead = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 125;
        transport.Raise(Stats.TrimEnd('}')
            + ""","cpu_model":"Intel(R) Xeon(R) Gold 6140","core_count":4,"machine":"QEMU Standard\u0007 PC","virtualization":"kvm","""
            + $"\"time\":{ahead}," + """ "utc_offset":19800,"zone":"IST"}""");

        Assert.Equal("QEMU Standard\u2407 PC", inspector.MachineText);
        Assert.Equal("Intel(R) Xeon(R) Gold 6140 · 4 cores", inspector.ProcessorText);
        Assert.Equal("kvm", inspector.VirtualizationText);
        Assert.EndsWith(" · UTC+05:30 (IST) · 2 min ahead", inspector.ClockText, StringComparison.Ordinal);

        // Was nicht sein kann, steht nicht da: eine Art, die kein Wort ist, null Kerne, eine Zeitzone neben der Minute.
        transport.Raise(Stats.TrimEnd('}') + ""","core_count":0,"virtualization":"a b","time":5,"utc_offset":61}""");
        Assert.Equal((false, false, false, false), (inspector.HasMachine, inspector.HasProcessor, inspector.HasVirtualization, inspector.HasClock));

        // Ein Kern, eigene Hardware.
        transport.Raise(Stats.TrimEnd('}') + ""","core_count":1,"virtualization":"none"}""");
        Assert.Equal(("1 core", "none (bare metal)"), (inspector.ProcessorText, inspector.VirtualizationText));
        transport.Raise(Stats.TrimEnd('}') + ""","virtualization":"vm"}""");
        Assert.Equal("virtual machine", inspector.VirtualizationText);
    }

    [Fact]
    public void The_clock_names_time_zone_and_how_far_it_is_off()
    {
        var here = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        string Text(long secondsOff, int offset, string? zone)
            => ServerInspectorViewModel.Describe(ServerClock.From(here.ToUnixTimeSeconds() + secondsOff, offset, zone, here));

        // Bis drei Sekunden gilt die Uhr als gleich: Die Messung war unterwegs, und der Server nennt ganze Sekunden.
        Assert.Equal("14:00:02 · UTC+02:00 (CEST) · same as here", Text(2, 7200, "CEST"));
        Assert.Equal("12:00:45 · UTC+00:00 · 45 s ahead", Text(45, 0, null));
        Assert.Equal("02:15:00 · UTC-09:30 · 15 min behind", Text(-900, -(9 * 3600 + 30 * 60), "+0930"));
        Assert.Equal("14:00:00 · UTC+00:00 (UTC) · 2 h 00 min ahead", Text(7200, 0, "UTC"));
        Assert.Equal("12:01:59 · UTC+00:00 · 119 s ahead", Text(119, 0, null));

        // Keine Zeit, keine Zeitzone: keine Uhr.
        Assert.Null(ServerClock.From(null, 0, "UTC", here));
        Assert.Null(ServerClock.From(0, 0, "UTC", here));
        Assert.Null(ServerClock.From(here.ToUnixTimeSeconds(), null, "UTC", here));
        Assert.Null(ServerClock.From(here.ToUnixTimeSeconds(), 15 * 3600, "UTC", here));
        Assert.Null(ServerClock.From(long.MaxValue, 0, "UTC", here));
    }

    [Theory]
    [InlineData(89, UsageLevel.Normal)]
    [InlineData(90, UsageLevel.Warning)]
    [InlineData(94, UsageLevel.Warning)]
    [InlineData(95, UsageLevel.Critical)]
    [InlineData(100, UsageLevel.Critical)]
    public void A_file_system_warns_from_ninety_percent(long used, UsageLevel expected)
        => Assert.Equal(expected, new DiskUsage("/", "/dev/sda1", used, 100).Level);

    [Fact]
    public void The_bar_under_the_terminal_measures_the_active_tab_without_the_inspector()
    {
        var shell = TestShell.Create(out var backend);
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        web.Start();
        shell.IsSnippetsVisible = false;

        // Von Haus aus an, wie bei MobaXterm.
        Assert.True(shell.MonitorBar);
        Assert.True(shell.ShowsMonitorBar);
        Assert.True(MonitorOf(web).IsActive);

        // Aus: Gemessen wird nichts mehr, das Kästchen bleibt zum Wiedereinschalten stehen.
        shell.MonitorBar = false;
        Assert.False(MonitorOf(web).IsActive);
        Assert.True(shell.ShowsMonitorBar);
        Assert.False(shell.Settings.MonitorBar);
        Assert.False(shell.IsDefaultSetting("MonitorBar"));

        shell.ToggleMonitorBar();
        Assert.True(MonitorOf(web).IsActive);

        // Nur das Terminal: Die Leiste weicht, und ohne Inspector misst niemand.
        shell.IsTerminalOnly = true;
        Assert.False(shell.ShowsMonitorBar);
        Assert.False(MonitorOf(web).IsActive);
        shell.IsTerminalOnly = false;

        // Die Startseite hat kein Terminal und keine Leiste.
        shell.ShowHome();
        Assert.False(shell.ShowsMonitorBar);
    }

    [Fact]
    public void Only_the_active_tab_is_measured_and_only_while_its_values_are_shown()
    {
        var shell = TestShell.Create(out var backend);
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        var database = shell.OpenSession(backend.GetProfiles()[1]);
        web.Start();
        database.Start();

        // Die Zeile unten zeigt die Werte des aktiven Tabs.
        Assert.Equal((false, true), (Watched(web), Watched(database)));
        shell.ActiveSession = web;
        Assert.Equal((true, false), (Watched(web), Watched(database)));

        // Ohne sie misst niemand – die Snippets rechts haben damit nichts zu tun.
        shell.MonitorBar = false;
        shell.IsSnippetsVisible = true;
        Assert.Equal((false, false), (Watched(web), Watched(database)));

        // Die Seite „Server-Details“ misst, solange sie offen ist.
        shell.ShowDetails();
        Assert.Same(web, shell.Details);
        Assert.Equal((true, false), (Watched(web), Watched(database)));
        shell.CloseDetails();
        Assert.Equal((false, false), (Watched(web), Watched(database)));

        // Eine Sitzung, die sich erst später verbindet, bekommt die Entscheidung beim Verbinden mit.
        shell.MonitorBar = true;
        var staging = shell.OpenSession(backend.GetProfiles()[2]);
        staging.Start();
        Assert.Equal((false, true), (Watched(web), Watched(staging)));

        static bool Watched(SessionViewModel session) => MonitorOf(session).IsActive;
    }

    [Fact]
    public void The_server_details_lie_over_the_panes_and_close_with_another_tab()
    {
        var shell = TestShell.Create(out var backend);
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        var database = shell.OpenSession(backend.GetProfiles()[1]);
        web.Start();
        database.Start();
        shell.ActiveSession = web;

        Assert.True(shell.RunCommand("server.details"));
        Assert.Same(web, shell.Details);
        Assert.True(shell.HasServerPage);
        Assert.False(shell.CanBeTerminalOnly);

        // „Details“ noch einmal schließt die Seite.
        shell.ShowDetails();
        Assert.Null(shell.Details);

        // Ein anderer Tab, die Startseite, die Einstellungen oder die Protokolle: Die Seite geht zu.
        foreach (var leave in (Action[])[() => shell.ActiveSession = database, shell.ShowHome, () => shell.IsSettingsOpen = true, () => shell.ShowLogs()])
        {
            shell.IsSettingsOpen = false;
            shell.ActiveSession = web;
            shell.ShowDetails();
            Assert.NotNull(shell.Details);
            leave();
            Assert.Null(shell.Details);
        }

        // Schließt ihr Tab, geht sie mit.
        shell.IsSettingsOpen = false;
        shell.ActiveSession = web;
        shell.ShowDetails();
        shell.CloseSession(web);
        Assert.Null(shell.Details);
    }

    [Fact]
    public void The_mock_values_move_only_while_watched()
    {
        var shell = TestShell.Create(out var backend);
        shell.MonitorBar = false;
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        web.Start();
        var monitor = MonitorOf(web);
        var before = web.Inspector.UptimeText;

        monitor.Advance();
        Assert.Equal(monitor.Current!.CpuPercent, web.Inspector.CpuPercent);
        Assert.InRange(web.Inspector.CpuPercent, 22, 46);
        Assert.Matches("^[0-9]+% · [0-9.]+ GB / 8[.]0 GB$", web.Inspector.MemoryText);

        shell.IsSnippetsVisible = false;
        var frozen = monitor.Current.CpuPercent;
        monitor.Advance();
        Assert.Equal(frozen, monitor.Current.CpuPercent);
        Assert.Equal(before, web.Inspector.UptimeText);
    }

    private static MockServerMonitor MonitorOf(SessionViewModel session) => Assert.IsType<MockServerMonitor>(session.Inspector.Monitor);
}
