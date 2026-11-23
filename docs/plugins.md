# Plugins schreiben

Ein Plugin erweitert NexusSSH um eigene Befehle: Es legt Einträge in die Befehlspalette, zeigt Ergebnisse als Text
und Tabelle, kann – mit den Rechten dazu – Befehle am Server vorschlagen, Dateien der Sitzung lesen, auf Verbinden
und Trennen hören und die Ausgabe des Terminals nach Mustern beobachten.

Ein Plugin ist eine Datei `.nxplugin`: ein **WebAssembly-Modul ohne Imports**. Es läuft abgeschottet im Kern der
App und ist eine reine Funktion – JSON hinein, JSON hinaus. Es hat keinen Zugriff auf Netz, Uhr, Zufall oder Dateien
dieses Rechners, sieht nie Passwörter oder Schlüssel und tippt nie ins Terminal. Was es am Server tun will, verlangt
es in seiner Antwort; die App erledigt das und ruft es mit dem Ergebnis wieder auf. **Jeder Befehl am Server läuft
erst, wenn der Benutzer ihn mit seinem ganzen Text freigegeben hat.**

Ein vollständiges Beispiel steht in [`backend/plugin-example`](../backend/plugin-example/src/lib.rs): Es zeigt die
Plattenbelegung des Servers (Befehl `df -hP`, Name des Systems aus `/etc/os-release`) und meldet „No space left on
device“ in der Ausgabe.

## Schnellstart in Rust

Mit dem Baukasten [`nexus-plugin`](../backend/plugin-sdk/src/lib.rs) – er erledigt Beschreibung, Speicher und den
Aufruf; das Plugin schreibt nur eine Funktion `fn(&Input) -> Output`.

```sh
rustup target add wasm32-unknown-unknown
cargo new --lib hello-plugin
```

`Cargo.toml`:

```toml
[lib]
crate-type = ["cdylib"]

[dependencies]
nexus-plugin = { git = "https://github.com/Ariazonaa/NexusSSH" }

[profile.release]
opt-level = "s"
lto = true
panic = "abort"
```

`src/lib.rs`:

```rust
use nexus_plugin::{Input, Kind, Output, View};

nexus_plugin::plugin!(
    r#"{"format":1,"id":"me.hello","name":"Hello","version":"1.0.0",
        "commands":[{"id":"hi","title":"Say hello"}]}"#,
    handle
);

fn handle(input: &Input) -> Output {
    match input.kind() {
        Kind::Command => Output::new().view(View::new().title("Hello").text("Hello from a plugin")),
        _ => Output::new(),
    }
}
```

Bauen und umbenennen:

```sh
cargo build --release --target wasm32-unknown-unknown
cp target/wasm32-unknown-unknown/release/hello_plugin.wasm hello.nxplugin
```

Installieren: **Einstellungen → Erweiterungen → Plugin hinzufügen …**. Die Vorschau zeigt Autor, Befehle, Prüfsumme
und die erbetenen Rechte; angehakt ist anfangs keines. Danach steht „Hello: Say hello“ in der Befehlspalette
(Strg+Umschalt+P).

Die App lädt nur die Datei, die installiert wurde (SHA-256). Nach einer Änderung also neu installieren: Das ersetzt
die alte Fassung, die eigenen Daten bleiben, und die Vorschau hakt die bisher gewährten Rechte vor.

## Die Beschreibung

Steht als JSON im Abschnitt `nexus-plugin` des Moduls (der Makro `plugin!` legt ihn an).

| Feld | Pflicht | Bedeutung |
|---|---|---|
| `format` | ja | immer `1` |
| `id` | ja | Kennung, klein, mit Punkten oder Bindestrichen (`me.disk-check`), höchstens 64 Zeichen |
| `name` | ja | Anzeigename, höchstens 60 Zeichen |
| `version` | ja | `1`, `1.2` … bis `1.2.3.4` |
| `author`, `description` | nein | Text für die Vorschau |
| `permissions` | nein | die erbetenen Rechte (siehe unten) |
| `commands` | nein* | bis 30 Einträge `{"id": "usage", "title": "Show disk usage"}` |
| `events` | nein | `["connected", "disconnected"]` – braucht das Recht `events` |
| `watch` | nein | bis 10 Muster – braucht das Recht `watch` |

\* Ohne Befehle nur, wenn das Plugin auf Ereignisse hört oder Muster beobachtet.

Ein Muster: `{"id": "full", "pattern": "No space left on device", "ignoreCase": true}`, mit `"regex": true` ein
regulärer Ausdruck (.NET, ohne Rückverweise und Lookarounds). Ein Ausdruck, der auf eine leere Zeile passt (`.*`),
wird abgelehnt.

## Rechte

Ohne jedes Recht darf ein Plugin Befehle in die Palette legen, Ergebnisse zeigen und eigene Daten behalten. Alles
andere erbittet es in `permissions`; der Benutzer hakt beim Installieren an, was er gewährt, und kann es jederzeit
wieder entziehen.

