namespace NexusSSH.Core.Services;

/// <summary>
/// Wie mehrere Einträge umbenannt werden: erst Suchen und Ersetzen im Namen, dann das Muster. Im Muster stehen
/// <c>{name}</c> für den Namen ohne Endung, <c>{ext}</c> für die Endung samt Punkt und <c>{n}</c> für eine
/// laufende Nummer; ein leeres Muster lässt den Namen, wie er nach dem Ersetzen ist.
/// </summary>
public sealed record RenameRule(string Find = "", string Replace = "", bool IgnoreCase = false, string Pattern = "", int Start = 1, int Digits = 1);

/// <summary>Warum sich ein Eintrag so nicht umbenennen lässt.</summary>
public enum RenameProblem
{
    None,

    /// <summary>Kein Name, den ein Server annimmt (leer, mit <c>/</c>, <c>.</c> oder <c>..</c>).</summary>
    Invalid,

    /// <summary>Zwei Einträge bekämen denselben Namen.</summary>
    Twice,

    /// <summary>So heißt schon ein Eintrag, der nicht mit umbenannt wird.</summary>
    Exists,
}

/// <summary>Ein Eintrag der Vorschau: wie er heißt, wie er heißen wird, und was dagegen spricht.</summary>
public sealed record RenameLine(string From, string To, RenameProblem Problem)
{
    public bool Changes => From != To;
}

/// <summary>Rechnet aus, wie mehrere Einträge nach einer Regel heißen werden – ohne etwas umzubenennen.</summary>
public static class RenamePlan
{
    public const string Name = "{name}";
    public const string Extension = "{ext}";
    public const string Number = "{n}";

    /// <summary>
    /// Die Vorschau für <paramref name="names"/>, in deren Reihenfolge. <paramref name="others"/> sind die übrigen
    /// Namen im Verzeichnis – mit ihnen darf keiner zusammenfallen.
    /// </summary>
    public static IReadOnlyList<RenameLine> Build(IReadOnlyList<string> names, RenameRule rule, IEnumerable<string> others)
    {
        var comparison = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pattern = rule.Pattern.Length > 0 ? rule.Pattern : Name + Extension;
        var digits = Math.Clamp(rule.Digits, 1, 9);
        var targets = names
            .Select((name, index) =>
            {
                var replaced = rule.Find.Length > 0 ? name.Replace(rule.Find, rule.Replace, comparison) : name;
                var (stem, extension) = Split(replaced);
                return pattern
                    .Replace(Name, stem, StringComparison.Ordinal)
                    .Replace(Extension, extension, StringComparison.Ordinal)
                    .Replace(Number, (rule.Start + index).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Trim();
            })
            .ToList();

        var sources = names.ToHashSet(StringComparer.Ordinal);
        var staying = others.Where(other => !sources.Contains(other)).ToHashSet(StringComparer.Ordinal);
        var counts = targets.GroupBy(target => target, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return
        [
            .. names.Select((name, index) =>
            {
                var target = targets[index];
                var problem = !RemotePath.IsValidName(target) ? RenameProblem.Invalid
                    : counts[target] > 1 ? RenameProblem.Twice
                    : staying.Contains(target) ? RenameProblem.Exists
                    : RenameProblem.None;
                return new RenameLine(name, target, problem);
            }),
        ];
    }

    /// <summary>Ob sich die Vorschau so ausführen lässt: nichts spricht dagegen, und mindestens ein Name ändert sich.</summary>
    public static bool CanApply(IReadOnlyList<RenameLine> lines)
        => lines.All(line => line.Problem == RenameProblem.None) && lines.Any(line => line.Changes);

    /// <summary>Ein Name für eine Kopie, den es unter <paramref name="taken"/> noch nicht gibt: „name (2).txt“, „name (3).txt“ …</summary>
    public static string CopyName(string name, IReadOnlySet<string> taken)
    {
        var (stem, extension) = Split(name);
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem} ({number}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // Die Endung ist, was hinter dem letzten Punkt steht – aber nicht bei „.bashrc“: Das ist ein Name, keine Endung.
    private static (string Stem, string Extension) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
    }
}
