namespace NexusSSH.Core.Models;

/// <summary>
/// Die UDP-Ports, aus denen <c>mosh-server</c> einen nimmt: eine Zahl (<c>60001</c>) oder ein Bereich
/// (<c>60001:60010</c>). Dieselbe Regel wie im Kern (<c>profiles::mosh_port_range</c>) – beide ändern: nur Ziffern, je 1
/// bis 65535 (höchstens fünf Ziffern), und „von“ nicht größer als „bis“. Der Bereich landet im Kern in einer Befehlszeile
/// am Server, aber nur als die Zahlen, die er selbst schreibt.
/// </summary>
public static class MoshPorts
{
    /// <summary>Gilt der Text – oder ist er leer (dann wählt <c>mosh-server</c> selbst)?</summary>
    public static bool IsValid(string? ports)
    {
        var text = ports?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        var cut = text.IndexOf(':', StringComparison.Ordinal);
        if (cut < 0)
        {
            return Port(text) is not null;
        }

        return Port(text[..cut]) is { } from && Port(text[(cut + 1)..]) is { } to && from <= to;
    }

    /// <summary>Der Text, wie er im Profil steht: ohne Rand, <c>null</c> wenn leer oder ungültig.</summary>
    public static string? Clean(string? ports)
        => ports?.Trim() is { Length: > 0 } text && IsValid(text) ? text : null;

    private static int? Port(string digits)
        => digits.Length is >= 1 and <= 5
            && digits.All(char.IsAsciiDigit)
            && int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) is var port and (>= 1 and <= 65535)
                ? port
                : null;
}
