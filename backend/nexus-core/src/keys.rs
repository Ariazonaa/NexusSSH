//! Ein Schlüsselpaar anlegen und seinen öffentlichen Teil auf einem Server hinterlegen – was sonst `ssh-keygen`
//! und `ssh-copy-id` tun.
//!
//! Der private Schlüssel entsteht im `~/.ssh` des Benutzers und verlässt den Rechner nie; vorhandene Dateien
//! werden nicht überschrieben. Auf den Server geht nur die Zeile des öffentlichen Schlüssels, als Befehl an
//! dessen Shell (`exec`) – sie wird vorher als Schlüssel gelesen und neu geschrieben, damit dort nichts anderes
//! ankommt als ein Schlüssel.

use std::io::Write;
use std::path::{Path, PathBuf};

use russh::keys::ssh_key::{Algorithm, LineEnding};
use russh::keys::{PrivateKey, PublicKey};

use crate::exec;
use crate::sftp::Failure;

/// Das neue Paar: wo der private Schlüssel liegt, und die Zeile für `authorized_keys`.
#[derive(Debug)]
pub(crate) struct Generated {
    pub(crate) path: PathBuf,
    pub(crate) public_key: String,
}

fn failed(code: &'static str, message: impl Into<String>) -> Failure {
    Failure {
        code,
        message: message.into(),
    }
}

/// Ein Dateiname, kein Pfad: Buchstaben, Ziffern, `_`, `-` und `.`, nicht mit einem Punkt vorn – und nicht `config`
/// oder `known_hosts`, die in `~/.ssh` etwas anderes bedeuten.
pub(crate) fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 64
        && !name.starts_with('.')
        && !name.ends_with(".pub")
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.'))
        && !matches!(
            name.to_ascii_lowercase().as_str(),
            "config" | "known_hosts" | "authorized_keys"
        )
}

/// Der Kommentar am öffentlichen Schlüssel: eine Zeile ohne Steuerzeichen, gekürzt.
fn comment_of(text: &str) -> String {
    text.chars().filter(|c| !c.is_control()).take(100).collect()
}

/// Legt in `ssh_dir` ein Ed25519-Paar an: `name` und `name.pub`. Mit `passphrase` wird der private Schlüssel
/// verschlüsselt. Gibt es eine der beiden Dateien schon, entsteht nichts.
pub(crate) fn generate(
    ssh_dir: &Path,
    name: &str,
    passphrase: Option<&str>,
    comment: &str,
) -> Result<Generated, Failure> {
    if !valid_name(name) {
        return Err(failed("invalid_name", format!("{name:?} is not a name for a key file")));
    }

    let path = ssh_dir.join(name);
    let public_path = ssh_dir.join(format!("{name}.pub"));
    if path.exists() || public_path.exists() {
        return Err(failed("exists", format!("{} already exists", path.display())));
    }

    let crypto = |error: russh::keys::ssh_key::Error| failed("failed", error.to_string());
    let mut rng = rand::rng();
    let mut key = PrivateKey::random(&mut rng, Algorithm::Ed25519).map_err(crypto)?;
    key.set_comment(comment_of(comment));
    let public_key = key.public_key().to_openssh().map_err(crypto)?;
    if let Some(passphrase) = passphrase.filter(|passphrase| !passphrase.is_empty()) {
        key = key.encrypt(&mut rng, passphrase).map_err(crypto)?;
    }

    let text = key.to_openssh(LineEnding::LF).map_err(crypto)?;
    let io = |error: std::io::Error| failed("failed", error.to_string());
    std::fs::create_dir_all(ssh_dir).map_err(io)?;
    write_new(&path, text.as_bytes(), true).map_err(io)?;
    if let Err(error) = write_new(&public_path, format!("{public_key}\n").as_bytes(), false) {
        // Ein privater Schlüssel ohne seinen öffentlichen Teil wäre ein halbes Paar.
        let _ = std::fs::remove_file(&path);
        return Err(io(error));
    }

    Ok(Generated { path, public_key })
}

