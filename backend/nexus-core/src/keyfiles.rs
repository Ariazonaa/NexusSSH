//! Die eigenen Schlüsseldateien: ansehen, die Passphrase ändern, zwischen den Formaten von OpenSSH und PuTTY
//! umwandeln – was sonst `ssh-keygen -l`, `ssh-keygen -p` und `puttygen` tun.
//!
//! Angesehen wird ohne Passphrase: Art, Fingerabdruck und Kommentar stehen im Kopf der Datei oder in `….pub`
//! daneben. Eine Passphrase kommt nur für die beiden Änderungen herein, bleibt im Speicher und steht in keinem
//! Protokoll. **Ein Schlüssel darf dabei nie verloren gehen:** Umwandeln schreibt eine neue Datei und fasst die
//! alte nicht an; die Passphrase zu ändern schreibt erst eine zweite Datei daneben, liest sie mit der neuen
//! Passphrase zurück, vergleicht den Schlüssel und tauscht erst dann die Dateien.

use std::path::{Path, PathBuf};

use russh::keys::ssh_key::public::KeyData;
use russh::keys::ssh_key::{EcdsaCurve, LineEnding};
use russh::keys::{HashAlg, PrivateKey, PublicKey};
use zeroize::Zeroizing;

use crate::sftp::Failure;
use crate::{keys, ppk};

/// Eine Schlüsseldatei ist klein; mehr wird nicht gelesen, gleich was dort liegt.
pub(crate) const LIMIT: u64 = 64 * 1024;

/// So viele Einträge von `~/.ssh` sieht die Übersicht höchstens an.
const MAX_ENTRIES: usize = 500;

/// So viele Dateien nennt die App höchstens dazu (Schlüssel aus Profilen, die woanders liegen).
const MAX_EXTRA: usize = 200;

/// Das Format einer Schlüsseldatei.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Format {
    /// Das Format von OpenSSH (`-----BEGIN OPENSSH PRIVATE KEY-----`).
    Openssh,
    /// Das Format von PuTTY (`.ppk`).
    Ppk,
    /// Die älteren PEM-Formate (PKCS#1, PKCS#8, SEC1) – gelesen, nie geschrieben.
    Pem,
}

/// Eine Schlüsseldatei, wie die Übersicht sie zeigt. Alles darin ist ohne Passphrase zu lesen.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct KeyFile {
    /// Der ganze Pfad.
    pub path: String,
    /// Der Dateiname.
    pub name: String,
    /// Das Format der Datei.
    pub format: Format,
    /// Art des Schlüssels, z. B. `ssh-ed25519`; leer, wenn sie ohne Passphrase nicht zu erfahren ist.
    pub algorithm: String,
    /// Länge in Bit, wo die Art sie nicht schon nennt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bits: Option<u32>,
    /// Fingerabdruck (SHA-256) in der Schreibweise von OpenSSH; leer wie `algorithm`.
    pub fingerprint: String,
    /// Der Kommentar; leer, wenn er im verschlüsselten Teil steht und keine Datei `….pub` ihn nennt.
    pub comment: String,
    /// Ob der private Teil mit einer Passphrase verschlüsselt ist.
    pub encrypted: bool,
    /// Die Zeile des öffentlichen Schlüssels, wie sie in `authorized_keys` gehört; leer wie `algorithm`.
    pub public_key: String,
    /// Art, Fingerabdruck und `public_key` stammen allein aus `….pub` daneben: Die Datei selbst verrät sie ohne
    /// Passphrase nicht, und ob beide zusammengehören, ist damit nicht geprüft.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub unchecked: bool,
}

fn failed(code: &'static str, message: impl Into<String>) -> Failure {
    Failure {
        code,
        message: message.into(),
    }
}

// Liest eine Datei als Text – nur eine gewöhnliche Datei, und nur, wenn sie so klein ist wie ein Schlüssel.
fn read(path: &Path) -> Option<Zeroizing<String>> {
    use std::io::Read;

    let file = std::fs::File::open(path).ok()?;
    if !file.metadata().ok()?.is_file() {
        return None;
    }

    let mut text = Zeroizing::new(String::new());
    file.take(LIMIT + 1).read_to_string(&mut text).ok()?;
    (text.len() as u64 <= LIMIT).then_some(text)
}

