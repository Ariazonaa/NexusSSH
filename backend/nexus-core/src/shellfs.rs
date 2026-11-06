//! Dateien ohne SFTP: Bietet der Server kein SFTP an (viele Geräte, ein BusyBox ohne `sftp-server`), erlaubt aber
//! Befehle, spricht der Kern SFTP mit sich selbst – ein kleiner SFTP-Server in diesem Prozess, der jede Anfrage
//! in einen Befehl auf dem Server übersetzt (`ls`, `tail`/`head`, `cat`, `mkdir`, `mv`, `rm` …). Alles darüber –
//! Dateibrowser, Transfers, Fortsetzen, Betrachter – arbeitet unverändert mit einer [`SftpSession`].
//!
//! Das ist ein Ausweg, kein Ersatz: Jede Anfrage kostet einen Befehl, Übertragen ist entsprechend langsam, und
//! einiges ist ungenau – die Änderungszeit kommt aus `ls -l` (auf die Minute, bei älteren Dateien auf den Tag,
//! in der Zeitzone des Servers gelesen als UTC), und Namen mit Zeilenumbruch erscheinen nicht richtig.
//!
//! Wie überall baut die Befehle allein der Kern: Jeder Pfad steht als ein in einfache Anführungszeichen gesetztes
//! Argument darin ([`exec::quote`]); ein Pfad mit Steuerzeichen wird gar nicht erst ausgeführt.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use russh_sftp::client::SftpSession;
use russh_sftp::protocol::{Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version};

use crate::exec;
use crate::session::Link;

/// So lange darf der Versuch dauern, ob der Server überhaupt Befehle wie `ls` ausführt.
const PROBE_LIMIT: Duration = Duration::from_secs(8);

/// Puffer zwischen dem SFTP-Client des Kerns und diesem Server.
const PIPE: usize = 512 * 1024;

/// Öffnet den Dateizugriff über Befehle. `None`, wenn der Server auch keine Befehle ausführt oder ihm `ls` fehlt.
pub(crate) async fn open(link: Arc<Link>) -> Option<(Arc<SftpSession>, String)> {
    // Ein Versuch vorab: Ohne `pwd` und `ls` wäre der Dateibrowser nur eine Fehlerliste. Mit kurzer Frist – ein
    // Gerät, das auf den Befehl gar nicht antwortet (die Kommandozeile eines Switches), soll den Aufbau der
    // Sitzung nicht minutenlang aufhalten.
    let probe = link
        .exec_within(&exec::posix("pwd && ls -ldn / >/dev/null"), PROBE_LIMIT)
        .await
        .ok()?;
    if probe.status != Some(0) {
        return None;
    }

    let (client, server) = tokio::io::duplex(PIPE);
    tokio::spawn(russh_sftp::server::run(server, ShellFiles::new(link)));
    let (client, meter) = crate::metered::counted(client);
    // Die Liste eines Verzeichnisses fragt hier nicht je Link nach dessen Ziel ([`crate::sftp::list`]).
    meter.mark_by_commands();
    let sftp = Arc::new(SftpSession::new(client).await.ok()?);
    crate::metered::attach(&sftp, &meter);
    let home = sftp.canonicalize(".").await.unwrap_or_else(|_| "/".to_owned());
    Some((sftp, home))
}

/// So viele Einträge eines Verzeichnisses gehen in eine Antwort.
const READDIR_CHUNK: usize = 200;

enum Open {
    /// Die Einträge werden einmal geliefert, danach kommt EOF.
    Directory(Option<Vec<File>>),
    /// Eine Datei; `next` ist die Stelle, an der ein Anhängen weiterschriebe – `None`, solange unbekannt.
    File { path: String, next: Option<u64> },
}

struct ShellFiles {
    link: Arc<Link>,
    handles: HashMap<String, Open>,
    next_handle: u64,
}

fn ok(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: "Ok".to_owned(),
        language_tag: "en-US".to_owned(),
    }
}

