//! Baukasten für Plugins von NexusSSH.
//!
//! Ein Plugin ist ein WebAssembly-Modul (`wasm32-unknown-unknown`) **ohne Imports**: eine reine Funktion, die JSON
//! bekommt und JSON zurückgibt. Was es am Server tun will (einen Befehl, eine Datei lesen), verlangt es in seiner
//! Antwort; die App erledigt das – nach Rückfrage, wo nötig – und ruft es mit dem Ergebnis wieder auf. Die Anleitung
//! steht in `docs/plugins.md`, ein vollständiges Beispiel in `backend/plugin-example`.
//!
//! ```ignore
//! use nexus_plugin::{Input, Kind, Output, View};
//!
//! nexus_plugin::plugin!(
//!     r#"{"format":1,"id":"me.hello","name":"Hello","version":"1.0.0","commands":[{"id":"hi","title":"Say hello"}]}"#,
//!     handle
//! );
//!
//! fn handle(input: &Input) -> Output {
//!     match input.kind() {
//!         Kind::Command => Output::new().view(View::new().title("Hello").text("Hello from a plugin")),
//!         _ => Output::new(),
//!     }
//! }
//! ```

use serde_json::{Map, Value, json};

pub use serde_json;

/// Warum das Plugin aufgerufen wird.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    /// Ein Befehl aus der Palette (welcher: [`Input::command`]), erster Schritt.
    Command,
    /// Ein weiterer Schritt desselben Befehls: das Ergebnis dessen, was das Plugin zuletzt verlangte.
    Result,
    /// Eine Sitzung wurde verbunden oder getrennt (Recht `events`).
    Event,
    /// Zeilen der Ausgabe, die auf ein Muster des Plugins passten (Recht `watch`).
    Watch,
    /// Etwas, das diese Fassung des Baukastens noch nicht kennt.
    Other,
}

/// Was das Plugin bekommt.
#[derive(Debug, Clone)]
pub struct Input(Value);

impl Input {
    /// Liest die Eingabe; was kein JSON ist, wird zu einer leeren Eingabe.
    pub fn parse(bytes: &[u8]) -> Self {
        Self(serde_json::from_slice(bytes).unwrap_or(Value::Null))
    }

    /// Die Eingabe, wie sie kam – für alles, wofür es keinen eigenen Zugriff gibt.
    pub fn raw(&self) -> &Value {
        &self.0
    }

    pub fn kind(&self) -> Kind {
        match self.0["kind"].as_str() {
            Some("command") => Kind::Command,
            Some("result") => Kind::Result,
            Some("event") => Kind::Event,
            Some("watch") => Kind::Watch,
            _ => Kind::Other,
        }
    }

    /// Die Kennung des Befehls aus der Beschreibung (bei `Command` und `Result`), sonst leer.
    pub fn command(&self) -> &str {
        self.0["command"].as_str().unwrap_or_default()
    }

    /// Der Name des Servers der Sitzung – nur mit dem Recht `commands` oder `files`, bei einem Ereignis immer.
    pub fn server(&self) -> Option<&str> {
        self.0["session"]["server"].as_str()
    }

    /// Die Sprache der App: `de`, `en`, `fr` oder `es`.
    pub fn language(&self) -> &str {
        self.0["language"].as_str().unwrap_or("en")
    }

    /// Ob der Benutzer dem Plugin dieses Recht gewährt hat (`notify`, `commands`, `files`, `watch`, `events`).
    pub fn granted(&self, permission: &str) -> bool {
        self.0["permissions"]
            .as_array()
            .is_some_and(|granted| granted.iter().any(|known| known.as_str() == Some(permission)))
    }

    /// Die eigenen Daten des Plugins (ein JSON-Objekt, anfangs leer).
    pub fn storage(&self) -> &Value {
        &self.0["storage"]
    }

    /// Der Stand, den das Plugin mit seiner letzten Antwort mitgab (bei `Result`).
    pub fn state(&self) -> &Value {
        &self.0["state"]
    }

    /// Das Ergebnis dessen, was das Plugin zuletzt verlangte (bei `Result`).
    pub fn outcome(&self) -> Outcome<'_> {
        Outcome(&self.0["result"])
    }

    /// Das Ereignis (`connected` oder `disconnected`, bei `Event`).
    pub fn event(&self) -> Option<&str> {
        self.0["event"].as_str()
    }

    /// Die passenden Zeilen (bei `Watch`).
    pub fn lines(&self) -> impl Iterator<Item = Line<'_>> {
        self.0["lines"].as_array().into_iter().flatten().map(|line| Line {
            pattern: line["pattern"].as_str().unwrap_or_default(),
            server: line["server"].as_str().unwrap_or_default(),
            text: line["text"].as_str().unwrap_or_default(),
        })
    }

    /// So viele passende Zeilen fielen weg, weil der Korb voll war (bei `Watch`).
    pub fn dropped(&self) -> u64 {
        self.0["dropped"].as_u64().unwrap_or(0)
    }
}

/// Eine Zeile der Ausgabe, die auf ein Muster passte.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Line<'a> {
    /// Die Kennung des Musters aus der Beschreibung.
    pub pattern: &'a str,
    pub server: &'a str,
    pub text: &'a str,
}

