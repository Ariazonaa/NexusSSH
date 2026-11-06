using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Nachrichten zwischen der App und der Terminal-Seite (<c>Assets/terminal/terminal.js</c>) im WebView2.
/// Beide Seiten müssen dieselben Namen kennen – wer hier etwas ändert, ändert auch <c>terminal.js</c>.
/// </summary>
public static class TerminalMessages
{
    public const int MaxColumns = 1000;
    public const int MaxRows = 500;

    /// <summary>So viele Fundstellen nimmt die App von der Seite höchstens an.</summary>
    public const int MaxHits = 500;

    /// <summary>Länger wird der Text einer Fundstelle nicht.</summary>
    public const int MaxHitLength = 400;

    /// <summary>Mehr Zeichen nimmt die App als gespeicherten Verlauf eines Terminals nicht an.</summary>
    public const int MaxDumpLength = 16 * 1024 * 1024;

    /// <summary>Mehr nimmt die App als Inhalt eines Terminals, das in ein anderes Fenster umzieht, nicht an.</summary>
    public const int MaxSnapshotLength = 32 * 1024 * 1024;

    private const int MaxShortcutLength = 40;

    /// <summary>Mehr als das wird beim Einfügen per Rechtsklick abgeschnitten.</summary>
    public const int MaxPasteLength = 1024 * 1024;

    /// <summary>Länger ist keine Web-Adresse, die jemand anklicken will.</summary>
    public const int MaxLinkLength = 2048;

    /// <summary>Längere Titel (OSC 0/2) nimmt die App nicht an – am Reiter ist ohnehin nur für wenige Wörter Platz.</summary>
    public const int MaxTitleLength = 1024;

    /// <summary>Länger werden Überschrift und Text einer Meldung (OSC 9, OSC 777) nicht – die Seite kürzt ebenso, Core noch einmal.</summary>
    public const int MaxNoticeTitleLength = 80;
    public const int MaxNoticeBodyLength = 240;

    /// <summary>So viele gespeicherte Befehle bekommt die Seite höchstens zum Vorschlagen, und so lang darf einer sein.</summary>
    public const int MaxSuggestions = 200;
    public const int MaxSuggestionLength = 4000;

    /// <summary>Legt ein Terminal für die Sitzung an.</summary>
    /// <param name="mark">Kennfarbe des Servers als <c>#rrggbb</c> – ein Streifen am Rand des Terminals; <c>null</c> = keiner.</param>
    /// <param name="theme">Ein eigenes Farbschema für dieses Terminal (Farbschema des Profils); <c>null</c> = das allgemeine aus <see cref="Options"/>.</param>
    public static string Open(string id, string? mark = null, TerminalTheme? theme = null)
        => ToJson(new HostMessage("open") { Id = id, Mark = mark, Theme = theme is null ? null : ThemeOf(theme) });

    /// <summary>Gibt einem offenen Terminal ein eigenes Farbschema – oder nimmt es ihm (<c>null</c>): Dann gilt wieder das allgemeine.</summary>
    public static string Theme(string id, TerminalTheme? theme)
        => ToJson(new HostMessage("theme") { Id = id, Theme = theme is null ? null : ThemeOf(theme) });

    public static string Close(string id) => ToJson(new HostMessage("close") { Id = id });

    /// <summary>Die Namen der Programme im Suchpfad des Servers eines Terminals – das erste Wort einer Zeile. Leer nimmt sie zurück.</summary>
    public static string Commands(string id, IReadOnlyList<string> names)
        => ToJson(new HostMessage("commands") { Id = id, Words = [.. names.Take(Terminals.Completions.MaxWords)] });

    /// <summary>
    /// Die Namen im Verzeichnis der Shell eines Terminals, zum Ergänzen beim Tippen (<see cref="Terminals.Completions.Names"/>).
    /// Eine leere Liste nimmt sie zurück.
    /// </summary>
    public static string Names(string id, IReadOnlyList<PathName> names)
        => ToJson(new HostMessage("names") { Id = id, Names = [.. names.Take(Terminals.Completions.MaxNames)] });

    /// <summary>Länger wird der Name in der Kopfzeile eines Terminals nicht.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>
    /// Was in der Kopfzeile eines Terminals steht, wenn die Fläche geteilt ist: Name und Zustand der Sitzung. Der
    /// Name kann vom Server stammen (Titel eines Programms) – die Seite bekommt ihn als sichtbaren Text ohne
    /// Steuerzeichen, gekürzt, und setzt ihn als Text ein, nie als HTML.
    /// </summary>
    /// <param name="closeHint">Was am × steht („Aus der Teilung nehmen“); <c>null</c> = kein ×.</param>
    public static string Label(string id, string name, ConnectionState state, string? closeHint)
        => ToJson(new HostMessage("label")
        {
            Id = id,
            Data = LabelText(name),
            State = state switch
            {
                ConnectionState.Connected => "connected",
                ConnectionState.Connecting or ConnectionState.Authenticating => "connecting",
                _ => "closed",
            },
            Hint = closeHint is null ? null : LabelText(closeHint),
        });

