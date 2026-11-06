namespace NexusSSH.Core.Services;

/// <summary>
/// Womit die App gestartet wurde – oder was ein zweiter Start an die laufende App weiterreicht: ein Ziel
/// (<c>benutzer@host[:port]</c> auf der Kommandozeile, ein <c>ssh://</c>-Link) oder ein gespeicherter Server aus
/// der Sprungliste (<c>--open &lt;kennung&gt;</c>). Alles davon kommt von außen: Ein Ziel nennt nur Benutzer, Host
/// und Port, nie einen Befehl, eine Option oder einen Pfad, und verbunden wird erst nach einer Rückfrage
/// (<see cref="ViewModels.ShellViewModel.OpenLaunchAsync"/>).
/// </summary>
public sealed record LaunchRequest
{
    /// <summary>Der Schalter der Sprungliste: öffnet einen gespeicherten Server.</summary>
    public const string OpenSwitch = "--open";

    /// <summary>Höchstens so viele Argumente reicht ein zweiter Start weiter.</summary>
    public const int MaxArguments = 8;

    private LaunchRequest()
    {
    }

    /// <summary>Ein Ziel; <see cref="QuickTarget.User"/> ist leer, wenn ein Link keinen Benutzer nennt.</summary>
    public QuickTarget? Target { get; private init; }

    /// <summary>Die Kennung eines gespeicherten Servers (Sprungliste).</summary>
    public string? ProfileId { get; private init; }

    /// <summary>Kam das Ziel aus einem Link (sonst von der Kommandozeile)?</summary>
    public bool FromLink { get; private init; }

    /// <summary>
    /// Liest die Argumente eines Starts. Schalter der App (<c>--demo</c>, <c>--no-activate</c> …) bleiben
    /// unbeachtet; gilt genau ein Ziel oder genau <c>--open &lt;kennung&gt;</c>, sonst <c>null</c> – bei zwei
    /// Zielen wird keines geraten, und ein Argument, das kein Ziel ist, verwirft alles.
    /// </summary>
    public static LaunchRequest? FromArguments(IReadOnlyList<string> arguments)
    {
        // Ein Link kommt als einziges Argument ("%1"). Steht etwas daneben, hat ein Programm ihn ohne Kodierung
        // weitergereicht, und aus einem `"` darin wurden weitere Argumente – nichts davon gilt.
        if (arguments.Count != 1 && arguments.Any(IsLink))
        {
            return null;
        }

        LaunchRequest? found = null;
        for (var index = 0; index < arguments.Count && index < MaxArguments * 2; index++)
        {
            var argument = arguments[index];
            LaunchRequest? next;
            if (string.Equals(argument, OpenSwitch, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                next = index < arguments.Count && IsProfileId(arguments[index]) ? new LaunchRequest { ProfileId = arguments[index] } : null;
                if (next is null)
                {
                    return null;
                }
            }
            else if (argument.StartsWith('-'))
            {
                continue;
            }
            else
            {
                // Was weder Schalter noch Ziel ist, macht den ganzen Start ungültig: Ein Programm, das einen Link
                // ohne Kodierung weiterreicht, könnte sonst hinter einem kaputten Link `--open <kennung>`
                // einschleusen – und das öffnet ohne Frage.
                next = Parse(argument);
                if (next is null)
                {
                    return null;
                }
            }

            if (found is not null)
            {
                return null;
            }

            found = next;
        }

        return found;
    }

    /// <summary>Ein einzelnes Ziel: <c>ssh://…</c> oder <c>benutzer@host[:port]</c>; <c>null</c> für alles andere.</summary>
    public static LaunchRequest? Parse(string? text)
    {
        var input = text?.Trim() ?? string.Empty;
        if (input.StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase))
        {
            return FromLinkText(input[SshScheme.Length..]);
        }

        return QuickTarget.Parse(input) is { } target ? new LaunchRequest { Target = target } : null;
    }

    private const string SshScheme = "ssh://";

    /// <summary>Sieht das Argument wie ein Link aus (gleich, ob er gültig ist)?</summary>
    public static bool IsLink(string argument) => argument.TrimStart().StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase);

    // `ssh://[benutzer@]host[:port][/]` – nichts sonst: kein Pfad, keine Abfrage, keine Parameter nach `;` (der
    // Entwurf der IETF kennt `;fingerprint=…`; einem Link wird kein Fingerabdruck geglaubt), kein Prozentzeichen.
    private static LaunchRequest? FromLinkText(string rest)
    {
        if (rest.EndsWith('/'))
        {
            rest = rest[..^1];
        }

        if (rest.Length is 0 or > 320 || rest.Any(c => c is '/' or '?' or '#' or ';' or '%' or ',' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        if (rest.Contains('@', StringComparison.Ordinal))
        {
            // Der Benutzer eines Links nur aus schlichten Zeichen: Ein `＠` (U+FF20) oder ähnlich aussehende Zeichen
            // ließen in der Rückfrage einen anderen Server lesen, als gemeint ist.
            return QuickTarget.Parse(rest) is { } target && target.User.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                ? new LaunchRequest { Target = target, FromLink = true }
                : null;
        }

        // Ohne Benutzer: Host und Port so streng wie mit einem, der Benutzer bleibt leer.
        return QuickTarget.Parse("x@" + rest) is { } bare
            ? new LaunchRequest { Target = bare with { User = string.Empty }, FromLink = true }
            : null;
    }

    // So sehen die Kennungen des Kerns aus (auch importierte): kurz, nur Buchstaben, Ziffern, `-` und `_`.
    private static bool IsProfileId(string text)
        => text.Length is > 0 and <= 64 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
