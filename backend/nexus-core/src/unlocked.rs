//! Entsperrte Schlüssel, solange der Kern läuft.
//!
//! Ein Schlüssel mit Passphrase wird je Verbindung neu entschlüsselt – wer die Passphrase nicht im Credential
//! Manager speichern will, tippte sie sonst bei jedem Tab. Hier bleibt der entschlüsselte Schlüssel im Speicher,
//! bis der Kern endet oder die App es abstellt: wie bei einem SSH-Agenten, nur ohne dass andere Programme ihn
//! erreichen. Nie auf der Platte.
//!
//! Gemerkt wird je Datei samt Größe und Änderungszeit: Ein ausgetauschter Schlüssel unter demselben Namen ist ein
//! anderer und wird neu gefragt.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use russh::keys::PrivateKey;

/// Woran eine Schlüsseldatei wiedererkannt wird.
#[derive(Clone, Copy, PartialEq, Eq)]
struct Stamp {
    length: u64,
    modified: Option<SystemTime>,
}

impl Stamp {
    fn of(path: &Path) -> Option<Self> {
        let metadata = fs::metadata(path).ok()?;
        Some(Self {
            length: metadata.len(),
            modified: metadata.modified().ok(),
        })
    }
}

struct State {
    enabled: bool,
    keys: HashMap<PathBuf, (Stamp, PrivateKey)>,
}

/// Die entsperrten Schlüssel. Abgeschaltet merkt es sich nichts und gibt nichts heraus.
pub(crate) struct Unlocked(Mutex<State>);

impl Default for Unlocked {
    fn default() -> Self {
        Self(Mutex::new(State {
            enabled: true,
            keys: HashMap::new(),
        }))
    }
}

impl Unlocked {
    fn state(&self) -> std::sync::MutexGuard<'_, State> {
        self.0.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Ein- oder ausschalten. Ausschalten vergisst sofort alles.
    pub(crate) fn set_enabled(&self, enabled: bool) {
        let mut state = self.state();
        state.enabled = enabled;
        if !enabled {
            state.keys.clear();
        }
    }

    /// Vergisst alle Schlüssel; die nächste Verbindung fragt wieder nach der Passphrase.
    pub(crate) fn forget(&self) {
        self.state().keys.clear();
    }

    /// Der entsperrte Schlüssel dieser Datei – nur, wenn die Datei noch dieselbe ist.
    pub(crate) fn get(&self, path: &Path) -> Option<PrivateKey> {
        let stamp = Stamp::of(path)?;
        let mut state = self.state();
        match state.keys.get(path) {
            Some((known, key)) if *known == stamp => Some(key.clone()),
            Some(_) => {
                state.keys.remove(path);
                None
            }
            None => None,
        }
    }

    /// Merkt sich einen eben entsperrten Schlüssel.
    pub(crate) fn keep(&self, path: &Path, key: &PrivateKey) {
        let Some(stamp) = Stamp::of(path) else {
            return;
        };
        let mut state = self.state();
        if state.enabled {
            state.keys.insert(path.to_owned(), (stamp, key.clone()));
        }
    }
}

#[cfg(test)]
mod tests {
    use russh::keys::ssh_key::private::Ed25519Keypair;

    use super::*;

    fn key(seed: u8) -> PrivateKey {
        PrivateKey::from(Ed25519Keypair::from_seed(&[seed; 32]))
    }

    fn file(name: &str, content: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!("nexus-core-unlocked-{}-{name}", std::process::id()));
        fs::create_dir_all(&directory).unwrap();
        let path = directory.join("id_test");
        fs::write(&path, content).unwrap();
        path
    }

    #[test]
    fn a_key_is_kept_for_its_file_until_it_is_forgotten() {
        let unlocked = Unlocked::default();
        let path = file("kept", "one");
        assert!(unlocked.get(&path).is_none());

        unlocked.keep(&path, &key(1));
        assert_eq!(
            unlocked.get(&path).map(|kept| kept.public_key().clone()),
            Some(key(1).public_key().clone())
        );

        unlocked.forget();
        assert!(unlocked.get(&path).is_none());
    }

    #[test]
    fn a_replaced_file_is_another_key() {
        let unlocked = Unlocked::default();
        let path = file("replaced", "one");
        unlocked.keep(&path, &key(1));

        fs::write(&path, "another key file").unwrap();

        assert!(unlocked.get(&path).is_none(), "eine andere Datei unter demselben Namen");
    }

    #[test]
    fn switched_off_nothing_is_kept_and_what_was_kept_is_gone() {
        let unlocked = Unlocked::default();
        let path = file("off", "one");
        unlocked.keep(&path, &key(1));

        unlocked.set_enabled(false);
        assert!(unlocked.get(&path).is_none());
        unlocked.keep(&path, &key(1));
        assert!(unlocked.get(&path).is_none());

        unlocked.set_enabled(true);
        unlocked.keep(&path, &key(1));
        assert!(unlocked.get(&path).is_some());
    }

    #[test]
    fn a_file_that_is_gone_keeps_nothing() {
        let unlocked = Unlocked::default();
        let path = file("gone", "one");
        unlocked.keep(&path, &key(1));
        fs::remove_file(&path).unwrap();
        assert!(unlocked.get(&path).is_none());
    }
}
