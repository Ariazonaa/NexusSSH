using System.Text.Json;
using System.Text.Json.Serialization;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Settings;

/// <summary>Einstellungen der Oberfläche. Keine Profile, keine Geheimnisse – nur, wie Fenster und Terminal aussehen.</summary>
public sealed class AppSettings
{
    private const int MaxWindowPosition = 100_000;

    public const double MinPaneSize = 180;
    public const double MaxPaneSize = 640;
    public const double DefaultSftpWidth = 300;
    public const double DefaultSnippetsWidth = 280;
    public const double DefaultBottomHeight = 200;
    public const double DefaultTreeHeight = 180;
    public const double MinTreeHeight = 60;
    public const double MaxTreeHeight = 900;

    public AppLanguage Language { get; set; } = AppLanguage.Auto;

    public double SftpWidth { get; set; } = DefaultSftpWidth;

    /// <summary>Breite der Spalte rechts (der Name stammt aus der Zeit, als dort die Server-Info stand).</summary>
    public double InspectorWidth { get; set; } = DefaultSnippetsWidth;

    public double BottomHeight { get; set; } = DefaultBottomHeight;

    public bool ShowSftp { get; set; } = true;

    /// <summary>
    /// Die Spalte rechts mit den gespeicherten Befehlen. Vorher stand dort die Server-Info: Wer sie sah, sieht nun die
    /// Snippets (<see cref="ShowInspector"/>).
    /// </summary>
    public bool ShowSnippets { get; set; } = true;

    /// <summary>
    /// Nur zum Lesen älterer Einstellungen: der Schalter der Server-Info. <see cref="Sanitize"/> übernimmt ihn in
    /// <see cref="ShowSnippets"/>; geschrieben wird er danach nicht mehr.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowInspector { get; set; }

    /// <summary>
    /// Der untere Bereich (Transfers, Ausgabe, Probleme …). Von Haus aus zu, wie im Entwurf: „Transfers n“ und
    /// „Probleme n“ in der Zeile unten blenden ihn ein.
    /// </summary>
    public bool ShowBottomPanel { get; set; }

    public int WindowWidth { get; set; } = 1440;

    public int WindowHeight { get; set; } = 880;

    /// <summary>Linke Kante des normalen Fensters in Pixeln; <c>null</c>: Windows wählt den Platz. Rechnereigen.</summary>
    public int? WindowX { get; set; }

    /// <summary>Obere Kante des normalen Fensters in Pixeln (<see cref="WindowX"/>).</summary>
    public int? WindowY { get; set; }

    /// <summary>Das Fenster war zuletzt maximiert; Position und Größe bleiben die des normalen Fensters.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Kennung des Farbschemas (<see cref="TerminalThemes"/>).</summary>
    public string TerminalTheme { get; set; } = TerminalThemes.DefaultId;

    /// <summary>Bilddatei hinter dem Terminal; leer = keines, der Hintergrund ist die Farbe des Farbschemas.</summary>
    public string TerminalBackgroundImage { get; set; } = string.Empty;

    /// <summary>Wie stark das Bild abgedunkelt wird, in Prozent (<see cref="TerminalBackdrop.MaxDim"/>).</summary>
    public int TerminalBackgroundDim { get; set; } = TerminalBackdrop.DefaultDim;

    /// <summary>Farbschemata, die der Benutzer importiert hat (<see cref="ThemeImport"/>); beim Laden geprüft.</summary>
    public List<TerminalTheme> CustomThemes { get; set; } = [];

    public int TerminalFontSize { get; set; } = TerminalThemes.DefaultFontSize;

    /// <summary>Schrift des Terminals (<see cref="TerminalThemes.Fonts"/> oder eine andere installierte).</summary>
    public string TerminalFont { get; set; } = TerminalThemes.Fonts[0];

    /// <summary>Wie PuTTY und MobaXterm: Markieren kopiert, Rechtsklick fügt ein. Von Haus aus an; wer es ausschaltete, behält das.</summary>
    public bool MouseCopyPaste { get; set; } = true;

    /// <summary>Form des Cursors im Terminal (<see cref="TerminalLook.CursorStyles"/>).</summary>
    public string TerminalCursor { get; set; } = TerminalLook.Block;

    public bool TerminalCursorBlink { get; set; } = true;

    /// <summary>Zeilenabstand im Terminal als Vielfaches der Schrifthöhe (1–2).</summary>
    public double TerminalLineHeight { get; set; } = TerminalLook.MinLineHeight;

    /// <summary>Zusätzlicher Abstand zwischen den Zeichen im Terminal, in Pixeln (0–8).</summary>
    public int TerminalLetterSpacing { get; set; }

    /// <summary>
    /// Ligaturen der Schrift im Terminal zeigen, wenn sie welche hat. An, weil es vor diesem Schalter immer so war –
    /// wer Cascadia Code oder Fira Code gewählt hat, sah sie schon.
    /// </summary>
    public bool TerminalLigatures { get; set; } = true;

