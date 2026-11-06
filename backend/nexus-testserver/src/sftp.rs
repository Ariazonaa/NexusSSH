//! SFTP-Teil des Testservers: ein echtes Verzeichnis als Wurzel `/`.
//!
//! Genug für die Tests des Kerns und zum Ausprobieren der App – kein vollständiger SFTP-Server. Pfade können
//! die Wurzel nicht verlassen.

use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

use russh_sftp::protocol::{Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version};

/// So viele Einträge eines Verzeichnisses gehen in eine Antwort.
const READDIR_CHUNK: usize = 200;

/// So viele Einträge nennt ein Verzeichnis namens `million` – knapp über einer Million, erfunden statt gelesen.
pub const MILLION: u64 = 1_000_100;

/// Die Attribute eines erfundenen Eintrags: eine leere, gewöhnliche Datei.
fn made_attributes() -> FileAttributes {
    FileAttributes {
        size: Some(0),
        uid: Some(OWNER_ID),
        gid: Some(OWNER_ID),
        permissions: Some(0o100_644),
        ..FileAttributes::empty()
    }
}

enum Open {
    File(fs::File),
    /// Die Einträge werden einmal geliefert, danach kommt EOF.
    Directory(Option<Vec<File>>),
    /// Ein erfundenes Verzeichnis ([`MILLION`]): so viele Einträge sind noch zu nennen.
    Made(u64),
}

pub(crate) struct Sftp {
    root: PathBuf,
    handles: HashMap<String, Open>,
    next_handle: u64,
    /// Offene Dateien, deren Inhalt langsam kommt ([`is_slow`]).
    slow: std::collections::HashSet<String>,
    /// Mit `setstat` gesetzte Rechte. Sie gelten nur für diese Verbindung: Das Dateisystem des Rechners kennt
    /// unter Windows keine Unix-Rechte, also merkt der Testserver sie sich selbst.
    modes: HashMap<PathBuf, u32>,
    /// Mit `setstat` gesetzte Besitzer und Gruppen, wie [`Sftp::modes`].
    owners: HashMap<PathBuf, (u32, u32)>,
}

fn ok(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: "Ok".to_owned(),
        language_tag: "en-US".to_owned(),
    }
}

/// Besitzer und Gruppe, die der Testserver für jede Datei nennt.
pub const OWNER_ID: u32 = 1000;

/// Attribute, die auf jedem Betriebssystem gleich aussehen: Besitzer und Gruppe [`OWNER_ID`], Ordner `rwxr-xr-x`,
/// Dateien `rw-r--r--`, Links `rwxrwxrwx`. Was das Dateisystem des Rechners dazu sagt, wäre unter Windows und
/// Linux verschieden.
fn portable(metadata: &fs::Metadata) -> FileAttributes {
    // Art der Datei, wie Unix sie in den oberen Bits führt (S_IFLNK, S_IFDIR, S_IFREG).
    let mode = if metadata.file_type().is_symlink() {
        0o120_777
    } else if metadata.is_dir() {
        0o040_755
    } else {
        0o100_644
    };
    let mut attributes = FileAttributes::from(metadata);
    attributes.permissions = Some(mode);
    attributes.uid = Some(OWNER_ID);
    attributes.gid = Some(OWNER_ID);
    attributes
}

/// Ob der Fehler „zu viele Ebenen symbolischer Links“ ist (`ELOOP`) – eine Schleife. Die Nummer hängt vom System ab;
/// `ErrorKind::FilesystemLoop` gibt es in Rust noch nicht stabil.
fn is_loop(error: &std::io::Error) -> bool {
    const ELOOP: i32 = if cfg!(any(
        target_os = "macos",
        target_os = "freebsd",
        target_os = "openbsd",
        target_os = "netbsd"
    )) {
        62
    } else {
        40
    };
    cfg!(unix) && error.raw_os_error() == Some(ELOOP)
}

