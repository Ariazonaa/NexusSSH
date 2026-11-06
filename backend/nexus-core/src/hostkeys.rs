//! Prüfung des Host-Schlüssels gegen bekannte Server – der Schutz vor einem Angreifer in der Leitung.
//!
//! Zwei Dateien im OpenSSH-Format:
//! - die eigene `known_hosts` im Datenordner der App. Nur sie wird geschrieben.
//! - `~/.ssh/known_hosts` des Benutzers. Sie wird nur gelesen: Wer einem Server per `ssh` schon vertraut, wird
//!   nicht noch einmal gefragt, aber NexusSSH ändert die Datei von OpenSSH nie.
//!
//! Die eigene Datei hat Vorrang. Vertraut der Benutzer hier einem geänderten Schlüssel, gilt das, auch wenn in
//! `~/.ssh/known_hosts` noch der alte steht.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use russh::keys::known_hosts::known_host_keys_path;
use russh::keys::ssh_key::certificate::CertType;
use russh::keys::{Certificate, HashAlg, PublicKey};

use crate::profiles::write_atomically;

/// Ergebnis der Prüfung.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Verdict {
    /// Der Schlüssel ist für diesen Server hinterlegt.
    Trusted,
    /// Der Server ist unbekannt.
    Unknown,
    /// Für den Server ist ein **anderer** Schlüssel desselben Typs hinterlegt.
    Changed {
        /// Datei mit dem alten Eintrag.
        file: PathBuf,
        /// Zeile des alten Eintrags (ab 1).
        line: usize,
        /// Fingerabdruck des Schlüssels, der dort steht – zum Vergleich mit dem neuen.
        previous: String,
        /// Der Eintrag steht in der Datei der App. Sonst steht er in der Datei des Benutzers (`~/.ssh/known_hosts`),
        /// die die App nie ändert: Er bleibt dort stehen, auch wenn der Benutzer dem neuen Schlüssel vertraut.
        own: bool,
    },
    /// Der Schlüssel ist für diesen Server widerrufen (`@revoked` in `known_hosts`): Mit ihm wird nicht verbunden,
    /// auch nicht auf Nachfrage.
    Revoked,
}

/// Die beiden Dateien.
#[derive(Clone, Debug)]
pub struct HostKeys {
    own: PathBuf,
    openssh: Option<PathBuf>,
}

impl HostKeys {
    /// `own` ist die Datei der App, `openssh` die des Benutzers (falls sie berücksichtigt werden soll).
    pub fn new(own: PathBuf, openssh: Option<PathBuf>) -> Self {
        Self { own, openssh }
    }

    /// Prüft den Schlüssel, den der Server vorgelegt hat.
    pub fn check(&self, host: &str, port: u16, key: &PublicKey) -> Verdict {
        // Ein widerrufener Schlüssel gilt nicht, gleich was sonst über den Server dasteht.
        if std::iter::once(&self.own)
            .chain(self.openssh.as_ref())
            .any(|path| is_revoked(path, host, port, key))
        {
            return Verdict::Revoked;
        }

        match check_file(&self.own, host, port, key, true) {
            Verdict::Unknown => {}
            verdict => return verdict,
        }

        match &self.openssh {
            Some(path) => check_file(path, host, port, key, false),
            None => Verdict::Unknown,
        }
    }

    /// Die Arten von Schlüsseln, die für diesen Server hinterlegt sind (`ssh-ed25519`, `rsa-sha2-512` …). Um sie
    /// bittet der Kern beim Verbinden zuerst: Nur für einen Schlüssel derselben Art lässt sich sagen, ob er sich
    /// geändert hat – böte der Server stattdessen eine andere an, sähe ein ausgetauschter Server aus wie ein
    /// unbekannter.
    pub fn known_algorithms(&self, host: &str, port: u16) -> Vec<russh::keys::Algorithm> {
        let mut known = Vec::new();
        for path in std::iter::once(&self.own).chain(self.openssh.as_ref()) {
            for (_, key) in recorded(path, host, port) {
                if !known.contains(&key.algorithm()) {
                    known.push(key.algorithm());
                }
            }
        }

        known
    }

    /// Vertraut dem Schlüssel dauerhaft: entfernt ältere Einträge für diesen Server aus der eigenen Datei und
    /// trägt den neuen ein.
    pub fn trust(&self, host: &str, port: u16, key: &PublicKey) -> io::Result<()> {
        let text = match fs::read_to_string(&self.own) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => String::new(),
            Err(error) => return Err(error),
        };