    /// <summary>
    /// Eingefügten Text bereinigen: Steuerzeichen, unsichtbare Zeichen und der Zeilenumbruch am Ende fallen weg,
    /// geschützte Leerzeichen werden gewöhnliche (<c>cleanPaste</c> in <c>terminal.js</c>).
    /// </summary>
    public bool PasteClean { get; set; } = true;

    /// <summary>
    /// Bilder im Terminal zeichnen, die der Server schickt (Sixel, das Protokoll von iTerm2). Aus, weil das Lesen
    /// fremder Bilddaten Angriffsfläche ist – einschalten muss es der Benutzer.
    /// </summary>
    public bool TerminalImages { get; set; }

    /// <summary>Beim Einfügen Tabulatoren durch Leerzeichen ersetzen – sonst lösen sie in der Shell die Vervollständigung aus.</summary>
    public bool PasteTabs { get; set; }

    /// <summary>Regeln zum Hervorheben im Terminal, eine je Zeile (<see cref="TerminalHighlights"/>). Leer: keine.</summary>
    public string Highlights { get; set; } = string.Empty;

    /// <summary>Kompakte Darstellung: dichtere Listen, kleinere Rundungen und Abstände. Wirkt nach einem Neustart.</summary>
    public bool Compact { get; set; }

    public const int DefaultUiZoom = 100;
    public const int MinUiZoom = 70;
    public const int MaxUiZoom = 160;
    public const int UiZoomStep = 10;

    /// <summary>
    /// Größe der ganzen Oberfläche in Prozent, neben der Skalierung von Windows. Gehört zu diesem Rechner und
    /// seinem Bildschirm – wandert nicht in den Export.
    /// </summary>
    public int UiZoom { get; set; } = DefaultUiZoom;

    public static int ClampUiZoom(int percent) => Math.Clamp(percent, MinUiZoom, MaxUiZoom);

    /// <summary>Bereiche und Startseite weich einblenden.</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Reißt eine Verbindung ab, baut die Sitzung sie von selbst wieder auf – mit wachsender Wartezeit.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Einen Schlüssel mit Passphrase nur einmal je Start entsperren: Der Kern behält ihn im Speicher, bis die App
    /// endet. Nie auf der Platte – das dauerhafte Speichern der Passphrase ist etwas anderes (Credential Manager).
    /// </summary>
    public bool RememberUnlockedKeys { get; set; } = true;

    /// <summary>Obergrenze für die Bandbreite aller Transfers zusammen, in KiB je Sekunde; 0 = keine.</summary>
    public int TransferLimitKib { get; set; }

    /// <summary>
    /// Muster für Namen und Pfade, die ein Ordner-Transfer und ein Abgleich auslassen
    /// (<see cref="NexusSSH.Core.Services.TransferExcludes"/>). Vor einem Transfer fragt die App, was davon trotzdem mit
    /// soll, wenn im Ordner etwas darauf passt. Leer: Ein Ordner geht immer ganz.
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = [.. NexusSSH.Core.Services.TransferExcludes.Default];

    /// <summary>
    /// Das Display des X-Servers dieses Rechners für X11-Forwarding; 0 ist das Übliche (Port 6000). Gehört zum
    /// Rechner und wandert nicht in den Export.
    /// </summary>
    public int X11Display { get; set; }

    /// <summary>
    /// Alle wie viele Sekunden ein Lebenszeichen an einen Server geht, dessen Profil nichts Eigenes nennt; 0 = keines.
    /// Hält die Verbindung durch Firewalls und Router offen und lässt einen stillen Abriss auffallen.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = DefaultKeepAliveSeconds;

    /// <summary>Nach so vielen unbeantworteten Lebenszeichen gilt eine Verbindung als abgerissen.</summary>
    public int KeepAliveMissed { get; set; } = DefaultKeepAliveMissed;

    /// <summary>
    /// Die Vorhersage beim Tippen in Terminals über Mosh (<see cref="MoshPredictions"/>): <c>adaptive</c> zeigt sie, wenn
    /// die Leitung langsam ist (wie <c>mosh</c> selbst), <c>always</c> immer, <c>never</c> nie. Hängt an der Leitung dieses
    /// Rechners – bleibt hier, nicht im Export und nicht festschreibbar.
    /// </summary>
    public string MoshPredict { get; set; } = DefaultMoshPredict;

    /// <summary>Die Vorgabe von <see cref="MoshPredict"/> – dasselbe nimmt der Kern ohne Angabe.</summary>
    public const string DefaultMoshPredict = "adaptive";

    /// <summary>Die Werte von <see cref="MoshPredict"/>, in der Reihenfolge der Auswahl in den Einstellungen.</summary>
    public static IReadOnlyList<string> MoshPredictions { get; } = [DefaultMoshPredict, "always", "never"];

    /// <summary>
    /// Die Ansicht „Zwei Spalten“ ist eingeschaltet: lokaler Ordner und Server nebeneinander an der Stelle des
    /// Terminals. Standardmäßig aus – der Dateibrowser ist sonst die schmale Leiste links.
    /// </summary>
    public bool DualFiles { get; set; }

