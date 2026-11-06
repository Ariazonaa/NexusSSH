# Architektur

Stand: Phase 3 (Spec §13) – SSH-Verbindungen mit Terminal (auch über Sprung-Server), Serverprofile samt Import aus
`~/.ssh/config`, Host-Schlüssel-Prüfung, Anmeldung per Schlüssel, Agent, Passwort oder Rückfragen des Servers,
Agent Forwarding, SFTP-Dateibrowser mit Up- und Download, Server-Auslastung, geteiltes Terminal, Suche,
Farbschemata und Befehlspalette.

## 1. Überblick

```
┌──────────────────────── NexusSSH.exe (WinUI 3, C#) ────────────────────────┐
│                                                                            │
│  NexusSSH.App            Fenster, Ansichten, Dialoge, WebView2 fürs Terminal│
│       │                                                                    │
│       ▼                                                                    │
│  NexusSSH.Core           ViewModels ── Dienst-Schnittstellen (INexusBackend)│
│                                             │                              │
│                               CoreBackend ──┤── MockBackend (Tests, --demo)│
│                                             │ JSON + Bytes über die C-ABI  │
│                                             ▼                              │
│  nexus_core.dll (Rust)   SSH-Sitzungen, Profile, Host-Schlüssel, Geheimnisse│
└────────────────────────────────────────────────────────────────────────────┘
```

| Teil | Sprache | Aufgabe |
| --- | --- | --- |
| `frontend/NexusSSH.App` | C#, WinUI 3 | Fenster, XAML-Ansichten, Dialoge, WebView2-Hülle |
| `frontend/NexusSSH.Core` | C#, `net10.0` ohne Windows | Modelle, Dienst-Schnittstellen, `CoreBackend` samt Anbindung der Rust-Bibliothek, Mock-Backend, ViewModels, Befehle, Einstellungen, Übersetzungen |
| `frontend/NexusSSH.Core.Tests` | C#, xunit.v3 | Tests für alles in Core |
| `backend/nexus-core` | Rust | Der Kern. Alles mit Netz und Geheimnissen. |
| `backend/nexus-testserver` | Rust | Kleiner SSH-Server mit nachgespielter Shell – Gegenstelle für Tests und zum Ausprobieren |

**Warum diese Teilung:** WinUI 3 gibt es nur für Windows, eine Linux-Version ist aber denkbar (§9). Deshalb
liegt so wenig wie möglich im WinUI-Projekt: Protokolle und Geheimnisse in Rust, Bedienlogik im plattformfreien
`NexusSSH.Core`, im App-Projekt nur XAML, Dialoge und die WebView2-Hülle.

## 2. Die Naht: `INexusBackend`

`NexusSSH.Core/Services/Backend.cs` beschreibt alles, was die Oberfläche vom Backend braucht:

- `IProfileStore` – Serverprofile lesen, speichern, löschen; Server aus `~/.ssh/config` auflisten und übernehmen
- `IServerConnection` – eine Verbindung: Zustand (`ConnectionStatus`), Rückfragen (`ConnectionPrompt`),
  `ITerminalSession`, dazu `IRemoteFileSystem?` (`null` ohne Verbindung oder ohne SFTP auf dem Server) und
  `IServerMonitor?` (`null` ohne Verbindung)
- `IRemoteFileSystem` – auflisten, Ordner anlegen, umbenennen, löschen; Up- und Downloads stellt es nur in die
  Warteschlange
- `IServerMonitor` – Auslastung des Servers; misst nur, solange `SetActive(true)` gilt
- `ITransferQueue` – alle Transfers aller Sitzungen, mit Abbrechen und Aufräumen
- `IUserPrompts` – zeigt Rückfragen an; die App setzt Dialoge ein, Tests eine Attrappe
- `IUiDispatcher` – bringt Ereignisse aus Hintergrund-Threads auf den UI-Thread

ViewModels kennen nur diese Schnittstellen. `App.OnLaunched` setzt `CoreBackend` ein, mit `--demo` das
`MockBackend`.

Terminal-Ein- und -Ausgabe sind **Bytes**, nicht Strings: SSH liefert Bytes, und ein UTF-8-Zeichen kann über zwei
Blöcke verteilt ankommen.

## 3. Zustand der Oberfläche

`ShellViewModel` hält die Liste der Sitzungen und die **aktive Sitzung**. Der gewählte Terminal-Tab bestimmt,
welchen Server SFTP-Leiste und Inspector zeigen.

Eine `SessionViewModel` ist der Tab, nicht die Verbindung: Sie überlebt einen Abriss, behält den Terminal-Inhalt
und hängt bei „Erneut verbinden“ eine neue `IServerConnection` an. Für das Terminal ist sie selbst die
`ITerminalSession` – der WebView merkt vom Wechsel der Verbindung nichts. Sie schreibt eigene Statuszeilen
(„Verbinde mit …“, „Verbindung verloren.“) gedimmt ins Terminal und reicht Rückfragen an `IUserPrompts` weiter.
Endet die Verbindung, während ein Dialog offen ist, wird er geschlossen und nicht mehr beantwortet. Lässt sich
ein Dialog nicht anzeigen, gilt die Frage als abgelehnt – „nicht gefragt“ heißt nie „zugestimmt“.

### Dateibrowser und Verzeichnisbaum (Spec §4)

`SftpBrowserViewModel` zeigt ein Verzeichnis als Liste (Verlauf, Brotkrumen, Pfad eintippen); darüber steht der
Baum der Verzeichnisse (`SftpBrowserViewModel.Tree.cs`, Knoten `DirectoryNode`). Der Baum hat keinen eigenen
Zustand der Navigation – er folgt der Liste: Nach jedem Laden klappt er bis zum angezeigten Verzeichnis auf
(`RevealAsync`, lädt dafür die Verzeichnisse darüber, die er noch nicht kennt) und markiert es (`CurrentNode`);
ein Klick auf einen Knoten ruft dieselbe Navigation wie die Liste. Was die Liste über ein Verzeichnis weiß, gilt
auch für seinen Knoten – so stimmt er nach „Neuer Ordner“, Umbenennen und Löschen ohne eigene Anfrage.
Unterverzeichnisse lädt er erst beim Aufklappen. Ein Verzeichnis auf dem Weg, das sich nicht auflisten lässt
(`/home` ohne Leserecht), bekommt nur den Knoten, der weiterführt; versteckte folgen „Versteckte Dateien“, außer
das angezeigte Verzeichnis liegt in einem. Ist der Baum ausgeblendet (`ShowTree`), fragt er den Server nichts.
Die Markierung setzt die Oberfläche von Hand (`x:Bind` an `SelectedItem` trägt nicht) und rollt danach zum Knoten.

### Geteiltes Terminal (Spec §5)

Bis zu sechzehn Sitzungen (`SplitTree.MaxPanes`) können sich die Terminal-Fläche teilen, in jeder Anordnung. Die
Anordnung ist ein Baum (`Terminals/SplitTree.cs`): `SplitNode<T>` ist ein Blatt (`SplitLeaf`, eine Sitzung) oder
ein Zweig (`SplitBranch`: Richtung nebeneinander oder untereinander, Kinder, ihre Anteile am Platz – Summe 1,
keiner unter `MinShare`). Der Baum ist unveränderlich; `SplitTree` baut aus ihm neue (einfügen, entfernen –
ein Zweig mit einem Kind fällt weg, gleiche Richtung wie der Elternzweig verschmilzt –, umstellen an eine Kante,
Anteile setzen, gleichmäßig verteilen) und findet den Nachbarn in einer Richtung über die Rechtecke im
Einheitsquadrat. Tiefe und Zahl der Bereiche sind begrenzt (`MaxDepth`, `MaxPanes`); was darüber hinausgeht, ändert
nichts. Jede Sitzung bleibt ein eigener Tab; `ShellViewModel` merkt sich nur die Gruppen (`SplitGroup`: Baum, der
groß geschaltete Bereich, ob die Anordnung feststeht). `ShellViewModel.Layout` (`TerminalLayout`: Baum, aktive
Sitzung, groß geschaltete, `Fixed`) sagt, was gerade zu sehen ist: die aktive Sitzung allein oder ihre Gruppe – in
derselben Anordnung, egal welche Sitzung aktiv ist. „Teilen“ öffnet eine weitere Sitzung zum selben Server und
teilt den aktiven Bereich in der gewählten Richtung; „Teilen mit …“ stellt einen offenen Tab dazu. Wer die Gruppe
verlässt oder schließt, lässt die übrigen zusammen (aktiv wird ein anderer Bereich derselben Teilung); bleibt eine
allein, ist die Teilung aufgehoben. Groß schalten (`ToggleZoom`) endet beim Teilen, Umstellen, beim Wechsel des
Fokus per Tastatur und wenn der vergrößerte Bereich geht; es wird nicht gespeichert. Die Bereiche eines Fensters von tmux
(`Fixed`) lassen sich weder umstellen noch groß schalten – ihre Anordnung gehört tmux, nur die Anteile an der
Fläche gehören der App.

Anteile und Umstellungen kommen von der Seite zurück (`shares`, `move`, siehe §4) und werden geprüft, bevor sie
den Baum ändern (`SetShares`, `MovePane`): eine Meldung zu einer Teilung, die nicht mehr die gezeigte ist, ändert
nichts.

Mit der Einstellung „Tabs beim Start wieder öffnen“ merkt sich die App beim Beenden die offenen Tabs, ihre
Teilungen samt Anordnung und Anteilen und den aktiven Tab (`RememberOpenSessions`, in `settings.json` nur
Profil-Kennungen; `SavedSplit` trägt die Nummern der Tabs und den Baum) und öffnet sie beim nächsten Start wieder.
Eine ältere Datei ohne Baum öffnet sich in der Anordnung von früher (`SplitTree.FromFlat`), ein Baum, der nicht zu
den Tabs passt, wird verworfen. Verbunden wird wie immer erst, wenn ein Terminal zu sehen ist – also nur für den aktiven
Tab und die, die sich mit ihm die Fläche teilen; die übrigen verbinden beim ersten Hinsehen.