        // Die eigene Datei enthält nur Zeilen, die hier geschrieben wurden: „host schlüssel“ bzw.
        // „[host]:port schlüssel“, nie gehashte Namen oder Listen. Deshalb genügt der Vergleich des ersten Worts.
        let name = entry_name(host, port);
        let mut rewritten = String::new();
        for line in text.lines() {
            if !line.trim().is_empty() && line.split(' ').next() != Some(name.as_str()) {
                rewritten.push_str(line);
                rewritten.push('\n');
            }
        }

        rewritten.push_str(&format!("{name} {}\n", key.to_openssh().map_err(io::Error::other)?));
        write_atomically(&self.own, &rewritten)
    }

    /// Die Zertifizierungsstellen, die für diesen Server gelten: Zeilen `@cert-authority muster schlüssel` in der
    /// eigenen Datei oder in der des Benutzers. Ohne eine solche Zeile ändert sich nichts – dann bittet der Kern
    /// den Server gar nicht erst um ein Zertifikat.
    pub fn authorities(&self, host: &str, port: u16) -> Vec<PublicKey> {
        let mut found = Vec::new();
        for path in std::iter::once(&self.own).chain(self.openssh.iter()) {
            let Ok(text) = fs::read_to_string(path) else {
                continue;
            };

            for line in text.lines() {
                let mut fields = line.split_whitespace();
                if fields.next() != Some("@cert-authority") {
                    continue;
                }

                let (Some(patterns), Some(kind), Some(data)) = (fields.next(), fields.next(), fields.next()) else {
                    continue;
                };
                if !authority_applies(patterns, host, port) {
                    continue;
                }

                if let Ok(key) = PublicKey::from_openssh(&format!("{kind} {data}"))
                    && !found.contains(&key)
                {
                    found.push(key);
                }
            }
        }

        // Eine widerrufene Stelle beglaubigt nichts mehr (`@revoked` mit ihrem Schlüssel) – gleich in welcher der
        // beiden Dateien der Widerruf steht (Hinweis von Codex).
        found.retain(|key| {
            !std::iter::once(&self.own)
                .chain(self.openssh.as_ref())
                .any(|path| is_revoked(path, host, port, key))
        });

        found
    }

    /// Prüft ein Host-Zertifikat: von einer Stelle unterschrieben, die für diesen Server gilt, ein Zertifikat für
    /// Server (nicht für Benutzer), jetzt gültig und für diesen Namen ausgestellt. Der Fehler sagt, woran es lag.
    pub fn check_certificate(&self, host: &str, port: u16, certificate: &Certificate, now: u64) -> Result<(), String> {
        let authorities = self.authorities(host, port);
        if authorities.is_empty() {
            return Err("no certificate authority is known for this server".to_owned());
        }

        if certificate.cert_type() != CertType::Host {
            return Err("the certificate is not a host certificate".to_owned());
        }

        // Ohne Namen gilt ein Zertifikat für jeden Server (so hält es auch OpenSSH); mit Namen muss dieser dabei sein.
        let principals = certificate.valid_principals();
        if !principals.is_empty() && !principals.iter().any(|name| name.eq_ignore_ascii_case(host)) {
            return Err(format!("the certificate is not issued for {host}"));
        }

        let fingerprints: Vec<_> = authorities.iter().map(|key| key.fingerprint(HashAlg::Sha256)).collect();
        certificate
            .validate_at(now, fingerprints.iter())
            .map_err(|error| format!("the certificate does not hold: {error}"))
    }

    /// Trägt eine Zertifizierungsstelle in die eigene Datei ein: Ein Server, auf den `patterns` passt und der ein
    /// Zertifikat dieser Stelle vorzeigt, gilt danach ohne Rückfrage als vertraut. Das ist mehr Vertrauen als ein
    /// einzelner Host-Schlüssel – deshalb nur auf ausdrücklichen Wunsch des Benutzers, nie aus einer fremden Datei.
    /// `key` ist eine Zeile mit einem öffentlichen Schlüssel (`art daten [kommentar]`); ein Kommentar fällt weg.
    pub fn trust_authority(&self, patterns: &str, key: &str) -> Result<KnownHost, AuthorityError> {
        let patterns = authority_patterns(patterns).ok_or(AuthorityError::Patterns)?;
        let key = authority_key(key).ok_or(AuthorityError::Key)?;
        let line = format!(
            "@cert-authority {patterns} {}",
            key.to_openssh().map_err(|_| AuthorityError::Key)?
        );

        let text = match fs::read_to_string(&self.own) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => String::new(),
            Err(error) => return Err(AuthorityError::File(error)),
        };

        // Dieselbe Stelle für dieselben Muster steht nur einmal da.
        if !text.lines().any(|existing| existing.trim() == line) {
            let mut rewritten = text;
            if !rewritten.is_empty() && !rewritten.ends_with('\n') {
                rewritten.push('\n');
            }

            rewritten.push_str(&line);
            rewritten.push('\n');
            write_atomically(&self.own, &rewritten).map_err(AuthorityError::File)?;
        }

        Ok(KnownHost {
            name: patterns,
            algorithm: key.algorithm().to_string(),
            fingerprint: fingerprint(&key),
            authority: true,
        })
    }

    /// Nimmt eine Zertifizierungsstelle wieder aus der eigenen Datei: die Zeile mit genau diesen Mustern und dem
    /// Schlüssel mit diesem Fingerabdruck. `false`, wenn es sie nicht gab.
    pub fn forget_authority(&self, patterns: &str, print: &str) -> io::Result<bool> {
        let text = match fs::read_to_string(&self.own) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
            Err(error) => return Err(error),
        };

        let mut rewritten = String::new();
        let mut found = false;
        for line in text.lines() {
            if authority_line(line).is_some_and(|(named, key)| named == patterns && fingerprint(&key) == print) {
                found = true;
            } else if !line.trim().is_empty() {
                rewritten.push_str(line);
                rewritten.push('\n');
            }
        }

        if found {
            write_atomically(&self.own, &rewritten)?;
        }

        Ok(found)
    }

    /// Was in der eigenen Datei steht – zum Ansehen: die Server, denen vertraut wird, und die
    /// Zertifizierungsstellen (`authority`). Die Datei des Benutzers (`~/.ssh/known_hosts`) gehört nicht dazu: Sie
    /// wird nur gelesen, und hier ließe sich nichts daraus entfernen. Eine Zeile, die sich nicht lesen lässt, fehlt
    /// in der Liste.
    pub fn list(&self) -> Vec<KnownHost> {
        let Ok(text) = fs::read_to_string(&self.own) else {
            return Vec::new();
        };

        text.lines()
            .filter_map(|line| {
                if let Some((patterns, key)) = authority_line(line) {
                    return Some(KnownHost {
                        name: patterns.to_owned(),
                        algorithm: key.algorithm().to_string(),
                        fingerprint: fingerprint(&key),
                        authority: true,
                    });
                }

                let (name, key) = line.trim().split_once(' ')?;
                let key = PublicKey::from_openssh(key).ok()?;
                Some(KnownHost {
                    name: name.to_owned(),
                    algorithm: key.algorithm().to_string(),
                    fingerprint: fingerprint(&key),
                    authority: false,
                })
            })
            .collect()
    }

    /// Vergisst einen Server aus der eigenen Datei: Beim nächsten Verbinden wird wieder nach seinem Schlüssel
    /// gefragt. `name` ist der Eintrag, wie [`list`](Self::list) ihn nennt. `false`, wenn es ihn nicht gab.
    pub fn forget(&self, name: &str) -> io::Result<bool> {
        // Zeilen mit Vermerk sind keine Server: Für eine Zertifizierungsstelle gibt es `forget_authority`.
        if name.starts_with('@') {
            return Ok(false);
        }

        let text = match fs::read_to_string(&self.own) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
            Err(error) => return Err(error),
        };

        let mut rewritten = String::new();
        let mut found = false;
        for line in text.lines() {
            if line.split(' ').next() == Some(name) {
                found = true;
            } else if !line.trim().is_empty() {
                rewritten.push_str(line);
                rewritten.push('\n');
            }
        }

        if found {
            write_atomically(&self.own, &rewritten)?;
        }

        Ok(found)
    }
}