    /// <summary>Der lokale Ordner, den die linke Spalte zuletzt zeigte; leer = der Benutzerordner. Bleibt rechnereigen.</summary>
    public string LocalFolder { get; set; } = string.Empty;

    /// <summary>Höhere Displays kennt der Kern nicht.</summary>
    public const int MaxX11Display = 63;

    /// <summary>Was ohne Angabe gilt – dieselben Werte nimmt der Kern von sich aus.</summary>
    public const int DefaultKeepAliveSeconds = 30;

    /// <inheritdoc cref="DefaultKeepAliveSeconds"/>
    public const int DefaultKeepAliveMissed = 3;

    /// <summary>Mehr unbeantwortete Lebenszeichen lassen sich nicht einstellen.</summary>
    public const int MaxKeepAliveMissed = 20;

    /// <summary>Ein Abstand für Lebenszeichen: 0 (keine) oder fünf Sekunden bis eine Stunde – wie im Kern.</summary>
    public static bool IsKeepAliveSeconds(int seconds) => seconds is 0 or (>= 5 and <= 3600);

    /// <summary>Wie viele Lebenszeichen unbeantwortet bleiben dürfen: 1 bis 20 – wie im Kern.</summary>
    public static bool IsKeepAliveMissed(int missed) => missed is >= 1 and <= MaxKeepAliveMissed;

    /// <summary>Mehr als das ist keine Grenze mehr (rund 1 GiB/s).</summary>
    public const int MaxTransferLimitKib = 1_000_000;

    /// <summary>Am Reiter den Titel zeigen, den der Server setzt (OSC 0/2), statt des Namens des Profils.</summary>
    public bool ServerTabTitles { get; set; }

    /// <summary>
    /// Die Leiste unter dem Terminal misst die Auslastung des Servers und zeigt sie (CPU, Speicher, Netz, Platte,
    /// Benutzer, Laufzeit) – wie bei MobaXterm. Aus wird für sie nicht gemessen; das Kästchen bleibt.
    /// </summary>
    public bool MonitorBar { get; set; } = true;

    /// <summary>Das Terminal schlägt beim Tippen gespeicherte Befehle vor.</summary>
    public bool SnippetSuggestions { get; set; } = true;

    /// <summary>
    /// Das Terminal ergänzt beim Tippen Unterbefehle und Schalter bekannter Programme (<c>git</c>, <c>docker</c> …)
    /// aus mitgelieferten Tabellen. Fragt niemanden.
    /// </summary>
    public bool OptionSuggestions { get; set; } = true;

    /// <summary>
    /// Das Terminal ergänzt beim Tippen Namen aus dem Verzeichnis der Shell. Dafür liest die App dieses Verzeichnis
    /// über die Dateiverbindung – einmal beim Verbinden und wenn die Shell ein anderes meldet.
    /// </summary>
    public bool PathSuggestions { get; set; } = true;

    /// <summary>
    /// Das Terminal ergänzt als erstes Wort einer Zeile die Programme des Servers. Dafür fragt die App nach jedem
    /// Verbinden einmal mit einem festen Befehl nach den Namen im Suchpfad.
    /// </summary>
    public bool CommandSuggestions { get; set; } = true;

    /// <summary>
    /// Das Terminal ergänzt Namen, die die App schon kennt: Dienste und Container eines Servers, sobald ihre Seite
    /// einmal offen war, und die Adressen der gespeicherten Server hinter <c>ssh</c>. Fragt niemanden.
    /// </summary>
    public bool KnownNameSuggestions { get; set; } = true;

    /// <summary>
    /// Tab nimmt im Terminal auch eine Ergänzung an, nicht nur einen gespeicherten Befehl. Aus: Tab bleibt dort der
    /// Shell und ihrer eigenen Vervollständigung, die Pfeiltaste nach rechts nimmt an.
    /// </summary>
    public bool TabCompletes { get; set; }

    /// <summary>Jeder beendete Transfer bekommt eine Zeile im Transfer-Protokoll (<c>logs\transfers.csv</c>). Aus, solange es niemand einschaltet.</summary>
    public bool TransferLog { get; set; }

    /// <summary>Hinweise von Windows, wenn im Hintergrund ein Transfer fertig wird oder die Glocke läutet.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>Die Glocke des Terminals (BEL) spielt den Hinweiston von Windows. Aus, solange es niemand einschaltet.</summary>
    public bool BellSound { get; set; }

    /// <summary>Die Glocke kennzeichnet den Reiter, bis er angesehen wird.</summary>
    public bool BellTab { get; set; } = true;

    /// <summary>Die Glocke lässt die Taskleiste blinken, wenn das Fenster nicht vorn ist.</summary>
    public bool BellTaskbar { get; set; }

    /// <summary>Meldungen von Programmen im Terminal (OSC 9, OSC 777) kennzeichnen den Reiter und erscheinen als Hinweis von Windows.</summary>
    public bool TerminalNotices { get; set; } = true;

