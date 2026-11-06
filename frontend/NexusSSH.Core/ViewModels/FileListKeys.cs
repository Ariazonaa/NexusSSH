using System.Globalization;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was eine Taste in einer Dateiliste bewirkt – in der Liste des Servers und der lokalen gleich.</summary>
public enum FileListAction
{
    None,
    Open,
    Rename,
    Refresh,
    Delete,
    DeleteForever,
    Up,
    Back,
    Forward,
    SelectAll,
    FocusPath,
    FocusFilter,

    /// <summary>Alt+Eingabe wie im Explorer – nur in der Liste des Servers.</summary>
    Properties,
}

/// <summary>
/// Die Tastenbelegung der Dateilisten wie im Explorer. Die Namen der Tasten sind plattformfrei
/// (<c>"Enter"</c>, <c>"F2"</c>, <c>"F4"</c>, <c>"F5"</c>, <c>"Delete"</c>, <c>"Back"</c>, <c>"Up"</c>, <c>"Left"</c>,
/// <c>"Right"</c>, <c>"A"</c>, <c>"D"</c>, <c>"F"</c>, <c>"L"</c>); die Oberfläche übersetzt ihre Tasten dorthin.
/// </summary>
public static class FileListKeys
{
    /// <param name="remote">Die Liste des Servers: kein Papierkorb (Umschalt+Entf ist dort wie Entf) und ein Filter.</param>
    public static FileListAction ActionFor(string key, bool control, bool shift, bool alt, bool remote)
        => (key, control, shift, alt) switch
        {
            ("Enter", false, false, false) => FileListAction.Open,
            ("Enter", false, false, true) when remote => FileListAction.Properties,
            ("F2", false, false, false) => FileListAction.Rename,
            ("F5", false, false, false) => FileListAction.Refresh,
            ("Delete", false, true, false) when !remote => FileListAction.DeleteForever,
            ("Delete", false, _, false) => FileListAction.Delete,
            ("Back", false, false, false) => FileListAction.Up,
            ("Up", false, false, true) => FileListAction.Up,
            ("Left", false, false, true) => FileListAction.Back,
            ("Right", false, false, true) => FileListAction.Forward,
            ("A", true, false, false) => FileListAction.SelectAll,
            ("L", true, false, false) => FileListAction.FocusPath,
            ("D", false, false, true) => FileListAction.FocusPath,
            ("F4", false, false, false) => FileListAction.FocusPath,
            ("F", true, false, false) when remote => FileListAction.FocusFilter,
            _ => FileListAction.None,
        };
}

/// <summary>
/// Tippen springt zum Namen wie im Explorer: Die getippten Buchstaben ergeben den Anfang des Namens, nach einer
/// Pause von über einer Sekunde beginnt die Suche neu, und derselbe Buchstabe mehrmals springt reihum zum
/// nächsten Namen mit diesem Anfangsbuchstaben. Groß- und Kleinschreibung und Akzente zählen nicht.
/// </summary>
public sealed class TypeAhead(TimeProvider time)
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private string _typed = string.Empty;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Ob gerade getippt wird – dann gehört auch ein Leerzeichen zum Namen, sonst der Liste (Auswahl).</summary>
    public bool IsTyping => _typed.Length > 0 && time.GetUtcNow() - _last <= Pause;

    /// <summary>Der Eintrag, zu dem der Buchstabe führt (Nummer in <paramref name="names"/>); <c>null</c>, wenn keiner passt.</summary>
    /// <param name="current">Der gewählte Eintrag, −1 ohne Auswahl.</param>
    public int? Next(char typed, IReadOnlyList<string> names, int current)
    {
        var now = time.GetUtcNow();
        if (now - _last > Pause)
        {
            _typed = string.Empty;
        }

        _last = now;
        _typed += typed;
        if (names.Count == 0)
        {
            return null;
        }

        // aa…: reihum durch die Namen mit diesem Anfangsbuchstaben.
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var repeated = _typed.Length > 1 && _typed.All(c => compare.Compare(c.ToString(), _typed[..1], Loose) == 0);
        var prefix = repeated ? _typed[..1] : _typed;

        // Ein neuer Anfang sucht nach der Auswahl, ein längerer beginnt bei ihr – sie kann schon passen.
        var start = prefix.Length == 1 ? current + 1 : Math.Max(current, 0);
        for (var step = 0; step < names.Count; step++)
        {
            var index = (start + step) % names.Count;
            if (compare.IsPrefix(names[index], prefix, Loose))
            {
                return index;
            }
        }

        return null;
    }
}