    private static string LabelText(string text)
    {
        var clean = Format.Visible(new string([.. text.Where(character => !char.IsControl(character))])).Trim();
        return clean.Length <= MaxLabelLength ? clean : clean[..(MaxLabelLength - 1)] + "…";
    }

    /// <summary>
    /// Was die Seite zeigt: ein Terminal oder mehrere geteilt (Spec §5, „Split View“) als Baum – ein Blatt je
    /// Terminal, ein Zweig mit Richtung (<c>row</c> nebeneinander, <c>column</c> untereinander), Anteilen und
    /// Kindern. <paramref name="active"/> bekommt den Fokus, <paramref name="zoomed"/> (wenn gesetzt) die ganze
    /// Fläche, <paramref name="fixed"/>: Die Anordnung gehört tmux, die Seite lässt sie nicht ändern. Ohne
    /// Terminals zeigt die Seite nichts.
    /// </summary>
    public static string Layout(SplitNode<string>? tree, string? active, string? zoomed, bool @fixed)
        => ToJson(new HostMessage("layout")
        {
            Id = active,
            Tree = tree is null ? null : TreeOf(tree),
            Zoom = zoomed,
            Fixed = @fixed ? true : null,
        });

    private static TreeMessage TreeOf(SplitNode<string> node) => node switch
    {
        SplitLeaf<string> leaf => new TreeMessage { Id = leaf.Item },
        SplitBranch<string> branch => new TreeMessage
        {
            Dir = branch.SideBySide ? "row" : "column",
            Shares = branch.Shares,
            Items = [.. branch.Children.Select(TreeOf)],
        },
        _ => throw new ArgumentException("Unknown node", nameof(node)),
    };

    /// <summary>
    /// Ausgabe des Servers. Base64, weil ein Block mitten in einem UTF-8-Zeichen enden kann. Die Seite bestätigt
    /// mit <c>ack</c>, sobald sie den Block verarbeitet hat (Flusskontrolle).
    /// </summary>
    public static string Write(string id, ReadOnlySpan<byte> data) => ToJson(new HostMessage("write") { Id = id, Data = Convert.ToBase64String(data) });

    /// <summary>
    /// Text aus der Zwischenablage für das Terminal dieser Sitzung. Die Seite reicht ihn als Einfügen an xterm – so
    /// gilt der „bracketed paste“-Modus der Shell wie bei Strg+V.
    /// </summary>
    /// <param name="confirmed">Der Benutzer hat dem Einfügen mehrerer Zeilen zugestimmt – die Seite fragt nicht noch einmal.</param>
    public static string Paste(string id, string text, bool confirmed = false)
        => ToJson(new HostMessage("paste") { Id = id, Data = text.Length <= MaxPasteLength ? text : text[..MaxPasteLength], Confirmed = confirmed ? true : null });

    /// <summary>
    /// Was die Rückfrage vor dem Einfügen mehrerer Zeilen zeigt: die Zahl der Zeilen und die ersten davon, als
    /// sichtbarer Text und gekürzt – der Text kann aus einer Webseite stammen.
    /// </summary>
    public static (int Lines, string Preview) PastePreview(string text)
    {
        const int Shown = 6;
        const int Width = 100;
        var lines = text.Split('\n');
        var preview = string.Join('\n', lines.Take(Shown).Select(line => Format.Visible(line.Length <= Width ? line : line[..Width] + "…")));
        return (lines.Length, lines.Length > Shown ? preview + "\n…" : preview);
    }

