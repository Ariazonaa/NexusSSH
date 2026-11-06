//! Schlüsseldateien im Format von PuTTY (`.ppk`) schreiben und ihren Kopf lesen.
//!
//! Lesen kann die Bibliothek (`PrivateKey::from_ppk`), schreiben nicht. Geschrieben wird Fassung 3 (PuTTY ab
//! 0.75): mit Passphrase AES-256-CBC, Schlüssel aus Argon2id, sonst unverschlüsselt; in beiden Fällen ein
//! HMAC-SHA-256 über Art, Kommentar und beide Teile. Das Format steht in PuTTYs Handbuch, Anhang C; die
//! Dateien hier sind an `puttygen` 0.83 geprüft – ohne Passphrase stimmen sie Byte für Byte überein.
//!
//! Der Kopf einer Datei (Art, Kommentar, öffentlicher Teil, ob verschlüsselt) steht im Klartext: Für die
//! Übersicht der Schlüssel wird nichts entsperrt.

use aes::cipher::{BlockModeDecrypt, BlockModeEncrypt, KeyIvInit};
use hmac::{Hmac, KeyInit, Mac};
use russh::keys::ssh_key::private::KeypairData;
use russh::keys::ssh_key::{Mpint, PrivateKey, PublicKey};
use sha2::Sha256;
use zeroize::Zeroize;

/// Wie PuTTY: 8 MiB, ein Faden. Die Zahl der Durchläufe misst PuTTY am Rechner aus (0.83 schrieb hier 21).
const ARGON2_MEMORY: u32 = 8192;
const ARGON2_PASSES: u32 = 21;
const ARGON2_PARALLELISM: u32 = 1;

/// Mehr nimmt der eigene Leser aus einer Datei nicht an (KiB bzw. Durchläufe).
const MAX_ARGON2_MEMORY: u32 = 1024 * 1024;
const MAX_ARGON2_PASSES: u32 = 1000;

/// Was im Kopf einer `.ppk`-Datei steht, ohne dass jemand eine Passphrase nennt.
#[derive(Debug)]
pub(crate) struct Header {
    pub(crate) public: PublicKey,
    pub(crate) comment: String,
    pub(crate) encrypted: bool,
}

/// Ob der Text eine Schlüsseldatei von PuTTY ist.
pub(crate) fn is_ppk(text: &str) -> bool {
    text.trim_start().starts_with("PuTTY-User-Key-File-")
}

/// Die Felder einer `.ppk`-Datei, noch nichts entschlüsselt und nichts geprüft.
struct Parsed {
    version: u8,
    algorithm: String,
    encryption: String,
    comment: String,
    public: Vec<u8>,
    private: Vec<u8>,
    derivation: String,
    memory: u32,
    passes: u32,
    parallelism: u32,
    salt: Vec<u8>,
    mac: Vec<u8>,
}

// Die Datei ist fremde Eingabe: Die Zahl der Zeilen eines Teils ist begrenzt, Unbekanntes wird übergangen.
fn parse(text: &str) -> Option<Parsed> {
    let mut lines = text.trim_start().lines();
    let (version, algorithm) = lines.next()?.strip_prefix("PuTTY-User-Key-File-")?.split_once(": ")?;
    let mut parsed = Parsed {
        version: version.parse().ok().filter(|version| matches!(version, 2 | 3))?,
        algorithm: algorithm.trim().to_owned(),
        encryption: "none".to_owned(),
        comment: String::new(),
        public: Vec::new(),
        private: Vec::new(),
        derivation: String::new(),
        memory: 0,
        passes: 0,
        parallelism: 0,
        salt: Vec::new(),
        mac: Vec::new(),
    };
    while let Some(line) = lines.next() {
        let Some((key, value)) = line.split_once(": ") else {
            continue;
        };
        let value = value.trim_end();
        match key {
            "Comment" => parsed.comment = value.chars().filter(|c| !c.is_control()).take(200).collect(),
            "Encryption" => value.clone_into(&mut parsed.encryption),
            "Key-Derivation" => value.clone_into(&mut parsed.derivation),
            "Argon2-Memory" => parsed.memory = value.parse().ok()?,
            "Argon2-Passes" => parsed.passes = value.parse().ok()?,
            "Argon2-Parallelism" => parsed.parallelism = value.parse().ok()?,
            "Argon2-Salt" => parsed.salt = unhex(value)?,
            "Private-MAC" => parsed.mac = unhex(value)?,
            "Public-Lines" | "Private-Lines" => {
                let count: usize = value.parse().ok().filter(|count| *count <= 256)?;
                let mut encoded = String::new();
                for _ in 0..count {
                    encoded.push_str(lines.next()?.trim());
                }

                let decoded = unbase64(&encoded);
                encoded.zeroize();
                if key == "Public-Lines" {
                    parsed.public = decoded?;
                } else {
                    parsed.private = decoded?;
                }
            }
            _ => {}
        }
    }

    Some(parsed)
}