### Eine Verbindung für mehrere Sitzungen

Eine neue Sitzung zu einem Server, zu dem schon eine Verbindung steht (teilen, duplizieren, noch ein Tab),
benutzt diese mit: `ShellViewModel` nennt beim Aufbau die stehende Verbindung (`INexusBackend.Connect(profile,
share)`), der Kern öffnet darauf nur einen weiteren Kanal – ohne Schlüsseltausch, ohne Anmeldung, ohne Banner.
Gefragt wird bei jedem Aufbau neu, also auch bei „Erneut verbinden“. Lässt der Server keinen weiteren Kanal zu
(`MaxSessions`) oder ist die andere Verbindung inzwischen zu Ende, baut der Kern eine eigene auf – die Sitzung
merkt davon nichts, außer dass sie sich anmelden muss. Meldet sich gerade eine andere Sitzung beim selben Server an, wartet die neue auf sie (`SessionViewModel.Start`)
und fährt dann mit – sonst gäbe es zwei Passwort-Rückfragen, wenn zwei Tabs zum selben Server zugleich starten
(wiederhergestellte Teilung). Scheitert die erste, meldet sich die nächste selbst an. Mitbenutzt wird nur eine Verbindung desselben Profils,
die auf demselben Weg entstand, den das Profil heute beschreibt (Host, Port und Benutzer jeder Station): Wurde
das Profil geändert, während eine Sitzung dazu offen ist, führt die alte Verbindung woandershin – eine neue
Sitzung bekäme sonst eine Shell auf dem alten Server unter dem Namen des neuen.

### Dateien bearbeiten, gespeicherte Befehle

- **Bearbeiten** (`Services/RemoteEditor.cs`, wie bei MobaXterm): Ein Doppelklick auf eine Datei holt sie in
  einen Arbeitsordner im Temp-Verzeichnis (je Prozess einer, je Datei ein Unterordner), öffnet sie im
  eingestellten Editor und lädt sie nach jedem Speichern wieder hoch. Gestartet wird immer der Editor mit der
  Datei als Argument, nie die Datei selbst (§6). Beim Beenden verschwindet der Arbeitsordner; was ein
  abgestürzter Prozess hinterlassen hat, räumt der nächste Start weg.
- **Gespeicherte Befehle** (Snippets) stehen in den Einstellungen und in der Palette; sie gehen als Eingabe an
  das aktive Terminal, wahlweise gleich mit Enter. Angeboten werden sie nur einer verbundenen Sitzung – sonst
  landete der Text in einer Passwortabfrage.

### Dateien an ein anderes Programm: Ziehen und Kopieren

Einträge der Dateiliste – Dateien und ganze Ordner – lassen sich in den Explorer ziehen oder kopieren und dort
einfügen. Ein Programm, das Dateien entgegennimmt, will sie vorfinden; SFTP-Dateien gibt es auf diesem Rechner
aber noch nicht. Windows kennt dafür „virtuelle Dateien“, wie ein Archiv oder ein Mail-Anhang sie anbietet: eine
Liste (`FileGroupDescriptorW`, mit Unterordnern im Namen) und zu jedem Eintrag ein Strom (`FileContents`), der
erst gelesen wird, wenn der Empfänger die Datei schreibt. Der Empfänger holt im Hintergrund ab
(`IDataObjectAsyncCapability`), zeigt dabei seinen Kopierdialog und meldet, wann er fertig ist.

**Die beiden Hälften.** `VirtualDrop` (Core, ohne Windows) weiß, was angeboten wird, und lädt, wenn gelesen wird.
`App/Interop` übersetzt das für Windows: `VirtualFileDataObject` ist das OLE-Datenobjekt, `OleDrag` zieht es
(`DoDragDrop`), `OleClipboard` legt es in die Zwischenablage (`OleSetClipboard`). Alles über Quellgeneratoren
(`GeneratedComInterface`, `LibraryImport`) – die eingebaute COM-Anbindung von .NET gibt es unter AOT nicht.

**Die Liste kommt vom Kern.** `sftp.tree` nennt, was ein Download anlegen würde: erst die Verzeichnisse, dann die
Dateien, je mit Größe, Zeit und dem Namen auf diesem Rechner. Dahinter steht derselbe Code wie hinter dem
Download selbst (`transfers::plan`; er listet bis zu 16 Verzeichnisse zugleich auf, wertet sie aber der Reihe
nach aus): gleiche Reihenfolge, gleiche Ersetzungen (`a:b` → `a_b`), gleiche Nummern
bei Namen, die unter Windows zusammenfallen, Links und Pipes ausgelassen. Nur so findet die App die geladene
Datei unter dem Namen, den sie dem Empfänger genannt hat. Mehr als 20 000 Einträge oder ein Pfad über 259
Zeichen lassen sich so nicht anbieten – dann bekommt der Empfänger nichts, und unter „Probleme“ steht, warum.

**Geladen wird beim Lesen.** Verlangt der Empfänger den Inhalt einer Datei, beginnt der Download des ausgewählten
Eintrags, zu dem sie gehört – ein Ordner als Ganzes, mit bis zu acht Dateien zugleich – in einen eigenen
Zwischenordner (`%TEMP%\NexusSSH\drag-<Prozess>\<zufällig>`). Der Strom liest die Datei mit, während sie noch
lädt: Der Download schreibt jede erst unter einem anderen Namen (`.nexus-part`) und benennt sie zum Schluss um;
bis dahin ist sie für den Leser nicht zu Ende. So bewegt sich der Kopierdialog des Explorers, und „Abbrechen“
dort kommt nach dem nächsten Stück an statt erst nach der ganzen Datei. Meldet der Empfänger das Ende – oder
gab es keinen –, wird abgebrochen, was noch lädt, und der Zwischenordner verschwindet. Wird der Transfer in der
App abgebrochen, hört der Empfänger still auf (`ERROR_CANCELLED`); scheitert er, meldet der Explorer einen
Lesefehler. Aus der Zwischenablage lässt sich mehrmals einfügen: Jedes Mal beginnt alles von vorn, auch die
Frage nach dem Inhalt der Ordner.

Was Windows dabei verlangt und man dem Code nicht ansieht – jede dieser Stellen ist gemessen, nicht geraten:

- **Der Empfänger darf warten, die App nicht.** Lesen blockiert, bis Daten da sind. Das Datenobjekt wird deshalb
  in einem Thread des Pools bekannt gemacht (Apartment mit vielen Threads) und im Thread der Oberfläche nur über
  einen Stellvertreter benutzt (`Ole.InOtherThreads`); die Aufrufe des Empfängers kommen dann in Threads von COM
  an. Gehörte es dem Thread der Oberfläche, stünde die App während jedes Downloads still.
- **`DoDragDrop` sieht in einer WinUI-App die Maus nicht.** Es erfährt Bewegung und Loslassen aus Mausnachrichten
  an sein eigenes Fenster; hier kommt die Maus aber als Zeiger an (`EnableMouseInPointer`), und diese Nachrichten
  gehen an das Fenster, in dem gedrückt wurde. Ohne Zutun endete das Ziehen nie. `OleDrag.FeedMouseAsync` reicht
  den Stand der Maus nach: alle 15 ms eine Mausnachricht an das Fenster, das im Thread der Oberfläche gerade die
  Maus hält. Die Liste (WinUI) erkennt nur die Geste; ihr eigenes Ziehen wird abgesagt.
- **Dem eigenen Fenster wird nichts angeboten.** Jedes Ziehen beginnt über ihm, und WinUI versucht sofort, aus dem
  Angebot Dateien zu machen – mit Ordnern darin scheitert das als unbehandelter Fehler der Oberfläche. Gefragt
  wird dabei über einen Vermittler von Windows, nicht aus dem eigenen Prozess; deshalb entscheidet die Stelle
  der Maus (`OverOwnWindow`), nicht der Aufrufer.
- **Der Explorer fragt früh.** Die Liste verlangt er schon, wenn die Maus über ihm steht; den Inhalt erst nach dem
  Loslassen. Die Frage nach dem Inhalt der Ordner geht deshalb beim Beginn des Ziehens hinaus (`Prepare`), und
  das Datenobjekt wartet höchstens zehn Sekunden auf die Antwort – so lange steht das Fenster des Empfängers.
- **Zwischenablage:** Das Angebot bleibt aus ihrem Verlauf (Win+V) und von anderen Geräten fern (drei Formate
  dafür im Datenobjekt) – einfügen ließe es sich nur, solange die App läuft. Beim Beenden nimmt die App es
  wieder heraus (`OleClipboard.Clear`).
- **Bild und Hinweis beim Ziehen** zeichnet der Bild-Helfer von Windows (`IDragSourceHelper2`, mit
  `DSH_ALLOWDROPDESCRIPTIONTEXT`): `DragImage` gibt ihm das Symbol einer Datei bzw. eines Ordners (eigene Bitmap
  – sein Standardbild rechnete er aus Formaten aus, die es hier nicht gibt). Der Helfer legt sein Bild und seinen
  Zustand im Datenobjekt ab, der Empfänger seinen Hinweis („Nach … kopieren“, `DropDescription`) – das Datenobjekt
  bewahrt deshalb auf, was per `SetData` hineinkommt (Speicher und Ströme, als Kopie). Drei Fallen, alle beim
  Bauen getroffen:
  - Die eigenen Formate lassen sich nicht überschreiben: Ein „Preferred DropEffect“ von außen ließ den Explorer
    gar nichts mehr annehmen.
  - Über dem eigenen Fenster zählt `EnumFormatEtc` auch das Aufbewahrte nicht auf. Windows merkt sich die
    Aufzählung von dort für das ganze Ziehen; stand darin nur das Bild, fragte der Explorer nie nach den Dateien.
    `GetData` liefert das Aufbewahrte überall – der Helfer fragt mit Namen.
  - Das Fenster des Ziehbilds zeichnet den Hinweis nur neu, wenn es dazu aufgefordert wird (`DDWM_UPDATEWINDOW` an
    `DragWindow`) – beim Explorer als Quelle tut das sein eigenes Datenobjekt. Hier geschieht es bei jedem
    `SetData` des Hinweises und bei jeder Rückmeldung (`GiveFeedback`); solange das Bild zu sehen ist
    (`IsShowingLayered`), bleibt der Zeiger ein Pfeil.

