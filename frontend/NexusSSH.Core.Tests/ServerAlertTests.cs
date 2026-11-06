using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Hinweise, wenn Platte, Speicher oder Last über einer Schwelle liegen – einmal je Überschreitung, und nur auf Wunsch.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerAlertTests
{
    private static readonly AlertThresholds Thresholds = new(90, 95, 2);

    public ServerAlertTests() => L.Init(AppLanguage.English);

    private static ServerStats Stats(double memory = 40, string load = "0.50 0.40 0.30", int cores = 4, params DiskUsage[] disks)
        => new(true, 10, memory, 0, "Debian", "Linux 6", TimeSpan.FromHours(1), (long)(memory * 10), 1000, Load: load, Disks: disks, Info: new ServerInfo("", "", cores, ""));

    [Fact]
    public void A_full_file_system_is_reported_once_and_again_only_after_it_had_room()
    {
        var watch = new AlertWatch();
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 89, 100)), Thresholds));

        var alert = Assert.Single(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 92, 100)), Thresholds));
        Assert.Equal((AlertKind.Disk, "/", "/ is 92% full (threshold 90%)"), (alert.Kind, alert.Subject, alert.Text));

        // Dieselbe Lage ist keine neue Meldung – auch nicht, wenn der Wert um die Schwelle pendelt.
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 93, 100)), Thresholds));
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 89, 100)), Thresholds));
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 91, 100)), Thresholds));

        // Erst wenn wieder Platz war (zwei Punkte unter der Schwelle), zählt das nächste Überschreiten neu.
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 87, 100)), Thresholds));
        Assert.Single(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 90, 100)), Thresholds));
    }

    [Fact]
    public void Every_file_system_and_its_inodes_count_for_themselves()
    {
        var watch = new AlertWatch();
        var alerts = watch.Check(
            Stats(disks:
            [
                new DiskUsage("/", "/dev/sda1", 30, 100, 10, 100),
                new DiskUsage("/srv/data", "/dev/sdb1", 95, 100, 99, 100),
                new DiskUsage("/home", "/dev/sdc1", 50, 100, 97, 100),
            ]),
            Thresholds);

        Assert.Equal(
            [
                "/srv/data is 95% full (threshold 90%)",
                "/srv/data: inodes 99% used (threshold 90%)",
                "/home: inodes 97% used (threshold 90%)",
            ],
            alerts.Select(alert => alert.Text));

        // Ausgehängt und wieder eingehängt: Das ist eine neue Lage.
        Assert.Empty(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 30, 100)), Thresholds));
        Assert.Single(watch.Check(Stats(disks: [new DiskUsage("/", "/dev/sda1", 30, 100), new DiskUsage("/srv/data", "/dev/sdb1", 95, 100)]), Thresholds));
    }

    [Fact]
    public void Without_a_list_the_root_file_system_is_watched()
    {
        var watch = new AlertWatch();
        var stats = new ServerStats(true, 10, 40, 96, "FreeBSD", "FreeBSD 14", TimeSpan.Zero, 400, 1000, 960, 1000);
        Assert.Equal("/ is 96% full (threshold 90%)", Assert.Single(watch.Check(stats, Thresholds)).Text);

        // Ohne bekannte Größe gibt es nichts zu melden.
        Assert.Empty(new AlertWatch().Check(new ServerStats(true, 10, 40, 0, "", "", TimeSpan.Zero, 400, 1000), Thresholds));
    }

    [Fact]
    public void Memory_and_load_must_stay_high_for_a_few_samples()
    {
        var watch = new AlertWatch();

        // Ein Ausschlag ist kein Zustand.
        Assert.Empty(watch.Check(Stats(memory: 97), Thresholds));
        Assert.Empty(watch.Check(Stats(memory: 60), Thresholds));
        Assert.Empty(watch.Check(Stats(memory: 97), Thresholds));
        Assert.Empty(watch.Check(Stats(memory: 97), Thresholds));
        Assert.Equal("memory 97% used (threshold 95%)", Assert.Single(watch.Check(Stats(memory: 97), Thresholds)).Text);
        Assert.Empty(watch.Check(Stats(memory: 98), Thresholds));

        // Die Last zählt je Kern: 8,4 bei vier Kernen sind 2,1 je Kern.
        Assert.Empty(watch.Check(Stats(load: "7.90 1.00 1.00"), Thresholds));
        Assert.Empty(watch.Check(Stats(load: "8.40 1.00 1.00"), Thresholds));
        Assert.Empty(watch.Check(Stats(load: "8.40 1.00 1.00"), Thresholds));
        Assert.Equal("load 8.4 with 4 cores (threshold 2.0 per core)", Assert.Single(watch.Check(Stats(load: "8.40 1.00 1.00"), Thresholds)).Text);

        // Dieselbe Last auf acht Kernen ist keine; ohne Angabe gilt ein Kern.
        var other = new AlertWatch();
        for (var sample = 0; sample < 5; sample++)
        {
            Assert.Empty(other.Check(Stats(load: "8.40 1.00 1.00", cores: 8), Thresholds));
        }

        Assert.Null(AlertWatch.LoadOf(Stats(load: "")));
        Assert.Null(AlertWatch.LoadOf(Stats(load: "x y z")));
        Assert.Equal(0.5, AlertWatch.LoadOf(Stats()));
    }

    [Fact]
    public void A_new_connection_starts_over()
    {
        var watch = new AlertWatch();
        Assert.Single(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 95, 100)), Thresholds));
        watch.Reset();
        Assert.Single(watch.Check(Stats(disks: new DiskUsage("/", "/dev/sda1", 95, 100)), Thresholds));
    }

    [Fact]
    public void Only_when_switched_on_are_all_open_sessions_measured_and_reported()
    {
        var shell = TestShell.Create(out var backend);
        List<(string Title, string Text)> notices = [];
        shell.AttentionRequested += (title, text) => notices.Add((title, text));
        var web = shell.OpenSession(backend.GetProfiles()[0]);
        var database = shell.OpenSession(backend.GetProfiles()[1]);
        web.Start();
        database.Start();
        shell.ActiveSession = database;

        // Von Haus aus: gemessen wird nur der sichtbare Tab, und gemeldet wird nichts.
        Assert.False(shell.ServerAlerts);
        Assert.Equal((false, true), (Active(web), Active(database)));
        Assert.Empty(notices);

        shell.ServerAlerts = true;
        Assert.Equal((true, true), (Active(web), Active(database)));

        // Die Beispieldaten haben zwei volle Dateisysteme; gemeldet wird je Sitzung und Dateisystem einmal – als ein
        // Hinweis je Messung (von zweien im selben Augenblick blendet Windows nur einen ein), unter „Probleme“ je
        // Lage eine Zeile.
        Measure(web);
        Measure(web);
        Assert.Equal(
            ["/var/lib/docker is 94% full (threshold 90%)\n/srv/backup is 96% full (threshold 90%)"],
            notices.Where(notice => notice.Title == web.Profile.Name).Select(notice => notice.Text));
        Assert.Contains(shell.Problems, entry => entry.Text == $"{web.Profile.Name}: /var/lib/docker is 94% full (threshold 90%)");
        Assert.Contains(shell.Problems, entry => entry.Text == $"{web.Profile.Name}: /srv/backup is 96% full (threshold 90%)");

        // Mehr, als ein Hinweis zeigt: Die letzte Zeile nennt den Rest als Zahl.
        Assert.Equal("a\nb\nc", ShellViewModel.NoticeFor(["a", "b", "c"]));
        Assert.Equal("a\nb\n… and 3 more, see “Problems”", ShellViewModel.NoticeFor(["a", "b", "c", "d", "e"]));

        // Eine höhere Schwelle gilt ab der nächsten Messung – für Lagen, die noch nicht gemeldet sind.
        shell.AlertDiskPercent = 95;
        var before = notices.Count;
        Measure(database);
        Assert.Equal(["/srv/backup is 96% full (threshold 95%)"], notices.Skip(before).Select(notice => notice.Text));

        // Ausgeschaltet ruht die Messung im Hintergrund wieder.
        shell.ServerAlerts = false;
        Assert.Equal((false, true), (Active(web), Active(database)));

        static bool Active(SessionViewModel session) => Assert.IsType<MockServerMonitor>(session.Inspector.Monitor).IsActive;
        static void Measure(SessionViewModel session) => Assert.IsType<MockServerMonitor>(session.Inspector.Monitor).Advance();
    }

    [Fact]
    public void Thresholds_stay_within_sensible_bounds()
    {
        var shell = TestShell.Create(out _);
        Assert.Equal((90, 95, 2), (shell.AlertDiskPercent, shell.AlertMemoryPercent, shell.AlertLoadPerCore));

        shell.AlertDiskPercent = 10;
        shell.AlertMemoryPercent = 400;
        shell.AlertLoadPerCore = 0;
        Assert.Equal((50, 99, 0.5), (shell.AlertDiskPercent, shell.AlertMemoryPercent, shell.AlertLoadPerCore));

        // Ein geleertes Feld ändert nichts.
        shell.AlertDiskPercent = double.NaN;
        Assert.Equal(50, shell.AlertDiskPercent);

        // Von Hand beschädigte Einstellungen kommen in den Bereich zurück.
        var settings = new AppSettings { AlertDiskPercent = 0, AlertMemoryPercent = 1000, AlertLoadPerCore = double.NaN }.Sanitize();
        Assert.Equal((50, 99, 2), (settings.AlertDiskPercent, settings.AlertMemoryPercent, settings.AlertLoadPerCore));
    }
}