    /// <summary>Farbschema, Schriftgröße und die Tastenkürzel der App, die die Seite melden soll.</summary>
    /// <param name="font">Name der Schrift; durch <see cref="TerminalThemes.Font"/> gegangen, bevor er die Seite erreicht.</param>
    /// <param name="mouseCopyPaste">Markieren kopiert, Rechtsklick fügt ein.</param>
    /// <param name="highlights">Regeln zum Hervorheben; ohne sie räumt die Seite ihre ab.</param>
    /// <param name="suggestions">Gespeicherte Befehle, die die Seite beim Tippen vorschlägt; ohne sie schlägt sie nichts vor.</param>
    /// <param name="look">Cursor und Abstände; ohne Angabe bleibt die Seite bei dem, was sie hat.</param>
    /// <param name="pasteClean">Eingefügten Text bereinigen (Steuerzeichen, unsichtbare Zeichen, Zeilenumbruch am Ende).</param>
    /// <param name="pasteTabs">Beim Einfügen Tabulatoren durch Leerzeichen ersetzen.</param>
    /// <param name="images">Bilder zeichnen, die der Server schickt (Sixel, iTerm2) – nur auf Wunsch des Benutzers.</param>
    public static string Options(TerminalTheme theme, int fontSize, IReadOnlyList<TerminalShortcut> shortcuts, string? font = null, bool mouseCopyPaste = false, IReadOnlyList<HighlightRule>? highlights = null, IReadOnlyList<Snippet>? suggestions = null, string? linkHint = null, TerminalLook? look = null, bool pasteClean = true, bool pasteTabs = false, bool images = false, bool optionSuggestions = false, bool pathSuggestions = false, IReadOnlyList<string>? hosts = null, bool tabCompletes = false)
        => ToJson(new HostMessage("options")
        {
            FontSize = fontSize,
            FontFamily = TerminalThemes.Font(font),
            MouseCopyPaste = mouseCopyPaste,
            Highlights = highlights ?? [],
            LinkHint = linkHint,
            Look = look,
            PasteClean = pasteClean,
            PasteTabs = pasteTabs,
            Images = images,
            // Immer dabei, auch leer: So schaltet die Seite ab, was in den Einstellungen abgeschaltet wurde.
            Completions = new CompletionMessage(
                optionSuggestions ? Terminals.Completions.Tables : [],
                pathSuggestions ? Terminals.Completions.PathCommands : [],
                pathSuggestions ? Terminals.Completions.DirectoryCommands : []),
            Hosts = [.. (hosts ?? []).Take(Terminals.Completions.MaxWords)],
            TabCompletes = tabCompletes,
            Suggestions = [.. (suggestions ?? []).Where(snippet => snippet.Text.Length <= MaxSuggestionLength).Take(MaxSuggestions).Select(snippet => new SuggestionMessage(snippet.Name, SuggestedText(snippet.Text))).Where(suggestion => suggestion.Text.Length > 0)],
            Shortcuts = shortcuts,
            Theme = ThemeOf(theme),
        });

    /// <summary>
    /// Was ein Vorschlag beim Tippen einsetzt: die erste Zeile des Befehls, ohne Steuerzeichen. Mehr zeigt die Seite
    /// nicht, also tippt sie auch nicht mehr – ein mehrzeiliger Befehl liefe sonst mit einem Tab los.
    /// </summary>
    public static string SuggestedText(string text)
        => new string([.. text.Split('\n')[0].Where(character => !char.IsControl(character))]).Trim();

    // Die Farben eines Schemas unter den Namen, die xterm.js kennt.
    private static ThemeMessage ThemeOf(TerminalTheme theme) => new(
        theme.Background,
        theme.Foreground,
        theme.Cursor,
        theme.Background,
        theme.Selection,
        theme.Ansi[0],
        theme.Ansi[1],
        theme.Ansi[2],
        theme.Ansi[3],
        theme.Ansi[4],
        theme.Ansi[5],
        theme.Ansi[6],
        theme.Ansi[7],
        theme.Ansi[8],
        theme.Ansi[9],
        theme.Ansi[10],
        theme.Ansi[11],
        theme.Ansi[12],
        theme.Ansi[13],
        theme.Ansi[14],
        theme.Ansi[15]);

    /// <summary>
    /// Das Bild hinter den Terminals als <c>data:</c>-Adresse (<see cref="TerminalBackdrop.DataUrl"/>) samt Abdunklung
    /// in Prozent; ohne Bild räumt die Seite ihres ab.
    /// </summary>
    public static string Backdrop(string? dataUrl, int dim)
        => ToJson(new HostMessage("backdrop") { Data = dataUrl ?? string.Empty, Dim = TerminalBackdrop.Dim(dim) });

    /// <summary>Sucht im Terminal der Sitzung weiter und hebt die Fundstellen hervor; die Seite antwortet mit <c>found</c>.</summary>
    public static string Find(string id, string query, bool matchCase, bool backwards, bool incremental)
        => ToJson(new HostMessage("find") { Id = id, Data = query, MatchCase = matchCase, Backwards = backwards, Incremental = incremental });

    /// <summary>Nimmt die Hervorhebungen der Suche zurück.</summary>
    public static string FindClear(string id) => ToJson(new HostMessage("findClear") { Id = id });

    /// <summary>Sucht in allen Terminals; die Seite antwortet mit <c>hits</c>.</summary>
    public static string Grep(string query, bool matchCase) => ToJson(new HostMessage("grep") { Data = query, MatchCase = matchCase });