fn status(error: std::io::Error) -> StatusCode {
    match error.kind() {
        std::io::ErrorKind::NotFound => StatusCode::NoSuchFile,
        std::io::ErrorKind::PermissionDenied => StatusCode::PermissionDenied,
        // Wie `sftp-server` von OpenSSH (`errno_to_portable`): Auch eine Schleife von Links heißt „No such file“ –
        // ein Client kann sie am Status nicht von einem Link ins Leere unterscheiden.
        _ if is_loop(&error) => StatusCode::NoSuchFile,
        _ => StatusCode::Failure,
    }
}

/// Pfad des Clients als Liste seiner Teile, ohne `.` und `..` – `..` kann die Wurzel nicht verlassen.
fn parts(path: &str) -> Vec<&str> {
    let mut parts = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            part => parts.push(part),
        }
    }

    parts
}

impl Sftp {
    pub(crate) fn new(root: PathBuf) -> Self {
        Self {
            root,
            handles: HashMap::new(),
            next_handle: 0,
            slow: std::collections::HashSet::new(),
            modes: HashMap::new(),
            owners: HashMap::new(),
        }
    }

    fn local(&self, path: &str) -> PathBuf {
        let mut local = self.root.clone();
        local.extend(parts(path));
        local
    }

    fn remember(&mut self, open: Open) -> String {
        self.next_handle += 1;
        let handle = self.next_handle.to_string();
        self.handles.insert(handle.clone(), open);
        handle
    }

    /// Attribute dessen, worauf der Pfad zeigt – ein Link wird verfolgt (`stat`).
    fn attributes(&self, path: &Path) -> Result<FileAttributes, StatusCode> {
        fs::metadata(path)
            .map(|metadata| self.with_mode(path, portable(&metadata)))
            .map_err(status)
    }

    /// Attribute des Eintrags selbst – ein Link bleibt ein Link, auch ein toter (`lstat`, Verzeichnisliste).
    fn own_attributes(&self, path: &Path) -> Result<FileAttributes, StatusCode> {
        fs::symlink_metadata(path)
            .map(|metadata| self.with_mode(path, portable(&metadata)))
            .map_err(status)
    }

    /// Setzt die mit `setstat` gemerkten Rechte ein; die Art der Datei (obere Bits) bleibt – außer bei einer
    /// vorgespielten Pipe ([`is_pipe`]), die sich als solche ausgibt (`S_IFIFO`).
    fn with_mode(&self, path: &Path, mut attributes: FileAttributes) -> FileAttributes {
        if let (Some(mode), Some(current)) = (self.modes.get(path), attributes.permissions) {
            attributes.permissions = Some((current & !0o7777) | mode);
        }

        if let Some((uid, gid)) = self.owners.get(path) {
            (attributes.uid, attributes.gid) = (Some(*uid), Some(*gid));
        }

        if let (true, Some(current)) = (is_pipe(path), attributes.permissions)
            && current & 0o170_000 == 0o100_000
        {
            attributes.permissions = Some(0o010_000 | (current & 0o7777));
        }

        attributes
    }
}

/// Dateien mit der Endung `.fifo` spielt der Testserver als Named Pipe vor – echte gibt es unter Windows nicht.
fn is_pipe(path: &Path) -> bool {
    path.extension().is_some_and(|extension| extension == "fifo")
}

/// Dateien mit der Endung `.slow` kommen wie über eine langsame Leitung: Jede Leseanfrage wartet [`SLOW_READ`],
/// und weil der Server eine nach der anderen beantwortet, wartet die sechzehnte vorausgeschickte sechzehnmal so
/// lange. Ein Client, der jeder Anfrage ab dem Absenden nur zehn Sekunden gibt, scheitert daran.
fn is_slow(path: &Path) -> bool {
    path.extension().is_some_and(|extension| extension == "slow")
}