Grenzen: Programme, die nur echte Dateien annehmen (`CF_HDROP`, etwa Notepad), bekommen nichts angeboten. Ein
Browser bekommt die Dateien eines Ordners ohne dessen Struktur.

### Export und Import

Um die App auf einem anderen Rechner mit denselben Daten zu benutzen, schreibt sie Profile, gespeicherte Befehle und
einen Teil der Einstellungen in eine JSON-Datei (`Core/Exchange`: `ExportFile`, `DataExchange`) und liest sie dort
wieder ein. Eine automatische Synchronisierung gibt es noch nicht; die Datei und ihr Abgleich sind die Grundlage
dafür.

- **Was drinsteht:** Profile mit Kennung, Ziel, Gruppe, Schlagworten, Schlüsselpfad, Sprung-Server
  (`jump` = Kennung eines anderen Profils der Datei) und Agent Forwarding; die gespeicherten Befehle; Sprache,
  Farbschema, Schriftgröße, Sortierung, versteckte Dateien, „Tabs wiederherstellen“.
- **Was nie drinsteht:** Passwörter und Passphrasen (sie liegen in der Anmeldeinformationsverwaltung des Rechners,
  die Oberfläche kennt sie gar nicht), Schlüsseldateien (nur ihr Pfad), „zuletzt verbunden“, und was zum Rechner
  gehört: Fenstergröße, Breite der Bereiche, Editor, letzter Download-Ordner, offene Tabs.
- **Schlüsselpfade** im Benutzerordner stehen als `~/…` in der Datei und werden beim Import auf den Benutzerordner
  des Zielrechners bezogen – der Benutzername darf dort ein anderer sein. Fehlt die Datei dort, sagt es der
  Dialog vor dem Import und das Protokoll danach.
- **Abgleich** (`DataExchange.Plan`): Ein Profil der Datei ist dasselbe wie ein vorhandenes, wenn die Kennung
  übereinstimmt oder Name und Ziel (Host, Port, Benutzer) gleich sind; dann wird das vorhandene ersetzt und behält
  seine Kennung, sonst entsteht ein neues unter der Kennung aus der Datei. Verweise auf Sprung-Server werden auf
  die Kennungen des Zielrechners umgeschrieben; ein Verweis ins Leere entfällt. Befehle gleichen Namens werden
  ersetzt, eigene bleiben. Der Plan wird gezeigt, bevor etwas geschieht, und der Benutzer wählt, welche Teile er
  übernimmt.
- **Die Datei ist fremde Eingabe** – sie kann von Hand bearbeitet sein. Größe und Zahl der Einträge sind begrenzt,
  Kennungen dürfen nur aus Buchstaben, Ziffern, `-` und `_` bestehen (sie stehen später in Dateien und in den Namen
  gespeicherter Geheimnisse), Einträge ohne Host oder Benutzer werden übergangen, und gespeichert wird über
  `IProfileStore.Save`: Der Kern prüft jedes Profil wie ein eingetipptes. Eine Datei mit höherer Formatversion
  wird abgelehnt statt halb gelesen.

### Befehle und Palette (Spec §13)

Alle Befehle stehen an einer Stelle: `ShellViewModel.Commands` (`ShellViewModel.Commands.cs`) – Kennung, Titel,
Tastenkürzel, ob er gerade geht, was er tut. Daraus entstehen

- die **Befehlspalette** im Suchfeld der Titelleiste (`SearchPalette`): Server (verbinden), offene Tabs
  (wechseln, daneben zeigen), Befehle; mit `>` am Anfang nur Befehle,
- die **Tastenkürzel im Fenster** (`MainWindow.AddShortcuts`),
- die **Tastenkürzel im Terminal**: Der WebView schluckt Tasten, also bekommt die Terminal-Seite die Liste
  (`TerminalShortcuts`) und meldet die Kennung des Befehls zurück.

Ein Befehl, der die Oberfläche braucht (Dialog, Fokus, Terminal leeren), löst `UiRequested` aus. Kürzel ohne
Umschalt sind nur für Tasten vergeben, die keine Shell braucht (`+`, `-`, `0`, Tab): Strg+Buchstabe bleibt dem
Server – ein Test hält das fest.

### Protokoll und Probleme (Spec §7)

`ShellViewModel.Log` schreibt in den Reiter „Ausgabe“; Warnungen und Fehler stehen zusätzlich unter „Probleme“
(Warnungen des Kerns, gescheiterte Verbindungen und Transfers, abgelehnte Profile).

## 4. Terminal

Ein einziges `WebView2` (`Controls/TerminalHost`) zeigt die Seite `Assets/terminal/index.html`. Die Seite hält je
Sitzung eine xterm.js-Instanz und zeigt eine bis sechzehn davon. Ein WebView für alle Sitzungen spart Speicher; die
geteilte Ansicht ist eine reine Layoutfrage innerhalb der Seite: Sie baut aus dem Baum der Nachricht `layout`
verschachtelte Flex-Container (`.split.row`, `.split.column`) mit einer Trennlinie (`.divider`) zwischen je zwei
Kindern. Terminals werden dabei umgehängt, nie neu angelegt – der Verlauf bleibt.

Nachrichten (JSON über `postMessage`, definiert in `NexusSSH.Core/Terminals/TerminalMessages.cs` und
`terminal.js` – beide Seiten gemeinsam ändern):

| Richtung | Nachricht | Bedeutung |
| --- | --- | --- |
| App → Seite | `open {id}` | Terminal anlegen |
| App → Seite | `layout {tree, id, zoom, fixed}` | diese Terminals zeigen (bis zu sechzehn): `tree` ist ein Blatt `{id}` oder ein Zweig `{dir: row\|column, shares, items}`, `id` bekommt den Fokus, `zoom` (wenn gesetzt) die ganze Fläche, `fixed`: die Anordnung gehört tmux und lässt sich nicht ändern; ohne `tree` zeigt die Seite nichts |
| App → Seite | `label {id, data, state, hint}` | Kopfzeile eines geteilten Terminals: Name (gesäubert, gekürzt, nur als Text gesetzt), Zustand (`connected`, `connecting`, `closed`), Text am × (ohne ihn kein ×) |
| App → Seite | `write {id, data}` | Ausgabe des Servers, Base64; die Seite bestätigt mit `ack` |
| App → Seite | `options {theme, fontSize, shortcuts}` | Farbschema, Schriftgröße, Tastenkürzel der App |
| App → Seite | `find {id, data, …}` / `findClear {id}` | im Terminal suchen und hervorheben |
| App → Seite | `grep {data, matchCase}` | in allen Terminals suchen |
| App → Seite | `reveal {id, row, data}` | zu einer Fundstelle rollen und sie markieren |
| App → Seite | `clear {id}` | Bildschirm und Verlauf leeren |
| App → Seite | `divider {id}` | der Trennlinie neben dem Terminal den Fokus geben (danach schieben sie die Pfeiltasten) |
| App → Seite | `dump {id}` | den ganzen Text des Terminals anfordern (Verlauf speichern) |
| App → Seite | `close {id}` | Terminal verwerfen |
| Seite → App | `ready` | Seite geladen |
| Seite → App | `resize {id, cols, rows}` | Größe in Zeichen |
| Seite → App | `input {id, data}` | Tasten und eingefügter Text |
| Seite → App | `shortcut {key}` | Kennung eines Befehls, dessen Kürzel im Terminal gedrückt wurde |
| Seite → App | `focus {id}` | Klick in ein anderes Terminal der geteilten Ansicht |
| Seite → App | `unsplit {id}` | das × in der Kopfzeile: dieses Terminal aus der Teilung nehmen (sein Tab bleibt offen) |
| Seite → App | `shares {id, path, shares}` | eine Trennlinie wurde losgelassen: `path` führt von der Wurzel zum Zweig (Nummern der Kinder), `shares` sind dessen neue Anteile; die App prüft Pfad, Zahl und Summe |
| Seite → App | `move {id, target, side}` | eine Kopfzeile wurde auf eine Kante (`left`, `right`, `top`, `bottom`) eines anderen Terminals gezogen |
| Seite → App | `ack {id, count}` | so viele Bytes der Ausgabe sind verarbeitet (Flusskontrolle) |
| Seite → App | `text {id, data}` | Antwort auf `dump`: Zeilen des Terminals, ohne Farben |
| Seite → App | `found {id, index, count}` | Stand der Suche im Terminal |
| Seite → App | `hits {data, items, more}` | Zeilen aller Terminals, in denen der Suchtext vorkommt |

Ablauf je Sitzung: `open` → `layout` → die Seite meldet `resize` → **erst dann** startet die Sitzung. So geht
die richtige Größe schon in die PTY-Anforderung an den Server.

**Flusskontrolle** (`Core/Terminals/TerminalOutputPump.cs`): Ein Server kann viel schneller schreiben, als ein
Terminal zeichnet (`cat` einer großen Datei). Die Pumpe fasst die Blöcke, die zwischen zwei Durchläufen des
UI-Threads eintreffen, je Sitzung zu einer Nachricht zusammen und zählt, was die Seite noch nicht bestätigt
hat. Über 1 MiB hält sie die Sitzung an (`ITerminalSession.SetPaused` → `session.flow`): Der Kern liest dann
nicht mehr vom Kanal, das SSH-Fenster läuft voll, und der Server wartet. Unter 256 KiB geht es weiter. So bleibt
der Speicher begrenzt, und die anderen Sitzungen laufen ungestört.

**Verlauf speichern:** `dump` liefert den Text des normalen Puffers (nicht den eines Vollbildprogramms),
umbrochene Zeilen wieder zusammengesetzt. Die App nimmt höchstens 16 MiB an, entfernt alle Steuerzeichen außer
Zeilenende und Tabulator und schreibt eine Textdatei.