// Woran eine Schlüsseldatei zu erkennen ist. `None`: keine.
fn format_of(text: &str) -> Option<Format> {
    if ppk::is_ppk(text) {
        Some(Format::Ppk)
    } else if text.contains("-----BEGIN OPENSSH PRIVATE KEY-----") {
        Some(Format::Openssh)
    } else if text.contains("-----BEGIN ") && text.contains("PRIVATE KEY-----") {
        Some(Format::Pem)
    } else {
        None
    }
}

/// Liest einen privaten Schlüssel aus dem Text seiner Datei, gleich in welchem Format. Wie
/// `russh::keys::decode_secret_key`, nur dass eine Datei von PuTTY, an der die Bibliothek scheitert, noch dem
/// eigenen Leser vorgelegt wird ([`ppk::read_ed25519`]).
pub(crate) fn decode(text: &str, passphrase: Option<&str>) -> Result<PrivateKey, russh::keys::Error> {
    russh::keys::decode_secret_key(text, passphrase).or_else(|error| {
        ppk::is_ppk(text)
            .then(|| ppk::read_ed25519(text, passphrase))
            .flatten()
            .ok_or(error)
    })
}

/// Liest einen privaten Schlüssel aus einer Datei – der Weg der Anmeldung.
pub(crate) fn load(path: &Path, passphrase: Option<&str>) -> Result<PrivateKey, russh::keys::Error> {
    let text = Zeroizing::new(std::fs::read_to_string(path)?);
    decode(&text, passphrase)
}

// Der öffentliche Schlüssel aus `….pub` neben der Datei.
fn beside(path: &Path) -> Option<PublicKey> {
    let mut beside = path.as_os_str().to_owned();
    beside.push(".pub");
    PublicKey::from_openssh(read(Path::new(&beside))?.trim()).ok()
}

fn shown(text: &str) -> String {
    text.chars().filter(|c| !c.is_control()).take(200).collect()
}

/// Was sich über die Datei sagen lässt, ohne sie zu entsperren. `None`, wenn sie kein privater Schlüssel ist.
pub(crate) fn inspect(path: &Path) -> Option<KeyFile> {
    let text = read(path)?;
    let format = format_of(&text)?;
    let (mut public, mut comment, encrypted) = match format {
        Format::Ppk => {
            let header = ppk::header(&text)?;
            (Some(header.public), header.comment, header.encrypted)
        }
        Format::Openssh => {
            let key = PrivateKey::from_openssh(text.as_bytes()).ok()?;
            (
                Some(key.public_key().clone()),
                key.comment().to_string(),
                key.is_encrypted(),
            )
        }
        Format::Pem => {
            let encrypted = text.contains("ENCRYPTED");
            let key = (!encrypted)
                .then(|| russh::keys::decode_secret_key(&text, None).ok())
                .flatten();
            (key.map(|key| key.public_key().clone()), String::new(), encrypted)
        }
    };

    // Die Datei daneben nennt, was der Kopf verschweigt: den Kommentar eines verschlüsselten Schlüssels und bei
    // den alten Formaten den öffentlichen Teil. Für den Kommentar gilt sie nur, wenn sie denselben Schlüssel nennt.
    let mut unchecked = false;
    if (public.is_none() || comment.is_empty())
        && let Some(other) = beside(path)
    {
        match &public {
            None => {
                unchecked = true;
                comment = other.comment().to_string();
                public = Some(other);
            }
            Some(own) if own.key_data() == other.key_data() => comment = other.comment().to_string(),
            Some(_) => {}
        }
    }

    let bits = public.as_ref().and_then(|public| match public.key_data() {
        KeyData::Rsa(rsa) => Some(rsa.key_size()),
        KeyData::Ed25519(_) => Some(256),
        KeyData::Ecdsa(ecdsa) => Some(match ecdsa.curve() {
            EcdsaCurve::NistP256 => 256,
            EcdsaCurve::NistP384 => 384,
            EcdsaCurve::NistP521 => 521,
        }),
        _ => None,
    });
    Some(KeyFile {
        path: path.display().to_string(),
        name: path
            .file_name()
            .map(|name| shown(&name.to_string_lossy()))
            .unwrap_or_default(),
        format,
        algorithm: public
            .as_ref()
            .map(|public| public.algorithm().as_str().to_owned())
            .unwrap_or_default(),
        bits,
        fingerprint: public
            .as_ref()
            .map(|public| public.fingerprint(HashAlg::Sha256).to_string())
            .unwrap_or_default(),
        comment: shown(&comment),
        encrypted,
        public_key: public
            .as_ref()
            .and_then(|public| {
                let mut line = public.clone();
                line.set_comment(shown(&comment));
                line.to_openssh().ok()
            })
            .unwrap_or_default(),
        unchecked,
    })
}