    /// <summary>Ton, wenn ein Server nicht zu erreichen ist. Die Töne zu Fehlern sind von Haus aus an.</summary>
    public bool SoundConnectFailed { get; set; } = true;

    /// <summary>Ton, wenn ein Server die Anmeldung ablehnt.</summary>
    public bool SoundAuthFailed { get; set; } = true;

    /// <summary>Ton, wenn eine stehende Verbindung abreißt.</summary>
    public bool SoundConnectionLost { get; set; } = true;

    /// <summary>Ton, wenn ein Transfer scheitert.</summary>
    public bool SoundTransferFailed { get; set; } = true;

    /// <summary>Ton, wenn eine Verbindung steht. Aus, solange es niemand einschaltet.</summary>
    public bool SoundConnected { get; set; }

    /// <summary>Ton, wenn ein Transfer fertig ist. Aus, solange es niemand einschaltet.</summary>
    public bool SoundTransferDone { get; set; }

    /// <summary>Die Töne zu Ereignissen nur spielen, wenn das Fenster nicht vorn ist.</summary>
    public bool SoundsOnlyInBackground { get; set; }

    /// <summary>
    /// Melden, wenn bei einer offenen Sitzung Platte, Speicher oder Last über der Schwelle liegen. Aus, solange es
    /// niemand einschaltet: Dafür misst die App bei allen offenen Sitzungen im Hintergrund, nicht nur beim Tab,
    /// dessen Werte zu sehen sind.
    /// </summary>
    public bool ServerAlerts { get; set; }

    public const int MinAlertPercent = 50;
    public const int MaxAlertPercent = 99;
    public const double MinAlertLoad = 0.5;
    public const double MaxAlertLoad = 50;

    /// <summary>Belegung eines Dateisystems (Platz oder Inodes) in Prozent, ab der gemeldet wird.</summary>
    public int AlertDiskPercent { get; set; } = 90;

    /// <summary>Belegter Arbeitsspeicher in Prozent, ab dem gemeldet wird.</summary>
    public int AlertMemoryPercent { get; set; } = 95;

    /// <summary>Lastdurchschnitt der letzten Minute je Kern, ab dem gemeldet wird.</summary>
    public double AlertLoadPerCore { get; set; } = 2;

    /// <summary>
    /// Die Startseite zeigt, welche Server auf ihrem Port antworten. Aus, solange es niemand einschaltet: Die App
    /// baut dafür von sich aus Verbindungen auf, die der Server sieht.
    /// </summary>
    public bool CheckReachability { get; set; }

    /// <summary>So viele beendete Verbindungen behält der Verlauf.</summary>
    public const int MaxHistory = 200;

    /// <summary>Verlauf der Verbindungen, die jüngste zuerst. Bleibt rechnereigen.</summary>
    public List<ConnectionRecord> History { get; set; } = [];

    /// <summary>Ob der Dateibrowser Einträge zeigt, deren Name mit einem Punkt beginnt.</summary>
    public bool ShowHiddenFiles { get; set; } = true;

    /// <summary>Ob der Dateibrowser über der Liste den Baum der Verzeichnisse zeigt. Standardmäßig aus: Der
    /// Browser ist eine einzelne Liste, der Baum ist eine abschaltbare Zugabe.</summary>
    public bool ShowDirectoryTree { get; set; }

    /// <summary>Höhe des Baums über der Liste.</summary>
    public double TreeHeight { get; set; } = DefaultTreeHeight;

    /// <summary>Wohin zuletzt heruntergeladen wurde – der Ordnerdialog beginnt dort.</summary>
    public string LastDownloadFolder { get; set; } = string.Empty;

    public ProfileSort ProfileSort { get; set; } = ProfileSort.Saved;

    /// <summary>Die Startseite zeigt die Server als Tabelle statt als Kacheln.</summary>
    public bool HomeAsTable { get; set; }

    /// <summary>Spalte, nach der die Tabelle der Startseite sortiert ist.</summary>
    public HomeTableSort HomeTableSort { get; set; } = HomeTableSort.Name;

    public bool HomeTableDescending { get; set; }

    /// <summary>
    /// Die Reihenfolge der Gruppen auf der Startseite, soweit der Benutzer sie festgelegt hat: ganze Pfade
    /// (<see cref="GroupPath"/>). Was hier fehlt, steht dahinter, in der Reihenfolge der Profile.
    /// </summary>
    public List<string> GroupOrder { get; set; } = [];

    /// <summary>Die eingeklappten Abschnitte der Startseite (<c>ProfileGroup.Key</c>).</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>
    /// Kennfarbe je Gruppe (Pfad der Gruppe → Kennung aus <see cref="ProfileColors"/>). Sie gilt für alle Profile in
    /// der Gruppe und darunter, die keine eigene Farbe haben.
    /// </summary>
    public Dictionary<string, string> GroupColors { get; set; } = [];

    /// <summary>
    /// Eigenes Zeichen je Gruppe (Pfad der Gruppe → <see cref="TileMarks"/>): Symbol oder Buchstaben auf den Kacheln
    /// aller Server in der Gruppe und darunter, die kein eigenes haben.
    /// </summary>
    public Dictionary<string, string> GroupMarks { get; set; } = [];

