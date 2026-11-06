//! Dateioperationen über SFTP: auflisten, anlegen, umbenennen, löschen.
//!
//! Der SFTP-Kanal gehört zur SSH-Verbindung der Sitzung und wird zusammen mit der Shell geöffnet
//! (`session.rs`). Transfers stehen in [`crate::transfers`].

use std::cmp::Ordering;
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use russh::ChannelMsg;
use russh::client::Handle;
use russh_sftp::client::SftpSession;
use russh_sftp::client::error::Error;
use russh_sftp::protocol::{FileAttributes, OpenFlags, StatusCode};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::task::JoinSet;
use tokio::time::timeout;

use crate::protocol::{Entry, EntryKind, TreeEntry};

const OPEN_TIMEOUT: Duration = Duration::from_secs(10);

/// So viele Links werden gleichzeitig aufgelöst – genug, damit ein Verzeichnis voller Links (`/usr/bin`) nicht
/// Sekunden braucht, wenig genug, um den Server nicht zu überrennen.
const LINKS_AT_ONCE: usize = 16;

/// So viele Bytes nennt der Kern vom Ziel eines Links höchstens (`PATH_MAX` von Linux). Was ein Server darüber
/// hinaus behauptet, wird abgeschnitten, nie abgelehnt: Der Eintrag bleibt in der Liste.
pub(crate) const MAX_TARGET: usize = 4096;

/// Größer wird `/etc/passwd` bzw. `/etc/group` nicht gelesen.
const MAX_NAMES_FILE: u64 = 1024 * 1024;

/// Namen zu den Benutzer- und Gruppennummern des Servers, aus `/etc/passwd` und `/etc/group`. SFTP nennt nur
/// Nummern; fehlt eine Datei oder ein Eintrag (LDAP), bleibt es bei der Nummer.
#[derive(Debug, Default)]
pub(crate) struct Names {
    users: HashMap<u32, String>,
    groups: HashMap<u32, String>,
}

impl Names {
    /// Liest die beiden Dateien; was nicht lesbar ist, fehlt einfach.
    pub(crate) async fn load(sftp: &SftpSession) -> Self {
        Self {
            users: Self::read(sftp, "/etc/passwd").await,
            groups: Self::read(sftp, "/etc/group").await,
        }
    }

    async fn read(sftp: &SftpSession, path: &str) -> HashMap<u32, String> {
        let small = sftp
            .metadata(path)
            .await
            .is_ok_and(|attributes| attributes.size.unwrap_or(u64::MAX) <= MAX_NAMES_FILE);
        match sftp.read(path).await {
            Ok(bytes) if small => Self::parse(&String::from_utf8_lossy(&bytes)),
            _ => HashMap::new(),
        }
    }

    /// Zeilen der Form `name:x:nummer:…`.
    fn parse(text: &str) -> HashMap<u32, String> {
        text.lines()
            .filter_map(|line| {
                let mut fields = line.split(':');
                let name = fields
                    .next()
                    .filter(|name| !name.is_empty() && !name.starts_with('#'))?;
                let id = fields.nth(1)?.parse().ok()?;
                Some((id, name.chars().filter(|c| !c.is_control()).take(64).collect()))
            })
            .collect()
    }

    /// Die Nummer zu einem Benutzer: die Zahl selbst oder der Name aus `/etc/passwd`.
    pub(crate) fn user_id(&self, name: &str) -> Option<u32> {
        Self::id(&self.users, name)
    }

    /// Die Nummer zu einer Gruppe, wie [`Names::user_id`].
    pub(crate) fn group_id(&self, name: &str) -> Option<u32> {
        Self::id(&self.groups, name)
    }

    fn id(known: &HashMap<u32, String>, name: &str) -> Option<u32> {
        let name = name.trim();
        name.parse().ok().or_else(|| {
            known
                .iter()
                .find(|(_, known)| known.as_str() == name)
                .map(|(id, _)| *id)
        })
    }

    fn user(&self, id: Option<u32>) -> Option<String> {
        id.map(|id| self.users.get(&id).cloned().unwrap_or_else(|| id.to_string()))
    }

    fn group(&self, id: Option<u32>) -> Option<String> {
        id.map(|id| self.groups.get(&id).cloned().unwrap_or_else(|| id.to_string()))
    }
}

/// Fehler einer Dateioperation: maschinenlesbare Art und die Meldung des Servers.
#[derive(Debug)]
pub(crate) struct Failure {
    pub(crate) code: &'static str,
    pub(crate) message: String,
}

impl From<Error> for Failure {
    fn from(error: Error) -> Self {
        let code = match &error {
            Error::Status(status) => match status.status_code {
                StatusCode::NoSuchFile => "not_found",
                StatusCode::PermissionDenied => "permission_denied",
                StatusCode::OpUnsupported => "unsupported",
                StatusCode::NoConnection | StatusCode::ConnectionLost => "connection",
                _ => "failed",
            },
            Error::Timeout => "timeout",
            Error::Limited(what) if what == TOO_MANY => "too_many",
            Error::IO(_) => "connection",
            _ => "failed",
        };
        // Der Text des Servers genügt: Davor stünde sonst noch einmal die Art des Fehlers, bei OpenSSH wortgleich
        // („Permission denied: Permission denied“).
        let message = match &error {
            Error::Status(status) if !status.error_message.trim().is_empty() => status.error_message.clone(),
            _ => error.to_string(),
        };
        Self { code, message }
    }
}

impl From<std::io::Error> for Failure {
    fn from(error: std::io::Error) -> Self {
        let code = match error.kind() {
            std::io::ErrorKind::NotFound => "local_not_found",
            std::io::ErrorKind::PermissionDenied => "local_permission_denied",
            _ => "local_io",
        };
        Self {
            code,
            message: error.to_string(),
        }
    }
}

/// Öffnet auf der bestehenden Verbindung einen SFTP-Kanal. `None`, wenn der Server kein SFTP anbietet –
/// die Sitzung läuft dann ohne Dateibrowser weiter.
pub(crate) async fn open<H: russh::client::Handler>(handle: &Handle<H>) -> Option<(Arc<SftpSession>, String)> {
    if NO_SFTP.load(std::sync::atomic::Ordering::Relaxed) {
        return None;
    }

    let attempt = async {
        let mut channel = handle.channel_open_session().await.ok()?;
        channel.request_subsystem(true, "sftp").await.ok()?;

        // Erst die Zusage des Servers abwarten: Auf einem abgelehnten Kanal hinge die SFTP-Begrüßung, bis ihre
        // Frist abläuft.
        loop {
            match channel.wait().await? {
                ChannelMsg::Success => break,
                ChannelMsg::Failure | ChannelMsg::Close | ChannelMsg::Eof => return None,
                _ => {}
            }
        }

        let (stream, meter) = crate::metered::counted(channel.into_stream());
        let sftp = Arc::new(SftpSession::new(stream).await.ok()?);
        crate::metered::attach(&sftp, &meter);
        let home = sftp.canonicalize(".").await.unwrap_or_else(|_| "/".to_owned());
        Some((sftp, home))
    };

    timeout(OPEN_TIMEOUT, attempt).await.ok().flatten()
}

