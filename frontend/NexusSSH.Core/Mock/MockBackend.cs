using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Mock;

/// <summary>
/// Beispieldaten ohne Netz: vier Profile in zwei Gruppen (Spec §8), je Server ein kleines Dateisystem,
/// schwankende Auslastung und eine nachgespielte Shell. Für Tests und für <c>NexusSSH.exe --demo</c>.
/// </summary>
public sealed class MockBackend : INexusBackend, IProfileStore, IDisposable
{
    private readonly TimeProvider? _timeProvider;
    private readonly MockTransferQueue _transfers;
    private readonly List<ServerProfile> _profiles =
    [
        new("web01", "web01", "10.0.1.10", 22, "ubuntu", "Production", ["web", "nginx"], IsFavorite: true, Color: "red"),
        new("database01", "database01", "10.0.1.20", 22, "admin", "Production", ["db", "postgres"], HasSavedSecret: true, System: "debian", Tunnels: BindableList.Of<ProfileTunnel>([new("t1", TunnelKind.Local, "PostgreSQL", 5432, "127.0.0.1", 5432), new("t2", TunnelKind.Dynamic, string.Empty, 1080, Auto: false)])),
        new("staging", "staging", "10.0.2.10", 2222, "deploy", "Development", ["web"], Color: "green", System: "alpine"),
        new("test01", "test01", "10.0.2.21", 22, "root", "Development", [], System: "freebsd"),
    ];

    // Was die Demo als Inhalt von ~/.ssh/config vorzeigt.
    private static readonly SshConfigHost[] ConfigHosts =
    [
        new("bastion", "bastion.example.org", 22, "ops", null, null, false, false),
        new("build01", "10.0.3.14", 22, "ci", null, "bastion", false, false, Tunnels: 1, Skipped: ["ProxyCommand", "Ciphers"]),
        new("web01", "10.0.1.10", 22, "ubuntu", null, null, false, false),
    ];

    // Das Bild der Beispieldaten (Entwurf A, `--demo`): dieselben Server, Dateien, Werte und Befehle wie im Bild –
    // auf Deutsch wie im Entwurf, in jeder anderen Sprache der Oberfläche englisch (für die Bilder der README).
    private static bool ShowsGerman => L.Language == AppLanguage.German;

    private static ServerProfile[] ShowcaseProfiles
    {
        get
        {
            var (customer, home) = ShowsGerman ? ("Kunde A", "Zuhause") : ("Customer A", "Home");
            return
            [
                new("web01", "web01", "10.0.0.11", 22, "deploy", customer, ["web"], Color: "green", System: "ubuntu"),
                new("db02", "db02", "10.0.0.20", 22, "postgres", customer, ["db"], Color: "blue", System: "debian"),
                new("mail", "mail", "mail.example.org", 22, "root", customer, [], System: "debian"),
                new("nas", "nas", "192.168.1.10", 22, "admin", home, [], Color: "yellow"),
                new("pi-hole", "pi-hole", "192.168.1.2", 22, "pi", home, [], System: "raspbian"),
                // Ein lokales Terminal als gespeichertes Profil: die Kachel „PowerShell – Dieser PC“ im Bild.
                new("powershell", "PowerShell", "pwsh", 0, string.Empty, home, [], Protocol: ServerProfile.Local),
            ];
        }
    }

    /// <summary>Die Tabs, die das Bild der Beispieldaten zeigt, in dieser Reihenfolge; der erste ist aktiv.</summary>
    public static IReadOnlyList<string> ShowcaseTabs { get; } = ["web01", "db02", "nas"];

    private static readonly Snippet[] GermanSnippets =
    [
        new("Fehler-Log nginx", "sudo tail -f /var/log/nginx/error.log", true),
        new("Platz prüfen", "df -h", true),
        new("nginx neu starten", "sudo systemctl restart nginx", true),
        new("Updates holen", "sudo apt update && sudo apt upgrade", true),
        new("Speicher", "free -h", true),
        new("Container", "docker ps", true),
    ];

    private static readonly Snippet[] EnglishSnippets =
    [
        new("nginx error log", "sudo tail -f /var/log/nginx/error.log", true),
        new("Check disk space", "df -h", true),
        new("Restart nginx", "sudo systemctl restart nginx", true),
        new("Install updates", "sudo apt update && sudo apt upgrade", true),
        new("Memory", "free -h", true),
        new("Containers", "docker ps", true),
    ];