| Recht | Erlaubt |
|---|---|
| `notify` | Hinweise von Windows (`notify`) und Einträge unter „Ausgabe“ (`log`) |
| `commands` | Befehle am Server der aktiven Sitzung vorschlagen – jeder wird gefragt, ein „Immer erlauben“ gibt es nicht |
| `files` | Dateien der aktiven Sitzung lesen (bis 256 KiB je Schritt) |
| `watch` | Zeilen der Ausgabe bekommen, die auf ein Muster passen (auch das Echo getippter Befehle) |
| `events` | aufgerufen werden, wenn eine Sitzung verbunden oder getrennt wird |

Ein Plugin sollte nur erbitten, was es braucht: Wer viele Rechte will, wird eher nicht installiert.

## Aufrufe

Die App ruft das Plugin auf mit einem JSON-Objekt. Immer dabei: `kind`, `language` (`de`, `en`, `fr`, `es`),
`version` (der App), `permissions` (die gewährten) und `storage` (die eigenen Daten, anfangs `{}`).

| `kind` | Wann | Dazu |
|---|---|---|
| `command` | Befehl aus der Palette | `command` (Kennung); mit `commands` oder `files` auch `session.server` |
| `result` | nächster Schritt desselben Befehls | `command`, `state` (was das Plugin zuletzt mitgab), `result` |
| `event` | Sitzung verbunden oder getrennt | `event`, `session.server` |
| `watch` | passende Zeilen | `lines` (je `pattern`, `server`, `text`), `dropped` (was nicht mehr in den Korb passte) |

`result` nach einem Befehl: `command`, `status` (oder `null`), `stdout`, `stderr`, `cut`, `timed_out`. Nach einer
Datei: `path`, `size` (ganze Datei), `offset`, `length`, `binary`, `text` (nur bei Text). Ging es nicht: `error` –
`permission` (Recht fehlt), `declined` (abgelehnt), `no_session` (keine verbundene Sitzung), `no_channel` (der Server
lässt keinen weiteren Kanal zu – `MaxSessions` in `sshd_config`; Terminal, Dateien und Auslastung belegen schon
welche) oder ein anderer Fehlercode des Servers. Das ist eine Antwort, kein Fehler: Das Plugin kann damit
weitermachen.

## Die Antwort

Ein JSON-Objekt; alles ist freiwillig.

| Feld | Bedeutung |
|---|---|
| `view` | `{"title", "text", "table": {"columns": [...], "rows": [[...]]}}` – ein Ergebnis-Dialog (nur bei `command`/`result`) |
| `notify` | Hinweis von Windows (Recht `notify`) |
| `log` | Eintrag unter „Ausgabe“, mit dem Namen des Plugins davor (Recht `notify`) |
| `storage` | ersetzt die eigenen Daten (JSON-Objekt, bis 1 MB) |
| `run` | `{"command": "df -hP", "timeout_seconds": 30}` – ein Befehl am Server (Recht `commands`) |
| `read` | `{"path": "/etc/os-release", "offset": 0}` – eine Datei der Sitzung (Recht `files`) |
| `state` | beliebiges JSON (bis 64 KiB), das mit dem Ergebnis von `run` oder `read` zurückkommt |

`run` oder `read`, nie beides. Ein Befehl darf keine Steuerzeichen außer Zeilenumbruch und Tabulator enthalten und
keine Zeichen, die die Schreibrichtung drehen – der Benutzer soll sehen, was läuft. Ein Aufruf aus der Palette hat
höchstens 10 Schritte. Bei `event` und `watch` hat niemand gefragt: Dort übergeht die App `view`, `run` und `read`.

Text aus der Antwort wird gesäubert (ohne Steuerzeichen) und gekürzt: Titel 100 Zeichen, Text 20 000, Tabelle 12
Spalten, 500 Zeilen, 200 Zeichen je Zelle.

## Grenzen und Fehler

Je Aufruf bekommt das Plugin eine frische Instanz mit begrenzter Rechenzeit (Fuel), 64 MB Speicher, bis 2 MB
Eingabe und 1 MB Antwort. **Ein Plugin, das zu lange rechnet, zu viel Speicher will, abbricht oder Unbrauchbares
antwortet, wird abgeschaltet**; der Grund steht unter „Probleme“ (`plugin_fuel`, `plugin_memory`, `plugin_trap`,
`plugin_output`). Einschalten geht unter Einstellungen → Erweiterungen. Ein Plugin läuft nie zweimal zugleich.

Zum Testen: Die Logik in `handle` ist eine gewöhnliche Funktion – mit `crate-type = ["cdylib", "rlib"]` prüft
`cargo test` sie auf dem eigenen Rechner (so macht es das Beispiel).

## Ohne Rust

Jede Sprache, die ein WebAssembly-Modul **ohne Imports** erzeugt (C, Zig, AssemblyScript …), taugt. Das Modul
exportiert:

- `memory`
- `nx_alloc(len: i32) -> i32` – Platz für die Eingabe; die App schreibt sie dorthin
- `nx_call(ptr: i32, len: i32) -> i64` – verarbeitet die Eingabe; zurück kommt `ptr << 32 | len` der Antwort (UTF-8)

und trägt die Beschreibung in einem benutzerdefinierten Abschnitt namens `nexus-plugin`. Ein Modul, das irgendetwas
importiert (auch WASI), lehnt die App ab.