/// So lange braucht jede Leseanfrage an eine langsame Datei ([`is_slow`]).
pub const SLOW_READ: std::time::Duration = std::time::Duration::from_millis(800);

impl russh_sftp::server::Handler for Sftp {
    type Error = StatusCode;

    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }

    async fn init(&mut self, _version: u32, _extensions: HashMap<String, String>) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        Ok(Name {
            id,
            files: vec![File::dummy(format!("/{}", parts(&path).join("/")))],
        })
    }

    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        if parts(&path).last() == Some(&"stuck") {
            // Wie ein Server, dessen Dateisystem hängt (ein totes Netzlaufwerk): Diese Anfrage wird nie beantwortet,
            // und alle weiteren auf diesem Kanal auch nicht.
            return std::future::pending().await;
        }

        if parts(&path).last() == Some(&"million") {
            // Ein Server, der schneller Einträge nennt, als ein echtes Dateisystem es könnte – ohne eine Million
            // Dateien auf der Platte. Daran greift die Obergrenze der Einträge, nicht die Frist.
            return Ok(Handle {
                id,
                handle: self.remember(Open::Made(MILLION)),
            });
        }

        let mut files = Vec::new();
        for entry in fs::read_dir(self.local(&path)).map_err(status)? {
            let entry = entry.map_err(status)?;
            // Wie ein echter Server: Die Liste nennt die Einträge selbst, ohne Links zu folgen.
            let attributes = self.own_attributes(&entry.path())?;
            files.push(File::new(entry.file_name().to_string_lossy(), attributes));
        }

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
            Some(Open::Made(0)) => Err(StatusCode::Eof),
            Some(Open::Made(left)) => {
                let count = (*left).min(READDIR_CHUNK as u64);
                let files = (*left - count..*left)
                    .map(|number| File::new(format!("entry-{number}"), made_attributes()))
                    .collect();
                *left -= count;
                Ok(Name { id, files })
            }
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
        let local = self.local(&filename);
        if is_pipe(&local) && local.is_file() {
            // Wie ein echter Server: Er bleibt im Öffnen der Pipe stehen, bis jemand am anderen Ende ist – hier
            // also für immer. Antworten auf spätere Anfragen kommen dann auch nicht mehr.
            return std::future::pending().await;
        }

        let file = OpenOptions::new()
            .read(flags.contains(OpenFlags::READ))
            .write(flags.contains(OpenFlags::WRITE))
            .append(flags.contains(OpenFlags::APPEND))
            .create(flags.contains(OpenFlags::CREATE))
            .truncate(flags.contains(OpenFlags::TRUNCATE))
            .open(&local)
            .map_err(status)?;
        let handle = self.remember(Open::File(file));
        if is_slow(&local) {
            self.slow.insert(handle.clone());
        }

        Ok(Handle { id, handle })
    }

    async fn read(&mut self, id: u32, handle: String, offset: u64, length: u32) -> Result<Data, Self::Error> {
        if self.slow.contains(&handle) {
            tokio::time::sleep(SLOW_READ).await;
        }

        let Some(Open::File(file)) = self.handles.get_mut(&handle) else {
            return Err(StatusCode::Failure);
        };

        let mut data = vec![0; length as usize];
        file.seek(SeekFrom::Start(offset)).map_err(status)?;
        let read = file.read(&mut data).map_err(status)?;
        if read == 0 {
            return Err(StatusCode::Eof);
        }

        data.truncate(read);
        Ok(Data { id, data })
    }

    async fn write(&mut self, id: u32, handle: String, offset: u64, data: Vec<u8>) -> Result<Status, Self::Error> {
        let Some(Open::File(file)) = self.handles.get_mut(&handle) else {
            return Err(StatusCode::Failure);
        };

        file.seek(SeekFrom::Start(offset)).map_err(status)?;
        file.write_all(&data).map_err(status)?;
        Ok(ok(id))
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        self.handles.remove(&handle);
        self.slow.remove(&handle);
        Ok(ok(id))
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.attributes(&self.local(&path))?,
        })
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.own_attributes(&self.local(&path))?,
        })
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        match self.handles.get(&handle) {
            Some(Open::File(file)) => Ok(Attrs {
                id,
                attrs: portable(&file.metadata().map_err(status)?),
            }),
            _ => Err(StatusCode::Failure),
        }
    }

    async fn setstat(&mut self, id: u32, path: String, attributes: FileAttributes) -> Result<Status, Self::Error> {
        let local = self.local(&path);
        fs::metadata(&local).map_err(status)?;
        if let Some(mode) = attributes.permissions {
            self.modes.insert(local.clone(), mode & 0o7777);
        }

        if let (Some(uid), Some(gid)) = (attributes.uid, attributes.gid) {
            self.owners.insert(local, (uid, gid));
        }

        Ok(ok(id))
    }

    async fn fsetstat(&mut self, id: u32, _handle: String, _attributes: FileAttributes) -> Result<Status, Self::Error> {
        Ok(ok(id))
    }

    async fn mkdir(&mut self, id: u32, path: String, _attributes: FileAttributes) -> Result<Status, Self::Error> {
        fs::create_dir(self.local(&path)).map_err(status)?;
        Ok(ok(id))
    }

    async fn rmdir(&mut self, id: u32, path: String) -> Result<Status, Self::Error> {
        fs::remove_dir(self.local(&path)).map_err(status)?;
        Ok(ok(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        fs::remove_file(self.local(&filename)).map_err(status)?;
        Ok(ok(id))
    }

    /// Wohin ein Link zeigt, wörtlich wie im Link (auch ein absolutes Ziel bleibt, wie es dasteht). Etwas, das kein
    /// Link ist, scheitert – die vorgespielten Links von `symlink` unten sind keine.
    async fn readlink(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let target = fs::read_link(self.local(&path)).map_err(status)?;
        Ok(Name {
            id,
            files: vec![File::dummy(target.to_string_lossy())],
        })
    }

    /// Ein Link wird nur vorgespielt (unter Windows bräuchte ein echter besondere Rechte): eine leere Datei an
    /// seiner Stelle und eine Zeile in `symlinks.log`. Liegt im Wurzelverzeichnis eine Datei `openssh-order`,
    /// liest der Server die beiden Pfade vertauscht – wie OpenSSH.
    async fn symlink(&mut self, id: u32, linkpath: String, targetpath: String) -> Result<Status, Self::Error> {
        let (link, target) = if self.root.join("openssh-order").exists() {
            (targetpath, linkpath)
        } else {
            (linkpath, targetpath)
        };
        let local = self.local(&link);
        if local.exists() {
            return Err(StatusCode::Failure);
        }

        fs::write(&local, b"").map_err(status)?;
        let mut log = OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.root.join("symlinks.log"))
            .map_err(status)?;
        writeln!(log, "{link} -> {target}").map_err(status)?;
        Ok(ok(id))
    }

    async fn rename(&mut self, id: u32, old_path: String, new_path: String) -> Result<Status, Self::Error> {
        let target = self.local(&new_path);
        // Wie SFTP v3: Umbenennen überschreibt nicht.
        if target.exists() {
            return Err(StatusCode::Failure);
        }

        fs::rename(self.local(&old_path), target).map_err(status)?;
        Ok(ok(id))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_cannot_leave_the_root() {
        let sftp = Sftp::new(PathBuf::from("root"));
        assert_eq!(sftp.local("/"), PathBuf::from("root"));
        assert_eq!(sftp.local("/a/./b"), PathBuf::from("root").join("a").join("b"));
        assert_eq!(
            sftp.local("/../../etc/passwd"),
            PathBuf::from("root").join("etc").join("passwd")
        );
        assert_eq!(sftp.local("a/../.."), PathBuf::from("root"));
    }
}