/// Die Schlüsseldateien in `~/.ssh` (nicht in Unterordnern), nach Namen, danach die Dateien, die die App dazu
/// nennt – Schlüssel aus Profilen, die woanders liegen. Was kein privater Schlüssel ist, fehlt.
pub(crate) fn list(ssh_dir: Option<&Path>, extra: &[String]) -> Vec<KeyFile> {
    let mut paths: Vec<PathBuf> = ssh_dir
        .and_then(|directory| std::fs::read_dir(directory).ok())
        .into_iter()
        .flatten()
        .take(MAX_ENTRIES)
        .filter_map(|entry| Some(entry.ok()?.path()))
        .filter(|path| {
            let name = path
                .file_name()
                .map(|name| name.to_string_lossy().to_ascii_lowercase())
                .unwrap_or_default();
            !name.ends_with(".pub") && !matches!(name.as_str(), "config" | "known_hosts" | "authorized_keys")
        })
        .collect();
    paths.sort();
    for path in extra.iter().take(MAX_EXTRA).map(PathBuf::from) {
        if path.is_absolute() && !paths.iter().any(|known| same_file(known, &path)) {
            paths.push(path);
        }
    }

    paths.iter().filter_map(|path| inspect(path)).collect()
}

fn same_file(left: &Path, right: &Path) -> bool {
    let text = |path: &Path| path.to_string_lossy().replace('\\', "/");
    if cfg!(windows) {
        text(left).eq_ignore_ascii_case(&text(right))
    } else {
        left == right
    }
}

// Den Schlüssel einer Datei entsperren. `text` ist ihr Inhalt.
fn unlock(text: &str, passphrase: Option<&str>) -> Result<PrivateKey, Failure> {
    let passphrase = passphrase.filter(|passphrase| !passphrase.is_empty());
    match decode(text, passphrase) {
        Ok(key) if key.is_encrypted() => Err(failed("wrong_passphrase", "the key needs its passphrase")),
        Ok(key) => Ok(key),
        // Woran es lag, sagen die Formate nicht alle: Mit einer Passphrase gilt sie als falsch, ohne als nötig –
        // außer die Datei ist gar nicht verschlüsselt.
        Err(error) => {
            let encrypted = match format_of(text) {
                Some(Format::Ppk) => ppk::header(text).is_some_and(|header| header.encrypted),
                Some(Format::Openssh) => PrivateKey::from_openssh(text.as_bytes()).is_ok_and(|key| key.is_encrypted()),
                Some(Format::Pem) => text.contains("ENCRYPTED"),
                None => false,
            };
            Err(if encrypted {
                failed("wrong_passphrase", "the passphrase does not open this key")
            } else {
                failed("unreadable", error.to_string())
            })
        }
    }
}

// Der Schlüssel als Text einer Datei im verlangten Format, mit `passphrase` verschlüsselt.
fn render(key: &PrivateKey, format: Format, passphrase: Option<&str>) -> Result<Zeroizing<String>, Failure> {
    let passphrase = passphrase.filter(|passphrase| !passphrase.is_empty());
    let crypto = |error: russh::keys::ssh_key::Error| failed("failed", error.to_string());
    match format {
        Format::Ppk => ppk::write(key, passphrase)
            .map(Zeroizing::new)
            .ok_or_else(|| failed("unsupported", "PuTTY's format does not hold this kind of key")),
        Format::Openssh | Format::Pem => match passphrase {
            Some(passphrase) => key
                .encrypt(&mut rand::rng(), passphrase)
                .map_err(crypto)?
                .to_openssh(LineEnding::LF)
                .map_err(crypto),
            None => key.to_openssh(LineEnding::LF).map_err(crypto),
        },
    }
}