/// Liest den Kopf. `None`, wenn der Text keine solche Datei ist oder ihr öffentlicher Teil sich nicht lesen lässt.
pub(crate) fn header(text: &str) -> Option<Header> {
    let mut parsed = parse(text)?;
    parsed.private.zeroize();
    let public = PublicKey::from_bytes(&parsed.public).ok()?;
    (public.algorithm().as_str() == parsed.algorithm).then_some(Header {
        public,
        comment: parsed.comment,
        encrypted: parsed.encryption != "none",
    })
}

/// Liest einen Ed25519-Schlüssel aus einer `.ppk`-Datei (Fassung 2 und 3) – der Weg für die Dateien, an denen
/// die Bibliothek scheitert: Sie liest die 32 Bytes des privaten Teils als Zahl und lehnt ab, was als Zahl nicht
/// in kürzester Form dasteht (eine Null vorn, danach ein Byte unter 0x80 – einer von gut 500 Schlüsseln).
/// Geprüft wird wie dort: erst der Prüfwert über alle Teile, dann, ob der private Teil zum öffentlichen gehört.
/// `None` bei allem, was nicht stimmt, auch bei einer falschen Passphrase.
pub(crate) fn read_ed25519(text: &str, passphrase: Option<&str>) -> Option<PrivateKey> {
    let mut parsed = parse(text)?;
    let key = opened_ed25519(&mut parsed, passphrase);
    parsed.private.zeroize();
    key
}

