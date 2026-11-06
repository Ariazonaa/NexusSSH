using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Exchange;

/// <summary>
/// Liest Sitzungen aus einer Datei, die ein anderes Programm geschrieben oder exportiert hat. Welches Format es ist,
/// sagt der Inhalt – der Name der Datei dient nur als Name der Sitzung, wo die Datei selbst keinen nennt.
///
/// Gelesen werden: eine <c>ssh_config</c> (auch die, die das Kommandozeilenwerkzeug von Termius exportiert),
/// MobaXterm (<c>.mxtsessions</c>, <c>.ini</c>), mRemoteNG (<c>confCons.xml</c>), Royal TS (<c>.rtsz</c>, unverschlüsselt),
/// SecureCRT (eine Sitzungsdatei <c>.ini</c> oder der XML-Export), Remmina (<c>.remmina</c>) und Xshell
/// (<c>.xsh</c>). Alles ist fremde Eingabe und wird nur gelesen; übernommen wird, was SSH oder Telnet ist und einen
/// Host hat – nie ein Passwort, auch wenn die Datei eines enthält.
/// </summary>
public static class ForeignFiles
{
    public const string SshConfig = "ssh_config";
    public const string MRemoteNg = "mRemoteNG";
    public const string RoyalTs = "Royal TS";
    public const string SecureCrt = "SecureCRT";
    public const string Remmina = "Remmina";
    public const string Xshell = "Xshell";

    /// <summary>Größer ist keine dieser Dateien; was darüber liegt, wird nicht gelesen.</summary>
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>
    /// Die Sitzungen aus dem Inhalt einer Datei. Leer, wenn die Datei keines der Formate ist oder nichts
    /// Übernehmbares enthält.
    /// </summary>
    /// <param name="fileName">Name der Datei – der Name der Sitzung bei Formaten mit einer Datei je Sitzung.</param>
    /// <param name="fallbackUser">Benutzer, wenn die Sitzung keinen nennt.</param>
    public static IReadOnlyList<ForeignSession> Read(string? fileName, byte[] bytes, string fallbackUser)
    {
        if (bytes.Length is 0 or > MaxLength)
        {
            return [];
        }

        var text = Text(bytes);
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var start = text.TrimStart();
        if (start.StartsWith('<'))
        {
            return FromXml(text, fallbackUser);
        }

        var sections = Ini(text);
        if (sections.Keys.Any(section => section.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase)))
        {
            return ForeignSessions.FromMobaXterm(text, fallbackUser);
        }

        if (sections.TryGetValue("remmina", out var remmina))
        {
            return FromRemmina(remmina, name, fallbackUser);
        }

        if (sections.TryGetValue("CONNECTION", out var connection))
        {
            return FromXshell(sections, connection, name, fallbackUser);
        }

        if (text.Contains("S:\"Hostname\"", StringComparison.Ordinal))
        {
            return FromSecureCrtIni(text, name, fallbackUser);
        }

