//! Ablage für Passwörter und Passphrasen (Spec §12): nie im Klartext auf der Platte.
//!
//! Unter Windows ist das der Credential Manager. Die App sieht gespeicherte Geheimnisse nie – sie gibt nur ein
//! frisch eingegebenes herein, der Kern liest es bei der nächsten Verbindung selbst.

use std::collections::HashMap;
use std::sync::Mutex;

/// Schlüssel-Wert-Ablage für Geheimnisse.
pub trait SecretStore: Send + Sync {
    /// Das Geheimnis zum Schlüssel, falls eines gespeichert ist.
    fn get(&self, key: &str) -> Option<String>;

    /// Speichert oder ersetzt. `label` ist der Text, den die Windows-Anmeldeinformationsverwaltung dazu zeigt.
    fn set(&self, key: &str, label: &str, secret: &str) -> Result<(), String>;

    /// Entfernt den Eintrag; kein Fehler, wenn es keinen gab.
    fn delete(&self, key: &str);
}

/// Schlüssel für das Passwort eines Profils.
pub fn password_key(profile_id: &str) -> String {
    format!("password:{profile_id}")
}

/// Schlüssel für die Passphrase des privaten Schlüssels eines Profils.
pub fn passphrase_key(profile_id: &str) -> String {
    format!("passphrase:{profile_id}")
}

/// Schlüssel für den Vermerk, für welches Ziel das Passwort eines Profils gespeichert wurde.
pub fn target_key(profile_id: &str) -> String {
    format!("target:{profile_id}")
}

/// Wohin ein Passwort geschickt wird: Protokoll, Benutzer, Host und Port. Ändert sich eines davon, ist es ein
/// anderes Ziel.
pub fn target_of(profile: &crate::profiles::Profile) -> String {
    format!(
        "{}|{}@{}:{}",
        profile.protocol.as_deref().unwrap_or("ssh"),
        profile.user,
        profile.host.to_lowercase(),
        profile.port
    )
}

/// Das gespeicherte Passwort eines Profils – aber nur für das Ziel, für das es gespeichert wurde.
///
/// Ein Passwort gehört zu einem Server, nicht zu einer Kennung: Ein Profil kann auf einen anderen Host zeigen,
/// ohne dass der Benutzer es so gewollt hat (eine importierte Datei mit derselben Kennung, ein geänderter Eintrag
/// in `~/.ssh/config`, eine von Hand bearbeitete `profiles.json`). Dann wird das Passwort vergessen statt
/// verschickt, und der Benutzer wird gefragt. Ein Passwort ohne Vermerk (von einer älteren Version gespeichert)
/// gilt für das Ziel, das das Profil jetzt hat, und bekommt den Vermerk.
pub fn saved_password(store: &dyn SecretStore, profile: &crate::profiles::Profile) -> Option<String> {
    let password = store.get(&password_key(&profile.id))?;
    // Ein lokales Terminal meldet niemanden an; was von früher (als Server) daliegt, wird vergessen.
    if profile.is_local() {
        forget_password(store, &profile.id);
        return None;
    }

    let target = target_of(profile);
    match store.get(&target_key(&profile.id)) {
        Some(bound) if bound == target => Some(password),
        Some(_) => {
            forget_password(store, &profile.id);
            None
        }
        None => {
            let _ = store.set(&target_key(&profile.id), "NexusSSH", &target);
            Some(password)
        }
    }
}

/// Speichert das Passwort eines Profils samt dem Ziel, für das es gilt.
pub fn save_password(
    store: &dyn SecretStore,
    profile: &crate::profiles::Profile,
    label: &str,
    password: &str,
) -> Result<(), String> {
    if profile.is_local() {
        return Err("a local terminal has no password".to_owned());
    }

    store.set(&password_key(&profile.id), label, password)?;
    store.set(&target_key(&profile.id), "NexusSSH", &target_of(profile))
}

/// Vergisst das Passwort eines Profils und seinen Vermerk.
pub fn forget_password(store: &dyn SecretStore, profile_id: &str) {
    store.delete(&password_key(profile_id));
    store.delete(&target_key(profile_id));
}

/// Gibt jedem gespeicherten Passwort ohne Vermerk das Ziel, das sein Profil jetzt hat. Läuft beim Start, bevor
/// irgendetwas ein Profil ändern kann – danach folgt kein Passwort mehr einem Profil zu einem anderen Server.
pub fn bind_unbound(store: &dyn SecretStore, profiles: &[crate::profiles::Profile]) {
    for profile in profiles {
        if store.get(&password_key(&profile.id)).is_some() && store.get(&target_key(&profile.id)).is_none() {
            let _ = store.set(&target_key(&profile.id), "NexusSSH", &target_of(profile));
        }
    }
}

