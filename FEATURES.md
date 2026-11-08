# Funktionen

Was NexusSSH heute kann – nach Bereichen, nicht nach Datum. Stand: Version 0.1.1 (Phase 3 samt Resten,
Auslieferung).

Geprüft ist alles gegen den eingebauten Testserver und das meiste an echten OpenSSH-Servern: Debian als root,
dazu Container mit 2FA, Schlüssel mit Passphrase, Alpine/BusyBox, fish und einem gewöhnlichen Benutzer, sowie
FreeBSD, OpenBSD, OpenWrt (dropbear ohne SFTP), OPNsense und OpenSSH unter Windows Server – dort als
Administrator und mit einem gewöhnlichen Konto, mit `cmd` und mit PowerShell als Anmelde-Shell. Ein gewöhnliches
Konto darf unter Windows kein WMI; die Auslastung kommt dann aus anderen Quellen (ohne Auslagerung und Durchsatz
der Platten).

## Fenster

- **Fenster merkt sich seinen Platz:** Position, Größe und „maximiert“ gelten beim nächsten Start wieder. Fehlt der
  Bildschirm von damals, geht es auf dem nächstgelegenen auf
- Dunkle Oberfläche in reinem Schwarz (AMOLED), ohne durchscheinenden Desktop: Die Bereiche liegen als abgerundete
  Karten mit Abstand auf dem schwarzen Fenster; schlichte Linien-Symbole in Titelleiste, Startseite, Dateibrowser
  und Snippets; Fensterknöpfe von Windows, eigenes Symbol
- **Einstellungen als eigene Seite** (unten in der Seitenleiste der Startseite oder Palette): links die Bereiche,
  rechts der gewählte – Allgemein, Aussehen, Verbindungen, Hinweise und Töne, Terminal, Dateien und Transfers, Tastenkürzel, Daten, Über
- **Einstellungen durchsuchen:** Ein Suchfeld oben zeigt aus allen Bereichen nur, was zum Getippten passt (jedes
  Wort muss vorkommen; Groß- und Kleinschreibung und Umlaute spielen keine Rolle). Was nicht mehr auf der
  Vorgabe steht, trägt rechts eine Marke – ein Klick darauf stellt genau diese Einstellung zurück
- **Größe der Oberfläche** stufenlos von 70 bis 160 % – zusätzlich zur Skalierung von Windows, gilt sofort und für
  alles im Fenster (Menüs, Dialoge, Terminal): in den Einstellungen, über die Palette („Oberfläche größer“,
  „kleiner“, „zurücksetzen“; Strg+Umschalt+Plus, +Minus, +0) oder mit Strg+Mausrad außerhalb des Terminals
- **Kompakte Darstellung** wählbar (dichtere Listen, kleinere Rundungen und Abstände; wirkt nach einem Neustart);
  Bereiche und Startseite blenden weich ein (abschaltbar)
- **Akzentfarbe wählbar** (Einstellungen): Himmelblau (Vorgabe für neue Installationen), wie Windows oder Violett,
  Blau, Türkis, Grün, Orange, Rot, Pink – für Fortschritt, Auswahl und Fokus; wirkt nach einem Neustart
- **Titelleiste**: links „Start“ und die Reiter der Sitzungen (als Pillen, mit Punkt für den Zustand der
  Verbindung; das × nur am aktiven und am überfahrenen Reiter; der Tooltip nennt Adresse und Zustand), in der
  Mitte die Befehlspalette, rechts die Knöpfe **Dateien** und **Snippets** (Bereich ein/aus), das Menü
  **Teilen** (rechts oder unten teilen, aufheben, gleich groß, groß schalten, an alle senden, Dateien in zwei
  Spalten) und **Suchen**; die freie Fläche zieht das Fenster. Trennen, „Erneut verbinden“ und „Als Server
  speichern“ stehen im Menü des Reiters (Rechtsklick) und in der Palette
- **„Erneut verbinden“ über dem Terminal**: Ist die Verbindung zu Ende, liegt eine Leiste mit dem Grund und dem
  Knopf über dem Terminal; der Verlauf darunter bleibt
- **Kennfarbe je Profil oder für eine ganze Gruppe** (rot, orange, gelb, grün, blau, violett): als Streifen am
  Reiter, als Farbe des Quadrats auf der Kachel und als Streifen am linken Rand jedes Terminals – auch in
  der geteilten Ansicht. Die Farbe einer Gruppe (Kontextmenü ihrer Überschrift) gilt für alles darin und darunter,
  was keine eigene hat – gegen die Verwechslung von Produktion und Test
- **Eigenes Symbol oder Buchstaben je Server und je Gruppe** (Menü „⋯“ der Kachel bzw. Kontextmenü der
  Überschrift der Gruppe): eines von zwölf Symbolen oder ein, zwei Buchstaben oder Ziffern; das Zeichen einer
  Gruppe gilt für alles darin, was kein eigenes hat, und steht klein in der Kennfarbe vor dem Namen der Gruppe.
  Im Quadrat der Kachel steht der Reihe nach: das eigene Zeichen des Servers, sonst das **Logo seines
  Betriebssystems** in dessen Markenfarbe (sobald es bekannt ist), sonst das Zeichen der Gruppe, sonst bei einem
  lokalen Terminal dessen Symbol, sonst der erste Buchstabe des Namens
- **Reiter anheften** (Rechtsklick oder Palette): Der Reiter rückt nach links, bekommt eine Nadel statt des × und
  bleibt dort – auch über einen Neustart, wenn die Tabs wiederhergestellt werden. Dazu „Alle anderen Tabs
  schließen“ (angeheftete bleiben), „Alle trennen“ und „Alle neu verbinden“
- **Terminal in eigenem Fenster**: Einen Reiter aus der Leiste weit ins Terminal oder aus dem Fenster ziehen (knapp
  unter der Leiste losgelassen bleibt er, wo er war; oder Rechtsklick, Palette) – sein
  Terminal steht dann in einem schlanken eigenen Fenster, etwa auf dem zweiten Bildschirm. Der Inhalt samt Farben
  wandert mit, die Sitzung läuft weiter. Der Reiter bleibt im Hauptfenster: Dort zeigen Dateibrowser und
  Auslastung die Sitzung wie jede andere, an der Stelle des Terminals steht, wo es ist. Das Fenster zu schließen
  holt das Terminal zurück und trennt nichts. Mit „Tabs wiederherstellen“ steht es nach einem Neustart wieder
  an seinem Platz
- **Erreichbarkeit auf der Kachel** (Einstellung, standardmäßig aus): ein Punkt zeigt, ob der Port des Servers
  antwortet; geprüft wird nur, solange die Startseite zu sehen ist, höchstens alle drei Minuten
- **Startseite mit den Servern als Kacheln** in Spalten, die die Breite füllen (bis zu vier): Favoriten als
  eigener Abschnitt oben, darunter die Gruppen; jede Kachel zeigt ein Quadrat mit Zeichen, Logo des Systems oder Anfangsbuchstabe,
  den Namen und die Adresse (Schlagworte im Tooltip). Im Kopf ein **Suchfeld**, das beim Tippen nur stehen lässt,
  was jedes Wort in Name, Host, Benutzer, Gruppe oder Schlagwort trägt, der Knopf **„+ Neuer Server“** und ein
  Menü „⋯“ mit Tabelle, Mehrere auswählen, Reihenfolge, Importieren und Lokales Terminal. Ein Klick auf eine
  Kachel verbindet; ihr Menü „⋯“ (beim Überfahren und mit dem Fokus zu sehen, auch per Rechtsklick, Menütaste
  oder Umschalt+F10) ändert, dupliziert, löscht. „Start“ (Symbol vor den Reitern) führt jederzeit dorthin
  zurück, ohne eine Sitzung zu schließen – die Tabs laufen weiter (auch per Palette: „Startseite“); die Startseite
  nimmt dann den ganzen Platz zwischen Titelleiste und Zeile unten
- **Zeile unten** für das ganze Fenster: links die Auslastung des Servers im aktiven Tab (siehe
  „Server-Details“), rechts **Details** (öffnet die Seite „Server-Details“), **Transfers n** (die Zahl dessen,
  was läuft oder wartet; ein Klick blendet den unteren Bereich mit den Transfers ein und wieder aus) und
  **Probleme n** (nur, wenn es welche gibt; ebenso mit den Problemen). Ohne aktive Sitzung und bei einer, die
  sich nicht messen lässt (lokales Terminal, Telnet, seriell), stehen dort nur die Knöpfe rechts
- Layout: Dateibrowser links, Terminal in der Mitte, **Snippets** rechts, darunter (von Haus aus zu)
  Ausgabe/Transfers/Probleme/Suche
- **Snippets rechts**: die gespeicherten Befehle mit Name und dem Anfang des Befehls; ein Suchfeld filtert nach
  Name und Text, **ein Klick führt den Befehl im aktiven Terminal aus** (auf Wunsch gleich mit Enter), Enter im
  Suchfeld den ersten Treffer, Esc springt ins Terminal zurück. Ohne verbundene Sitzung mit Shell sind die
  Einträge grau, und ein Satz sagt, warum. Der Stift einer Zeile (beim Überfahren) und „+“ öffnen die Seite
  „Snippets“ der Startseite zum Bearbeiten. Die Spalte gibt es auch für ein lokales Terminal. **Ordner**: Befehle
  ohne Ordner stehen oben, darunter jeder Ordner unter seiner Überschrift (Symbol, Name, Anzahl); ein Klick auf die
  Überschrift klappt ihn ein und aus, gemerkt auf diesem Rechner
- Bereiche ein- und ausblendbar, die Lücken zwischen den Karten ziehbar (Doppelklick = Standardgröße) und per
  Tastatur bedienbar
- **Mit der Tastatur von Bereich zu Bereich**: Strg+F6 und Strg+Umschalt+F6 wechseln zwischen Dateibrowser,
  Terminal, Snippets (ins Suchfeld) und unterem Bereich – auch aus dem Terminal heraus, wo Tab der Shell gehört. Außerhalb
  des Terminals genügt F6 bzw. Umschalt+F6
  (Pfeiltasten, Pos1); Größen bleiben erhalten