    /// <summary>Rollt das Terminal zu einer Fundstelle und markiert sie.</summary>
    public static string Reveal(string id, int row, string query, bool matchCase)
        => ToJson(new HostMessage("reveal") { Id = id, Row = row, Data = query, MatchCase = matchCase });

    /// <summary>Leert den Bildschirm und den Verlauf des Terminals (nur die Anzeige, nichts auf dem Server).</summary>
    public static string Clear(string id) => ToJson(new HostMessage("clear") { Id = id });

    /// <summary>Gibt der Trennlinie neben dem Terminal den Fokus – danach schieben sie die Pfeiltasten.</summary>
    public static string FocusDivider(string id) => ToJson(new HostMessage("divider") { Id = id });

    /// <summary>Fordert den ganzen Text des Terminals an (Verlauf und Bildschirm); die Seite antwortet mit <c>text</c>.</summary>
    public static string Dump(string id) => ToJson(new HostMessage("dump") { Id = id });

    /// <summary>
    /// Fordert den Inhalt des Terminals samt Farben und Modi an, wie er sich in ein anderes Terminal schreiben
    /// lässt; die Seite antwortet mit <c>snapshot</c>, sobald alles Frühere verarbeitet ist.
    /// </summary>
    public static string Snapshot(string id) => ToJson(new HostMessage("snapshot") { Id = id });

    /// <summary>Bittet um die Auswahl eines Terminals mit ihren Farben („Mit Formatierung kopieren“).</summary>
    public static string CopyRich(string id) => ToJson(new HostMessage("copy") { Id = id, Data = "rich" });

    /// <summary>Bittet um die Auswahl eines Terminals ohne Zeilenumbrüche („Als eine Zeile kopieren“).</summary>
    public static string CopyLine(string id) => ToJson(new HostMessage("copy") { Id = id, Data = "line" });