// Ob `text` mit `passphrase` genau diesen Schlüssel ergibt – geprüft, bevor eine Datei ihn ersetzt oder als
// seine Kopie gilt.
fn holds(text: &str, passphrase: Option<&str>, key: &PrivateKey) -> bool {
    unlock(text, passphrase).is_ok_and(|read| read.key_data() == key.key_data())
}

/// Ändert die Passphrase einer Schlüsseldatei: `old` öffnet sie, `new` schützt sie danach – ohne `new` (oder
/// leer) liegt der Schlüssel danach unverschlüsselt da. Das Format bleibt; nur eine Datei in einem der alten
/// PEM-Formate wird dabei ins Format von OpenSSH umgeschrieben, wie es `ssh-keygen -p` auch tut.
pub(crate) fn change_passphrase(path: &Path, old: Option<&str>, new: Option<&str>) -> Result<(), Failure> {
    let text = read(path).ok_or_else(|| failed("unreadable", format!("{} is not a key file", path.display())))?;
    let format = format_of(&text).ok_or_else(|| failed("unreadable", "this is not a private key"))?;
    let key = unlock(&text, old)?;
    let written = render(&key, format, new)?;
    if !holds(&written, new, &key) {
        return Err(failed("failed", "the rewritten key could not be read back"));
    }

    replace(path, text.as_bytes(), written.as_bytes())
}

/// Schreibt den Schlüssel der Datei in das andere Format, als neue Datei `name` im selben Ordner, mit derselben
/// Passphrase geschützt; die alte Datei bleibt, wie sie ist. Ins Format von OpenSSH entsteht dazu `name.pub`.
/// Nichts Vorhandenes wird überschrieben.
pub(crate) fn convert(path: &Path, passphrase: Option<&str>, to: Format, name: &str) -> Result<PathBuf, Failure> {
    if to == Format::Pem {
        return Err(failed("unsupported", "keys are not written in the old PEM formats"));
    }

    if !keys::valid_name(name) {
        return Err(failed("invalid_name", format!("{name:?} is not a name for a key file")));
    }

    let text = read(path).ok_or_else(|| failed("unreadable", format!("{} is not a key file", path.display())))?;
    if format_of(&text).ok_or_else(|| failed("unreadable", "this is not a private key"))? == to {
        return Err(failed("same_format", "the key already has this format"));
    }

    let directory = path
        .parent()
        .ok_or_else(|| failed("failed", "the key file has no folder"))?;
    let target = directory.join(name);
    let public_target = directory.join(format!("{name}.pub"));
    if target.exists() || (to == Format::Openssh && public_target.exists()) {
        return Err(failed("exists", format!("{} already exists", target.display())));
    }

    let key = unlock(&text, passphrase)?;
    let written = render(&key, to, passphrase)?;
    if !holds(&written, passphrase, &key) {
        return Err(failed("failed", "the converted key could not be read back"));
    }

    let io = |error: std::io::Error| failed("failed", error.to_string());
    keys::write_new(&target, written.as_bytes(), true).map_err(io)?;
    if to == Format::Openssh {
        let line = key
            .public_key()
            .to_openssh()
            .map_err(|error| failed("failed", error.to_string()));
        let done =
            line.and_then(|line| keys::write_new(&public_target, format!("{line}\n").as_bytes(), false).map_err(io));
        if let Err(failure) = done {
            // Ein privater Schlüssel ohne seinen öffentlichen Teil wäre ein halbes Paar.
            let _ = std::fs::remove_file(&target);
            return Err(failure);
        }
    }

    Ok(target)
}