- **Kontrastdesigns und große Schrift von Windows**: Mit einem Kontrastdesign (hell oder dunkel) nimmt die App
  dessen Farben an, auch für Verlaufskurven und Balken, und folgt einem Wechsel, während sie läuft. Die
  vergrößerte Schrift aus den Bedienhilfen von Windows gilt in der ganzen Oberfläche
- Deutsch, Englisch, Französisch und Spanisch (die beiden letzten übersetzt, aber noch nicht von Muttersprachlern gegengelesen)
- Auf Wunsch kommen beim Start die Tabs wieder, die beim Beenden offen waren – samt Teilung

## Auslieferung

- **Installer** (`NexusSSH-Setup-<version>.exe`): je Benutzer, ohne Adminrechte, mit Deinstallation; läuft auch
  still, ohne Fenster (`docs/deployment.md`)
- Native-AOT-Build: kein JIT, das Fenster steht nach rund einer Viertelsekunde
- Ein Tag `v*` im Repo baut das Setup in der CI und legt ein Release an
- **Hinweis auf neue Versionen**: Beim Start (höchstens 1×/Tag, in den Einstellungen abschaltbar) sieht die App
  bei den GitHub-Releases nach und zeigt einen Hinweis mit Link zur Download-Seite, wenn eine neuere Version da
  ist. Auch von Hand über die Palette („Nach Updates suchen"). Nur ein Aufruf an GitHub – kein Konto, keine Daten

## Server

- **Startseite als Tabelle** statt als Kacheln (Menü „⋯“ im Kopf der Startseite): alle Server in einer Liste mit
  Name, Adresse, Gruppe und letzter Verbindung; ein Klick auf einen Spaltenkopf sortiert, ein zweiter dreht die
  Richtung um. Der Filter gilt auch dort
- **Mehrere Server auf einmal** (Startseite, „Mehrere auswählen“): markieren und zusammen verbinden, in eine
  Gruppe legen, Schlagworte hinzufügen oder entfernen, löschen
- **Einführung statt leerer Seite**: Solange es kein Profil gibt, zeigt die Startseite die drei ersten Schritte –
  Server anlegen oder übernehmen (mit den Knöpfen dafür), verbinden, Palette
- **Startseite mit Seitenleiste**: fünf Einträge – Server, Snippets, Schlüssel & Identitäten (eine Seite, oben
  umschaltbar), Tunnel und „Mehr“, das Abgleiche, Bekannte Server und die Übersicht aufklappt; unten die
  Einstellungen. Die Palette führt direkt zu jeder Seite
- **Gruppen in Gruppen**: `Kunde/Projekt/Prod` als Gruppe eines Profils ergibt Ordner auf der Startseite – eine
  Ebene je Ansicht, ein Klick auf den Namen führt tiefer, Brotkrumen führen zurück
- **Gruppen einklappen und ordnen**: Ein Klick auf die Überschrift klappt ein (ein kleiner Pfeil zeigt, wie es
  steht); „Nach oben“/„Nach unten“, Farbe und Zeichen der Gruppe im Kontextmenü der Überschrift (Rechtsklick,
  Menütaste, Umschalt+F10). Ein Profil wechselt die Gruppe über „In Gruppe verschieben“ oder durch Ziehen der
  Kachel auf eine Überschrift
- Profile mit Host, Port, Benutzer, Name, Gruppe, Schlagworten, Favorit, Schlüsseldatei; der Editor ist in drei
  Reiter gegliedert (Server, Anmeldung, Verbindungsweg)
- **Notizen je Server:** freier Text im Profil (Zweck, Ansprechpartner, Wartungsfenster), auf der Seite
  „Server-Details“ zu sehen und über Filter und Palette zu finden
- **Identitäten**: Benutzer und Schlüssel einmal anlegen (Startseite, „Schlüssel & Identitäten“) und in mehreren Profilen wählen –
  ändert sich der Schlüssel, ändert man ihn an einer Stelle. Sie wandern beim Export mit
- **Betriebssystem erkannt**: Beim ersten Verbinden merkt sich die App das System des Servers (Ubuntu, Debian,
  Alpine, FreeBSD …) – aus `/etc/os-release` über die Dateiverbindung, sonst aus der Auslastung; es klappt also auch
  bei Servern, die keine Befehle zulassen. Sein Logo steht im Quadrat auf der Kachel (außer der Server hat ein
  eigenes Zeichen), sein Name im Tooltip. Liefert ein Server keine Auslastung, steht der Grund unter „Ausgabe“
- Serverliste nach Gruppen auf der Startseite; anlegen, ändern, **duplizieren**, löschen
- **Reihenfolge** wählbar (Menü „⋯“ der Startseite): wie angelegt, nach Name, zuletzt verbunden zuerst (Favoriten immer vorn)
- Über **Sprung-Server** nennt das Terminal beim Verbinden den Weg („Dieser Rechner → bastion → web01“);
  scheitert der Aufbau, sagt es, an welchem Server
- **Verbindung halten**: wie oft ein Lebenszeichen an den Server geht und nach wie vielen unbeantworteten die
  Verbindung als abgerissen gilt – allgemein in den Einstellungen und je Server im Profil
- **Terminal je Server**: als welches Terminal sich die App ausgibt (`TERM`, etwa `vt100`), in welchem Zeichensatz
  der Server spricht (UTF-8, ISO-8859-1, ISO-8859-15, Windows-1252) und welche Umgebungsvariablen er bekommt
  (`LANG` …) – für alte Systeme, auf denen Umlaute oder Farben sonst kaputt ankommen
- **Import aus `~/.ssh/config`**: `HostName`, `User`, `Port`, `IdentityFile`, `ProxyJump` (auch mehrere Sprünge als
  Kette, auch ausgeschrieben als `user@host:port`), `ForwardAgent`; auch `Include` und Blöcke mit Mustern (`Host *`); auswählen, was übernommen wird; die
  Datei wird nur gelesen. Dazu `IdentitiesOnly`, `ServerAliveInterval`/`ServerAliveCountMax` (Lebenszeichen),
  `LocalForward`/`RemoteForward`/`DynamicForward` (als Tunnel, die nur für diesen Rechner lauschen),
  `Compression`, `ConnectTimeout`, `AddressFamily`, `SetEnv` – und `Match`-Blöcke nach Host, Benutzer und lokalem
  Benutzer (ein `Match exec` gilt nie: Die App startet nichts, was in einer Datei steht). Je Server nennt der
  Dialog, **was nicht übernommen wurde** (`ProxyCommand`, `Ciphers` …)
- **Nur diesen Schlüssel anbieten** (im Profil, wie `IdentitiesOnly`): keine weiteren Schlüssel aus dem Agenten –
  für Agenten und Passwort-Manager mit vielen Schlüsseln
- **Profile, die `~/.ssh/config` folgen** (Häkchen beim Import oder Auswahl im Editor eines Profils): Adresse,
  Port, Benutzer, Schlüsseldatei, Sprung-Server und Agent Forwarding kommen dann aus der Datei – beim Start,
  beim Speichern und wenige Sekunden nachdem sich die Datei (oder eine eingebundene) ändert, auch während die App
  läuft. Im Editor sind diese Felder gesperrt. Hat das Profil eine Identität, bestimmt sie Benutzer und
  Schlüssel, die Datei nur den Rest. Name, Gruppe, Farbe, Notizen und alles Übrige bleiben, wie sie im Profil
  stehen. Fehlt der Eintrag in der Datei, bleibt das Profil unverändert; lösen lässt sich die Verknüpfung im Editor
- **Sitzungen übernehmen** aus PuTTY und WinSCP (gespeicherte Sitzungen des Benutzers) und aus MobaXterm (dessen
  Konfiguration oder eine exportierte `.mxtsessions`-Datei): Name, Host, Port, Benutzer, Ordner und Schlüsseldatei
  (auch PuTTYs `.ppk`); auswählen, was übernommen wird. Nur lesend, ohne Passwörter
- **Sitzungen aus Dateien anderer Programme**: eine `ssh_config` von irgendwo (auch die, die das
  Kommandozeilenwerkzeug von Termius mit `termius export-ssh-config` schreibt), mRemoteNG (`confCons.xml`), Royal TS
  (`.rtsz`, unverschlüsselt), SecureCRT (Sitzungsdateien oder XML-Export), Remmina (`.remmina`) und Xshell (`.xsh`) –
  mehrere Dateien auf einmal, SSH und Telnet, mit Ordnern als Gruppen und ohne Passwörter. Außer MobaXterm und
  `ssh_config` nach den Formaten gebaut, noch nicht mit echten Dateien dieser Programme geprüft
- **Schlüssel erzeugen und hinterlegen**: ein neues Ed25519-Schlüsselpaar in `~/.ssh` (auf Wunsch mit Passphrase),
  der öffentliche Teil wird auf dem verbundenen Server in `authorized_keys` eingetragen und das Profil auf den
  Schlüssel umgestellt – was sonst `ssh-keygen` und `ssh-copy-id` tun. Vorhandene Dateien bleiben unberührt
- **Schlüssel verwalten**: eine Seite der Startseite mit den eigenen Schlüsseldateien (was in `~/.ssh` liegt und
  was ein Profil oder eine Identität nennt) – Art und Länge, Fingerabdruck, Kommentar, Format, ob eine Passphrase
  sie schützt und wer sie benutzt, gelesen ohne Passphrase. Die **Passphrase ändern**, setzen oder entfernen; die
  Datei behält ihr Format. **Umwandeln** zwischen den Formaten von OpenSSH und PuTTY (`.ppk`, Fassung 3) als neue
  Datei daneben, mit derselben Passphrase – die alte bleibt. Der öffentliche Schlüssel lässt sich kopieren
- **Gespeichertes Passwort vergessen**, ohne das Profil zu löschen
- **Export und Import**: Profile (samt Gruppen und Sprung-Servern), gespeicherte Befehle und Einstellungen als
  Datei, um sie auf einem anderen Rechner zu übernehmen – **ohne Passwörter**. Vor dem Import zeigt die App, was
  neu wäre und was ersetzt würde, und man wählt, was übernommen wird; ein Profil, das es schon gibt, wird ersetzt
  statt verdoppelt. Schlüsselpfade im Benutzerordner stimmen auch bei anderem Benutzernamen

## Verbindung

- **Schnellverbinden**: `benutzer@host` oder `benutzer@host:2222` in die Befehlspalette tippen und Enter – die
  Sitzung geht auf, ohne dass es einen Server dazu gibt. Das Menü des Reiters und die Palette bieten an, einen
  daraus zu machen („Diese Verbindung als Server speichern“); sonst ist er mit dem Tab wieder weg
- **Start mit Ziel**: `NexusSSH.exe benutzer@host[:port]` auf der Kommandozeile oder ein Klick auf einen Link
  `ssh://[benutzer@]host[:port]` öffnet eine Sitzung – nach einer Rückfrage, die das Ziel nennt (und sagt, ob es
  ein gespeicherter Server ist). Ein Link nennt nur Benutzer, Host und Port, nie einen Befehl oder eine Option;
  ohne Benutzer gilt der gespeicherte Server dazu, sonst wie bei `ssh` der Name des Kontos. Läuft die App schon,
  bekommt sie das Ziel, statt eine zweite zu öffnen. Links mit NexusSSH zu öffnen ist eine Wahl beim Installieren
  (von Haus aus aus)
- **Sprungliste**: Ein Rechtsklick auf das Symbol in der Taskleiste zeigt die zuletzt verbundenen Server; ein Klick
  verbindet
- **Portweiterleitung (Tunnel)**, je Server und wie bei OpenSSH: **lokal** (`ssh -L` – ein Port auf diesem Rechner
  führt zu einem Dienst hinter dem Server, etwa `localhost:5432` zur Datenbank dort), **entfernt** (`ssh -R` – ein
  Port am Server führt zu diesem Rechner) und **dynamisch** (`ssh -D` – ein SOCKS-Proxy über den Server). Ein
  Tunnel steht, solange eine Verbindung zu seinem Server steht. Die Seite „Tunnel“ der Startseite listet alle mit
  Zustand und Schalter; die Seite „Server-Details“ zeigt die der aktiven Sitzung. Gelauscht wird nur für diesen Rechner –
  für das ganze Netz nur auf ausdrücklichen Wunsch am Tunnel. Tunnel stehen nicht im Export
- **X11-Forwarding**: grafische Programme des Servers erscheinen auf dem X-Server dieses Rechners (VcXsrv, Xming);
  je Profil zu erlauben, nur für das Ziel und nie für einen Sprung-Server. Verlangt der X-Server selbst eine
  Anmeldung (VcXsrv mit `-auth`), meldet die App die Programme mit dessen Geheimnis aus der `.Xauthority`
  des Benutzers an
- **Seriell** (COM-Anschluss, nur Windows): die Konsole eines Switches oder Mikrocontrollers im Terminal –
  Schnittstelle und Geschwindigkeit im Profil, fest 8N1 ohne Flusskontrolle
- **Telnet** für Geräte, die kein SSH können (Switches, alte Router): im Profil „Telnet statt SSH“. Unverschlüsselt
  – anmelden muss man sich im Terminal selbst; Dateien, Auslastung und Sprung-Server gibt es dabei nicht
- **Lokales Terminal**: eine Shell dieses Rechners in einem Tab – ohne Verbindung, aber sonst wie jede Sitzung
  (geteilt, Suche, Farbschema, Tab umbenennen). Gestartet wird nur, was der Kern selbst
  gefunden hat: unter Windows PowerShell 7, Windows PowerShell, Eingabeaufforderung, WSL (je Distribution) und
  Git Bash, sonst die Anmelde-Shell und die Shells aus `/etc/shells`. Geöffnet wird es mit Strg+Umschalt+T, über
  die Befehlspalette (ein Eintrag je gefundener Shell), „Lokales Terminal“ im Menü „⋯“ der Startseite oder als
  gespeichertes Profil mit Shell und Startordner (fehlt der Ordner, beginnt die Shell im Benutzerordner, und ein
  Hinweis sagt es). Welche Shell Strg+Umschalt+T öffnet, steht in den Einstellungen. Ein lokaler Tab hat keinen
  Dateibrowser, keine Auslastung und keine Server-Details (die Snippets rechts gibt es); schließt man ihn,
  endet auch alles, was die Shell gestartet hat. Lokale Terminals bleiben auf diesem Rechner: nicht im Export, und
  ein Import übergeht sie
- **Mosh** je SSH-Profil („Terminal über Mosh führen“): Das Terminal läuft über UDP und übersteht Funklöcher, einen
  Wechsel des Netzes, ohne dass die Sitzung abreißt; Getipptes zeigt die App
  schon an, bevor der Server geantwortet hat (sobald er in der Zeile ein Zeichen bestätigt hat; bei langsamer
  Leitung oder im Funkloch unterstrichen, bis er es bestätigt – „Vorhersage beim Tippen“:
  automatisch bei langsamer Leitung, immer oder nie). Angemeldet wird wie immer über SSH (Schlüssel, Agent,
  Passwort, 2FA, Host-Schlüssel-Prüfung), danach startet die App `mosh-server` auf dem Server und verbindet sich mit
  ihm über UDP; die UDP-Ports lassen sich im Profil festlegen (`60001:60010`). Steht kein `mosh-server`, keine
  UTF-8-Locale oder kommt kein UDP-Paket durch (Firewall, NAT), läuft das Terminal binnen 15 s über SSH weiter,
  und ein Hinweis nennt den Grund. Der Reiter trägt ein Zeichen für Mosh, und hört das Terminal nichts vom Server,
  steht dort, seit wann. **Dateien, Auslastung und Tunnel** bleiben an der SSH-Verbindung: Reißt sie, läuft das
  Terminal weiter, und sie verbinden sich von selbst neu, sobald der Server wieder antwortet (oder sofort mit
  „Jetzt neu verbinden“) – im Ordner, in dem man war. Grenzen: nicht über einen Sprung-Server, kein Agent
  Forwarding, kein X11, nur UTF-8, und das Terminal hat keinen Verlauf zum Zurückrollen (Mosh schickt nur den
  Bildschirm). Gegenstelle ist das gewöhnliche `mosh-server` (geprüft mit 1.4.0)
- SSH über `russh`: Host-Schlüssel-Prüfung, PTY, Shell, Größenänderung, Keepalive
- Anmeldung in dieser Reihenfolge: Schlüsseldatei des Profils, SSH-Agent (OpenSSH-Agent von Windows, Pageant),
  Standardschlüssel aus `~/.ssh`, Passwort, Fragen des Servers (2FA); verschlüsselte Schlüssel mit Passphrase
- **Schlüssel in Bitwarden / Vaultwarden:** Hält der Agent den Schlüssel, den das Profil nennt, signiert der Agent –
  zuerst und ohne Frage nach der Passphrase. Als Schlüsseldatei genügt dafür der öffentliche Teil (`….pub`), so
  wie Bitwarden ihn ablegt; damit findet sich unter vielen Schlüsseln der richtige. Ohne Angabe
  werden die ersten drei des Agenten angeboten; scheitert die Anmeldung und er hielt mehr, sagt ein Hinweis,
  was zu tun ist. Ein gesperrter Agent hat gut eine Minute Zeit, sich entsperren zu lassen; bleibt er gesperrt
  oder wird die Freigabe im Passwort-Manager abgelehnt, geht die Anmeldung mit dem Nächsten weiter, und ein
  Hinweis nennt den Grund. Unterstützt und geprüft ist das mit Bitwarden – auch an einem selbst
  gehosteten Tresor (Vaultwarden); andere Passwort-Manager sind nicht vorgesehen
- **Sprung-Server** (wie `ProxyJump`): ein Profil verbindet über ein anderes, auch über mehrere hintereinander;
  jeder Server wird einzeln geprüft und hat seine eigene Anmeldung
- **Eine Verbindung für mehrere Sitzungen:** Geteilte Terminals, duplizierte Tabs und jeder weitere Tab zum selben
  Server benutzen die stehende Verbindung mit – ohne neue Anmeldung; geht das nicht (Grenze des Servers),
  verbindet die Sitzung selbst. Starten zwei Tabs zum selben Server zugleich, wartet der zweite auf die Anmeldung
  des ersten
- **Konten ohne Shell** (nur SFTP erlaubt, wie bei vielen Hostern; Shell `nologin`): Die Sitzung bleibt für
  Dateien offen – Dateibrowser und Transfers gehen, im Terminal steht, was der Server gesagt hat
- **Agent Forwarding** je Profil, standardmäßig aus; ein Sprung-Server bekommt den Agenten nie
- Unbekannter Host-Schlüssel: Rückfrage mit Fingerabdruck. Geänderter Schlüssel: Warnung mit dem bisherigen und
  dem neuen Fingerabdruck nebeneinander und der Stelle des alten Eintrags – steht er in der eigenen
  `~/.ssh/known_hosts`, sagt die Rückfrage, welche Zeile man dort selbst entfernen müsste. Server aus
  `~/.ssh/known_hosts` gelten als bekannt (die Datei wird nie geändert)
- **Host-Zertifikate**: Steht in `known_hosts` eine Zertifizierungsstelle für den Server (`@cert-authority`), wird
  ein Server, dessen Schlüssel sie unterschrieben hat, ohne Rückfrage angenommen – wie bei OpenSSH. Ein
  Zertifikat, das nicht hält (abgelaufen, anderer Name, fremde Stelle), wird gemeldet, und es gilt die gewöhnliche
  Rückfrage zum Schlüssel. Eine Stelle lässt sich auf der Startseite unter „Bekannte Server“ eintragen und wieder
  entfernen; der Verbindungstest nennt einen so vertrauten Server. Ein widerrufener Schlüssel (`@revoked`) und
  eine widerrufene Stelle gelten nie
- Der **Banner** des Servers erscheint vor der Anmeldung im Terminal (als reiner Text)
- Passwörter und Passphrasen nur in der Windows-Anmeldeinformationsverwaltung und nur auf Wunsch; nie in einer Datei
- **Ein Schlüssel mit Passphrase wird nur einmal je Start entsperrt** (abschaltbar): Er bleibt im Speicher, bis die
  App endet – nie auf der Platte; „Entsperrte Schlüssel vergessen“ in der Palette fragt beim nächsten Mal wieder
- Sitzung überlebt ihre Verbindung: Tab und Terminal-Inhalt bleiben, „Erneut verbinden“ baut neu auf
- **`exit` schließt den Tab:** Endet die Shell, nachdem darin getippt wurde (`exit`, Strg+D), geht der Tab zu.
  Wirft der Server gleich nach dem Verbinden wieder hinaus, bleibt der Tab mit der Meldung stehen
- **Automatisch neu verbinden** nach einem Abriss: mit wachsenden Pausen (2 s bis 60 s), höchstens acht Versuche;
  nicht nach „Trennen“, `exit` oder gescheiterter Anmeldung. Abschaltbar. Der Reiter zeigt, was gerade geschieht
  („2. Versuch in 8 s“) – auch wenn ein anderer Tab oder die Startseite vorn ist
- **Verbindung je Profil einstellbar:** nur IPv4 oder nur IPv6, wie lange auf den Aufbau gewartet wird,
  Komprimierung für langsame Leitungen, automatisches Wiederverbinden für diesen Server aus
- **Ältere Verfahren erlauben** je Profil, für alte Geräte, die keine heutigen sprechen (SHA-1, CBC) – mit
  Warnung, nie von selbst und nie aus einer Export-Datei
- **Scheitert der Aufbau, steht da, woran es lag** – Name nicht auflösbar, Port zu, keine Antwort, kein Weg, kein
  gemeinsames Verfahren – jeweils mit dem, was man versuchen kann
- **Verbindungstest** im Profil-Dialog: löst den Namen auf, öffnet den Port, zeigt, als was sich der Dienst
  vorstellt, den Host-Schlüssel (bekannt, unbekannt, geändert) und die angebotenen Anmeldeverfahren – ohne sich
  anzumelden und ohne etwas zu speichern
- **Antwortzeit und Traceroute** auf der Seite „Server-Details“: wie lange der Server zum Öffnen des Ports
  braucht (gemessen nur auf Klick, nie von selbst), und der Weg
  dorthin Station für Station im Reiter „Ausgabe“
- **Verlauf der Verbindungen** (Reiter „Verlauf“): Server, Beginn, Dauer und wie sie endete – nur das, nichts aus
  der Sitzung
- **Bekannte Server** in den Einstellungen ansehen (Art des Schlüssels, Fingerabdruck) und einzeln vergessen

## Terminal

- **Tab umbenennen**; auf Wunsch steht am Reiter der Titel, den der Server setzt (vim, htop, tmux)
- xterm.js: 256 Farben, 10 000 Zeilen Verlauf, Strg+C kopiert bei Markierung (sonst Abbruch), Strg+V fügt ein;
  auch Text, den ein anderes Programm „tippt“ (Auto-Type eines Passwortmanagers, Texterweiterung, Fernsteuerung),
  kommt richtig an
- Tabs, je Server beliebig viele; Tab duplizieren; Wechsel mit Strg+Tab; **Tabs verschieben** durch Ziehen
  mit der Maus oder mit Strg+Umschalt+Bild↑/Bild↓ – der verschobene Tab ist danach der aktive
- **Geteiltes Terminal**: bis zu sechzehn Sitzungen in jeder Anordnung – „nebeneinander“ (Strg+Umschalt+E) und
  „untereinander“ (Strg+Umschalt+O) teilen das aktive Terminal in der gewählten Richtung, auch innerhalb einer
  Teilung und beliebig ineinander geschachtelt (ein breites Terminal links, rechts drei übereinander). Klick
  wechselt das aktive Terminal; auch offene Tabs lassen sich dazustellen. Geteilt hat jedes Terminal eine
  schmale Kopfzeile: ein Punkt für den Zustand der Verbindung, der Name der Sitzung, bei einem groß
  geschalteten ein Zeichen davor und ein ×, das dieses Terminal aus der Teilung nimmt (sein Tab bleibt offen)
  - **Trennlinien** lassen sich mit der Maus ziehen (jede für sich) oder, angeklickt, mit den Pfeiltasten
    verschieben (Umschalt: größere Schritte, Esc zurück ins Terminal); ohne Maus wählt „Trennlinie mit den
    Pfeiltasten verschieben“ in der Palette die neben dem aktiven Terminal. Ein Doppelklick darauf gibt allen
    Teilen des Zweigs gleich viel Platz
  - **Umstellen**: Die Kopfzeile eines Terminals auf eine der vier Kanten eines anderen ziehen – es steht dann
    dort; der Rest ordnet sich neu, und kein Terminal verliert dabei seinen Verlauf
  - **Groß schalten** (Strg+Umschalt+Z oder Doppelklick auf die Kopfzeile): ein Terminal bekommt vorübergehend die
    ganze Fläche, die anderen laufen weiter; noch einmal umschalten holt sie zurück (wird nicht gespeichert)
  - **Mit der Tastatur zum Nachbarn**: Strg+Umschalt+Pfeil (belegbar wie alle Kürzel) – nur, solange mehr als ein
    Terminal zu sehen ist; sonst gehören die Tasten dem Programm im Terminal
  - „Terminals gleich groß machen“ (Befehlspalette) verteilt den Platz gleichmäßig
  - Anordnung und Größen bleiben erhalten, auch beim Neustart (Tabs wieder öffnen); eine ältere
    Einstellungsdatei öffnet sich in der Anordnung, die sie hatte
- **Flusskontrolle**: Sehr viel Ausgabe (`cat` einer großen Datei) bremst den Server, statt den Speicher zu füllen;
  die anderen Sitzungen laufen ungestört weiter
- **Suche im Terminal** (Strg+Umschalt+F): Fundstellen hervorgehoben, „3 von 12“, weiter/zurück,
  Groß-/Kleinschreibung
- **Suche über alle Terminals** im Reiter „Suche“: Trefferliste, Klick springt zur Zeile
- **Verlauf speichern**: alles, was im Terminal steht, als Textdatei
- **Ausgabe mitschreiben**: auf Befehl je Sitzung läuft die Ausgabe des Servers als lesbarer Text in eine Datei,
  bis man es wieder ausschaltet; ein roter Punkt am Reiter zeigt es. Nie von selbst, nie das Getippte
- **An mehrere Terminals zugleich tippen**: in einer geteilten Ansicht geht die Eingabe auf Wunsch an alle ihre
  Terminals – dasselbe Kommando auf mehreren Servern
- **Hinweis von Windows** (und blinkendes Taskleistensymbol), wenn im Hintergrund ein Transfer fertig wird oder
  das Terminal die Glocke läutet; abschaltbar
- **Meldungen aus dem Terminal**: Schickt ein Programm eine Meldung mit Text (OSC 9, OSC 777 – etwa am Ende eines
  langen Befehls), markiert die App den Reiter und zeigt einen Hinweis von Windows, wenn das Fenster nicht vorn
  ist – mit dem Namen des Servers davor, höchstens alle paar Sekunden (zusammen mit der Glocke); abschaltbar
- **Eine Rückfrage im Hintergrund** (eine Sitzung verbindet von selbst neu und braucht das Passwort) lässt das
  Symbol in der Taskleiste blinken; den Vordergrund nimmt sich die App dafür nicht
- **Was die Glocke tut**, ist einstellbar: den Reiter markieren, einen Ton spielen, das Symbol in der Taskleiste
  blinken lassen – jedes für sich, oder nichts davon
- **Töne bei Fehlern und Ereignissen:** Server nicht zu erreichen, Anmeldung abgelehnt, Verbindung abgerissen,
  Transfer gescheitert (von Haus aus an), auf Wunsch auch „verbunden“ und „Transfer fertig“ – jedes für sich
  schaltbar, mit einem Knopf zum Probehören, auf Wunsch nur, wenn das Fenster nicht vorn ist. Gespielt werden
  die Töne von Windows; derselbe Ton kommt höchstens alle paar Sekunden
- **Laufende tmux- und screen-Sitzungen** des Servers finden und daran anhängen: auf Befehl über die Palette
  oder, je Server gemerkt, gleich nach dem Verbinden (anbieten oder von selbst an dieselbe Sitzung)
- **Steuermodus von tmux** (`tmux -CC`, ab tmux 2.1; geprüft mit 2.1, 2.6 und 3.7): Die Fenster einer tmux-Sitzung stehen als Tabs der App –
  neue, umbenannte und beendete Fenster folgen von selbst. Einen dieser Tabs zu schließen hängt ab; die Sitzung
  läuft auf dem Server weiter und übersteht einen Abriss der Verbindung. Die Bereiche eines Fensters stehen als
  geteiltes Terminal, in jeder Anordnung bis zu sechzehn Bereichen (ein breiter links, vier rechts übereinander
  …); bei mehr zeigt das Fenster seinen aktiven Bereich. Die Aufteilung gehört tmux – umstellen und groß schalten
  geht dort nicht –, die Trennlinien dagegen lassen sich ziehen, und tmux bekommt die Größen der App. Jedes Fenster bringt seinen Verlauf
  mit (bis 2000 Zeilen). „tmux: Neues Fenster“ in der Palette; „Tab umbenennen“ benennt das Fenster bei tmux um.
  Bei sehr viel Ausgabe hält die App den Bereich an und zeichnet danach neu, und tmux bremst auch von sich aus,
  wenn die Leitung nicht nachkommt (tmux ab 3.2)
- **Gespeicherte Befehle** (Snippets): selbst angelegte Befehle, mit einem Klick in der Spalte rechts oder über
  die Palette ins aktive Terminal getippt, auf Wunsch gleich mit Enter; verwaltet auf der Seite „Snippets“ der
  Startseite. Jeder Befehl kann in einem **Ordner** stehen (eine Ebene; das Feld „Ordner“ schlägt die vorhandenen
  vor, ein neuer Name legt einen an); ↑ ↓ bzw. Alt+Pfeil verschieben ihn in seinem Ordner, ein neuer landet im
  Ordner des gewählten. Der Ordner wandert mit in den Export
- **Farbschemata**: Black (Standard: Campbell auf reinem Schwarz), Campbell, One Half Dark, Dracula, Nord, Gruvbox Dark, Solarized Dark, Tango Dark
- **Bilder im Terminal** (Sixel und das Protokoll von iTerm2; Einstellung, von Haus aus **aus**): für `img2sixel`,
  `chafa`, `imgcat` oder die Vorschau in Dateimanagern. Die Bilddaten kommen vom Server – deshalb mit Warnung und
  festen Grenzen für Größe und Speicher
- **Bild hinter dem Terminal** (Einstellungen, von Haus aus keines – dann bleibt es beim Schwarz des Farbschemas):
  PNG, JPEG, WebP oder BMP, mit einstellbarer Abdunklung, damit der Text lesbar bleibt
- **Eigene Farbschemata importieren** (Einstellungen): aus einer Datei von iTerm2 (`.itermcolors`), Windows
  Terminal (JSON) oder Xresources; sie stehen danach neben den mitgelieferten, auch in der Palette, und lassen
  sich wieder entfernen
- **Eigene Farbschemata** anlegen und ändern: zwanzig Farben mit Farbwähler und Vorschau; sie wandern im
  Export mit. **Je Profil** lässt sich ein Schema festlegen – Produktion etwa immer mit dunkelrotem Hintergrund
- Schriftgröße 8–32 (Strg +, Strg −, Strg 0, Strg+Mausrad); **Schrift wählbar** (Vorschläge oder jede installierte)
- Wie PuTTY und MobaXterm: **Markieren kopiert, Rechtsklick fügt ein** (von Haus aus an, in den Einstellungen abschaltbar)
- **Cursor und Abstände einstellbar:** Block, Strich oder Unterstrich, blinkend oder nicht; Zeilen- und
  Zeichenabstand; Ligaturen der Schrift (`=>`, `!=`) an oder aus
- **Eingefügter Text wird bereinigt** (abschaltbar): Steuerzeichen und unsichtbare Zeichen fallen weg, geschützte
  Leerzeichen werden gewöhnliche, und der Zeilenumbruch am Ende entfällt – eine eingefügte Zeile läuft erst mit
  Enter. Auf Wunsch werden Tabulatoren zu Leerzeichen
- **Anklickbare Adressen und Pfade**: Strg+Klick öffnet eine Web-Adresse im Browser und zeigt einen Pfad im
  Dateibrowser (Ordner öffnen, Datei auswählen)
- **Vorschläge beim Tippen** aus den gespeicherten Befehlen: eine kleine Liste am Cursor, Tab übernimmt;
  abschaltbar. Was getippt wird, bleibt dabei im Terminal und wird nirgends gespeichert
- **Ergänzen beim Tippen**: Unterbefehle und Schalter von `git`, `docker`, `systemctl`, `apt`, `journalctl` und
  `tar`, und hinter `cd`, `ls`, `cat`, `vim` … die Dateien und Ordner aus dem Verzeichnis der Shell (Namen mit
  Leerzeichen kommen in Anführungszeichen). Die Pfeiltaste nach rechts übernimmt, Tab bleibt der Shell; beides
  einzeln abschaltbar. Die Namen liest die App über die Dateiverbindung – verlässlich folgt das einer Shell, die
  ihr Verzeichnis meldet (OSC 7), sonst gilt es bis zum ersten `cd`. Auch eine Ebene tiefer (`cat archiv/ne…`).
  Dazu: schon das erste Wort einer Zeile aus den **Programmen des Servers**; die Adressen
  der gespeicherten Server hinter `ssh` und `scp`. Auf Wunsch nimmt auch Tab eine Ergänzung an
- **Wörter hervorheben** nach eigenen Regeln (Einstellungen): Text oder regulärer Ausdruck, je Regel eine von
  sechs Farben des Farbschemas – etwa `ERROR` rot, `WARN` gelb, IP-Adressen cyan
- Terminal leeren

## Befehlspalette

- **Tastenkürzel selbst belegen** (Einstellungen): je Befehl ändern, entfernen oder zurücksetzen; die App sagt,
  wenn ein Kürzel schon vergeben ist oder im Terminal dem Server gehört
- **Übersicht aller Tastenkürzel** (Strg+F1 oder Palette): die Hilfe-Seite an der Stelle der Kürzel, so wie sie
  gerade belegt sind; „Zum Drucken speichern …“ schreibt sie als schlichte HTML-Seite, die jeder Browser druckt
- Suchfeld in der Titelleiste (Strg+Umschalt+P): Server verbinden, zu offenen Tabs wechseln, Befehle ausführen,
  gespeicherte Befehle abschicken; `>` am Anfang zeigt nur Befehle
- Befehle: Server hinzufügen, importieren, teilen, Teilung aufheben, Terminal vergrößern, zum Nachbarn wechseln, Terminals gleich groß machen, suchen, leeren, Verlauf speichern,
  Schriftgröße, trennen, erneut verbinden, duplizieren, Tab schließen (Strg+Umschalt+W), nächster/voriger Tab, Tab
  verschieben, Dateiliste aktualisieren, versteckte Dateien, gespeicherte Befehle verwalten, Bereiche
  ein-/ausblenden, Farbschema wählen
- Dieselben Befehle im Kontextmenü eines Tabs und, fürs Teilen, im Menü „Teilen“ der Titelleiste

## Dateien (SFTP)

- Dateibrowser für den Server des aktiven Tabs: Liste (Ordner zuerst, Namen natürlich sortiert – `datei-2` vor
  `datei-10`), Navigation (hoch, zurück, vor), Links werden verfolgt; der Pfad steht immer als Feld da und lässt
  sich eintippen (`~` = Startverzeichnis, relativ zum angezeigten Verzeichnis; Enter wechselt, Esc verwirft)
- **Aufbau wie bei MobaXterm**: oben sechs Symbole ohne Text (hoch, neu laden, herunterladen, hochladen, neuer
  Ordner, löschen) und das Menü **„Ansicht und mehr“** (zurück, vor, auf dem Server suchen, Lesezeichen, Ordner
  hochladen, neue Datei, versteckte Dateien, Rechte, Datum, Baum, Sortierung, als root, Bereich ausblenden).
  Das **Kontextmenü ist kurz** – Öffnen bzw. Bearbeiten, Herunterladen, Umbenennen, Löschen, Pfad kopieren,
  Eigenschaften –, alles Übrige steht darin unter **„Mehr“** (Vorschau, Kopieren, Duplizieren, Ausschneiden,
  zum Kopieren vormerken, Einfügen, mehrere umbenennen, rückgängig, Packen, Entpacken, Vergleichen, an anderen
  Server senden, Verknüpfung anlegen, Belegung, Mitlesen, Abgleich, im Terminal öffnen, Pfad kopieren als). Keine
  Funktion fiel dabei weg. Die Liste zeigt Name und Größe unter kleinen Spaltenköpfen (das Änderungsdatum steht
  im Tooltip und in den Eigenschaften), unten die Zahl der Einträge
- **„Terminal-Ordner folgen“** (Kästchen unter dem Pfad, je Tab, von Haus aus aus): Der Dateibrowser wechselt in
  den Ordner, den die Shell meldet (OSC 7; fish von selbst, bash und zsh mit einer Zeile in der Konfiguration –
  „Wie?“ daneben führt zur Hilfe). Die App tippt dafür nie etwas in die Shell; meldet sie nichts, ist das
  Kästchen grau
- **Auf dem Server suchen**: Suchfeld im Dateibereich, findet Namen unter dem angezeigten Ordner (auch ohne
  Shell-Zugang, höchstens 500 Treffer); ein Klick auf einen Treffer wechselt in dessen Ordner und wählt ihn aus
- **Liste filtern**: Strg+F in der Dateiliste (oder das Feld unten) – übrig bleibt, was den Text im Namen hat; in der Liste lostippen springt dagegen zum Namen, wie im Explorer;
  Esc hebt den Filter auf, Enter öffnet den einzigen Treffer
- **Dateien in zwei Spalten** (einschaltbar: Menü „Teilen“ der Titelleiste oder Befehl in der Palette): links
  ein Ordner dieses Rechners, rechts der Server, beide in voller Breite an der Stelle des Terminals – das
  weiterläuft und beim Zurückschalten wieder da ist. Links: Laufwerke, Brotkrumen, Pfad eintippen, sortiert wie die
  Serverliste; neuer Ordner, Umbenennen (F2) und Löschen (Entf, in den Papierkorb) über Rechtsklick oder „…“.
  Dazwischen: Auswahl hochladen, Auswahl hierher laden oder von einer Spalte in die andere ziehen; vor dem
  Überschreiben wird gefragt. Ein Knopf gleicht die beiden angezeigten Ordner ab (Richtung wählen, Vorschau, dann
  erst Übertragung). Der Schalter und der lokale Ordner bleiben über einen Neustart erhalten
- **Lesezeichen für Ordner**, je Server: im Menü „Ansicht und mehr“ unter „Lesezeichen“ den angezeigten Ordner
  merken und später mit einem Klick dorthin springen
- **An anderen Server senden**: gewählte Dateien und Ordner in den Ordner, den eine andere Sitzung gerade zeigt –
  direkt von Server zu Server, ohne Umweg über die Platte dieses Rechners; ein abgebrochener oder angehaltener
  Versuch geht am Ziel weiter, statt von vorn zu beginnen
- **Ordner abgleichen**: einen lokalen Ordner mit dem angezeigten vergleichen und zum oder vom Server nur
  übertragen, was fehlt, anders groß oder älter ist – mit Vorschau, was geschähe, als ein Auftrag mit gemeinsamem
  Fortschritt. Gelöscht wird am Ziel nur auf ausdrücklichen Wunsch
- **Ausschlüsse**: Muster in den Einstellungen (`.git`, `node_modules`, `*.tmp`, auch Pfade wie `build/*.map`),
  die beim Übertragen eines Ordners und beim Abgleich ausgelassen werden. Liegt im Ordner etwas davon, fragt die
  App vor dem Transfer, was trotzdem mit soll – je Muster ein Kästchen, mit Zahl und Beispiel. Für einen
  einzelnen Abgleich lassen sich die Muster im Dialog ändern
- **Abgleich nach Inhalt**: auf Wunsch vergleicht der Abgleich Prüfsummen statt Größe und Uhrzeit
- **Gemerkte Abgleiche**: ein Abgleich lässt sich unter einem Namen merken (lokaler Ordner, Ordner am Server,
  Richtung, Vergleich, Ausschlüsse) und von der Startseite („Abgleiche“) oder der Befehlspalette mit einem Klick
  erneut ausführen – über eine stehende Verbindung zum Server oder einen neuen Tab, mit der gewohnten Vorschau
  davor
- **Vorschau**: Textdateien und Bilder ansehen, ohne einen Editor zu starten
- **Tempo und Restzeit** je Transfer, **alle anhalten** und später dort fortsetzen, **Obergrenze für die
  Bandbreite** aller Transfers zusammen
- **Warteschlange ordnen:** über das Menü einer Zeile einen wartenden Transfer früher, später oder als Nächstes
  laufen lassen und einen einzelnen anhalten (fortsetzen mit „Wiederholen“)
- **Größe eines Ordners** in den Eigenschaften, auf Klick vom Server ausgerechnet
- **Packen und Entpacken auf dem Server** aus dem Kontextmenü (unter „Mehr“): die Auswahl als `.tar.gz`, ein Archiv (`tar`,
  `zip`) in seinen Ordner – ohne etwas zu überschreiben; braucht ein Konto, das Befehle ausführen darf
- **Ordnen auf dem Server**, ohne den Umweg über diesen Rechner: Ausschneiden, Kopieren und Einfügen zwischen
  Ordnern (Strg+X/C/V), **Duplizieren** (Strg+D), **mehrere umbenennen** nach Muster mit Vorschau
  (Suchen/Ersetzen, `{name}`, `{ext}`, laufende Nummer) und **Rückgängig** für die letzte Umbenennung oder
  Verschiebung (Strg+Z). Liegt am Ziel schon etwas, wird gefragt
- **Belegung des Datenträgers**: was unter einem Ordner wie viel Platz braucht, das Größte zuerst, zum
  Hineingehen – „wo ist der Platz hin?“ (rechnet auf dem Server)
- **Spaltenköpfe** in beiden Listen: ein Klick auf die Überschrift sortiert (nochmal klicken kehrt um), Rechtsklick
  wählt die Spalten (Größe, Datum, Rechte, **Besitzer, Gruppe, Typ**), Ziehen am Rand ändert die Breite – Spalten
  und Breiten bleiben je Liste erhalten. Ordner stehen immer vorn; auch die lokale Liste sortiert Namen wie der
  Server (natürlich, ohne auf Groß und Klein zu achten – ein `ä` steht hinter dem `z`). Sortieren geht auch über
  das Menü „Ansicht und mehr“ (in der lokalen Liste „…“)
- **Tastatur wie im Explorer**, in der Serverliste und der lokalen: Lostippen springt zum Namen, Strg+A,
  Umschalt+Pfeile, F2 (umbenennen), F5 (aktualisieren), Entf und Umschalt+Entf, Strg+L, Alt+D oder F4 (Pfad
  eintippen), Rücktaste und Alt+Hoch (ein Ordner hinauf), Alt+Links/Rechts (zurück, vor), in der Serverliste
  Alt+Eingabe (Eigenschaften)
- **Pfad kopieren als …**: der Pfad als `sftp://benutzer@host/pfad`, als Ziel für `scp` (`benutzer@host:/pfad`, bei
  anderem Port samt `-P`) oder bei FTP als `ftp://`- bzw. `ftps://`-Link – um jemandem zu sagen, wo die Datei
  liegt. Steuerzeichen im Pfad kommen nie hindurch; Verdächtiges steht in einfachen Anführungszeichen. In der
  lokalen Liste bleibt es der bloße Pfad
- **Verknüpfungen**: Die Liste nennt, wohin ein symbolischer Link zeigt, und kennzeichnet Links ins Leere (auch für
  Sprachausgaben); die Eigenschaften zeigen das Ziel ebenfalls. Über Befehle (Konten ohne SFTP) und auf Servern,
  deren `readlink` scheitert (Synology DSM), steht kein Ziel da – dann bleibt es beim Symbol. Über FTP steht das Ziel da, wenn der Server es in seiner Liste nennt; ob es ins Leere zeigt, lässt sich dort nicht sagen
- **„Neue Datei“** (Menü „Ansicht und mehr“), **„Verknüpfung anlegen“** (symbolischer Link), **„Pfad kopieren
  als“** und **„Im Terminal öffnen“** (`cd` in den Ordner; die drei im Kontextmenü unter „Mehr“)
- **Verzeichnisbaum** über der Liste (standardmäßig aus – der Browser ist eine einzelne Liste): klappt sich bis
  zum angezeigten Ordner auf und markiert ihn, ein Klick wechselt dorthin, Unterordner werden erst beim Aufklappen
  geladen. Ein- und ausblendbar (auch über die Palette), die Höhe lässt sich ziehen; ausgeblendet fragt er den
  Server nichts
- **Versteckte Dateien** ein- und ausblendbar
- **Eigenschaften**: Größe, Zeit, Rechte (`-rw-r--r-- (644)`), Besitzer und Gruppe mit Namen; **Rechte, Besitzer
  und Gruppe ändern** wie mit `chmod` und `chown` (Name oder Nummer) – bei einem Ordner auf Wunsch **für alles
  darin**; Ordner bleiben dabei betretbar, Links werden nicht verfolgt
- Neuer Ordner, Umbenennen (überschreibt nie), Löschen (Ordner samt Inhalt, mit Rückfrage)
- **Datei bearbeiten** (Doppelklick, wie bei MobaXterm): holen, im eingestellten Editor öffnen, nach jedem
  Speichern wieder hochladen
- **Konflikt beim Bearbeiten**: Hat sich die Datei am Server geändert, seit sie geholt wurde, fragt die App vor
  dem Hochladen, statt sie zu überschreiben
- **Regeln beim Übertragen**: Gibt es am Ziel schon etwas mit demselben Namen, fragt die App einmal für alles –
  überschreiben, überspringen, was gleich groß und nicht älter ist, nur Neueres übertragen, oder beide behalten
  („Name (2)“). In Ordnern gilt die Wahl für jede Datei einzeln; sie lässt sich merken (Einstellungen). Das
  gilt für Hochladen (auch per Ziehen aus dem Explorer), Herunterladen und „An anderen Server senden“
- **Fortschritt im Symbol der Taskleiste**: ein Balken über alle laufenden Transfers zusammen, wandernd, solange
  die Menge noch nicht bekannt ist, gelb, wenn alles angehalten ist
- **Transfer-Protokoll** (Einstellungen, von Haus aus aus): Jeder beendete Transfer bekommt eine Zeile in einer
  CSV-Datei im Datenordner – wann, Richtung, Server, Name, woher, wohin, wie viel, Ergebnis (fertig, gescheitert
  mit Grund, abgebrochen, angehalten). Die Datei wird nur ergänzt; „Transfer-Protokoll anzeigen“ (auch in der
  Palette) öffnet ihren Ordner. Getrennt wird mit dem Listentrennzeichen des Rechners (in Deutschland das
  Semikolon), damit Excel die Datei beim Öffnen gleich in Spalten teilt
- **Kopieren am Server auch ohne Shell**: Darf ein Konto nur SFTP (oder ist es FTP), kopiert die App über die
  Dateiverbindung statt mit `cp`
- **Zwei Dateien vergleichen** im eingestellten Vergleichsprogramm (WinMerge, Beyond Compare, VS Code …): zwei
  Dateien des Servers, Server gegen lokale Datei, oder vorgemerkt auch zwischen zwei Servern
- **Datei mitlesen** (`tail -f`) im unteren Bereich, ohne Terminal: mit Filter, Hervorhebung und Anhalten.
  Wird die Datei rotiert (`app.log` → `app.log.1`, neue `app.log`), zeigt die Anzeige die neue weiter
- **Betrachter** für Dateien des Servers als eigene Seite: Text mit Syntaxfarben, Hex-Ansicht, Bilder und PDF;
  große Dateien werden in Stücken direkt vom Server gelesen
- **Eingebauter Editor** für kleine Textdateien (UTF-8, bis 1 MiB): Strg+S speichert auf dem Server,
  Zeilenenden und BOM bleiben; bei einer Änderung am Server inzwischen wird gefragt
- **Dateibrowser als root** (`sudo`), je Sitzung und nur auf ausdrücklichen Wunsch: ansehen, bearbeiten und
  übertragen mit den Rechten von root; deutlich gekennzeichnet, mit einem Klick zurück. Ein Passwort für `sudo`
  wird nur gefragt, wenn der Server es verlangt, und nie gespeichert
- **Dateien ohne SFTP**: Hat ein Server kein SFTP, erlaubt aber Befehle (Geräte, BusyBox), arbeitet der
  Dateibrowser über Shell-Befehle weiter – langsamer, aber mit Auflisten, Bearbeiten und Übertragen
- **Server unter Windows** (OpenSSH von Microsoft): Terminal mit `cmd.exe` oder PowerShell, Dateien über dessen
  SFTP-Server. Der Kern erkennt den Server nach dem Verbinden und schickt seine Befehle dort als PowerShell:
  **Auslastung** in der Zeile unten, auf „Server-Details“, in der Übersicht und für die Hinweise zu Schwellen (CPU samt
  Kernen, Speicher, Auslagerung, Laufwerke, Netz mit Adressen, Datenträger, Laufzeit), im Dateibrowser
  **Belegung, Kopieren und Verschieben am Server, Packen und Entpacken** (`.tar.gz` und ZIP, mit dem `tar` von
  Windows) und der **Abgleich nach Inhalt**. Nur unter Linux und den BSDs gibt es Mitlesen, „als root“,
  Protokolle und das Anhängen an tmux – das ist dort ausgeblendet. Legt ein Konto ohne
  Adminrechte eine Verknüpfung an, sagt die Meldung, dass Windows das meist nur Administratoren erlaubt. Bei
  einem Konto ohne Befehle ist alles ausgeblendet, was welche braucht; Kopieren und Duplizieren gehen dann über
  die Dateiverbindung
- **FTP/FTPS**: Ein Profil kann statt SSH einen FTP-Server meinen – nur Dateien, kein Terminal. Verschlüsselt
  mit TLS, auch die Datenverbindungen; ein Zertifikat, das Windows nicht kennt, wird mit Fingerabdruck
  nachgefragt, gemerkt und bei einer Änderung gemeldet. Unverschlüsseltes FTP gibt es nur auf ausdrücklichen
  Wunsch im Profil, mit einer Warnung bei jedem Verbinden. Dateibrowser, Transfers samt Ordnern, Fortsetzen und
  Bearbeiten arbeiten wie über SFTP. Geprüft gegen vsftpd, ProFTPD, Pure-FTPd und FileZilla Server, mit und
  ohne TLS; hält ein Server die Stelle zum Fortsetzen nicht, merkt die App das und nimmt einen sicheren
  Umweg. Ein Ordner
  überträgt über FTP zwei Dateien zugleich (Server begrenzen die Zahl der Verbindungen). Was Befehle am Server
  braucht (Packen, Kopieren am Server, Belegung, Mitlesen, als root), bietet der Dateibrowser dort
  nicht an. Auch **implizites FTPS** (TLS vom ersten Byte an, meist Port 990) als Wahl im Profil. Lässt der
  Server keine weitere Verbindung zu, steht das samt seinem Text unter „Probleme“. Für Server ohne passiven
  Modus gibt es auf Wunsch den **aktiven Modus** (der Server baut die Datenverbindungen zu diesem Rechner auf).
  Für alte Server, die Dateinamen nicht als UTF-8 schreiben, nennt das Profil die **Kodierung der Dateinamen**
  (ISO-8859-1 oder Windows-1252) – dann stehen Umlaute richtig in der Liste und kommen beim Server richtig an
- Upload und Download von Dateien und ganzen Ordnern (**„Ordner hochladen …“** oder per Ziehen aus dem
  Explorer); der Ordnerdialog beginnt beim Ziel des letzten Downloads
- **In den Explorer**: Dateien und ganze Ordner aus der Liste in ein Explorer-Fenster ziehen, oder **kopieren**
  (Strg+C, Kontextmenü) und dort einfügen – beliebig groß, auch gemischt markiert. Geladen wird erst, wenn
  abgelegt oder eingefügt ist: Der Explorer zeigt seinen Kopierdialog, die App den Transfer; „Abbrechen“ auf
  einer der beiden Seiten beendet beides. Auch ein Browser nimmt die Dateien an. Beim Ziehen zeigt Windows ein
  Bild (Datei- bzw. Ordnersymbol) und darunter, was beim Loslassen geschieht („Nach Downloads kopieren“)
- **Viele kleine Dateien**: Bis zu acht Dateien eines Ordners sind zugleich unterwegs – 3.000 kleine Dateien
  brauchen über eine Leitung mit 23 ms Laufzeit rund 45 s (`scp -r`: 300 s)
- **Viele Ordner**: Auch das Erfassen eines Baums vor dem Download (und vor dem Ziehen in den Explorer) läuft
  nebeneinander, 16 Verzeichnisse zugleich – 421 Verzeichnisse sind über dieselbe Leitung in 2,7 s erfasst statt
  in 40 s
- Transfer-Warteschlange: zwei gleichzeitig, Fortschritt mit Menge (bei lauter leeren Dateien nach Dateien:
  „12 von 30 Dateien“), Abbrechen, **Wiederholen**, Erledigte entfernen;
  ein Transfer läuft weiter, wenn sein Tab schließt, solange ein anderer Tab dieselbe Verbindung benutzt.
  „Wiederholen“ nimmt eine Verbindung, die zum selben Server steht – auch nach „Erneut verbinden“ oder aus einem
  anderen Tab
- Antwortet das Dateisystem des Servers nicht mehr, holt sich der Dateibrowser einen **neuen Kanal** auf der
  stehenden Verbindung, ohne neue Anmeldung
- Downloads und Uploads neuer Dateien über eine Teil-Datei – nie eine halbe Datei unter dem richtigen Namen; ein
  abgebrochener Upload räumt seine Teil-Datei auf dem Server weg
- **Ein abgebrochener Upload geht weiter, wo er aufgehört hat**: „Wiederholen“ schickt bei einer einzelnen Datei
  nur noch den Rest – nach einem Abbruch von Hand wie nach einer abgerissenen Verbindung, bei einer vorhandenen
  Datei wie bei einer neuen (deren Teil-Datei dann noch auf dem Server liegt). Vorher wird geprüft, dass die
  Datei hier unverändert ist und dass auf dem Server wirklich ihr Anfang liegt; sonst beginnt der Upload von
  vorn
- **Ein Ordner wird nicht noch einmal ganz übertragen**: „Wiederholen“ überspringt in beiden Richtungen die
  Dateien, die der Versuch davor vollständig übertragen hat, und setzt die fort, die unterwegs waren – jeweils
  nur, wenn die Quelle seither dieselbe ist (Größe und Änderungszeit) und am Ziel noch liegt, was dort ankam
- **Ein abgebrochener Download geht weiter** – ob die Verbindung riss oder man ihn selbst abgebrochen hat: Seine
  Teil-Datei bleibt liegen, und „Wiederholen“ lädt nur den Rest, solange die Datei auf dem Server dieselbe ist.
  Nimmt man ihn aus der Liste („Fertige entfernen“), verschwindet die Teil-Datei damit
- Die Zeile im Transfer Center nennt, wie viel vom Versuch davor schon da war („… – 142 MB vom Versuch davor“)
- **Das übersteht einen Neustart**: Gescheiterte und abgebrochene Transfers stehen nach dem nächsten Start
  wieder in der Liste, und „Wiederholen“ setzt fort wie vorher. Was beim Schließen der App noch lief, hält sie
  an und zeigt es danach als unterbrochen – es geht dort weiter, wo es stand. Downloads zum Bearbeiten und zum
  Ziehen in andere Programme bleiben draußen
- **Auch nach einem Absturz** (der App oder von Windows) geht ein Download weiter: Seine Teil-Datei wird
  unterwegs regelmäßig fest auf die Platte geschrieben, und „Wiederholen“ macht an der letzten sicheren Stelle
  weiter; Ordner überspringen, was schon vollständig war. Entfernt man die Zeile, verschwindet die Teil-Datei.
  Ein Upload geht nach einem Absturz ebenso weiter – an der Stelle, die der Server hat und die der Kern unterwegs
  mitgeschrieben hat, nach demselben Vergleich wie sonst
- Reißt die Verbindung unter einem Transfer ab, steht er als gescheitert da (mit dem Grund), nicht als
  abgebrochen; schließt man dagegen den Tab, ist er abgebrochen
- Schutz vor Dateinamen des Servers, die den Zielordner verlassen würden; Dateien vom Server werden nie ausgeführt
- **Namen, die Windows nicht kennt** (`backup-12:00.tar`, Maildir, `CON`), kommen unter einem Ersatznamen an
  (`backup-12_00.tar`, `_CON`) statt gar nicht; fallen zwei Namen eines Ordners hier zusammen (`Makefile` und
  `makefile`), bekommt der zweite eine Nummer. Eine bearbeitete Datei geht unter ihrem echten Namen zurück
- Namen mit Zeilenumbruch, Tabulator oder umgekehrter Schreibrichtung werden entschärft angezeigt (␊, ␉)
- **Pipes, Geräte und Sockets** sind als solche zu erkennen und werden nie geöffnet – das Öffnen einer Named Pipe
  hielte den Dateibrowser der Sitzung an
- Scheitert ein Transfer, steht der Grund als Satz da („Zugriff verweigert.“) statt als Fehlercode

## Auslastung und Server-Details

- **Auslastung in der Zeile unten** (wie „Remote monitoring“ bei MobaXterm, von Haus aus an): CPU und RAM mit
  Balken, Netz, Datenträger, Benutzer und Laufzeit des Servers im aktiven Tab. Das Kästchen „Überwachung“ davor
  schaltet die Messung ab (auch in den Einstellungen und per Palette); nur für SSH, nicht in „Nur das Terminal“
- **Seite „Server-Details“** („Details“ in der Zeile unten oder Palette): alles Weitere über den Server der
  aktiven Sitzung, als Seite über den Bereichen; sie schließt mit Esc, dem ×, oder wenn ein anderer Tab oder die
  Startseite vorn ist. Dazu Adresse, Gruppe, Notizen, die Tunnel dieser Verbindung mit Schalter, die
  Antwortzeit (nur auf Klick), Traceroute, „Verlauf anzeigen“ und der Weg zu den Protokollen
- Gemessen wird für Linux-, FreeBSD- und OpenBSD-Server, nur solange die Zeile unten mit eingeschalteter
  Überwachung oder die Seite „Server-Details“ den aktiven Tab zeigt. Verstummt die Messung (etwa ein hängendes
  Laufwerk auf dem Server), sagt die App das und versucht es nach wachsenden Pausen von selbst neu
- **Jeder Kern einzeln** als Balken (Linux, FreeBSD) und **woraus die Auslastung besteht**: Benutzer, System,
  Warten auf E/A, gestohlen
- **Netz je Schnittstelle**: jede Netzwerkkarte mit ihrem Durchsatz und ihren Adressen (IPv4
  und IPv6, zum Markieren und Kopieren); Enden virtueller Leitungen ohne eigene Adresse bleiben aus
- **Durchsatz der Datenträger** (Linux, FreeBSD, OpenBSD): gelesene und geschriebene Bytes je Sekunde
  über alle Platten zusammen, wie beschäftigt die meistbeschäftigte ist, der Verlauf als Kurve – und bei mehreren
  Platten jede einzeln. OpenBSD nennt Lesen und Schreiben nur zusammen; so steht es dann auch da
- **Alle Dateisysteme** mit Einhängepunkt, Belegung und Inodes; der Balken wird gelb ab 90 % und rot ab 95 % –
  auch, wenn nur die Inodes ausgehen. Dateisysteme im Speicher, snap-Abbilder und Docker-Schichten bleiben aus
- **Protokolle ansehen** (Palette: „Protokolle des Servers ansehen“): eine Seite mit dem Journal von systemd –
  nach Zeitraum, Schwere und Einheit – oder den letzten Zeilen einer Datei unter `/var/log` (auch rotierte
  `.gz`). Ein Feld filtert das Gelesene nach Text; Fehler stehen rot, Warnungen gelb; die gezeigten oder
  markierten Zeilen lassen sich kopieren. Ohne Journal (BusyBox, BSD) bleiben die Dateien
- **Mehr über das System**: Modell des Rechners, Prozessor und Zahl der Kerne, Art der Virtualisierung, Uhrzeit
  und Zeitzone des Servers – und wie weit seine Uhr von der eigenen abweicht
- **Übersicht aller offenen Sitzungen** (Startseite → „Übersicht“ oder Palette): je Sitzung eine Zeile mit CPU,
  Arbeitsspeicher, Datenträger und Last; ein Klick öffnet die Sitzung. Für alle gemessen wird nur, solange die
  Seite zu sehen ist
- **Hinweise zu Schwellen** (auf Wunsch, in den Einstellungen): meldet für offene Sitzungen, wenn ein
  Dateisystem, der Arbeitsspeicher oder die Last je Kern über dem eingestellten Wert liegen – einmal je
  Überschreitung, unter „Probleme“ und als Hinweis von Windows
- **Nur das Terminal** (F11 oder Palette): Titelleiste, Dateibrowser, Snippets, unterer Bereich und die Zeile
  unten weichen, das Fenster füllt den Bildschirm; dieselbe Taste holt alles zurück
- Server ohne `/proc` und ohne diese Zähler oder ohne erlaubte Befehle: Hinweis statt Werten

## Unterer Bereich

- Bei einer neuen Installation zu; „Transfers n“ und „Probleme n“ in der Zeile unten blenden ihn mit dem
  passenden Reiter ein und wieder aus, die Palette mit „Unteren Bereich ein-/ausblenden“
- **Ausgabe**: Protokoll der App
- **Transfers**: alle Up- und Downloads
- **Probleme**: Warnungen und Fehler (gescheiterte Verbindungen und Transfers, Hinweise des Kerns), mit Zähler im Reiter
- **Suche**: Treffer aus dem Verlauf aller Terminals

## KI-Zugriff (MCP)

Programme wie Claude Code können über MCP die Verbindungen der App benutzen, etwa um einen Fehler auf einem Server zu
suchen. Schlüssel und Passwörter bekommen sie nie.

- **Von Haus aus aus**: Einschalten und Server freigeben unter Einstellungen → KI-Zugriff. Dort steht auch die Zeile
  zum Einrichten in Claude Code. Beides gilt nur auf diesem Rechner und steht nicht im Export
- **Nur freigegebene Server**: Andere sieht ein KI-Programm gar nicht, auch nicht unter „Ausgabe“. Zeigt ein Profil
  später auf ein anderes Ziel, endet seine Freigabe. Lokale Terminals, Telnet und serielle Verbindungen sind nie
  erreichbar
- **Lesen**: Liste der Server, „Ausgabe“ und „Probleme“ der App, Verzeichnisse, Textdateien, das Journal und
  Protokolldateien, die Auslastung
- **Verbinden**: Ein KI-Programm kann zu einem freigegebenen Server verbinden. Das geschieht in einem Tab im
  Hintergrund; Rückfragen nach Passwort oder Host-Schlüssel kommen wie immer zu dir
- **Befehle nur nach Rückfrage**: Jeder Befehl steht vollständig im Dialog, „Ablehnen“ ist vorbelegt. Ohne Antwort
  binnen zwei Minuten gilt er als abgelehnt
- **Sichtbar im Terminal**: Der Befehl wird in die Shell der Sitzung getippt, du siehst ihn samt Ausgabe, und `cd` und
  Variablen bleiben erhalten. Eine angefangene Zeile geht dabei verloren. Fragt `sudo` nach dem Passwort, tippst du es
  selbst, das KI-Programm sieht es nicht. Belegt ein Programm den ganzen Bildschirm (vim, htop, less, tmux), wird nichts
  getippt und der Befehl abgelehnt. Enter drückt die App erst, wenn die Shell genau die freigegebene Zeile zeigt: Liest
  sie die Tasten anders, etwa im vi-Befehlsmodus oder an einer Passwortfrage, läuft nichts. Andere laufende Programme,
  etwa `tail -f`, erkennt die App nicht: Der Text landete dort, deshalb die Rückfrage. Überschreitet ein Befehl seine
  Frist, läuft er im Terminal weiter
- **Eigener Kanal, wo Tippen nicht geht**: bei fish und csh, Windows-Servern (Befehle sind dort PowerShell), Mosh,
  einem anderen Zeichensatz als UTF-8, dem Steuermodus von tmux und Befehlen mit Steuerzeichen. Die Rückfrage sagt, welcher
  Weg es ist
- **Sichtbar**: Jeder Aufruf steht unter „Ausgabe“, und der Reiter trägt eine Marke, solange dort gearbeitet wird
- **Grenzen**: Was ein KI-Programm liest, geht an dessen Anbieter. Text von Servern kann eine KI zu etwas verleiten,
  deshalb die Rückfragen. Jedes Programm unter deinem Windows-Konto erreicht den Zugang, solange er eingeschaltet ist

## Unter der Haube

- Rust-Kern als DLL, C#-Oberfläche in WinUI 3; alles Plattformfreie liegt außerhalb des WinUI-Projekts, damit eine
  Linux-Version möglich bleibt
- C#- und Rust-Tests (viele gegen den Testserver im selben Prozess, auch über Sprung-Server und mit einem
  SSH-Agenten); dazu lässt sich die veröffentlichte AOT-Fassung im Selbsttest starten