    /// <summary>
    /// Liest eine Nachricht der Seite. Alles Unbekannte oder Unvollständige ergibt <c>null</c>: Der WebView ist
    /// die Grenze zu fremdem Inhalt (Terminal-Ausgabe eines Servers), seine Nachrichten gelten als ungeprüft.
    /// </summary>
    public static WebMessage? Parse(string json)
    {
        RawWebMessage? raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, TerminalJsonContext.Default.RawWebMessage);
        }
        // Auch ArgumentException: Die wirft der JSON-Leser bei einem halben UTF-16-Zeichen im Text (gefunden von den
        // Zufallstests) – für den Aufrufer ist das dieselbe unlesbare Eingabe.
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }

        return raw switch
        {
            { Type: "ready" } => new WebMessage.Ready(),
            { Type: "input", Id: { Length: > 0 } id, Data: { } data } => new WebMessage.Input(id, Encoding.UTF8.GetBytes(data)),
            { Type: "resize", Id: { Length: > 0 } id, Cols: int cols and >= 1 and <= MaxColumns, Rows: int rows and >= 1 and <= MaxRows }
                => new WebMessage.Resize(id, cols, rows),
            { Type: "shortcut", Key: { Length: > 0 and <= MaxShortcutLength } key } => new WebMessage.Shortcut(key),
            { Type: "focus", Id: { Length: > 0 } id } => new WebMessage.Focused(id),
            { Type: "unsplit", Id: { Length: > 0 } id } => new WebMessage.PaneClosed(id),
            { Type: "shares", Id: { Length: > 0 } id, Path: { } path, Shares: { } values }
                when SharePath(path) is { } steps && ShareValues(values) is { } parts => new WebMessage.Shares(id, steps, parts),
            { Type: "move", Id: { Length: > 0 } id, Target: { Length: > 0 } target, Side: { } side }
                when SideOf(side) is { } edge => new WebMessage.Moved(id, target, edge),
            { Type: "paste", Id: { Length: > 0 } id } => new WebMessage.PasteRequested(id),
            { Type: "confirmPaste", Id: { Length: > 0 } id, Data: { Length: > 0 and <= MaxPasteLength } text } => new WebMessage.PasteNeedsConsent(id, text),
            { Type: "bell", Id: { Length: > 0 } id } => new WebMessage.Bell(id),
            { Type: "notify", Id: { Length: > 0 } id } when Notice(raw.Title, raw.Body) is { } notice => new WebMessage.Notice(id, notice.Title, notice.Body),
            { Type: "title", Id: { Length: > 0 } id, Data: { Length: <= MaxTitleLength } title } => new WebMessage.TitleChanged(id, title),
            { Type: "cwd", Id: { Length: > 0 } id, Data: { } path } when ServerPath(path) is { } directory => new WebMessage.DirectoryChanged(id, directory),
            { Type: "link", Id: { Length: > 0 } id, Data: { } address } when WebLink(address) is { } link => new WebMessage.LinkActivated(id, link),
            { Type: "path", Id: { Length: > 0 } id, Data: { } path } when ClickedPath(path) is { } target => new WebMessage.PathActivated(id, target),
            { Type: "ack", Id: { Length: > 0 } id, Count: int count and > 0 } => new WebMessage.Acknowledged(id, count),
            { Type: "text", Id: { Length: > 0 } id, Data: { Length: <= MaxDumpLength } data } => new WebMessage.Text(id, Printable(data)),
            { Type: "snapshot", Id: { Length: > 0 } id, Data: { Length: <= MaxSnapshotLength } content } => new WebMessage.Snapshot(id, content),
            { Type: "copied", Id: { Length: > 0 } id, Key: "line", Data: { Length: <= RichCopy.MaxLength } line }
                => new WebMessage.Copied(id, OneLine: true, RichCopy.Printable(line).ReplaceLineEndings(string.Empty), null, null, null),
            { Type: "copied", Id: { Length: > 0 } id, Key: "rich", Data: { Length: <= RichCopy.MaxLength } rich }
                => new WebMessage.Copied(
                    id,
                    OneLine: false,
                    RichCopy.Printable(rich),
                    RichCopy.Clean(raw.Runs?.Where(run => run is not null).Select(run => (run.T, run.F, run.B, run.S ?? 0))),
                    RichCopy.Color(raw.Fore),
                    RichCopy.Color(raw.Back)),
            { Type: "found", Id: { Length: > 0 } id, Index: int index and >= -1, Count: int count and >= 0 }
                => new WebMessage.Found(id, index, count),
            { Type: "hits", Data: { } query, Items: { } items } => new WebMessage.Hits(
                query,
                [
                    .. items
                        .Where(item => item is { Id.Length: > 0, Row: >= 0, Text: not null })
                        .Take(MaxHits)
                        .Select(item => new TerminalHit(item.Id!, item.Row!.Value, Shorten(item.Text!))),
                ],
                raw.More == true || items.Count > MaxHits),
            _ => null,
        };
    }

    // Der Pfad zu einem Zweig: höchstens so tief wie ein Baum sein darf, jede Nummer ein Kind (höchstens MaxPanes − 1).
    private static List<int>? SharePath(List<int?> path)
        => path.Count <= SplitTree.MaxDepth && path.All(step => step is >= 0 and < SplitTree.MaxPanes) ? [.. path.Select(step => step!.Value)] : null;

    // Anteile: so viele, wie ein Zweig Kinder haben kann, alle endlich. Ob sie zum Baum passen, prüft die Shell.
    private static List<double>? ShareValues(List<double?> values)
        => values.Count is >= 2 and <= SplitTree.MaxPanes && values.All(value => value is { } share && double.IsFinite(share))
            ? [.. values.Select(value => value!.Value)]
            : null;

    private static PaneSide? SideOf(string side) => side switch
    {
        "left" => PaneSide.Left,
        "right" => PaneSide.Right,
        "top" => PaneSide.Top,
        "bottom" => PaneSide.Bottom,
        _ => null,
    };

    // Eine Fundstelle ist Ausgabe eines Servers: als Zeile Text behandeln, Steuerzeichen haben darin nichts verloren.
    private static string Shorten(string text)
    {
        // Zeichen, die die Schreibrichtung umkehren oder eine Zeile vortäuschen, erscheinen als Ersatzzeichen – wie
        // bei Dateinamen.
        var clean = Format.Visible(new string([.. text.Where(character => !char.IsControl(character))]));
        return clean.Length <= MaxHitLength ? clean : clean[..MaxHitLength];
    }

    // Eine Meldung, die ein Programm auf dem Server geschickt hat: Überschrift und Text als sichtbarer Text, ohne
    // Steuerzeichen, gekürzt. Ohne beides – oder nur Steuerzeichen – gibt es keine Meldung.
    private static (string Title, string Body)? Notice(string? title, string? body)
    {
        var cleanTitle = NoticeText(title, MaxNoticeTitleLength);
        var cleanBody = NoticeText(body, MaxNoticeBodyLength);
        return cleanTitle.Length == 0 && cleanBody.Length == 0 ? null : (cleanTitle, cleanBody);
    }

    /// <summary>
    /// Text einer Meldung für die Oberfläche: Steuerzeichen und Zeichen, die die Schreibrichtung umkehren oder
    /// eine Zeile vortäuschen (<see cref="Format.IsInvisible"/>), fallen weg – wie in der Seite –, höchstens
    /// <paramref name="max"/> Zeichen samt „…“. Der Text stammt vom Server.
    /// </summary>
    public static string NoticeText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Erst kürzen, dann säubern: Ein Server kann Kilobytes schicken.
        var head = text.Length > max * 4 ? text[..(max * 4)] : text;
        var clean = new string([.. head.Where(character => !Format.IsInvisible(character))]).Trim();
        if (clean.Length <= max)
        {
            return clean;
        }

        // Nicht mitten in einem Ersatzpaar kürzen.
        var length = max - 1;
        if (char.IsHighSurrogate(clean[length - 1]))
        {
            length--;
        }

        return clean[..length] + "…";
    }

    // Ein Verzeichnis, das die Shell eines Servers gemeldet hat: Es geht als Pfad an den Dateibrowser. Nur ein
    // absoluter Pfad ohne Steuerzeichen und von vernünftiger Länge – alles andere ist keine Meldung.
    private static string? ServerPath(string path)
        => path.Length is > 0 and <= 4096 && path[0] == '/' && !path.Any(char.IsControl) ? path : null;

    // Ein Pfad, der im Terminal angeklickt wurde: absolut, ab dem Startverzeichnis (~/…) oder relativ. Er stand in
    // der Ausgabe eines Servers und geht nur an den Dateibrowser – ohne Steuerzeichen, von vernünftiger Länge, und
    // nichts, was nach einem Laufwerk oder einer Adresse aussieht.
    private static string? ClickedPath(string path)
        => path.Length is > 0 and <= 4096 && !path.Any(char.IsControl) && !path.Contains('\\') && !path.Contains(':') ? path : null;

    /// <summary>
    /// Eine Adresse, die im Terminal angeklickt wurde – sie stand in der Ausgabe eines Servers, ist also fremd.
    /// Geöffnet wird nur, was eine gewöhnliche Web-Adresse ist: <c>http</c> oder <c>https</c>, mit Host, ohne
    /// Anmeldedaten davor (<c>https://bank.example@evil.example</c> sähe aus wie die Bank), ohne Steuerzeichen und
    /// von vernünftiger Länge. Alles andere – <c>file:</c>, <c>ms-…:</c>, ein Programm – ist keine.
    /// </summary>
    public static Uri? WebLink(string address)
        => address.Length is > 0 and <= MaxLinkLength
            && !address.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            ? uri
            : null;

    // Der Verlauf eines Terminals ist Ausgabe eines Servers: Er wird als Textdatei gespeichert, also bleiben nur
    // druckbare Zeichen, Tabulatoren und Zeilenenden.
    private static string Printable(string text)
        => text.Any(IsUnwanted) ? new string([.. text.Where(character => !IsUnwanted(character))]) : text;

    private static bool IsUnwanted(char character) => char.IsControl(character) && character is not ('\n' or '\t');

    private static string ToJson(HostMessage message) => JsonSerializer.Serialize(message, TerminalJsonContext.Default.HostMessage);
}