    /// <summary>Eigenes Zeichen je Server (Kennung des Profils → <see cref="TileMarks"/>).</summary>
    public Dictionary<string, string> ServerMarks { get; set; } = [];

    /// <summary>Wonach die Dateiliste sortiert ist.</summary>
    public FileSort FileSort { get; set; } = FileSort.Name;

    /// <summary>Dateiliste in umgekehrter Reihenfolge (Z–A, groß zuerst, neu zuerst).</summary>
    public bool FileSortDescending { get; set; }

    /// <summary>
    /// Nur noch zum Einlesen einer Datei aus der Zeit vor den Spalten: Die Dateiliste zeigte das Änderungsdatum als
    /// Spalte. Jetzt gehört das zu <see cref="RemoteColumns"/>; <see cref="Sanitize"/> übernimmt den Wert dorthin.
    /// </summary>
    public bool ShowFileDates { get; set; }

    /// <summary>Wie <see cref="ShowFileDates"/>, für die Zugriffsrechte.</summary>
    public bool ShowFilePermissions { get; set; }

    /// <summary>
    /// Die Spalten der Dateiliste des Servers: welche zu sehen sind und wie breit. Rechnereigen, nicht im Export.
    /// Ohne Wert (eine ältere Datei) gelten die alten Schalter, danach die Vorgabe (<see cref="FileColumns.Clean(ColumnSet?, bool)"/>).
    /// </summary>
    public ColumnSet? RemoteColumns { get; set; }

    /// <summary>Die Spalten der lokalen Liste in der Ansicht „Zwei Spalten“ – ohne Besitzer, Gruppe und Rechte.</summary>
    public ColumnSet? LocalColumns { get; set; }

    /// <summary>Wonach die lokale Liste sortiert ist – unabhängig von der des Servers daneben.</summary>
    public FileSort LocalSort { get; set; } = FileSort.Name;

    /// <summary>Die lokale Liste in umgekehrter Reihenfolge.</summary>
    public bool LocalSortDescending { get; set; }

    /// <summary>Editor für „Datei bearbeiten“; leer = der Editor von Windows.</summary>
    public string EditorPath { get; set; } = string.Empty;

    /// <summary>
    /// Die Shell, die ein neues lokales Terminal öffnet (Strg+Umschalt+T): die Kennung, wie der Kern sie nennt
    /// (<c>pwsh</c>, <c>wsl:Ubuntu</c>, <c>login</c> …); leer = die erste, die der Kern fand. Gehört zu diesem Rechner.
    /// </summary>
    public string LocalShell { get; set; } = string.Empty;

    /// <summary>Programm für „Vergleichen“ (WinMerge, VS Code …); leer = keines. Gehört zu diesem Rechner.</summary>
    public string ComparePath { get; set; } = string.Empty;

    /// <summary>Gespeicherte Befehle. Sie stehen im Klartext in <c>settings.json</c> – Passwörter gehören nicht hinein.</summary>
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>Die eingeklappten Ordner der Snippets (<see cref="Snippet.Folder"/>). Gehört zu diesem Rechner.</summary>
    public List<string> CollapsedSnippetFolders { get; set; } = [];

    /// <summary>Identitäten: Benutzer und Schlüssel, die mehrere Profile teilen. Keine Geheimnisse.</summary>
    public List<Identity> Identities { get; set; } = [];

    /// <summary>
    /// Vom Benutzer geänderte Tastenkürzel: Kennung des Befehls → Kürzel (<c>Shift+KeyK</c>, <c>F5</c>; Strg gehört
    /// immer dazu), leer = der Befehl hat keines. Was hier nicht steht, hat sein vorgegebenes Kürzel.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    /// <summary>
    /// Lesezeichen für Ordner am Server: Kennung des Profils → Pfade, in der Reihenfolge, in der sie gemerkt wurden.
    /// Bleibt rechnereigen (die Kennungen gelten nur hier).
    /// </summary>
    public Dictionary<string, List<string>> Bookmarks { get; set; } = [];

    /// <summary>
    /// Je Profil (Kennung), was nach dem Verbinden mit laufenden tmux-/screen-Sitzungen geschieht: anbieten oder
    /// von selbst an eine bestimmte anhängen (<see cref="NexusSSH.Core.Models.MuxSteps"/>). Rechnereigen.
    /// </summary>
    public Dictionary<string, string> MuxSteps { get; set; } = [];

    /// <summary>Beim Start die Tabs wieder öffnen, die beim Beenden offen waren – samt Teilungen.</summary>
    public bool RestoreTabs { get; set; }

    /// <summary>
    /// Vor dem Beenden fragen, wenn Sitzungen verbunden sind oder Transfers laufen. An, solange es niemand abstellt:
    /// Ein Klick auf das X trennt sonst alles ohne ein Wort.
    /// </summary>
    public bool ConfirmClose { get; set; } = true;

