//! Plugins mit Code: ein WebAssembly-Modul, ausgeführt von `wasmi` (Interpreter, kein JIT).
//!
//! Ein Plugin ist eine reine Funktion: JSON hinein, JSON heraus. Es **importiert nichts** – kein WASI, keine
//! Funktion des Kerns –, also kann es weder Dateien noch Netz noch System erreichen; ein Modul mit Importen wird
//! abgelehnt. Was es tun darf (Hinweise, später Befehle vorschlagen, Dateien lesen), sagt es in seiner Antwort; ob
//! das gilt, entscheidet die App nach den Rechten, die der Benutzer gewährt hat.
//!
//! Das Modul exportiert `memory`, `nx_alloc(len: i32) -> i32` (Platz für die Eingabe) und
//! `nx_call(ptr: i32, len: i32) -> i64` (Antwort als `ptr << 32 | len`). Seine Beschreibung (JSON) steht in einem
//! eigenen Abschnitt `nexus-plugin`. Je Aufruf entsteht eine frische Instanz – nichts bleibt im Speicher stehen –,
//! mit begrenzten Rechenschritten ([`FUEL`]) und begrenztem Speicher ([`MAX_MEMORY`]).

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use sha2::{Digest, Sha256};
use wasmi::{Config, Engine, Linker, Module, Store, StoreLimits, StoreLimitsBuilder, TrapCode};

use crate::sftp::Failure;

/// Name des Abschnitts mit der Beschreibung.
pub(crate) const SECTION: &str = "nexus-plugin";

/// Größer ist kein Plugin.
pub(crate) const MAX_FILE: u64 = 8 * 1024 * 1024;

/// So groß darf die Beschreibung sein.
const MAX_MANIFEST: usize = 64 * 1024;

/// So viel Speicher bekommt eine Instanz höchstens.
pub(crate) const MAX_MEMORY: usize = 64 * 1024 * 1024;

/// So viele Rechenschritte hat ein Aufruf – grob ein bis zwei Sekunden.
pub(crate) const FUEL: u64 = 2_000_000_000;

/// So viele Rechenschritte hat die Prüfung vor dem Installieren (eine Startfunktion läuft dabei mit).
const INSPECT_FUEL: u64 = 10_000_000;

/// So groß dürfen Eingabe und Antwort sein.
pub(crate) const MAX_INPUT: usize = 2 * 1024 * 1024;
pub(crate) const MAX_OUTPUT: usize = 1024 * 1024;

fn failed(code: &'static str, message: impl Into<String>) -> Failure {
    Failure {
        code,
        message: message.into(),
    }
}

/// Was die Vorschau vor dem Installieren braucht: die Beschreibung (JSON, wie sie im Modul steht – die App prüft
/// sie) und der SHA-256 der Datei.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Inspection {
    pub(crate) manifest: String,
    pub(crate) sha256: String,
}

fn engine() -> &'static Engine {
    static ENGINE: OnceLock<Engine> = OnceLock::new();
    ENGINE.get_or_init(|| {
        let mut config = Config::default();
        config.consume_fuel(true);
        Engine::new(&config)
    })
}

