//! C-ABI für die WinUI-App. Die Gegenstelle ist `NexusSSH.App/Native/NativeCore.cs`.
//!
//! Regeln für alles hier:
//! - Kein Panic überquert die Grenze: jeder Einstieg läuft in `catch_unwind`.
//! - Zeiger, die Rust herausgibt, gehören Rust. Antworten gibt die App mit [`nexus_core_free`] zurück.
//! - Zeiger, die die App hereingibt, gelten nur während des Aufrufs.
//! - Jede inkompatible Änderung erhöht [`crate::ABI_VERSION`]; `NativeCore.ExpectedAbi` in der App zieht mit.

use std::ffi::{c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use serde::Deserialize;

use crate::core::{Core, CoreConfig, EventSink};
use crate::secrets::SecretStore;

/// `kind` des Callbacks: `data` ist ein Ereignis als JSON (UTF-8), `session` ist 0.
pub const CALLBACK_EVENT: u32 = 0;
/// `kind` des Callbacks: `data` ist Terminal-Ausgabe der Sitzung `session`.
pub const CALLBACK_OUTPUT: u32 = 1;

/// Rückruf in die App. Kommt von beliebigen Threads; `data` gilt nur während des Aufrufs.
pub type Callback = extern "C" fn(user_data: *mut c_void, kind: u32, session: u64, data: *const u8, len: usize);

/// Version der C-ABI, siehe [`crate::ABI_VERSION`].
#[unsafe(no_mangle)]
pub extern "C" fn nexus_core_abi_version() -> u32 {
    crate::ABI_VERSION
}

/// Version des Kerns als NUL-terminierter UTF-8-String mit statischer Lebensdauer.
#[unsafe(no_mangle)]
pub extern "C" fn nexus_core_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr().cast()
}

struct CallbackSink {
    callback: Callback,
    user_data: usize,
    // Nach nexus_core_destroy darf die App nicht mehr gerufen werden: Sie hat ihr Gegenstück freigegeben.
    closed: AtomicBool,
}

impl CallbackSink {
    fn call(&self, kind: u32, session: u64, data: &[u8]) {
        if !self.closed.load(Ordering::Acquire) {
            (self.callback)(self.user_data as *mut c_void, kind, session, data.as_ptr(), data.len());
        }
    }
}

impl EventSink for CallbackSink {
    fn event(&self, json: &str) {
        self.call(CALLBACK_EVENT, 0, json.as_bytes());
    }

    fn output(&self, session: u64, data: &[u8]) {
        self.call(CALLBACK_OUTPUT, session, data);
    }
}

/// Der Kern, wie ihn die App als undurchsichtigen Zeiger hält.
pub struct NexusCore {
    core: Core,
    sink: Arc<CallbackSink>,
}

#[derive(Deserialize)]
struct Config {
    data_dir: PathBuf,
    /// Statt `~/.ssh` des Benutzers; fehlt die Angabe, gilt dessen Ordner.
    #[serde(default)]
    ssh_dir: Option<PathBuf>,
}

#[cfg(windows)]
fn secret_store() -> Arc<dyn SecretStore> {
    Arc::new(crate::secrets::CredentialManager)
}

// Außerhalb von Windows gibt es noch keinen Schlüsselbund-Anschluss: Geheimnisse leben nur bis zum Programmende.
#[cfg(not(windows))]
fn secret_store() -> Arc<dyn SecretStore> {
    Arc::new(crate::secrets::MemorySecretStore::default())
}