/// Ein Server, dem in der eigenen `known_hosts` vertraut wird.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct KnownHost {
    /// Der Eintrag, wie er in der Datei steht: `host` oder `[host]:port`.
    pub name: String,
    /// Art des Schlüssels, z. B. `ssh-ed25519`.
    pub algorithm: String,
    /// Fingerabdruck in der Schreibweise von OpenSSH.
    pub fingerprint: String,
    /// Eine Zertifizierungsstelle (`@cert-authority`): `name` sind dann die Muster der Server, für die sie gilt.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub authority: bool,
}

/// Warum sich eine Zertifizierungsstelle nicht eintragen ließ.
#[derive(Debug)]
pub enum AuthorityError {
    /// Die Muster sind leer oder enthalten etwas, das in einer Zeile der Datei nichts zu suchen hat.
    Patterns,
    /// Das ist kein öffentlicher Schlüssel.
    Key,
    /// Die Datei ließ sich nicht lesen oder schreiben.
    File(io::Error),
}

/// So lang darf die Liste der Muster einer Zertifizierungsstelle sein.
pub const MAX_AUTHORITY_PATTERNS: usize = 1024;

/// Die Muster einer Zertifizierungsstelle, wie sie in die Datei kommen: durch Komma getrennt, jedes aus den
/// Zeichen eines Hostnamens samt `*`, `?`, `[host]:port` und `!` davor – und mindestens eines, das nicht verneint.
/// Alles andere (Leerraum, Steuerzeichen, `#`) ergäbe eine andere Zeile, als der Benutzer gesehen hat.
pub fn authority_patterns(patterns: &str) -> Option<String> {
    let patterns = patterns.trim();
    if patterns.is_empty() || patterns.len() > MAX_AUTHORITY_PATTERNS {
        return None;
    }

    let plain = |pattern: &str| {
        !pattern.is_empty()
            && pattern
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | '*' | '?' | ':' | '[' | ']'))
    };
    let mut positive = false;
    for pattern in patterns.split(',') {
        match pattern.strip_prefix('!') {
            Some(negated) if plain(negated) => {}
            None if plain(pattern) => positive = true,
            _ => return None,
        }
    }

    positive.then(|| patterns.to_owned())
}