    /// <summary>Die gespeicherten Befehle aus dem Bild, in der Sprache der Oberfläche.</summary>
    public static IReadOnlyList<Snippet> ShowcaseSnippets => ShowsGerman ? GermanSnippets : EnglishSnippets;

    /// <summary>Ob das die Befehle aus dem Bild sind – in welcher Sprache auch immer (sie bleiben nicht gespeichert).</summary>
    public static bool IsShowcase(IEnumerable<Snippet> snippets)
    {
        var list = snippets.ToList();
        return list.SequenceEqual(GermanSnippets) || list.SequenceEqual(EnglishSnippets);
    }

    private static string ShowcaseTerminal => ShowsGerman ? GermanTerminal : EnglishTerminal;

    private const string EnglishTerminal = """
        $ ls -la
        drwxr-xr-x  6 www-data www-data 4096 Oct 24 18:02 .
        drwxr-xr-x  3 root     root     4096 Mar  2  2026 ..
        drwxr-xr-x  4 www-data www-data 4096 Oct 20 09:14 assets
        drwxr-xr-x  2 www-data www-data 4096 Oct 11 21:40 config
        -rw-r--r--  1 www-data www-data 4302 Oct 24 18:02 index.php
        -rw-r--r--  1 www-data www-data   68 Jun  9 11:20 robots.txt
        drwxr-xr-x  9 www-data www-data 4096 Oct 25 07:55 uploads
        -rw-r-----  1 www-data www-data 3170 Oct 11 21:41 wp-config.php
        $ df -h /
        Filesystem      Size  Used Avail Use% Mounted on
        /dev/sda1        80G   51G   29G  64% /
        """;

    // Was das Terminal von web01 im Bild zeigt; eine Zeile mit $ ist ein Befehl hinter dem Prompt.
    private const string GermanTerminal = """
        $ ls -la
        drwxr-xr-x  6 www-data www-data 4096 Okt 24 18:02 .
        drwxr-xr-x  3 root     root     4096 Mär  2  2026 ..
        drwxr-xr-x  4 www-data www-data 4096 Okt 20 09:14 assets
        drwxr-xr-x  2 www-data www-data 4096 Okt 11 21:40 config
        -rw-r--r--  1 www-data www-data 4302 Okt 24 18:02 index.php
        -rw-r--r--  1 www-data www-data   68 Jun  9 11:20 robots.txt
        drwxr-xr-x  9 www-data www-data 4096 Okt 25 07:55 uploads
        -rw-r-----  1 www-data www-data 3170 Okt 11 21:41 wp-config.php
        $ df -h /
        Dateisystem    Größe Benutzt Verf. Verw% Eingehängt auf
        /dev/sda1        80G     51G   29G   64% /
        """;

    private readonly bool _showcase;
    private int _lastId;

    /// <param name="timeProvider">
    /// Treibt den Timer der Auslastung. <c>null</c> = nichts bewegt sich von selbst (Tests). Die Transfers bewegen
    /// sich nie von selbst (<see cref="MockTransferQueue.Advance"/>).
    /// </param>
    /// <param name="showcase">
    /// Statt der Beispieldaten für Tests und Prüfläufe die aus dem Bild des Entwurfs: andere Server, die Dateien in
    /// <c>/var/www/html</c>, feste Werte, nas verbindet noch. Für <c>--demo</c>.
    /// </param>
    public MockBackend(TimeProvider? timeProvider = null, bool showcase = false)
    {
        _timeProvider = timeProvider;
        _showcase = showcase;

        // Die Warteschlange steht still: Im Bild bleibt „Transfers 2“, wie es ist, und ein Prüflauf
        // (tools/ui-test.ps1) findet den wartenden Transfer noch, wenn er an der Reihe ist – rückte sie vor, wäre er
        // auf einem langsamen Rechner nach gut einer Minute fertig und ließe sich nicht mehr anhalten.
        _transfers = new MockTransferQueue(null, showcase);
        if (showcase)
        {
            _profiles.Clear();
            _profiles.AddRange(ShowcaseProfiles);
        }
    }

    public event Action? Changed;

    /// <summary>Hinweise des „Kerns“ – die Beispieldaten melden nur im Bild einen (nas verbindet neu).</summary>
    public event Action<string>? Warning;

    /// <summary>Das zuletzt eingestellte Display – die Beispieldaten leiten nichts weiter.</summary>
    public int X11Display { get; private set; }