/// Der Pfad als ein Argument; Steuerzeichen darin machen die Anfrage zum Fehler.
fn arg(path: &str) -> Result<String, StatusCode> {
    // Ein Pfad, der mit `-` beginnt, wäre für `rm`, `mv` oder `chmod` eine Option.
    if path.starts_with('-') {
        return exec::quote(&format!("./{path}")).ok_or(StatusCode::BadMessage);
    }

    exec::quote(path).ok_or(StatusCode::BadMessage)
}

/// Was die Fehlerausgabe eines Befehls über den Grund sagt.
fn reason(stderr: &str) -> StatusCode {
    let text = stderr.to_lowercase();
    if text.contains("no such file") || text.contains("not found") || text.contains("does not exist") {
        StatusCode::NoSuchFile
    } else if text.contains("permission denied") || text.contains("not permitted") || text.contains("read-only") {
        StatusCode::PermissionDenied
    } else {
        StatusCode::Failure
    }
}

impl ShellFiles {
    fn new(link: Arc<Link>) -> Self {
        Self {
            link,
            handles: HashMap::new(),
            next_handle: 0,
        }
    }

    fn remember(&mut self, open: Open) -> String {
        self.next_handle += 1;
        let handle = self.next_handle.to_string();
        self.handles.insert(handle.clone(), open);
        handle
    }

    /// Führt einen Befehl aus, der nur gelingen oder scheitern kann.
    async fn run(&self, command: &str) -> Result<String, StatusCode> {
        let output = self
            .link
            .exec(&exec::posix(command))
            .await
            .map_err(|_| StatusCode::ConnectionLost)?;
        match output.status {
            Some(0) | None => Ok(output.stdout),
            Some(_) => Err(reason(&output.stderr)),
        }
    }

    async fn attributes(&self, path: &str, follow: bool) -> Result<FileAttributes, StatusCode> {
        let flags = if follow { "-ldnL" } else { "-ldn" };
        let listing = self.run(&format!("LC_ALL=C ls {flags} {}", arg(path)?)).await?;
        listing
            .lines()
            .find_map(|line| parse_line(line, now()))
            .map(|(_, attributes)| attributes)
            .ok_or(StatusCode::Failure)
    }

    fn path_of(&self, handle: &str) -> Result<String, StatusCode> {
        match self.handles.get(handle) {
            Some(Open::File { path, .. }) => Ok(path.clone()),
            _ => Err(StatusCode::Failure),
        }
    }
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| since.as_secs())
}

/// Liest eine Zeile von `ls -ln`: Rechte, Links, uid, gid, Größe, Datum (drei Wörter), Name. `None` für alles
/// andere (`total 12`, Fehlermeldungen).
pub(crate) fn parse_line(line: &str, now: u64) -> Option<(String, FileAttributes)> {
    let mode = line.split_whitespace().next()?;
    let kind = match mode.chars().next()? {
        '-' => 0o100_000,
        'd' => 0o040_000,
        'l' => 0o120_000,
        'p' => 0o010_000,
        'c' => 0o020_000,
        'b' => 0o060_000,
        's' => 0o140_000,
        _ => return None,
    };
    // Zehn Zeichen aus ASCII (`drwxr-xr-x`, danach höchstens `+`, `.` oder `@`): Die Zeile ist fremd – auch ein
    // Dateiname mit Zeilenumbruch kann so aussehen wie eine.
    let rights = mode.get(1..10).filter(|rights| rights.is_ascii())?;

    // Felder der Reihe nach abtrennen; der Name ist der Rest der Zeile – er darf Leerzeichen enthalten.
    let mut rest = line;
    let mut field = || {
        rest = rest.trim_start();
        let end = rest.find(char::is_whitespace).unwrap_or(rest.len());
        let (word, after) = rest.split_at(end);
        rest = after;
        (!word.is_empty()).then_some(word)
    };

    field()?;
    field()?;
    let uid: u32 = field()?.parse().ok()?;
    let gid: u32 = field()?.parse().ok()?;
    let mut size = field()?;

    // Ein Gerät nennt statt der Größe „major, minor“.
    if size.ends_with(',') {
        field()?;
        size = "0";
    }

    let size: u64 = size.parse().ok()?;
    let (month, day, clock) = (field()?, field()?, field()?);
    let modified = parse_date(month, day, clock, now);
    let name = rest.strip_prefix(' ').unwrap_or(rest);
    let name = if kind == 0o120_000 {
        name.split(" -> ").next().unwrap_or(name)
    } else {
        name
    };
    if name.is_empty() {
        return None;
    }

    let attributes = FileAttributes {
        size: Some(size),
        uid: Some(uid),
        gid: Some(gid),
        permissions: Some(kind | permission_bits(rights)),
        user: None,
        group: None,
        atime: modified,
        mtime: modified,
    };
    Some((name.to_owned(), attributes))
}

