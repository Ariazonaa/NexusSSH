using System.Globalization;
using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Exchange;

/// <summary>Eine gespeicherte Sitzung eines anderen Programms, so weit sie sich als Profil übernehmen lässt.</summary>
/// <param name="Source">Name des Programms, für die Anzeige.</param>
/// <param name="Group">Ordner der Sitzung dort; wird die Gruppe des Profils.</param>
/// <param name="Protocol"><c>telnet</c> für eine Telnet-Sitzung (dann ohne Benutzer und Schlüssel); sonst SSH.</param>
public sealed record ForeignSession(string Source, string Name, string Host, int Port, string User, string Group, string? KeyPath = null, string? Protocol = null)
{
    public bool IsTelnet => Protocol == ServerProfile.Telnet;

    public string Address => ToProfile().Address;

    /// <summary>Das Profil zu der Sitzung, noch nicht gespeichert – geprüft wird es beim Speichern wie ein eingetipptes.</summary>
    public ServerProfile ToProfile()
        // Die Sitzung stammt aus einer fremden Datei: Ein Schlüssel im Netz wird nicht übernommen (DataExchange.IsNetworkPath).
        => new(string.Empty, Name, Host, Port, User, Group, [], KeyPath: KeyPath is { Length: > 0 } key && !DataExchange.IsNetworkPath(key.Trim()) ? KeyPath : null, Protocol: Protocol);
}