fn opened_ed25519(parsed: &mut Parsed, passphrase: Option<&str>) -> Option<PrivateKey> {
    use russh::keys::ssh_key::private::Ed25519Keypair;

    if parsed.algorithm != "ssh-ed25519" {
        return None;
    }

    let old = parsed.version == 2;
    let mut mac_key = Vec::new();
    match parsed.encryption.as_str() {
        "none" if old => mac_key = sha1(&[b"putty-private-key-file-mac-key"]),
        "none" => {}
        "aes256-cbc" => {
            let passphrase = passphrase?.as_bytes();
            let mut derived = [0u8; 80];
            if old {
                derived[..20].copy_from_slice(&sha1(&[&[0, 0, 0, 0], passphrase]));
                derived[20..40].copy_from_slice(&sha1(&[&[0, 0, 0, 1], passphrase]));
                derived[32..48].fill(0);
                mac_key = sha1(&[b"putty-private-key-file-mac-key", passphrase]);
            } else {
                let flavour = match parsed.derivation.as_str() {
                    "Argon2id" => argon2::Algorithm::Argon2id,
                    "Argon2i" => argon2::Algorithm::Argon2i,
                    "Argon2d" => argon2::Algorithm::Argon2d,
                    _ => return None,
                };
                // Die Zahlen stehen in der Datei: Was mehr verlangt als ein Gigabyte oder tausend Durchläufe, ist
                // kein Schlüssel von PuTTY, sondern ein Versuch, den Rechner zu beschäftigen.
                if parsed.memory > MAX_ARGON2_MEMORY || parsed.passes > MAX_ARGON2_PASSES || parsed.parallelism > 16 {
                    return None;
                }

                let params = argon2::Params::new(parsed.memory, parsed.passes, parsed.parallelism, Some(80)).ok()?;
                argon2::Argon2::new(flavour, argon2::Version::V0x13, params)
                    .hash_password_into(passphrase, &parsed.salt, &mut derived)
                    .ok()?;
                mac_key.extend_from_slice(&derived[48..]);
            }

            if parsed.private.is_empty() || !parsed.private.len().is_multiple_of(16) {
                return None;
            }

            let mut cipher = cbc::Decryptor::<aes::Aes256>::new_from_slices(&derived[..32], &derived[32..48]).ok()?;
            for block in parsed.private.as_chunks_mut::<16>().0 {
                cipher.decrypt_block(block.into());
            }
            derived.zeroize();
        }
        _ => return None,
    }

    let mut signed = Vec::new();
    for part in [
        parsed.algorithm.as_bytes(),
        parsed.encryption.as_bytes(),
        parsed.comment.as_bytes(),
        &parsed.public,
        &parsed.private,
    ] {
        put(&mut signed, part);
    }
    let genuine = if old {
        let mut mac = Hmac::<sha1::Sha1>::new_from_slice(&mac_key).ok()?;
        mac.update(&signed);
        mac.verify_slice(&parsed.mac).is_ok()
    } else {
        let mut mac = Hmac::<Sha256>::new_from_slice(&mac_key).ok()?;
        mac.update(&signed);
        mac.verify_slice(&parsed.mac).is_ok()
    };
    signed.zeroize();
    mac_key.zeroize();
    if !genuine {
        return None;
    }

    // Der private Teil: eine Länge und so viele Bytes – 32, wie PuTTY sie schreibt; kürzer oder mit einer Null
    // davor, wie eine Zahl dastünde, wird ebenso gelesen.
    let length = usize::try_from(u32::from_be_bytes(parsed.private.get(..4)?.try_into().ok()?)).ok()?;
    let mut bytes = parsed.private.get(4..4usize.checked_add(length)?)?;
    if bytes.len() == 33 && bytes[0] == 0 {
        bytes = &bytes[1..];
    }
    if bytes.len() > 32 {
        return None;
    }

    let mut seed = [0u8; 32];
    seed[32 - bytes.len()..].copy_from_slice(bytes);
    let pair = Ed25519Keypair::from_seed(&seed);
    seed.zeroize();
    let public = PublicKey::from_bytes(&parsed.public).ok()?;
    if public.key_data().ed25519() != Some(&pair.public) {
        return None;
    }

    PrivateKey::new(KeypairData::Ed25519(pair), parsed.comment.clone()).ok()
}

fn sha1(parts: &[&[u8]]) -> Vec<u8> {
    use sha1::Digest;

    let mut hash = sha1::Sha1::new();
    for part in parts {
        hash.update(part);
    }
    hash.finalize().to_vec()
}

