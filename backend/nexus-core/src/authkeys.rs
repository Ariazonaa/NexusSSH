//! Die Schlüssel, die sich an einem Konto des Servers anmelden dürfen (`authorized_keys`): ansehen, einzeln
//! entfernen, einen anhängen – nur über SFTP, nie über einen Befehl der Shell. („Schlüssel erzeugen und hinterlegen“
//! hängt weiter über [`crate::keys::install_command`] an.)
//!
//! Die Datei kommt vom Server und ist fremd: Gelesen wird sie nur bis [`crate::sftp::READ_MAX`], gezeigt nur, was der
//! Kern selbst aus ihr errechnet (Art, Länge, Fingerabdruck) und der Kommentar samt Optionen ohne Steuerzeichen.
//! Entfernt wird genau eine Zeile; alles andere bleibt Byte für Byte, wie es war – auch Kommentare, Leerzeilen und
//! Zeilenenden. Hat sich die Datei seit dem Ansehen geändert, entfernt der Kern nichts.

use russh::keys::{HashAlg, PublicKey};
use russh_sftp::client::SftpSession;
use sha2::{Digest, Sha256};

use crate::sftp::Failure;

/// So viele Einträge je Datei zeigt der Kern höchstens; dahinter kommt nichts mehr.
const MAX_KEYS: usize = 2000;

/// So viele Zeichen eines Kommentars, einer Option oder einer unlesbaren Zeile reichen der App.
const SHOWN: usize = 200;

/// Eine Zeile mit einem Schlüssel.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct ServerKey {
    /// Nummer der Zeile in der Datei, ab 1 – zusammen mit `fingerprint` das, was [`remove`] braucht.
    pub line: u32,
    /// Art des Schlüssels, z. B. `ssh-ed25519`; leer, wenn die Zeile nicht zu lesen war.
    pub algorithm: String,
    /// Länge in Bit, wo bekannt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bits: Option<u32>,
    /// Fingerabdruck (SHA-256) in der Schreibweise von OpenSSH; leer wie `algorithm`.
    pub fingerprint: String,
    /// Der Kommentar (meist `benutzer@rechner`), ohne Steuerzeichen.
    pub comment: String,
    /// Was vor dem Schlüssel steht (`from=…`, `command=…`, `restrict`), ohne Steuerzeichen; leer, wenn nichts.
    pub options: String,
    /// Die Zeile, wenn sie kein Schlüssel war – gekürzt, zum Erkennen. Leer bei einem lesbaren Schlüssel.
    #[serde(skip_serializing_if = "String::is_empty")]
    pub unreadable: String,
}

/// Eine Datei mit Schlüsseln auf dem Server.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct ServerKeyFile {
    /// Der Pfad, wie SFTP ihn nennt.
    pub path: String,
    /// Ob es die Datei gibt.
    pub exists: bool,
    /// Gilt für alle Administratoren des Servers (Windows: `administrators_authorized_keys`).
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub shared: bool,
    /// SHA-256 des Inhalts als Hex – [`remove`] prüft damit, dass sich nichts geändert hat.
    pub version: String,
    /// Die Schlüssel, in der Reihenfolge der Datei.
    pub keys: Vec<ServerKey>,
    /// Warum die Datei nicht ganz gelesen wurde (`too_large`, `permission_denied`, `too_many`); sonst nichts.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<&'static str>,
}

fn failed(code: &'static str, message: impl Into<String>) -> Failure {
    Failure {
        code,
        message: message.into(),
    }
}

fn shown(text: &str) -> String {
    text.chars().filter(|c| !c.is_control()).take(SHOWN).collect()
}