/// Die Bits von `st_mode`, die die Art der Datei nennen (`S_IFMT`).
const TYPE_MASK: u32 = 0o170_000;

/// Ob die Attribute eine Pipe, ein Gerät oder einen Socket beschreiben. Solche Dateien öffnet der Kern nie: Ein
/// SFTP-Server bleibt beim Öffnen einer Pipe stehen, bis jemand am anderen Ende ist – und mit ihm der ganze
/// Dateibrowser der Sitzung. Nennt der Server keine Art, gilt der Eintrag als gewöhnliche Datei.
pub(crate) fn is_special(attributes: &FileAttributes) -> bool {
    matches!(
        attributes.permissions.map(|mode| mode & TYPE_MASK),
        Some(0o010_000 | 0o020_000 | 0o060_000 | 0o140_000)
    )
}

/// Die Absage für alles, was [`is_special`] meldet.
pub(crate) fn not_a_file(path: &str) -> Failure {
    Failure {
        code: "not_a_file",
        message: format!("{path} is not a regular file"),
    }
}

fn entry(name: String, attributes: &FileAttributes, link: bool, names: &Names) -> Entry {
    let kind = if attributes.file_type().is_dir() {
        EntryKind::Directory
    } else if is_special(attributes) {
        EntryKind::Other
    } else {
        EntryKind::File
    };
    Entry {
        name,
        kind,
        link,
        size: shown_size(attributes.size),
        modified: attributes.mtime.map(u64::from),
        // Nur die Rechte, nicht die Bits für die Art der Datei.
        permissions: attributes.permissions.map(|mode| mode & 0o7777),
        owner: attributes.user.clone().or_else(|| names.user(attributes.uid)),
        group: attributes.group.clone().or_else(|| names.group(attributes.gid)),
        target: None,
        dangling: false,
    }
}

/// Was der Server zu einem Link sagt: worauf er zeigt (`stat` folgt dem Link), wohin (`readlink`, der Text im Link
/// selbst – `None`, wo [`list`] ihn nicht fragt) und ob die Kette der Links im Nichts endet (`dead`).
struct Resolved {
    stat: Result<FileAttributes, Error>,
    target: Option<Result<String, Error>>,
    dead: bool,
}

/// Der Eintrag zu einem Link. Er zeigt, was hinter dem Link steckt (ein Link auf ein Verzeichnis lässt sich
/// betreten), und nennt, wohin er zeigt. Ein Link, dessen Ziel sich nicht lesen lässt, bleibt als „anderes“ in der
/// Liste; ins Leere zeigt er (`dangling`) nur, wenn `dead` gilt – bei einer Schleife oder ohne Zugriff weiß man das
/// nicht. Fehlt `resolved`, brach die Aufgabe ab, die ihn auflösen sollte: Dann steht er ohne Ziel da, statt zu
/// fehlen.
fn link_entry(name: String, own: &FileAttributes, resolved: Option<Resolved>, names: &Names) -> Entry {
    let Some(Resolved { stat, target, dead }) = resolved else {
        return Entry {
            kind: EntryKind::Other,
            ..entry(name, own, true, names)
        };
    };

    let mut link = match stat {
        Ok(stat) => entry(name, &stat, true, names),
        Err(_) => Entry {
            kind: EntryKind::Other,
            ..entry(name, own, true, names)
        },
    };
    link.target = target.and_then(Result::ok).and_then(shown_target);
    link.dangling = dead;
    link
}

/// Ob der Server mit „No such file“ geantwortet hat.
fn is_missing(error: &Error) -> bool {
    matches!(error, Error::Status(status) if status.status_code == StatusCode::NoSuchFile)
}

/// So vielen Gliedern einer Kette von Links geht der Kern höchstens nach, wenn er klärt, ob ein Link ins Leere zeigt
/// ([`leads_nowhere`]).
const MAX_HOPS: usize = 8;

/// Der Pfad, auf den der Text `target` im Link `link` zeigt: ein absolutes Ziel, wie es dasteht, ein relatives vom
/// Ordner des Links aus. Nicht bereinigt – `..` und Links auf dem Weg löst der Server selbst richtig auf.
fn beyond(link: &str, target: &str) -> String {
    if target.starts_with('/') {
        return target.to_owned();
    }

    let directory = link.rsplit_once('/').map_or(".", |(directory, _)| directory);
    join(directory, target)
}

/// Ob die Kette der Links ab `link` im Nichts endet. `stat` hat schon „No such file“ gesagt, aber das sagt der
/// `sftp-server` von OpenSSH auch bei einer Schleife von Links (`ELOOP`) – und eine Schleife zeigt nicht ins Leere.
/// Also dem Link nachgehen, Glied für Glied mit `lstat` und `readlink`: Fehlt ein Glied, endet die Kette im Nichts.
/// Kommt ein Pfad ein zweites Mal vor oder sind es mehr als [`MAX_HOPS`] Glieder, ist es eine Schleife oder zu lang
/// (nicht tot); jede andere Antwort – ein Fehler, ein Glied, das kein Link ist – sagt nichts, dann gilt der Link
/// auch nicht als tot. `target` ist der Text in `link`.
async fn leads_nowhere(sftp: &SftpSession, link: &str, target: &str) -> bool {
    let mut seen = vec![link.to_owned()];
    let (mut at, mut text) = (link.to_owned(), target.to_owned());
    for _ in 0..MAX_HOPS {
        // Ein Text, den es als Pfad nicht geben kann: Dem folgt der Kern nicht.
        if text.len() > MAX_TARGET {
            return false;
        }

        let next = beyond(&at, &text);
        if seen.contains(&next) {
            return false;
        }

        match sftp.symlink_metadata(next.clone()).await {
            Err(error) => return is_missing(&error),
            Ok(attributes) if attributes.file_type().is_symlink() => match sftp.read_link(next.clone()).await {
                Ok(onward) => {
                    seen.push(next.clone());
                    (at, text) = (next, onward);
                }
                Err(_) => return false,
            },
            Ok(_) => return false,
        }
    }

    false
}

/// Das Ziel eines Links für die App: höchstens [`MAX_TARGET`] Bytes, an einer Zeichengrenze gekürzt. `None`, wenn der
/// Server keines nennt.
fn shown_target(mut target: String) -> Option<String> {
    let end = (0..=MAX_TARGET.min(target.len()))
        .rev()
        .find(|end| target.is_char_boundary(*end))
        .unwrap_or(0);
    target.truncate(end);
    (!target.is_empty()).then_some(target)
}