/// Der Schlüssel als Text einer `.ppk`-Datei, mit `passphrase` verschlüsselt. `None` für eine Art von Schlüssel,
/// die PuTTY nicht kennt oder die hier nicht geschrieben wird (DSA, Schlüssel auf einem Sicherheitsschlüssel),
/// und für einen Schlüssel, der noch verschlüsselt ist.
pub(crate) fn write(key: &PrivateKey, passphrase: Option<&str>) -> Option<String> {
    let algorithm = key.algorithm();
    let algorithm = algorithm.as_str();
    let public = key.public_key().to_bytes().ok()?;
    let mut private = private_blob(key.key_data())?;
    let comment: String = key.comment().to_string().chars().filter(|c| !c.is_control()).collect();
    let passphrase = passphrase.filter(|passphrase| !passphrase.is_empty());
    let encryption = if passphrase.is_some() { "aes256-cbc" } else { "none" };

    let mut text = format!("PuTTY-User-Key-File-3: {algorithm}\nEncryption: {encryption}\nComment: {comment}\n");
    lines(&mut text, "Public-Lines", &public);

    let mut mac_key = Vec::new();
    let mut secrets = None;
    if let Some(passphrase) = passphrase {
        use rand::Rng;

        let mut salt = [0u8; 16];
        rand::rng().fill_bytes(&mut salt);
        let params = argon2::Params::new(ARGON2_MEMORY, ARGON2_PASSES, ARGON2_PARALLELISM, Some(80)).ok()?;
        let mut derived = [0u8; 80];
        argon2::Argon2::new(argon2::Algorithm::Argon2id, argon2::Version::V0x13, params)
            .hash_password_into(passphrase.as_bytes(), &salt, &mut derived)
            .ok()?;
        mac_key.extend_from_slice(&derived[48..]);
        secrets = Some(derived);
        text.push_str(&format!(
            "Key-Derivation: Argon2id\nArgon2-Memory: {ARGON2_MEMORY}\nArgon2-Passes: {ARGON2_PASSES}\nArgon2-Parallelism: {ARGON2_PARALLELISM}\nArgon2-Salt: {}\n",
            hex(&salt)
        ));

        // Aufgefüllt auf ganze Blöcke; der Leser hört nach dem Schlüssel auf zu lesen. Der Prüfwert gilt dem
        // aufgefüllten Klartext.
        let mut padding = [0u8; 16];
        rand::rng().fill_bytes(&mut padding);
        let missing = (16 - private.len() % 16) % 16;
        private.extend_from_slice(&padding[..missing]);
    }

    let mut signed = Vec::new();
    for part in [
        algorithm.as_bytes(),
        encryption.as_bytes(),
        comment.as_bytes(),
        &public,
        &private,
    ] {
        put(&mut signed, part);
    }
    let mut mac = Hmac::<Sha256>::new_from_slice(&mac_key).ok()?;
    mac.update(&signed);
    let mac = mac.finalize().into_bytes();
    signed.zeroize();

    if let Some(mut derived) = secrets {
        let mut cipher = cbc::Encryptor::<aes::Aes256>::new_from_slices(&derived[..32], &derived[32..48]).ok()?;
        for block in private.as_chunks_mut::<16>().0 {
            cipher.encrypt_block(block.into());
        }
        derived.zeroize();
    }

    lines(&mut text, "Private-Lines", &private);
    private.zeroize();
    text.push_str(&format!("Private-MAC: {}\n", hex(&mac)));
    Some(text)
}

// Der private Teil, wie PuTTY ihn je Art ablegt.
fn private_blob(data: &KeypairData) -> Option<Vec<u8>> {
    let mut blob = Vec::new();
    match data {
        KeypairData::Ed25519(pair) => {
            // Immer genau die 32 Bytes, wie sie sind – auch mit gesetztem höchsten Bit oder einer Null vorn
            // (an 700 Schlüsseln aus `puttygen` 0.83 nachgezählt). Keine Zahl im Sinn des Protokolls.
            let mut bytes = pair.private.to_bytes();
            put(&mut blob, &bytes);
            bytes.zeroize();
        }
        KeypairData::Rsa(pair) => {
            let private = pair.private();
            for number in [private.d(), private.p(), private.q(), private.iqmp()] {
                put(&mut blob, number.as_bytes());
            }
        }
        KeypairData::Ecdsa(pair) => put(
            &mut blob,
            Mpint::from_positive_bytes(pair.private_key_bytes()).as_bytes(),
        ),
        _ => return None,
    }

    Some(blob)
}

// Ein Stück im Format des SSH-Protokolls: Länge (32 Bit, höchstes Byte vorn), dann die Bytes.
fn put(buffer: &mut Vec<u8>, bytes: &[u8]) {
    buffer.extend_from_slice(&u32::try_from(bytes.len()).unwrap_or(u32::MAX).to_be_bytes());
    buffer.extend_from_slice(bytes);
}