/// Liest und prüft ein Plugin aus seinen Bytes: ein gültiges Modul, nichts importiert, die drei Exporte mit den
/// richtigen Typen, eine Beschreibung in UTF-8.
pub(crate) fn inspect_bytes(bytes: &[u8]) -> Result<(Module, Inspection), Failure> {
    if bytes.len() as u64 > MAX_FILE {
        return Err(failed("plugin_invalid", "larger than 8 MB"));
    }

    let module = Module::new(engine(), bytes).map_err(|error| failed("plugin_invalid", error.to_string()))?;
    if module.imports().len() > 0 {
        return Err(failed("plugin_invalid", "a plugin must not import anything"));
    }

    // Die Exporte so, wie der Kern sie ruft – sonst scheitert erst der erste Aufruf. Zum Prüfen genügen wenige
    // Rechenschritte: Eine Startfunktion, die mehr braucht, taugt ohnehin nicht.
    let mut store = Store::new(engine(), Limits::new());
    store.limiter(|limits| &mut limits.limits);
    store
        .set_fuel(INSPECT_FUEL)
        .map_err(|error| failed("plugin_invalid", error.to_string()))?;
    let instance = Linker::new(engine())
        .instantiate_and_start(&mut store, &module)
        .map_err(|error| trap("plugin_invalid", &error))?;
    if instance.get_memory(&store, "memory").is_none()
        || instance.get_typed_func::<i32, i32>(&store, "nx_alloc").is_err()
        || instance.get_typed_func::<(i32, i32), i64>(&store, "nx_call").is_err()
    {
        return Err(failed("plugin_invalid", "missing memory, nx_alloc or nx_call"));
    }

    let mut manifests = module.custom_sections().filter(|section| section.name() == SECTION);
    let (Some(section), None) = (manifests.next(), manifests.next()) else {
        return Err(failed("plugin_invalid", "exactly one nexus-plugin section is needed"));
    };
    if section.data().len() > MAX_MANIFEST {
        return Err(failed("plugin_invalid", "the description is larger than 64 KB"));
    }
    let manifest = std::str::from_utf8(section.data())
        .map_err(|_| failed("plugin_invalid", "the description is not UTF-8"))?
        .to_owned();

    let sha256 = Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect();
    Ok((module, Inspection { manifest, sha256 }))
}

/// Liest ein Plugin aus einer Datei ([`inspect_bytes`]).
pub(crate) fn inspect(path: &str) -> Result<Inspection, Failure> {
    inspect_bytes(&read(path)?).map(|(_, inspection)| inspection)
}

/// Ruft das Plugin in `path` mit `input` auf und liefert seine Antwort. Prüft zuerst, dass die Datei noch die mit
/// `sha256` ist, die der Benutzer installiert hat – eine ausgetauschte läuft nicht.
pub(crate) fn call(path: &str, sha256: &str, input: &str) -> Result<String, Failure> {
    call_with(path, sha256, input, FUEL)
}

// Wie [`call`], mit einer anderen Zahl an Rechenschritten (die Tests: Ohne Optimierung rechnet der Interpreter langsam).
fn call_with(path: &str, sha256: &str, input: &str, fuel: u64) -> Result<String, Failure> {
    if input.len() > MAX_INPUT {
        return Err(failed("plugin_input", "the input is larger than 2 MB"));
    }

    let bytes = read(path)?;
    let module = cached(&bytes, sha256)?;

    let mut store = Store::new(engine(), Limits::new());
    store.limiter(|limits| &mut limits.limits);
    store
        .set_fuel(fuel)
        .map_err(|error| failed("plugin_trap", error.to_string()))?;
    let instance = Linker::new(engine())
        .instantiate_and_start(&mut store, &module)
        .map_err(|error| trap("plugin_trap", &error))?;
    let memory = instance
        .get_memory(&store, "memory")
        .ok_or_else(|| failed("plugin_invalid", "no memory"))?;
    let alloc = instance
        .get_typed_func::<i32, i32>(&store, "nx_alloc")
        .map_err(|error| failed("plugin_invalid", error.to_string()))?;
    let run = instance
        .get_typed_func::<(i32, i32), i64>(&store, "nx_call")
        .map_err(|error| failed("plugin_invalid", error.to_string()))?;

    let length = i32::try_from(input.len()).map_err(|_| failed("plugin_input", "too long"))?;
    let at = alloc
        .call(&mut store, length)
        .map_err(|error| trap("plugin_trap", &error))?;
    let at = usize::try_from(at).map_err(|_| failed("plugin_output", "nx_alloc returned a negative address"))?;
    memory
        .write(&mut store, at, input.as_bytes())
        .map_err(|_| failed("plugin_output", "nx_alloc returned room the plugin does not have"))?;

    let packed = run
        .call(&mut store, (at as i32, length))
        .map_err(|error| trap("plugin_trap", &error))?;
    let packed = packed as u64;
    let (from, size) = ((packed >> 32) as usize, (packed & 0xffff_ffff) as usize);
    if size > MAX_OUTPUT {
        return Err(failed("plugin_output", "the answer is larger than 1 MB"));
    }
    let mut output = vec![0u8; size];
    memory
        .read(&store, from, &mut output)
        .map_err(|_| failed("plugin_output", "the answer lies outside the plugin's memory"))?;
    String::from_utf8(output).map_err(|_| failed("plugin_output", "the answer is not UTF-8"))
}