    public void SetX11Display(int display) => X11Display = display;

    public (int Seconds, int Missed)? KeepAlive { get; private set; }

    public void SetKeepAlive(int seconds, int missed) => KeepAlive = (seconds, missed);

    /// <summary>An diesem Port scheitert in den Beispieldaten jeder Tunnel – so lässt sich der Fehlerfall zeigen.</summary>
    public const int TakenTunnelPort = 9;

    private readonly HashSet<(string Profile, string Tunnel)> _tunnels = [];

    public event Action<TunnelNotice>? TunnelChanged;

    /// <summary>Die Beispieldaten lauschen nirgends: Ein Tunnel „steht“, sobald er es soll – außer an <see cref="TakenTunnelPort"/>.</summary>
    public void SetTunnel(IServerConnection connection, string tunnelId, bool on)
    {
        if (!_connections.TryGetValue(connection, out var profileId) || connection.Status.State != ConnectionState.Connected)
        {
            return;
        }

        var tunnel = _profiles.FirstOrDefault(profile => profile.Id == profileId)?.Tunnels?.FirstOrDefault(candidate => candidate.Id == tunnelId);
        if (!on)
        {
            if (_tunnels.Remove((profileId, tunnelId)))
            {
                TunnelChanged?.Invoke(new TunnelNotice(profileId, tunnelId, TunnelState.Stopped));
            }
        }
        else if (tunnel is not null && !_tunnels.Contains((profileId, tunnelId)))
        {
            if (tunnel.ListenPort == TakenTunnelPort)
            {
                TunnelChanged?.Invoke(new TunnelNotice(profileId, tunnelId, TunnelState.Failed, $"port {TakenTunnelPort} is already in use on this computer"));
            }
            else
            {
                _tunnels.Add((profileId, tunnelId));
                TunnelChanged?.Invoke(new TunnelNotice(profileId, tunnelId, TunnelState.Listening));
            }
        }
    }

    private readonly Dictionary<IServerConnection, string> _connections = [];

    // Wie im Kern: Mit der Verbindung starten die Tunnel, die es sollen, und mit ihr enden alle.
    private void WatchTunnels(IServerConnection connection, string profileId)
    {
        _connections[connection] = profileId;
        connection.StatusChanged += status =>
        {
            if (status.State == ConnectionState.Connected)
            {
                foreach (var tunnel in _profiles.FirstOrDefault(profile => profile.Id == profileId)?.Tunnels ?? [])
                {
                    if (tunnel.Auto)
                    {
                        SetTunnel(connection, tunnel.Id, on: true);
                    }
                }
            }
            else if (status.State == ConnectionState.Closed && !_connections.Any(other => other.Value == profileId && !ReferenceEquals(other.Key, connection) && other.Key.Status.State == ConnectionState.Connected))
            {
                foreach (var running in _tunnels.Where(running => running.Profile == profileId).ToList())
                {
                    _tunnels.Remove(running);
                    TunnelChanged?.Invoke(new TunnelNotice(profileId, running.Tunnel, TunnelState.Stopped));
                }
            }
        };
    }

    /// <summary>
    /// Der Bericht, den der nächste Verbindungstest liefert; ohne Angabe ein gelungener, wie ihn ein gewöhnlicher
    /// Server ergäbe. Die Beispieldaten gehen dafür nicht ins Netz.
    /// </summary>
    public ProbeReport? NextProbe { get; set; }

    /// <summary>Die Profile, für die ein Test verlangt wurde.</summary>
    public List<ServerProfile> Probed { get; } = [];

    /// <summary>Die Shells, die die Beispieldaten für ein lokales Terminal nennen – ein Test darf sie ersetzen.</summary>
    public IReadOnlyList<LocalShell> LocalShells { get; set; } =
    [
        new("pwsh", "PowerShell 7"),
        new("cmd", "Command Prompt"),
        new("wsl:Ubuntu", "WSL: Ubuntu"),
    ];

    public Task<IReadOnlyList<LocalShell>> LocalShellsAsync(CancellationToken cancellation = default) => Task.FromResult(LocalShells);

    public Task<ProbeReport> ProbeAsync(ServerProfile profile, CancellationToken cancellation = default)
    {
        Probed.Add(profile);
        return Task.FromResult(NextProbe ?? new ProbeReport(
            "methods",
            ["203.0.113.10"],
            "203.0.113.10",
            ConnectMs: 12,
            Software: "SSH-2.0-OpenSSH_9.6",
            KeyAlgorithm: "ssh-ed25519",
            Fingerprint: "SHA256:beispielbeispielbeispielbeispielbeispielbei",
            KeyState: ProbeReport.Trusted,
            Methods: ["publickey", "password"]));
    }