        return FromSshConfig(text, fallbackUser);
    }

    /// <summary>Den Text einer Datei: UTF-16 oder UTF-8 nach ihrer Marke, sonst UTF-8, und wenn das nicht passt, Latin-1.</summary>
    public static string Text(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return ForeignSessions.ReadText(bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes[3..] : bytes);
    }

    // --- ssh_config: „Host alias“ und darunter HostName, User, Port, IdentityFile ---

    /// <summary>
    /// Eine <c>ssh_config</c>: je <c>Host</c>-Block mit einem einzelnen Namen ohne Muster eine Sitzung. Blöcke für
    /// viele Hosts (<c>Host *</c>, <c>Host web*</c>) geben ihre Angaben an die Hosts weiter, auf die sie passen und
    /// die selbst nichts dazu sagen – wie bei ssh gilt die erste Angabe.
    /// </summary>
    public static IReadOnlyList<ForeignSession> FromSshConfig(string text, string fallbackUser)
    {
        var blocks = new List<(List<string> Patterns, Dictionary<string, string> Values)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cut = line.IndexOfAny([' ', '\t', '=']);
            if (cut <= 0)
            {
                continue;
            }

            var (keyword, value) = (line[..cut].ToLowerInvariant(), line[(cut + 1)..].Trim().TrimStart('=').Trim().Trim('"'));
            if (keyword == "host")
            {
                blocks.Add(([.. value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)], new(StringComparer.Ordinal)));
            }
            else if (keyword == "match")
            {
                // Bedingungen wertet hier niemand aus: Was darunter steht, gehört zu keinem Host.
                blocks.Add(([], new(StringComparer.Ordinal)));
            }
            else if (blocks.Count > 0)
            {
                blocks[^1].Values.TryAdd(keyword, value);
            }
        }

        var sessions = new List<ForeignSession?>();
        foreach (var alias in blocks.SelectMany(block => block.Patterns).Where(pattern => pattern.IndexOfAny(['*', '?', '!']) < 0).Distinct(StringComparer.Ordinal))
        {
            string? Find(string keyword) => blocks
                .Where(block => block.Patterns.Any(pattern => Matches(pattern, alias)) && !block.Patterns.Any(pattern => pattern.StartsWith('!') && Matches(pattern[1..], alias)))
                .Select(block => block.Values.GetValueOrDefault(keyword))
                .FirstOrDefault(value => value is { Length: > 0 });

            var key = Find("identityfile");
            sessions.Add(ForeignSessions.Session(SshConfig, alias, Find("hostname") ?? alias, Find("port") ?? "22", Find("user") ?? fallbackUser, string.Empty, key is null ? null : ExpandHome(key)));
        }

        return ForeignSessions.Collect(sessions);
    }

    // „web*“, „10.0.?.1“ – wie die Muster von ssh, ohne die Verneinung (die prüft der Aufrufer).
    internal static bool Matches(string pattern, string name)
    {
        if (pattern.StartsWith('!'))
        {
            return false;
        }

        // Ohne Rekursion: Nach einem `*` merkt sich die Schleife die Stelle und setzt nur dorthin zurück. Eine Zeile
        // wie `Host *a*a*a*a…` aus einer fremden Datei kostete sonst mit jedem Stern ein Vielfaches.
        static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
        {
            var (p, n, star, mark) = (0, 0, -1, 0);
            while (n < name.Length)
            {
                if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = n;
                }
                else if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
                {
                    p++;
                    n++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    n = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }
        return pattern.Length <= 256 && name.Length <= 256 && Match(pattern, name);
    }

    private static string ExpandHome(string path)
        => path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..].Replace('/', Path.DirectorySeparatorChar))
            : path;

    // --- XML: mRemoteNG, Royal TS, SecureCRT (Export) ---

    private static IReadOnlyList<ForeignSession> FromXml(string text, string fallbackUser)
    {
        XDocument document;
        try
        {
            // Keine DTD, keine Verweise nach außen: Die Datei ist fremd.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxLength });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [];
        }

        if (document.Root is not { } root)
        {
            return [];
        }

        var sessions = new List<ForeignSession?>();
        switch (root.Name.LocalName)
        {
            case "Connections":
                MRemoteNodes(root, string.Empty, fallbackUser, sessions, depth: 0);
                break;

            case "RTSZDocument" or "RoyalDocument":
                RoyalObjects(root, fallbackUser, sessions);
                break;

            case "VanDyke":
                if (root.Elements("key").FirstOrDefault(key => (string?)key.Attribute("name") == "Sessions") is { } list)
                {
                    SecureCrtKeys(list, string.Empty, fallbackUser, sessions, depth: 0);
                }

                break;
        }

        return ForeignSessions.Collect(sessions);
    }

    // mRemoteNG: <Node Name="web" Type="Connection" Hostname="h" Protocol="SSH2" Port="22" Username="u" />, Ordner als
    // Type="Container". Passwörter stehen verschlüsselt daneben und bleiben, wo sie sind.
    private static void MRemoteNodes(XElement parent, string group, string fallbackUser, List<ForeignSession?> sessions, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        foreach (var node in parent.Elements("Node"))
        {
            var name = (string?)node.Attribute("Name") ?? string.Empty;
            if (string.Equals((string?)node.Attribute("Type"), "Container", StringComparison.OrdinalIgnoreCase))
            {
                MRemoteNodes(node, group.Length > 0 ? $"{group} / {name}" : name, fallbackUser, sessions, depth + 1);
                continue;
            }

            var (host, port) = ((string?)node.Attribute("Hostname") ?? string.Empty, (string?)node.Attribute("Port") ?? string.Empty);
            var protocol = ((string?)node.Attribute("Protocol") ?? string.Empty).ToUpperInvariant();
            if (protocol.StartsWith("SSH", StringComparison.Ordinal))
            {
                var user = (string?)node.Attribute("Username") is { Length: > 0 } named ? named : fallbackUser;
                sessions.Add(ForeignSessions.Session(MRemoteNg, name, host, port, user, group, key: null));
            }
            else if (protocol == "TELNET")
            {
                sessions.Add(ForeignSessions.Telnet(MRemoteNg, name, host, port, group));
            }
        }
    }

    // Royal TS: <RoyalSSHConnection><Name>…</Name><URI>host</URI><Port>22</Port><ParentID>…</ParentID>, Ordner als
    // <RoyalFolder><ID>…</ID><Name>…</Name><ParentID>…</ParentID>. Der Benutzer steht meist in einem eigenen Objekt
    // für Anmeldedaten; steht er an der Verbindung selbst (CredentialUsername), kommt er mit.
    private static void RoyalObjects(XElement root, string fallbackUser, List<ForeignSession?> sessions)
    {
        var folders = root.Descendants("RoyalFolder")
            .Select(folder => (Id: (string?)folder.Element("ID"), Name: (string?)folder.Element("Name") ?? string.Empty, Parent: (string?)folder.Element("ParentID")))
            .Where(folder => folder.Id is { Length: > 0 })
            .GroupBy(folder => folder.Id!)
            .ToDictionary(group => group.Key, group => group.First());

        string GroupOf(string? id)
        {
            var parts = new List<string>();
            for (var steps = 0; steps < 32 && id is not null && folders.TryGetValue(id, out var folder); steps++)
            {
                // Der oberste Ordner ist das Dokument selbst – sein Name ist keine Gruppe.
                if (folder.Parent is { Length: > 0 } && folder.Name.Length > 0)
                {
                    parts.Insert(0, folder.Name);
                }

                id = folder.Parent;
            }

            return string.Join(" / ", parts);
        }

        foreach (var connection in root.Descendants().Where(element => element.Name.LocalName is "RoyalSSHConnection" or "RoyalTelnetConnection"))
        {
            var name = (string?)connection.Element("Name") ?? string.Empty;
            var host = (string?)connection.Element("URI") ?? string.Empty;
            var port = (string?)connection.Element("Port") ?? string.Empty;
            var group = GroupOf((string?)connection.Element("ParentID"));
            if (connection.Name.LocalName == "RoyalTelnetConnection")
            {
                sessions.Add(ForeignSessions.Telnet(RoyalTs, name, host, port, group));
            }
            else
            {
                var user = (string?)connection.Element("CredentialUsername") is { Length: > 0 } named ? named : fallbackUser;
                sessions.Add(ForeignSessions.Session(RoyalTs, name, host, port, user, group, (string?)connection.Element("PrivateKeyFile")));
            }
        }
    }

    // SecureCRT, „Export Settings“: <key name="Sessions"><key name="Ordner"><key name="Sitzung"><string name="Hostname">…
    // Eine Sitzung ist ein Schlüssel mit „Hostname“ darin, alles andere ein Ordner.
    private static void SecureCrtKeys(XElement parent, string group, string fallbackUser, List<ForeignSession?> sessions, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        foreach (var key in parent.Elements("key"))
        {
            var name = (string?)key.Attribute("name") ?? string.Empty;
            string? Value(string wanted) => key.Elements().FirstOrDefault(element => element.Name.LocalName is "string" or "dword" && (string?)element.Attribute("name") == wanted)?.Value;

            if (Value("Hostname") is { Length: > 0 } host)
            {
                Add(sessions, SecureCrt, name, host, Value("Protocol Name"), Value("[SSH2] Port") ?? Value("Port"), Value("Username"), group, Value("Identity Filename V2"), fallbackUser);
            }
            else
            {
                SecureCrtKeys(key, group.Length > 0 ? $"{group} / {name}" : name, fallbackUser, sessions, depth + 1);
            }
        }
    }

    // --- Eine Datei je Sitzung: SecureCRT (.ini), Remmina, Xshell ---

    // SecureCRT, Sitzungsdatei: S:"Hostname"=web01  ·  S:"Username"=root  ·  D:"[SSH2] Port"=00000016 (hexadezimal)
    private static IReadOnlyList<ForeignSession> FromSecureCrtIni(string text, string name, string fallbackUser)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            // S:"Name"=Wert oder D:"Name"=Zahl
            var trimmed = line.Trim();
            if (trimmed.Length > 4 && trimmed[1] == ':' && trimmed[2] == '"' && trimmed.IndexOf("\"=", 3, StringComparison.Ordinal) is > 3 and var end)
            {
                values.TryAdd($"{trimmed[0]}:{trimmed[3..end]}", trimmed[(end + 2)..]);
            }
        }

        var port = values.GetValueOrDefault("D:[SSH2] Port") is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
        var sessions = new List<ForeignSession?>();
        Add(sessions, SecureCrt, name, values.GetValueOrDefault("S:Hostname"), values.GetValueOrDefault("S:Protocol Name"), port, values.GetValueOrDefault("S:Username"), string.Empty, values.GetValueOrDefault("S:Identity Filename V2"), fallbackUser);
        return ForeignSessions.Collect(sessions);
    }

    // Remmina: [remmina] name=web · server=host:port · protocol=SSH · username=u · group=a/b · ssh_privatekey=…
    private static IReadOnlyList<ForeignSession> FromRemmina(Dictionary<string, string> values, string fileName, string fallbackUser)
    {
        var protocol = values.GetValueOrDefault("protocol", string.Empty).ToUpperInvariant();
        if (protocol is not ("SSH" or "SFTP"))
        {
            return [];
        }

        var (host, port) = HostAndPort(values.GetValueOrDefault("server", string.Empty));
        var user = values.GetValueOrDefault("username") is { Length: > 0 } named ? named : values.GetValueOrDefault("ssh_username") is { Length: > 0 } other ? other : fallbackUser;
        var group = values.GetValueOrDefault("group", string.Empty).Replace("/", " / ", StringComparison.Ordinal);
        var name = values.GetValueOrDefault("name") is { Length: > 0 } title ? title : fileName;
        return ForeignSessions.Collect([ForeignSessions.Session(Remmina, name, host, port, user, group, values.GetValueOrDefault("ssh_privatekey"))]);
    }

    // Xshell: [CONNECTION] Host=… Port=22 Protocol=SSH · [CONNECTION:AUTHENTICATION] UserName=… UserKey=…
    private static IReadOnlyList<ForeignSession> FromXshell(Dictionary<string, Dictionary<string, string>> sections, Dictionary<string, string> connection, string name, string fallbackUser)
    {
        var authentication = sections.GetValueOrDefault("CONNECTION:AUTHENTICATION") ?? [];
        var sessions = new List<ForeignSession?>();
        Add(sessions, Xshell, name, connection.GetValueOrDefault("Host"), connection.GetValueOrDefault("Protocol"), connection.GetValueOrDefault("Port"), authentication.GetValueOrDefault("UserName"), string.Empty, key: null, fallbackUser);
        return ForeignSessions.Collect(sessions);
    }

    // --- Gemeinsames ---

    // SSH (auch „SSH2“, „SSH1“) wird eine Sitzung, Telnet eine Telnet-Sitzung, alles andere (RDP, Serial, …) nichts.
    private static void Add(List<ForeignSession?> sessions, string source, string name, string? host, string? protocol, string? port, string? user, string group, string? key, string fallbackUser)
    {
        var kind = (protocol ?? "SSH").Trim().ToUpperInvariant();
        if (kind.StartsWith("SSH", StringComparison.Ordinal))
        {
            sessions.Add(ForeignSessions.Session(source, name, host ?? string.Empty, port ?? "22", user is { Length: > 0 } ? user : fallbackUser, group, key));
        }
        else if (kind == "TELNET")
        {
            sessions.Add(ForeignSessions.Telnet(source, name, host ?? string.Empty, port ?? "23", group));
        }
    }

    // „host“, „host:2222“, „[2001:db8::1]:22“ – eine IPv6-Adresse ohne Klammern bleibt ganz der Host.
    private static (string Host, string Port) HostAndPort(string server)
    {
        server = server.Trim();
        if (server.StartsWith('[') && server.IndexOf(']', StringComparison.Ordinal) is > 0 and var close)
        {
            return (server[1..close], server[(close + 1)..].TrimStart(':'));
        }

        var colon = server.LastIndexOf(':');
        return colon > 0 && server.IndexOf(':', StringComparison.Ordinal) == colon ? (server[..colon], server[(colon + 1)..]) : (server, string.Empty);
    }

    // Eine INI-Datei: Abschnitte mit „name=wert“. Zeilen vor dem ersten Abschnitt gehören zu keinem.
    private static Dictionary<string, Dictionary<string, string>> Ini(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
                }
            }
            else if (current is not null && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                current.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }

        return sections;
    }
}
