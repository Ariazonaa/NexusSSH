using System.Globalization;

namespace NexusSSH.Core.Services;

/// <summary>
/// Ein Ziel zum Schnellverbinden, wie man es <c>ssh</c> nennt: <c>benutzer@host</c>, <c>benutzer@host:2222</c>,
/// <c>benutzer@[2001:db8::1]:2222</c>. Nichts anderes – kein Befehl, keine Optionen, kein Pfad: Was hier
/// herauskommt, geht als Benutzer, Host und Port in ein Profil, und das prüft der Kern wie jedes eingetippte.
/// </summary>
public sealed record QuickTarget(string User, string Host, int Port)
{
    public const int DefaultPort = 22;

    /// <summary>
    /// So nennt die App das Ziel – und so heißt das vorläufige Profil. Eine IPv6-Adresse steht immer in Klammern,
    /// auch ohne Port: Sonst läse <see cref="Parse"/> ihren letzten Doppelpunkt als Port.
    /// </summary>
    public string Display
    {
        get
        {
            var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
            return Port == DefaultPort ? $"{User}@{host}" : $"{User}@{host}:{Port.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>Liest ein Ziel; <c>null</c> für alles, was keines ist.</summary>
    public static QuickTarget? Parse(string? text)
    {
        var input = text?.Trim() ?? string.Empty;
        var at = input.LastIndexOf('@');
        if (at <= 0 || at == input.Length - 1 || input.Length > 320)
        {
            return null;
        }

        var (user, rest) = (input[..at], input[(at + 1)..]);
        // Kein Konto beginnt mit `-` – so sähe nur ein Schalter aus (`-oProxyCommand=…` in einem Link).
        if (user.Length > 64 || user.StartsWith('-') || user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '@' or ':' or '/' or '\\'))
        {
            return null;
        }

        string host;
        var port = DefaultPort;
        if (rest.StartsWith('['))
        {
            // Eine IPv6-Adresse in Klammern, damit ihr letzter Doppelpunkt nicht für den Port gehalten wird.
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 2)
            {
                return null;
            }

            host = rest[1..close];
            var tail = rest[(close + 1)..];
            if (tail.Length > 0 && (tail[0] != ':' || !TryPort(tail[1..], out port)))
            {
                return null;
            }

            if (!System.Net.IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return null;
            }
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon < 0 ? rest : rest[..colon];
            if (colon >= 0 && !TryPort(rest[(colon + 1)..], out port))
            {
                return null;
            }

            // Ein Hostname oder eine IPv4-Adresse: Buchstaben, Ziffern, Punkt, Bindestrich, Unterstrich.
            if (host.Length is 0 or > 255 || host.StartsWith('-') || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return null;
            }
        }

        return new QuickTarget(user, host, port);
    }

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