    /// <summary>Ob entsperrte Schlüssel gemerkt würden – die Beispieldaten kennen keine Schlüssel.</summary>
    public bool RemembersUnlockedKeys { get; private set; } = true;

    /// <summary>Wie oft „vergessen“ verlangt wurde.</summary>
    public int ForgotUnlockedKeys { get; private set; }

    public void RememberUnlockedKeys(bool remember) => RemembersUnlockedKeys = remember;

    public void ForgetUnlockedKeys() => ForgotUnlockedKeys++;

    public IProfileStore Profiles => this;

    public ITransferQueue Transfers => _transfers;

    public IReadOnlyList<ServerProfile> GetProfiles() => [.. _profiles.Where(profile => !profile.IsTransient)];

    /// <summary>Auch die vorläufigen Profile (Schnellverbinden), die in keiner Liste stehen – für Tests.</summary>
    public IReadOnlyList<ServerProfile> AllProfiles => [.. _profiles];

    public ServerProfile Save(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            throw new BackendException("invalid_profile", "host");
        }

        // Wie der Kern: Ein lokales Terminal nennt eine Kennung (nie einen Pfad), und sein Startordner ist absolut.
        if (profile.IsLocal)
        {
            if (!LocalShellNames.IsId(profile.Host))
            {
                throw new BackendException("invalid_profile", "host");
            }

            if (profile.LocalDirectory is not null && !LocalShellNames.IsFolder(profile.LocalDirectory))
            {
                throw new BackendException("invalid_profile", "local_dir");
            }
        }

        var saved = profile.Id.Length == 0 ? profile with { Id = $"mock-{++_lastId}" } : profile;

        // Wie der Kern: Ein neuer Tunnel bekommt seine Kennung beim Speichern.
        if (saved.Tunnels is { Count: > 0 } tunnels && tunnels.Any(tunnel => tunnel.Id.Length == 0))
        {
            var taken = tunnels.Select(tunnel => tunnel.Id).ToHashSet();
            saved = saved with
            {
                Tunnels = BindableList.Of(tunnels.Select(tunnel =>
                {
                    if (tunnel.Id.Length > 0)
                    {
                        return tunnel;
                    }

                    var id = Enumerable.Range(1, ProfileTunnel.MaxPerProfile + 1).Select(number => $"t{number}").First(taken.Add);
                    return tunnel with { Id = id };
                })),
            };
        }
        var index = _profiles.FindIndex(existing => existing.Id == saved.Id);
        if (index < 0)
        {
            _profiles.Add(saved);
        }
        else
        {
            _profiles[index] = saved;
        }