/// `rwxr-xr-x` als Zahl, samt setuid, setgid und Sticky (`s`, `S`, `t`, `T`).
fn permission_bits(text: &str) -> u32 {
    let mut bits = 0;
    for (index, character) in text.chars().take(9).enumerate() {
        let bit = 1 << (8 - index);
        let special = [0o4000, 0o2000, 0o1000][index / 3];
        match character {
            'r' | 'w' | 'x' => bits |= bit,
            's' | 't' => bits |= bit | special,
            'S' | 'T' => bits |= special,
            _ => {}
        }
    }

    bits
}

/// Das Datum von `ls -l` in der Locale C: „Oct  9 22:14“ für Jüngeres, „Oct  9  2025“ für Älteres. Gelesen als UTC –
/// die Zeitzone des Servers kennt hier niemand.
fn parse_date(month: &str, day: &str, clock: &str, now: u64) -> Option<u32> {
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    let month = MONTHS.iter().position(|name| name.eq_ignore_ascii_case(month))? as u64 + 1;
    let day: u64 = day.parse().ok().filter(|day| (1..=31).contains(day))?;
    let seconds = match clock.split_once(':') {
        Some((hour, minute)) => {
            // Nur, was eine Uhr zeigen kann: Die Zeile kommt vom Server, und mit beliebigen Zahlen liefe die
            // Rechnung darunter über.
            let (hour, minute): (u64, u64) = (hour.parse().ok()?, minute.parse().ok()?);
            if hour > 23 || minute > 59 {
                return None;
            }

            // Ohne Jahr: das laufende – oder das davor, wenn das Datum sonst in der Zukunft läge.
            let year = year_of(now);
            let this = days_from_civil(year, month, day) * 86_400 + hour * 3_600 + minute * 60;
            if this > now + 86_400 {
                days_from_civil(year - 1, month, day) * 86_400 + hour * 3_600 + minute * 60
            } else {
                this
            }
        }
        None => {
            days_from_civil(
                clock.parse().ok().filter(|year| (1970..=2200).contains(year))?,
                month,
                day,
            ) * 86_400
        }
    };
    u32::try_from(seconds).ok()
}