/// Das Ergebnis eines Befehls oder einer gelesenen Datei.
#[derive(Debug, Clone, Copy)]
pub struct Outcome<'a>(&'a Value);

impl<'a> Outcome<'a> {
    /// Warum nichts kam: `permission`, `declined`, `no_session` oder ein Fehlercode des Servers. `None`, wenn es ging.
    pub fn error(&self) -> Option<&'a str> {
        self.0["error"].as_str()
    }

    /// Der Rückgabewert des Befehls (`None`, wenn er keinen meldete).
    pub fn status(&self) -> Option<i64> {
        self.0["status"].as_i64()
    }

    pub fn stdout(&self) -> &'a str {
        self.0["stdout"].as_str().unwrap_or_default()
    }

    pub fn stderr(&self) -> &'a str {
        self.0["stderr"].as_str().unwrap_or_default()
    }

    /// Ob der Befehl seine Frist überschritt.
    pub fn timed_out(&self) -> bool {
        self.0["timed_out"].as_bool().unwrap_or(false)
    }

    /// Der Inhalt einer gelesenen Datei als Text; `None` bei einer binären oder bei einem Fehler.
    pub fn text(&self) -> Option<&'a str> {
        self.0["text"].as_str()
    }

    /// Die Größe der ganzen Datei; mit `offset` liest das Plugin weiter, was über 256 KiB hinausgeht.
    pub fn size(&self) -> u64 {
        self.0["size"].as_u64().unwrap_or(0)
    }

    pub fn raw(&self) -> &'a Value {
        self.0
    }
}

/// Ein Ergebnis zum Zeigen (nur bei einem Befehl aus der Palette): Überschrift, Text, Tabelle.
#[derive(Debug, Clone, Default)]
pub struct View(Map<String, Value>);

impl View {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn title(mut self, title: &str) -> Self {
        self.0.insert("title".into(), title.into());
        self
    }

    /// Text mit Zeilenumbrüchen, bis 20 000 Zeichen.
    pub fn text(mut self, text: &str) -> Self {
        self.0.insert("text".into(), text.into());
        self
    }

    /// Eine Tabelle: bis 12 Spalten, 500 Zeilen, 200 Zeichen je Zelle.
    pub fn table<S: AsRef<str>>(mut self, columns: &[S], rows: &[Vec<String>]) -> Self {
        let columns: Vec<&str> = columns.iter().map(AsRef::as_ref).collect();
        self.0
            .insert("table".into(), json!({ "columns": columns, "rows": rows }));
        self
    }
}

/// Die Antwort des Plugins.
#[derive(Debug, Clone, Default)]
pub struct Output(Map<String, Value>);

impl Output {
    pub fn new() -> Self {
        Self::default()
    }

    /// Ein Ergebnis zeigen – nur bei einem Befehl aus der Palette; bei Ereignissen und Zeilen übergeht die App es.
    pub fn view(mut self, view: View) -> Self {
        self.0.insert("view".into(), Value::Object(view.0));
        self
    }

    /// Ein Hinweis von Windows (Recht `notify`).
    pub fn notify(mut self, text: &str) -> Self {
        self.0.insert("notify".into(), text.into());
        self
    }

    /// Ein Eintrag unter „Ausgabe“, mit dem Namen des Plugins davor (Recht `notify`).
    pub fn log(mut self, text: &str) -> Self {
        self.0.insert("log".into(), text.into());
        self
    }

    /// Ersetzt die eigenen Daten (ein JSON-Objekt bis 1 MB).
    pub fn storage(mut self, data: Value) -> Self {
        self.0.insert("storage".into(), data);
        self
    }

    /// Verlangt einen Befehl am Server (Recht `commands`): Er läuft erst, wenn der Benutzer ihn freigibt; danach ruft
    /// die App das Plugin mit `Kind::Result` wieder auf. Ohne Steuerzeichen außer Zeilenumbruch und Tabulator.
    pub fn run(mut self, command: &str, timeout_seconds: u32) -> Self {
        self.0.insert(
            "run".into(),
            json!({ "command": command, "timeout_seconds": timeout_seconds }),
        );
        self
    }

    /// Verlangt eine Datei der Sitzung (Recht `files`), ab `offset`, bis 256 KiB.
    pub fn read(mut self, path: &str, offset: u64) -> Self {
        self.0.insert("read".into(), json!({ "path": path, "offset": offset }));
        self
    }

    /// Ein Stand, den das Plugin mit dem Ergebnis zurückbekommt (bis 64 KiB JSON).
    pub fn state(mut self, state: Value) -> Self {
        self.0.insert("state".into(), state);
        self
    }

    pub fn to_json(&self) -> String {
        Value::Object(self.0.clone()).to_string()
    }
}