/// Wohin der Link `path` zeigt: der Text im Link, wie der Server ihn nennt – nicht aufgelöst. Etwas, das kein Link
/// ist, scheitert mit der Meldung des Servers.
pub(crate) async fn readlink(sftp: &SftpSession, path: &str) -> Result<String, Failure> {
    shown_target(sftp.read_link(path).await?).ok_or_else(|| Failure {
        code: "failed",
        message: format!("{path}: the server names no target"),
    })
}

/// Inhalt eines Verzeichnisses: Ordner zuerst, dann nach Namen ohne Rücksicht auf Groß- und Kleinschreibung.
pub(crate) async fn list(sftp: &Arc<SftpSession>, path: &str, names: &Names) -> Result<Vec<Entry>, Failure> {
    let mut entries = Vec::new();
    let mut links = Vec::new();
    for item in read_dir(sftp, path).await? {
        let attributes = item.metadata();
        if attributes.file_type().is_symlink() {
            links.push((item.file_name(), item.path(), attributes));
        } else {
            entries.push(entry(item.file_name(), &attributes, false, names));
        }
    }

    // Ein Link zeigt, was hinter ihm steckt, und nennt, wohin er zeigt. Jede Auflösung sind zwei Anfragen an den
    // Server – die Links einer Gruppe, und beide je Link, laufen zugleich. Hinter einem Dateizugriff über Befehle
    // (ohne SFTP) kostete das Ziel je Link einen Befehl mehr, und die laufen dort einer nach dem anderen: Dort
    // bleibt es bei `stat`, das Ziel gibt es nur auf Nachfrage (`sftp.readlink`), und ob ein Link ins Leere zeigt,
    // bleibt offen.
    let targets = !crate::metered::of(sftp).is_some_and(|meter| meter.by_commands());
    for batch in links.chunks(LINKS_AT_ONCE) {
        let mut lookups = JoinSet::new();
        for (index, (_, link, _)) in batch.iter().enumerate() {
            let (sftp, link) = (Arc::clone(sftp), link.clone());
            lookups.spawn(async move {
                let (stat, target) = if targets {
                    let (stat, target) = tokio::join!(sftp.metadata(link.clone()), sftp.read_link(link.clone()));
                    (stat, Some(target))
                } else {
                    (sftp.metadata(link.clone()).await, None)
                };

                // Nur ein Link, dessen `stat` „No such file“ sagt, kostet noch mehr Anfragen – und nur, wenn
                // sein Ziel bekannt ist, denn der Kette geht man über die Ziele nach.
                let dead = match (&stat, &target) {
                    (Err(error), Some(Ok(first))) if is_missing(error) => leads_nowhere(&sftp, &link, first).await,
                    _ => false,
                };
                (index, Resolved { stat, target, dead })
            });
        }

        // Bricht eine Aufgabe ab (die Bibliothek verschluckt sich an einer Antwort), fehlt nur ihre Auflösung: Die
        // Schleife darf deshalb nicht bei der ersten aufhören, sonst fehlten alle Links dahinter in der Liste.
        let mut resolved: Vec<Option<Resolved>> = batch.iter().map(|_| None).collect();
        while let Some(done) = lookups.join_next().await {
            if let Ok((index, answer)) = done {
                resolved[index] = Some(answer);
            }
        }

        for ((name, _, own), answer) in batch.iter().zip(resolved) {
            entries.push(link_entry(name.clone(), own, answer, names));
        }
    }

    entries.sort_by(|a, b| {
        (b.kind == EntryKind::Directory)
            .cmp(&(a.kind == EntryKind::Directory))
            .then_with(|| natural(&a.name, &b.name))
            .then_with(|| a.name.cmp(&b.name))
    });
    Ok(entries)
}

/// Vergleicht zwei Namen so, wie man sie liest: ohne Rücksicht auf Groß- und Kleinschreibung, und Ziffernfolgen
/// als Zahlen – `datei-2` steht vor `datei-10`.
fn natural(a: &str, b: &str) -> Ordering {
    // Eine Ziffernfolge ohne ihre führenden Nullen: Die längere ist die größere Zahl, bei gleicher Länge
    // entscheidet der Vergleich Zeichen für Zeichen.
    fn number(text: &mut std::iter::Peekable<std::str::Chars<'_>>) -> String {
        let mut digits = String::new();
        while let Some(digit) = text.next_if(char::is_ascii_digit) {
            if digit != '0' || !digits.is_empty() {
                digits.push(digit);
            }
        }

        digits
    }

    let (mut left, mut right) = (a.chars().peekable(), b.chars().peekable());
    loop {
        let order = match (left.peek().copied(), right.peek().copied()) {
            (None, None) => return Ordering::Equal,
            (None, Some(_)) => return Ordering::Less,
            (Some(_), None) => return Ordering::Greater,
            (Some(x), Some(y)) if x.is_ascii_digit() && y.is_ascii_digit() => {
                let (x, y) = (number(&mut left), number(&mut right));
                x.len().cmp(&y.len()).then_with(|| x.cmp(&y))
            }
            (Some(x), Some(y)) => {
                left.next();
                right.next();
                x.to_lowercase().cmp(y.to_lowercase())
            }
        };
        if order != Ordering::Equal {
            return order;
        }
    }
}

pub(crate) async fn make_directory(sftp: &SftpSession, path: &str) -> Result<(), Failure> {
    Ok(sftp.create_dir(path).await?)
}

/// So viele Treffer liefert eine Suche höchstens.
pub(crate) const FIND_HITS: usize = 500;

/// So viele Einträge sieht sich eine Suche höchstens an, bevor sie mit dem aufhört, was sie hat – in Tests
/// herabgesetzt (`set_find_limit_for_tests`).
pub(crate) static FIND_SEEN: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(200_000);

