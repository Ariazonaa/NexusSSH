using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Wie ein Pfad auf dem Server in die Zwischenablage kommt.</summary>
public enum PathLinkKind
{
    /// <summary>Der Pfad, wie ihn der Server nennt.</summary>
    Plain,

    /// <summary><c>sftp://benutzer@host/pfad</c></summary>
    Sftp,

    /// <summary><c>benutzer@host:/pfad</c>, bei anderem Port als Befehl <c>scp -P …</c></summary>
    Scp,

    /// <summary><c>ftp://</c> bzw. <c>ftps://</c></summary>
    Ftp,
}

/// <summary>
/// Ein Pfad auf dem Server als Link oder Ziel für <c>scp</c>. Der Pfad kommt vom Server und landet in der
/// Zwischenablage, also oft in einer Shell: Steuerzeichen kommen nie hindurch (im Link als <c>%XX</c>, sonst als
/// <c>?</c>), und was eine Shell deuten könnte, steht in einfachen Anführungszeichen.
/// </summary>
public static class PathLink
{
    /// <summary>Die Arten, die zu diesem Profil passen: SSH Link und scp, FTP den FTP-Link, sonst nur der Pfad.</summary>
    public static IReadOnlyList<PathLinkKind> KindsFor(ServerProfile profile)
        => profile.IsFtp
            ? [PathLinkKind.Plain, PathLinkKind.Ftp]
            : profile.IsSsh
                ? (CanScp(profile) ? [PathLinkKind.Plain, PathLinkKind.Sftp, PathLinkKind.Scp] : [PathLinkKind.Plain, PathLinkKind.Sftp])
                : [PathLinkKind.Plain];

    public static string Format(ServerProfile profile, string path, PathLinkKind kind) => kind switch
    {
        PathLinkKind.Sftp => Url("sftp", profile, path, 22),
        PathLinkKind.Ftp => Url(profile.IsPlainFtp ? "ftp" : "ftps", profile, path, profile.IsImplicitFtp ? 990 : 21),
        PathLinkKind.Scp => CanScp(profile) ? Scp(profile, path) : Url("sftp", profile, path, 22),
        _ => NoControls(path),
    };

    // Ein Benutzer oder Host mit „-“ vorn wäre im scp-Ziel eine Option (Profile kommen auch aus fremden Dateien): dann
    // gibt es kein scp-Ziel, und wer eines verlangt, bekommt den sftp-Link, der nie mit „-“ beginnt.
    private static bool CanScp(ServerProfile profile) => !profile.User.StartsWith('-') && !profile.Host.StartsWith('-');

    private static string Url(string scheme, ServerProfile profile, string path, int defaultPort)
    {
        var text = new StringBuilder(scheme).Append("://");
        if (profile.User.Length > 0)
        {
            text.Append(Encode(profile.User)).Append('@');
        }

        text.Append(Host(profile.Host));
        if (profile.Port != defaultPort)
        {
            text.Append(':').Append(profile.Port);
        }

        var segments = path.Split('/');
        text.Append(string.Join('/', segments.Select(Encode)));
        return text.ToString();
    }

    private static string Scp(ServerProfile profile, string path)
    {
        var user = profile.User.Length > 0 ? profile.User + "@" : string.Empty;
        var clean = NoControls(path);
        var target = $"{user}{Host(profile.Host)}:{clean}";
        // Eckige Klammern sind nur um eine IPv6-Adresse unverdächtig, nie im Benutzer oder Pfad.
        var quoted = IsPlain(user) && IsPlain(clean) && IsPlain(profile.Host.Replace(":", "0", StringComparison.Ordinal)) ? target : "'" + target.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return profile.Port == 22 ? quoted : $"scp -P {profile.Port} {quoted}";
    }

    private static string Host(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    /// <summary>Der Pfad selbst, nur ohne Steuerzeichen.</summary>
    public static string Plain(string path) => NoControls(path);

    private static string NoControls(string text)
        => string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = NexusSSH.Core.Format.IsInvisible(source[i]) ? '?' : source[i];
            }
        });

    // Was eine Shell nie deutet.
    private static bool IsPlain(string text)
        => text.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '/' or '+' or '@' or ':' or ',' or '=' or '-');

    // RFC 3986: Unreserviertes bleibt, alles andere als UTF-8 mit %XX.
    private static string Encode(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                result.Append((char)b);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }
}