/// Der öffentliche Schlüssel aus einer Zeile `art daten [kommentar]` – ohne den Kommentar. Ein Zertifikat ist
/// kein Schlüssel einer Stelle.
fn authority_key(line: &str) -> Option<PublicKey> {
    let mut fields = line.split_whitespace();
    let (kind, data) = (fields.next()?, fields.next()?);
    if kind.contains("-cert-") || data.len() > 16 * 1024 {
        return None;
    }

    PublicKey::from_openssh(&format!("{kind} {data}")).ok()
}

/// Liest eine Zeile `@cert-authority muster art daten` der eigenen Datei.
fn authority_line(line: &str) -> Option<(&str, PublicKey)> {
    let mut fields = line.split_whitespace();
    if fields.next() != Some("@cert-authority") {
        return None;
    }

    let (patterns, kind, data) = (fields.next()?, fields.next()?, fields.next()?);
    Some((patterns, PublicKey::from_openssh(&format!("{kind} {data}")).ok()?))
}

/// Fingerabdruck in der Schreibweise von OpenSSH, z. B. `SHA256:abc…`.
pub fn fingerprint(key: &PublicKey) -> String {
    key.fingerprint(HashAlg::Sha256).to_string()
}

fn entry_name(host: &str, port: u16) -> String {
    if port == 22 {
        host.to_owned()
    } else {
        format!("[{host}]:{port}")
    }
}

/// Ob eine Liste von Mustern aus `known_hosts` (`*.example.org,!alt.example.org`, `[host]:2222`) auf den Server
/// passt. Bei einem anderen Port als 22 zählt `[host]:port` – und, wie bei OpenSSH, auch der bloße Name.
fn authority_applies(patterns: &str, host: &str, port: u16) -> bool {
    let bracketed = format!("[{host}]:{port}");
    let names: Vec<&str> = if port == 22 {
        vec![host]
    } else {
        vec![bracketed.as_str(), host]
    };
    let matches = |pattern: &str| names.iter().any(|name| glob(pattern.as_bytes(), name.as_bytes()));

    let mut applies = false;
    for pattern in patterns.split(',').filter(|pattern| !pattern.is_empty()) {
        match pattern.strip_prefix('!') {
            // Eine Verneinung, die passt, schließt den Server aus – was sonst noch in der Liste steht, zählt nicht.
            Some(negated) if matches(negated) => return false,
            Some(_) => {}
            None => applies |= matches(pattern),
        }
    }

    applies
}

/// `*` für beliebig viele Zeichen, `?` für eines; Groß- und Kleinschreibung zählt bei Hostnamen nicht.
fn glob(pattern: &[u8], name: &[u8]) -> bool {
    match pattern.split_first() {
        None => name.is_empty(),
        Some((b'*', rest)) => (0..=name.len()).any(|skip| glob(rest, &name[skip..])),
        Some((wanted, rest)) => name
            .split_first()
            .is_some_and(|(found, tail)| (*wanted == b'?' || wanted.eq_ignore_ascii_case(found)) && glob(rest, tail)),
    }
}

/// Ob der Schlüssel in dieser Datei für den Server widerrufen ist: eine Zeile `@revoked <Muster> <Art> <Schlüssel>`,
/// deren Muster auf den Server passen (auch `*`) und deren Schlüssel der vorgelegte ist.
fn is_revoked(path: &Path, host: &str, port: u16, key: &PublicKey) -> bool {
    let Ok(text) = fs::read_to_string(path) else {
        return false;
    };

    text.lines().any(|line| {
        let mut fields = line.split_whitespace();
        if fields.next() != Some("@revoked") {
            return false;
        }

        let (Some(patterns), Some(kind), Some(data)) = (fields.next(), fields.next(), fields.next()) else {
            return false;
        };
        authority_applies(patterns, host, port)
            && PublicKey::from_openssh(&format!("{kind} {data}"))
                .is_ok_and(|revoked| revoked.key_data() == key.key_data())
    })
}