/// Sucht unter `root` nach Einträgen, deren Name `needle` enthält – ohne Rücksicht auf Groß- und Kleinschreibung.
///
/// Über SFTP selbst statt über `find` auf dem Server: Das geht auch dort, wo das Konto keine Befehle ausführen
/// darf, und kein Name des Benutzers landet in einer Befehlszeile. Ebene für Ebene, damit nahe Treffer zuerst
/// kommen. Links werden nicht verfolgt (sie könnten im Kreis führen), Verzeichnisse ohne Leserecht übersprungen,
/// ebenso `/proc`, `/sys` und `/dev` – dort gibt es nichts, was man als Datei suchen würde.
///
/// Der zweite Wert sagt, ob die Suche vorzeitig aufhörte: `Some(n)`, wenn sie nach `n` angesehenen Einträgen
/// abbrach – dann kann es weitere Treffer geben, und der Benutzer muss das erfahren.
pub(crate) async fn find(
    sftp: &SftpSession,
    root: &str,
    needle: &str,
) -> Result<(Vec<TreeEntry>, Option<u64>), Failure> {
    let limit = FIND_SEEN.load(std::sync::atomic::Ordering::Relaxed);
    let needle = needle.trim().to_lowercase();
    if needle.is_empty() {
        return Err(Failure {
            code: "failed",
            message: "nothing to search for".to_owned(),
        });
    }

    let mut hits = Vec::new();
    let mut seen = 0_usize;
    let mut pending = std::collections::VecDeque::from([root.to_owned()]);
    let mut at_root = true;
    while let Some(directory) = pending.pop_front() {
        let items = match read_dir(sftp, directory.as_str()).await {
            Ok(items) => items,
            // Nur das Verzeichnis, in dem die Suche beginnt, muss lesbar sein.
            Err(error) if at_root => return Err(error.into()),
            Err(_) => continue,
        };
        at_root = false;
        for item in items {
            if leaves_directory(&item.file_name()) {
                continue;
            }

            seen += 1;
            if seen > limit {
                return Ok((hits, Some(limit as u64)));
            }

            let attributes = item.metadata();
            let kind = attributes.file_type();
            let (name, path) = (item.file_name(), item.path());
            if name.to_lowercase().contains(&needle) {
                hits.push(TreeEntry {
                    remote: path.clone(),
                    local: String::new(),
                    directory: kind.is_dir(),
                    size: shown_size(attributes.size),
                    modified: attributes.mtime.map(u64::from),
                });
                if hits.len() >= FIND_HITS {
                    return Ok((hits, None));
                }
            }

            if kind.is_dir() && !kind.is_symlink() && !matches!(path.as_str(), "/proc" | "/sys" | "/dev") {
                pending.push_back(path);
            }
        }
    }

    Ok((hits, None))
}

/// Legt eine leere Datei an. Gibt es den Namen schon, bleibt er, wie er ist – eine vorhandene Datei würde sonst
/// geleert.
pub(crate) async fn create_file(sftp: &SftpSession, path: &str) -> Result<(), Failure> {
    if sftp.try_exists(path).await? {
        return Err(Failure {
            code: "exists",
            message: format!("{path} already exists"),
        });
    }

    let mut file = sftp.create(path).await?;
    file.shutdown().await.map_err(|error| Failure {
        code: "failed",
        message: error.to_string(),
    })
}

/// So viel liest eine Anfrage höchstens aus einer Datei – wer mehr will, fragt in Stücken.
pub(crate) const READ_MAX: u64 = 1024 * 1024;

/// Größere Inhalte nimmt `sftp.write` nicht an; dafür gibt es den Upload.
pub(crate) const WRITE_MAX: usize = 4 * 1024 * 1024;

/// Liest ein Stück einer Datei: ab `offset` höchstens `length` Bytes (nie mehr als [`READ_MAX`]). Dazu die Größe
/// der ganzen Datei, wie der Server sie nennt. Pipes, Geräte und Sockets öffnet der Kern nie ([`is_special`]).
pub(crate) async fn read_part(
    sftp: &SftpSession,
    path: &str,
    offset: u64,
    length: u64,
) -> Result<(Vec<u8>, u64), Failure> {
    use tokio::io::{AsyncReadExt, AsyncSeekExt};

    let attributes = sftp.metadata(path).await?;
    if is_special(&attributes) || attributes.is_dir() {
        return Err(not_a_file(path));
    }

    let wanted = length.min(READ_MAX) as usize;
    let _patient = crate::metered::patient(sftp);
    let mut data = vec![0u8; wanted];
    let mut file = sftp.open(path).await?;
    file.seek(std::io::SeekFrom::Start(offset)).await?;
    let mut filled = 0;
    while filled < wanted {
        match file.read(&mut data[filled..]).await? {
            0 => break,
            count => filled += count,
        }
    }

    data.truncate(filled);
    let _ = file.shutdown().await;
    Ok((data, shown_size(attributes.size)))
}

/// Schreibt eine kleine Datei ganz neu: anlegen oder leeren, schreiben, schließen – an Ort und Stelle, damit
/// Besitzer und Rechte bleiben, wie sie sind. Für den eingebauten Editor; größere Dateien gehen als Upload.
pub(crate) async fn write_whole(sftp: &SftpSession, path: &str, data: &[u8]) -> Result<(), Failure> {
    use russh_sftp::protocol::OpenFlags;

    if data.len() > WRITE_MAX {
        return Err(Failure {
            code: "too_large",
            message: format!("more than {WRITE_MAX} bytes"),
        });
    }

    if let Ok(attributes) = sftp.metadata(path).await
        && (is_special(&attributes) || attributes.is_dir())
    {
        return Err(not_a_file(path));
    }

    let _patient = crate::metered::patient(sftp);
    let mut file = sftp
        .open_with_flags(path, OpenFlags::WRITE | OpenFlags::CREATE | OpenFlags::TRUNCATE)
        .await?;
    file.write_all(data).await?;
    file.shutdown().await?;
    Ok(())
}

const BASE64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Bytes als Base64 – so reisen Dateiinhalte im JSON zur App.
pub(crate) fn to_base64(data: &[u8]) -> String {
    let mut text = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let bits = u32::from(chunk[0]) << 16
            | u32::from(*chunk.get(1).unwrap_or(&0)) << 8
            | u32::from(*chunk.get(2).unwrap_or(&0));
        for index in 0..4 {
            if index <= chunk.len() {
                text.push(BASE64[(bits >> (18 - 6 * index) & 63) as usize] as char);
            } else {
                text.push('=');
            }
        }
    }

    text
}

/// Liest Base64 mit Füllzeichen; `None`, wenn der Text keines ist.
pub(crate) fn from_base64(text: &str) -> Option<Vec<u8>> {
    let bytes = text.as_bytes();
    if !bytes.len().is_multiple_of(4) {
        return None;
    }

    let mut data = Vec::with_capacity(bytes.len() / 4 * 3);
    for (number, chunk) in bytes.chunks(4).enumerate() {
        let last = number == bytes.len() / 4 - 1;
        let padding = chunk.iter().rev().take_while(|byte| **byte == b'=').count();
        if padding > 2 || (padding > 0 && !last) {
            return None;
        }

        let mut bits = 0u32;
        for byte in &chunk[..4 - padding] {
            bits = bits << 6 | BASE64.iter().position(|letter| letter == byte)? as u32;
        }

        bits <<= 6 * padding;
        data.extend_from_slice(&bits.to_be_bytes()[1..4 - padding]);
    }

    Some(data)
}