// `Name: Zahl der Zeilen`, dann Base64 mit 64 Zeichen je Zeile.
fn lines(text: &mut String, name: &str, bytes: &[u8]) {
    let mut encoded = base64(bytes);
    text.push_str(&format!("{name}: {}\n", encoded.len().div_ceil(64)));
    for line in encoded.as_bytes().chunks(64) {
        text.push_str(std::str::from_utf8(line).unwrap_or_default());
        text.push('\n');
    }
    encoded.zeroize();
}

fn base64(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut text = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let group = (u32::from(chunk[0]) << 16)
            | (u32::from(chunk.get(1).copied().unwrap_or(0)) << 8)
            | u32::from(chunk.get(2).copied().unwrap_or(0));
        for (index, shift) in [18, 12, 6, 0].into_iter().enumerate() {
            text.push(if index <= chunk.len() {
                char::from(ALPHABET[(group >> shift) as usize & 63])
            } else {
                '='
            });
        }
    }

    text
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn unbase64(text: &str) -> Option<Vec<u8>> {
    let text = text.trim_end_matches('=');
    let mut bytes = Vec::with_capacity(text.len() * 3 / 4);
    let (mut group, mut bits) = (0u32, 0u32);
    for character in text.bytes() {
        let value = match character {
            b'A'..=b'Z' => character - b'A',
            b'a'..=b'z' => character - b'a' + 26,
            b'0'..=b'9' => character - b'0' + 52,
            b'+' => 62,
            b'/' => 63,
            _ => return None,
        };
        group = (group << 6) | u32::from(value);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            bytes.push((group >> bits) as u8);
            group &= (1 << bits) - 1;
        }
    }

    Some(bytes)
}