/// <summary>Tastenkürzel, das die Seite abfangen und der App melden soll (der WebView reicht Tasten nicht an XAML weiter).</summary>
/// <param name="Code">Taste, wie <c>KeyboardEvent.code</c> sie nennt (<c>KeyF</c>, <c>Tab</c>, <c>Equal</c>).</param>
/// <param name="Shift">Ob Umschalt dazugehört. Strg gehört immer dazu.</param>
/// <param name="Id">Was die Seite meldet – die Kennung des Befehls.</param>
public sealed record TerminalShortcut(string Code, bool Shift, string Id);

/// <summary>Eine Zeile aus dem Verlauf eines Terminals, in der der Suchtext vorkommt.</summary>
public sealed record TerminalHit(string Id, int Row, string Text);

/// <summary>Nachricht der Terminal-Seite an die App.</summary>
public abstract record WebMessage
{
    private WebMessage()
    {
    }

    /// <summary>Die Seite ist geladen und nimmt Nachrichten an.</summary>
    public sealed record Ready : WebMessage;

    /// <summary>Tastatureingabe oder eingefügter Text, als UTF-8.</summary>
    public sealed record Input(string Id, byte[] Data) : WebMessage;

    /// <summary>Größe des Terminals in Zeichen. Kommt einmal nach dem Anlegen und danach bei jeder Änderung.</summary>
    public sealed record Resize(string Id, int Columns, int Rows) : WebMessage;

    /// <summary>Tastenkürzel der App, das im Terminal gedrückt wurde – die Kennung aus <see cref="TerminalShortcut"/>.</summary>
    public sealed record Shortcut(string Key) : WebMessage;