    /// <summary>
    /// Was geschieht, wenn es am Ziel eines Transfers schon etwas mit demselben Namen gibt: fragen (die Vorgabe)
    /// oder die Regel, die der Benutzer sich gemerkt hat.
    /// </summary>
    public ExistingRule TransferRule { get; set; } = ExistingRule.Ask;

    /// <summary>Beim Start prüfen, ob eine neuere Version vorliegt (eine Anfrage an GitHub, höchstens 1×/Tag).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Kennung der Akzentfarbe (<see cref="AccentColors"/>); wirkt nach einem Neustart.</summary>
    public string Accent { get; set; } = AccentColors.DefaultId;

    /// <summary>Wann zuletzt nach Updates gesucht wurde (ISO 8601, UTC); leer = noch nie. Bleibt rechnereigen.</summary>
    public string LastUpdateCheck { get; set; } = string.Empty;

    /// <summary>
    /// Die Version der App, die hier zuletzt gelaufen ist. Ist die laufende neuer, zeigt die App einmal, was neu
    /// ist; leer heißt: erster Start.
    /// </summary>
    public string LastSeenVersion { get; set; } = string.Empty;

    /// <summary>Die Profile der zuletzt offenen Tabs, in ihrer Reihenfolge. Nur gefüllt, wenn <see cref="RestoreTabs"/> an ist.</summary>
    public List<string> OpenTabs { get; set; } = [];

    /// <summary>Welche dieser Tabs sich die Terminal-Fläche geteilt haben.</summary>
    public List<SavedSplit> OpenSplits { get; set; } = [];

    /// <summary>Welche der Tabs in <see cref="OpenTabs"/> angeheftet waren, als Nummern darin.</summary>
    public List<int> PinnedTabs { get; set; } = [];

    /// <summary>Welche der Tabs in <see cref="OpenTabs"/> ihr Terminal in einem eigenen Fenster hatten, und wo es stand.</summary>
    public List<SavedWindow> DetachedTabs { get; set; } = [];

    /// <summary>Nummer des zuletzt aktiven Tabs in <see cref="OpenTabs"/>; −1 = keiner.</summary>
    public int ActiveTab { get; set; } = -1;

    /// <summary>Gemerkte Abgleiche: lokaler Ordner und Ordner am Server samt Richtung. Rechnereigen, nicht im Export.</summary>
    public List<SavedSync> SavedSyncs { get; set; } = [];

    /// <summary>Ob KI-Programme die App über MCP erreichen. Rechnereigen, nie aus einer Datei.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>
    /// Die Server, die KI-Programme erreichen: je Eintrag Profil samt Ziel (<c>ShellViewModel.McpTarget</c>) – zeigt ein
    /// Profil woandershin, gilt die Freigabe nicht mehr. Rechnereigen, nie aus einer Datei.
    /// </summary>
    public List<string> McpServers { get; set; } = [];

