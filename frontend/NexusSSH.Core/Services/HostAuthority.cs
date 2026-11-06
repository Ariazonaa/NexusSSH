namespace NexusSSH.Core.Services;

/// <summary>
/// Eine Zertifizierungsstelle für Host-Zertifikate (<c>@cert-authority</c> in <c>known_hosts</c>): was der Dialog
/// annimmt, bevor er den Kern fragt. Der Kern prüft beides noch einmal und liest den Schlüssel wirklich
/// (<c>hostkeys::authority_patterns</c>, <c>trust_authority</c>) – das hier sagt nur früh, ob es Sinn hat.
/// </summary>
public static class HostAuthority
{
    /// <summary>So lang darf die Liste der Muster sein – wie im Kern.</summary>
    public const int MaxPatterns = 1024;

    /// <summary>
    /// Muster für Server, durch Komma getrennt: Zeichen eines Hostnamens, <c>*</c>, <c>?</c>, <c>[host]:port</c>,
    /// <c>!</c> davor zum Ausnehmen – und mindestens eines, das nicht ausnimmt.
    /// </summary>
    public static bool IsPatterns(string? text)
    {
        var patterns = text?.Trim() ?? string.Empty;
        if (patterns.Length is 0 or > MaxPatterns)
        {
            return false;
        }

        var positive = false;
        foreach (var pattern in patterns.Split(','))
        {
            var negated = pattern.StartsWith('!');
            var name = negated ? pattern[1..] : pattern;
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '*' or '?' or ':' or '[' or ']'))
            {
                return false;
            }

            positive |= !negated;
        }

        return positive;
    }

    /// <summary>Ob die Muster jeden Server meinen – dann sagt der Dialog das eigens.</summary>
    public static bool CoversEverything(string? text)
        => IsPatterns(text) && text!.Trim().Split(',').Any(pattern => pattern.Length > 0 && pattern.All(c => c == '*'));

    /// <summary>
    /// Sieht aus wie eine Zeile mit einem öffentlichen Schlüssel (<c>ssh-ed25519 AAAA… [kommentar]</c>) – und nicht
    /// wie ein Zertifikat oder ein privater Schlüssel. Ob es wirklich einer ist, entscheidet der Kern.
    /// </summary>
    public static bool LooksLikeKey(string? text)
    {
        var fields = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2
            && !(text ?? string.Empty).Contains('\n', StringComparison.Ordinal)
            && (fields[0].StartsWith("ssh-", StringComparison.Ordinal) || fields[0].StartsWith("ecdsa-", StringComparison.Ordinal) || fields[0].StartsWith("sk-", StringComparison.Ordinal))
            && !fields[0].Contains("-cert-", StringComparison.Ordinal)
            && fields[1].Length is >= 16 and <= 16 * 1024
            && fields[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');
    }
}