        Changed?.Invoke();
        return saved;
    }

    public void Delete(string id)
    {
        if (_profiles.RemoveAll(profile => profile.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    // Zwei Beispiele, damit die Liste in den Einstellungen etwas zeigt.
    private readonly List<KnownHost> _knownHosts =
    [
        new("10.0.1.10", "ssh-ed25519", "SHA256:1ZgT7dCm0kKqv3s4bqk0mWcVqk2m9o0y3o6m8m0k1xQ"),
        new("[10.0.2.10]:2222", "ssh-ed25519", "SHA256:Qm8k0m1x1ZgT7dCm0kKqv3s4bqk0mWcVqk2m9o0y3o6"),
    ];

    public IReadOnlyList<KnownHost> KnownHosts() => [.. _knownHosts];

    public bool ForgetHost(string name, string? authority = null)
        => _knownHosts.RemoveAll(host => host.Name == name && (authority is null ? !host.IsAuthority : host.IsAuthority && host.Fingerprint == authority)) > 0;

    public KnownHost TrustAuthority(string patterns, string key)
    {
        if (!HostAuthority.IsPatterns(patterns))
        {
            throw new BackendException("invalid_patterns", "these are not host name patterns");
        }

        if (!HostAuthority.LooksLikeKey(key))
        {
            throw new BackendException("invalid_key", "this is not a public key");
        }

        var fields = key.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var added = new KnownHost(patterns.Trim(), fields[0], "SHA256:" + fields[1][..Math.Min(43, fields[1].Length)], IsAuthority: true);
        if (!_knownHosts.Contains(added))
        {
            _knownHosts.Add(added);
        }

        return added;
    }

    public SshConfigListing ReadSshConfig()
        => new("~/.ssh/config", [.. ConfigHosts.Select(host => host with { Exists = Find(host.Alias) is not null })], ["exec"]);

    public int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false)
    {
        var before = _profiles.Count;
        var marked = 0;
        foreach (var alias in aliases)
        {
            Import(alias);

            // Verknüpft wird nur, was genannt ist – auch ein Profil, das es schon gab; ein mitgekommener Sprung-Server nicht.
            var index = _profiles.FindIndex(profile => profile.Name == alias);
            if (linked && index >= 0 && _profiles[index].SshAlias != alias)
            {
                _profiles[index] = _profiles[index] with { SshAlias = alias };
                marked++;
            }
        }

        if (_profiles.Count > before || marked > 0)
        {
            Changed?.Invoke();
        }

        return _profiles.Count - before;
    }

    // Drei Beispiele, damit die Seite „Schlüssel“ etwas zeigt; daneben die Passphrase jedes verschlüsselten.
    private readonly List<KeyFile> _keyFiles =
    [
        new("~/.ssh/id_ed25519", "id_ed25519", KeyFormat.OpenSsh, "ssh-ed25519", 256, "SHA256:Zk3m8Qv0x1ZgT7dCm0kKqv3s4bqk0mWcVqk2m9o0y3o", "me@workstation", false, "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMockOwnKey me@workstation"),
        new("~/.ssh/id_rsa_work", "id_rsa_work", KeyFormat.OpenSsh, "ssh-rsa", 4096, "SHA256:0kKqv3s4bqk0mWcVqk2m9o0y3o6m8m0k1xQ1ZgT7dCm", "work", true, "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAACAQMockWorkKey work"),
        new("~/.ssh/legacy.ppk", "legacy.ppk", KeyFormat.Ppk, "ecdsa-sha2-nistp256", 256, "SHA256:m9o0y3o6m8m0k1xQ1ZgT7dCm0kKqv3s4bqk0mWcVqk2", "putty", false, "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYMockOldKey putty"),
    ];

    private readonly Dictionary<string, string> _keyPassphrases = new(StringComparer.Ordinal) { ["~/.ssh/id_rsa_work"] = "demo" };

    public IReadOnlyList<KeyFile> KeyFiles(IReadOnlyList<string> otherPaths) => [.. _keyFiles];

    public void ChangeKeyPassphrase(string path, string? passphrase, string? newPassphrase)
    {
        var at = Unlocked(path, passphrase);
        if (string.IsNullOrEmpty(newPassphrase))
        {
            _keyPassphrases.Remove(path);
        }
        else
        {
            _keyPassphrases[path] = newPassphrase;
        }

        _keyFiles[at] = _keyFiles[at] with { IsEncrypted = !string.IsNullOrEmpty(newPassphrase) };
    }

    public string ConvertKey(string path, string? passphrase, KeyFormat format, string name)
    {
        var key = _keyFiles[Unlocked(path, passphrase)];
        var target = $"~/.ssh/{name}";
        if (key.Format == format)
        {
            throw new BackendException("same_format", "the key already has this format");
        }

        if (!NexusSSH.Core.ViewModels.KeyRequest.IsValidName(name))
        {
            throw new BackendException("invalid_name", $"{name} is not a name for a key file");
        }

        if (_keyFiles.Any(other => other.Path == target))
        {
            throw new BackendException("exists", $"{target} already exists");
        }

        _keyFiles.Add(key with { Path = target, Name = name, Format = format });
        if (_keyPassphrases.TryGetValue(path, out var kept))
        {
            _keyPassphrases[target] = kept;
        }

        return target;
    }

    // Die Stelle des Schlüssels in der Liste – wenn die Passphrase zu ihm passt.
    private int Unlocked(string path, string? passphrase)
    {
        var at = _keyFiles.FindIndex(key => key.Path == path);
        if (at < 0)
        {
            throw new BackendException("unreadable", $"{path} is not a key file");
        }

        if (_keyPassphrases.TryGetValue(path, out var expected) && expected != (passphrase ?? string.Empty))
        {
            throw new BackendException("wrong_passphrase", "the passphrase does not open this key");
        }

        return at;
    }

    /// <summary>Die Namen der „angelegten“ Schlüssel. Für Tests.</summary>
    public List<string> GeneratedKeys { get; } = [];

    public GeneratedKey GenerateKey(string name, string? passphrase, string comment)
    {
        if (GeneratedKeys.Contains(name))
        {
            throw new BackendException("exists", $"{name} already exists");
        }

        GeneratedKeys.Add(name);
        return new GeneratedKey($"~/.ssh/{name}", $"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMockKeyFor{name} {comment}");
    }

    public void ForgetSecrets(string id)
    {
        var index = _profiles.FindIndex(profile => profile.Id == id);
        if (index >= 0 && _profiles[index].HasSavedSecret)
        {
            _profiles[index] = _profiles[index] with { HasSavedSecret = false };
            Changed?.Invoke();
        }
    }

    // Die Beispieldaten kennen keine Anmeldung; es gibt also nichts mitzubenutzen.
    public IServerConnection Connect(ServerProfile profile, IServerConnection? share = null)
    {
        if (_showcase && ShowcaseConnection(profile) is { } shown)
        {
            return shown;
        }

        var stats = StatsFor(profile);
        var files = FilesFor(profile, _transfers);
        var monitor = new MockServerMonitor(stats, StableSeed(profile.Id), _timeProvider, steady: _showcase);
        var connection = new Connection(new MockTerminalSession(profile, stats, files), files, monitor);
        WatchTunnels(connection, profile.Id);
        return connection;
    }

    // Was im Bild anders ist: web01 mit seinen Dateien, Werten und dem Terminal von dort, nas noch im Aufbau.
    private Connection? ShowcaseConnection(ServerProfile profile)
    {
        switch (profile.Id)
        {
            case "web01":
                var stats = ShowcaseStats();
                var files = new MockRemoteFileSystem("/var/www/html", _transfers, profile.Name)
                    .Folder("/var/www/html/assets", hoursAgo: 196)
                    .Folder("/var/www/html/config", hoursAgo: 410)
                    .Folder("/var/www/html/uploads", hoursAgo: 75)
                    .File("/var/www/html/index.php", 4_302, hoursAgo: 99)
                    .File("/var/www/html/robots.txt", 68, hoursAgo: 3_400)
                    .File("/var/www/html/wp-config.php", 3_170, hoursAgo: 410);
                var monitor = new MockServerMonitor(stats, StableSeed(profile.Id), _timeProvider, steady: true);
                return new Connection(new MockTerminalSession(profile, stats, files, ShowcaseTerminal, "/var/www/html"), files, monitor);

            case "nas":
                // nas versucht es gerade neu: Die Verbindung kommt nicht zustande, und ein Problem steht da.
                Warning?.Invoke(L.Get("DemoNasRetrying"));
                var nasStats = StatsFor(profile);
                var nasFiles = FilesFor(profile, _transfers);
                return new Connection(new MockTerminalSession(profile, nasStats, nasFiles), nasFiles, new MockServerMonitor(nasStats, StableSeed(profile.Id)), stuck: true);

            default:
                return null;
        }
    }

    // Die Werte der Zeile unten im Bild: CPU 12 %, RAM 2,1 / 8 GB, Netz ↓ 1,2 ↑ 0,3 MB/s, Platte 64 %, 12 Tage.
    private static ServerStats ShowcaseStats()
    {
        const long Gb = 1024L * 1024 * 1024;
        var stats = Stats(12, 26.25, 63.75, 8, 80, "Ubuntu 24.04.1 LTS", "6.8.0-45-generic", TimeSpan.FromDays(12.3));
        return stats with
        {
            MemoryUsed = (long)(2.1 * Gb),
            DiskUsed = 51 * Gb,
            NetReceived = 1_258_291,
            NetSent = 314_573,
        };
    }

    public void Dispose() => _transfers.Dispose();

    private ServerProfile? Find(string name)
        => _profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Legt das Profil zu einem Eintrag an, samt seinem Sprung-Server, und liefert seine Kennung.
    private string? Import(string alias)
    {
        if (Find(alias) is { } existing)
        {
            return existing.Id;
        }

        if (ConfigHosts.FirstOrDefault(host => host.Alias == alias) is not { } host)
        {
            return null;
        }

        var jump = host.Jump is null ? null : Import(host.Jump);
        var profile = new ServerProfile(
            $"mock-{++_lastId}",
            host.Alias,
            host.Host,
            host.Port,
            host.User,
            string.Empty,
            [],
            JumpId: jump);
        _profiles.Add(profile);
        return profile.Id;
    }

    private static ServerStats StatsFor(ServerProfile profile) => profile.Id switch
    {
        "web01" => Stats(34, 42, 28, 8, 80, "Ubuntu 24.04.1 LTS", "6.8.0-45-generic", TimeSpan.FromDays(32)),
        "database01" => Stats(12, 71, 63, 32, 500, "Debian GNU/Linux 12", "6.1.0-25-amd64", TimeSpan.FromDays(118)),
        "staging" => Stats(8, 35, 47, 4, 40, "Ubuntu 22.04.5 LTS", "5.15.0-122-generic", TimeSpan.FromHours(19)),

        // Der kleine Rechner im Bild der Beispieldaten nennt sich so, wie es Raspberry Pi OS tut.
        "pi-hole" => Stats(5, 31, 22, 1, 32, "Raspbian GNU/Linux 12 (bookworm)", "6.6.51+rpt-rpi-v8", TimeSpan.FromDays(41)),
        _ => Stats(3, 22, 15, 2, 20, "Debian GNU/Linux 12", "6.1.0-25-amd64", TimeSpan.FromDays(4)),
    };

    private static ServerStats Stats(double cpu, double memory, double disk, int memoryGb, int diskGb, string os, string kernel, TimeSpan uptime)
    {
        const long Gb = 1024L * 1024 * 1024;
        return new ServerStats(
            true,
            cpu,
            memory,
            disk,
            os,
            kernel,
            uptime,
            (long)(memoryGb * Gb * memory / 100),
            memoryGb * Gb,
            (long)(diskGb * Gb * disk / 100),
            diskGb * Gb,
            Load: string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cpu / 50:0.00} {cpu / 60:0.00} {cpu / 70:0.00}"),
            NetReceived: (long)(cpu * 4_000),
            NetSent: (long)(cpu * 1_500),
            Interfaces:
            [
                new NetInterface("eth0", (long)(cpu * 3_500), (long)(cpu * 1_400), "192.0.2.10, 2001:db8::10"),
                new NetInterface("wg0", (long)(cpu * 500), (long)(cpu * 100), "10.66.0.2"),
            ],
            Io: new DiskIo((long)(cpu * 60_000), (long)(cpu * 25_000), Math.Min(cpu * 0.8, 100)),
            IoDevices:
            [
                new DiskIoDevice("sda", new DiskIo((long)(cpu * 45_000), (long)(cpu * 20_000), Math.Min(cpu * 0.8, 100))),
                new DiskIoDevice("sdb", new DiskIo((long)(cpu * 15_000), (long)(cpu * 5_000), Math.Min(cpu * 0.3, 100))),
            ],
            Cores: [Math.Min(cpu * 1.6, 100), cpu * 0.4, cpu * 1.2, cpu * 0.8],
            Split: new CpuSplit(cpu * 0.7, cpu * 0.25, cpu > 30 ? 2 : 0, 0),
            Disks:
            [
                new DiskUsage("/", "/dev/sda1", (long)(diskGb * Gb * disk / 100), diskGb * Gb, 250_000, 6_400_000),
                new DiskUsage("/var/lib/docker", "/dev/sdb1", 188 * Gb, 200 * Gb, 990_000, 13_000_000),
                new DiskUsage("/srv/backup", "/dev/sdc1", 96 * Gb, 100 * Gb),
            ],
            Info: new ServerInfo("Dell Inc. PowerEdge R640", "Intel(R) Xeon(R) Gold 6140 CPU @ 2.30GHz", 4, memoryGb > 16 ? ServerInfo.NoVirtualization : "kvm"),
            Clock: new ServerClock(DateTimeOffset.UtcNow.AddSeconds(memoryGb > 16 ? 95 : 0), "UTC", TimeSpan.FromSeconds(memoryGb > 16 ? 95 : 0)));
    }

    private static MockRemoteFileSystem FilesFor(ServerProfile profile, MockTransferQueue transfers)
    {
        var home = profile.User == "root" ? "/root" : $"/home/{profile.User}";
        var start = profile.Tags.Contains("web") ? "/var/www" : home;
        var files = new MockRemoteFileSystem(start, transfers, profile.Name)
            .File($"{home}/.bashrc", 3_771, hoursAgo: 2_100)
            .File($"{home}/.profile", 807, hoursAgo: 2_100)
            .File($"{home}/notes.txt", 1_240, hoursAgo: 30)
            .File($"{home}/scripts/deploy.sh", 2_816, hoursAgo: 96)
            // Links für --demo: auf einen Ordner, auf eine Datei, ins Leere, mit sehr langem Ziel, ohne bekanntes Ziel (DSM)
            .Link($"{home}/skripte", "scripts")
            .Link($"{home}/notizen", $"{home}/notes.txt")
            .Link($"{home}/verwaist", "gibt-es-nicht")
            .Link($"{home}/langes-ziel", "/srv/" + string.Concat(Enumerable.Repeat("sehr-langer-ordner/", 210)) + "ende")
            .LinkUnlisted($"{home}/unbekannt", $"{home}/.profile")
            .File("/etc/hosts", 221, hoursAgo: 4_000)
            .File("/etc/ssh/sshd_config", 3_254, hoursAgo: 900)
            .File("/var/log/syslog", 1_482_301, hoursAgo: 0);

        if (profile.Tags.Contains("web"))
        {
            // Beispiel aus Spec §4
            files
                .File("/var/www/html/index.html", 4_302, hoursAgo: 50)
                .File("/var/www/html/favicon.ico", 15_086, hoursAgo: 700)
                .File("/var/www/html/css/site.css", 18_944, hoursAgo: 50)
                .File("/var/www/assets/logo.svg", 2_611, hoursAgo: 700)
                .File("/var/www/assets/hero.webp", 248_320, hoursAgo: 340)
                .File("/var/www/nginx.conf", 1_834, hoursAgo: 26)
                .File("/var/www/docker-compose.yml", 912, hoursAgo: 170)
                .File("/var/www/app.log", 6_291_456, hoursAgo: 0)
                .File($"{home}/backup.tar.gz", 734_003_200, hoursAgo: 12);
        }

        if (profile.Tags.Contains("db"))
        {
            files
                .File($"{home}/dumps/database.sql", 96_468_992, hoursAgo: 6)
                .File($"{home}/dumps/database-old.sql.gz", 21_233_664, hoursAgo: 174)
                .File("/etc/postgresql/16/main/postgresql.conf", 29_870, hoursAgo: 1_200)
                .File("/etc/postgresql/16/main/pg_hba.conf", 5_002, hoursAgo: 1_200);
        }

        return files;
    }

    // string.GetHashCode ist je Prozess zufällig; die Schwankung soll aber bei jedem Start gleich aussehen.
    private static int StableSeed(string text) => text.Aggregate(17, (seed, c) => unchecked((seed * 31) + c));

    /// <summary>Die „Verbindung“ steht sofort, sobald das Terminal startet; Rückfragen gibt es keine.</summary>
    private sealed class Connection(MockTerminalSession shell, MockRemoteFileSystem files, MockServerMonitor monitor, bool stuck = false)
        : IServerConnection, ITerminalSession
    {
        public event Action<ConnectionStatus>? StatusChanged;

        public event Action<ConnectionPrompt>? PromptRequested
        {
            add { }
            remove { }
        }

        public event Action<string>? Banner
        {
            add { }
            remove { }
        }

        public event Action<ReadOnlyMemory<byte>>? Output
        {
            add => shell.Output += value;
            remove => shell.Output -= value;
        }

        public ITerminalSession Terminal => this;

        public IRemoteFileSystem? Files => files;

        public IServerMonitor? Monitor => monitor;

        public ConnectionStatus Status { get; private set; } = new(ConnectionState.Idle);

        public void Start()
        {
            if (Status.State != ConnectionState.Idle)
            {
                return;
            }

            // Ein Server, der im Bild noch verbindet, kommt nie weiter.
            if (stuck)
            {
                Status = new ConnectionStatus(ConnectionState.Connecting);
                StatusChanged?.Invoke(Status);
                return;
            }

            Status = new ConnectionStatus(ConnectionState.Connected);
            // Erst die Shell, dann die Meldung: Wer auf „verbunden“ hin gleich tippt (die Zeile für einen Container),
            // erreichte sonst eine Shell, die noch nichts annimmt.
            shell.Start();
            StatusChanged?.Invoke(Status);
        }

        public void Write(ReadOnlyMemory<byte> input) => shell.Write(input);

        public void Resize(int columns, int rows) => shell.Resize(columns, rows);

        public void SetPaused(bool paused) => shell.SetPaused(paused);

        public void Dispose() => monitor.Dispose();
    }
}