/// Legt den symbolischen Link `link` an, der auf `target` zeigt. Beides wird vorher geprüft: Den Namen des Links
/// darf es nicht geben, das Ziel muss es geben.
///
/// Die Prüfung des Ziels ist mehr als Höflichkeit. OpenSSH liest die beiden Pfade der Anfrage andersherum, als
/// die Spezifikation sie nennt (Link und Ziel vertauscht), und am Server lässt sich nicht ablesen, welche
/// Reihenfolge er meint. Also zuerst die von OpenSSH – ein Server nach Spezifikation versteht das als „lege
/// einen Link an der Stelle des Ziels an“ und lehnt ab, weil es das Ziel gibt – und danach die der Spezifikation.
pub(crate) async fn symlink(sftp: &SftpSession, link: &str, target: &str) -> Result<(), Failure> {
    if sftp.symlink_metadata(link).await.is_ok() {
        return Err(Failure {
            code: "exists",
            message: format!("{link} already exists"),
        });
    }

    if !sftp.try_exists(target).await? {
        return Err(Failure {
            code: "not_found",
            message: format!("{target}: no such file or directory"),
        });
    }

    // Scheitern beide, gilt der Grund des ersten Versuchs: Der zweite fragt einen Server von OpenSSH nach etwas
    // anderem, und dessen Antwort („No such file“) verdeckte den wahren Grund – unter Windows etwa, dass ein Konto
    // ohne Adminrechte keine Links anlegen darf (am Gast gesehen 2026-10-19).
    let openssh = match sftp.symlink(target, link).await {
        Ok(()) => true,
        Err(first) => {
            if sftp.symlink(link, target).await.is_err() {
                return Err(first.into());
            }
            false
        }
    };

    // Synology DSM zeigt über SFTP Pfade, die es dort nicht gibt (`/home` ist der Ordner des Benutzers unter
    // `/volume1/homes`), schreibt das Ziel eines Links aber wörtlich hin: Der Link zeigte ins Leere, und lesen
    // ließe er sich über SFTP auch nicht mehr. Ein relatives Ziel gilt in beiden Sichten – also, wenn der Link
    // ins Leere zeigt, obwohl es das Ziel gibt, noch einmal relativ, in der Reihenfolge, die eben galt.
    if sftp.metadata(link).await.is_err()
        && let Some(relative) = relative_target(link, target)
        && sftp.remove_file(link).await.is_ok()
    {
        let made = if openssh {
            sftp.symlink(relative.as_str(), link).await
        } else {
            sftp.symlink(link, relative.as_str()).await
        };
        if made.is_err() || sftp.metadata(link).await.is_err() {
            // Hilft auch das nicht, bleibt es beim Link wie verlangt.
            let _ = sftp.remove_file(link).await;
            if openssh {
                sftp.symlink(target, link).await?;
            } else {
                sftp.symlink(link, target).await?;
            }
        }
    }

    Ok(())
}

/// Das Ziel `target` vom Ordner des Links `link` aus gesehen (`../b/c`); beide absolut und ohne `.` und `..`.
fn relative_target(link: &str, target: &str) -> Option<String> {
    let parts = |path: &str| -> Option<Vec<String>> {
        let rest = path.strip_prefix('/')?;
        let parts: Vec<String> = rest
            .split('/')
            .filter(|part| !part.is_empty())
            .map(str::to_owned)
            .collect();
        parts.iter().all(|part| part != "." && part != "..").then_some(parts)
    };
    let mut from = parts(link)?;
    from.pop()?;
    let to = parts(target)?;
    let common = from.iter().zip(&to).take_while(|(a, b)| a == b).count();
    let mut relative: Vec<String> = vec!["..".to_owned(); from.len() - common];
    relative.extend(to[common..].iter().cloned());
    Some(if relative.is_empty() {
        ".".to_owned()
    } else {
        relative.join("/")
    })
}

/// Was an einem Eintrag geändert wird: Rechte (wie `chmod`), Besitzer und Gruppe (wie `chown`). Was `None` ist,
/// bleibt, wie es ist.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct Change {
    pub(crate) mode: Option<u32>,
    pub(crate) uid: Option<u32>,
    pub(crate) gid: Option<u32>,
}

/// Rechte für ein Verzeichnis, wenn `mode` für einen ganzen Baum gilt: Wo gelesen werden darf, darf auch betreten
/// werden – wie `chmod -R u=rwX`. Sonst ließe sich nach `644` kein Ordner mehr öffnen.
fn directory_mode(mode: u32) -> u32 {
    mode | ((mode & 0o444) >> 2)
}

/// Wendet die Änderung auf `path` an; `recursive` nimmt bei einem Verzeichnis alles darin mit. Übertragen wird nur,
/// was sich ändern soll – Größe und Zeiten bleiben. Der Eintrag selbst folgt einem Link (wie `chmod` und `chown`),
/// beim Abstieg werden Links ausgelassen: Sie könnten aus dem Baum hinaus oder im Kreis führen.
pub(crate) async fn change(sftp: &SftpSession, path: &str, change: Change, recursive: bool) -> Result<(), Failure> {
    let root = sftp.metadata(path).await?;
    let is_directory = root.file_type().is_dir();
    apply(sftp, path, &root, change, recursive && is_directory).await?;
    if !(recursive && is_directory) {
        return Ok(());
    }

    let mut pending = vec![path.to_owned()];
    while let Some(directory) = pending.pop() {
        for item in read_dir(sftp, directory.as_str()).await? {
            let attributes = item.metadata();
            let kind = attributes.file_type();
            if kind.is_symlink() || leaves_directory(&item.file_name()) {
                continue;
            }

            apply(sftp, &item.path(), &attributes, change, kind.is_dir()).await?;
            if kind.is_dir() {
                pending.push(item.path());
            }
        }
    }

    Ok(())
}

// SFTP überträgt Besitzer und Gruppe nur gemeinsam: Wer eines ändert, muss das andere so mitschicken, wie es ist.
async fn apply(
    sftp: &SftpSession,
    path: &str,
    current: &FileAttributes,
    change: Change,
    as_tree_directory: bool,
) -> Result<(), Failure> {
    let owner = change.uid.is_some() || change.gid.is_some();
    let attributes = FileAttributes {
        permissions: change.mode.map(|mode| {
            let mode = mode & 0o7777;
            if as_tree_directory { directory_mode(mode) } else { mode }
        }),
        uid: owner.then(|| change.uid.or(current.uid).unwrap_or(0)),
        gid: owner.then(|| change.gid.or(current.gid).unwrap_or(0)),
        ..FileAttributes::empty()
    };
    Ok(sftp.set_metadata(path, attributes).await?)
}

/// Benennt um, ohne ein vorhandenes Ziel zu überschreiben.
pub(crate) async fn rename(sftp: &SftpSession, from: &str, to: &str) -> Result<(), Failure> {
    // SFTP v3 überschreibt laut Protokoll nicht, manche Server tun es trotzdem – deshalb vorher nachsehen.
    if sftp.try_exists(to).await? {
        return Err(Failure {
            code: "exists",
            message: format!("{to} already exists"),
        });
    }

    Ok(sftp.rename(from, to).await?)
}