/// Die Schlüssel, die in dieser Datei für den Server stehen, mit ihrer Zeile.
///
/// Die Bibliothek übergeht eine Zeile, die sie nicht lesen kann (zwei Leerzeichen oder ein Tabulator zwischen den
/// Feldern), oder gibt gleich die ganze Datei auf (eine Art von Schlüssel, die sie nicht kennt) – der Server gälte
/// dann als unbekannt, und ein ausgetauschter Schlüssel sähe aus wie ein erster Kontakt. Steht so eine Zeile in
/// der Datei, liest der Kern deshalb eine bereinigte Fassung: jede Zeile an ihrer Stelle (die Zeilennummern stimmen so weiter), mit einfachen
/// Leerzeichen, und leer, wenn sie kein Eintrag ist, den der Kern lesen kann.
fn recorded(path: &Path, host: &str, port: u16) -> Vec<(usize, PublicKey)> {
    let Ok(text) = fs::read_to_string(path) else {
        return Vec::new();
    };

    // Jede Zeile, wie die Bibliothek sie braucht – und ob dafür etwas zu ändern war. Kommentare, Leerzeilen und
    // Zeilen mit Vermerk (`@cert-authority`, `@revoked`) sind keine Einträge; sie zählen dabei nicht.
    let mut differs = false;
    let cleaned: String = text
        .lines()
        .map(|line| {
            let fields: Vec<&str> = line.split_whitespace().collect();
            match fields.as_slice() {
                [] => "\n".to_owned(),
                [first, ..] if first.starts_with(['#', '@']) => "\n".to_owned(),
                [hosts, kind, data, ..] if PublicKey::from_openssh(&format!("{kind} {data}")).is_ok() => {
                    let entry = format!("{hosts} {kind} {data}");
                    differs |= !line.starts_with(&entry);
                    entry + "\n"
                }
                _ => {
                    differs = true;
                    "\n".to_owned()
                }
            }
        })
        .collect();

    // Der gewöhnliche Fall: Die Datei ist, wie die Bibliothek sie liest.
    if !differs {
        return known_host_keys_path(host, port, path).unwrap_or_default();
    }
    // Die Bibliothek liest nur Dateien: eine Kopie neben der Datei der App, die gleich wieder verschwindet.
    static NUMBER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let number = NUMBER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let scratch = std::env::temp_dir().join(format!("nexusssh-known-hosts-{}-{number}", std::process::id()));
    if fs::write(&scratch, cleaned).is_err() {
        return Vec::new();
    }

    let known = known_host_keys_path(host, port, &scratch).unwrap_or_default();
    let _ = fs::remove_file(&scratch);
    known
}

fn check_file(path: &Path, host: &str, port: u16, key: &PublicKey, own: bool) -> Verdict {
    // Eine fehlende Datei ist leer. Was sich auch bereinigt nicht lesen lässt, gibt die Verbindung nicht frei –
    // dann gilt der Server als unbekannt und der Benutzer wird gefragt.
    let known = recorded(path, host, port);

    if known.iter().any(|(_, recorded)| recorded.key_data() == key.key_data()) {
        return Verdict::Trusted;
    }

    match known
        .iter()
        .find(|(_, recorded)| recorded.algorithm() == key.algorithm())
    {
        Some((line, recorded)) => Verdict::Changed {
            file: path.to_owned(),
            line: *line,
            previous: fingerprint(recorded),
            own,
        },
        None => Verdict::Unknown,
    }
}

#[cfg(test)]
mod tests {
    use russh::keys::PrivateKey;
    use russh::keys::known_hosts::learn_known_hosts_path;
    use russh::keys::ssh_key::private::Ed25519Keypair;

    use super::*;

    fn key(seed: u8) -> PublicKey {
        PrivateKey::from(Ed25519Keypair::from_seed(&[seed; 32]))
            .public_key()
            .clone()
    }

    fn files(name: &str) -> (HostKeys, PathBuf, PathBuf) {
        let directory = std::env::temp_dir().join(format!(
            "nexus-core-hostkeys-{}-{name}-{}",
            std::process::id(),
            crate::profiles::unix_now()
        ));
        fs::create_dir_all(&directory).unwrap();
        let own = directory.join("known_hosts");
        let openssh = directory.join("openssh_known_hosts");
        (HostKeys::new(own.clone(), Some(openssh.clone())), own, openssh)
    }

    fn line(host: &str, key: &PublicKey) -> String {
        format!("{host} {}", key.to_openssh().unwrap())
    }

