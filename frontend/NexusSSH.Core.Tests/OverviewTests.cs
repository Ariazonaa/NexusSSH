using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Die Übersicht aller offenen Sitzungen: Gemessen wird für alle nur, solange sie zu sehen ist.</summary>
[Collection(LanguageCollection.Name)]
public sealed class OverviewTests
{
    public OverviewTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, List<SessionViewModel> Sessions, List<WatchedMonitor> Monitors) Open(int count, AppSettings? settings = null)
    {
        var shell = TestShell.Create(out var backend, settings);
        var profiles = backend.Profiles.GetProfiles();
        var sessions = Enumerable.Range(0, count).Select(index => shell.OpenSession(profiles[index % profiles.Count])).ToList();
        var monitors = sessions.Select(session =>
        {
            var monitor = new WatchedMonitor();
            session.Inspector.Attach(monitor);
            return monitor;
        }).ToList();
        return (shell, sessions, monitors);
    }

    [Fact]
    public void All_sessions_measure_while_the_overview_shows_and_only_then()
    {
        var (shell, sessions, monitors) = Open(3);
        shell.IsSnippetsVisible = true;
        shell.ActiveSession = sessions[0];
        Assert.Equal([true, false, false], monitors.Select(monitor => monitor.Active));
        Assert.False(shell.IsOverviewShown);

        shell.ShowHome(HomePage.Overview);

        Assert.True(shell.IsOverviewShown);
        Assert.Equal([true, true, true], monitors.Select(monitor => monitor.Active));

        // Eine andere Seite der Startseite, die Einstellungen darüber oder ein Tab: Niemand sieht mehr hin.
        shell.HomePage = HomePage.Servers;
        Assert.Equal([false, false, false], monitors.Select(monitor => monitor.Active));
        shell.HomePage = HomePage.Overview;
        Assert.Equal([true, true, true], monitors.Select(monitor => monitor.Active));
        shell.IsSettingsOpen = true;
        Assert.Equal([false, false, false], monitors.Select(monitor => monitor.Active));
        shell.IsSettingsOpen = false;
        Assert.Equal([true, true, true], monitors.Select(monitor => monitor.Active));

        shell.ActiveSession = sessions[1];
        Assert.False(shell.IsOverviewShown);
        Assert.Equal([false, true, false], monitors.Select(monitor => monitor.Active));

        // Der Befehl der Palette führt dorthin.
        shell.Commands.Single(command => command.Id == "home.overview").Run();
        Assert.True(shell.IsOverviewShown);
        Assert.Equal([true, true, true], monitors.Select(monitor => monitor.Active));
    }

    [Fact]
    public void The_rows_show_what_each_inspector_measured()
    {
        var (shell, sessions, monitors) = Open(2);
        shell.ShowHome(HomePage.Overview);

        monitors[1].Measure(new ServerStats(true, 42, 61, 73, "Debian", "6.1", TimeSpan.FromHours(3), MemoryUsed: 61, MemoryTotal: 100, DiskUsed: 73, DiskTotal: 100, Load: "0.42 0.31 0.25"));

        Assert.False(sessions[0].Inspector.HasMetrics);
        var measured = sessions[1].Inspector;
        Assert.True(measured.HasMetrics);
        Assert.Equal((42, 61, 73, "0.42 0.31 0.25"), (measured.CpuPercent, measured.MemoryPercent, measured.DiskPercent, measured.LoadText));
        Assert.Equal(Format.Percent(42), measured.CpuText);

        // Die Zeile heißt für Screenreader wie ihr Tab.
        Assert.Equal(sessions[1].Title, sessions[1].ToString());
    }

    [Fact]
    public void A_row_without_values_says_why()
    {
        Assert.Equal(string.Empty, ShellViewModel.OverviewNote(measurable: true, ConnectionState.Connected, hasMetrics: true, unavailable: false));
        Assert.Equal("measuring …", ShellViewModel.OverviewNote(true, ConnectionState.Connected, false, false));
        Assert.Equal("not connected", ShellViewModel.OverviewNote(true, ConnectionState.Closed, false, false));
        Assert.Equal("not connected", ShellViewModel.OverviewNote(true, ConnectionState.Idle, false, false));
        Assert.Equal("no usage data", ShellViewModel.OverviewNote(true, ConnectionState.Connected, false, unavailable: true));

        // Telnet, seriell, FTP und die Fenster von tmux messen nie.
        Assert.Equal("no usage data", ShellViewModel.OverviewNote(measurable: false, ConnectionState.Connected, false, false));
    }

    [Fact]
    public void A_session_that_cannot_measure_is_not_asked_to()
    {
        var shell = TestShell.Create(out var backend);
        var telnet = backend.Profiles.GetProfiles()[0] with { Id = "t", Protocol = "telnet" };
        var session = shell.OpenSession(telnet);
        var monitor = new WatchedMonitor();
        session.Inspector.Attach(monitor);

        shell.ShowHome(HomePage.Overview);

        Assert.False(monitor.Active);
    }

    private sealed class WatchedMonitor : IServerMonitor
    {
        public bool Active { get; private set; }

        public ServerStats? Current { get; private set; }

        public bool IsUnavailable => false;

        public event Action? Changed;

        public void SetActive(bool active) => Active = active;

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public void Measure(ServerStats stats)
        {
            Current = stats;
            Changed?.Invoke();
        }
    }
}