/// <summary>Ein Schlüssel der Registry mit seinen Werten als Text – gelesen von der App, gedeutet hier.</summary>
public sealed record StoredSession(string Key, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Liest die gespeicherten Sitzungen von PuTTY, WinSCP und MobaXterm. Alles hier ist fremde Eingabe und wird nur
/// gelesen; übernommen wird, was SSH oder Telnet ist und einen Host hat. Passwörter, die diese Programme speichern, bleiben
/// dort. Der Pfad einer Schlüsseldatei kommt mit – auch im Format von PuTTY (<c>.ppk</c>), das der Kern liest.
/// </summary>
public static class ForeignSessions
{
    public const string Putty = "PuTTY";
    public const string WinScp = "WinSCP";
    public const string MobaXterm = "MobaXterm";

    /// <summary>Mehr Sitzungen als das übernimmt ein Import nicht.</summary>
    public const int MaxSessions = 2000;

    /// <summary>
    /// PuTTY: ein Schlüssel je Sitzung unter <c>Software\SimonTatham\PuTTY\Sessions</c>, der Name URL-kodiert.
    /// </summary>
    /// <param name="fallbackUser">Benutzer, wenn die Sitzung keinen nennt (PuTTY fragt dann beim Verbinden).</param>
    public static IReadOnlyList<ForeignSession> FromPutty(IEnumerable<StoredSession> keys, string fallbackUser)
        => Collect(keys.Select(key =>
        {
            var host = Value(key, "HostName");
            var protocol = Value(key, "Protocol", "ssh").ToLowerInvariant();
            if (protocol == "telnet")
            {
                // Telnet kennt keine Anmeldung durch die App: nur Host und Port (üblich 23), kein Benutzer, kein Schlüssel.
                return Telnet(Putty, Decode(key.Key), host[(host.LastIndexOf('@') + 1)..], Value(key, "PortNumber"), string.Empty);
            }

            if (protocol != "ssh")
            {
                return null;
            }

            // „benutzer@host“ im Feld für den Host ist bei PuTTY üblich.
            var at = host.LastIndexOf('@');
            var user = Value(key, "UserName") is { Length: > 0 } named ? named : at > 0 ? host[..at] : fallbackUser;
            return Session(Putty, Decode(key.Key), host[(at + 1)..], Value(key, "PortNumber"), user, string.Empty, Value(key, "PublicKeyFile"));
        }));

    /// <summary>
    /// WinSCP: ein Schlüssel je Sitzung unter <c>Software\Martin Prikryl\WinSCP 2\Sessions</c>; Name (samt Ordner,
    /// mit <c>/</c> getrennt), Host und Dateipfade sind URL-kodiert. Übernommen wird, was über SSH läuft (SFTP, SCP).
    /// </summary>
    public static IReadOnlyList<ForeignSession> FromWinScp(IEnumerable<StoredSession> keys, string fallbackUser)
        => Collect(keys.Select(key =>
        {
            // FSProtocol: 0 = SCP, 1 und 2 = SFTP, darüber FTP, WebDAV, S3. Ohne Angabe gilt SFTP.
            if (Value(key, "FSProtocol", "1") is not ("0" or "1" or "2"))
            {
                return null;
            }

            var path = Decode(key.Key);
            var slash = path.LastIndexOf('/');
            var user = Decode(Value(key, "UserName")) is { Length: > 0 } named ? named : fallbackUser;
            return Session(WinScp, path[(slash + 1)..], Decode(Value(key, "HostName")), Value(key, "PortNumber"), user, slash > 0 ? path[..slash] : string.Empty, Decode(Value(key, "PublicKeyFile")));
        }));

    /// <summary>
    /// MobaXterm: <c>MobaXterm.ini</c> oder eine exportierte <c>.mxtsessions</c>-Datei. Die Sitzungen stehen in den
    /// Abschnitten <c>[Bookmarks…]</c> als <c>Name=#Symbol#Art%Host%Port%Benutzer%…</c>; Art 0 ist SSH.
    /// </summary>
    public static IReadOnlyList<ForeignSession> FromMobaXterm(string text, string fallbackUser)
    {
        var sessions = new List<ForeignSession?>();
        var (inBookmarks, group) = (false, string.Empty);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('['))
            {
                (inBookmarks, group) = (line.StartsWith("[Bookmarks", StringComparison.OrdinalIgnoreCase), string.Empty);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inBookmarks || equals <= 0)
            {
                continue;
            }

            var (name, value) = (line[..equals].Trim(), line[(equals + 1)..].Trim());
            if (name.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                group = value.Replace("\\", " / ", StringComparison.Ordinal);
                continue;
            }

            // „#109#0%host%22%root%…“: zwischen den ersten beiden # das Symbol, danach die Felder.
            var fields = value.StartsWith('#') && value.IndexOf('#', 1) is > 0 and var second ? value[(second + 1)..].Split('%') : [];
            // Art 1 ist Telnet: Host und Port stehen an denselben Stellen.
            if (fields is ["1", var device, var devicePort, ..])
            {
                sessions.Add(Telnet(MobaXterm, name, device, devicePort, group));
                continue;
            }

            if (fields.Length < 4 || fields[0] != "0")
            {
                continue;
            }

            // Ohne festen Benutzer steht dort nichts oder ein Platzhalter in Klammern.
            var user = fields[3].Length > 0 && fields[3].IndexOfAny(['<', '[']) < 0 ? fields[3] : fallbackUser;
            sessions.Add(Session(MobaXterm, name, fields[1], fields[2], user, group, key: null));
        }

        return Collect(sessions);
    }

    /// <summary>Den Text einer Datei von MobaXterm: UTF-8, wenn sie das ist, sonst die westeuropäische Kodierung von Windows.</summary>
    public static string ReadText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Das Profil zu einer Sitzung, noch nicht gespeichert – geprüft wird es beim Speichern wie ein eingetipptes.</summary>
    public static ServerProfile ToProfile(ForeignSession session) => session.ToProfile();

    /// <summary>Ob es zu der Sitzung schon ein Profil gibt: derselbe Name oder dasselbe Ziel.</summary>
    public static bool Exists(ForeignSession session, IEnumerable<ServerProfile> profiles)
        => profiles.Any(profile => profile.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase)
            || (profile.Host.Equals(session.Host, StringComparison.OrdinalIgnoreCase) && profile.Port == session.Port && profile.User == session.User && profile.IsTelnet == session.IsTelnet));

    internal static ForeignSession? Session(string source, string name, string host, string port, string user, string group, string? key)
    {
        (name, host, user) = (name.Trim(), host.Trim(), user.Trim());
        if (name.Length == 0 || host.Length == 0 || user.Length == 0)
        {
            return null;
        }

        var number = int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 22;
        // Auch ein Schlüssel im Format von PuTTY (.ppk): Den liest der Kern wie jeden anderen.
        var keyPath = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        return new ForeignSession(source, name, host, number, user, group.Trim(), keyPath);
    }

    internal static ForeignSession? Telnet(string source, string name, string host, string port, string group)
    {
        (name, host) = (name.Trim(), host.Trim());
        if (name.Length == 0 || host.Length == 0)
        {
            return null;
        }

        var number = int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 23;
        return new ForeignSession(source, name, host, number, string.Empty, group.Trim(), Protocol: ServerProfile.Telnet);
    }

    // Ohne die Lücken, jeden Namen nur einmal, in der Reihenfolge der Namen.
    internal static IReadOnlyList<ForeignSession> Collect(IEnumerable<ForeignSession?> sessions)
        => [.. sessions
            .OfType<ForeignSession>()
            .DistinctBy(session => (session.Group, session.Name))
            .OrderBy(session => session.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSessions)];

    private static string Value(StoredSession key, string name, string fallback = "")
        => key.Values.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    private static string Decode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