/// Startet den Kern. `config` ist JSON: `{"data_dir": "…"}`, wahlweise mit `"ssh_dir": "…"` statt `~/.ssh`.
///
/// Liefert null, wenn das scheitert; der Grund kommt vorher als `core.warning`-Ereignis über `callback`.
///
/// # Safety
/// `config` muss auf `config_len` lesbare Bytes zeigen. `callback` muss gültig bleiben und `user_data`
/// annehmen, bis [`nexus_core_destroy`] zurückgekehrt ist.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nexus_core_create(
    config: *const u8,
    config_len: usize,
    callback: Callback,
    user_data: *mut c_void,
) -> *mut NexusCore {
    catch_unwind(AssertUnwindSafe(|| {
        let sink = Arc::new(CallbackSink {
            callback,
            user_data: user_data as usize,
            closed: AtomicBool::new(false),
        });
        let fail = |message: String| {
            let event = serde_json::json!({ "event": "core.warning", "code": "start_failed", "message": message });
            sink.event(&event.to_string());
            std::ptr::null_mut()
        };

        // SAFETY: laut Vertrag zeigt `config` auf `config_len` lesbare Bytes.
        let Some(json) = (unsafe { bytes(config, config_len) }) else {
            return fail("config is null".to_owned());
        };
        let config: Config = match serde_json::from_slice(json) {
            Ok(config) => config,
            Err(error) => return fail(format!("invalid config: {error}")),
        };

        let mut core_config = CoreConfig::for_user(config.data_dir);
        if config.ssh_dir.is_some() {
            core_config.ssh_dir = config.ssh_dir;
        }

        match Core::new(core_config, secret_store(), sink.clone()) {
            Ok(core) => Box::into_raw(Box::new(NexusCore {
                core,
                sink: sink.clone(),
            })),
            Err(message) => fail(message),
        }
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// Beendet den Kern samt aller Sitzungen. Danach kommt kein Rückruf mehr.
///
/// # Safety
/// `core` muss von [`nexus_core_create`] stammen und darf danach nicht mehr benutzt werden. Kein anderer
/// Thread darf gleichzeitig eine Funktion mit diesem Zeiger aufrufen.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nexus_core_destroy(core: *mut NexusCore) {
    if core.is_null() {
        return;
    }

    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: laut Vertrag ein Zeiger aus Box::into_raw, der nur hier zurückgenommen wird.
        let core = unsafe { Box::from_raw(core) };
        core.sink.closed.store(true, Ordering::Release);
        drop(core);
    }));
}

/// Führt eine Anfrage (JSON, siehe [`crate::protocol::Request`]) aus und liefert die Antwort als JSON.
/// Die Länge steht danach in `response_len`; den Puffer gibt die App mit [`nexus_core_free`] zurück.
/// Null bei ungültigen Argumenten.
///
/// # Safety
/// `core` muss ein lebender Zeiger aus [`nexus_core_create`] sein, `request` auf `request_len` lesbare Bytes
/// zeigen und `response_len` beschreibbar sein.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nexus_core_request(
    core: *const NexusCore,
    request: *const u8,
    request_len: usize,
    response_len: *mut usize,
) -> *mut u8 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: laut Vertrag gültige Zeiger; null wird abgefangen.
        let (Some(core), Some(request)) = (unsafe { core.as_ref() }, unsafe { bytes(request, request_len) }) else {
            return std::ptr::null_mut();
        };
        if response_len.is_null() {
            return std::ptr::null_mut();
        }

        let response = match std::str::from_utf8(request) {
            Ok(json) => core.core.request(json),
            Err(_) => r#"{"ok":false,"error":"bad_request","message":"request is not UTF-8"}"#.to_owned(),
        };

        let buffer = response.into_bytes().into_boxed_slice();
        // SAFETY: `response_len` ist laut Vertrag beschreibbar und oben auf null geprüft.
        unsafe { *response_len = buffer.len() };
        Box::into_raw(buffer).cast()
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// Gibt eine Antwort von [`nexus_core_request`] frei.
///
/// # Safety
/// `data` und `len` müssen genau das Paar sein, das [`nexus_core_request`] geliefert hat, und dürfen nur
/// einmal freigegeben werden.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nexus_core_free(data: *mut u8, len: usize) {
    if !data.is_null() {
        // SAFETY: laut Vertrag ein Box<[u8]> dieser Länge aus nexus_core_request.
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(data, len)) });
    }
}

/// Eingabe des Benutzers an die Shell einer Sitzung. 1 bei Erfolg, 0 wenn es die Sitzung nicht gibt.
///
/// # Safety
/// `core` muss ein lebender Zeiger aus [`nexus_core_create`] sein und `data` auf `len` lesbare Bytes zeigen.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nexus_core_write(core: *const NexusCore, session: u64, data: *const u8, len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: laut Vertrag gültige Zeiger; null wird abgefangen.
        match (unsafe { core.as_ref() }, unsafe { bytes(data, len) }) {
            (Some(core), Some(data)) => i32::from(core.core.write(session, data)),
            _ => 0,
        }
    }))
    .unwrap_or(0)
}

/// # Safety
/// `data` muss null sein oder auf `len` lesbare Bytes zeigen, die während `'a` unverändert bleiben.
unsafe fn bytes<'a>(data: *const u8, len: usize) -> Option<&'a [u8]> {
    if data.is_null() {
        // Ein leerer Puffer darf null sein (C# liefert für leere Spans keinen Zeiger).
        return (len == 0).then_some(&[]);
    }

    // SAFETY: siehe Vertrag der Funktion.
    Some(unsafe { std::slice::from_raw_parts(data, len) })
}