    #[test]
    fn one_line_the_library_cannot_read_does_not_hide_the_others() {
        // Zwei Leerzeichen, ein Tabulator, eine unbekannte Art von Schlüssel, Unsinn: Früher galt der Server dann
        // als unbekannt – und ein ausgetauschter Schlüssel sah aus wie ein erster Kontakt.
        let (hosts, _, openssh) = files("unreadable-line");
        let text = format!(
            "# Kommentar\nother.org  {}\nbroken line without a key\nweird.org ssh-future AAAAB3NzaC1yc2E=\n{}\nexample.org\t{}\n",
            key(9).to_openssh().unwrap(),
            line("another.org", &key(8)),
            key(1).to_openssh().unwrap()
        );
        fs::write(&openssh, text).unwrap();

        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Trusted);
        assert_eq!(hosts.check("other.org", 22, &key(9)), Verdict::Trusted);
        assert_eq!(
            hosts.check("example.org", 22, &key(2)),
            Verdict::Changed {
                file: openssh,
                // Die Zeile in der Datei des Benutzers, nicht in der bereinigten Fassung.
                line: 6,
                previous: fingerprint(&key(1)),
                own: false,
            }
        );
        assert_eq!(hosts.check("unknown.org", 22, &key(1)), Verdict::Unknown);
    }

    #[test]
    fn a_revoked_key_is_never_accepted() {
        let (hosts, own, openssh) = files("revoked");
        fs::write(
            &openssh,
            format!(
                "@revoked * {}\n@revoked [gate.example.org]:2222 {}\n",
                key(7).to_openssh().unwrap(),
                key(6).to_openssh().unwrap()
            ),
        )
        .unwrap();

        // Für jeden Server widerrufen – auch wenn derselbe Schlüssel als vertraut eingetragen ist.
        assert_eq!(hosts.check("example.org", 22, &key(7)), Verdict::Revoked);
        fs::write(&own, format!("{}\n", line("example.org", &key(7)))).unwrap();
        assert_eq!(hosts.check("example.org", 22, &key(7)), Verdict::Revoked);

        // Nur für den genannten Server und nur dieser Schlüssel.
        assert_eq!(hosts.check("gate.example.org", 2222, &key(6)), Verdict::Revoked);
        assert_eq!(hosts.check("elsewhere.org", 22, &key(6)), Verdict::Unknown);
        assert_eq!(hosts.check("gate.example.org", 2222, &key(5)), Verdict::Unknown);
    }

    #[test]
    fn the_kinds_of_key_known_for_a_server_are_named() {
        let (hosts, _, openssh) = files("algorithms");
        assert!(hosts.known_algorithms("example.org", 22).is_empty());

        hosts.trust("example.org", 22, &key(1)).unwrap();
        fs::write(&openssh, format!("{}\n", line("other.org", &key(2)))).unwrap();
        assert_eq!(hosts.known_algorithms("example.org", 22), [key(1).algorithm()]);
        assert_eq!(hosts.known_algorithms("other.org", 22), [key(2).algorithm()]);
        assert!(hosts.known_algorithms("third.org", 22).is_empty());
    }

    #[test]
    fn an_unseen_server_is_unknown() {
        let (hosts, _, _) = files("unknown");
        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Unknown);
    }

    #[test]
    fn a_trusted_key_is_recognised_for_its_host_and_port_only() {
        let (hosts, _, _) = files("trusted");
        hosts.trust("example.org", 2222, &key(1)).unwrap();

        assert_eq!(hosts.check("example.org", 2222, &key(1)), Verdict::Trusted);
        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Unknown);
        assert_eq!(hosts.check("other.org", 2222, &key(1)), Verdict::Unknown);
    }

    #[test]
    fn a_different_key_for_a_known_server_is_reported_as_changed() {
        let (hosts, own, _) = files("changed");
        hosts.trust("example.org", 22, &key(1)).unwrap();

        assert_eq!(
            hosts.check("example.org", 22, &key(2)),
            Verdict::Changed {
                file: own,
                line: 1,
                previous: fingerprint(&key(1)),
                own: true,
            }
        );
    }

    #[test]
    fn the_users_openssh_file_counts_but_is_never_written() {
        let (hosts, own, openssh) = files("openssh");
        learn_known_hosts_path("example.org", 22, &key(1), &openssh).unwrap();
        let before = fs::read_to_string(&openssh).unwrap();

        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Trusted);
        // Der alte Eintrag steht in der Datei des Benutzers – das sagt das Urteil, samt dem Fingerabdruck dort.
        // (Zeile 2: russh beginnt eine neue Datei mit einer Leerzeile.)
        assert_eq!(
            hosts.check("example.org", 22, &key(2)),
            Verdict::Changed {
                file: openssh.clone(),
                line: 2,
                previous: fingerprint(&key(1)),
                own: false,
            }
        );

        hosts.trust("example.org", 22, &key(2)).unwrap();
        assert_eq!(fs::read_to_string(&openssh).unwrap(), before);
        assert!(own.exists());
    }

    #[test]
    fn trusted_servers_can_be_listed_and_forgotten_one_by_one() {
        let (hosts, own, openssh) = files("list");
        assert!(hosts.list().is_empty());
        assert!(!hosts.forget("example.org").unwrap());

        learn_known_hosts_path("only-openssh.org", 22, &key(9), &openssh).unwrap();
        hosts.trust("example.org", 22, &key(1)).unwrap();
        hosts.trust("example.org", 2222, &key(2)).unwrap();

        // Nur die eigene Datei – die des Benutzers wird gelesen, aber hier weder gezeigt noch geändert.
        let listed = hosts.list();
        assert_eq!(
            listed.iter().map(|host| host.name.as_str()).collect::<Vec<_>>(),
            ["example.org", "[example.org]:2222"]
        );
        assert_eq!(listed[0].algorithm, "ssh-ed25519");
        assert_eq!(listed[0].fingerprint, fingerprint(&key(1)));

        assert!(hosts.forget("[example.org]:2222").unwrap());
        assert_eq!(hosts.check("example.org", 2222, &key(2)), Verdict::Unknown);
        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Trusted);
        assert_eq!(fs::read_to_string(&own).unwrap().lines().count(), 1);
        assert!(!hosts.forget("[example.org]:2222").unwrap());
        assert_eq!(hosts.check("only-openssh.org", 22, &key(9)), Verdict::Trusted);

        // Eine Zeile, die kein Schlüssel ist, fehlt in der Liste, statt sie zu sprengen.
        fs::write(&own, format!("{}broken line\n", fs::read_to_string(&own).unwrap())).unwrap();
        assert_eq!(hosts.list().iter().filter(|entry| !entry.authority).count(), 1);
    }

    #[test]
    fn trusting_a_changed_key_overrides_the_old_entries() {
        let (hosts, own, openssh) = files("override");
        learn_known_hosts_path("example.org", 22, &key(1), &openssh).unwrap();
        hosts.trust("example.org", 22, &key(2)).unwrap();
        hosts.trust("other.org", 22, &key(4)).unwrap();

        // Die eigene Datei hat Vorrang vor der von OpenSSH …
        assert_eq!(hosts.check("example.org", 22, &key(2)), Verdict::Trusted);

        // … und ein erneuter Wechsel ersetzt den eigenen Eintrag, statt einen zweiten anzulegen.
        hosts.trust("example.org", 22, &key(3)).unwrap();
        assert_eq!(hosts.check("example.org", 22, &key(3)), Verdict::Trusted);
        assert!(matches!(
            hosts.check("example.org", 22, &key(2)),
            Verdict::Changed { .. }
        ));
        assert_eq!(hosts.check("other.org", 22, &key(4)), Verdict::Trusted);
        assert_eq!(fs::read_to_string(own).unwrap().lines().count(), 2);
    }

    #[test]
    fn fingerprints_look_like_openssh() {
        assert!(fingerprint(&key(1)).starts_with("SHA256:"));
    }

    #[test]
    fn authority_patterns_follow_known_hosts() {
        assert!(authority_applies("*.example.org", "web.example.org", 22));
        assert!(authority_applies(
            "a.example.org,WEB.example.org",
            "web.example.org",
            22
        ));
        assert!(!authority_applies("*.example.org", "example.com", 22));
        assert!(!authority_applies(
            "*.example.org,!web.example.org",
            "web.example.org",
            22
        ));
        assert!(!authority_applies("!web.example.org", "web.example.org", 22));
        assert!(authority_applies("10.0.?.1", "10.0.7.1", 22));

        // Ein anderer Port: „[host]:port“ zählt und der bloße Name auch; ein fremder Port nicht.
        assert!(authority_applies("[web.example.org]:2222", "web.example.org", 2222));
        assert!(authority_applies("web.example.org", "web.example.org", 2222));
        assert!(!authority_applies("[web.example.org]:2200", "web.example.org", 2222));
        assert!(!authority_applies("[web.example.org]:2222", "web.example.org", 22));
        assert!(!authority_applies("", "web.example.org", 22));
    }

    #[test]
    fn authorities_are_read_from_both_files_and_other_lines_do_not_count() {
        let (hosts, own, openssh) = files("authorities");
        hosts.trust("example.org", 22, &key(1)).unwrap();
        let line = |pattern: &str, seed: u8| format!("@cert-authority {pattern} {}\n", key(seed).to_openssh().unwrap());
        fs::write(
            &openssh,
            format!(
                "# Kommentar\n{}@revoked * {}\nunlesbar\n@cert-authority * nonsense AAAA\n",
                line("*.example.org", 7),
                key(9).to_openssh().unwrap()
            ),
        )
        .unwrap();
        let mut text = fs::read_to_string(&own).unwrap();
        text.push_str(&line("example.org,*.example.org", 8));
        text.push_str(&line("*.example.org", 7));
        fs::write(&own, text).unwrap();

        assert_eq!(hosts.authorities("web.example.org", 22), [key(8), key(7)]);
        assert_eq!(hosts.authorities("example.org", 22), [key(8)]);
        assert!(hosts.authorities("example.com", 22).is_empty());

        // Die Zeile der Stelle stört die Liste der bekannten Server nicht.
        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Trusted);
        assert_eq!(hosts.list().iter().filter(|entry| !entry.authority).count(), 1);
    }

    #[test]
    fn an_authority_is_written_listed_and_removed_without_touching_the_servers() {
        let (hosts, own, _) = files("authority-own");
        hosts.trust("example.org", 22, &key(1)).unwrap();
        let line = key(7).to_openssh().unwrap();

        let added = hosts
            .trust_authority(
                " *.example.org,!old.example.org ",
                &format!("{line} kommentar mit leerzeichen"),
            )
            .unwrap();
        assert_eq!(
            (added.name.as_str(), added.authority, added.fingerprint.as_str()),
            ("*.example.org,!old.example.org", true, fingerprint(&key(7)).as_str())
        );
        // Noch einmal dasselbe: keine zweite Zeile. Andere Muster: eine eigene.
        hosts.trust_authority("*.example.org,!old.example.org", &line).unwrap();
        hosts.trust_authority("10.0.0.?", &line).unwrap();
        assert_eq!(
            fs::read_to_string(&own)
                .unwrap()
                .lines()
                .filter(|line| line.starts_with("@cert-authority"))
                .count(),
            2
        );
        assert_eq!(hosts.authorities("web.example.org", 22), [key(7)]);
        assert!(hosts.authorities("old.example.org", 22).is_empty());
        assert_eq!(
            hosts
                .list()
                .iter()
                .map(|entry| (entry.name.as_str(), entry.authority))
                .collect::<Vec<_>>(),
            [
                ("example.org", false),
                ("*.example.org,!old.example.org", true),
                ("10.0.0.?", true)
            ]
        );

        // Ein Server mehr lässt die Stellen stehen; der Weg für Server entfernt keine Stelle.
        hosts.trust("web.example.org", 2222, &key(2)).unwrap();
        assert!(!hosts.forget("@cert-authority").unwrap());
        assert!(!hosts.forget("10.0.0.?").unwrap());
        assert_eq!(hosts.list().iter().filter(|entry| entry.authority).count(), 2);

        // Entfernt wird genau die eine Zeile: diese Muster, dieser Schlüssel.
        assert!(!hosts.forget_authority("10.0.0.?", &fingerprint(&key(8))).unwrap());
        assert!(!hosts.forget_authority("10.0.0.*", &fingerprint(&key(7))).unwrap());
        assert!(hosts.forget_authority("10.0.0.?", &fingerprint(&key(7))).unwrap());
        assert_eq!(hosts.authorities("10.0.0.5", 22), []);
        assert_eq!(hosts.authorities("web.example.org", 22), [key(7)]);
        assert_eq!(hosts.check("example.org", 22, &key(1)), Verdict::Trusted);
        assert_eq!(hosts.check("web.example.org", 2222, &key(2)), Verdict::Trusted);
    }

    #[test]
    fn only_host_name_patterns_and_real_keys_become_an_authority() {
        for good in [
            "*",
            "web01",
            "*.example.org,10.0.?.1",
            "[web.example.org]:2222",
            "a,!b",
            "fe80::1",
        ] {
            assert_eq!(authority_patterns(good).as_deref(), Some(good), "{good}");
        }

        for bad in [
            "",
            "   ",
            "a b",
            "a\tb",
            "a,",
            ",a",
            "!a",
            "!a,!b",
            "a\n@cert-authority *",
            "a#b",
            "a,!",
            "ä.example.org",
        ] {
            assert_eq!(authority_patterns(bad), None, "{bad:?}");
        }
        assert_eq!(authority_patterns(&"a".repeat(MAX_AUTHORITY_PATTERNS + 1)), None);

        let (hosts, own, _) = files("authority-refused");
        let line = key(7).to_openssh().unwrap();
        assert!(matches!(
            hosts.trust_authority("a b", &line),
            Err(AuthorityError::Patterns)
        ));
        for bad in [
            "",
            "ssh-ed25519",
            "ssh-ed25519 AAAA",
            "ssh-ed25519-cert-v01@openssh.com AAAA",
            "nonsense",
        ] {
            assert!(
                matches!(hosts.trust_authority("*", bad), Err(AuthorityError::Key)),
                "{bad:?}"
            );
        }
        assert!(!own.exists());
    }
}