// Ersetzt den Inhalt von `path`: erst ganz in eine Datei daneben, dann getauscht – nie eine halbe Datei, und
// nie ein Augenblick ohne Schlüssel. Die Rechte der alten Datei gelten weiter. `read` ist, was vorhin aus der
// Datei gelesen wurde: Steht dort inzwischen etwas anderes (ein anderes Programm hat den Schlüssel getauscht,
// während hier gerechnet wurde), wird nichts ersetzt – sonst überschriebe der alte Schlüssel den neuen
// (Hinweis von Codex). Ganz schließt das die Lücke nicht, es macht sie so klein wie einen Aufruf.
fn replace(path: &Path, read: &[u8], content: &[u8]) -> Result<(), Failure> {
    let io = |error: std::io::Error| failed("failed", error.to_string());
    let mut next = path.as_os_str().to_owned();
    next.push(".nexus-new");
    let next = PathBuf::from(next);
    // Ein Rest eines früheren Versuchs, der nicht zu Ende kam.
    let _ = std::fs::remove_file(&next);
    keys::write_new(&next, content, true).map_err(io)?;
    let swapped = if self::read(path).is_some_and(|now| now.as_bytes() == read) {
        swap(path, &next).map_err(io)
    } else {
        Err(failed("changed", "the key file changed in the meantime"))
    };
    if swapped.is_err() {
        let _ = std::fs::remove_file(&next);
    }

    swapped
}

/// Für Zufallstests: was die Übersicht mit dem Text einer Datei tut. Entsperrt wird nur ohne Passphrase – mit
/// einer rechnete jede Eingabe an der Ableitung des Schlüssels.
pub(crate) fn exercise(text: &str) {
    let _ = format_of(text);
    let _ = ppk::header(text);
    let _ = ppk::read_ed25519(text, None);
    let _ = unlock(text, None);
}

/// Für Zufallstests: ein frisch erzeugter Schlüssel in beiden Formaten, dazu einer mit Passphrase.
pub(crate) fn samples() -> Vec<Vec<u8>> {
    use russh::keys::ssh_key::Algorithm;

    let Ok(mut key) = PrivateKey::random(&mut rand::rng(), Algorithm::Ed25519) else {
        return Vec::new();
    };
    key.set_comment("fuzz@test");
    [
        render(&key, Format::Openssh, None),
        render(&key, Format::Ppk, None),
        render(&key, Format::Ppk, Some("x")),
    ]
    .into_iter()
    .flatten()
    .map(|text| text.as_bytes().to_vec())
    .collect()
}

#[cfg(windows)]
fn swap(path: &Path, next: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;

    use windows_sys::Win32::Storage::FileSystem::ReplaceFileW;

    // `ReplaceFileW` statt Umbenennen: Die neue Datei übernimmt Rechte und Merkmale der alten. Ein Schlüssel,
    // dessen Rechte der Benutzer eng gesetzt hat, bekäme sonst die geerbten des Ordners.
    let wide = |path: &Path| -> Vec<u16> { path.as_os_str().encode_wide().chain(Some(0)).collect() };
    let (old, new) = (wide(path), wide(next));
    // SAFETY: Beide Zeiger zeigen auf nullterminierte Zeichenketten, die den Aufruf überleben; die übrigen
    // Argumente sind ausdrücklich „keines“.
    let done = unsafe {
        ReplaceFileW(
            old.as_ptr(),
            new.as_ptr(),
            std::ptr::null(),
            0,
            std::ptr::null(),
            std::ptr::null(),
        )
    };
    if done == 0 {
        return Err(std::io::Error::last_os_error());
    }

    Ok(())
}

#[cfg(not(windows))]
fn swap(path: &Path, next: &Path) -> std::io::Result<()> {
    std::fs::set_permissions(next, std::fs::metadata(path)?.permissions())?;
    std::fs::rename(next, path)
}

#[cfg(test)]
mod tests {
    use russh::keys::ssh_key::Algorithm;

    use super::*;

