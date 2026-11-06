using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Models;

/// <summary>Eine laufende Sitzung von tmux oder screen auf einem Server.</summary>
/// <param name="Kind"><c>tmux</c> oder <c>screen</c>.</param>
/// <param name="Windows">Zahl der Fenster; nur tmux nennt sie.</param>
/// <param name="Attached">Ob gerade jemand daran hängt.</param>
/// <param name="Command">Die Zeile, die in der Shell daran anhängt – gebaut vom Kern.</param>
public sealed record MuxSession(string Kind, string Name, int? Windows, bool Attached, string Command)
{
    /// <summary>
    /// Ob sich die Zeile gefahrlos tippen lässt: eine der beiden bekannten Formen mit einem Namen aus Buchstaben,
    /// Ziffern, Punkt, Unterstrich und Bindestrich. Der Kern liefert nichts anderes – hier steht die Prüfung noch
    /// einmal, weil die Zeile in die Shell des Servers geht und der Name von dort kommt.
    /// </summary>
    public bool IsSafe
        => Name is { Length: > 0 and <= 64 }
            && Name[0] != '-'
            && Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            && Command == Kind switch
            {
                "tmux" => $"tmux attach-session -t ={Name}",
                "screen" => $"screen -x {Name}",
                _ => null,
            };

    /// <summary>Was die Liste zeigt und Screenreader vorlesen: Art, Name, Fenster, ob jemand daran hängt.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Kind };
            if (Windows is { } windows)
            {
                parts.Add(windows == 1 ? L.Get("MuxWindowOne") : L.Get("MuxWindows", windows));
            }

            parts.Add(L.Get(Attached ? "MuxInUse" : "MuxDetached"));
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => $"{Name}, {Summary}";
}

/// <summary>Wie der gemerkte Schritt je Profil in den Einstellungen steht.</summary>
public static class MuxSteps
{
    /// <summary>Nach dem Verbinden anbieten.</summary>
    public const string Offer = "offer";

    /// <summary>
    /// Von selbst an diese Sitzung anhängen – mit <paramref name="control"/> im Steuermodus von tmux (die Fenster
    /// als Tabs der App).
    /// </summary>
    public static string For(MuxSession session, bool control = false)
        => $"{(control && session.Kind == "tmux" ? "control" : "attach")}:{session.Kind}:{session.Name}";

    /// <summary>Art und Name aus einem gemerkten „von selbst anhängen“; <c>null</c> für alles andere.</summary>
    public static (string Kind, string Name)? Target(string? step)
        => step?.Split(':', 3) switch
        {
            ["attach", ("tmux" or "screen") and var kind, { Length: > 0 } name] => (kind, name),
            ["control", "tmux", { Length: > 0 } name] => ("tmux", name),
            _ => null,
        };

    /// <summary>Ob der gemerkte Schritt den Steuermodus von tmux meint.</summary>
    public static bool IsControl(string? step) => step?.StartsWith("control:tmux:", StringComparison.Ordinal) == true;

    /// <summary>Nur, was sich lesen lässt, und nicht beliebig viel.</summary>
    public static Dictionary<string, string> Sanitize(Dictionary<string, string>? steps)
        => (steps ?? [])
            .Where(entry => entry.Key is { Length: > 0 and <= 128 } && (entry.Value == Offer || Target(entry.Value) is not null))
            .Take(2000)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
}