fn unhex(text: &str) -> Option<Vec<u8>> {
    if !text.len().is_multiple_of(2) || text.len() > 256 || !text.is_ascii() {
        return None;
    }

    (0..text.len())
        .step_by(2)
        .map(|at| u8::from_str_radix(&text[at..at + 2], 16).ok())
        .collect()
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use russh::keys::ssh_key::{Algorithm, EcdsaCurve};

    use super::*;

    fn random(algorithm: Algorithm, comment: &str) -> PrivateKey {
        let mut key = PrivateKey::random(&mut rand::rng(), algorithm).unwrap();
        key.set_comment(comment);
        key
    }

    fn same(left: &PrivateKey, right: &PrivateKey) -> bool {
        left.key_data() == right.key_data()
    }

    #[test]
    fn what_is_written_is_read_back_as_the_same_key() {
        for algorithm in [
            Algorithm::Ed25519,
            Algorithm::Ecdsa {
                curve: EcdsaCurve::NistP256,
            },
            Algorithm::Ecdsa {
                curve: EcdsaCurve::NistP384,
            },
            Algorithm::Ecdsa {
                curve: EcdsaCurve::NistP521,
            },
        ] {
            let key = random(algorithm.clone(), "me@pc");

            let plain = write(&key, None).unwrap();
            let read = PrivateKey::from_ppk(&plain, None).unwrap();
            assert!(same(&read, &key), "{algorithm:?}");
            assert_eq!(read.comment().to_string(), "me@pc");

            let head = header(&plain).unwrap();
            assert!(!head.encrypted);
            assert_eq!(head.comment, "me@pc");
            assert_eq!(head.public.key_data(), key.public_key().key_data());
        }
    }

    /// Der private Teil eines Ed25519-Schlüssels ist bei PuTTY keine Zahl, sondern 32 Bytes: Auch ein Schlüssel,
    /// der mit einer Null oder einem gesetzten höchsten Bit beginnt, muss sich so schreiben und wieder lesen lassen.
    #[test]
    fn an_ed25519_key_keeps_its_32_bytes_whatever_they_start_with() {
        use russh::keys::ssh_key::private::Ed25519Keypair;

        for first in [0x00, 0x01, 0x7f, 0x80, 0xff] {
            let mut seed = [0x5a; 32];
            seed[0] = first;
            seed[1] = if first == 0 { 0x00 } else { 0x90 };
            let key = PrivateKey::new(KeypairData::Ed25519(Ed25519Keypair::from_seed(&seed)), "edge").unwrap();

            let text = write(&key, None).unwrap();

            assert!(text.contains("Private-Lines: 1\nAAAAI"), "{first:#x}: {text}");
            assert!(same(&read_ed25519(&text, None).unwrap(), &key), "{first:#x}");

            let secret = write(&key, Some("geheim")).unwrap();
            assert!(
                same(&read_ed25519(&secret, Some("geheim")).unwrap(), &key),
                "{first:#x}"
            );
            assert!(read_ed25519(&secret, Some("falsch")).is_none());
            assert!(read_ed25519(&secret, None).is_none());
        }

        // Genau diesen Fall liest die Bibliothek nicht – deshalb der eigene Leser.
        let mut seed = [0x5a; 32];
        seed[..2].fill(0);
        let key = PrivateKey::new(KeypairData::Ed25519(Ed25519Keypair::from_seed(&seed)), "").unwrap();
        assert!(PrivateKey::from_ppk(write(&key, None).unwrap(), None).is_err());
    }

    #[test]
    fn the_own_reader_refuses_what_was_changed_or_asks_for_too_much() {
        let key = random(Algorithm::Ed25519, "me@pc");
        let text = write(&key, None).unwrap();

        // Ein geänderter Kommentar: Der Prüfwert gilt allen Teilen.
        assert!(read_ed25519(&text.replace("me@pc", "you@pc"), None).is_none());
        // Kein Ed25519-Schlüssel: nicht Sache dieses Lesers.
        let other = random(
            Algorithm::Ecdsa {
                curve: EcdsaCurve::NistP256,
            },
            "",
        );
        assert!(read_ed25519(&write(&other, None).unwrap(), None).is_none());

        // Zahlen für Argon2 stehen in der Datei – über jedes Maß wird nicht gerechnet.
        let secret = write(&key, Some("x")).unwrap();
        let greedy = secret.replace("Argon2-Memory: 8192", "Argon2-Memory: 4000000000");
        let started = std::time::Instant::now();
        assert!(read_ed25519(&greedy, Some("x")).is_none());
        assert!(started.elapsed() < std::time::Duration::from_secs(2));
    }

    #[test]
    fn a_passphrase_encrypts_the_private_part_and_only_it_opens_the_file() {
        let key = random(Algorithm::Ed25519, "secret one");

        let text = write(&key, Some("correct horse")).unwrap();

        assert!(text.contains("Encryption: aes256-cbc\n"));
        assert!(text.contains("Key-Derivation: Argon2id\n"));
        assert!(same(
            &PrivateKey::from_ppk(&text, Some("correct horse".to_owned())).unwrap(),
            &key
        ));
        assert!(PrivateKey::from_ppk(&text, Some("wrong".to_owned())).is_err());
        assert!(PrivateKey::from_ppk(&text, None).is_err());

        // Der Kopf verrät Art und öffentlichen Teil auch so – und dass eine Passphrase nötig ist.
        let head = header(&text).unwrap();
        assert!(head.encrypted);
        assert_eq!(head.public.key_data(), key.public_key().key_data());

        // Eine leere Passphrase ist keine.
        assert!(write(&key, Some("")).unwrap().contains("Encryption: none\n"));
    }

    #[test]
    fn a_key_that_is_still_locked_is_not_written() {
        let key = random(Algorithm::Ed25519, "").encrypt(&mut rand::rng(), "x").unwrap();
        assert!(write(&key, None).is_none());
    }

    #[test]
    fn text_that_is_not_a_key_file_has_no_header() {
        for text in [
            "",
            "PuTTY-User-Key-File-3",
            "PuTTY-User-Key-File-3: ssh-ed25519\nPublic-Lines: 99999999\n",
            "PuTTY-User-Key-File-3: ssh-ed25519\nPublic-Lines: 2\nAAAA\n",
            "PuTTY-User-Key-File-3: ssh-ed25519\nPublic-Lines: 1\nnot base64 at all\n",
            "-----BEGIN OPENSSH PRIVATE KEY-----\n",
        ] {
            assert!(header(text).is_none(), "{text:?}");
        }
    }

    #[test]
    fn base64_pads_like_everyone_else() {
        for (bytes, text) in [
            ("", ""),
            ("f", "Zg=="),
            ("fo", "Zm8="),
            ("foo", "Zm9v"),
            ("foob", "Zm9vYg=="),
        ] {
            assert_eq!(base64(bytes.as_bytes()), text);
        }
    }

    /// Gegen PuTTY selbst: Der Test läuft nur, wenn `NEXUS_PUTTY_DIR` einen Ordner nennt, in dem für `ed25519`,
    /// `rsa`, `ecdsa` und `ecdsa521` je ein Schlüssel von `ssh-keygen` und daneben `<name>.ppk` von `puttygen`
    /// liegen, dazu `<name>-secret.ppk` mit der Passphrase „geheim“ – ein privater Schlüssel hat im Repository
    /// nichts verloren. Ohne Passphrase ist die Datei eindeutig: Sie muss Byte für Byte dieselbe sein.
    #[test]
    fn files_are_the_ones_puttygen_writes() {
        let Some(directory) = std::env::var_os("NEXUS_PUTTY_DIR").map(PathBuf::from) else {
            return;
        };

        for name in ["ed25519", "rsa", "ecdsa", "ecdsa521"] {
            let key = russh::keys::load_secret_key(directory.join(name), None).unwrap();
            let theirs = std::fs::read_to_string(directory.join(format!("{name}.ppk"))).unwrap();

            assert_eq!(write(&key, None).unwrap(), theirs.replace("\r\n", "\n"), "{name}");
            assert!(same(&PrivateKey::from_ppk(&theirs, None).unwrap(), &key), "{name}");

            let secret = directory.join(format!("{name}-secret.ppk"));
            // Der umgekehrte Weg braucht PuTTY: Nach `NEXUS_PUTTY_OUT` kommt je Schlüssel eine Datei mit der
            // Passphrase „geheim“, die `puttygen` dort lesen muss (`<name>-ours.ppk`).
            if let Some(out) = std::env::var_os("NEXUS_PUTTY_OUT").map(PathBuf::from) {
                std::fs::write(
                    out.join(format!("{name}-ours.ppk")),
                    write(&key, Some("geheim")).unwrap(),
                )
                .unwrap();
            }

            // Die alte Fassung 2 (PuTTY vor 0.75) schreibt hier niemand; gelesen wird sie, auch vom eigenen Leser.
            for old in [format!("{name}-v2.ppk"), format!("{name}-v2-secret.ppk")] {
                let Ok(theirs) = std::fs::read_to_string(directory.join(&old)) else {
                    continue;
                };
                let passphrase = old.contains("secret").then_some("geheim");
                assert!(theirs.starts_with("PuTTY-User-Key-File-2: "), "{old}");
                assert!(
                    same(
                        &PrivateKey::from_ppk(&theirs, passphrase.map(str::to_owned)).unwrap(),
                        &key
                    ),
                    "{old}"
                );
                if name == "ed25519" {
                    assert!(same(&read_ed25519(&theirs, passphrase).unwrap(), &key), "{old}");
                    assert!(read_ed25519(&theirs.replace("test ed25519", "changed"), passphrase).is_none());
                }
            }

            if secret.exists() {
                let theirs = std::fs::read_to_string(secret).unwrap();
                if name == "ed25519" {
                    assert!(same(&read_ed25519(&theirs, Some("geheim")).unwrap(), &key));
                }
                assert!(
                    same(&PrivateKey::from_ppk(&theirs, Some("geheim".to_owned())).unwrap(), &key),
                    "{name}"
                );
                assert!(header(&theirs).unwrap().encrypted);
            }
        }
    }
}
