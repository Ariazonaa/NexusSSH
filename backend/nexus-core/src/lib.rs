//! NexusSSH-Kern.
//!
//! Hier liegt alles, was mit dem Netz und mit Geheimnissen zu tun hat: SSH-Verbindungen, Profil-Speicherung,
//! der Zugriff auf den Windows Credential Manager, die Prüfung der Host-Schlüssel. Die WinUI-Oberfläche (C#)
//! lädt diese Bibliothek als `nexus_core.dll` und spricht ausschließlich über die C-ABI in [`ffi`] mit ihr.
//!
//! Aufbau:
//! - [`core`] – der Kern als Rust-Objekt: Anfragen annehmen, Sitzungen halten, Ereignisse melden
//! - [`protocol`] – die JSON-Nachrichten zwischen App und Kern
//! - `session` – eine SSH-Sitzung von der TCP-Verbindung (auch über Sprung-Server) bis zur Shell
//! - `monitor` – Auslastung des Servers für den Server-Inspektor
//! - `exec` – einzelne Befehle auf dem Server: packen, entpacken, die Liste der Prozesse
//! - `local` – das lokale Terminal: eine Shell dieses Rechners in einem Pseudo-Terminal (ConPTY, `openpty`)
//! - [`sshconfig`] – Import aus `~/.ssh/config`
//! - `sftp`, `transfers` – Dateioperationen und Up-/Downloads über den SFTP-Kanal der Sitzung
//! - [`hostkeys`], [`profiles`], [`secrets`] – was eine Sitzung dafür braucht
//!
//! `docs/architecture.md` beschreibt das Zusammenspiel mit der App.

pub mod core;
mod dial;
mod elevate;
mod excludes;
mod exec;
pub mod ffi;
mod ftp;
#[doc(hidden)]
pub mod fuzzing;
pub mod hostkeys;
mod keyfiles;
mod keys;
mod local;
mod logs;
mod metered;
mod monitor;
mod mosh;
mod ppk;
mod probe;
pub mod profiles;
pub mod protocol;
pub mod secrets;
mod serial;
mod session;
mod sftp;
mod shellfs;
pub mod sshconfig;
mod telnet;
mod transfers;
mod tunnels;
mod unlocked;
mod winserver;
mod x11;

/// Version des Kerns, wie sie in `Cargo.toml` steht.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Version der C-ABI und des Nachrichtenformats zwischen App und Kern.
///
/// Wird bei jeder inkompatiblen Änderung an [`ffi`] oder [`protocol`] erhöht. Die App prüft den Wert beim
/// Start und weigert sich, mit einer DLL zu arbeiten, die nicht zu ihr passt.
pub const ABI_VERSION: u32 = 16;

/// Nur für Tests: Der Kern öffnet kein SFTP mehr und greift, wo der Server Befehle erlaubt, über die auf Dateien
/// zu – so lässt sich dieser Weg an Servern prüfen, die SFTP anbieten. Die App ruft das nie auf.
#[doc(hidden)]
pub fn set_no_sftp_for_tests(off: bool) {
    sftp::NO_SFTP.store(off, std::sync::atomic::Ordering::Relaxed);
}

/// Nur für Tests: setzt herab, wie viele Einträge sich eine Suche ansieht, bevor sie aufhört. Die App ruft das
/// nie auf.
#[doc(hidden)]
pub fn set_find_limit_for_tests(entries: usize) {
    sftp::FIND_SEEN.store(entries, std::sync::atomic::Ordering::Relaxed);
}

/// Nur für Tests: setzt die Obergrenze für die Einträge einer Verzeichnisliste herab, damit sie sich ohne
/// Millionen von Dateien auslösen lässt. Die App ruft das nie auf.
#[doc(hidden)]
pub fn set_list_limit_for_tests(entries: u64) {
    sftp::MAX_ENTRIES.store(entries, std::sync::atomic::Ordering::Relaxed);
}