    /// <summary>Holt von Hand bearbeitete oder beschädigte Werte in den erlaubten Bereich zurück.</summary>
    public AppSettings Sanitize()
    {
        TransferRule = Enum.IsDefined(TransferRule) ? TransferRule : ExistingRule.Ask;
        SftpWidth = Pane(SftpWidth, DefaultSftpWidth);
        AlertDiskPercent = Math.Clamp(AlertDiskPercent, MinAlertPercent, MaxAlertPercent);
        AlertMemoryPercent = Math.Clamp(AlertMemoryPercent, MinAlertPercent, MaxAlertPercent);
        AlertLoadPerCore = double.IsFinite(AlertLoadPerCore) ? Math.Clamp(AlertLoadPerCore, MinAlertLoad, MaxAlertLoad) : 2;
        InspectorWidth = Pane(InspectorWidth, DefaultSnippetsWidth);
        if (ShowInspector is { } inspector)
        {
            ShowSnippets = inspector;
            ShowInspector = null;
        }

        BottomHeight = Pane(BottomHeight, DefaultBottomHeight);
        TreeHeight = double.IsFinite(TreeHeight) ? Math.Clamp(TreeHeight, MinTreeHeight, MaxTreeHeight) : DefaultTreeHeight;
        WindowWidth = Math.Clamp(WindowWidth, 900, 10_000);
        WindowHeight = Math.Clamp(WindowHeight, 560, 10_000);
        if (WindowX is not (>= -MaxWindowPosition and <= MaxWindowPosition) || WindowY is not (>= -MaxWindowPosition and <= MaxWindowPosition))
        {
            (WindowX, WindowY) = (null, null);
        }

        // Die Datei kann von Hand geändert sein: Ein eigenes Schema gilt nur, wenn jede seiner Farben eine ist.
        CustomThemes = [.. (CustomThemes ?? []).Where(ThemeImport.IsValid).DistinctBy(theme => theme.Id).Take(TerminalThemes.MaxCustom)];
        TerminalTheme = TerminalThemes.Find(TerminalTheme, CustomThemes).Id;
        Accent = AccentColors.Find(Accent).Id;
        TerminalFont = TerminalThemes.Font(TerminalFont);
        Highlights = TerminalHighlights.Limit(Highlights);
        TerminalFontSize = Math.Clamp(TerminalFontSize, TerminalThemes.MinFontSize, TerminalThemes.MaxFontSize);
        UiZoom = ClampUiZoom(UiZoom);
        TerminalCursor = TerminalLook.Cursor(TerminalCursor);
        TerminalBackgroundImage ??= string.Empty;
        TerminalBackgroundDim = TerminalBackdrop.Dim(TerminalBackgroundDim);
        TerminalLineHeight = TerminalLook.Height(TerminalLineHeight);
        TerminalLetterSpacing = TerminalLook.Spacing(TerminalLetterSpacing);
        if (!Enum.IsDefined(Language))
        {
            Language = AppLanguage.Auto;
        }

        FileSort = FileColumns.Clean(FileSort, remote: true);
        LocalSort = FileColumns.Clean(LocalSort, remote: false);

        // Eine Datei aus der Zeit vor den Spalten kennt nur die beiden Schalter; sie gelten einmal und sind danach verbraucht.
        RemoteColumns ??= FileColumns.FromSwitches(ShowFileDates, ShowFilePermissions);
        (ShowFileDates, ShowFilePermissions) = (false, false);
        RemoteColumns = FileColumns.Clean(RemoteColumns, remote: true);
        LocalColumns = FileColumns.Clean(LocalColumns, remote: false);

        if (!Enum.IsDefined(ProfileSort))
        {
            ProfileSort = ProfileSort.Saved;
        }

        if (!Enum.IsDefined(HomeTableSort))
        {
            HomeTableSort = HomeTableSort.Name;
        }

        LastDownloadFolder ??= string.Empty;
        LocalFolder ??= string.Empty;
        History = [.. (History ?? []).Where(record => record is { Server.Length: > 0 }).Take(MaxHistory)];
        EditorPath ??= string.Empty;
        LocalShell = Services.LocalShellNames.IsId(LocalShell) ? LocalShell : string.Empty;
        ComparePath ??= string.Empty;
        Snippets = [.. (Snippets ?? []).Where(snippet => snippet is { Name.Length: > 0, Text.Length: > 0 }).Select(SnippetFolders.Cleaned)];
        CollapsedSnippetFolders = [.. (CollapsedSnippetFolders ?? []).Where(folder => folder is { Length: > 0 } && folder == SnippetFolders.Clean(folder)).Distinct().Take(500)];
        Shortcuts = (Shortcuts ?? []).Where(entry => entry.Key is { Length: > 0 and <= 64 } && entry.Value is { Length: <= 32 }).Take(500).ToDictionary(entry => entry.Key, entry => entry.Value);
        Bookmarks = FolderBookmarks.Sanitize(Bookmarks);
        MuxSteps = NexusSSH.Core.Models.MuxSteps.Sanitize(MuxSteps);
        TransferLimitKib = Math.Clamp(TransferLimitKib, 0, MaxTransferLimitKib);
        ExcludePatterns = NexusSSH.Core.Services.TransferExcludes.Clean(ExcludePatterns);
        X11Display = Math.Clamp(X11Display, 0, MaxX11Display);
        KeepAliveSeconds = IsKeepAliveSeconds(KeepAliveSeconds) ? KeepAliveSeconds : DefaultKeepAliveSeconds;
        KeepAliveMissed = IsKeepAliveMissed(KeepAliveMissed) ? KeepAliveMissed : DefaultKeepAliveMissed;
        MoshPredict = MoshPredict is { } predict && MoshPredictions.Contains(predict) ? predict : DefaultMoshPredict;
        GroupOrder = [.. (GroupOrder ?? []).Where(path => path is { Length: > 0 and <= 512 }).Distinct().Take(2000)];
        GroupColors = (GroupColors ?? [])
            .Where(entry => entry.Key is { Length: > 0 and <= 512 } && ProfileColors.Find(entry.Value).Id.Length > 0)
            .Take(2000)
            .ToDictionary(entry => entry.Key, entry => ProfileColors.Find(entry.Value).Id);
        GroupMarks = Marks(GroupMarks);
        ServerMarks = Marks(ServerMarks);
        CollapsedGroups = [.. (CollapsedGroups ?? []).Where(key => key is { Length: > 0 and <= 512 }).Distinct().Take(2000)];
        Identities = [.. (Identities ?? []).Where(identity => identity is { Id.Length: > 0, Name.Length: > 0 } && Identity.IsValidUser(identity.User)).DistinctBy(identity => identity.Id)];
        McpServers = [.. (McpServers ?? []).Where(target => target is { Length: > 0 }).Distinct(StringComparer.Ordinal)];
        OpenTabs = [.. (OpenTabs ?? []).Where(id => !string.IsNullOrEmpty(id))];
        PinnedTabs = [.. (PinnedTabs ?? []).Where(index => index >= 0 && index < OpenTabs.Count).Distinct().Order()];
        OpenSplits = [.. (OpenSplits ?? []).Where(split => split is { Tabs.Count: >= 2 }).Select(split => split.Cleaned())];
        SavedSyncs =
        [
            .. (SavedSyncs ?? [])
                .Where(sync => sync is { IsUsable: true })
                .DistinctBy(sync => sync.Id)
                .Take(SavedSync.MaxCount)
                .Select(sync => sync with { Name = sync.Name.Trim(), Exclude = NexusSSH.Core.Services.TransferExcludes.Clean(sync.Exclude) }),
        ];
        DetachedTabs =
        [
            .. (DetachedTabs ?? [])
                .Where(window => window is not null && window.Tab >= 0 && window.Tab < OpenTabs.Count)
                .DistinctBy(window => window.Tab)
                .Select(window => window.Bounds is { IsUsable: true } ? window : window with { Bounds = null }),
        ];

        return this;
    }