/// Löscht eine Datei oder ein Verzeichnis samt Inhalt. Links werden entfernt, nicht verfolgt.
pub(crate) async fn delete(sftp: &SftpSession, path: &str) -> Result<(), Failure> {
    // Ohne Rekursion der async-Funktion: ein Stapel aus Verzeichnissen, die erst geleert und dann entfernt werden.
    let mut pending = vec![(path.to_owned(), false)];
    while let Some((current, emptied)) = pending.pop() {
        if emptied {
            sftp.remove_dir(current).await?;
            continue;
        }

        let attributes = sftp.symlink_metadata(current.as_str()).await?;
        if attributes.file_type().is_dir() {
            pending.push((current.clone(), true));
            for item in read_dir(sftp, current.as_str()).await? {
                if !leaves_directory(&item.file_name()) {
                    pending.push((item.path(), false));
                }
            }
        } else {
            sftp.remove_file(current).await?;
        }
    }

    Ok(())
}

/// Nur für Tests: Der Kern tut, als böte kein Server SFTP an – so lässt sich der Dateizugriff über Befehle an
/// Servern prüfen, die SFTP können.
pub(crate) static NO_SFTP: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// So viele Einträge kopiert [`copy`] höchstens.
const COPY_LIMIT: usize = 20_000;

/// Kopiert eine Datei oder einen Ordner samt Inhalt auf dem Server – über SFTP, also durch diesen Rechner
/// hindurch: lesen und gleich wieder schreiben. Der Weg für Konten, denen der Server keine Befehle erlaubt
/// (nur SFTP), und für FTP; mit Shell kopiert `cp` am Server, ohne dass ein Byte die Leitung sieht.
///
/// Wie `cp -RPp`: Verweise bleiben Verweise, Rechte und Änderungszeit wandern mit, so gut der Server es zulässt.
/// Pipes, Geräte und Sockets werden ausgelassen – sie lassen sich über SFTP nicht anlegen, und sie zu öffnen
/// bliebe hängen. Das Ziel darf es noch nicht geben.
pub(crate) async fn copy(
    sftp: &SftpSession,
    from: &str,
    to: &str,
    limiter: &crate::transfers::Limiter,
) -> Result<(), Failure> {
    // In sich selbst hinein hörte ein Kopieren nie auf.
    let inside = to
        .strip_prefix(from)
        .is_some_and(|rest| rest.is_empty() || rest.starts_with('/'));
    // Ein Pfad mit `..` oder `.` darin könnte derselbe Ort unter anderem Namen sein – die Prüfung oben sähe es
    // nicht. Die App nennt nur bereinigte Pfade; etwas anderes wird gar nicht erst versucht.
    let plain = |path: &str| path.split('/').all(|part| part != ".." && part != ".");
    if !from.starts_with('/') || !to.starts_with('/') || from == "/" || inside || !plain(from) || !plain(to) {
        return Err(Failure {
            code: "bad_request",
            message: format!("{to}: cannot copy there"),
        });
    }

    match sftp.symlink_metadata(to).await {
        Ok(_) => {
            return Err(Failure {
                code: "exists",
                message: format!("{to} already exists"),
            });
        }
        Err(error) => {
            let failure = Failure::from(error);
            if failure.code != "not_found" {
                return Err(failure);
            }
        }
    }

    let _patient = crate::metered::patient(sftp);
    let mut pending = vec![(from.to_owned(), to.to_owned())];
    let mut seen = 0usize;
    let mut buffer = vec![0u8; 256 * 1024];
    while let Some((source, target)) = pending.pop() {
        seen += 1;
        if seen > COPY_LIMIT {
            return Err(Failure {
                code: "too_many",
                message: format!("more than {COPY_LIMIT} entries – copy the folder in parts"),
            });
        }

        let attributes = sftp.symlink_metadata(source.as_str()).await?;
        let kind = attributes.file_type();
        if kind.is_symlink() {
            // Der Verweis selbst, nicht das, worauf er zeigt – wohin, steht in ihm.
            // Der SFTP-Server von Synology DSM liest über SFTP keinen Link (immer „No such file“) – dann sagt die
            // Meldung, welcher Link es war.
            let aim = sftp.read_link(source.as_str()).await.map_err(|error| Failure {
                code: Failure::from(error).code,
                message: format!("{source}: the server cannot read this link"),
            })?;

            // OpenSSH liest die beiden Pfade der Anfrage vertauscht, andere Server nicht – welcher hier antwortet,
            // zeigt erst das Ergebnis: Steht der Verweis danach am Ziel und zeigt, wohin er soll, war es richtig.
            let made = sftp.symlink(aim.as_str(), target.as_str()).await.is_ok()
                && sftp.read_link(target.as_str()).await.is_ok_and(|points| points == aim);
            if !made {
                // Ein Server, der die Reihenfolge anders liest, hat stattdessen einen Verweis unter dem Namen des
                // Ziels dieses Verweises angelegt – genau den wieder wegnehmen, sonst bliebe er liegen.
                if sftp.read_link(aim.as_str()).await.is_ok_and(|points| points == target) {
                    let _ = sftp.remove_file(aim.as_str()).await;
                }

                sftp.symlink(target.as_str(), aim.as_str()).await?;
            }
        } else if kind.is_dir() {
            sftp.create_dir(target.as_str()).await?;
            for item in read_dir(sftp, source.as_str()).await? {
                let name = item.file_name();
                if !leaves_directory(&name) {
                    pending.push((join(&source, &name), join(&target, &name)));
                }
            }

            keep(sftp, &target, &attributes).await;
        } else if !is_special(&attributes) {
            let mut input = sftp.open(source.as_str()).await?;
            // Nur anlegen, nie überschreiben: Entsteht das Ziel zwischen der Prüfung oben und jetzt, scheitert
            // das hier, statt eine fremde Datei zu leeren.
            let mut output = sftp
                .open_with_flags(
                    target.as_str(),
                    OpenFlags::WRITE | OpenFlags::CREATE | OpenFlags::EXCLUDE,
                )
                .await?;
            loop {
                let read = input.read(&mut buffer).await.map_err(io_failure)?;
                if read == 0 {
                    break;
                }

                output.write_all(&buffer[..read]).await.map_err(io_failure)?;

                // Auch dieser Weg hält sich an die Obergrenze für die Bandbreite – hin und zurück.
                limiter.take(read * 2).await;
            }

            output.shutdown().await.map_err(io_failure)?;
            keep(sftp, &target, &attributes).await;
        }
    }

    Ok(())
}

// Rechte und Zeiten der Quelle für die Kopie; verweigert der Server es, bleibt die Kopie trotzdem eine.
async fn keep(sftp: &SftpSession, path: &str, source: &FileAttributes) {
    let wanted = FileAttributes {
        permissions: source.permissions.map(|mode| mode & 0o7777),
        atime: source.atime,
        mtime: source.mtime,
        ..FileAttributes::empty()
    };
    let _ = sftp.set_metadata(path, wanted).await;
}

fn io_failure(error: std::io::Error) -> Failure {
    Failure {
        code: "connection",
        message: error.to_string(),
    }
}

/// So lange darf das Lesen eines einzelnen Verzeichnisses dauern.
pub(crate) const LIST_LIMIT: std::time::Duration = std::time::Duration::from_secs(120);