/// Schreibt eine Datei, die es noch nicht gibt (`create_new`); `private` unter Unix nur für den Besitzer lesbar.
/// Unter Windows erbt sie die Rechte des Benutzerordners.
pub(crate) fn write_new(path: &Path, content: &[u8], private: bool) -> std::io::Result<()> {
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(if private { 0o600 } else { 0o644 });
    }
    #[cfg(not(unix))]
    let _ = private;

    let mut file = options.open(path)?;
    file.write_all(content)?;
    file.sync_all()
}

/// Der Befehl, der eine Zeile in `~/.ssh/authorized_keys` des angemeldeten Benutzers einträgt – einmal: Steht sie
/// schon dort, geschieht nichts. Verzeichnis und Datei bekommen die Rechte, die sshd verlangt. Für root unter
/// OpenWrt in `/etc/dropbear/authorized_keys`: Nur dort sieht dropbear für root nach.
///
/// `public_key` ist fremde Eingabe (die App reicht sie durch): Nur was sich als Schlüssel lesen lässt, wird
/// verwendet, und dann in der Form, die die Bibliothek selbst schreibt.
pub(crate) fn install_command(public_key: &str) -> Result<String, Failure> {
    let key = PublicKey::from_openssh(public_key.trim()).map_err(|error| failed("invalid_key", error.to_string()))?;
    let line = key
        .to_openssh()
        .map_err(|error| failed("invalid_key", error.to_string()))?;
    let quoted = exec::quote(&line).ok_or_else(|| failed("invalid_key", "the key cannot be passed to a command"))?;
    Ok(format!(
        "{INSTALL_START}{quoted} \"$f\" 2>/dev/null || printf '%s\\n' {quoted} >> \"$f\"; }}"
    ))
}

/// Der Anfang von [`install_command`] bis zum Schlüssel.
const INSTALL_START: &str = concat!(
    "umask 077 && f=~/.ssh/authorized_keys && ",
    "if [ \"$(id -u)\" = 0 ] && [ -f /etc/openwrt_release ] && [ -d /etc/dropbear ]; ",
    "then f=/etc/dropbear/authorized_keys; else mkdir -p ~/.ssh; fi && { grep -qxF -- "
);

#[cfg(test)]
mod tests {
    use super::*;