/// Was der Store an Daten trägt: allein die Grenzen.
struct Limits {
    limits: StoreLimits,
}

impl Limits {
    fn new() -> Self {
        Self {
            limits: StoreLimitsBuilder::new()
                .memory_size(MAX_MEMORY)
                .memories(1)
                .tables(4)
                .instances(1)
                .table_elements(100_000)
                .trap_on_grow_failure(true)
                .build(),
        }
    }
}

// Woran ein Aufruf scheiterte: zu viele Rechenschritte, zu viel Speicher, oder ein anderer Abbruch des Plugins.
fn trap(code: &'static str, error: &wasmi::Error) -> Failure {
    let text = error.to_string();
    match error.as_trap_code() {
        Some(TrapCode::OutOfFuel) => failed("plugin_fuel", "the plugin ran too long"),
        Some(TrapCode::GrowthOperationLimited) => failed("plugin_memory", "the plugin wanted more than 64 MB"),
        _ if text.contains("fuel") => failed("plugin_fuel", "the plugin ran too long"),
        _ if text.contains("limit") || text.contains("grow") => failed("plugin_memory", text),
        _ => failed(code, text),
    }
}

fn read(path: &str) -> Result<Vec<u8>, Failure> {
    use std::io::Read;

    let file = std::fs::File::open(path).map_err(|error| failed("plugin_missing", error.to_string()))?;
    let mut bytes = Vec::new();
    file.take(MAX_FILE + 1)
        .read_to_end(&mut bytes)
        .map_err(|error| failed("plugin_missing", error.to_string()))?;
    if bytes.len() as u64 > MAX_FILE {
        return Err(failed("plugin_invalid", "larger than 8 MB"));
    }
    Ok(bytes)
}

// Ein übersetztes Modul je Inhalt; die Prüfsumme muss die erwartete sein. Höchstens ein paar behält der Kern.
fn cached(bytes: &[u8], sha256: &str) -> Result<Module, Failure> {
    static MODULES: OnceLock<Mutex<HashMap<String, Module>>> = OnceLock::new();
    let actual: String = Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect();
    if actual != sha256 {
        return Err(failed(
            "plugin_changed",
            "the plugin file is not the one that was installed",
        ));
    }

    let modules = MODULES.get_or_init(|| Mutex::new(HashMap::new()));
    if let Some(module) = modules
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .get(&actual)
    {
        return Ok(module.clone());
    }

    let (module, _) = inspect_bytes(bytes)?;
    let mut modules = modules.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    if modules.len() >= 16 {
        modules.clear();
    }
    modules.insert(actual, module.clone());
    Ok(module)
}