/// Die Einträge eines Verzeichnisses – mit einer Frist für das Ganze. Die Bibliothek liest, bis der Server „Ende“
/// sagt, und sammelt alles; ein Server, der nie aufhört, füllte sonst den Speicher, und niemand könnte es
/// abbrechen. Läuft die Frist ab, fällt das bis dahin Gesammelte weg und die Anfrage scheitert.
pub(crate) async fn read_dir(
    sftp: &SftpSession,
    path: &str,
) -> Result<russh_sftp::client::fs::ReadDir, russh_sftp::client::error::Error> {
    let listing = tokio::time::timeout(LIST_LIMIT, sftp.read_dir(path));
    let Some(meter) = crate::metered::of(sftp) else {
        return listing.await.unwrap_or(Err(Error::Timeout));
    };

    // Die Frist begrenzt die Zeit, der Zähler die Menge: Die Bibliothek sammelt, bis der Server „Ende“ sagt, und
    // ein Server, der schnell genug immer weiter Einträge nennt, füllte bis dahin den Speicher. Gezählt wird, was
    // seit dem Beginn dieser Liste auf der Sitzung ankam – laufen mehrere Listen zugleich, zählen sie zusammen.
    let (start, limit) = (meter.entries(), MAX_ENTRIES.load(std::sync::atomic::Ordering::Relaxed));
    tokio::pin!(listing);
    loop {
        tokio::select! {
            listed = &mut listing => return listed.unwrap_or(Err(Error::Timeout)),
            () = meter.grew() => {
                if meter.entries().saturating_sub(start) > limit {
                    // Die Liste wird fallen gelassen; ihre Antworten, die noch kommen, gehören dann zu keiner
                    // Anfrage mehr, und der Griff bleibt am Server offen, bis die Sitzung endet.
                    return Err(Error::Limited(TOO_MANY.to_owned()));
                }
            }
        }
    }
}

/// So viele Einträge darf ein Verzeichnis nennen (alle Listen einer Sitzung zusammen, die gerade laufen). Weit
/// über dem, was ein Verzeichnis sinnvoll enthält; darüber gibt der Kern die Liste auf, statt weiter zu sammeln.
pub(crate) static MAX_ENTRIES: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1_000_000);

/// Der Text des Fehlers, an dem [`Failure`] eine zu lange Liste erkennt.
const TOO_MANY: &str = "the directory lists more entries than this app reads";

/// Eine Größe, wie der Server sie nennt, für die App: höchstens, was dort als Zahl mit Vorzeichen Platz hat. Ein
/// Server, der 2^63 oder mehr behauptet, ließe sonst das ganze Ereignis beim Lesen scheitern.
pub(crate) fn shown_size(size: Option<u64>) -> u64 {
    size.unwrap_or(0).min(i64::MAX as u64)
}

/// Ein Name aus einer Liste des Servers, der kein einzelner Eintrag des gelisteten Verzeichnisses ist: mit `/`
/// darin, leer, `.` oder `..`. Wer rekursiv löscht oder Rechte setzt, folgt so einem Namen nicht – er führte aus
/// dem Verzeichnis hinaus, in dem der Benutzer etwas tun wollte.
pub(crate) fn leaves_directory(name: &str) -> bool {
    name.is_empty() || name == "." || name == ".." || name.contains('/')
}

/// Fügt einem Verzeichnis einen Namen an – SFTP-Pfade haben immer `/` als Trenner.
pub(crate) fn join(directory: &str, name: &str) -> String {
    if directory.ends_with('/') {
        format!("{directory}{name}")
    } else {
        format!("{directory}/{name}")
    }
}