/// Macht aus einer Beschreibung (JSON) und einer Funktion `fn(&Input) -> Output` ein Plugin: legt die Beschreibung in
/// den Abschnitt `nexus-plugin` und exportiert `nx_alloc` und `nx_call`. Einmal im Crate, an seiner Wurzel. Wirkt nur
/// beim Bauen für `wasm32` – für Tests auf dem eigenen Rechner bleibt die Funktion eine gewöhnliche Funktion.
#[macro_export]
macro_rules! plugin {
    ($manifest:expr, $handler:path) => {
        #[cfg(target_arch = "wasm32")]
        const __NEXUS_MANIFEST: &[u8] = $manifest.as_bytes();

        #[cfg(target_arch = "wasm32")]
        #[unsafe(link_section = "nexus-plugin")]
        #[used]
        static __NEXUS_SECTION: [u8; __NEXUS_MANIFEST.len()] = $crate::__copy(__NEXUS_MANIFEST);

        #[cfg(target_arch = "wasm32")]
        #[unsafe(no_mangle)]
        pub extern "C" fn nx_alloc(len: i32) -> i32 {
            $crate::__alloc(len)
        }

        #[cfg(target_arch = "wasm32")]
        #[unsafe(no_mangle)]
        pub extern "C" fn nx_call(ptr: i32, len: i32) -> i64 {
            // SAFETY: Die App schreibt die Eingabe genau dorthin, wo nx_alloc Platz für `len` Bytes gab.
            unsafe { $crate::__call(ptr, len, $handler) }
        }
    };
}

#[doc(hidden)]
pub const fn __copy<const N: usize>(bytes: &[u8]) -> [u8; N] {
    let mut copy = [0u8; N];
    let mut i = 0;
    while i < N {
        copy[i] = bytes[i];
        i += 1;
    }
    copy
}

// Jeder Aufruf bekommt eine frische Instanz: Was hier liegen bleibt, verschwindet mit ihr.
#[doc(hidden)]
pub fn __alloc(len: i32) -> i32 {
    let mut room = Vec::<u8>::with_capacity(usize::try_from(len).unwrap_or(0));
    let at = room.as_mut_ptr();
    std::mem::forget(room);
    at as usize as i32
}

/// # Safety
/// `ptr` und `len` müssen den Platz bezeichnen, den `__alloc` gab und den die App mit der Eingabe füllte.
#[doc(hidden)]
pub unsafe fn __call(ptr: i32, len: i32, handler: fn(&Input) -> Output) -> i64 {
    let input = unsafe { std::slice::from_raw_parts(ptr as usize as *const u8, usize::try_from(len).unwrap_or(0)) };
    let answer = handler(&Input::parse(input)).to_json().into_bytes().leak();
    ((answer.as_ptr() as usize as i64) << 32) | answer.len() as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_input_is_read_for_every_kind() {
        let result = Input::parse(
            br#"{"kind":"result","command":"usage","session":{"server":"web01"},"state":{"step":1},
                "result":{"command":"df","status":0,"stdout":"x","stderr":"","timed_out":false},
                "language":"de","permissions":["commands"],"storage":{"runs":2}}"#,
        );
        assert_eq!(
            (result.kind(), result.command(), result.server(), result.language()),
            (Kind::Result, "usage", Some("web01"), "de")
        );
        assert!(result.granted("commands") && !result.granted("files"));
        assert_eq!(
            (
                result.outcome().status(),
                result.outcome().stdout(),
                result.outcome().error()
            ),
            (Some(0), "x", None)
        );
        assert_eq!(
            (result.state()["step"].as_i64(), result.storage()["runs"].as_i64()),
            (Some(1), Some(2))
        );

        let watch = Input::parse(
            br#"{"kind":"watch","lines":[{"pattern":"oom","server":"a","text":"Out of memory"}],"dropped":3}"#,
        );
        assert_eq!(watch.kind(), Kind::Watch);
        assert_eq!(
            watch.lines().collect::<Vec<_>>(),
            [Line {
                pattern: "oom",
                server: "a",
                text: "Out of memory"
            }]
        );
        assert_eq!(watch.dropped(), 3);

        assert_eq!(
            Input::parse(br#"{"kind":"event","event":"connected"}"#).event(),
            Some("connected")
        );
        assert_eq!(Input::parse(b"not json").kind(), Kind::Other);
    }

    #[test]
    fn the_output_is_the_json_the_app_reads() {
        let output = Output::new()
            .view(
                View::new()
                    .title("T")
                    .text("a\nb")
                    .table(&["A", "B"], &[vec!["1".into(), "2".into()]]),
            )
            .notify("n")
            .log("l")
            .storage(json!({ "runs": 1 }))
            .run("df -hP", 30)
            .state(json!({ "step": "df" }));
        let value: Value = serde_json::from_str(&output.to_json()).unwrap();

        assert_eq!(value["view"]["table"]["rows"][0][1], "2");
        assert_eq!(value["run"], json!({ "command": "df -hP", "timeout_seconds": 30 }));
        assert_eq!(
            (value["notify"].as_str(), value["state"]["step"].as_str()),
            (Some("n"), Some("df"))
        );
    }

    #[test]
    fn the_description_is_copied_byte_for_byte() {
        const TEXT: &[u8] = br#"{"format":1}"#;
        const COPY: [u8; TEXT.len()] = __copy(TEXT);
        assert_eq!(&COPY, TEXT);
    }
}