**Suche:** Die Suchleiste über dem Terminal benutzt das Such-Addon von xterm.js (Hervorhebung, „3 von 12“). Die
Trefferliste im unteren Bereich geht durch den Verlauf aller Terminals; die Seite liefert höchstens 500 Zeilen,
die App nimmt auch nicht mehr an.

**Farbschemata** stehen in `Core/Terminals/TerminalThemes.cs`. Der Hintergrund des Terminals gilt auch für die
Flächen darum: Alle benutzen den Pinsel `TerminalBackgroundBrush`, dessen Farbe mit dem Schema wechselt.

Härtung, weil die Seite Ausgabe fremder Server darstellt:

- Inhalte kommen über `SetVirtualHostNameToFolderMapping` aus dem Programmordner, nicht aus dem Netz.
- CSP in `index.html`: nur eigene Skripte, keine Netzzugriffe.
- Navigation woandershin und neue Fenster werden abgebrochen; Host-Objekte sind aus.
- Nachrichten der Seite gelten als ungeprüft: `TerminalMessages.Parse` verwirft alles Unbekannte. Fundstellen
  sind Ausgabe eines Servers – sie werden gekürzt, von Steuerzeichen befreit und als reiner Text gezeigt. Ein
  gemeldetes Tastenkürzel führt nur einen Befehl aus, den es gibt.
- Der **Banner** eines Servers kommt, bevor irgendjemand angemeldet ist. Er wird als reiner Text gedruckt: Der
  Kern lässt nur Zeilenenden und druckbare Zeichen durch (höchstens 4000), die Sitzung noch einmal – eine
  Escape-Folge darin kann das Terminal nicht steuern.
- DevTools und Kontextmenü gibt es nur im Debug-Build.

xterm.js liegt eingecheckt unter `Assets/terminal/vendor` (ohne npm, siehe `tools/vendor-xterm.ps1`).

## 5. Rust-Kern und C-ABI

Der Kern wird als `nexus_core.dll` in den Prozess geladen. `RustCore.targets` ruft vor jedem Build `cargo build`
und legt die DLL neben die EXE. Die Schnittstelle ist schmal und nachrichtenbasiert (`backend/nexus-core/src/ffi.rs`,
Gegenstück `NexusSSH.Core/Backend/Native/NativeCore.cs` + `NativeCoreTransport.cs`):

| Funktion | Zweck |
| --- | --- |
| `nexus_core_abi_version`, `nexus_core_version` | Passt die DLL zur App? |
| `nexus_core_create(config, callback, user_data)` | Kern starten; `config` ist JSON mit dem Datenordner und wahlweise einem anderen `.ssh`-Ordner |
| `nexus_core_request(core, json) → json` | Anfrage ausführen, Antwort sofort |
| `nexus_core_free` | Antwortpuffer zurückgeben |
| `nexus_core_write(core, session, bytes)` | Terminal-Eingabe |
| `nexus_core_destroy` | Kern samt Sitzungen beenden; danach kein Rückruf mehr |

Der Rückruf `callback(user_data, kind, session, data, len)` kommt von Threads des Kerns: `kind 0` ist ein Ereignis
als JSON, `kind 1` Terminal-Ausgabe einer Sitzung als rohe Bytes.

Regeln: kein Panic über die Grenze (`catch_unwind`, deshalb kein `panic = "abort"` im Release-Profil), Zeiger aus
Rust gehören Rust, Zeiger der App gelten nur während des Aufrufs. Jede inkompatible Änderung an `ffi.rs` oder am
Nachrichtenformat erhöht `ABI_VERSION` und `NativeCore.ExpectedAbi`; die App startet nicht mit einer DLL, die nicht
passt.

### Nachrichten

Definiert in `backend/nexus-core/src/protocol.rs` und `NexusSSH.Core/Backend/CoreProtocol.cs` – beide Seiten
gemeinsam ändern. `CoreBackendTests` und die Tests daneben halten die JSON-Texte auf der C#-Seite fest.

Anfragen (`op`): `profiles.list`, `profiles.save`, `profiles.delete`, `secrets.forget`, `sshconfig.read`,
`sshconfig.import`, `session.open` (wahlweise mit `share` = Sitzung, deren Verbindung mitbenutzt wird, und `mosh_predict` bei einem
Profil mit Mosh), `session.relink` (die SSH-Verbindung unter einem Mosh-Terminal jetzt neu aufbauen), `local.shells`
(die Shells dieses Rechners für ein lokales Terminal: Kennung und Name, nie ein Pfad),
`session.resize`, `session.flow`, `session.close`, `monitor.set`, `prompt.answer`, `sftp.list`, `sftp.mkdir`,
`sftp.rename`, `sftp.chmod` und `sftp.chown` (wahlweise `recursive`), `sftp.delete`, `sftp.reopen` (neuer SFTP-Kanal
auf der stehenden Verbindung), `sftp.tree` (was ein Download anlegen würde, ohne zu laden), `sftp.readlink` (wohin ein Link zeigt), `transfer.start` (Upload wahlweise mit `name` = Name auf dem Server; beim neuen Versuch mit `resume` = Bytes, die der abgebrochene übertragen hat),
`transfer.target` (wohin ein Download schriebe), `transfer.cancel`, `transfer.retain` (welche Transfers die App
noch zeigt), `transfer.suspend` (die App endet). Was dauert, wird nur angestoßen; der Fortschritt kommt als
Ereignis – `transfer.state` nennt Bytes (`bytes`/`total`) und Dateien (`files_done`/`files`); die App misst nach
Dateien, wenn es keine Bytes gibt.

Ereignisse (`event`):

| Ereignis | Bedeutung |
| --- | --- |
| `session.state` | `connecting` → `authenticating` → `connected` (mit `home`, wenn der Server SFTP anbietet) → `closed` (mit `reason`, englischer `message`, `exit_status`). Über Sprung-Server wiederholen sich die ersten beiden je Server, mit `hop` = `user@host` |
| `session.files_only` | Die Sitzung hat keine Shell (abgelehnt oder sofort beendet, mit `exit_status`, falls es einen gab), bleibt aber für Dateien offen – weiter `connected`, bis `closed` kommt |
| `session.banner` | Text, den der Server vor der Anmeldung zeigt |
| `session.mosh` | Das Terminal läuft über Mosh: `running` (gleich nach `connected` und wenn Mosh wieder Antworten bekommt) oder `silent` mit `silent_for` (Sekunden ohne Nachricht vom Server, alle paar Sekunden neu gemeldet). Fehlt das Ereignis nach `connected`, läuft das Terminal über SSH |
| `session.link_lost` | Die SSH-Verbindung unter einem Mosh-Terminal ist weg (oder ein neuer Aufbau scheiterte, dann mit `message`): Das Terminal läuft weiter, Dateien, Auslastung und Tunnel nicht |
| `session.linked` | Die SSH-Verbindung unter einem Mosh-Terminal steht wieder (mit `home`, wenn der Server SFTP anbietet) |
| `sftp.result` | Ergebnis einer `sftp.*`-Anfrage, zugeordnet über die `request`-Nummer der App; bei `sftp.list` mit `entries` (Name, Art, Größe, Zeit, `permissions`, `owner`, `group`; bei Links `target` = der Text im Link und `dangling`, wenn er ins Leere zeigt), bei `sftp.readlink` mit `target`; Listen aus Befehlen (`shellfs`) nennen weder `target` noch `dangling`; über FTP trägt ein Link `target`, wenn der Server ihn mit `LIST` nannte (nicht mit `MLSD`), nie aber `dangling` |
| `transfer.state` | `queued` → `running` → `completed` / `failed` / `cancelled`, mit `bytes` und `total`; bei einem neuen Versuch dazu `resumed` = so viel war vom Versuch davor schon am Ziel und ging nicht noch einmal über die Leitung |
| `monitor.stats` | Auslastung: `cpu` (0–100), `memory_used`/`_total`, `disk_used`/`_total` (Bytes), `os`, `kernel`, `uptime`, `load` (1, 5, 15 Minuten), `net_rx`/`net_tx` (Bytes je Sekunde) |
| `monitor.unavailable` | Der Server liefert keine Auslastung |
| `prompt.host_key` | Unbekannter oder geänderter Host-Schlüssel; Antwort `accept` |
| `prompt.secret` | Passwort, Passphrase oder Fragen des Servers; Antwort `values` (+ `save`), ohne `values` = abgebrochen |
| `profiles.changed` | Profile haben sich geändert (z. B. „zuletzt verbunden“) |
| `core.warning` | Hinweis fürs Protokoll (Schlüsseldatei unlesbar, Geheimnis nicht gespeichert, kein Agent, `local_dir_missing`, bei Mosh `mosh_unavailable`/`mosh_locale`/`mosh_blocked` …) |

Die Sitzungsnummer vergibt die App. Ein `closed` kommt je Sitzung genau einmal – ob sie von selbst endet oder
geschlossen wird. `profiles.list` nennt je Profil auch `saved_secret`: ob dazu ein Passwort oder eine
Passphrase gespeichert ist (das Geheimnis selbst verlässt den Kern nie).

### Ablauf einer Sitzung (`session.rs`)

1. Der Weg zum Server (`ProfileStore::chain`): erst die Sprung-Server, zuletzt das Ziel. Ein fehlendes Profil
   oder ein Kreis lehnt schon `session.open` ab.
2. TCP-Verbindung zum ersten Server (15 s Frist), Schlüsselaustausch.
3. Host-Schlüssel prüfen (§6). Unbekannt oder geändert → Rückfrage; Ablehnung beendet mit `host_key_rejected`.
4. Anmelden, Reihenfolge wie bei OpenSSH. Zuerst `none`, um die zugelassenen Methoden zu erfahren, dann:
   Schlüsseldatei des Profils → SSH-Agent (OpenSSH-Agent von Windows, sonst Pageant; höchstens drei Schlüssel) →
   Standarddateien in `~/.ssh` (nur ohne Schlüsseldatei im Profil) → Passwort (gespeichertes zuerst, sonst bis zu
   drei Rückfragen) → Fragen des Servers (keyboard-interactive).
   Die Grenzen gibt es, weil Server nach wenigen Fehlversuchen abbrechen (OpenSSH: `MaxAuthTries 6`).