/// Ablage nur im Arbeitsspeicher – für Tests und Plattformen ohne Schlüsselbund.
#[derive(Default)]
pub struct MemorySecretStore {
    entries: Mutex<HashMap<String, String>>,
}

impl SecretStore for MemorySecretStore {
    fn get(&self, key: &str) -> Option<String> {
        self.entries.lock().ok()?.get(key).cloned()
    }

    fn set(&self, key: &str, _label: &str, secret: &str) -> Result<(), String> {
        self.entries
            .lock()
            .map_err(|_| "secret store is unavailable".to_owned())?
            .insert(key.to_owned(), secret.to_owned());
        Ok(())
    }

    fn delete(&self, key: &str) {
        if let Ok(mut entries) = self.entries.lock() {
            entries.remove(key);
        }
    }
}

#[cfg(windows)]
pub use windows::CredentialManager;

#[cfg(windows)]
mod windows {
    use std::ptr;

    use windows_sys::Win32::Foundation::GetLastError;
    use windows_sys::Win32::Security::Credentials::{
        CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredDeleteW, CredFree, CredReadW, CredWriteW,
    };

    use super::SecretStore;

    /// Windows Credential Manager, Einträge vom Typ „Generisch“ unter `NexusSSH:<schlüssel>`.
    ///
    /// Die Einträge gehören dem angemeldeten Windows-Benutzer und sind mit dessen Anmeldung geschützt (DPAPI).
    #[derive(Default)]
    pub struct CredentialManager;

    fn target(key: &str) -> Vec<u16> {
        format!("NexusSSH:{key}").encode_utf16().chain(Some(0)).collect()
    }

    impl SecretStore for CredentialManager {
        fn get(&self, key: &str) -> Option<String> {
            let target = target(key);
            let mut credential: *mut CREDENTIALW = ptr::null_mut();

            // SAFETY: `target` ist NUL-terminiert und lebt über den Aufruf hinaus. Bei Erfolg zeigt
            // `credential` auf einen von Windows angelegten Block, den wir lesen und mit CredFree freigeben.
            unsafe {
                if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) == 0 || credential.is_null() {
                    return None;
                }

                let blob = (*credential).CredentialBlob;
                let size = (*credential).CredentialBlobSize as usize;
                let secret = if blob.is_null() {
                    None
                } else {
                    String::from_utf8(std::slice::from_raw_parts(blob, size).to_vec()).ok()
                };
                CredFree(credential.cast());
                secret
            }
        }

        fn set(&self, key: &str, label: &str, secret: &str) -> Result<(), String> {
            let mut target = target(key);
            let mut user: Vec<u16> = label.encode_utf16().chain(Some(0)).collect();
            let mut blob = secret.as_bytes().to_vec();
            let blob_size = u32::try_from(blob.len()).map_err(|_| "secret is too long".to_owned())?;

            // SAFETY: Alle Zeiger in `credential` zeigen auf Puffer, die bis nach dem Aufruf leben; die
            // übrigen Felder sind laut Dokumentation mit 0 gültig.
            let written = unsafe {
                let mut credential: CREDENTIALW = std::mem::zeroed();
                credential.Type = CRED_TYPE_GENERIC;
                credential.TargetName = target.as_mut_ptr();
                credential.UserName = user.as_mut_ptr();
                credential.CredentialBlob = blob.as_mut_ptr();
                credential.CredentialBlobSize = blob_size;
                credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
                CredWriteW(&credential, 0)
            };

            // Die Kopie des Geheimnisses nicht länger als nötig im Speicher lassen.
            blob.fill(0);

            if written == 0 {
                // SAFETY: liest nur den Fehlercode des aufrufenden Threads.
                return Err(format!("CredWriteW failed with error {}", unsafe { GetLastError() }));
            }

            Ok(())
        }