    fn directory(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!(
            "nexus-core-keys-{}-{name}-{}",
            std::process::id(),
            crate::profiles::unix_now()
        ));
        let _ = std::fs::remove_dir_all(&directory);
        directory
    }

    #[test]
    fn a_new_pair_is_written_and_can_be_read_back() {
        let ssh = directory("plain").join(".ssh");

        let generated = generate(&ssh, "id_test", None, "me@pc\nsecond line").unwrap();

        assert_eq!(generated.path, ssh.join("id_test"));
        assert!(generated.public_key.starts_with("ssh-ed25519 "));
        assert!(generated.public_key.ends_with(" me@pcsecond line"));
        assert_eq!(
            std::fs::read_to_string(ssh.join("id_test.pub")).unwrap(),
            format!("{}\n", generated.public_key)
        );

        let private = PrivateKey::from_openssh(std::fs::read_to_string(&generated.path).unwrap()).unwrap();
        assert!(!private.is_encrypted());
        assert_eq!(
            private.public_key().key_data(),
            PublicKey::from_openssh(&generated.public_key).unwrap().key_data()
        );
    }

    /// Schlüssel im Format von PuTTY (`.ppk`) liest der Kern wie jeden anderen – deshalb übernimmt der Import aus
    /// PuTTY und WinSCP sie. Ein solcher Schlüssel lässt sich hier nicht erzeugen, und ein privater Schlüssel hat im
    /// Repository nichts verloren: Der Test läuft nur, wenn `NEXUS_PPK_DIR` einen Ordner nennt, in dem `plain`
    /// (OpenSSH), `plain.ppk` und `secret.ppk` (Passphrase „geheim“) desselben Schlüssels liegen – zum Beispiel
    /// aus `ssh-keygen` und `WinSCP.com /keygen`.
    #[test]
    fn keys_in_puttys_format_are_read_like_any_other() {
        let Some(directory) = std::env::var_os("NEXUS_PPK_DIR").map(PathBuf::from) else {
            return;
        };
        let expected = russh::keys::load_secret_key(directory.join("plain"), None).unwrap();

        let plain = russh::keys::load_secret_key(directory.join("plain.ppk"), None).unwrap();
        assert_eq!(plain.public_key().key_data(), expected.public_key().key_data());

        let secret = russh::keys::load_secret_key(directory.join("secret.ppk"), Some("geheim")).unwrap();
        assert_eq!(secret.public_key().key_data(), expected.public_key().key_data());
        assert!(russh::keys::load_secret_key(directory.join("secret.ppk"), Some("falsch")).is_err());
        assert!(russh::keys::load_secret_key(directory.join("secret.ppk"), None).is_err());
    }

    #[test]
    fn a_passphrase_encrypts_the_private_key() {
        let ssh = directory("secret");

        let generated = generate(&ssh, "id_secret", Some("correct horse"), "").unwrap();

        let private = PrivateKey::from_openssh(std::fs::read_to_string(&generated.path).unwrap()).unwrap();
        assert!(private.is_encrypted());
        assert!(private.decrypt("wrong").is_err());
        assert!(private.decrypt("correct horse").is_ok());
    }

    #[test]
    fn nothing_that_exists_is_overwritten_and_only_plain_names_are_taken() {
        let ssh = directory("taken");
        std::fs::create_dir_all(&ssh).unwrap();
        std::fs::write(ssh.join("id_old"), "old key").unwrap();
        std::fs::write(ssh.join("id_half.pub"), "old public key").unwrap();

        assert_eq!(generate(&ssh, "id_old", None, "").unwrap_err().code, "exists");
        assert_eq!(generate(&ssh, "id_half", None, "").unwrap_err().code, "exists");
        assert_eq!(std::fs::read_to_string(ssh.join("id_old")).unwrap(), "old key");
        assert!(!ssh.join("id_half").exists());

        for name in [
            "",
            "..",
            "a/b",
            "a\\b",
            ".hidden",
            "config",
            "Known_Hosts",
            "authorized_keys",
            "id.pub",
            "with space",
            "ü",
        ] {
            assert_eq!(
                generate(&ssh, name, None, "").unwrap_err().code,
                "invalid_name",
                "{name:?}"
            );
        }
    }

    #[test]
    fn only_a_key_reaches_the_command_for_the_server() {
        let ssh = directory("install");
        let generated = generate(&ssh, "id_install", None, "it's me").unwrap();

        let command = install_command(&format!("  {}\n", generated.public_key)).unwrap();

        let quoted = exec::quote(&generated.public_key).unwrap();
        assert!(quoted.contains(r"it'\''s me"));
        assert_eq!(
            command,
            format!(
                "umask 077 && f=~/.ssh/authorized_keys && if [ \"$(id -u)\" = 0 ] && [ -f /etc/openwrt_release ] && [ -d /etc/dropbear ]; then f=/etc/dropbear/authorized_keys; else mkdir -p ~/.ssh; fi && {{ grep -qxF -- {quoted} \"$f\" 2>/dev/null || printf '%s\\n' {quoted} >> \"$f\"; }}"
            )
        );

        for bad in ["", "not a key", "ssh-ed25519 AAAA; rm -rf ~", "ssh-ed25519\n$(reboot)"] {
            assert_eq!(install_command(bad).unwrap_err().code, "invalid_key", "{bad:?}");
        }
    }
}