5. **Sprung-Server:** Gibt es weitere Server auf dem Weg, öffnet die stehende Verbindung einen Kanal dorthin
   (`direct-tcpip`), und darüber läuft die nächste SSH-Verbindung – Schritte 3 und 4 wiederholen sich mit dem
   Profil dieses Servers (eigener Host-Schlüssel, eigene Anmeldung, eigenes gespeichertes Passwort). Die
   Verbindung ist Ende zu Ende verschlüsselt; ein Sprung-Server sieht nur den Datenstrom.
6. PTY (`xterm-256color`) in der Größe des Terminals; Agent Forwarding, wenn das Profil es erlaubt und ein Agent
   läuft; Shell.
7. Zweiter Kanal derselben Verbindung für SFTP. Lehnt der Server das Subsystem ab, steht die Sitzung trotzdem –
   ohne Dateibrowser.
8. Lesen und Schreiben laufen getrennt: Stockt das Senden, wird weiter gelesen. Keepalive alle 30 s.

Nennt `session.open` mit `share` eine Sitzung zum selben Profil, deren Verbindung steht, entfallen die Schritte
2 bis 5: Die Verbindung (`Link`, samt ihrer Sprung-Server) gehört dann beiden Sitzungen und endet erst mit der
letzten. Scheitert schon das Öffnen des Kanals, baut die Sitzung ihre eigene Verbindung auf.

`session.flow` hält das Lesen einer Sitzung an oder setzt es fort (Flusskontrolle, §4). Angehalten bleibt die
Sitzung trotzdem ansprechbar: Eingaben, Größenänderungen und das Schließen gehen weiter durch.

### Lokales Terminal (`local/`)

Ein Profil mit `protocol: local` öffnet eine Shell dieses Rechners statt einer Verbindung – wie Telnet und seriell
eine schmale Sitzung (`local/session.rs`, `session.rs` zweigt gleich am Anfang ab): keine Anmeldung, kein Passwort,
keine Dateien, keine Auslastung (`session.files_only` gibt es hier nicht, der Tab hat schlicht keinen Dateibrowser).
`host` ist die **Kennung** der Shell (`pwsh`, `powershell`, `cmd`, `wsl`, `wsl:<Distribution>`, `gitbash`, `login`,
unter Unix auch ein schlichter Pfad aus `/etc/shells`), `local_dir` der Startordner. Programm und Argumente stehen
je Art fest im Kern; die App nennt nie einen Pfad (`local.shells` liefert Kennung und Namen, nie ein Programm).

1. `local::shell(id)` findet die Shell – nur, was `local::shells` selbst gefunden hat: unter Windows an festen Orten
   (Systemordner, Registereintrag „App Paths“ von PowerShell 7, Programmordner – nie über den Suchpfad), WSL über
   `wsl.exe --list` mit Frist, unter Unix die Anmelde-Shell und `/etc/shells`. Eine Kennung, die es hier nicht gibt,
   beendet die Sitzung mit `connect_failed`.
2. Der Startordner wird in einem eigenen Thread mit Frist nachgesehen (er kann auf einem Netzlaufwerk liegen); fehlt
   er oder antwortet er nicht, beginnt die Shell im Benutzerordner, und `core.warning` mit `local_dir_missing`
   sagt es.
3. Das Pseudo-Terminal: unter Windows ConPTY über zwei anonyme Pipes, die Shell in einem Job-Objekt mit
   `KILL_ON_JOB_CLOSE` (was sie startet, endet mit dem Tab); unter Unix `openpty`, die Shell als Anführer einer
   eigenen Sitzung und als Anmelde-Shell, beim Schließen bekommt die ganze Sitzung `SIGHUP` (und die Shell nach
   einer Frist `SIGKILL`). Lesen und Schreiben laufen in eigenen Threads; der begrenzte Kanal zum Leser ist die
   Bremse (`session.flow`). Beim Schließen wird weiter gelesen und verworfen, damit `ClosePseudoConsole` nie auf
   einen Leser wartet, der nicht mehr da ist.
4. Die Umgebung ist die der App ohne `WEBVIEW2_*` (die App setzt dort ihren eigenen Datenordner) plus
   `TERM_PROGRAM=NexusSSH`, unter Unix `TERM=xterm-256color` und `COLORTERM=truecolor`.
5. Endet die Shell, holt der Kern noch ab, was sie zuletzt schrieb, und meldet `closed` mit ihrem Rückgabewert
   (`exit_status`, unter Windows ohne Vorzeichen).

Die Profile bleiben auf diesem Rechner: nicht im Export, und ein Import übergeht sie. Der Verbindungstest hat keinen
Weg zu prüfen. Die Shell, die Strg+Umschalt+T öffnet, ist eine Einstellung dieses Rechners.

### Mosh (`mosh.rs`, Bibliothek `backend/mosh-client`)