    /// <summary>Der Benutzer hat in das Terminal dieser Sitzung geklickt (bei geteilter Ansicht: ein anderes).</summary>
    public sealed record Focused(string Id) : WebMessage;

    /// <summary>Das × in der Kopfzeile eines Terminals der geteilten Ansicht: Es soll aus der Teilung; die Sitzung bleibt.</summary>
    public sealed record PaneClosed(string Id) : WebMessage;

    /// <summary>
    /// Eine Trennlinie wurde gezogen: neue Anteile für die Kinder des Zweigs am <paramref name="Path"/> (Nummern der
    /// Kinder von der Wurzel an; leer = die Wurzel). <paramref name="Id"/> ist ein Terminal in diesem Zweig – die App
    /// bestimmt daran die Teilung. Pfad und Anteile sind nur auf Form und Grenzen geprüft; ob sie zum Baum passen,
    /// entscheidet <c>ShellViewModel.SetShares</c>.
    /// </summary>
    public sealed record Shares(string Id, IReadOnlyList<int> Path, IReadOnlyList<double> Values) : WebMessage;

    /// <summary>Der Kopf eines Terminals wurde an die Kante eines anderen gezogen.</summary>
    public sealed record Moved(string Id, string Target, PaneSide Side) : WebMessage;

    /// <summary>Rechtsklick im Terminal dieser Sitzung, wenn er einfügen soll: Die App liest die Zwischenablage – die Seite darf es nicht.</summary>
    public sealed record PasteRequested(string Id) : WebMessage;

    /// <summary>
    /// Die Seite soll mehrere Zeilen in eine Shell einfügen, die jede sofort ausführt (kein eingeklammertes
    /// Einfügen) – das geschieht erst, wenn der Benutzer zustimmt.
    /// </summary>
    public sealed record PasteNeedsConsent(string Id, string Content) : WebMessage;

    /// <summary>Das Programm im Terminal dieser Sitzung hat die Glocke geläutet (BEL).</summary>
    public sealed record Bell(string Id) : WebMessage;

    /// <summary>
    /// Ein Programm im Terminal dieser Sitzung hat eine Meldung geschickt (OSC 9, OSC 777): Überschrift (darf leer
    /// sein) und Text. Fremder Text – schon gesäubert und gekürzt, mindestens eines von beiden ist nicht leer.
    /// </summary>
    public sealed record Notice(string Id, string Title, string Body) : WebMessage;

    /// <summary>
    /// Die Shell dieser Sitzung hat ihr Arbeitsverzeichnis gemeldet (OSC 7). Der Pfad kommt vom Server: absolut,
    /// ohne Steuerzeichen und nicht länger als ein Pfad sein kann – sonst gibt es die Nachricht nicht.
    /// </summary>
    public sealed record DirectoryChanged(string Id, string Path) : WebMessage;

    /// <summary>
    /// Ein Programm auf dem Server hat den Titel des Terminals gesetzt (OSC 0/2). Fremder Text: Die Sitzung säubert
    /// und kürzt ihn, bevor er am Reiter steht (<c>SessionViewModel.SetServerTitle</c>).
    /// </summary>
    public sealed record TitleChanged(string Id, string Title) : WebMessage;

    /// <summary>Mit Strg angeklickt: eine Web-Adresse aus der Ausgabe, schon geprüft (<see cref="TerminalMessages.WebLink"/>).</summary>
    public sealed record LinkActivated(string Id, Uri Link) : WebMessage;

    /// <summary>Mit Strg angeklickt: ein Pfad aus der Ausgabe – absolut, mit <c>~/</c> oder relativ zum Verzeichnis der Shell. Der Dateibrowser zeigt ihn, wenn es ihn gibt.</summary>
    public sealed record PathActivated(string Id, string Path) : WebMessage;

    /// <summary>Das Terminal hat so viele Bytes der Ausgabe verarbeitet (Flusskontrolle).</summary>
    public sealed record Acknowledged(string Id, int Count) : WebMessage;

    /// <summary>Der ganze Text eines Terminals, Zeilen mit <c>\n</c> getrennt – die Antwort auf <c>dump</c>.</summary>
    public sealed record Text(string Id, string Data) : WebMessage;

    /// <summary>
    /// Der Inhalt eines Terminals samt Farben und Modi, als Text mit Steuerzeichen – die Antwort auf
    /// <c>snapshot</c>. Er ist wie Ausgabe des Servers zu behandeln und geht nur wieder in ein Terminal.
    /// </summary>
    public sealed record Snapshot(string Id, string Data) : WebMessage;