        fn delete(&self, key: &str) {
            let target = target(key);
            // SAFETY: `target` ist NUL-terminiert und lebt über den Aufruf hinaus.
            unsafe {
                CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0);
            }
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn a_secret_round_trips_through_the_credential_manager() {
            let store = CredentialManager;
            let key = format!("test:{}", std::process::id());
            store.delete(&key);
            assert_eq!(store.get(&key), None);

            store.set(&key, "nexus-core test", "pässwörd ✓").unwrap();
            assert_eq!(store.get(&key).as_deref(), Some("pässwörd ✓"));

            store.set(&key, "nexus-core test", "second").unwrap();
            assert_eq!(store.get(&key).as_deref(), Some("second"));

            store.delete(&key);
            assert_eq!(store.get(&key), None);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_memory_store_keeps_and_forgets() {
        let store = MemorySecretStore::default();
        assert_eq!(store.get("a"), None);

        store.set("a", "label", "one").unwrap();
        assert_eq!(store.get("a").as_deref(), Some("one"));

        store.delete("a");
        assert_eq!(store.get("a"), None);
    }

    #[test]
    fn a_saved_password_does_not_follow_its_profile_to_another_target() {
        let store = MemorySecretStore::default();
        let profile = crate::profiles::Profile {
            id: "p1".to_owned(),
            host: "Web.Example".to_owned(),
            user: "deploy".to_owned(),
            port: 22,
            ..crate::profiles::Profile::default()
        };
        save_password(&store, &profile, "deploy@web.example", "geheim").unwrap();
        assert_eq!(saved_password(&store, &profile).as_deref(), Some("geheim"));

        // Groß- und Kleinschreibung des Hosts ist kein anderes Ziel.
        let same = crate::profiles::Profile {
            host: "web.example".to_owned(),
            ..profile.clone()
        };
        assert_eq!(saved_password(&store, &same).as_deref(), Some("geheim"));

        // Ein anderer Host, Port, Benutzer oder ein anderes Protokoll: Das Passwort wird vergessen, nicht verschickt
        // – und kommt auch nicht wieder, wenn das Profil zurückgestellt wird.
        for moved in [
            crate::profiles::Profile {
                host: "attacker.example".to_owned(),
                ..profile.clone()
            },
            crate::profiles::Profile {
                port: 2222,
                ..profile.clone()
            },
            crate::profiles::Profile {
                user: "root".to_owned(),
                ..profile.clone()
            },
            crate::profiles::Profile {
                protocol: Some("ftp".to_owned()),
                ..profile.clone()
            },
        ] {
            save_password(&store, &profile, "deploy@web.example", "geheim").unwrap();
            assert_eq!(saved_password(&store, &moved), None, "{}", target_of(&moved));
            assert_eq!(saved_password(&store, &profile), None);
            assert_eq!(store.get(&password_key("p1")), None);
        }
    }

    #[test]
    fn a_local_terminal_has_no_password() {
        let store = MemorySecretStore::default();
        let local = crate::profiles::Profile {
            id: "p1".to_owned(),
            protocol: Some("local".to_owned()),
            host: "cmd".to_owned(),
            ..crate::profiles::Profile::default()
        };

        assert!(save_password(&store, &local, "cmd", "geheim").is_err());
        assert_eq!(store.get(&password_key("p1")), None);

        // Auch eines aus der Zeit, als das Profil noch ein Server war, kommt nicht zum Vorschein – auch ohne Vermerk.
        store.set(&password_key("p1"), "x", "alt").unwrap();
        assert_eq!(saved_password(&store, &local), None);
        assert_eq!(store.get(&password_key("p1")), None);
    }

    #[test]
    fn a_password_from_an_older_version_is_bound_to_the_target_it_has_now() {
        let store = MemorySecretStore::default();
        let profile = crate::profiles::Profile {
            id: "p1".to_owned(),
            host: "web.example".to_owned(),
            user: "deploy".to_owned(),
            port: 22,
            ..crate::profiles::Profile::default()
        };
        store.set(&password_key("p1"), "label", "geheim").unwrap();
        bind_unbound(&store, std::slice::from_ref(&profile));
        assert_eq!(
            store.get(&target_key("p1")).as_deref(),
            Some("ssh|deploy@web.example:22")
        );

        let moved = crate::profiles::Profile {
            host: "attacker.example".to_owned(),
            ..profile.clone()
        };
        assert_eq!(saved_password(&store, &moved), None);

        // Ohne den Durchgang beim Start: Der erste Gebrauch bindet.
        store.set(&password_key("p1"), "label", "geheim").unwrap();
        assert_eq!(saved_password(&store, &profile).as_deref(), Some("geheim"));
        assert_eq!(saved_password(&store, &moved), None);

        forget_password(&store, "p1");
        assert_eq!(store.get(&target_key("p1")), None);
    }

    #[test]
    fn password_and_passphrase_use_different_keys() {
        assert_ne!(password_key("p1"), passphrase_key("p1"));
    }
}