#[cfg(test)]
mod tests {
    use std::ffi::CStr;
    use std::sync::Mutex;

    use super::*;

    static EVENTS: Mutex<Vec<String>> = Mutex::new(Vec::new());

    extern "C" fn record(_user_data: *mut c_void, kind: u32, _session: u64, data: *const u8, len: usize) {
        if kind == CALLBACK_EVENT {
            // SAFETY: Der Kern übergibt `len` gültige Bytes.
            let json = unsafe { std::slice::from_raw_parts(data, len) };
            EVENTS.lock().unwrap().push(String::from_utf8_lossy(json).into_owned());
        }
    }

    fn request(core: *const NexusCore, json: &str) -> String {
        let mut len = 0;
        // SAFETY: gültiger Kern, gültiger Puffer, beschreibbare Länge.
        let data = unsafe { nexus_core_request(core, json.as_ptr(), json.len(), &mut len) };
        assert!(!data.is_null());
        // SAFETY: Paar aus nexus_core_request, wird genau einmal freigegeben.
        unsafe {
            let response = String::from_utf8(std::slice::from_raw_parts(data, len).to_vec()).unwrap();
            nexus_core_free(data, len);
            response
        }
    }

    #[test]
    fn version_is_nul_terminated_and_matches_the_crate() {
        // SAFETY: Der Zeiger zeigt auf ein statisches, NUL-terminiertes Literal.
        let version = unsafe { CStr::from_ptr(nexus_core_version()) };
        assert_eq!(version.to_str().unwrap(), crate::VERSION);
        assert_eq!(nexus_core_abi_version(), crate::ABI_VERSION);
    }

    #[test]
    fn a_core_can_be_created_used_and_destroyed_through_the_c_abi() {
        let directory = std::env::temp_dir().join(format!("nexus-core-ffi-{}", std::process::id()));

        // Ein eigener Ordner statt ~/.ssh: Der Test liest so nie die Dateien des angemeldeten Benutzers.
        let ssh_dir = directory.join("ssh");
        std::fs::create_dir_all(&ssh_dir).unwrap();
        std::fs::write(ssh_dir.join("config"), "Host probe\n  HostName 10.9.9.9\n  User x\n").unwrap();
        let config = serde_json::json!({ "data_dir": directory.join("data"), "ssh_dir": ssh_dir }).to_string();

        // SAFETY: gültiger Puffer und gültiger Callback.
        let core = unsafe { nexus_core_create(config.as_ptr(), config.len(), record, std::ptr::null_mut()) };
        assert!(!core.is_null());

        assert_eq!(
            request(core, r#"{"op":"profiles.list"}"#),
            r#"{"ok":true,"profiles":[]}"#
        );
        assert!(request(core, r#"{"op":"sshconfig.read"}"#).contains(r#""alias":"probe","host":"10.9.9.9""#));
        assert!(request(core, "not json").contains(r#""error":"bad_request""#));
        assert!(
            request(core, r#"{"op":"session.resize","session":9,"cols":80,"rows":24}"#).contains("unknown_session")
        );

        // SAFETY: gültiger Kern; leerer Puffer darf null sein.
        assert_eq!(unsafe { nexus_core_write(core, 9, std::ptr::null(), 0) }, 0);

        // SAFETY: Zeiger aus nexus_core_create, danach nicht mehr benutzt.
        unsafe { nexus_core_destroy(core) };
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn null_and_garbage_arguments_do_not_crash() {
        let mut len = 0;
        // SAFETY: null wird von den Funktionen selbst abgefangen.
        unsafe {
            assert!(nexus_core_request(std::ptr::null(), std::ptr::null(), 0, &mut len).is_null());
            assert_eq!(nexus_core_write(std::ptr::null(), 1, std::ptr::null(), 0), 0);
            nexus_core_free(std::ptr::null_mut(), 0);
            nexus_core_destroy(std::ptr::null_mut());
        }

        let config = "{ not json";
        // SAFETY: gültiger Puffer und gültiger Callback.
        let core = unsafe { nexus_core_create(config.as_ptr(), config.len(), record, std::ptr::null_mut()) };
        assert!(core.is_null());
        assert!(
            EVENTS
                .lock()
                .unwrap()
                .iter()
                .any(|event| event.contains("start_failed"))
        );
    }
}