    /// <summary>
    /// Die Auswahl eines Terminals, um die die App gebeten hat: als eine Zeile oder mit den Stücken gleicher Farbe.
    /// Schon geprüft – Text ohne Steuerzeichen, Farben nur als <c>#rrggbb</c>. Leerer Text heißt: nichts markiert.
    /// </summary>
    public sealed record Copied(string Id, bool OneLine, string Content, IReadOnlyList<CopyRun>? Runs, string? Foreground, string? Background) : WebMessage;

    /// <summary>Stand der Suche im Terminal: Nummer der gezeigten Fundstelle (ab 0, −1 = unbekannt) und ihre Zahl.</summary>
    public sealed record Found(string Id, int Index, int Count) : WebMessage;

    /// <summary>Fundstellen einer Suche über alle Terminals. <see cref="More"/>: Es gab mehr, als gemeldet wurden.</summary>
    public sealed record Hits(string Query, IReadOnlyList<TerminalHit> Items, bool More) : WebMessage;
}

internal sealed record HostMessage(string Type)
{
    public string? Id { get; init; }

    public string? Mark { get; init; }

    public string? State { get; init; }

    public string? Hint { get; init; }

    public bool? Confirmed { get; init; }

    public int? Dim { get; init; }

    public string? Data { get; init; }

    public TreeMessage? Tree { get; init; }

    public string? Zoom { get; init; }

    public bool? Fixed { get; init; }

    public ThemeMessage? Theme { get; init; }

    public int? FontSize { get; init; }

    public string? FontFamily { get; init; }

    public bool? MouseCopyPaste { get; init; }

    public IReadOnlyList<HighlightRule>? Highlights { get; init; }

    public IReadOnlyList<SuggestionMessage>? Suggestions { get; init; }

    public CompletionMessage? Completions { get; init; }

    public IReadOnlyList<PathName>? Names { get; init; }

    public IReadOnlyList<string>? Words { get; init; }

    public IReadOnlyList<string>? Hosts { get; init; }

    public bool? TabCompletes { get; init; }

    public string? LinkHint { get; init; }

    public TerminalLook? Look { get; init; }

    public bool? PasteClean { get; init; }

    public bool? PasteTabs { get; init; }

    public bool? Images { get; init; }

    public IReadOnlyList<TerminalShortcut>? Shortcuts { get; init; }

    public bool? MatchCase { get; init; }

    public bool? Backwards { get; init; }

    public bool? Incremental { get; init; }

    public int? Row { get; init; }
}

/// <summary>Ein Knoten der Teilung für die Seite: ein Blatt (<see cref="Id"/>) oder ein Zweig (<see cref="Dir"/>, <see cref="Shares"/>, <see cref="Items"/>).</summary>
internal sealed record TreeMessage
{
    public string? Id { get; init; }

    public string? Dir { get; init; }

    public IReadOnlyList<double>? Shares { get; init; }

    public IReadOnlyList<TreeMessage>? Items { get; init; }
}

// Die Namen sind die von xterm.js (ITheme).
internal sealed record ThemeMessage(
    string Background,
    string Foreground,
    string Cursor,
    string CursorAccent,
    string SelectionBackground,
    string Black,
    string Red,
    string Green,
    string Yellow,
    string Blue,
    string Magenta,
    string Cyan,
    string White,
    string BrightBlack,
    string BrightRed,
    string BrightGreen,
    string BrightYellow,
    string BrightBlue,
    string BrightMagenta,
    string BrightCyan,
    string BrightWhite);

internal sealed record SuggestionMessage(string Name, string Text);

/// <summary>Was die Seite über die gespeicherten Befehle hinaus ergänzt: Tabellen bekannter Programme und hinter welchen Programmen ein Name aus dem Verzeichnis steht.</summary>
internal sealed record CompletionMessage(IReadOnlyList<CommandTable> Tables, IReadOnlyList<string> PathCommands, IReadOnlyList<string> DirCommands);

internal sealed record RawHit(string? Id, int? Row, string? Text);

// Ein Stück einer kopierten Auswahl: Text, Schrift- und Hintergrundfarbe, Schriftschnitt als Bits (1 fett, 2 kursiv, 4 unterstrichen).
internal sealed record RawRun(string? T, string? F, string? B, int? S);

internal sealed record RawWebMessage(
    string? Type,
    string? Id,
    string? Data,
    int? Cols,
    int? Rows,
    string? Key,
    int? Index,
    int? Count,
    List<RawHit>? Items,
    bool? More,
    List<RawRun>? Runs = null,
    string? Fore = null,
    string? Back = null,
    List<int?>? Path = null,
    List<double?>? Shares = null,
    string? Target = null,
    string? Side = null,
    string? Title = null,
    string? Body = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(RawWebMessage))]
internal sealed partial class TerminalJsonContext : JsonSerializerContext;