    fn directory(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!(
            "nexus-core-keyfiles-{}-{name}-{}",
            std::process::id(),
            crate::profiles::unix_now()
        ));
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(&directory).unwrap();
        directory
    }

    fn key_of(path: &Path, passphrase: Option<&str>) -> PrivateKey {
        load(path, passphrase).unwrap()
    }

    #[test]
    fn the_list_names_what_a_key_file_says_without_unlocking_it() {
        let ssh = directory("list");
        let plain = keys::generate(&ssh, "id_plain", None, "me@pc").unwrap();
        keys::generate(&ssh, "id_secret", Some("correct horse"), "work key").unwrap();
        std::fs::write(ssh.join("config"), "Host x\n").unwrap();
        std::fs::write(ssh.join("known_hosts"), "x ssh-ed25519 AAAA\n").unwrap();
        std::fs::write(ssh.join("notes.txt"), "not a key").unwrap();
        std::fs::create_dir(ssh.join("sub")).unwrap();
        let elsewhere = directory("list-elsewhere");
        let other = keys::generate(&elsewhere, "id_other", None, "").unwrap();
        convert(&plain.path, None, Format::Ppk, "id_plain.ppk").unwrap();

        let found = list(
            Some(&ssh),
            &[
                other.path.display().to_string(),
                plain.path.display().to_string(),
                "relative/key".to_owned(),
                elsewhere.join("missing").display().to_string(),
            ],
        );

        assert_eq!(
            found.iter().map(|key| key.name.as_str()).collect::<Vec<_>>(),
            ["id_plain", "id_plain.ppk", "id_secret", "id_other"]
        );
        let first = &found[0];
        assert_eq!(first.format, Format::Openssh);
        assert_eq!(first.algorithm, "ssh-ed25519");
        assert_eq!(first.bits, Some(256));
        assert!(first.fingerprint.starts_with("SHA256:"));
        assert_eq!(first.comment, "me@pc");
        assert!(!first.encrypted);
        assert_eq!(first.public_key, plain.public_key);

        // Dieselben Angaben aus der Datei von PuTTY.
        assert_eq!(found[1].format, Format::Ppk);
        assert_eq!(found[1].fingerprint, first.fingerprint);
        assert_eq!(found[1].comment, "me@pc");

        // Der Kommentar eines verschlüsselten Schlüssels steht in der Datei daneben.
        assert!(found[2].encrypted);
        assert_eq!(found[2].comment, "work key");
        assert!(found[2].fingerprint.starts_with("SHA256:"));

        let json = serde_json::to_string(first).unwrap();
        assert!(json.contains(r#""format":"openssh""#), "{json}");
    }

    /// Ein verschlüsselter Schlüssel in einem der alten Formate verrät ohne Passphrase nichts. Was dann dasteht,
    /// kommt aus der Datei daneben – und ist als ungeprüft gekennzeichnet, denn die kann zu einem anderen gehören.
    #[test]
    fn what_only_the_file_beside_a_key_says_is_marked_as_unchecked() {
        let ssh = directory("unchecked");
        let other = keys::generate(&ssh, "id_other", None, "someone else").unwrap();
        let old = ssh.join("old.pem");
        std::fs::write(
            &old,
            "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,00\n\nAAAA\n-----END RSA PRIVATE KEY-----\n",
        )
        .unwrap();

        let alone = inspect(&old).unwrap();
        assert_eq!(
            (alone.format, alone.encrypted, alone.unchecked),
            (Format::Pem, true, false)
        );
        assert_eq!((alone.algorithm.as_str(), alone.fingerprint.as_str()), ("", ""));

        std::fs::copy(ssh.join("id_other.pub"), ssh.join("old.pem.pub")).unwrap();
        let beside = inspect(&old).unwrap();
        assert!(beside.unchecked);
        assert_eq!(beside.public_key, other.public_key);
        assert!(serde_json::to_string(&beside).unwrap().contains(r#""unchecked":true"#));

        // Wo die Datei selbst ihren öffentlichen Teil nennt, ist nichts ungeprüft.
        assert!(!inspect(&other.path).unwrap().unchecked);
        assert!(
            !serde_json::to_string(&inspect(&other.path).unwrap())
                .unwrap()
                .contains(r#""unchecked":"#)
        );
    }

    /// Tauscht ein anderes Programm die Datei, während hier an der neuen Passphrase gerechnet wird, bleibt seine
    /// Datei stehen.
    #[test]
    fn a_file_that_changed_in_the_meantime_is_not_replaced() {
        let ssh = directory("changed");
        let key = keys::generate(&ssh, "id_test", None, "").unwrap();
        let read = std::fs::read(&key.path).unwrap();
        std::fs::write(&key.path, "something newer").unwrap();

        assert_eq!(replace(&key.path, &read, b"rewritten").unwrap_err().code, "changed");

        assert_eq!(std::fs::read_to_string(&key.path).unwrap(), "something newer");
        assert_eq!(
            std::fs::read_dir(&ssh).unwrap().count(),
            2,
            "nothing is left beside the pair"
        );
    }

    #[test]
    fn a_stale_file_beside_a_key_does_not_lend_its_comment() {
        let ssh = directory("stale");
        let key = keys::generate(&ssh, "id_a", Some("x"), "mine").unwrap();
        let other = keys::generate(&ssh, "id_b", None, "someone else").unwrap();
        std::fs::copy(ssh.join("id_b.pub"), ssh.join("id_a.pub")).unwrap();

        let seen = inspect(&key.path).unwrap();

        assert_eq!(seen.comment, "");
        assert_ne!(seen.public_key, other.public_key);
    }

    #[test]
    fn the_passphrase_is_set_changed_and_removed_and_the_key_stays_the_same() {
        let ssh = directory("passphrase");
        let generated = keys::generate(&ssh, "id_test", None, "me@pc").unwrap();
        let original = key_of(&generated.path, None);

        change_passphrase(&generated.path, None, Some("eins")).unwrap();
        assert!(inspect(&generated.path).unwrap().encrypted);
        assert!(load(&generated.path, None).is_err());
        assert_eq!(key_of(&generated.path, Some("eins")).key_data(), original.key_data());

        // Die alte Passphrase muss stimmen – sonst bleibt die Datei, wie sie ist.
        let before = std::fs::read(&generated.path).unwrap();
        assert_eq!(
            change_passphrase(&generated.path, Some("falsch"), Some("zwei"))
                .unwrap_err()
                .code,
            "wrong_passphrase"
        );
        assert_eq!(
            change_passphrase(&generated.path, None, Some("zwei")).unwrap_err().code,
            "wrong_passphrase"
        );
        assert_eq!(std::fs::read(&generated.path).unwrap(), before);

        change_passphrase(&generated.path, Some("eins"), Some("zwei")).unwrap();
        assert_eq!(key_of(&generated.path, Some("zwei")).key_data(), original.key_data());
        assert_eq!(key_of(&generated.path, Some("zwei")).comment().to_string(), "me@pc");

        change_passphrase(&generated.path, Some("zwei"), None).unwrap();
        assert!(!inspect(&generated.path).unwrap().encrypted);
        assert_eq!(key_of(&generated.path, None).key_data(), original.key_data());

        // Nichts bleibt daneben liegen.
        let mut names: Vec<_> = std::fs::read_dir(&ssh)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        names.sort();
        assert_eq!(names, ["id_test", "id_test.pub"]);
    }

    #[test]
    fn a_key_in_puttys_format_keeps_its_format_when_the_passphrase_changes() {
        let ssh = directory("ppk-passphrase");
        let generated = keys::generate(&ssh, "id_test", Some("eins"), "me@pc").unwrap();
        let original = key_of(&generated.path, Some("eins"));
        let ppk = convert(&generated.path, Some("eins"), Format::Ppk, "id_test.ppk").unwrap();

        // Die Kopie ist mit derselben Passphrase geschützt.
        assert!(inspect(&ppk).unwrap().encrypted);
        assert_eq!(key_of(&ppk, Some("eins")).key_data(), original.key_data());

        change_passphrase(&ppk, Some("eins"), Some("zwei")).unwrap();

        let seen = inspect(&ppk).unwrap();
        assert_eq!(seen.format, Format::Ppk);
        assert!(seen.encrypted);
        assert_eq!(seen.comment, "me@pc");
        assert_eq!(key_of(&ppk, Some("zwei")).key_data(), original.key_data());
        assert!(load(&ppk, Some("eins")).is_err());
    }

    #[test]
    fn converting_writes_a_new_file_and_leaves_everything_else_alone() {
        let ssh = directory("convert");
        let generated = keys::generate(&ssh, "id_test", None, "me@pc").unwrap();
        let original = key_of(&generated.path, None);
        let before = std::fs::read(&generated.path).unwrap();

        let ppk = convert(&generated.path, None, Format::Ppk, "id_test.ppk").unwrap();

        assert_eq!(ppk, ssh.join("id_test.ppk"));
        assert_eq!(std::fs::read(&generated.path).unwrap(), before);
        assert!(!ssh.join("id_test.ppk.pub").exists());
        assert_eq!(key_of(&ppk, None).key_data(), original.key_data());

        // Und zurück: ein Paar wie von ssh-keygen.
        let back = convert(&ppk, None, Format::Openssh, "id_back").unwrap();
        assert_eq!(key_of(&back, None).key_data(), original.key_data());
        assert_eq!(
            std::fs::read_to_string(ssh.join("id_back.pub")).unwrap(),
            format!("{}\n", generated.public_key)
        );

        // Nichts Vorhandenes wird überschrieben, kein Pfad als Name genommen, nichts ins selbe Format geschrieben.
        assert_eq!(
            convert(&generated.path, None, Format::Ppk, "id_test.ppk")
                .unwrap_err()
                .code,
            "exists"
        );
        std::fs::write(ssh.join("id_new.pub"), "taken").unwrap();
        assert_eq!(
            convert(&ppk, None, Format::Openssh, "id_new").unwrap_err().code,
            "exists"
        );
        assert!(!ssh.join("id_new").exists());
        for name in ["../x.ppk", "a/b.ppk", "", "config", "x.pub"] {
            assert_eq!(
                convert(&generated.path, None, Format::Ppk, name).unwrap_err().code,
                "invalid_name",
                "{name:?}"
            );
        }
        assert_eq!(
            convert(&generated.path, None, Format::Openssh, "id_same")
                .unwrap_err()
                .code,
            "same_format"
        );
        assert_eq!(
            convert(&ssh.join("id_test.pub"), None, Format::Ppk, "x.ppk")
                .unwrap_err()
                .code,
            "unreadable"
        );
    }

    /// Einer von gut 500 Ed25519-Schlüsseln beginnt so, dass die Bibliothek seine Datei von PuTTY ablehnt. Für die
    /// Anmeldung und für alles hier gilt dann der eigene Leser.
    #[test]
    fn a_putty_file_the_library_refuses_is_still_read() {
        use russh::keys::ssh_key::private::{Ed25519Keypair, KeypairData};

        let ssh = directory("edge");
        let mut seed = [0x5a; 32];
        seed[..2].fill(0);
        let key = PrivateKey::new(KeypairData::Ed25519(Ed25519Keypair::from_seed(&seed)), "edge").unwrap();
        let path = ssh.join("edge.ppk");
        std::fs::write(&path, render(&key, Format::Ppk, Some("geheim")).unwrap().as_bytes()).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(russh::keys::decode_secret_key(&text, Some("geheim")).is_err());

        assert_eq!(key_of(&path, Some("geheim")).key_data(), key.key_data());
        assert!(load(&path, Some("falsch")).is_err());
        assert!(load(&path, None).is_err());

        change_passphrase(&path, Some("geheim"), None).unwrap();
        assert_eq!(key_of(&path, None).key_data(), key.key_data());
        let back = convert(&path, None, Format::Openssh, "edge").unwrap();
        assert_eq!(key_of(&back, None).key_data(), key.key_data());
    }

    #[test]
    fn an_rsa_key_survives_the_way_to_putty_and_back() {
        let ssh = directory("rsa");
        let mut key = PrivateKey::random(&mut rand::rng(), Algorithm::Rsa { hash: None }).unwrap();
        key.set_comment("rsa test");
        let path = ssh.join("id_rsa");
        std::fs::write(&path, key.to_openssh(LineEnding::LF).unwrap().as_bytes()).unwrap();

        let seen = inspect(&path).unwrap();
        assert_eq!(seen.algorithm, "ssh-rsa");
        assert!(seen.bits.is_some_and(|bits| bits >= 2048), "{:?}", seen.bits);

        let ppk = convert(&path, None, Format::Ppk, "id_rsa.ppk").unwrap();
        assert_eq!(key_of(&ppk, None).key_data(), key.key_data());
        change_passphrase(&ppk, None, Some("geheim")).unwrap();
        assert_eq!(key_of(&ppk, Some("geheim")).key_data(), key.key_data());
    }
}
