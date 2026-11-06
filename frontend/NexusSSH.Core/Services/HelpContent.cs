using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Services;

/// <summary>Ein Handgriff auf der Hilfe-Seite: worum es geht und wie es geht.</summary>
public sealed record HelpTopic(string Title, string Text)
{
    /// <summary>Titel und Text – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Title}: {Text}";
}

/// <summary>Ein Tastenkürzel auf der Hilfe-Seite.</summary>
public sealed record HelpShortcut(string Title, string Keys)
{
    public override string ToString() => $"{Title}: {Keys}";
}

/// <summary>
/// Was die Hilfe-Seite zeigt: die wichtigsten Handgriffe, die geltenden Tastenkürzel und, was die laufende Version
/// neu kann. Die Kürzel in den Texten kommen aus den Befehlen, wie sie gerade belegt sind – hat der Benutzer eines
/// geändert, steht das geänderte da.
/// </summary>
public static class HelpContent
{
    /// <summary>Die Handgriffe, in der Reihenfolge, in der man sie braucht.</summary>
    public static IReadOnlyList<HelpTopic> Topics(IReadOnlyList<ShellCommand> commands)
    {
        string Keys(string id) => commands.FirstOrDefault(command => command.Id == id)?.Shortcut?.Display ?? L.Get("HelpNoShortcut");
        return BindableList.Of<HelpTopic>(
        [
            new(L.Get("HelpConnectTitle"), L.Get("HelpConnectText", Keys("view.palette"))),
            new(L.Get("HelpPaletteTitle"), L.Get("HelpPaletteText", Keys("view.palette"))),
            new(L.Get("HelpLeaveTitle"), L.Get("HelpLeaveText", Keys("view.nextPane"), Keys("view.previousPane"))),
            new(L.Get("HelpTabsTitle"), L.Get("HelpTabsText", Keys("session.next"), Keys("session.close"))),
            new(L.Get("HelpSplitTitle"), L.Get("HelpSplitText", Keys("terminal.splitRight"), Keys("terminal.splitDown"))),
            new(L.Get("HelpCopyTitle"), L.Get("HelpCopyText")),
            new(L.Get("HelpFindTitle"), L.Get("HelpFindText", Keys("terminal.find"))),
            new(L.Get("HelpFilesTitle"), L.Get("HelpFilesText")),
            new(L.Get("SftpFollowShell"), L.Get("SftpFollowHelpText")),
            new(L.Get("HelpTransfersTitle"), L.Get("HelpTransfersText")),
            new(L.Get("HelpTroubleTitle"), L.Get("HelpTroubleText")),
        ]);
    }

    /// <summary>Alle Befehle, die ein Kürzel haben, mit dem Kürzel, das gilt.</summary>
    public static IReadOnlyList<HelpShortcut> Shortcuts(IReadOnlyList<ShellCommand> commands)
        => BindableList.Of(commands.Where(command => command.Shortcut is not null).Select(command => new HelpShortcut(command.Title.TrimEnd('…', ' '), command.Shortcut!.Display)));

    /// <summary>
    /// Die Tastenkürzel als eigene Seite zum Drucken: schlichtes HTML mit einer Tabelle, hell, ohne Skripte und
    /// ohne etwas, das aus dem Netz geladen würde. Titel der Befehle stehen als Text darin, nie als Markup.
    /// </summary>
    public static string ShortcutsHtml(IReadOnlyList<ShellCommand> commands, string heading)
    {
        static string Text(string value) => System.Net.WebUtility.HtmlEncode(value);
        var page = new System.Text.StringBuilder()
            .Append("<!DOCTYPE html>\n<html lang=\"").Append(Text(L.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(Text(heading))
            .Append("</title>\n<style>\nbody { font-family: \"Segoe UI\", sans-serif; margin: 2em; color: #111; background: #fff; }\n")
            .Append("h1 { font-size: 1.4em; }\ntable { border-collapse: collapse; width: 100%; }\n")
            .Append("td { padding: 0.35em 0.6em; border-bottom: 1px solid #ccc; }\n")
            .Append("td.keys { font-family: Consolas, monospace; white-space: nowrap; text-align: right; }\n")
            .Append("@media print { body { margin: 0; } }\n</style>\n</head>\n<body>\n<h1>")
            .Append(Text(heading))
            .Append("</h1>\n<table>\n");
        foreach (var shortcut in Shortcuts(commands))
        {
            page.Append("<tr><td>").Append(Text(shortcut.Title)).Append("</td><td class=\"keys\">").Append(Text(shortcut.Keys)).Append("</td></tr>\n");
        }

        return page.Append("</table>\n</body>\n</html>\n").ToString();
    }

    /// <summary>Was die laufende Version neu kann – kurz, das Wichtigste zuerst.</summary>
    public static IReadOnlyList<string> WhatsNew()
        => BindableList.Of<string>(
        [
            L.Get("NewsLayout"),
            L.Get("NewsMcp"),
            L.Get("NewsSnippetFolders"),
            L.Get("NewsExit"),
            L.Get("NewsTabs"),
            L.Get("NewsPaste"),
            L.Get("NewsWindow"),
        ]);

    /// <summary>
    /// Ob nach dem Start gezeigt wird, was neu ist: nur, wenn eine ältere Version dieser App hier schon gelaufen
    /// ist. Beim allerersten Start gibt es nichts „Neues“ – da grüßt die Einführung.
    /// </summary>
    public static bool IsUpdate(string? lastSeen, string current)
        => Version.TryParse(lastSeen, out var before) && Version.TryParse(current, out var now) && now > before;
}