/// Letzter Teil eines SFTP-Pfads.
pub(crate) fn base_name(path: &str) -> &str {
    path.trim_end_matches('/').rsplit('/').next().unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_relative_target_leads_from_the_folder_of_the_link_to_the_target() {
        for (link, target, expected) in [
            ("/home/t/src dir/link", "/home/t/src dir/a b.txt", Some("a b.txt")),
            ("/home/t/a/link", "/home/t/b/c.txt", Some("../b/c.txt")),
            ("/home/t/a/b/link", "/home/x", Some("../../../x")),
            ("/home/t/link", "/home/t", Some(".")),
            ("/link", "/etc/passwd", Some("etc/passwd")),
            ("relative/link", "/x", None),
            ("/home/link", "/home/../etc", None),
            ("/", "/x", None),
        ] {
            assert_eq!(relative_target(link, target).as_deref(), expected, "{link} -> {target}");
        }
    }

    /// Eine Antwort des Servers, die „nein“ sagt.
    fn refused(code: StatusCode) -> Error {
        Error::Status(russh_sftp::protocol::Status {
            id: 0,
            status_code: code,
            error_message: String::new(),
            language_tag: String::new(),
        })
    }

    #[test]
    fn only_a_missing_file_counts_as_missing() {
        assert!(is_missing(&refused(StatusCode::NoSuchFile)));
        for error in [
            refused(StatusCode::Failure),
            refused(StatusCode::PermissionDenied),
            refused(StatusCode::OpUnsupported),
            Error::Timeout,
        ] {
            assert!(!is_missing(&error), "{error}");
        }
    }

    #[test]
    fn the_target_of_a_link_leads_from_the_folder_of_the_link() {
        for (link, target, expected) in [
            ("/links/rel", "datei", "/links/datei"),
            ("/links/rel", "sub/datei", "/links/sub/datei"),
            // Nicht bereinigt: Ein Link auf dem Weg könnte `..` anders auflösen, als der Text es vermuten lässt.
            ("/links/sub/rel", "../datei", "/links/sub/../datei"),
            ("/links/abs", "/etc/passwd", "/etc/passwd"),
            ("/rel", "datei", "/datei"),
            ("rel", "datei", "./datei"),
        ] {
            assert_eq!(beyond(link, target), expected, "{link} -> {target}");
        }
    }

    #[test]
    fn a_link_entry_shows_what_it_points_at_and_names_the_target_it_was_given() {
        let names = Names::default();
        let own = FileAttributes {
            permissions: Some(0o120_777),
            ..FileAttributes::empty()
        };
        let of = |mode: u32| FileAttributes {
            permissions: Some(mode),
            ..FileAttributes::empty()
        };
        let link = |stat: Result<FileAttributes, Error>, target: Option<Result<String, Error>>, dead: bool| {
            link_entry("l".to_owned(), &own, Some(Resolved { stat, target, dead }), &names)
        };

        // Ein Link auf einen Ordner ist ein Ordner – und nennt sein Ziel.
        let folder = link(Ok(of(0o040_755)), Some(Ok("sub".to_owned())), false);
        assert_eq!(
            (folder.kind, folder.link, folder.dangling),
            (EntryKind::Directory, true, false)
        );
        assert_eq!(folder.target.as_deref(), Some("sub"));

        // Ins Leere zeigt nur, was die Kette der Links als tot ausweist (`dead`). Das Ziel kennt der Link trotzdem.
        let dead = link(Err(refused(StatusCode::NoSuchFile)), Some(Ok("fehlt".to_owned())), true);
        assert_eq!((dead.kind, dead.link, dead.dangling), (EntryKind::Other, true, true));
        assert_eq!(dead.target.as_deref(), Some("fehlt"));

        // Scheitert `stat`, ohne dass die Kette im Nichts endet, bleibt der Link „anderes“ und nicht tot: bei einer
        // Schleife (OpenSSH sagt dazu „No such file“), fehlendem Zugriff, einem Server ohne Antwort.
        for stat in [
            refused(StatusCode::NoSuchFile),
            refused(StatusCode::Failure),
            refused(StatusCode::PermissionDenied),
            refused(StatusCode::OpUnsupported),
            Error::Timeout,
        ] {
            let unknown = link(Err(stat), Some(Ok("kreis".to_owned())), false);
            assert_eq!((unknown.kind, unknown.dangling), (EntryKind::Other, false));
            assert_eq!(unknown.target.as_deref(), Some("kreis"));
        }

        // Wo das Ziel nicht gefragt wird (Dateizugriff über Befehle) oder der Server `readlink` nicht kann (DSM: „No
        // such file“ auf jeden Link), bleibt der Link, was `stat` sagt – ohne Ziel und nicht tot.
        let unasked = link(Ok(of(0o100_644)), None, false);
        assert_eq!(
            (unasked.kind, unasked.dangling, unasked.target),
            (EntryKind::File, false, None)
        );
        for target in [refused(StatusCode::OpUnsupported), refused(StatusCode::NoSuchFile)] {
            let unnamed = link(Ok(of(0o100_644)), Some(Err(target)), false);
            assert_eq!(
                (unnamed.kind, unnamed.dangling, unnamed.target),
                (EntryKind::File, false, None)
            );
        }

        // Brach die Aufgabe ab, die den Link auflösen sollte, bleibt er in der Liste – ohne Ziel, als „anderes“.
        let lost = link_entry("l".to_owned(), &own, None, &names);
        assert_eq!(
            (lost.name.as_str(), lost.kind, lost.link, lost.dangling, lost.target),
            ("l", EntryKind::Other, true, false, None)
        );
    }

    #[test]
    fn a_target_beyond_the_limit_is_cut_and_never_refused() {
        assert_eq!(shown_target("datei".to_owned()).as_deref(), Some("datei"));
        assert_eq!(shown_target(String::new()), None);

        let exact = "a".repeat(MAX_TARGET);
        assert_eq!(shown_target(exact.clone()), Some(exact));
        let longer = shown_target("a".repeat(MAX_TARGET + 100)).unwrap();
        assert_eq!(longer.len(), MAX_TARGET);

        // Nie mitten in einem Zeichen: „ä“ belegt zwei Bytes. Hier läge die Grenze nach dem ersten davon.
        let umlauts = shown_target(format!("a{}", "ä".repeat(MAX_TARGET))).unwrap();
        assert_eq!(umlauts.len(), MAX_TARGET - 1);
        assert!(umlauts.ends_with('ä'));
    }

    #[test]
    fn a_size_never_exceeds_what_the_app_can_read() {
        assert_eq!(shown_size(None), 0);
        assert_eq!(shown_size(Some(5)), 5);
        assert_eq!(shown_size(Some(u64::MAX)), i64::MAX as u64);
        assert_eq!(shown_size(Some(1 << 63)), i64::MAX as u64);
    }
    #[test]
    fn file_contents_travel_as_base64_and_come_back_the_same() {
        for (bytes, text) in [
            (&b""[..], ""),
            (b"f", "Zg=="),
            (b"fo", "Zm8="),
            (b"foo", "Zm9v"),
            (b"foob", "Zm9vYg=="),
            (&[0xff, 0xfe, 0x00][..], "//4A"),
        ] {
            assert_eq!(to_base64(bytes), text);
            assert_eq!(from_base64(text).as_deref(), Some(bytes));
        }

        // Kein Base64: falsche Länge, fremde Zeichen, Füllzeichen an der falschen Stelle.
        for text in ["Zg=", "Zm9v!", "Z===", "Zg==Zm9v", "====", "Zm 9"] {
            assert_eq!(from_base64(text), None, "{text}");
        }
    }

    #[test]
    fn names_sort_the_way_one_reads_them() {
        let mut names = vec![
            "datei-10.log",
            "Datei-2.log",
            "datei-1.log",
            "datei-02.log",
            "datei-100.log",
            "datei.log",
            "v1.10",
            "v1.9",
            "Zebra",
            "apfel",
            "10",
            "9",
            "ä",
        ];
        names.sort_by(|a, b| natural(a, b).then_with(|| a.cmp(b)));
        assert_eq!(
            names,
            [
                "9",
                "10",
                "apfel",
                "datei-1.log",
                "Datei-2.log",
                "datei-02.log",
                "datei-10.log",
                "datei-100.log",
                "datei.log",
                "v1.9",
                "v1.10",
                "Zebra",
                "ä",
            ]
        );
        assert_eq!(natural("abc", "ABC"), Ordering::Equal);
        assert_eq!(natural("a007", "a7"), Ordering::Equal);
        assert_eq!(natural("a", "a1"), Ordering::Less);
    }

    #[test]
    fn a_tree_keeps_its_directories_enterable() {
        assert_eq!(directory_mode(0o644), 0o755);
        assert_eq!(directory_mode(0o600), 0o700);
        assert_eq!(directory_mode(0o640), 0o750);
        assert_eq!(directory_mode(0o755), 0o755);
        assert_eq!(directory_mode(0o000), 0o000);
        assert_eq!(directory_mode(0o2664), 0o2775);
    }

    #[test]
    fn owners_are_given_as_a_name_or_a_number() {
        let names = Names {
            users: HashMap::from([(0, "root".to_owned()), (1000, "demo".to_owned())]),
            groups: HashMap::from([(33, "www-data".to_owned())]),
        };

        assert_eq!(names.user_id("demo"), Some(1000));
        assert_eq!(names.user_id(" 1234 "), Some(1234));
        assert_eq!(names.user_id("nobody-here"), None);
        assert_eq!(names.group_id("www-data"), Some(33));
        assert_eq!(names.group_id("demo"), None);
    }

    #[test]
    fn join_and_base_name_handle_slashes() {
        assert_eq!(join("/", "etc"), "/etc");
        assert_eq!(join("/var/www", "html"), "/var/www/html");
        assert_eq!(join("/var/www/", "html"), "/var/www/html");
        assert_eq!(base_name("/var/www/html"), "html");
        assert_eq!(base_name("/var/www/"), "www");
        assert_eq!(base_name("file"), "file");
        assert_eq!(base_name("/"), "");
    }
}
