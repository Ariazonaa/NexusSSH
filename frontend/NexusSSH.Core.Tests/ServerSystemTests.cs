using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Das Betriebssystem eines Servers: erkennen, am Profil merken, an Kachel und Kopfzeile zeigen.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerSystemTests
{
    public ServerSystemTests() => L.Init(AppLanguage.English);

    [Theory]
    [InlineData("Ubuntu 24.04.1 LTS", "ubuntu")]
    [InlineData("Debian GNU/Linux 12 (bookworm)", "debian")]
    [InlineData("Raspbian GNU/Linux 11 (bullseye)", "raspbian")]
    [InlineData("Alpine Linux v3.20", "alpine")]
    [InlineData("Fedora Linux 41 (Server Edition)", "fedora")]
    [InlineData("Rocky Linux 9.4 (Blue Onyx)", "rocky")]
    [InlineData("AlmaLinux 9.4 (Seafoam Ocelot)", "almalinux")]
    [InlineData("CentOS Stream 9", "centos")]
    [InlineData("Red Hat Enterprise Linux 9.4 (Plow)", "rhel")]
    [InlineData("Amazon Linux 2023", "amazon")]
    [InlineData("openSUSE Leap 15.6", "opensuse")]
    [InlineData("Arch Linux", "arch")]
    [InlineData("FreeBSD", "freebsd")]
    [InlineData("OpenBSD", "openbsd")]
    [InlineData("Void Linux", "linux")]
    [InlineData("Linux", "linux")]
    public void A_system_is_recognised_by_the_name_the_server_reports(string name, string expected)
    {
        Assert.Equal(expected, ServerSystems.Detect(name)?.Id);
        Assert.Equal(expected, ServerSystems.Find(expected)?.Id);
    }

    [Fact]
    public void Every_system_has_its_sign_and_travels_with_an_export()
    {
        Assert.All(ServerSystems.Ids, id => Assert.StartsWith("M", ServerSystemIcons.Find(id), StringComparison.OrdinalIgnoreCase));
        Assert.Null(ServerSystemIcons.Find("plan9"));
        Assert.Null(ServerSystemIcons.Find(null));

        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, [], System: "debian");
        var file = Exchange.DataExchange.Create([profile], new AppSettings(), "1.0", DateTimeOffset.UnixEpoch, null);
        Assert.Equal("debian", Assert.Single(file.Profiles!).System);

        // Beim Import gilt nur eine Kennung, die es gibt; sonst bleibt, was hier schon erkannt war.
        file.Profiles![0] = file.Profiles[0] with { System = "nonsense" };
        var plan = Exchange.DataExchange.Plan(file, [profile], new AppSettings(), null, _ => true);
        Assert.Equal("debian", Assert.Single(plan.Profiles).Profile.System);
    }

    [Fact]
    public void Anything_else_keeps_the_ordinary_symbol()
    {
        Assert.Null(ServerSystems.Detect(""));
        Assert.Null(ServerSystems.Detect(null));
        Assert.Null(ServerSystems.Detect("Plan 9"));
        Assert.Null(ServerSystems.Find("plan9"));
        Assert.Null(ServerSystems.Find(null));
    }

    [Fact]
    public void The_first_connection_measures_once_and_remembers_the_system_at_the_profile()
    {
        // Einmal messen, auch wenn niemand hinsieht – und danach wieder ruhen.
        var inspector = new ServerInspectorViewModel(new ServerProfile("p", "web01", "h", 22, "u", string.Empty, []), new ImmediateDispatcher());
        var silent = new SilentMonitor();
        inspector.Attach(silent);
        Assert.False(silent.IsActive);

        inspector.Probe();
        Assert.True(silent.IsActive);

        silent.Report(new ServerStats(true, 1, 2, 3, "Debian GNU/Linux 12", "6.1", TimeSpan.FromHours(1)));
        Assert.False(silent.IsActive);
        Assert.Equal("Debian GNU/Linux 12", inspector.OsName);

        // Ein zweites Mal wird nicht gemessen: Die Werte sind da.
        inspector.Probe();
        Assert.False(silent.IsActive);

        // In der Shell: Sobald der Server sein System nennt, steht es an der Sitzung und am Profil.
        var shell = TestShell.Create(out _);
        shell.IsSnippetsVisible = false;
        shell.MonitorBar = false;
        var profile = shell.Profiles[0];
        Assert.Null(profile.System);

        var session = shell.OpenSession(profile);
        session.Start();
        var monitor = Assert.IsType<MockServerMonitor>(session.Connection.Monitor);

        Assert.False(monitor.IsActive);
        Assert.Equal("ubuntu", session.System);
        Assert.Equal("ubuntu", shell.Profiles.Single(candidate => candidate.Id == profile.Id).System);

        // Beim nächsten Mal ist es bekannt: Das Symbol steht sofort da, und gemessen wird erst, wenn jemand hinsieht.
        var again = shell.OpenSession(shell.Profiles.Single(candidate => candidate.Id == profile.Id));
        again.Start();
        Assert.Equal("ubuntu", again.System);
        Assert.False(Assert.IsType<MockServerMonitor>(again.Connection.Monitor).IsActive);
    }

    [Theory]
    [InlineData("PRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\nNAME=\"Debian GNU/Linux\"\nID=debian\n", "debian")]
    [InlineData("NAME=\"Ubuntu\"\nID_LIKE=debian\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n", "ubuntu")]
    [InlineData("NAME='Arch Linux'\r\nID=arch\r\n", "arch")]
    [InlineData("NAME=FreeBSD\nPRETTY_NAME=\"FreeBSD 14.5-RELEASE-p1\"\n", "freebsd")]
    [InlineData("PRETTY_NAME=\"\"\nNAME=\"Alpine Linux\"\n", "alpine")]
    [InlineData("ID=debian\n", null)]
    [InlineData("", null)]
    public void A_system_is_recognised_by_the_os_release_file(string content, string? expected)
        => Assert.Equal(expected, ServerSystems.FromOsRelease(System.Text.Encoding.UTF8.GetBytes(content))?.Id);

    [Fact]
    public async Task The_os_release_file_names_the_system_without_any_measurement()
    {
        // Wie bei einem Server, dessen Messung scheitert: Die Datei genügt, gemessen wird gar nicht erst. (Die
        // Beispieldaten nennen ihr System schon beim Verbinden – deshalb hier vergessen und von vorn.)
        var shell = TestShell.Create(out _);
        shell.IsSnippetsVisible = false;
        shell.MonitorBar = false;
        var profile = shell.Profiles[0];
        var session = shell.OpenSession(profile);
        session.Start();
        await session.Connection.Files!.WriteAsync("/etc/os-release", "PRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\n"u8.ToArray());
        session.System = null;

        await shell.RecognizeSystemAsync(session);

        Assert.Equal("debian", session.System);
        Assert.Equal("debian", shell.Profiles.Single(candidate => candidate.Id == profile.Id).System);
        Assert.False(Assert.IsType<MockServerMonitor>(session.Connection.Monitor).IsActive);
    }

    [Fact]
    public void Why_a_server_provides_no_load_figures_goes_to_the_output()
    {
        var shell = TestShell.Create(out _);
        shell.IsSnippetsVisible = false;
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        var monitor = Assert.IsType<MockServerMonitor>(session.Connection.Monitor);

        monitor.Refuse("the server refused to run a command");

        Assert.True(session.Inspector.IsUnavailable);
        var entry = Assert.Single(shell.Output, entry => entry.Text.Contains("the server refused to run a command", StringComparison.Ordinal));
        Assert.Equal(LogSeverity.Warning, entry.Severity);
        Assert.Contains(shell.Profiles[0].Name, entry.Text, StringComparison.Ordinal);
    }

    // Wie der Kern: ohne Messung keine Werte.
    private sealed class SilentMonitor : Services.IServerMonitor
    {
        public ServerStats? Current { get; private set; }

        public bool IsUnavailable => false;

        public bool IsActive { get; private set; }

        public event Action? Changed;

        public void SetActive(bool active) => IsActive = active;

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public void Report(ServerStats stats)
        {
            Current = stats;
            Changed?.Invoke();
        }
    }

    [Fact]
    public void Editing_a_profile_keeps_the_system_unless_it_points_to_another_host()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, [], System: "ubuntu");

        Assert.Equal("ubuntu", new ProfileEditorViewModel(profile) { Name = "web" }.ToProfile().System);
        Assert.Null(new ProfileEditorViewModel(profile) { Host = "10.0.1.11" }.ToProfile().System);
    }

    [Fact]
    public void The_core_stores_the_system_with_the_profile()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] = """{"ok":true,"profile":{"id":"p1","name":"web","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true,"system":"debian"}}""";
        using var backend = new Backend.CoreBackend(transport);

        var saved = backend.Save(new ServerProfile(string.Empty, "web", "h", 22, "u", string.Empty, [], System: "debian"));

        Assert.Equal("debian", saved.System);
        Assert.Equal("debian", Assert.Single(transport.Sent("profiles.save")).GetProperty("profile").GetProperty("system").GetString());
    }
}
