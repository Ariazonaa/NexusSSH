namespace NexusSSH.Core.Settings;

/// <summary>
/// Ein Betriebssystem, wie es an Kachel und Kopfzeile eines Servers steht: sein Zeichen
/// (<see cref="ServerSystemIcons"/>) in seiner Farbe.
/// </summary>
/// <param name="Id">Kennung, wie sie im Profil steht.</param>
/// <param name="Hex">Farbe des Zeichens als <c>#rrggbb</c> – hell genug für den schwarzen Grund.</param>
/// <param name="Marker">Woran der Name des Systems zu erkennen ist (klein geschrieben), der erste Treffer gilt.</param>
public sealed record ServerSystem(string Id, string Name, string Hex, params string[] Marker);

/// <summary>Die Betriebssysteme, die die App am Namen erkennt, den die Auslastung meldet.</summary>
public static class ServerSystems
{
    // Die Reihenfolge zählt: Besonderes vor Allgemeinem – „Raspbian“ nennt sich auch Debian, und am Ende steht,
    // was jedes Linux ist.
    private static readonly ServerSystem[] All =
    [
        new("ubuntu", "Ubuntu", "#E95420", "ubuntu"),
        new("raspbian", "Raspberry Pi OS", "#D9336A", "raspbian", "raspberry"),
        new("debian", "Debian", "#F0447A", "debian"),
        new("alpine", "Alpine", "#2A8FBD", "alpine"),
        new("fedora", "Fedora", "#51A2DA", "fedora"),
        new("rocky", "Rocky Linux", "#10B981", "rocky"),
        new("almalinux", "AlmaLinux", "#4C9AFF", "alma"),
        new("centos", "CentOS", "#B565C4", "centos"),
        new("rhel", "Red Hat Enterprise Linux", "#EE0000", "red hat", "rhel"),
        new("amazon", "Amazon Linux", "#FF9900", "amazon linux"),
        new("opensuse", "openSUSE", "#73BA25", "suse"),
        new("arch", "Arch Linux", "#1793D1", "arch linux", "archlinux"),
        new("freebsd", "FreeBSD", "#EB4034", "freebsd"),
        new("openbsd", "OpenBSD", "#F2CA30", "openbsd"),
        new("linux", "Linux", "#E0E0E0", "linux"),
    ];

    /// <summary>Alle Kennungen – für Tests, die zu jeder ein Zeichen erwarten.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(system => system.Id)];

    /// <summary>Das System zu einer Kennung aus dem Profil; <c>null</c>, wenn es keine oder eine unbekannte ist.</summary>
    public static ServerSystem? Find(string? id) => id is null ? null : Array.Find(All, system => system.Id == id);

    /// <summary>
    /// Erkennt das System an dem Namen, den der Server nennt („Ubuntu 24.04.1 LTS“, „Debian GNU/Linux 12“,
    /// „FreeBSD“). <c>null</c>, wenn nichts davon passt – dann bleibt das gewöhnliche Symbol.
    /// </summary>
    public static ServerSystem? Detect(string? name)
    {
        var lower = name?.ToLowerInvariant() ?? string.Empty;
        return lower.Length == 0 ? null : Array.Find(All, system => system.Marker.Any(marker => lower.Contains(marker, StringComparison.Ordinal)));
    }

    /// <summary>So viel von <c>/etc/os-release</c> wird gelesen – die Datei ist ein paar hundert Bytes lang.</summary>
    public const int OsReleaseLength = 8 * 1024;

    /// <summary>
    /// Erkennt das System am Inhalt von <c>/etc/os-release</c>, wie die Messung im Kern: an <c>PRETTY_NAME</c>, sonst
    /// an <c>NAME</c>. Der Inhalt kommt vom Server; er wird nur mit den festen Merkmalen verglichen, nie gezeigt.
    /// </summary>
    public static ServerSystem? FromOsRelease(ReadOnlySpan<byte> content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content[..Math.Min(content.Length, OsReleaseLength)]);
        string? Value(string key) => text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(line => line[(key.Length + 1)..].Trim().Trim('"', '\''))
            .FirstOrDefault(value => value.Length > 0);

        return Detect(Value("PRETTY_NAME")) ?? Detect(Value("NAME"));
    }
}
