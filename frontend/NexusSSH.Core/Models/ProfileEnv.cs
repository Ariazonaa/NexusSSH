namespace NexusSSH.Core.Models;

/// <summary>
/// Eine Umgebungsvariable, die ein Server zu Beginn der Shell genannt bekommt (<c>LANG=de_DE.UTF-8</c>). Der
/// Server nimmt nur an, was er erlaubt (<c>AcceptEnv</c>) – meist <c>LANG</c> und <c>LC_…</c>.
/// </summary>
public sealed record ProfileEnv(string Name, string Value)
{
    /// <summary>So viele kann ein Profil nennen – wie im Kern (<c>profiles::MAX_ENV</c>).</summary>
    public const int Max = 16;

    /// <summary>So lang darf ein Wert sein – wie im Kern.</summary>
    public const int MaxValue = 256;

    /// <summary>Als was sich die App ausgibt, wenn das Profil nichts nennt.</summary>
    public const string DefaultTerm = "xterm-256color";

    /// <summary>Buchstaben, Ziffern und <c>_</c>, nicht mit einer Ziffer vorn – und Wert ohne Steuerzeichen.</summary>
    public bool IsValid
        => Name.Length is >= 1 and <= 64
            && !char.IsAsciiDigit(Name[0])
            && Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            && Value.Length <= MaxValue
            && !Value.Any(char.IsControl);

    /// <summary>
    /// Ob das der Name eines Terminals sein kann (<c>TERM</c>), wie die Einträge von terminfo heißen: Buchstaben,
    /// Ziffern, <c>-</c>, <c>.</c>, <c>_</c>, <c>+</c>, vorn ein Buchstabe oder eine Ziffer.
    /// </summary>
    public static bool IsTerminalName(string? name)
        => name is { Length: >= 1 and <= 64 }
            && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '+');

    /// <summary>
    /// Liest Zeilen <c>NAME=wert</c>. Leere Zeilen zählen nicht; was keine Variable ist, doppelt vorkommt oder über
    /// die Obergrenze hinausgeht, steht in <paramref name="rejected"/>.
    /// </summary>
    public static List<ProfileEnv> Parse(string? text, out List<string> rejected)
    {
        List<ProfileEnv> variables = [];
        rejected = [];
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var cut = line.IndexOf('=', StringComparison.Ordinal);
            var variable = cut > 0 ? new ProfileEnv(line[..cut].Trim(), line[(cut + 1)..]) : null;
            if (variable is { IsValid: true } && variables.Count < Max && variables.All(known => known.Name != variable.Name))
            {
                variables.Add(variable);
            }
            else
            {
                rejected.Add(line);
            }
        }

        return variables;
    }

    /// <summary>Die Zeilen, aus denen <see cref="Parse"/> dieselben Variablen wieder liest.</summary>
    public static string Format(IReadOnlyList<ProfileEnv>? variables)
        => string.Join('\n', (variables ?? []).Select(variable => $"{variable.Name}={variable.Value}"));
}