    private static Dictionary<string, string> Marks(Dictionary<string, string>? marks)
        => (marks ?? [])
            .Where(entry => entry.Key is { Length: > 0 and <= 512 } && TileMarks.Normalize(entry.Value) is not null)
            .Take(2000)
            .ToDictionary(entry => entry.Key, entry => TileMarks.Normalize(entry.Value)!);

    private static double Pane(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, MinPaneSize, MaxPaneSize) : fallback;
}

/// <summary>
/// Eine beendete Verbindung im Verlauf: Server, Adresse, Beginn, Dauer und wie sie endete (ein Satz wie in der
/// Hinweisleiste). Nichts von dem, was in der Sitzung geschah.
/// </summary>
public sealed record ConnectionRecord(string Server, string Address, DateTimeOffset Started, int Seconds, string End)
{
    /// <summary>Server, Beginn und Ende – Listen lesen das Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => $"{Server}, {Format.DateAndTime(Started)}, {End}";
}

/// <summary>Wonach eine Dateiliste sortiert ist (die Spalten, <see cref="FileColumn"/>); Ordner stehen vor den Dateien, nur nach der Art stehen Ordner-Verweise bei den Verweisen.</summary>
public enum FileSort
{
    Name,
    Size,
    Modified,
    Permissions,
    Owner,
    Group,
    Type,
}

/// <summary>
/// Die Spalten einer Dateiliste: welche zu sehen sind und wie breit sie sind (<see cref="AppSettings.RemoteColumns"/>,
/// <see cref="AppSettings.LocalColumns"/>). Die Spalten stehen mit dem Namen aus <see cref="FileColumn"/> darin, damit
/// die Datei lesbar bleibt und eine Spalte, die diese Version nicht kennt, einfach wegfällt.
/// </summary>
/// <param name="Visible">Die zuschaltbaren Spalten, die zu sehen sind. Der Name steht nie darin: Er ist immer zu sehen.</param>
/// <param name="Widths">Die Breite der Spalten, die der Benutzer verstellt hat, in Pixeln.</param>
public sealed record ColumnSet(List<string> Visible, Dictionary<string, double> Widths);

/// <summary>Die Spalten der Tabelle auf der Startseite, nach denen sich sortieren lässt.</summary>
public enum HomeTableSort
{
    Name,
    Address,
    Group,
    Recent,
}

/// <summary>Reihenfolge der Profile innerhalb einer Gruppe; Favoriten stehen immer vorn.</summary>
public enum ProfileSort
{
    /// <summary>Wie gespeichert – in der Reihenfolge, in der sie angelegt wurden.</summary>
    Saved,
    Name,

    /// <summary>Zuletzt verbundene zuerst.</summary>
    Recent,
}


/// <summary>Lage und Größe eines Fensters in Pixeln des Bildschirms.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>Ob das eine brauchbare Größe ist; wo das Fenster stehen darf, prüft die App an den Bildschirmen.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable => Width is >= 200 and <= 20_000 && Height is >= 120 and <= 20_000 && Math.Abs((long)X) <= 100_000 && Math.Abs((long)Y) <= 100_000;
}

/// <summary>Ein Tab aus <see cref="AppSettings.OpenTabs"/>, dessen Terminal in einem eigenen Fenster stand.</summary>
/// <param name="Tab">Nummer des Tabs in der Liste.</param>
public sealed record SavedWindow(int Tab, WindowBounds? Bounds);

/// <summary>
/// Ein gespeicherter Befehl. <see cref="Run"/>: gleich mit Enter abschicken statt nur eintippen. <see cref="Folder"/>:
/// der Ordner, in dem er steht – leer heißt ohne Ordner (<see cref="SnippetFolders"/>).
/// </summary>
public sealed record Snippet(string Name, string Text, bool Run, string Folder = "")
{
    public override string ToString() => Name;
}

/// <summary>Liest und schreibt <c>settings.json</c>. Eine fehlende oder kaputte Datei ergibt die Standardwerte.</summary>
public sealed class SettingsStore(string path)
{
    public AppSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            return (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings()).Sanitize();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Schreibt erst in eine Nachbardatei und tauscht dann – ein Absturz mitten im Schreiben lässt die alte Datei heil.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