/// SHA-256 des Inhalts als Hex.
pub(crate) fn version_of(data: &[u8]) -> String {
    Sha256::digest(data).iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Die Zeilen der Datei als Stücke der Bytes: Anfang, Ende ohne Zeilenumbruch, Ende samt Umbruch.
fn lines(data: &[u8]) -> Vec<(usize, usize, usize)> {
    let mut result = Vec::new();
    let mut start = 0;
    while start < data.len() {
        let (end, next) = match data[start..].iter().position(|byte| *byte == b'\n') {
            Some(at) => (start + at, start + at + 1),
            None => (data.len(), data.len()),
        };
        result.push((start, end, next));
        start = next;
    }
    result
}

// Wo die Optionen vor dem Schlüssel enden: am ersten Leerraum außerhalb von Anführungszeichen (sshd erlaubt darin
// Leerzeichen und `\"`, z. B. `command="echo a b"`).
fn options_end(line: &str) -> Option<usize> {
    let mut quoted = false;
    let mut escaped = false;
    for (at, character) in line.char_indices() {
        match character {
            _ if escaped => escaped = false,
            '\\' if quoted => escaped = true,
            '"' => quoted = !quoted,
            ' ' | '\t' if !quoted => return Some(at),
            _ => {}
        }
    }
    None
}

/// Eine Zeile als Schlüssel: Optionen und Schlüssel, oder `None`.
fn key_of(line: &str) -> Option<(String, PublicKey)> {
    if let Ok(key) = PublicKey::from_openssh(line) {
        return Some((String::new(), key));
    }
    let end = options_end(line)?;
    let key = PublicKey::from_openssh(line[end..].trim_start()).ok()?;
    Some((line[..end].to_owned(), key))
}

/// Liest den Inhalt einer Datei `authorized_keys`. Leerzeilen und Kommentare (`#`) zählen mit, zeigen sich aber
/// nicht. Das zweite: ob alles gelesen ist (sonst hörte das Lesen nach [`MAX_KEYS`] Einträgen auf).
pub(crate) fn parse(data: &[u8]) -> (Vec<ServerKey>, bool) {
    let mut keys = Vec::new();
    for (number, (start, end, _)) in lines(data).into_iter().enumerate() {
        let text = String::from_utf8_lossy(&data[start..end]);
        let line = text.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if keys.len() == MAX_KEYS {
            return (keys, false);
        }

        let line_number = u32::try_from(number + 1).unwrap_or(u32::MAX);
        keys.push(match key_of(line) {
            Some((options, key)) => ServerKey {
                line: line_number,
                algorithm: key.algorithm().as_str().to_owned(),
                bits: crate::keyfiles::bits_of(&key),
                fingerprint: key.fingerprint(HashAlg::Sha256).to_string(),
                comment: shown(key.comment().as_str_lossy()),
                options: shown(&options),
                unreadable: String::new(),
            },
            None => ServerKey {
                line: line_number,
                algorithm: String::new(),
                bits: None,
                fingerprint: String::new(),
                comment: String::new(),
                options: String::new(),
                unreadable: shown(line).chars().take(60).collect(),
            },
        });
    }
    (keys, true)
}

/// Der Inhalt ohne die Zeile `line` – aber nur, wenn dort der Schlüssel mit diesem Fingerabdruck steht. Alles
/// andere bleibt Byte für Byte; die Zeile geht samt ihrem Umbruch.
pub(crate) fn without(data: &[u8], line: u32, fingerprint: &str) -> Result<Vec<u8>, Failure> {
    let changed = || failed("changed", "the file changed since it was read");
    let index = usize::try_from(line)
        .ok()
        .and_then(|line| line.checked_sub(1))
        .ok_or_else(changed)?;
    let (start, end, next) = *lines(data).get(index).ok_or_else(changed)?;
    let text = String::from_utf8_lossy(&data[start..end]);
    let (_, key) = key_of(text.trim()).ok_or_else(changed)?;
    if fingerprint.is_empty() || key.fingerprint(HashAlg::Sha256).to_string() != fingerprint {
        return Err(changed());
    }

    let mut result = Vec::with_capacity(data.len());
    result.extend_from_slice(&data[..start]);
    result.extend_from_slice(&data[next..]);
    Ok(result)
}

// Ein Pfad wie `/C:/Users/name`: der Server ist ein Windows (OpenSSH für Windows nennt Pfade so).
fn on_windows(home: &str) -> bool {
    let bytes = home.as_bytes();
    bytes.len() >= 3 && bytes[0] == b'/' && bytes[1].is_ascii_alphabetic() && bytes[2] == b':'
}

/// Die Dateien, in denen sshd für dieses Konto nachsieht – soweit der Kern das ohne die Konfiguration des Servers
/// sagen kann: `~/.ssh/authorized_keys`; root unter OpenWrt dazu `/etc/dropbear/authorized_keys` (dropbear liest
/// für root nur die); auf Windows dazu `administrators_authorized_keys` (OpenSSH für Windows liest für
/// Administratoren nur die). Das zweite heißt: gilt für alle Administratoren.
async fn candidates(sftp: &SftpSession) -> Result<Vec<(String, bool)>, Failure> {
    let home = sftp.canonicalize(".").await?;
    let own = format!("{}/.ssh/authorized_keys", home.trim_end_matches('/'));
    if on_windows(&home) {
        let drive = &home[..3];
        return Ok(vec![
            (own, false),
            (format!("{drive}/ProgramData/ssh/administrators_authorized_keys"), true),
        ]);
    }
    if home == "/root" && sftp.try_exists("/etc/openwrt_release").await.unwrap_or(false) {
        return Ok(vec![("/etc/dropbear/authorized_keys".to_owned(), false), (own, false)]);
    }
    Ok(vec![(own, false)])
}

// Liest eine Datei ganz; `Err("too_large")`, wenn sie größer ist als [`crate::sftp::READ_MAX`].
async fn read_whole(sftp: &SftpSession, path: &str) -> Result<Vec<u8>, Failure> {
    let (data, size) = crate::sftp::read_part(sftp, path, 0, crate::sftp::READ_MAX).await?;
    if size > data.len() as u64 {
        return Err(failed("too_large", format!("{path} has {size} bytes")));
    }
    Ok(data)
}

/// Die Dateien mit Schlüsseln für das angemeldete Konto. Eine Datei, die es nicht gibt, steht mit `exists: false`
/// da – außer der für alle Administratoren, die nur erscheint, wenn es sie gibt und das Konto sie lesen darf.
pub(crate) async fn list(sftp: &SftpSession) -> Result<Vec<ServerKeyFile>, Failure> {
    let mut files = Vec::new();
    for (path, shared) in candidates(sftp).await? {
        let mut file = ServerKeyFile {
            path: path.clone(),
            exists: true,
            shared,
            version: String::new(),
            keys: Vec::new(),
            error: None,
        };
        match read_whole(sftp, &path).await {
            Ok(data) => {
                let (keys, complete) = parse(&data);
                file.version = version_of(&data);
                file.keys = keys;
                file.error = (!complete).then_some("too_many");
            }
            Err(failure) if shared && matches!(failure.code, "not_found" | "permission_denied") => continue,
            Err(failure) if failure.code == "not_found" => file.exists = false,
            Err(failure) => file.error = Some(failure.code),
        }
        files.push(file);
    }
    Ok(files)
}

/// Entfernt aus `path` die Zeile `line`, wenn die Datei noch `version` ist und dort der Schlüssel mit
/// `fingerprint` steht. Geschrieben wird an Ort und Stelle (Besitzer, Rechte und unter Windows die ACL bleiben –
/// sshd prüft sie), danach noch einmal gelesen.
pub(crate) async fn remove(
    sftp: &SftpSession,
    path: &str,
    version: &str,
    line: u32,
    fingerprint: &str,
) -> Result<(), Failure> {
    // Nur eine der Dateien, die [`list`] zeigt – die App nennt den Pfad, gemeint ist aber nie eine andere Datei.
    if !candidates(sftp).await?.iter().any(|(candidate, _)| candidate == path) {
        return Err(failed(
            "not_allowed",
            format!("{path} is not a file of authorized keys"),
        ));
    }
    let data = read_whole(sftp, path).await?;
    if version_of(&data) != version {
        return Err(failed("changed", "the file changed since it was read"));
    }
    let next = without(&data, line, fingerprint)?;
    crate::sftp::write_whole(sftp, path, &next).await?;
    if read_whole(sftp, path).await? != next {
        return Err(failed("not_written", format!("{path} does not read back as written")));
    }
    Ok(())
}

/// Der Inhalt mit `line` am Ende – im Zeilenende, das die Datei schon benutzt (CRLF, wenn sie es tut). Fehlt der
/// letzten Zeile ihr Umbruch, bekommt sie einen; sonst bleibt alles Byte für Byte.
pub(crate) fn with(data: &[u8], line: &str) -> Vec<u8> {
    let end: &[u8] = if data.windows(2).any(|pair| pair == b"\r\n") {
        b"\r\n"
    } else {
        b"\n"
    };
    let mut next = data.to_vec();
    if !next.is_empty() && !next.ends_with(b"\n") {
        next.extend_from_slice(end);
    }
    next.extend_from_slice(line.as_bytes());
    next.extend_from_slice(end);
    next
}

/// Hängt `public_key` an `path` an – nur an eine der Dateien von [`list`], und nur, wenn sie noch `version` ist
/// (eine Datei, die es nicht gab: leere Version). Steht der Schlüssel schon dort, geschieht nichts. Fehlt die Datei,
/// entsteht sie samt Verzeichnis mit den Rechten, die sshd verlangt (0600, 0700). Über SFTP wie [`remove`]: kein
/// Befehl, also auch ohne Shell, auf Windows-Servern und ohne einen weiteren Kanal (`MaxSessions`).
///
/// `public_key` kommt von der App: Nur was sich als Schlüssel lesen lässt, wird verwendet, und dann in der Form, die
/// die Bibliothek selbst schreibt.
pub(crate) async fn add(sftp: &SftpSession, path: &str, version: &str, public_key: &str) -> Result<(), Failure> {
    let key = PublicKey::from_openssh(public_key.trim()).map_err(|error| failed("invalid_key", error.to_string()))?;
    let line = key
        .to_openssh()
        .map_err(|error| failed("invalid_key", error.to_string()))?;
    if !candidates(sftp).await?.iter().any(|(candidate, _)| candidate == path) {
        return Err(failed(
            "not_allowed",
            format!("{path} is not a file of authorized keys"),
        ));
    }

    let (data, existed) = match read_whole(sftp, path).await {
        Ok(data) => (data, true),
        Err(failure) if failure.code == "not_found" => (Vec::new(), false),
        Err(failure) => return Err(failure),
    };
    let current = if existed { version_of(&data) } else { String::new() };
    if current != version {
        return Err(failed("changed", "the file changed since it was read"));
    }
    let fingerprint = key.fingerprint(HashAlg::Sha256).to_string();
    if parse(&data).0.iter().any(|known| known.fingerprint == fingerprint) {
        return Ok(());
    }

    let mode = |bits: u32| russh_sftp::protocol::FileAttributes {
        permissions: Some(bits),
        ..russh_sftp::protocol::FileAttributes::empty()
    };
    if !existed
        && let Some((directory, _)) = path.rsplit_once('/')
        && !directory.is_empty()
        && !sftp.try_exists(directory).await?
    {
        sftp.create_dir(directory).await?;
        let _ = sftp.set_metadata(directory, mode(0o700)).await;
    }
    let next = with(&data, &line);
    crate::sftp::write_whole(sftp, path, &next).await?;
    if !existed {
        let _ = sftp.set_metadata(path, mode(0o600)).await;
    }
    if read_whole(sftp, path).await? != next {
        return Err(failed("not_written", format!("{path} does not read back as written")));
    }
    Ok(())
}

/// Für die Zufallstests: Lesen und Entfernen jeder gefundenen Zeile, ohne Panic.
pub(crate) fn exercise(data: &[u8]) {
    let (keys, _) = parse(data);
    for key in keys.iter().take(8) {
        if let Ok(next) = without(data, key.line, &key.fingerprint) {
            assert!(next.len() < data.len(), "removing a line did not shorten the file");
        }
    }
}

/// Echte Eingaben für die Zufallstests: frisch erzeugte Schlüssel mit Optionen, Kommentaren, CRLF und Kaputtem.
pub(crate) fn samples() -> Vec<Vec<u8>> {
    let line = |comment: &str| {
        russh::keys::PrivateKey::random(&mut rand::rng(), russh::keys::Algorithm::Ed25519)
            .ok()
            .and_then(|key| {
                let mut public = key.public_key().clone();
                public.set_comment(comment);
                public.to_openssh().ok()
            })
            .unwrap_or_default()
    };
    let (a, b) = (line("me@pc"), line("with spaces"));
    vec![
        format!("{a}\n# comment\ncommand=\"echo a b\",no-pty {b}\r\nbroken line\n\n").into_bytes(),
        format!("from=\"10.0.0.1,a\\\"b\" {a}").into_bytes(),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(comment: &str) -> String {
        let key = russh::keys::PrivateKey::random(&mut rand::rng(), russh::keys::Algorithm::Ed25519).unwrap();
        let mut public = key.public_key().clone();
        public.set_comment(comment);
        public.to_openssh().unwrap()
    }

    fn fingerprint(line: &str) -> String {
        key_of(line).unwrap().1.fingerprint(HashAlg::Sha256).to_string()
    }

    #[test]
    fn keys_options_comments_and_broken_lines_are_read_with_their_line_numbers() {
        let (a, b, c) = (key("me@pc"), key("with spaces in it"), key("opt"));
        let text = format!("# from the admin\n{a}\n\n{b}\r\ncommand=\"echo a b\",no-pty {c}\nnot a key at all\n   \n");

        let (keys, complete) = parse(text.as_bytes());

        assert!(complete);
        assert_eq!(keys.len(), 4);
        assert_eq!(
            (keys[0].line, keys[0].algorithm.as_str(), keys[0].bits),
            (2, "ssh-ed25519", Some(256))
        );
        assert_eq!(keys[0].comment, "me@pc");
        assert_eq!(keys[0].fingerprint, fingerprint(&a));
        assert_eq!((keys[1].line, keys[1].comment.as_str()), (4, "with spaces in it"));
        assert_eq!(
            (keys[2].line, keys[2].options.as_str()),
            (5, "command=\"echo a b\",no-pty")
        );
        assert_eq!(keys[2].comment, "opt");
        assert_eq!((keys[3].line, keys[3].unreadable.as_str()), (6, "not a key at all"));
        assert!(keys[3].fingerprint.is_empty());
    }

    #[test]
    fn removing_takes_exactly_that_line_and_leaves_every_other_byte() {
        let (a, b, c) = (key("a"), key("b"), key("c"));
        let text = format!("# keep me\r\n{a}\r\n\n{b}\r\n{c}");

        let next = without(text.as_bytes(), 4, &fingerprint(&b)).unwrap();
        assert_eq!(String::from_utf8(next).unwrap(), format!("# keep me\r\n{a}\r\n\n{c}"));

        // Die letzte Zeile ohne Umbruch: Was davor steht, bleibt samt seinem Umbruch.
        let next = without(text.as_bytes(), 5, &fingerprint(&c)).unwrap();
        assert_eq!(
            String::from_utf8(next).unwrap(),
            format!("# keep me\r\n{a}\r\n\n{b}\r\n")
        );
    }

    #[test]
    fn adding_appends_in_the_line_ending_the_file_already_uses() {
        assert_eq!(with(b"", "k"), b"k\n");
        assert_eq!(with(b"# a\n", "k"), b"# a\nk\n");
        assert_eq!(with(b"# a\r\nb", "k"), b"# a\r\nb\r\nk\r\n");
        assert_eq!(with(b"b", "k"), b"b\nk\n");
    }

    #[test]
    fn a_line_that_no_longer_holds_that_key_is_not_removed() {
        let (a, b) = (key("a"), key("b"));
        let text = format!("{a}\n{b}\n");

        for (line, print) in [
            (1, fingerprint(&b)),
            (3, fingerprint(&a)),
            (0, fingerprint(&a)),
            (1, String::new()),
        ] {
            let failure = without(text.as_bytes(), line, &print).unwrap_err();
            assert_eq!(failure.code, "changed", "line {line}");
        }
    }

    #[test]
    fn control_characters_in_comments_and_options_do_not_reach_the_app() {
        let a = key("evil\u{1b}[31mred\u{7}");
        let (keys, _) = parse(format!("from=\"10.0.0.1\u{1b}\" {a}\n").as_bytes());
        // Steuerzeichen in den Optionen machen sie für sshd ungültig – der Kern liest die Zeile trotzdem.
        assert!(!keys[0].comment.chars().any(char::is_control));
        assert!(!keys[0].options.chars().any(char::is_control));
    }

    #[test]
    fn the_version_changes_with_every_byte() {
        assert_ne!(version_of(b"a\n"), version_of(b"a\r\n"));
        assert_eq!(version_of(b"").len(), 64);
    }

    #[test]
    fn a_windows_home_is_recognised() {
        assert!(on_windows("/C:/Users/me"));
        assert!(!on_windows("/home/me"));
        assert!(!on_windows("/"));
    }
}
