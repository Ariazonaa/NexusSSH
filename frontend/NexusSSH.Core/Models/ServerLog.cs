using System.Globalization;
using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Models;

/// <summary>Wie weit das Journal zurückreichen soll – die feste Auswahl, die der Kern kennt.</summary>
public enum LogSince
{
    /// <summary>Ohne Grenze: die letzten Zeilen, gleich wie alt.</summary>
    All,
    Minutes15,
    Hour,
    Hours6,
    Day,
    Week,
}

/// <summary>Was vom Server gelesen werden soll: das Journal von systemd oder eine Textdatei.</summary>
/// <param name="Path">Absoluter Pfad einer Datei; <c>null</c> für das Journal.</param>
/// <param name="Priority">Nur Journal: diese Schwere und alles Ernstere, 0 (Notfall) bis 7; <c>null</c> für alles.</param>
/// <param name="Unit">Nur Journal: Name einer Einheit (<c>nginx.service</c>); leer für alle.</param>
/// <param name="Lines">So viele Zeilen höchstens.</param>
public sealed record LogQuery(string? Path = null, LogSince Since = LogSince.All, int? Priority = null, string Unit = "", int Lines = LogQuery.DefaultLines)
{
    public const int DefaultLines = 500;
    public const int MaxLines = 2000;

    /// <summary>Die Zeilenzahlen, die die Seite zur Wahl stellt.</summary>
    public static IReadOnlyList<int> LineChoices { get; } = BindableList.Of([200, 500, 1000, 2000]);

    /// <summary>
    /// Ob der Text der Name einer Einheit sein kann, wie der Kern ihn annimmt: Buchstaben, Ziffern und <c>@._:-\</c>.
    /// Der Kern prüft selbst noch einmal – hier nur, damit die Seite es gleich sagen kann.
    /// </summary>
    public static bool IsUnitName(string text)
        => text is { Length: > 0 and <= 128 } && text[0] != '-' && text.All(character => char.IsAsciiLetterOrDigit(character) || "@._:-\\".Contains(character));
}

/// <summary>Eine Zeile eines Protokolls. Quelle und Text kommen vom Server: nur als reiner Text zu zeigen.</summary>
/// <param name="Time">Wann, in der Zeit dieses Rechners; <c>null</c>, wenn die Quelle es nicht nennt (Dateien).</param>
/// <param name="Priority">Schwere wie bei syslog, 0 (Notfall) bis 7 (Fehlersuche); <c>null</c>, wenn die Quelle keine nennt.</param>
public sealed record ServerLogLine(DateTimeOffset? Time, int? Priority, string Source, string Message)
{
    /// <summary>Ab dieser Schwere ist es ein Fehler (0–3), darunter eine Warnung (4).</summary>
    public const int ErrorPriority = 3;
    public const int WarningPriority = 4;

    public string TimeText => Time is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    public bool HasTime => Time is not null;

    public bool HasSource => Source.Length > 0;

    /// <summary>Wie ernst die Zeile ist – danach richtet sich ihre Farbe.</summary>
    public UsageLevel Level => Priority switch
    {
        <= ErrorPriority and >= 0 => UsageLevel.Critical,
        WarningPriority => UsageLevel.Warning,
        _ => UsageLevel.Normal,
    };

    /// <summary>Das Wort für die Schwere („Fehler“, „Warnung“ …); leer, wenn die Quelle keine nennt.</summary>
    public string PriorityText => Priority switch
    {
        0 or 1 or 2 => L.Get("LogsPriorityCritical"),
        3 => L.Get("LogsPriorityError"),
        4 => L.Get("LogsPriorityWarning"),
        5 => L.Get("LogsPriorityNotice"),
        6 => L.Get("LogsPriorityInfo"),
        7 => L.Get("LogsPriorityDebug"),
        _ => string.Empty,
    };

    /// <summary>Die Zeile als Text, wie sie kopiert wird: Zeit, Quelle, Text.</summary>
    public string Text => string.Join(' ', new[] { TimeText, HasSource ? $"{Source}:" : string.Empty, Message }.Where(part => part.Length > 0));

    /// <summary>Screenreader lesen das als Bezeichnung der Zeile vor – mit der Schwere, die sonst nur die Farbe zeigt.</summary>
    public override string ToString() => PriorityText.Length > 0 ? $"{PriorityText}: {Text}" : Text;
}

/// <summary>Die gelesenen Zeilen, älteste zuerst.</summary>
/// <param name="Limited">Das Konto sieht nur seine eigenen Einträge im Journal (nicht in <c>systemd-journal</c> oder <c>adm</c>).</param>
public sealed record ServerLog(IReadOnlyList<ServerLogLine> Lines, bool Limited = false);