Ein SSH-Profil mit `mosh: true` führt sein Terminal über Mosh (UDP). Die Bibliothek `mosh-client`
([`mosh-client-rs`](https://github.com/wandcs/mosh-client-rs), eingecheckt) spricht das Protokoll, hält den
Bildschirmzustand und rechnet die Vorhersage; was gegenüber dem Original geändert ist, steht in
`backend/mosh-client/PATCHES.md` (jede Änderung mit Test).

1. Angemeldet wird wie bei jeder SSH-Sitzung (Schritte 1 bis 5 oben; **nie über einen Sprung-Server** – UDP geht
   nicht hindurch, `Profile::normalized` lehnt die Kombination ab).
2. Auf der stehenden Verbindung startet der Kern `mosh-server new -s -c 256 -l LANG=C.UTF-8` (bei Ablehnung der
   Locale noch einmal mit `en_US.UTF-8`; wahlweise `-p` mit den geprüften Ports des Profils). Die Zeile besteht aus
   dem festen Text `exec::MOSH_START` und `mosh::start_line`, nie aus Text des Profils oder des Servers; gelesen
   wird nur, was hinter der Marke `@nexus-mosh` kommt, und es muss genau ein Datensatz `MOSH CONNECT <port>
   <schlüssel>` darin stehen (`Bootstrap::parse`).
3. Das UDP-Ziel ist immer die Adresse, an der auch die SSH-Verbindung ankam (IPv4 und IPv6), nie eine, die der
   Server nennt. Der Schlüssel der Sitzung bleibt in der Bibliothek, die Ausgabe von `mosh-server` wird nach dem
   Lesen überschrieben; er steht in keinem Ereignis, keinem Protokoll und keiner Datei.
4. Jeder Treiber läuft auf einem eigenen Thread mit eigener Laufzeit (`mosh::Terminal`): Ein feindseliger Server kann
   je Anweisung Sekunden Rechenzeit kosten, und das soll den Kern nicht aufhalten. Die Größe wird auf 500 × 200
   begrenzt. Beim Schließen des Tabs bittet der Kern `mosh-server` um das Ende und wartet höchstens zwei Sekunden auf
   die Threads.
5. **Rückfall:** Fehlt `mosh-server`, gibt es keine UTF-8-Locale, antwortet er in anderer Form oder kommt binnen 15 s
   kein UDP-Paket an, läuft das Terminal über den SSH-Kanal weiter, und `core.warning` nennt den Grund
   (`mosh_unavailable`, `mosh_locale`, `mosh_blocked`).
6. Danach meldet `session.mosh` `running`, oder `silent` mit `silent_for`, solange nichts vom Server kommt.
   Reißt die SSH-Verbindung darunter, läuft das Terminal weiter: `session.link_lost` (Dateien, Auslastung und Tunnel
   gehen), und sobald Mosh wieder Antworten bekommt, baut der Kern sie neu auf (von selbst höchstens alle 30 s,
   sofort mit `session.relink`) und meldet `session.linked` mit dem Startordner. Nach einer gescheiterten Anmeldung
   oder abgelehnten Rückfrage versucht er es nicht mehr von selbst.
7. Vorhersage: `session.open` nimmt `mosh_predict` (`adaptive`, `always`, `never`; Einstellung dieses Rechners).

Mosh kennt keinen Verlauf zum Zurückrollen (es schickt nur den Bildschirm), kein Agent Forwarding, kein X11 und nur
UTF-8; ein Profil mit Mosh schaltet sie deshalb im Editor und im Kern ab.

### Auslastung (`monitor.rs`)

Für den Server-Inspektor (Spec §6) läuft auf einem weiteren Kanal eine kleine Shell-Schleife, die alle drei
Sekunden `/proc/stat`, `/proc/meminfo`, `/proc/uptime`, `/proc/loadavg`, `/proc/net/dev` und `df /` ausgibt;
dazu einmal `/etc/os-release` und `uname`. Der Kern rechnet daraus die Werte (CPU und Netz als Unterschied
zweier Messungen; beim Netz zählen alle Schnittstellen außer `lo`).

Ohne `/proc/stat` (FreeBSD, OpenBSD) nimmt die Schleife die Zähler des Systems und gibt sie roh aus; der Parser
kennt alle Formen: `kern.cp_time` (untätig ist der letzte Wert; OpenBSD trennt mit Kommas und zählt zusätzlich
spin), `hw.physmem` (FreeBSD `name: wert`, OpenBSD `name=wert`) und die Seiten aus `vm.stats.vm` (verfügbar =
frei + inaktiv + Cache, wie `top`). OpenBSD kennt `vm.stats` nicht; dort kommen freie und inaktive Seiten aus
`vmstat -s`, und der Puffer-Cache zählt als belegt. Ob `vm.stats` fehlt, sagt die leere Ausgabe, nicht der
Rückgabewert – OpenBSDs `sysctl` endet auch bei einem unbekannten Namen mit 0. Die Laufzeit kommt aus
`kern.boottime` (nur auf die Sekunde – die Netzrate schwankt dadurch etwas), dazu `vm.loadavg` und
`netstat -ibn`. Von `netstat` zählt je Karte nur die Zeile `<Link…>` (die Adresszeilen nennen dieselben Bytes
noch einmal); die Spalten stehen im Kopf (OpenBSD hat keine für Pakete), und fehlt einer Zeile eine Spalte
(Karte ohne Hardware-Adresse), fehlt sie vor den Bytes. OpenBSD hat kein `/etc/os-release`; der Name kommt aus
`uname`. Geprüft mit FreeBSD 14.5 und OpenBSD 7.9 in QEMU.

- Gemessen wird **nur, solange es jemand sieht**: `monitor.set` schaltet ein und aus; die App verlangt es für
  die aktive Sitzung bei eingeblendetem Inspektor oder eingeschalteter Leiste unter dem Terminal (die zeigt CPU,
  Speicher, Netz, Platte und Laufzeit, der Inspektor die Einzelheiten). Abgeschaltet schließt der Kanal, die
  Schleife endet.
- Das geht auf Linux-, FreeBSD- und OpenBSD-Servern, die Befehle zulassen. Alles andere meldet sich einmal als
  `monitor.unavailable`; der Kern versucht es dann nicht von selbst wieder.
- Der Befehl läuft über `sh -c`, weil die Anmelde-Shell eine andere sein kann (fish, csh).
- Was der Server antwortet, ist fremd: Texte werden gekürzt und von Steuerzeichen befreit, überlange Zeilen
  beenden die Überwachung.
- **Speicher, solange gemessen wird:** Jede Messung lässt in der App etwa 4 KB Abfall zurück (JSON, DTO,
  Texte). Weil sonst kaum etwas allokiert wird, sammelt die GC erst, wenn ihr Gen0-Budget voll ist (hier
  37–51 MB) – bei einer Messung alle 3 s nach rund zehn Stunden. Bis dahin wächst der private Speicher um gut
  4 MB je Stunde; danach steht er, lebendig bleiben unter 4 MB. Das ist kein Leck. Wer das nachprüft:
  `GC.GetTotalMemory` und `GC.CollectionCount` mitschreiben und gegen den `nexus-testserver` mit kurzem Takt
  messen – nicht nach dem privaten Speicher allein urteilen.

### SSH-Konfiguration (`sshconfig.rs`)

`sshconfig.read` liest `~/.ssh/config` und liefert die Server so, wie `ssh <alias>` sie auflösen würde: je
Einstellung der erste passende Wert, auch aus Blöcken mit Mustern (`Host *`); `Include` wird verfolgt, `Match`
übersprungen (bis auf `Match all`). Übernommen werden `HostName`, `User`, `Port`, `IdentityFile`, `ProxyJump` und
`ForwardAgent`. `sshconfig.import` legt Profile an; ein Sprung-Server wird mit angelegt, wenn es für ihn noch
keines gibt. Mehrere Sprünge (`ProxyJump a,b`) werden als Kette angelegt: `b` springt über `a`. Gibt es `b`
schon mit einem anderen Weg, bleibt es unangetastet, und für die Kette entsteht ein eigenes Profil
(„b via a“). Die Datei wird nur gelesen.

### Dateien und Transfers (`sftp.rs`, `transfers.rs`)

- Auflisten folgt Links: Ein Link auf ein Verzeichnis lässt sich betreten. Die Links eines Verzeichnisses
  werden nebeneinander aufgelöst (16 zugleich), nicht einer nach dem anderen. Sortiert wird im Kern (Ordner
  zuerst, dann nach Namen ohne Rücksicht auf Groß- und Kleinschreibung).
- Zu jedem Eintrag gehören Rechte, Besitzer und Gruppe. SFTP liefert nur Nummern; die Namen liest der Kern
  einmal je Sitzung aus `/etc/passwd` und `/etc/group` des Servers. Geht das nicht, bleiben die Nummern.
- Umbenennen überschreibt nie ein vorhandenes Ziel. Löschen nimmt Verzeichnisse samt Inhalt mit, entfernt Links
  aber nur, statt ihnen zu folgen.
- Ein Transfer ist eine Datei oder ein ganzes Verzeichnis. Höchstens zwei laufen gleichzeitig, die übrigen warten
  – in der Reihenfolge, in der sie eingereiht wurden (`transfers::Slots`; die Wartenummer wird schon in der
  Anfrage gezogen, nicht erst, wenn die Aufgabe des Transfers läuft).
  Bei Verzeichnissen wird erst der Baum erfasst, damit die Gesamtgröße feststeht; Links im Baum werden übersprungen.
- Ein Download schreibt in `<name>.nexus-part` und benennt erst am Ende um: Ein Abbruch hinterlässt nie eine halbe
  Datei unter dem richtigen Namen und zerstört keine vorhandene. Die Teil-Datei wird weggeräumt – außer die
  Verbindung ist schuld (abgerissen, oder der Server antwortet nicht mehr): Dann bleibt sie für den neuen Versuch
  liegen, solange der Kern sich an sie erinnert (siehe unten).
- Ein Upload, der eine **neue** Datei anlegt, geht genauso über `<name>.nexus-part` auf dem Server; bei Abbruch
  oder Fehler wird die Teil-Datei entfernt. Eine **vorhandene** Datei wird an Ort und Stelle überschrieben –
  so bleiben Besitzer, Rechte und feste Links erhalten, und der Upload geht auch dort, wo man im Verzeichnis
  nichts anlegen darf. Ein Abbruch hinterlässt dann die angefangene Datei.
- Ein gescheiterter oder abgebrochener Transfer lässt sich wiederholen: Die App hebt auf, was zu ihm gehört,
  und startet ihn als neuen Transfer.
- **Der Upload einer einzelnen Datei setzt dabei fort**, statt von vorn zu beginnen. Zwei Fragen sind zu klären,
  und jede beantwortet, wer sie beantworten kann:
  - *Ist die lokale Datei noch dieselbe?* Das weiß die App: Sie merkt sich beim Start Größe und Änderungszeit
    (`LocalStamp`) und nennt dem Kern nur dann in `resume`, wie viel der abgebrochene Versuch geschickt hat. Hat
    ein Versuch nichts geschickt (es gab keine Verbindung), gilt weiter die Zahl des Versuchs davor.
  - *Liegt auf dem Server ihr Anfang?* Das prüft der Kern (`transfers::resume_point`). Er macht am kleineren von
    „geschickt“ und „Größe auf dem Server“ weiter: Schreiben läuft dem Server voraus (bis zu 16 unbestätigte
    Pakete von 32 KiB), gezählt ist also mehr, als sicher ankam – am echten Server lagen nach dem Abriss
    288 KiB weniger dort als gezählt, mitten in einem Paket. Vor der Stelle vergleicht er das letzte MiB mit der
    lokalen Datei; das deckt alles ab, was beim Abriss unterwegs war, und merkt, wenn dort inzwischen etwas
    anderes liegt. Die ganze Datei zu vergleichen hieße, sie zu holen – das kostete, was das Fortsetzen spart.
    Ist die Datei auf dem Server länger als die lokale, stammt sie nicht vom abgebrochenen Versuch.
  Trifft etwas nicht zu oder lässt es sich nicht prüfen, beginnt der Upload von vorn. Fortgesetzt wird in der
  vorhandenen Datei (ohne sie zu kürzen) oder, bei einer neuen, in der Teil-Datei, die nach einer abgerissenen
  Verbindung liegen geblieben ist; nach einem Abbruch von Hand ist die Teil-Datei weg, dann geht es von vorn
  los. Ohne `resume` setzt der Kern nie fort – dass der Anfang zufällig passt, heißt nicht, dass er von dieser
  Datei stammt.
- **Ordner und Downloads bauen auf dem auf, was der Kern selbst vom Versuch davor weiß** (`transfers::Trail`).
  Bei einem Ordner sagt die Zahl der App nichts über die einzelne Datei (mehrere liefen nebeneinander), und beim
  Download liegt die Quelle auf dem Server – ob sie noch dieselbe ist, kann nur der Kern sehen. Deshalb hält
  jeder Transfer je Datei fest, wie weit sie kam, zusammen mit Größe und Änderungszeit ihrer Quelle:
  vollständig übertragen, beim Upload so viele Bytes geschickt, beim Download die Teil-Datei liegt noch hier.
  Endet der Transfer nicht gut, bleibt diese Spur im Kern (`Shared::trails`, höchstens 32 Transfers), benannt
  nach Richtung, lokalem Pfad, Pfad auf dem Server und Zielnamen – daran erkennt der Kern den neuen Versuch
  wieder. Der nimmt sie heraus und geht Datei für Datei vor:
  - Ist die Quelle nicht mehr dieselbe (andere Größe oder Zeit; nennt der Server keine Zeit, gilt sie nie als
    dieselbe), wird die Datei von vorn übertragen.
  - War sie vollständig und liegt am Ziel noch in dieser Größe, wird sie übersprungen. Das kostet keine Anfrage
    mehr als bisher: Beim Upload fragt der Kern ohnehin, ob es die Datei gibt, beim Download sagt es die
    Liste des Verzeichnisses.
  - War sie unterwegs, geht es in ihr weiter: beim Upload wie bei der einzelnen Datei (`resume_point`, mit der
    Zahl aus der Spur), beim Download am Ende der Teil-Datei. Dort braucht es keinen Vergleich des Inhalts – die
    Teil-Datei hat der Kern selbst geschrieben, und er schreibt sie nach einem Fehler zu Ende auf die Platte,
    bevor er das Ende des Transfers meldet.
  Was die Spur von Dateien noch weiß, die der neue Versuch gar nicht erreicht hat, gilt für den übernächsten
  weiter. Ein Transfer **ohne** `resume` nimmt die Spur ebenfalls heraus und lässt sie fallen: Ein neuer Transfer
  derselben Dateien („überschreiben?“ ist gefragt und beantwortet) macht ungültig, was der alte geschafft hatte.
  Fällt eine Spur – ersetzt, verdrängt, nach einem guten Ende, oder weil die App den Transfer nicht mehr zeigt –,
  verschwinden mit ihr die Teil-Dateien, die nur ihretwegen liegen geblieben sind: Nichts bleibt auf der Platte,
  was niemand mehr fortsetzen kann.
- **Über einen Neustart hinweg** gilt dasselbe, auf beide Seiten verteilt:
  - Der Kern schreibt seine Spuren nach `trails.json` im Datenordner – die abgeschlossenen und, während ein
    Transfer läuft, auch dessen Spur (`Trails::start`/`finish`; höchstens einmal je Sekunde neu geschrieben, und
    am Ende jedes Transfers). Endet der Kern, bleiben die Spuren samt Teil-Dateien; eine unlesbare Datei heißt
    „keine Spuren“. Gelöscht wird für eine Spur nur, was auf `.nexus-part` endet – die Datei könnte von Hand
    geändert sein.
  - **Ein Download, der gerade läuft**, steht in seiner Spur als `writing` mit der Teil-Datei und der Stelle, bis
    zu der sie sicher auf der Platte steht: Alle 64 MiB und spätestens alle 5 s schreibt der Kern die Teil-Datei
    fest (`sync_data`) und rückt die Stelle vor. Stürzt die App ab – oder Windows –, kürzt der nächste Versuch
    die Teil-Datei auf diese Stelle und macht dort weiter; was dahinter stand, hat die Platte vielleicht nie
    erreicht. Ist die Teil-Datei kürzer als die Stelle, beginnt er von vorn. Das Festschreiben kostet auf dem
    eigenen Rechner kein messbares Tempo (6 GB über Loopback: rund 170 MB/s wie vorher).
  - **Ein Upload, der gerade läuft**, schreibt höchstens einmal je Sekunde mit, wie viel er geschickt hat
    (`Reached::Sent`). Nach einem Absturz ist das die Zahl, die `resume_point` braucht – beim Upload einer Datei
    geht sie der Zahl der App vor, die dann nichts weiß. Sie hinkt hinterher und nennt nur Geschicktes; weiter
    geht es wie immer am kleineren von ihr und der Größe am Server, nach dem Vergleich des letzten MiB. Die
    Größe am Server allein taugt nicht: Wird eine vorhandene Datei an Ort und Stelle überschrieben, hat sie noch
    ihre alte Länge, und ein gleiches Ende verdeckte eine geänderte Mitte.
  - Die App schreibt ihre Liste nach `transfers.json` (`SavedTransfers`): je Transfer alles, was
    `transfer.start` braucht, samt Größe und Änderungszeit einer hochgeladenen Datei, und was die Zeile zeigt.
    Gespeichert wird bei jedem Wechsel des Zustands; was gerade läuft oder wartet, steht dort als unterbrochen
    (`interrupted`) – so lässt sich nach einem Absturz wiederholen, was lief. Weiß sie dann nicht, wie weit er
    kam (0 Bytes), nennt sie trotzdem `resume` (1 – „baue auf deiner Spur auf“); beim Upload einer Datei nimmt
    der Kern die Stelle aus seiner Spur, und fehlt die, prüft er mit 1 genau das erste Byte. Downloads in
    den Arbeitsordner (Bearbeiten, Ziehen in ein anderes Programm) bleiben draußen: Den Ordner räumt der nächste
    Start weg.
  - Beim Start und nach „Fertige entfernen“ nennt die App dem Kern die Transfers, die sie noch zeigt
    (`transfer.retain`); was er von anderen noch weiß, vergisst er samt Teil-Dateien.
  - Beim Beenden hält die App zuerst alle Transfers an (`transfer.suspend`: sie enden wie bei einer abgerissenen
    Verbindung) und schließt erst dann die Tabs. Der Kern wartet beim Ende bis zu drei Sekunden, bis jeder
    Transfer seinen Stand gespeichert hat.
- Warum ein Transfer vorzeitig endet, erfährt er, bevor die Verbindung unter ihm fällt (`transfers::Halt`):
  Hat der Benutzer es so gewollt (Abbrechen, Tab geschlossen), ist er **abgebrochen** – auch wenn die Datei, die
  gerade lief, das als Fehler der Verbindung erlebt. Ein Upload räumt dann seine Teil-Datei auf dem Server weg:
  Der Kern erreichte sie später nicht mehr, wenn der Benutzer die Zeile entfernt. Ein Download behält seine
  Teil-Datei hier (`Reached::Kept`) – „Wiederholen“ macht in ihr weiter, „Fertige entfernen“ räumt sie weg
  (`transfer.retain`), ein neuer Download derselben Datei ohne `resume` ebenso. Ist die Verbindung von
  selbst zu Ende gegangen oder endet die App, ist er **gescheitert** (`connection`) und behält, womit ein neuer
  Versuch weitermachen kann. Es gilt der erste Grund: Schließt die App nach `transfer.suspend` ihre Tabs, wird
  daraus kein Abbruch mehr.
- Ob überschrieben wird, fragt die Oberfläche **vor** dem Start; der Kern überschreibt dann ohne Rückfrage.
- Endet die Sitzung, enden ihre Transfers – außer eine andere Sitzung benutzt dieselbe Verbindung mit: Dann
  laufen sie weiter und gehören fortan dieser Sitzung (`Shared::finish`). Mit der letzten Sitzung der Verbindung
  enden sie.
- Rechte ändern (`sftp.chmod`) setzt nur die zwölf Rechte-Bits; Besitzer, Größe und Zeiten bleiben. Für einen
  Link bietet die Oberfläche es nicht an – es träfe sein Ziel.

## 6. Sicherheit

- **Host-Schlüssel** (`hostkeys.rs`): Geprüft wird gegen die eigene `known_hosts` im Datenordner und gegen
  `~/.ssh/known_hosts` des Benutzers. Die Datei von OpenSSH wird nur gelesen, nie geschrieben. Die eigene hat
  Vorrang: Wer hier einem geänderten Schlüssel zustimmt, wird nicht bei jeder Verbindung erneut gewarnt, nur weil
  bei OpenSSH noch der alte steht. Im Dialog ist „Abbrechen“ die Vorgabe. Über Sprung-Server wird jeder Server
  einzeln geprüft.
- **Geheimnisse** (`secrets.rs`): Passwörter und Passphrasen liegen nur im Windows Credential Manager
  (`NexusSSH:password:<profil>` bzw. `NexusSSH:passphrase:<profil>`), und nur, wenn der Benutzer „speichern“
  ankreuzt. Die App bekommt gespeicherte Geheimnisse nie zu sehen – der Kern liest sie selbst. Ein gespeichertes
  Passwort, das der Server ablehnt, wird entfernt. Mit dem Profil gehen seine Geheimnisse.
- **Agent Forwarding** ist je Profil abgeschaltet und wird nie stillschweigend eingeschaltet – auch der Import
  übernimmt es nur, wenn `ForwardAgent yes` in der Konfiguration steht. Öffnet ein Server einen Agent-Kanal ohne
  diese Erlaubnis, lehnt der Kern ihn ab. Ein Sprung-Server bekommt den Agenten nie, auch wenn sein eigenes
  Profil es erlaubt: Die Erlaubnis gilt nur für das Ziel der Sitzung.
- **Lokales Terminal:** Gestartet wird nur eine Shell, die der Kern selbst gefunden hat; das Profil nennt eine
  Kennung, nie ein Programm oder Argumente (unter Unix nur Shells aus `/etc/shells`). Die Umgebung der Shell ist
  die der App ohne `WEBVIEW2_*`. Beim Schließen endet alles, was die Shell gestartet hat (Job-Objekt bzw. ihre
  Sitzung). Lokale Profile stehen nie im Export, und ein Import überschreibt keines.
- **Mosh:** Die Zeile, die `mosh-server` startet, ist fester Text mit geprüften Portnummern; die Antwort gilt nur
  nach einer Marke und mit genau einem `MOSH CONNECT`. UDP geht nur zu der Adresse, an der die SSH-Verbindung
  ankam. Der Schlüssel der Sitzung steht in keinem Ereignis, Protokoll oder in einer Datei. Nie über einen
  Sprung-Server, und fehlt etwas (Programm, Locale, UDP), läuft das Terminal über SSH weiter, nie unverschlüsselt.
- **Profile** enthalten kein Passwort (`ServerProfile` hat kein Feld dafür). Host und Benutzer werden geprüft
  (keine Leer- oder Steuerzeichen, kein führendes `-`), im Editor und verbindlich im Kern – auch beim Import: Ein
  Eintrag wie `HostName -oProxyCommand=…` wird übersprungen.
- **Texte des Servers** (Fragen bei keyboard-interactive, Terminal-Ausgabe, Systemname im Inspektor, Fundstellen
  der Suche) gelten als fremd: Dialoge und Listen zeigen sie als reinen Text; das Terminal läuft in der
  gehärteten Seite (§4).
- **Dateinamen des Servers** werden beim Download Teil eines lokalen Pfads. Ein böswilliger Server könnte Namen wie
  `..\..\Autostart\x.bat` melden. `transfers::local_name` macht deshalb aus jedem Namen einen einzelnen
  gewöhnlichen Namensteil: Ein `/`, `.` oder `..` wird abgelehnt (`unsafe_name`); was auf einem Server alltäglich
  ist, unter Windows aber nicht geht – `\`, `:`, `* ? " < > |`, Steuerzeichen, Punkt oder Leerzeichen am Ende,
  Gerätenamen wie `CON` –, wird durch `_` ersetzt (`backup-12:00.tar` → `backup-12_00.tar`, `CON` → `_CON`).
  Fallen dabei oder durch Groß-/Kleinschreibung zwei Namen eines Ordners zusammen, bekommt der zweite eine
  Nummer (`makefile (2)`). Das Ziel bildet allein der Kern; die App fragt ihn danach (`transfer.target`), statt
  den Namen ein zweites Mal zu bilden – für die Frage „überschreiben?“ und für den Editor. Geht eine bearbeitete
  Datei zurück, nennt die App den Namen auf dem Server (`name` in `transfer.start`).
- **Angezeigt** werden Dateinamen ohne Steuerzeichen (`Format.Visible`): Zeilenumbruch, Tabulator oder Escape
  erscheinen als ihr Zeichenbild (␊, ␉, ␛), Zeichen, die die Schreibrichtung umkehren (U+202E …), als
  Ersatzzeichen. Eine Zeile bleibt so eine Zeile, und `rechnung<U+202E>txt.exe` sieht nicht aus wie eine
  Textdatei.
- **Pipes, Geräte und Sockets** öffnet der Kern nie (`sftp::is_special`), weder zum Lesen noch zum Schreiben: Ein
  SFTP-Server bleibt beim Öffnen einer Named Pipe stehen, bis jemand am anderen Ende ist – und beantwortet dann
  auch keine andere Anfrage der Sitzung mehr. Die Liste zeigt sie mit eigenem Symbol; Bearbeiten und
  Herunterladen sind dafür aus, in einem Ordner werden sie beim Download ausgelassen.
- **Dateien zum Bearbeiten** werden nie „geöffnet“, wie Windows es bei einem Doppelklick täte: Gestartet wird
  der eingestellte Editor (`UseShellExecute` aus), die Datei ist nur sein Argument. Eine `.bat` oder `.lnk` vom
  Server wird so angezeigt, nicht ausgeführt. Die Kopie liegt in einem eigenen Ordner unter dem Namen, den der
  Kern ihr gibt (siehe oben); mehr als 32 MiB holt die App zum Bearbeiten nicht.
- **Gespeichertes Passwort vergessen** entfernt Passwort und Passphrase eines Profils aus dem Credential
  Manager, ohne das Profil zu löschen.
- `settings.json` enthält Fensterlayout, Sprache, Farbschema, Schriftgröße, den Pfad des Editors, den letzten
  Download-Ordner und die gespeicherten Befehle (Snippets) – diese im Klartext; der Dialog sagt dazu, dass
  Passwörter nicht hineingehören.

Bewusst nicht gebaut: ein **Befehlsverlauf** der App (Spec §5, „Command History“). Dafür müsste die App
mitschreiben, was getippt wird – und sähe dann auch jedes Passwort, das jemand an einem `sudo`-Prompt eingibt.
Den Verlauf führt die Shell auf dem Server (Pfeil hoch, Strg+R).

Bekannte Lücken: Geheimnisse werden im Speicher nicht gezielt überschrieben (sie laufen als JSON durch die
C#-Seite). Außerhalb von Windows gibt es noch keinen Schlüsselbund-Anschluss.

## 7. Build und Ablage

- Zielplattform: Windows x64, unpackaged, self-contained.
- Build-Ausgabe außerhalb des Repos unter `%LOCALAPPDATA%\NexusSSH.build` (`artifacts` für .NET, `cargo` für
  Rust), weil `X:` eine SMB-Freigabe ist. Überschreibbar mit `NexusArtifactsRoot`.
- Benutzerdaten unter `%LOCALAPPDATA%\NexusSSH`: `profiles.json`, `known_hosts`, `settings.json`,
  `transfers.json`, `trails.json`, `WebView2\`, `logs\app.log`. `NEXUSSSH_DATA_DIR` legt einen anderen Ordner
  fest, `NEXUSSSH_SSH_DIR` einen anderen als `~/.ssh` (beides vor allem für Tests an der laufenden App).
- Der Rust-Kern nutzt `russh` mit `ring` statt `aws-lc-rs`: baut unter Windows ohne NASM und CMake.
- CI: `.github/workflows/ci.yml` – Rust-Kern und die plattformfreien C#-Tests unter Linux, die ganze Solution
  unter Windows.
- **Update-Hinweis** (`Core/Services/UpdateService.cs`): `IUpdateService`/`GitHubUpdateService` fragt beim Start
  (höchstens 1×/Tag, Einstellung `CheckForUpdates`) `api.github.com/repos/<repo>/releases/latest` ab und
  vergleicht die Version. `UpdateCheck.Parse` ist der reine, getestete Teil; der Dienst wirft nie (offline = kein
  Update). Das Ziel-Repo steht in `App.UpdateRepository`. `ShellViewModel.AvailableUpdate` treibt eine InfoBar im
  Fenster; der Palette-Befehl `app.checkUpdates` prüft von Hand. Kein Konto, keine Daten – nur eine GET-Anfrage.

### Auslieferung: Native AOT, Installer

Ausgeliefert wird ein **Native-AOT-Build** (`PublishAot` im App-Projekt, `tools/publish-app.ps1`): kein JIT, das
Fenster steht nach rund einer Viertelsekunde. Der gewöhnliche Build (Debug, Tests) bleibt, wie er ist.

- **Listen für XAML.** WinUI bekommt Listen als COM-Objekte; den Code dafür erzeugt CsWinRT beim Übersetzen der
  App – nur für Typen, die es dort sieht. Die Listen entstehen aber in `NexusSSH.Core`. Deshalb zwei Regeln:
  Jeder Listentyp, der als `ItemsSource` an XAML geht, steht in `NexusSSH.App/AotExposedTypes.cs`; und Core gibt
  dafür eine echte `List<T>` (`BindableList.Of`) oder `ObservableCollection<T>` heraus, nie eine
  Collection-Expression für `IReadOnlyList` (die ergibt einen namenlosen Typ des Compilers). Ein Test hält die
  Laufzeittypen fest. Verstöße fallen im gewöhnlichen Build nicht auf – unter AOT stürzt die Zuweisung ab.
- **Keine Eigenschaftsnamen als Text** in XAML (`DisplayMemberPath`, `ItemsPath`, `{Binding}`): Sie würden per
  Reflexion aufgelöst. Eine Profilgruppe ist deshalb selbst die Liste ihrer Profile.
- **Selbsttest.** `NexusSSH.exe --self-test` startet mit Beispieldaten in einem Fenster außerhalb des
  Bildschirms, lädt die Ansichten, benutzt Palette und Teilung, und beendet sich mit 0 oder – wenn eine Ausnahme
  auftrat – mit 3. Die CI veröffentlicht bei jedem Push den AOT-Build und ruft ihn so auf
  (`tools/self-test.ps1`): AOT-Fehler zeigen sich zum Teil erst zur Laufzeit.
- **Nicht mit ausgeliefert** werden die Debug-Symbole und die Bibliotheken des Windows App SDK für maschinelles
  Lernen (`onnxruntime.dll`, `DirectML.dll`): NexusSSH benutzt sie nicht. Der Ordner hat damit 93 MB.
- `tools/make-release.ps1` baut daraus **`NexusSSH-Setup-<version>.exe`** (Inno Setup, `installer/NexusSSH.iss`:
  Installation je Benutzer ohne Adminrechte, 23 MB).
- Das Setup entsteht lokal über `tools/make-release.ps1`; eine CI kann daraus bei einem Tag `v*` ein Release mit
  der Datei und ihrer Prüfsumme anlegen.
- Die PowerShell-Skripte unter `tools/` tragen ein BOM: Windows PowerShell 5.1 (damit ruft die CI sie auf) liest
  eine Datei ohne BOM als ANSI.

## 8. Speicher

Privater Speicher, gemessen am 2026-10-02 mit zwei verbundenen Sitzungen in geteilter Ansicht und laufender
Auslastungsanzeige:

| Prozess | Debug | Release (JIT) | Release (Native AOT) |
| --- | --- | --- | --- |
| `NexusSSH.exe` | 167 MB | 139 MB | 135 MB |
| `msedgewebview2.exe` (6 Prozesse) | 185 MB | 186 MB | 187 MB |

Der WebView2 ist der größere Posten und hängt kaum von der Zahl der Sitzungen ab – deshalb ein WebView für
alle. Native AOT spart beim Speicher kaum etwas (der Großteil ist WinUI selbst), wohl aber beim Start: Das
Fenster steht nach rund 250 ms. Noch nicht gemessen: viele Sitzungen mit vollem Verlauf (je Terminal bis zu
10 000 Zeilen).

## 9. Linux später

Eine Linux-Version ist nicht geplant, soll aber möglich bleiben. Was dafür schon gilt und so bleiben muss:

- **Der Rust-Kern ist plattformneutral.** Windows-Spezifisches steht hinter `cfg(windows)` (Credential Manager,
  Agent über Named Pipe/Pageant); die CI baut und testet den Kern unter Linux, einschließlich des Agenten über
  einen Unix-Socket. Es fehlt ein Schlüsselbund für Linux (Secret Service) – bis dahin hält `MemorySecretStore`
  Geheimnisse nur bis zum Programmende.
- **`NexusSSH.Core` ist plattformfrei** (`net10.0`): Modelle, ViewModels, Befehle, Übersetzungen, das Protokoll
  und die Anbindung der Rust-Bibliothek (`Backend/Native`; der Name `nexus_core` wird zu `libnexus_core.so`).
  Eine Oberfläche mit einem plattformübergreifenden .NET-Toolkit könnte das alles unverändert übernehmen.
- **Neu zu bauen wäre nur das App-Projekt:** XAML-Ansichten, Dialoge und der Terminal-Wirt. Die Terminal-Seite
  (`Assets/terminal`, xterm.js) ist gewöhnliches HTML und läuft in jedem eingebetteten Browser; ihre Brücke zum
  Wirt steckt in zwei Funktionen am Anfang von `terminal.js` (`post`, `listen`).
- **Regel daraus:** Logik gehört nach Core oder in den Kern, nie in Code-Behind. Pfade nie von Hand mit `\`
  zusammensetzen. Nichts Windows-Spezifisches in Core.

## 10. Offene Punkte der Architektur

- **Bearbeitete Dateien nach dem Beenden:** Bleibt der Editor offen, wenn die App endet, ist seine Datei
  gelöscht; ein späteres Speichern lädt nichts mehr hoch.
- **Wiederholen nach dem Schließen des Tabs:** Ein Transfer überlebt seinen Tab, solange die Verbindung steht;
  „wiederholen“ kennt aber nur die Sitzung, die ihn gestartet hat, und scheitert, wenn es sie nicht mehr gibt.