- Zufallstests der Eingänge von außen (ssh-config, Telnet, Antwort von `mosh-server`, Auslastung, Listen von `ls` und FTP, Import-Formate,
  Nachrichten der Terminal-Seite) bei jedem Testlauf, dazu Ziele für `cargo fuzz`
- Diagnosepaket für einen Fehlerbericht: Versionen, Protokoll, Ausgabe und Einstellungen als ZIP – Namen von
  Servern, Benutzern und Gruppen, Adressen und Pfade sind darin ersetzt oder weggelassen
- Auswahl mit Formatierung kopieren (Text, HTML und RTF mit Farben und Schrift) oder als eine Zeile, beides aus
  der Befehlspalette
- Hilfe-Seite mit den wichtigsten Handgriffen, allen Tastenkürzeln und dem, was die Version neu kann – nach einem
  Update öffnet sie sich einmal von selbst
- Rückfrage vor dem Beenden, solange Sitzungen verbunden sind oder Transfers laufen (abstellbar)
- Ausführliches Protokoll für eine Verbindung auf Wunsch: was ausgehandelt und welche Anmeldung versucht wurde –
  ohne Geheimnisse
- Server-Details: Auslagerung und Aufteilung des Speichers; Verlauf von CPU und Arbeitsspeicher über die ganze
  Sitzung mit wählbarem Zeitraum und Export als CSV