/// Für die Zufallstests: beliebige Bytes als Plugin lesen – ohne Panic.
pub(crate) fn exercise(data: &[u8]) {
    let _ = inspect_bytes(data);
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// Ein Modul aus WebAssembly-Text, dazu die Beschreibung als eigener Abschnitt.
    pub(crate) fn plugin(wat: &str, manifest: &str) -> Vec<u8> {
        let mut bytes = wat::parse_str(wat).unwrap();
        let mut section = Vec::new();
        leb(&mut section, SECTION.len());
        section.extend_from_slice(SECTION.as_bytes());
        section.extend_from_slice(manifest.as_bytes());
        bytes.push(0);
        leb(&mut bytes, section.len());
        bytes.extend_from_slice(&section);
        bytes
    }

    fn leb(out: &mut Vec<u8>, mut value: usize) {
        loop {
            let byte = (value & 0x7f) as u8;
            value >>= 7;
            if value == 0 {
                out.push(byte);
                break;
            }
            out.push(byte | 0x80);
        }
    }

    /// Ein Plugin, das seine Eingabe unverändert zurückgibt (Platz ab Adresse 1024).
    pub(crate) const ECHO: &str = r#"(module
        (memory (export "memory") 1)
        (func (export "nx_alloc") (param i32) (result i32) i32.const 1024)
        (func (export "nx_call") (param $ptr i32) (param $len i32) (result i64)
            (i64.or (i64.shl (i64.extend_i32_u (local.get $ptr)) (i64.const 32)) (i64.extend_i32_u (local.get $len)))))"#;

    fn write(name: &str, bytes: &[u8]) -> (String, String) {
        let path = std::env::temp_dir().join(format!("nexus-plugin-{}-{name}.wasm", std::process::id()));
        std::fs::write(&path, bytes).unwrap();
        let sha = Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect();
        (path.display().to_string(), sha)
    }

    #[test]
    fn a_plugin_answers_with_what_it_writes_into_its_memory() {
        let bytes = plugin(ECHO, r#"{"id":"t.echo"}"#);
        let (path, sha) = write("echo", &bytes);

        assert_eq!(inspect(&path).unwrap().manifest, r#"{"id":"t.echo"}"#);
        assert_eq!(
            call(&path, &sha, r#"{"kind":"command"}"#).unwrap(),
            r#"{"kind":"command"}"#
        );
    }

    #[test]
    fn a_plugin_that_imports_anything_is_refused() {
        let bytes = plugin(
            r#"(module (import "wasi_snapshot_preview1" "fd_write" (func (param i32 i32 i32 i32) (result i32)))
                (memory (export "memory") 1)
                (func (export "nx_alloc") (param i32) (result i32) i32.const 0)
                (func (export "nx_call") (param i32 i32) (result i64) i64.const 0))"#,
            "{}",
        );
        assert_eq!(inspect_bytes(&bytes).unwrap_err().code, "plugin_invalid");
    }

    #[test]
    fn missing_exports_or_description_are_refused() {
        let no_call = plugin(
            r#"(module (memory (export "memory") 1) (func (export "nx_alloc") (param i32) (result i32) i32.const 0))"#,
            "{}",
        );
        assert_eq!(inspect_bytes(&no_call).unwrap_err().code, "plugin_invalid");
        let no_manifest = wat::parse_str(ECHO).unwrap();
        assert_eq!(inspect_bytes(&no_manifest).unwrap_err().code, "plugin_invalid");
        assert_eq!(
            inspect_bytes(b"\0asm\x01\0\0\0garbage").unwrap_err().code,
            "plugin_invalid"
        );
    }

    #[test]
    fn a_plugin_that_runs_forever_is_stopped() {
        let bytes = plugin(
            r#"(module (memory (export "memory") 1)
                (func (export "nx_alloc") (param i32) (result i32) i32.const 0)
                (func (export "nx_call") (param i32 i32) (result i64) (loop $forever (br $forever)) i64.const 0))"#,
            "{}",
        );
        let (path, sha) = write("forever", &bytes);
        assert_eq!(
            call_with(&path, &sha, "{}", 50_000_000).unwrap_err().code,
            "plugin_fuel"
        );
    }

    #[test]
    fn a_plugin_that_wants_too_much_memory_is_stopped() {
        let bytes = plugin(
            r#"(module (memory (export "memory") 1)
                (func (export "nx_alloc") (param i32) (result i32) i32.const 0)
                (func (export "nx_call") (param i32 i32) (result i64) (drop (memory.grow (i32.const 2000))) i64.const 0))"#,
            "{}",
        );
        let (path, sha) = write("greedy", &bytes);
        assert_eq!(call(&path, &sha, "{}").unwrap_err().code, "plugin_memory");
    }

    #[test]
    fn an_answer_outside_the_memory_or_a_swapped_file_is_refused() {
        let bytes = plugin(
            r#"(module (memory (export "memory") 1)
                (func (export "nx_alloc") (param i32) (result i32) i32.const 0)
                (func (export "nx_call") (param i32 i32) (result i64) i64.const 0x0000fff000001000))"#,
            "{}",
        );
        let (path, sha) = write("outside", &bytes);
        assert_eq!(call(&path, &sha, "{}").unwrap_err().code, "plugin_output");

        let (echo, _) = write("swapped", &plugin(ECHO, "{}"));
        assert_eq!(call(&echo, &sha, "{}").unwrap_err().code, "plugin_changed");
    }
}