/// Tage seit dem 1.1.1970 (Algorithmus von Howard Hinnant, für Jahre ab 1970).
fn days_from_civil(year: u64, month: u64, day: u64) -> u64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year / 400;
    let year_of_era = year - era * 400;
    let day_of_year = (153 * (if month > 2 { month - 3 } else { month + 9 }) + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

fn year_of(seconds: u64) -> u64 {
    let mut year = 1970 + seconds / 31_556_952;
    while days_from_civil(year + 1, 1, 1) * 86_400 <= seconds {
        year += 1;
    }

    while days_from_civil(year, 1, 1) * 86_400 > seconds {
        year -= 1;
    }

    year
}

impl russh_sftp::server::Handler for ShellFiles {
    type Error = StatusCode;

    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }

    async fn init(&mut self, _version: u32, _extensions: HashMap<String, String>) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        // „.“ ist das Verzeichnis, in dem ein Befehl beginnt – das Zuhause des Kontos. Alles andere wird nur
        // geordnet (`a/../b`), nicht nachgeschlagen.
        let resolved = if path.is_empty() || path == "." {
            self.run("pwd").await?.lines().next().unwrap_or("/").to_owned()
        } else {
            let mut parts: Vec<&str> = Vec::new();
            for part in path.split('/') {
                match part {
                    "" | "." => {}
                    ".." => {
                        parts.pop();
                    }
                    part => parts.push(part),
                }
            }

            format!("/{}", parts.join("/"))
        };
        Ok(Name {
            id,
            files: vec![File::dummy(resolved)],
        })
    }

    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        // Mit Schrägstrich am Ende: Ein Verweis auf ein Verzeichnis wird so geöffnet, nicht selbst gezeigt.
        let directory = format!("{}/", path.trim_end_matches('/'));
        let listing = self.run(&format!("LC_ALL=C ls -lan {}", arg(&directory)?)).await?;
        let now = now();
        let files = listing
            .lines()
            .filter_map(|line| parse_line(line, now))
            // Ein Name ist ein einzelner Eintrag dieses Verzeichnisses. Eine Zeile mit `/` im Namen ist gefälscht
            // (das Ziel eines Verweises darf Zeilenumbruch und `/` enthalten, und `ls` gibt es roh aus) – mit ihr
            // führte „Ordner löschen“ aus dem Ordner hinaus.
            .filter(|(name, _)| name != "." && name != ".." && !name.contains('/'))
            .map(|(name, attributes)| File::new(name, attributes))
            .collect();
        Ok(Handle {
            id,
            handle: self.remember(Open::Directory(Some(files))),
        })
    }

    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        match self.handles.get_mut(&handle) {
            // In Stücken, wie ein echter Server: Ein ganzes Verzeichnis in einem einzigen Paket sprengt bei sehr
            // vielen Einträgen die Größe, die die Gegenseite für ein Paket annimmt.
            Some(Open::Directory(files)) => match files.take() {
                // Vom Ende her: Das kostet je Stück nur das Stück, nicht den ganzen Rest. Die Reihenfolge einer
                // Verzeichnisliste bedeutet nichts – wer sie braucht, sortiert.
                Some(mut left) if left.len() > READDIR_CHUNK => {
                    let chunk = left.split_off(left.len() - READDIR_CHUNK);
                    *files = Some(left);
                    Ok(Name { id, files: chunk })
                }
                Some(last) => Ok(Name { id, files: last }),
                None => Err(StatusCode::Eof),
            },
            _ => Err(StatusCode::Failure),
        }
    }

    async fn open(
        &mut self,
        id: u32,
        filename: String,
        flags: OpenFlags,
        _attributes: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        let file = arg(&filename)?;
        let next = if flags.contains(OpenFlags::WRITE) {
            if flags.contains(OpenFlags::TRUNCATE) {
                self.run(&format!(": > {file}")).await?;
                Some(0)
            } else if flags.contains(OpenFlags::CREATE) {
                // Anlegen, wenn es sie nicht gibt; was darin steht, bleibt.
                self.run(&format!(": >> {file}")).await?;
                None
            } else {
                self.run(&format!("[ -f {file} ] && [ -w {file} ]"))
                    .await
                    .map_err(|_| StatusCode::NoSuchFile)?;
                None
            }
        } else {
            // Lesen: Es muss eine gewöhnliche Datei sein – eine Pipe zu öffnen bliebe stehen.
            let attributes = self.attributes(&filename, true).await?;
            if attributes.permissions.map(|mode| mode & 0o170_000) != Some(0o100_000) {
                return Err(StatusCode::Failure);
            }

            None
        };
        Ok(Handle {
            id,
            handle: self.remember(Open::File { path: filename, next }),
        })
    }

    async fn read(&mut self, id: u32, handle: String, offset: u64, length: u32) -> Result<Data, Self::Error> {
        let file = arg(&self.path_of(&handle)?)?;

        // `tail -c +N` beginnt beim N-ten Byte (ab 1 gezählt), `head -c` hört nach der verlangten Menge auf. Das
        // `head` von OpenBSD kennt `-c` nicht: Dann liest `dd` einen Block dieser Größe – aus der Pipe kann das
        // weniger sein als verlangt, was für ein Lesen in Stücken genügt (leer ist es nur am Ende der Datei).
        let command = format!(
            "{{ tail -c +{start} {file} | head -c {length}; }} 2>/dev/null || tail -c +{start} {file} | dd bs={length} count=1 2>/dev/null",
            start = offset + 1
        );
        let output = self
            .link
            .exec_raw(&exec::posix(&command), None, length as usize)
            .await
            .map_err(|_| StatusCode::ConnectionLost)?;
        if output.stdout.is_empty() {
            return Err(StatusCode::Eof);
        }

        Ok(Data {
            id,
            data: output.stdout,
        })
    }

    async fn write(&mut self, id: u32, handle: String, offset: u64, data: Vec<u8>) -> Result<Status, Self::Error> {
        let path = self.path_of(&handle)?;
        let file = arg(&path)?;
        let known = match self.handles.get(&handle) {
            Some(Open::File { next: Some(next), .. }) => *next,
            // Noch unbekannt, wie lang die Datei ist (Fortsetzen): einmal nachsehen.
            _ => self
                .run(&format!("wc -c < {file}"))
                .await?
                .trim()
                .parse()
                .map_err(|_| StatusCode::Failure)?,
        };

        // Der gewöhnliche Fall ist Schreiben am Ende – das kann `cat` schnell. Mitten hinein geht nur Byte für
        // Byte mit `dd`; das kommt beim Hochladen nicht vor, nur wenn jemand eine Datei stückweise überschreibt.
        let command = if offset == known {
            format!("cat >> {file}")
        } else {
            format!("dd of={file} bs=1 seek={offset} conv=notrunc 2>/dev/null")
        };
        let output = self
            .link
            .exec_raw(&exec::posix(&command), Some(&data), 0)
            .await
            .map_err(|_| StatusCode::ConnectionLost)?;
        if !matches!(output.status, Some(0) | None) {
            return Err(reason(&output.stderr));
        }

        // Wie lang die Datei jetzt ist: am Ende angehängt, oder mitten hinein geschrieben.
        let end = offset + data.len() as u64;
        if let Some(Open::File { next, .. }) = self.handles.get_mut(&handle) {
            *next = Some(known.max(end));
        }

        Ok(ok(id))
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        self.handles.remove(&handle);
        Ok(ok(id))
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.attributes(&path, true).await?,
        })
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.attributes(&path, false).await?,
        })
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        let path = self.path_of(&handle)?;
        Ok(Attrs {
            id,
            attrs: self.attributes(&path, true).await?,
        })
    }

    async fn setstat(&mut self, id: u32, path: String, attributes: FileAttributes) -> Result<Status, Self::Error> {
        let file = arg(&path)?;
        if let (Some(uid), Some(gid)) = (attributes.uid, attributes.gid) {
            self.run(&format!("chown {uid}:{gid} {file}")).await?;
        }

        if let Some(mode) = attributes.permissions {
            self.run(&format!("chmod {:o} {file}", mode & 0o7777)).await?;
        }

        // Kürzen auf null kann die Shell; eine andere Länge und die Zeiten lassen sich nicht überall gleich
        // setzen – das bleibt aus, statt je nach Server Verschiedenes zu tun.
        if attributes.size == Some(0) {
            self.run(&format!(": > {file}")).await?;
        }

        Ok(ok(id))
    }

    async fn fsetstat(&mut self, id: u32, handle: String, attributes: FileAttributes) -> Result<Status, Self::Error> {
        let path = self.path_of(&handle)?;
        self.setstat(id, path, attributes).await
    }

    async fn mkdir(&mut self, id: u32, path: String, _attributes: FileAttributes) -> Result<Status, Self::Error> {
        self.run(&format!("mkdir {}", arg(&path)?)).await?;
        Ok(ok(id))
    }

    async fn rmdir(&mut self, id: u32, path: String) -> Result<Status, Self::Error> {
        self.run(&format!("rmdir {}", arg(&path)?)).await?;
        Ok(ok(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        self.run(&format!("rm {}", arg(&filename)?)).await?;
        Ok(ok(id))
    }

    async fn rename(&mut self, id: u32, old_path: String, new_path: String) -> Result<Status, Self::Error> {
        // Wie SFTP: Liegt am Ziel schon etwas, scheitert es – `mv` allein überschriebe.
        let (from, to) = (arg(&old_path)?, arg(&new_path)?);
        self.run(&format!("[ ! -e {to} ] && [ ! -L {to} ] && mv {from} {to}"))
            .await?;
        Ok(ok(id))
    }

    async fn readlink(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let target = self.run(&format!("readlink {}", arg(&path)?)).await?;
        Ok(Name {
            id,
            files: vec![File::dummy(target.lines().next().unwrap_or_default())],
        })
    }

    async fn symlink(&mut self, id: u32, linkpath: String, targetpath: String) -> Result<Status, Self::Error> {
        // Nach der Spezifikation: erst der Verweis, dann sein Ziel. Gibt es den Namen schon, scheitert es – darauf
        // verlässt sich der Kern, der die Reihenfolge von OpenSSH zuerst versucht (siehe `sftp::symlink`).
        let (link, target) = (arg(&linkpath)?, arg(&targetpath)?);
        self.run(&format!("[ ! -e {link} ] && [ ! -L {link} ] && ln -s {target} {link}"))
            .await?;
        Ok(ok(id))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // 2026-10-09 12:00:00 UTC
    const NOW: u64 = 1_791_547_200;

    fn parsed(line: &str) -> (String, FileAttributes) {
        parse_line(line, NOW).unwrap_or_else(|| panic!("not parsed: {line}"))
    }

    #[test]
    fn a_forged_line_neither_panics_nor_leads_out_of_the_directory() {
        // Ein Dateiname mit Zeilenumbruch ergibt in der Ausgabe von `ls` eine zweite Zeile, die aussieht wie ein
        // Eintrag. Rechte, die keine sind (hier endet das zehnte Byte mitten in einem Zeichen), sind keine Zeile.
        for line in [
            "-rw-r--r-é 1 0 0 1 Oct  1 08:00 y",
            "-rwxrwxrwé 1 0 0 1 Oct  1 08:00 y",
            "dééééééééé 1 0 0 1 Oct  1 08:00 y",
            "-rw-r--r 1 0 0 1 Oct  1 08:00 y",
        ] {
            assert!(parse_line(line, NOW).is_none(), "{line}");
        }

        // Eine gefälschte Zeile mit einem Pfad als Namen wird zwar gelesen – aber wer die Liste zeigt oder einen
        // Baum abläuft, nimmt sie nicht: Der Name ist kein Eintrag dieses Verzeichnisses.
        let (name, _) = parse_line(
            "-rw-r--r-- 1 0 0 1 Jan  1  2024 ../../../home/victim/.ssh/authorized_keys",
            NOW,
        )
        .unwrap();
        assert!(crate::sftp::leaves_directory(&name));
        for inside in ["a", "a b", ".hidden", "..."] {
            assert!(!crate::sftp::leaves_directory(inside), "{inside}");
        }
        for outside in ["", ".", "..", "a/b", "/etc", "../x"] {
            assert!(crate::sftp::leaves_directory(outside), "{outside}");
        }

        // Ein Pfad, der mit `-` beginnt, ist für `rm` und `chmod` keine Option.
        assert_eq!(arg("-rf").as_deref(), Ok("'./-rf'"));
        assert_eq!(arg("/tmp/-rf").as_deref(), Ok("'/tmp/-rf'"));
    }

    #[test]
    fn a_line_of_ls_becomes_name_kind_rights_owner_size_and_time() {
        let (name, file) = parsed("-rw-r--r--    1 1000     1000          1834 Oct  9 10:15 nginx.conf");
        assert_eq!(name, "nginx.conf");
        assert_eq!(file.permissions, Some(0o100_644));
        assert_eq!((file.uid, file.gid, file.size), (Some(1000), Some(1000), Some(1834)));
        assert_eq!(file.mtime, Some(1_791_540_900));

        let (name, folder) = parsed("drwxr-xr-x 2 0 0 4096 Jan  5  2021 my folder  with spaces");
        assert_eq!(name, "my folder  with spaces");
        assert_eq!(folder.permissions, Some(0o040_755));
        assert_eq!(folder.mtime, Some(1_609_804_800));

        let (name, link) = parsed("lrwxrwxrwx 1 0 0 7 Oct  1 08:00 current -> v1.2 -> x");
        assert_eq!(name, "current");
        assert_eq!(link.permissions, Some(0o120_777));

        // setuid, Sticky, Geräte („major, minor“ statt der Größe) und eine Pipe.
        assert_eq!(
            parsed("-rwsr-xr-x 1 0 0 1 Oct  1 08:00 su").1.permissions,
            Some(0o104_755)
        );
        assert_eq!(
            parsed("drwxrwxrwt 9 0 0 1 Oct  1 08:00 tmp").1.permissions,
            Some(0o041_777)
        );
        assert_eq!(
            parsed("-rwSr--r-T 1 0 0 1 Oct  1 08:00 odd").1.permissions,
            Some(0o105_644)
        );
        let (name, device) = parsed("crw-rw-rw- 1 0 0 1, 3 Oct  1 08:00 null");
        assert_eq!(
            (name.as_str(), device.permissions, device.size),
            ("null", Some(0o020_666), Some(0))
        );
        assert_eq!(
            parsed("prw-r--r-- 1 0 0 0 Oct  1 08:00 queue.fifo").1.permissions,
            Some(0o010_644)
        );
    }

    #[test]
    fn what_is_not_an_entry_is_skipped() {
        for line in [
            "total 12",
            "",
            "ls: cannot access 'x': No such file or directory",
            "?rw-r--r-- 1 0 0 1 Oct  1 08:00 x",
            "-rw-r--r-- 1 a b 1 Oct  1 08:00 names-not-numbers",
        ] {
            assert!(parse_line(line, NOW).is_none(), "{line}");
        }
    }

    #[test]
    fn a_date_without_a_year_is_this_year_or_the_one_before() {
        // Dezember liegt im Oktober 2026 in der Zukunft – gemeint ist also der vom Vorjahr.
        assert_eq!(
            parsed("-rw-r--r-- 1 0 0 1 Dec 24 18:00 gift").1.mtime,
            Some(1_766_599_200)
        );
        // Der 29. Februar eines Schaltjahrs.
        assert_eq!(
            parsed("-rw-r--r-- 1 0 0 1 Feb 29  2024 leap").1.mtime,
            Some(1_709_164_800)
        );
        assert_eq!(year_of(NOW), 2026);
        assert_eq!(year_of(0), 1970);
        assert_eq!(days_from_civil(1970, 1, 1), 0);
    }

    #[test]
    fn what_a_command_complains_about_becomes_the_reason() {
        assert_eq!(reason("ls: /x: No such file or directory"), StatusCode::NoSuchFile);
        assert_eq!(
            reason("rm: can't remove 'a': Permission denied"),
            StatusCode::PermissionDenied
        );
        assert_eq!(
            reason("mv: cannot move: Read-only file system"),
            StatusCode::PermissionDenied
        );
        assert_eq!(reason("something else"), StatusCode::Failure);
        assert_eq!(arg("a\nb"), Err(StatusCode::BadMessage));
    }
}