- Beim Start prüft die App, ob im Datenordner außer dem Benutzer und den Verwaltern des Rechners noch andere
  schreiben dürfen, und nennt sie unter „Probleme“
- Ein Verzeichnis, das über eine Million Einträge nennt, wird nicht weiter gelesen
- Feste Läufe gegen die veröffentlichte Fassung: Selbsttest mit echter Verbindung samt Transfer und Tempo des
  Terminals (`tools/self-test.ps1 -TestServer …`), Startzeit und Speicher gegen Obergrenzen
  (`tools/measure-start.ps1`, `tools/budgets.json`), Bedienung über UI Automation (`tools/ui-test.ps1`)
- `nexus-testserver`: SSH-Server mit nachgespielter Shell, SFTP, Weiterleitung, Banner und vorgespielter Auslastung
- Mosh über die eingecheckte Bibliothek `backend/mosh-client` ([`mosh-client-rs`](https://github.com/wandcs/mosh-client-rs),
  MIT oder Apache-2.0); was gegenüber dem Original geändert ist, steht mit Begründung und Test in
  `backend/mosh-client/PATCHES.md`
- `NexusSSH.exe --demo`: Beispieldaten ohne Netz – drei Server-Tabs und sechs Snippets, wie auf den Bildern der
  Oberfläche; `--demo-full`: die ausführlicheren Beispieldaten, mit denen die Prüfläufe arbeiten
- CI: Linux (Rust und die plattformfreien C#-Tests) und Windows (ganze Solution)

## Noch nicht

Docker, Kubernetes, Plugins, AI-Assistent (Phase 4); ein Befehlsverlauf der App
(bewusst nicht, siehe `docs/architecture.md` §6 – dafür gibt es die gespeicherten Befehle);
automatische Synchronisierung zwischen Installationen (von Hand geht es über Export und Import).
