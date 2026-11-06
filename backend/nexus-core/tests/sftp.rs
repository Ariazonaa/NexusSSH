//! Dateibrowser und Transfers des Kerns gegen den SFTP-Teil von `nexus-testserver`: ein echtes Verzeichnis
//! auf der Serverseite, ein zweites als „lokaler Rechner“.

mod common;

use std::path::{Path, PathBuf};

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::{Options, Running};
use serde_json::{Value, json};

struct Files {
    fixture: Fixture,
    /// Erscheint dem Kern per SFTP als `/`.
    remote: PathBuf,
    /// Der lokale Rechner des Benutzers.
    local: PathBuf,
    next_request: std::cell::Cell<u64>,
}

fn write(path: impl AsRef<Path>, content: impl AsRef<[u8]>) {
    std::fs::create_dir_all(path.as_ref().parent().unwrap()).unwrap();
    std::fs::write(path, content).unwrap();
}

/// Inhalt, der sich nicht zufällig wiederholt – ein vertauschter Block fiele beim Vergleich auf.
fn pattern(length: usize) -> Vec<u8> {
    (0..length).map(|index| (index * 31 + index / 251) as u8).collect()
}

/// Kippt das erste Byte einer Datei und lässt ihre Größe. Steht die Marke nach einem neuen Versuch noch, wurde
/// die Datei nicht noch einmal übertragen.
fn mark(path: impl AsRef<Path>) {
    use std::io::{Read, Seek, SeekFrom, Write};

    let mut file = std::fs::OpenOptions::new().read(true).write(true).open(path).unwrap();
    let mut first = [0];
    file.read_exact(&mut first).unwrap();
    file.seek(SeekFrom::Start(0)).unwrap();
    file.write_all(&[first[0] ^ 0xff]).unwrap();
}

/// Ob die Datei `content` enthält – bis auf das erste Byte, das die Marke von [`mark`] trägt.
fn is_marked(path: impl AsRef<Path>, content: &[u8]) -> bool {
    let found = std::fs::read(path).unwrap();
    found.len() == content.len() && found[0] == content[0] ^ 0xff && found[1..] == content[1..]
}

/// Ersetzt eine Datei durch eine gleich große mit anderem Inhalt und späterer Änderungszeit – SFTP nennt die Zeit
/// nur auf die Sekunde, zwei Fassungen aus derselben Sekunde wären sonst nicht zu unterscheiden.
fn replace(path: impl AsRef<Path>, content: &[u8]) {
    std::fs::write(&path, content).unwrap();
    let later = std::time::SystemTime::now() + std::time::Duration::from_secs(10);
    std::fs::File::options()
        .write(true)
        .open(path)
        .unwrap()
        .set_modified(later)
        .unwrap();
}

/// Die Namen in einem Verzeichnis, sortiert.
fn names_in(directory: impl AsRef<Path>) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap())
        .collect();
    names.sort();
    names
}

impl Files {
    /// Verbundene Sitzung 1 mit SFTP.
    fn connected() -> Self {
        let fixture = Fixture::with(|directory| Options {
            sftp_root: Some(directory.join("remote")),
            ..with_password()
        });
        let remote = fixture.directory.join("remote");
        let local = fixture.directory.join("local");
        std::fs::create_dir_all(&remote).unwrap();
        std::fs::create_dir_all(&local).unwrap();
        write(remote.join("readme.txt"), "hello");
        write(remote.join("Zebra.log"), "z");
        write(remote.join("www").join("index.html"), "<h1>hi</h1>");
        write(remote.join("www").join("css").join("site.css"), "body {}");

        let profile = fixture.profile(json!({}));
        fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
        fixture.open(1, &profile);
        fixture.trust_host_key();
        fixture.recorder.state("authenticating");
        let connected = fixture.recorder.state("connected");
        assert_eq!(connected["home"], "/", "{connected}");

        Self {
            fixture,
            remote,
            local,
            next_request: std::cell::Cell::new(1),
        }
    }

    /// Schickt eine `sftp.*`-Anfrage und wartet auf ihr Ergebnis.
    fn sftp(&self, mut request: Value) -> Value {
        let number = self.next_request.get();
        self.next_request.set(number + 1);
        request["session"] = json!(1);
        request["request"] = json!(number);
        self.fixture.ok(request);
        self.fixture.recorder.find("sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == number
        })
    }

    fn done(&self, request: Value) {
        let result = self.sftp(request.clone());
        assert_eq!(result["ok"], true, "{request} -> {result}");
    }

    fn names(&self, path: &str) -> Vec<String> {
        let result = self.sftp(json!({ "op": "sftp.list", "path": path }));
        assert_eq!(result["ok"], true, "{result}");
        result["entries"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| entry["name"].as_str().unwrap().to_owned())
            .collect()
    }

    fn start(&self, transfer: u64, direction: &str, local: &Path, remote: &str) {
        self.fixture.ok(json!({
            "op": "transfer.start", "session": 1, "transfer": transfer,
            "direction": direction, "local": local, "remote": remote,
        }));
    }

    /// Ein Upload, den die App noch einmal versucht: `sent` Bytes hatte der abgebrochene Versuch geschickt.
    fn retry(&self, transfer: u64, local: &Path, remote: &str, sent: usize) {
        self.again(1, transfer, "upload", local, remote, sent as u64);
    }

    /// Ein Transfer, den die App noch einmal versucht – über die Sitzung, die gerade steht.
    fn again(&self, session: u64, transfer: u64, direction: &str, local: &Path, remote: &str, sent: u64) {
        self.fixture.ok(json!({
            "op": "transfer.start", "session": session, "transfer": transfer,
            "direction": direction, "local": local, "remote": remote, "resume": sent,
        }));
    }

    /// Der Server ist nach einem Ausfall wieder da: eine neue Verbindung als Sitzung 2. Er läuft, solange der
    /// Rückgabewert lebt.
    fn reconnect(&self) -> Running {
        self.reconnect_as(2)
    }

    /// Wie [`Files::reconnect`], als Sitzung mit dieser Nummer – jede Nummer nur einmal je Test: Die Ereignisse
    /// eines früheren Kerns stehen noch im Protokoll.
    fn reconnect_as(&self, session: u64) -> Running {
        let server = self.fixture.another_server(
            session as u8,
            Options {
                sftp_root: Some(self.remote.clone()),
                ..with_password()
            },
        );
        let profile = self
            .fixture
            .profile(json!({ "name": "wieder da", "port": server.address.port() }));
        self.fixture
            .secrets
            .set(&password_key(&profile), "demo", "geheim")
            .unwrap();
        self.fixture
            .ok(json!({ "op": "session.open", "session": session, "profile_id": profile, "cols": 80, "rows": 24 }));
        self.fixture.trust_host_key();
        self.fixture.recorder.find("neue Sitzung verbunden", |event| {
            event["event"] == "session.state" && event["session"] == session && event["state"] == "connected"
        });
        server
    }

    /// Wartet, bis ein Transfer mindestens so viele Bytes gemeldet hat.
    fn reached(&self, transfer: u64, bytes: usize) {
        self.fixture.recorder.find("so viele Bytes", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && event["bytes"].as_u64().is_some_and(|b| b >= bytes as u64)
        });
    }

    /// Bricht einen laufenden Transfer von Hand ab, sobald er `bytes` übertragen hat, und liefert, wie weit er kam.
    fn cancelled_at(&self, transfer: u64, bytes: usize) -> u64 {
        self.reached(transfer, bytes);
        self.fixture
            .ok(json!({ "op": "transfer.cancel", "transfer": transfer }));
        let end = self.finished(transfer);
        assert_eq!(end["state"], "cancelled", "{end}");
        end["bytes"].as_u64().unwrap()
    }

    /// Ab welchem Byte ein Transfer fortgesetzt wurde – `None`, wenn er von vorn begann.
    fn resumed(&self, transfer: u64) -> Option<u64> {
        self.fixture
            .recorder
            .all("transfer.state")
            .iter()
            .filter(|event| event["transfer"] == transfer)
            .find_map(|event| event["resumed"].as_u64())
    }

    /// Wartet auf das Ende eines Transfers und liefert dessen letztes Ereignis.
    fn finished(&self, transfer: u64) -> Value {
        self.fixture.recorder.find("Ende des Transfers", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        })
    }

    fn states(&self, transfer: u64) -> Vec<String> {
        self.fixture
            .recorder
            .all("transfer.state")
            .iter()
            .filter(|event| event["transfer"] == transfer)
            .map(|event| event["state"].as_str().unwrap().to_owned())
            .collect()
    }
}

#[test]
fn a_directory_is_listed_with_folders_first_and_names_ignoring_case() {
    let files = Files::connected();

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let entries = result["entries"].as_array().unwrap();

    let names: Vec<_> = entries.iter().map(|entry| entry["name"].as_str().unwrap()).collect();
    assert_eq!(names, ["www", "readme.txt", "Zebra.log"]);
    assert_eq!(entries[0]["kind"], "directory");
    assert_eq!(entries[1]["kind"], "file");
    assert_eq!(entries[1]["size"], 5);
    assert_eq!(entries[1]["link"], false);
    assert!(entries[1]["modified"].as_u64().unwrap() > 1_700_000_000);

    assert_eq!(files.names("/www"), ["css", "index.html"]);
}

#[test]
fn a_missing_directory_reports_not_found() {
    let files = Files::connected();

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/nope" }));

    assert_eq!(result["ok"], false);
    assert_eq!(result["error"], "not_found");
    assert!(result.get("entries").is_none());
}

#[test]
fn folders_can_be_created_renamed_and_deleted_with_their_content() {
    let files = Files::connected();

    files.done(json!({ "op": "sftp.mkdir", "path": "/backup" }));
    assert!(files.remote.join("backup").is_dir());

    files.done(json!({ "op": "sftp.rename", "from": "/backup", "to": "/archive" }));
    assert!(!files.remote.join("backup").exists());
    assert!(files.remote.join("archive").is_dir());

    files.done(json!({ "op": "sftp.rename", "from": "/readme.txt", "to": "/archive/readme.md" }));
    assert_eq!(
        std::fs::read_to_string(files.remote.join("archive").join("readme.md")).unwrap(),
        "hello"
    );

    // Löschen nimmt den ganzen Baum mit.
    files.done(json!({ "op": "sftp.delete", "path": "/www" }));
    assert!(!files.remote.join("www").exists());
    files.done(json!({ "op": "sftp.delete", "path": "/Zebra.log" }));
    assert_eq!(files.names("/"), ["archive"]);
}

#[test]
fn an_empty_file_is_created_and_an_existing_one_is_left_alone() {
    let files = Files::connected();

    files.done(json!({ "op": "sftp.touch", "path": "/notes.txt" }));
    assert_eq!(std::fs::read(files.remote.join("notes.txt")).unwrap(), b"");

    // Eine vorhandene Datei würde durch Anlegen geleert – sie bleibt, wie sie ist.
    let result = files.sftp(json!({ "op": "sftp.touch", "path": "/readme.txt" }));
    assert_eq!(result["ok"], false);
    assert_eq!(result["error"], "exists");
    assert_eq!(
        std::fs::read_to_string(files.remote.join("readme.txt")).unwrap(),
        "hello"
    );

    let missing = files.sftp(json!({ "op": "sftp.touch", "path": "/nope/notes.txt" }));
    assert_eq!(missing["ok"], false);
}

#[test]
fn a_link_is_created_whichever_order_of_paths_the_server_expects() {
    let files = Files::connected();
    let log = files.remote.join("symlinks.log");

    // Ein Server nach Spezifikation: Der Versuch in der Reihenfolge von OpenSSH scheitert am vorhandenen Ziel.
    files.done(json!({ "op": "sftp.symlink", "path": "/latest.txt", "to": "/readme.txt" }));
    assert_eq!(std::fs::read_to_string(&log).unwrap(), "/latest.txt -> /readme.txt\n");
    assert_eq!(
        std::fs::read_to_string(files.remote.join("readme.txt")).unwrap(),
        "hello"
    );

    // Ein Server, der die Pfade vertauscht liest – wie OpenSSH.
    std::fs::write(files.remote.join("openssh-order"), b"").unwrap();
    files.done(json!({ "op": "sftp.symlink", "path": "/current.txt", "to": "/readme.txt" }));
    assert_eq!(
        std::fs::read_to_string(&log).unwrap(),
        "/latest.txt -> /readme.txt\n/current.txt -> /readme.txt\n"
    );

    // Nichts Vorhandenes wird ersetzt, und ein Link ins Leere entsteht nicht.
    let taken = files.sftp(json!({ "op": "sftp.symlink", "path": "/latest.txt", "to": "/readme.txt" }));
    assert_eq!((&taken["ok"], &taken["error"]), (&json!(false), &json!("exists")));
    let missing = files.sftp(json!({ "op": "sftp.symlink", "path": "/other.txt", "to": "/nope.txt" }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found"))
    );
    assert!(!files.remote.join("other.txt").exists());
}

#[test]
fn key_files_are_listed_rekeyed_and_converted_over_the_protocol() {
    let files = Files::connected();
    let ssh = files.fixture.directory.join("ssh");
    let generated = files
        .fixture
        .ok(json!({ "op": "key.generate", "name": "id_nexus", "comment": "me@pc" }));
    let path = generated["path"].as_str().unwrap().to_owned();

    let listed = files.fixture.ok(json!({ "op": "key.list" }));
    let keys = listed["keys"].as_array().unwrap();
    assert_eq!(keys.len(), 1, "{listed}");
    assert_eq!(
        (
            &keys[0]["name"],
            &keys[0]["format"],
            &keys[0]["algorithm"],
            &keys[0]["bits"]
        ),
        (
            &json!("id_nexus"),
            &json!("openssh"),
            &json!("ssh-ed25519"),
            &json!(256)
        )
    );
    assert_eq!(
        (&keys[0]["encrypted"], &keys[0]["comment"], &keys[0]["public_key"]),
        (&json!(false), &json!("me@pc"), &generated["public_key"])
    );
    let fingerprint = keys[0]["fingerprint"].as_str().unwrap().to_owned();
    assert!(fingerprint.starts_with("SHA256:"), "{fingerprint}");

    // Eine Passphrase setzen; die falsche alte ändert nichts.
    files
        .fixture
        .ok(json!({ "op": "key.passphrase", "path": path, "new_passphrase": "eins" }));
    let wrong = files
        .fixture
        .request(json!({ "op": "key.passphrase", "path": path, "passphrase": "falsch", "new_passphrase": "zwei" }));
    assert_eq!(
        (&wrong["ok"], &wrong["error"]),
        (&json!(false), &json!("wrong_passphrase")),
        "{wrong}"
    );

    // Ins Format von PuTTY: eine neue Datei daneben, mit derselben Passphrase – und derselbe Schlüssel.
    let converted = files.fixture.ok(
        json!({ "op": "key.convert", "path": path, "passphrase": "eins", "format": "ppk", "name": "id_nexus.ppk" }),
    );
    assert_eq!(converted["path"], json!(ssh.join("id_nexus.ppk").display().to_string()));
    assert!(
        std::fs::read_to_string(ssh.join("id_nexus.ppk"))
            .unwrap()
            .starts_with("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\n")
    );
    let listed = files.fixture.ok(json!({ "op": "key.list" }));
    let keys = listed["keys"].as_array().unwrap();
    assert_eq!(
        keys.iter().map(|key| &key["name"]).collect::<Vec<_>>(),
        [&json!("id_nexus"), &json!("id_nexus.ppk")]
    );
    assert!(
        keys.iter()
            .all(|key| key["encrypted"] == json!(true) && key["fingerprint"] == json!(fingerprint)),
        "{listed}"
    );
    assert_eq!(keys[1]["format"], json!("ppk"));

    // Was nicht geht, sagt der Kern – und schreibt nichts.
    for (request, error) in [
        (
            json!({ "op": "key.convert", "path": path, "passphrase": "eins", "format": "ppk", "name": "id_nexus.ppk" }),
            "exists",
        ),
        (
            json!({ "op": "key.convert", "path": path, "passphrase": "eins", "format": "ppk", "name": "../x.ppk" }),
            "invalid_name",
        ),
        (
            json!({ "op": "key.convert", "path": path, "passphrase": "eins", "format": "openssh", "name": "copy" }),
            "same_format",
        ),
        (
            json!({ "op": "key.convert", "path": path, "format": "ppk", "name": "other.ppk" }),
            "wrong_passphrase",
        ),
        (
            json!({ "op": "key.passphrase", "path": ssh.join("missing").display().to_string() }),
            "unreadable",
        ),
    ] {
        let answer = files.fixture.request(request);
        assert_eq!(
            (&answer["ok"], &answer["error"]),
            (&json!(false), &json!(error)),
            "{answer}"
        );
    }
    assert_eq!(
        std::fs::read_dir(&ssh).unwrap().count(),
        3,
        "id_nexus, id_nexus.pub, id_nexus.ppk"
    );
}

#[test]
fn a_new_key_pair_lies_in_the_ssh_directory_and_its_public_half_goes_to_the_server() {
    let files = Files::connected();
    let ssh = files.fixture.directory.join("ssh");

    let generated = files
        .fixture
        .ok(json!({ "op": "key.generate", "name": "id_nexus", "comment": "it's me" }));
    let public_key = generated["public_key"].as_str().unwrap().to_owned();
    assert_eq!(generated["path"], json!(ssh.join("id_nexus").display().to_string()));
    assert!(
        public_key.starts_with("ssh-ed25519 ") && public_key.ends_with(" it's me"),
        "{public_key}"
    );
    assert!(
        std::fs::read_to_string(ssh.join("id_nexus"))
            .unwrap()
            .starts_with("-----BEGIN OPENSSH PRIVATE KEY-----")
    );

    // Ein zweites Mal entsteht nichts: Der Name ist vergeben.
    let again = files
        .fixture
        .request(json!({ "op": "key.generate", "name": "id_nexus" }));
    assert_eq!(
        (&again["ok"], &again["error"]),
        (&json!(false), &json!("exists")),
        "{again}"
    );

    // Hinterlegen trägt die Zeile ein – einmal, auch wenn es zweimal verlangt wird.
    let authorized = files.remote.join(".ssh").join("authorized_keys");
    files.done(json!({ "op": "key.install", "public_key": public_key }));
    files.done(json!({ "op": "key.install", "public_key": public_key }));
    assert_eq!(
        std::fs::read_to_string(&authorized).unwrap(),
        format!(
            "{public_key}
"
        )
    );

    // Was kein Schlüssel ist, erreicht den Server nicht.
    let refused = files.fixture.request(
        json!({ "op": "key.install", "session": 1, "request": 901, "public_key": "ssh-ed25519 AAAA; rm -rf ~" }),
    );
    assert_eq!(
        (&refused["ok"], &refused["error"]),
        (&json!(false), &json!("invalid_key")),
        "{refused}"
    );
    assert_eq!(std::fs::read_to_string(&authorized).unwrap().lines().count(), 1);
}

#[test]
fn a_search_finds_names_below_a_folder_whatever_their_case() {
    let files = Files::connected();
    let deep = files.remote.join("srv").join("app").join("conf");
    std::fs::create_dir_all(&deep).unwrap();
    std::fs::write(deep.join("Nginx.CONF"), "server {}").unwrap();
    std::fs::write(files.remote.join("srv").join("nginx-notes.txt"), "x").unwrap();
    std::fs::write(files.remote.join("srv").join("other.txt"), "x").unwrap();

    let result = files.sftp(json!({ "op": "sftp.find", "path": "/srv", "name": " nginx " }));

    assert_eq!(result["ok"], true, "{result}");
    let hits: Vec<(&str, bool, u64)> = result["tree"]
        .as_array()
        .unwrap()
        .iter()
        .map(|hit| {
            (
                hit["remote"].as_str().unwrap(),
                hit["directory"].as_bool().unwrap(),
                hit["size"].as_u64().unwrap(),
            )
        })
        .collect();
    // Ebene für Ebene: der nahe Treffer zuerst.
    assert_eq!(
        hits,
        [
            ("/srv/nginx-notes.txt", false, 1),
            ("/srv/app/conf/Nginx.CONF", false, 9)
        ]
    );

    // Auch Verzeichnisse sind Treffer; nichts gefunden ist kein Fehler.
    let folders = files.sftp(json!({ "op": "sftp.find", "path": "/", "name": "conf" }));
    assert!(
        folders["tree"]
            .as_array()
            .unwrap()
            .iter()
            .any(|hit| hit["remote"] == "/srv/app/conf" && hit["directory"] == true),
        "{folders}"
    );
    let none = files.sftp(json!({ "op": "sftp.find", "path": "/srv", "name": "gibt-es-nicht" }));
    assert_eq!(
        (&none["ok"], none["tree"].as_array().map(Vec::len)),
        (&json!(true), Some(0)),
        "{none}"
    );

    // Ohne Suchtext und in einem Verzeichnis, das es nicht gibt, sagt die Antwort, warum.
    let empty = files.sftp(json!({ "op": "sftp.find", "path": "/srv", "name": "  " }));
    assert_eq!(empty["ok"], false, "{empty}");
    let missing = files.sftp(json!({ "op": "sftp.find", "path": "/nichts", "name": "a" }));
    assert_eq!(missing["error"], "not_found", "{missing}");
}

#[test]
fn copying_moving_checksums_and_usage_run_as_commands_on_the_server() {
    let files = Files::connected();

    // Kopieren samt Unterordnern, an einen Ort mit eigenem Namen; das Original bleibt.
    files.done(json!({ "op": "sftp.copy", "pairs": [{ "from": "/www", "to": "/www (2)" }, { "from": "/readme.txt", "to": "/www/readme.txt" }] }));
    assert_eq!(files.names("/www (2)"), ["css", "index.html"]);
    assert_eq!(files.names("/www"), ["css", "index.html", "readme.txt"]);
    assert_eq!(
        std::fs::read_to_string(files.remote.join("www (2)").join("css").join("site.css")).unwrap(),
        "body {}"
    );

    // Verschieben: Am alten Ort ist danach nichts mehr.
    files.done(json!({ "op": "sftp.copy", "move": true, "pairs": [{ "from": "/www (2)", "to": "/www/copy" }] }));
    assert!(!files.remote.join("www (2)").exists());
    assert_eq!(files.names("/www/copy"), ["css", "index.html"]);

    // In sich selbst hinein, ohne vollen Pfad oder ohne Einträge: abgelehnt, bevor etwas läuft.
    for pairs in [
        json!([{ "from": "/www", "to": "/www/inner" }]),
        json!([{ "from": "www", "to": "/x" }]),
        json!([]),
    ] {
        let refused = files
            .fixture
            .request(json!({ "op": "sftp.copy", "session": 1, "request": 950, "pairs": pairs }));
        assert_eq!(refused["ok"], false, "{refused}");
    }

    // Was es nicht gibt, lässt sich nicht kopieren – der Fehler des Servers kommt als Meldung an.
    let missing = files.sftp(json!({ "op": "sftp.copy", "pairs": [{ "from": "/nichts", "to": "/x" }] }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("command_failed")),
        "{missing}"
    );

    // Prüfsummen: je Datei eine, in der Reihenfolge der Anfrage; für einen Ordner oder nichts gibt es keine.
    let sums =
        files.sftp(json!({ "op": "sftp.checksum", "paths": ["/readme.txt", "/www", "/nichts", "/www/readme.txt"] }));
    let hello = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
    assert_eq!(sums["sums"], json!([hello, null, null, hello]), "{sums}");

    // Belegung: die Zeilen von `du`, in Bytes.
    let usage = files.sftp(json!({ "op": "sftp.usage", "path": "/www" }));
    assert_eq!(
        usage["usage"],
        json!([
            { "path": "/www/else", "bytes": 12 * 1024 },
            { "path": "/www/.hidden", "bytes": 4 * 1024 },
            { "path": "/www", "bytes": 2048 * 1024 }
        ]),
        "{usage}"
    );
}

#[test]
fn packing_unpacking_and_the_size_run_as_commands_on_the_server() {
    let files = Files::connected();
    let log = || std::fs::read_to_string(files.remote.join("exec.log")).unwrap_or_default();

    // Der Testserver führt nichts aus, er merkt sich die Befehlszeile – sie ist, was hier geprüft wird.
    files.done(
        json!({ "op": "sftp.pack", "directory": "/www", "names": ["css", "index.html"], "archive": "site.tar.gz" }),
    );
    files.done(json!({ "op": "sftp.unpack", "directory": "/www", "archive": "site.tar.gz" }));
    assert_eq!(
        log().lines().collect::<Vec<_>>(),
        [
            "cd '/www' && if test -e './site.tar.gz'; then echo 'the archive already exists' >&2; exit 17; fi && tar -czf './site.tar.gz' -- './css' './index.html'",
            "cd '/www' && if test \"$(uname -s)\" = OpenBSD; then if gzip -dcf < './site.tar.gz' | pax | grep -qE '(^/|(^|/)[.][.](/|$))'; then echo 'the archive holds paths that would leave the folder' >&2; exit 18; fi; gzip -dcf < './site.tar.gz' | pax -rk; elif tar -tf './site.tar.gz' >/dev/null 2>&1; then tar -xkf './site.tar.gz'; else gzip -t < './site.tar.gz' && gzip -dc < './site.tar.gz' | tar -xkf -; fi",
        ]
    );

    // Scheitert der Befehl, ist seine Fehlerausgabe die Meldung.
    let failed = files.sftp(json!({ "op": "sftp.unpack", "directory": "/www", "archive": "fail.tar" }));
    assert_eq!(
        (&failed["ok"], &failed["error"]),
        (&json!(false), &json!("command_failed")),
        "{failed}"
    );
    assert!(failed["message"].as_str().unwrap().contains("Cannot open"), "{failed}");

    // Ein Name, der aus dem Verzeichnis führt, erreicht den Server gar nicht.
    let before = log().lines().count();
    let refused = files
        .fixture
        .request(json!({ "op": "sftp.pack", "session": 1, "request": 900, "directory": "/www", "names": ["../etc"], "archive": "x.tar.gz" }));
    assert_eq!(
        (&refused["ok"], &refused["error"]),
        (&json!(false), &json!("unsafe_name")),
        "{refused}"
    );
    assert_eq!(log().lines().count(), before);

    // Die Größe eines Ordners: die Summe aus der letzten Zeile von `du`, in Bytes.
    let size = files.sftp(json!({ "op": "sftp.size", "path": "/www" }));
    assert_eq!(
        (&size["ok"], &size["size"]),
        (&json!(true), &json!(2048 * 1024)),
        "{size}"
    );
    assert_eq!(log().lines().last().unwrap(), "du -sk '/www' 2>/dev/null || true");
    let relative = files
        .fixture
        .request(json!({ "op": "sftp.size", "session": 1, "request": 901, "path": "www" }));
    assert_eq!(relative["error"], "unsafe_name", "{relative}");
}

#[test]
fn renaming_never_overwrites_an_existing_target() {
    let files = Files::connected();

    let result = files.sftp(json!({ "op": "sftp.rename", "from": "/readme.txt", "to": "/Zebra.log" }));

    assert_eq!(result["ok"], false);
    assert_eq!(result["error"], "exists");
    assert_eq!(std::fs::read_to_string(files.remote.join("Zebra.log")).unwrap(), "z");
    assert!(files.remote.join("readme.txt").exists());
}

#[test]
fn operations_that_fail_say_why() {
    let files = Files::connected();

    let exists = files.sftp(json!({ "op": "sftp.mkdir", "path": "/www" }));
    assert_eq!(exists["ok"], false);

    let missing = files.sftp(json!({ "op": "sftp.delete", "path": "/nope" }));
    assert_eq!(missing["error"], "not_found");
    assert!(missing["message"].as_str().is_some_and(|message| !message.is_empty()));
}

#[test]
fn a_file_uploads_byte_for_byte_and_reports_its_progress() {
    let files = Files::connected();
    let content = pattern(3 * 1024 * 1024 + 17);
    write(files.local.join("backup.bin"), &content);

    files.start(1, "upload", &files.local.join("backup.bin"), "/www");
    let last = files.finished(1);

    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(last["bytes"], content.len());
    assert_eq!(last["total"], content.len());
    assert_eq!(
        std::fs::read(files.remote.join("www").join("backup.bin")).unwrap(),
        content
    );

    let states = files.states(1);
    assert_eq!(states.first().map(String::as_str), Some("queued"));
    assert!(states.contains(&"running".to_owned()));
    assert_eq!(files.names("/www"), ["css", "backup.bin", "index.html"]);
}

#[test]
fn uploading_replaces_an_existing_file_completely() {
    let files = Files::connected();
    write(files.remote.join("notes.txt"), "a much longer text than the new one");
    write(files.local.join("notes.txt"), "short");

    files.start(1, "upload", &files.local.join("notes.txt"), "/");

    assert_eq!(files.finished(1)["state"], "completed");
    assert_eq!(
        std::fs::read_to_string(files.remote.join("notes.txt")).unwrap(),
        "short"
    );
}

#[test]
fn a_folder_uploads_with_everything_in_it() {
    let files = Files::connected();
    write(files.local.join("site").join("index.html"), "root");
    write(files.local.join("site").join("img").join("logo.svg"), "<svg/>");
    write(files.local.join("site").join("img").join("deep").join("a.txt"), "a");
    std::fs::create_dir_all(files.local.join("site").join("empty")).unwrap();

    files.start(1, "upload", &files.local.join("site"), "/");
    let last = files.finished(1);

    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(last["total"], 4 + 6 + 1);
    let target = files.remote.join("site");
    assert_eq!(std::fs::read_to_string(target.join("index.html")).unwrap(), "root");
    assert_eq!(
        std::fs::read_to_string(target.join("img").join("logo.svg")).unwrap(),
        "<svg/>"
    );
    assert_eq!(
        std::fs::read_to_string(target.join("img").join("deep").join("a.txt")).unwrap(),
        "a"
    );
    assert!(target.join("empty").is_dir());
}

#[test]
fn a_file_downloads_byte_for_byte_and_leaves_no_partial_file() {
    let files = Files::connected();
    let content = pattern(2 * 1024 * 1024 + 3);
    write(files.remote.join("dump.sql"), &content);

    files.start(1, "download", &files.local, "/dump.sql");
    let last = files.finished(1);

    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(last["bytes"], content.len());
    assert_eq!(std::fs::read(files.local.join("dump.sql")).unwrap(), content);
    let left: Vec<_> = std::fs::read_dir(&files.local)
        .unwrap()
        .map(|entry| entry.unwrap().file_name())
        .collect();
    assert_eq!(left, ["dump.sql"]);
}

/// Eine langsame Leitung: Der Kern schickt Leseanfragen voraus, und die hinteren sind erst nach vielen Sekunden an
/// der Reihe. Mit der gewöhnlichen Frist von zehn Sekunden je Anfrage scheiterte der Download mitten in der Datei
/// (gefunden 2026-10-12 an einer auf 256 kbit/s gedrosselten Leitung).
#[test]
fn a_download_over_a_slow_line_outlasts_the_usual_time_limit_of_a_request() {
    let files = Files::connected();
    // Der Testserver braucht für jede Leseanfrage an eine Datei `.slow` 0,8 s und beantwortet sie der Reihe nach:
    // Was hinter sechzehn vorausgeschickten ansteht – auch das Schließen am Ende –, wartet über zehn Sekunden.
    let content = pattern(10 * 256 * 1024);
    write(files.remote.join("dump.slow"), &content);

    files.start(1, "download", &files.local, "/dump.slow");
    let last = files.finished(1);

    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(std::fs::read(files.local.join("dump.slow")).unwrap(), content);
}

#[test]
fn a_folder_downloads_with_everything_in_it() {
    let files = Files::connected();

    files.start(1, "download", &files.local, "/www");
    let last = files.finished(1);

    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(
        std::fs::read_to_string(files.local.join("www").join("index.html")).unwrap(),
        "<h1>hi</h1>"
    );
    assert_eq!(
        std::fs::read_to_string(files.local.join("www").join("css").join("site.css")).unwrap(),
        "body {}"
    );
}

#[test]
fn a_folder_transfer_limited_to_some_files_leaves_everything_else_alone() {
    let files = Files::connected();
    let mine = files.local.join("mine");
    write(mine.join("index.html"), "local version");
    write(mine.join("css").join("site.css"), "changed");
    write(mine.join("js").join("deep").join("app.js"), "js");
    write(mine.join("skip").join("me.txt"), "no");

    // Hinauf: der lokale Ordner „mine“ in den Ordner „www“ des Servers, aber nur zwei Dateien daraus.
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "upload",
        "local": mine, "remote": "/", "name": "www", "only": ["css/site.css", "js/deep/app.js"],
    }));
    let up = files.finished(1);

    assert_eq!(up["state"], "completed", "{up}");
    assert_eq!((&up["total"], &up["files"]), (&json!(7 + 2), &json!(2)), "{up}");
    let www = files.remote.join("www");
    assert_eq!(
        std::fs::read_to_string(www.join("css").join("site.css")).unwrap(),
        "changed"
    );
    assert_eq!(
        std::fs::read_to_string(www.join("js").join("deep").join("app.js")).unwrap(),
        "js"
    );
    assert_eq!(std::fs::read_to_string(www.join("index.html")).unwrap(), "<h1>hi</h1>");
    assert!(
        !www.join("skip").exists(),
        "ein Ordner ohne gewählte Datei entsteht nicht"
    );

    // Herunter: der Ordner „www“ des Servers in den lokalen Ordner „copy“, aber nur eine Datei daraus.
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 2, "direction": "download",
        "local": files.local, "remote": "/www", "name": "copy", "only": ["css/site.css"],
    }));
    let down = files.finished(2);

    assert_eq!(down["state"], "completed", "{down}");
    assert_eq!((&down["total"], &down["files"]), (&json!(7), &json!(1)), "{down}");
    let copy = files.local.join("copy");
    assert_eq!(
        std::fs::read_to_string(copy.join("css").join("site.css")).unwrap(),
        "changed"
    );
    assert!(!copy.join("index.html").exists());
    assert!(!copy.join("js").exists());
    assert!(
        !files.local.join("www").exists(),
        "der Name des Ordners auf dem Server zählt hier nicht"
    );

    // Der lokale Name ist ein Name, kein Pfad.
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 3, "direction": "download",
        "local": files.local, "remote": "/www", "name": "../outside", "only": ["css/site.css"],
    }));
    let refused = files.finished(3);
    assert_eq!(refused["state"], "failed", "{refused}");
    assert!(
        refused["message"].as_str().unwrap().starts_with("unsafe_name"),
        "{refused}"
    );
    assert!(!files.local.parent().unwrap().join("outside").exists());
}

#[test]
fn files_go_from_one_server_to_another_without_touching_this_computer() {
    let files = Files::connected();
    let other = files.fixture.directory.join("other");
    write(
        other.join("in").join("www").join("index.html"),
        "an older page that is longer",
    );
    let _server = {
        let server = files.fixture.another_server(
            2,
            Options {
                sftp_root: Some(other.clone()),
                ..with_password()
            },
        );
        let profile = files
            .fixture
            .profile(json!({ "name": "ziel", "port": server.address.port() }));
        files
            .fixture
            .secrets
            .set(&password_key(&profile), "demo", "geheim")
            .unwrap();
        files
            .fixture
            .ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24 }));
        files.fixture.trust_host_key();
        files.fixture.recorder.find("Ziel verbunden", |event| {
            event["event"] == "session.state" && event["session"] == 2 && event["state"] == "connected"
        });
        server
    };
    let relay = |transfer: u64, remote: &str, directory: &str| {
        files.fixture.request(json!({
            "op": "transfer.start", "session": 1, "transfer": transfer, "direction": "relay",
            "local": directory, "remote": remote, "target_session": 2,
        }))
    };

    // Ein Ordner: Alles darin kommt an, eine vorhandene Datei wird ersetzt, die Pipe bleibt aus.
    assert_eq!(relay(1, "/www", "/in")["ok"], true);
    let folder = files.finished(1);
    assert_eq!(folder["state"], "completed", "{folder}");
    let www = other.join("in").join("www");
    assert_eq!(std::fs::read_to_string(www.join("index.html")).unwrap(), "<h1>hi</h1>");
    assert_eq!(
        std::fs::read_to_string(www.join("css").join("site.css")).unwrap(),
        "body {}"
    );
    assert!(!www.join("events.fifo").exists());
    let left: Vec<_> = std::fs::read_dir(&www)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|name| name.contains(".part"))
        .collect();
    assert!(left.is_empty(), "{left:?}");

    // Eine einzelne Datei.
    assert_eq!(relay(2, "/readme.txt", "/")["ok"], true);
    let single = files.finished(2);
    assert_eq!(
        (&single["state"], &single["total"]),
        (&json!("completed"), &json!(5)),
        "{single}"
    );
    assert_eq!(std::fs::read_to_string(other.join("readme.txt")).unwrap(), "hello");

    // Auf diesem Rechner ist dabei nichts entstanden.
    assert_eq!(std::fs::read_dir(&files.local).unwrap().count(), 0);

    // Die Regeln des Benutzers gelten auch hier: Was am Ziel nicht älter ist, bleibt bei „nur Neueres“ stehen;
    // „Gleiches überspringen“ ersetzt es, weil es anders groß ist – und lässt es danach in Ruhe.
    let ruled = |transfer: u64, extra: serde_json::Value| {
        let mut request = json!({
            "op": "transfer.start", "session": 1, "transfer": transfer, "direction": "relay",
            "local": "/", "remote": "/readme.txt", "target_session": 2,
        });
        request
            .as_object_mut()
            .unwrap()
            .extend(extra.as_object().unwrap().clone());
        files.fixture.ok(request);
        files.finished(transfer)
    };
    write(other.join("readme.txt"), "changed at the target");
    assert_eq!(ruled(6, json!({ "keep": "newer" }))["state"], "completed");
    assert_eq!(
        std::fs::read_to_string(other.join("readme.txt")).unwrap(),
        "changed at the target"
    );
    assert_eq!(ruled(7, json!({ "keep": "same" }))["state"], "completed");
    assert_eq!(std::fs::read_to_string(other.join("readme.txt")).unwrap(), "hello");
    std::fs::write(other.join("readme.txt"), "HELLO").unwrap();
    assert_eq!(ruled(8, json!({ "keep": "same" }))["state"], "completed");
    assert_eq!(std::fs::read_to_string(other.join("readme.txt")).unwrap(), "HELLO");

    // „Umbenennen“: Das Neue kommt unter dem genannten Namen daneben an; ein Pfad ist kein Name.
    assert_eq!(ruled(9, json!({ "name": "readme (2).txt" }))["state"], "completed");
    assert_eq!(std::fs::read_to_string(other.join("readme (2).txt")).unwrap(), "hello");
    assert_eq!(std::fs::read_to_string(other.join("readme.txt")).unwrap(), "HELLO");
    let odd = ruled(10, json!({ "name": "in/readme.txt" }));
    assert_eq!(odd["state"], "failed", "{odd}");
    assert!(odd["message"].as_str().unwrap().starts_with("unsafe_name"), "{odd}");
    assert!(!other.join("in").join("readme.txt").exists());

    // Was es an der Quelle nicht gibt, scheitert mit dem Grund; ohne Ziel-Sitzung beginnt nichts.
    assert_eq!(relay(3, "/nope.bin", "/")["ok"], true);
    let missing = files.finished(3);
    assert_eq!(missing["state"], "failed", "{missing}");
    assert!(
        missing["message"].as_str().unwrap().starts_with("not_found"),
        "{missing}"
    );

    let refused = files.fixture.request(json!({
        "op": "transfer.start", "session": 1, "transfer": 4, "direction": "relay",
        "local": "/", "remote": "/readme.txt", "target_session": 99,
    }));
    assert_eq!(
        (&refused["ok"], &refused["error"]),
        (&json!(false), &json!("no_sftp")),
        "{refused}"
    );
    let untargeted = files.fixture.request(json!({
        "op": "transfer.start", "session": 1, "transfer": 5, "direction": "relay", "local": "/", "remote": "/readme.txt",
    }));
    assert_eq!(untargeted["ok"], false, "{untargeted}");
}

#[test]
fn a_transfer_of_something_missing_fails_with_the_reason() {
    let files = Files::connected();

    files.start(1, "download", &files.local, "/nope.bin");
    let download = files.finished(1);
    assert_eq!(download["state"], "failed");
    assert!(
        download["message"].as_str().unwrap().starts_with("not_found"),
        "{download}"
    );

    files.start(2, "upload", &files.local.join("nope.bin"), "/");
    let upload = files.finished(2);
    assert_eq!(upload["state"], "failed");
    assert!(
        upload["message"].as_str().unwrap().starts_with("local_not_found"),
        "{upload}"
    );
}

#[test]
fn a_waiting_transfer_can_be_moved_ahead_and_a_running_one_paused_alone() {
    // Zwei gebremste Uploads belegen beide Plätze, drei weitere Transfers warten. Der mittlere wird vorgezogen,
    // der letzte im Warten angehalten; dann wird einer der laufenden angehalten – und der vorgezogene rückt
    // nach, nicht der, der zuerst eingereiht wurde.
    let files = Files::connected();
    for name in ["a.bin", "b.bin"] {
        write(files.local.join(name), pattern(8 * 1024 * 1024));
    }
    for name in ["c.bin", "d.bin", "e.bin"] {
        write(files.remote.join(name), pattern(1024));
    }

    // Die Bremse hält die beiden großen lange genug am Laufen, ohne dass der Test darauf warten müsste.
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 256 * 1024 }));
    files.start(1, "upload", &files.local.join("a.bin"), "/");
    files.start(2, "upload", &files.local.join("b.bin"), "/");
    files.start(3, "download", &files.local, "/c.bin");
    files.start(4, "download", &files.local, "/d.bin");
    files.start(5, "download", &files.local, "/e.bin");
    let running = |transfer: u64| {
        files.fixture.recorder.find("läuft", |event| {
            event["event"] == "transfer.state" && event["transfer"] == transfer && event["state"] == "running"
        })
    };
    running(1);
    running(2);

    files
        .fixture
        .ok(json!({ "op": "transfer.move", "transfer": 4, "before": 3 }));
    let not_waiting = files
        .fixture
        .request(json!({ "op": "transfer.move", "transfer": 1, "before": 3 }));
    assert_eq!(not_waiting["error"], "not_waiting");
    let unknown = files
        .fixture
        .request(json!({ "op": "transfer.move", "transfer": 9, "before": 3 }));
    assert_eq!(unknown["error"], "unknown_transfer");

    // Ein wartender lässt sich anhalten, bevor er an der Reihe war: Er endet wie bei einer abgerissenen
    // Verbindung, ohne je gelaufen zu sein.
    files.fixture.ok(json!({ "op": "transfer.pause", "transfer": 5 }));
    let waited = files.finished(5);
    assert_eq!(waited["state"], "failed", "{waited}");
    assert_eq!(files.states(5), ["queued", "failed"]);

    // Ebenso ein laufender – und der andere laufende merkt davon nichts.
    files.fixture.ok(json!({ "op": "transfer.pause", "transfer": 1 }));
    let paused = files.finished(1);
    assert_eq!(paused["state"], "failed", "{paused}");
    assert!(
        paused["message"].as_str().unwrap().starts_with("connection"),
        "{paused}"
    );

    // Auf dem frei gewordenen Platz läuft erst der vorgezogene, dann der, der zuerst eingereiht wurde.
    assert_eq!(files.finished(4)["state"], "completed");
    assert_eq!(files.finished(3)["state"], "completed");
    let started: Vec<u64> = files
        .fixture
        .recorder
        .all("transfer.state")
        .iter()
        .filter(|event| event["state"] == "running")
        .filter_map(|event| event["transfer"].as_u64())
        .filter(|transfer| *transfer == 3 || *transfer == 4)
        .collect();
    assert_eq!(started.first(), Some(&4), "{started:?}");
    assert!(!files.states(2).iter().any(|state| state == "failed"));

    files.fixture.ok(json!({ "op": "transfer.cancel", "transfer": 2 }));
    assert_eq!(files.finished(2)["state"], "cancelled");
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 0 }));
}

#[test]
fn a_waiting_transfer_can_be_cancelled_before_it_starts() {
    let files = Files::connected();
    for name in ["a.bin", "b.bin", "c.bin"] {
        write(files.local.join(name), pattern(6 * 1024 * 1024));
    }

    // Zwei laufen gleichzeitig, der dritte wartet – und wird abgebrochen, bevor er an die Reihe kommt.
    files.start(1, "upload", &files.local.join("a.bin"), "/");
    files.start(2, "upload", &files.local.join("b.bin"), "/");
    files.start(3, "upload", &files.local.join("c.bin"), "/");
    files.fixture.ok(json!({ "op": "transfer.cancel", "transfer": 3 }));

    assert_eq!(files.finished(3)["state"], "cancelled");
    assert_eq!(files.states(3), ["queued", "cancelled"]);
    assert!(!files.remote.join("c.bin").exists());
    assert_eq!(files.finished(1)["state"], "completed");
    assert_eq!(files.finished(2)["state"], "completed");

    // Erledigte Transfers kennt der Kern nicht mehr.
    let late = files.fixture.request(json!({ "op": "transfer.cancel", "transfer": 1 }));
    assert_eq!(late["error"], "unknown_transfer");
}

#[test]
fn a_cancelled_download_keeps_what_arrived_and_a_retry_continues() {
    let files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.remote.join("big.bin"), &content);

    files.start(1, "download", &files.local, "/big.bin");
    let loaded = files.cancelled_at(1, 4 * MIB);

    // Kein halbes Ziel – aber die Teil-Datei bleibt, solange die App den Transfer zeigt.
    assert_eq!(names_in(&files.local), ["big.bin.nexus-part"]);
    let partial = files.local.join("big.bin.nexus-part");
    let left = std::fs::metadata(&partial).unwrap().len();
    assert!(left >= 4 * MIB as u64 && left < content.len() as u64, "{left}");
    mark(&partial);

    files.again(1, 2, "download", &files.local, "/big.bin", loaded);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(left));
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der Anfang wurde noch einmal geladen"
    );
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn a_cancelled_download_that_is_not_retried_leaves_nothing_behind() {
    let files = Files::connected();
    write(files.remote.join("big.bin"), pattern(48 * MIB));
    files.start(1, "download", &files.local, "/big.bin");
    files.cancelled_at(1, 4 * MIB);
    assert_eq!(names_in(&files.local), ["big.bin.nexus-part"]);

    // Nimmt der Benutzer die Zeile aus der Liste, ist die Teil-Datei nur noch Müll.
    files.fixture.ok(json!({ "op": "transfer.retain", "jobs": [] }));
    assert_eq!(names_in(&files.local), Vec::<String>::new());

    // Ebenso, wenn er denselben Download neu beginnt statt ihn zu wiederholen.
    files.start(2, "download", &files.local, "/big.bin");
    files.cancelled_at(2, 4 * MIB);
    files.start(3, "download", &files.local, "/big.bin");
    assert_eq!(files.finished(3)["state"], "completed");
    assert_eq!(files.resumed(3), None);
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn closing_the_session_ends_its_transfers() {
    let files = Files::connected();
    write(files.remote.join("big.bin"), pattern(48 * 1024 * 1024));

    files.start(1, "download", &files.local, "/big.bin");
    files.fixture.recorder.find("läuft", |event| {
        event["event"] == "transfer.state" && event["transfer"] == 1 && event["state"] == "running"
    });
    files.fixture.ok(json!({ "op": "session.close", "session": 1 }));

    // Der Benutzer hat die Sitzung geschlossen: Das ist ein Abbruch, kein Fehler der Verbindung – auch wenn die
    // Datei, die gerade lief, es so erlebt. Ein halbes Ziel gibt es nicht; liegen bleibt höchstens die Teil-Datei,
    // mit der ein neuer Versuch weitermacht.
    let last = files.finished(1);
    assert_eq!(last["state"], "cancelled", "{last}");
    assert!(
        names_in(&files.local).iter().all(|name| name == "big.bin.nexus-part"),
        "{:?}",
        names_in(&files.local)
    );

    let after = files
        .fixture
        .request(json!({ "op": "sftp.list", "session": 1, "request": 99, "path": "/" }));
    assert_eq!(after["error"], "no_sftp");
}

#[test]
fn a_server_without_sftp_still_gives_a_shell_but_no_files() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");

    let connected = fixture.recorder.state("connected");
    assert!(connected.get("home").is_none(), "{connected}");
    fixture.recorder.output(1, "$ ");

    let list = fixture.request(json!({ "op": "sftp.list", "session": 1, "request": 1, "path": "/" }));
    assert_eq!(list["error"], "no_sftp");
    let transfer = fixture.request(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "upload", "local": "x", "remote": "/",
    }));
    assert_eq!(transfer["error"], "no_sftp");
}

#[test]
fn the_shell_keeps_working_next_to_file_operations() {
    let files = Files::connected();
    files.fixture.recorder.output(1, "$ ");

    assert!(files.fixture.core.write(1, b"echo neben sftp\r"));
    assert_eq!(files.names("/www"), ["css", "index.html"]);

    assert!(files.fixture.recorder.output(1, "\r\n$ ").contains("neben sftp"));
}

#[test]
fn entries_carry_permissions_and_the_names_of_owner_and_group() {
    let files = Files::connected();
    // Der Testserver nennt für alles Besitzer und Gruppe 1000; die Namen dazu stehen wie auf einem echten Server
    // in /etc/passwd und /etc/group.
    write(
        files.remote.join("etc").join("passwd"),
        "root:x:0:0:root:/root:/bin/bash\n# Kommentar\ndemo:x:1000:1000:Demo,,,:/home/demo:/bin/sh\nkaputt\n",
    );
    write(files.remote.join("etc").join("group"), "root:x:0:\nstaff:x:1000:demo\n");

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let entries = result["entries"].as_array().unwrap();
    let entry = |name: &str| entries.iter().find(|entry| entry["name"] == name).unwrap().clone();

    let folder = entry("www");
    assert_eq!(folder["permissions"], 0o755);
    assert_eq!(folder["owner"], "demo");
    assert_eq!(folder["group"], "staff");

    let file = entry("readme.txt");
    assert_eq!(
        file["permissions"], 0o644,
        "nur die Rechte, ohne die Bits für die Art der Datei"
    );
    assert_eq!(file["owner"], "demo");
}

#[test]
fn permissions_can_be_changed_like_with_chmod() {
    let files = Files::connected();
    let permissions = |path: &str, name: &str| {
        let result = files.sftp(json!({ "op": "sftp.list", "path": path }));
        result["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|entry| entry["name"] == name)
            .unwrap()["permissions"]
            .clone()
    };
    assert_eq!(permissions("/", "readme.txt"), 0o644);

    files.done(json!({ "op": "sftp.chmod", "path": "/readme.txt", "mode": 0o600 }));
    assert_eq!(permissions("/", "readme.txt"), 0o600);
    assert_eq!(permissions("/", "Zebra.log"), 0o644, "nur die genannte Datei");

    // Auch Ordner, auch setuid/setgid/sticky – aber nichts oberhalb der zwölf Rechte-Bits: Die Art der Datei
    // lässt sich so nicht umschreiben.
    files.done(json!({ "op": "sftp.chmod", "path": "/www", "mode": 0o100_000 | 0o2775 }));
    let result = files.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let folder = result["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == "www")
        .unwrap();
    assert_eq!(folder["permissions"], 0o2775);
    assert_eq!(folder["kind"], "directory");

    let missing = files.sftp(json!({ "op": "sftp.chmod", "path": "/gibt-es-nicht", "mode": 0o644 }));
    assert_eq!(missing["ok"], false);
    assert_eq!(missing["error"], "not_found");
}

#[test]
fn without_passwd_and_group_the_numbers_stand_in() {
    let files = Files::connected();

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/www" }));

    let entry = &result["entries"][0];
    assert_eq!(entry["owner"], "1000");
    assert_eq!(entry["group"], "1000");
}

#[test]
fn a_new_file_appears_only_when_its_upload_is_complete() {
    let files = Files::connected();
    write(files.local.join("big.bin"), pattern(48 * 1024 * 1024));

    files.start(1, "upload", &files.local.join("big.bin"), "/www");
    files.fixture.recorder.find("erste Daten", |event| {
        event["event"] == "transfer.state" && event["transfer"] == 1 && event["bytes"].as_u64().is_some_and(|b| b > 0)
    });

    // Unterwegs liegt nur die Teil-Datei auf dem Server.
    assert!(!files.remote.join("www").join("big.bin").exists());
    assert!(files.remote.join("www").join("big.bin.nexus-part").exists());

    files.fixture.ok(json!({ "op": "transfer.cancel", "transfer": 1 }));
    assert_eq!(files.finished(1)["state"], "cancelled");

    // Nach dem Abbruch bleibt nichts zurück – weder das Ziel noch die Teil-Datei. Der Server räumt kurz nach der
    // Meldung auf, deshalb einen Moment Geduld.
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    while files.remote.join("www").join("big.bin.nexus-part").exists() {
        assert!(
            std::time::Instant::now() < deadline,
            "die Teil-Datei ist liegen geblieben"
        );
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
    assert_eq!(files.names("/www"), ["css", "index.html"]);
}

#[test]
fn a_cancelled_upload_over_an_existing_file_does_not_remove_it() {
    let files = Files::connected();
    write(files.remote.join("big.bin"), "alt");
    write(files.local.join("big.bin"), pattern(48 * 1024 * 1024));

    files.start(1, "upload", &files.local.join("big.bin"), "/");
    files.fixture.recorder.find("erste Daten", |event| {
        event["event"] == "transfer.state" && event["transfer"] == 1 && event["bytes"].as_u64().is_some_and(|b| b > 0)
    });
    files.fixture.ok(json!({ "op": "transfer.cancel", "transfer": 1 }));
    assert_eq!(files.finished(1)["state"], "cancelled");

    // Eine vorhandene Datei wird an Ort und Stelle überschrieben (Besitzer und Rechte bleiben) – sie ist nach dem
    // Abbruch unvollständig, aber noch da, und es gibt keine Teil-Datei.
    assert!(files.remote.join("big.bin").exists());
    assert!(!files.remote.join("big.bin.nexus-part").exists());
}

const MIB: usize = 1024 * 1024;

#[test]
fn a_cancelled_upload_over_an_existing_file_continues_where_it_stopped() {
    let files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.remote.join("big.bin"), "alt");
    write(files.local.join("big.bin"), &content);

    files.start(1, "upload", &files.local.join("big.bin"), "/");
    files.fixture.recorder.find("erste Daten", |event| {
        event["event"] == "transfer.state" && event["transfer"] == 1 && event["bytes"].as_u64().is_some_and(|b| b > 0)
    });
    files.fixture.ok(json!({ "op": "transfer.cancel", "transfer": 1 }));
    let end = files.finished(1);
    assert_eq!(end["state"], "cancelled");
    let sent = end["bytes"].as_u64().unwrap();
    assert!(sent > 0 && sent < content.len() as u64, "{end}");

    files.retry(2, &files.local.join("big.bin"), "/", sent as usize);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    let from = files.resumed(2).expect("der zweite Versuch setzt fort");
    assert!(
        from > 0 && from <= sent,
        "fortgesetzt bei {from}, geschickt waren {sent}"
    );
    assert_eq!(end["bytes"], content.len());
    assert!(std::fs::read(files.remote.join("big.bin")).unwrap() == content);
    assert!(!files.remote.join("big.bin.nexus-part").exists());
}

#[test]
fn a_resumed_upload_leaves_alone_what_is_already_on_the_server() {
    let files = Files::connected();
    let content = pattern(5 * MIB);
    write(files.local.join("data.bin"), &content);
    // Der Anfang liegt schon drüben. Sein erstes Byte ist markiert: Bleibt die Marke stehen, wurde der Anfang
    // nicht noch einmal geschickt. Sie liegt weit vor dem Stück, das verglichen wird.
    let mut there = content[..3 * MIB].to_vec();
    there[0] ^= 0xff;
    write(files.remote.join("data.bin"), &there);

    files.retry(1, &files.local.join("data.bin"), "/", 3 * MIB);

    let end = files.finished(1);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(1), Some(3 * MIB as u64));
    // Der Fortschritt beginnt nicht bei null: Was schon drüben liegt, zählt mit.
    let first = files
        .fixture
        .recorder
        .find("erster Stand nach dem Fortsetzen", |event| {
            event["event"] == "transfer.state" && event["transfer"] == 1 && event["resumed"].is_u64()
        });
    assert!(first["bytes"].as_u64().unwrap() >= 3 * MIB as u64, "{first}");
    assert_eq!(first["total"], content.len());
    assert_eq!(end["bytes"], content.len());

    let arrived = std::fs::read(files.remote.join("data.bin")).unwrap();
    assert_eq!(arrived.len(), content.len());
    assert_eq!(arrived[0], content[0] ^ 0xff, "der Anfang wurde noch einmal geschickt");
    assert!(arrived[1..] == content[1..]);
}

#[test]
fn a_resumed_upload_continues_at_the_smaller_of_what_was_sent_and_what_arrived() {
    let files = Files::connected();
    let content = pattern(4 * MIB);
    write(files.local.join("a.bin"), &content);
    write(files.local.join("b.bin"), &content);
    write(files.local.join("c.bin"), &content);
    // a: Der Server hat mehr, als gezählt wurde (die letzten Pakete kamen noch an).
    write(files.remote.join("a.bin"), &content[..3 * MIB]);
    // b: Der Server hat weniger, als geschickt wurde (die letzten Pakete gingen verloren).
    write(files.remote.join("b.bin"), &content[..2 * MIB]);
    // c: weniger, als verglichen würde – dann wird eben alles verglichen, was da ist.
    write(files.remote.join("c.bin"), &content[..100 * 1024]);

    files.retry(1, &files.local.join("a.bin"), "/", 2 * MIB + 5);
    files.retry(2, &files.local.join("b.bin"), "/", 3 * MIB);
    files.retry(3, &files.local.join("c.bin"), "/", 4 * MIB);

    for transfer in 1..=3 {
        let end = files.finished(transfer);
        assert_eq!(end["state"], "completed", "{end}");
    }
    assert_eq!(files.resumed(1), Some(2 * MIB as u64 + 5));
    assert_eq!(files.resumed(2), Some(2 * MIB as u64));
    assert_eq!(files.resumed(3), Some(100 * 1024));
    for name in ["a.bin", "b.bin", "c.bin"] {
        assert!(std::fs::read(files.remote.join(name)).unwrap() == content, "{name}");
    }
}

#[test]
fn a_resumed_upload_starts_over_when_the_server_holds_something_else() {
    let files = Files::connected();
    let content = pattern(4 * MIB);
    for name in ["other.bin", "longer.bin", "hole.bin", "empty.bin"] {
        write(files.local.join(name), &content);
    }
    // Anderer Inhalt gleicher Länge – jemand hat die Datei inzwischen ersetzt.
    write(files.remote.join("other.bin"), vec![7u8; 2 * MIB]);
    // Länger als die lokale Datei: Das stammt nicht vom abgebrochenen Versuch.
    let mut longer = content.clone();
    longer.extend_from_slice(b"und noch mehr");
    write(files.remote.join("longer.bin"), &longer);
    // Ein Loch kurz vor dem Ende: Ein Paket ist nie angekommen, ein späteres schon.
    let mut hole = content[..3 * MIB].to_vec();
    hole[3 * MIB - 200_000..3 * MIB - 150_000].fill(0);
    write(files.remote.join("hole.bin"), &hole);
    // Nichts da, womit sich etwas vergleichen ließe.
    write(files.remote.join("empty.bin"), "");

    files.retry(1, &files.local.join("other.bin"), "/", 2 * MIB);
    files.retry(2, &files.local.join("longer.bin"), "/", 4 * MIB);
    files.retry(3, &files.local.join("hole.bin"), "/", 3 * MIB);
    files.retry(4, &files.local.join("empty.bin"), "/", 2 * MIB);

    for (transfer, name) in [(1, "other.bin"), (2, "longer.bin"), (3, "hole.bin"), (4, "empty.bin")] {
        let end = files.finished(transfer);
        assert_eq!(end["state"], "completed", "{name}: {end}");
        assert_eq!(files.resumed(transfer), None, "{name} wurde fortgesetzt");
        assert!(std::fs::read(files.remote.join(name)).unwrap() == content, "{name}");
    }
}

#[test]
fn without_the_word_of_the_app_an_upload_always_starts_from_the_beginning() {
    let files = Files::connected();
    let content = pattern(3 * MIB);
    write(files.local.join("data.bin"), &content);
    write(files.local.join("neu.bin"), &content);
    let mut there = content[..2 * MIB].to_vec();
    there[0] ^= 0xff;
    write(files.remote.join("data.bin"), &there);
    write(files.remote.join("neu.bin.nexus-part"), &there);

    // Dass der Anfang zufällig passt, heißt nicht, dass er von dieser Datei stammt.
    files.start(1, "upload", &files.local.join("data.bin"), "/");
    files.start(2, "upload", &files.local.join("neu.bin"), "/");

    for (transfer, name) in [(1, "data.bin"), (2, "neu.bin")] {
        assert_eq!(files.finished(transfer)["state"], "completed");
        assert_eq!(files.resumed(transfer), None);
        assert!(std::fs::read(files.remote.join(name)).unwrap() == content, "{name}");
    }
    assert!(!files.remote.join("neu.bin.nexus-part").exists());
}

#[test]
fn a_new_file_whose_upload_lost_the_connection_continues_in_what_was_left_behind() {
    let files = Files::connected();
    let content = pattern(5 * MIB);
    write(files.local.join("neu.bin"), &content);
    // Reißt die Verbindung, kann der Kern die Teil-Datei nicht mehr wegräumen – sie liegt noch da.
    let mut left = content[..3 * MIB].to_vec();
    left[0] ^= 0xff;
    write(files.remote.join("www").join("neu.bin.nexus-part"), &left);

    files.retry(1, &files.local.join("neu.bin"), "/www", 3 * MIB);

    let end = files.finished(1);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(1), Some(3 * MIB as u64));
    let arrived = std::fs::read(files.remote.join("www").join("neu.bin")).unwrap();
    assert_eq!(arrived[0], content[0] ^ 0xff, "der Anfang wurde noch einmal geschickt");
    assert!(arrived[1..] == content[1..]);
    assert!(!files.remote.join("www").join("neu.bin.nexus-part").exists());
}

#[test]
fn an_upload_whose_connection_broke_continues_over_a_new_connection() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.local.join("big.bin"), &content);

    files.start(1, "upload", &files.local.join("big.bin"), "/www");
    files.fixture.recorder.find("erste Daten", |event| {
        event["event"] == "transfer.state" && event["transfer"] == 1 && event["bytes"].as_u64().is_some_and(|b| b > 0)
    });
    // Geschickt ist nicht angekommen: Erst wenn der Server etwas in die Teil-Datei geschrieben hat, reißt die
    // Verbindung – sonst läge dort unter Last noch nichts, womit es weitergehen könnte.
    let partial = files.remote.join("www").join("big.bin.nexus-part");
    let patience = std::time::Instant::now() + std::time::Duration::from_secs(20);
    while std::fs::metadata(&partial).map_or(0, |file| file.len()) == 0 {
        assert!(std::time::Instant::now() < patience, "am Server kam nichts an");
        std::thread::sleep(std::time::Duration::from_millis(2));
    }
    files.fixture.cut_connections();
    let end = files.finished(1);
    assert_eq!(end["state"], "failed", "{end}");
    let sent = end["bytes"].as_u64().unwrap();
    // Wegräumen ging nicht mehr: Die Teil-Datei liegt noch auf dem Server, mit dem, was ankam.
    let arrived = std::fs::metadata(&partial).unwrap().len();
    assert!(arrived > 0 && arrived < content.len() as u64, "angekommen: {arrived}");

    // Der Server ist wieder da; die App versucht es über eine neue Verbindung noch einmal.
    let _server = files.reconnect();
    files.again(2, 2, "upload", &files.local.join("big.bin"), "/www", sent);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    let from = files.resumed(2).expect("der zweite Versuch setzt fort");
    assert!(
        from > 0 && from <= sent.min(arrived),
        "fortgesetzt bei {from}; geschickt {sent}, angekommen {arrived}"
    );
    assert!(std::fs::read(files.remote.join("www").join("big.bin")).unwrap() == content);
    assert!(!partial.exists());
}

#[test]
fn a_new_file_whose_cancelled_upload_left_nothing_behind_starts_from_the_beginning() {
    let files = Files::connected();
    let content = pattern(2 * MIB);
    write(files.local.join("neu.bin"), &content);

    files.retry(1, &files.local.join("neu.bin"), "/www", MIB);

    assert_eq!(files.finished(1)["state"], "completed");
    assert_eq!(files.resumed(1), None);
    assert!(std::fs::read(files.remote.join("www").join("neu.bin")).unwrap() == content);
}

#[test]
fn a_folder_the_core_has_not_tried_before_starts_from_the_beginning() {
    let files = Files::connected();
    let content = pattern(2 * MIB);
    write(files.local.join("ordner").join("data.bin"), &content);
    write(files.local.join("ordner").join("fertig.bin"), &content);
    // Was im Ziel liegt, sieht aus wie von einem abgebrochenen Versuch – der Kern weiß aber von keinem.
    let mut there = content[..MIB].to_vec();
    there[0] ^= 0xff;
    write(files.remote.join("ordner").join("data.bin"), &there);
    write(files.remote.join("ordner").join("fertig.bin"), &content);
    mark(files.remote.join("ordner").join("fertig.bin"));

    files.retry(1, &files.local.join("ordner"), "/", MIB);

    assert_eq!(files.finished(1)["state"], "completed");
    assert_eq!(files.resumed(1), None);
    for name in ["data.bin", "fertig.bin"] {
        assert!(
            std::fs::read(files.remote.join("ordner").join(name)).unwrap() == content,
            "{name}"
        );
    }
}

/// Ein Ordner aus `count` Dateien von 2 MiB; liefert deren Inhalt.
fn album(directory: &Path, count: usize) -> Vec<u8> {
    let content = pattern(2 * MIB);
    for file in 0..count {
        write(directory.join(format!("f-{file:02}.bin")), &content);
    }

    content
}

/// Bei so vielen Bytes sind von einem [`album`] mindestens drei Dateien vollständig übertragen: Höchstens acht
/// laufen zugleich, mehr als 16 MiB können also nicht in angefangenen Dateien stecken.
const SOME_OF_THE_ALBUM: usize = 20 * MIB;

#[test]
fn a_folder_uploaded_again_does_not_send_what_has_already_arrived() {
    let files = Files::connected();
    let source = files.local.join("album");
    let content = album(&source, 24);

    files.start(1, "upload", &source, "/");
    let sent = files.cancelled_at(1, SOME_OF_THE_ALBUM);

    // Der Abbruch von Hand räumt die angefangenen Dateien weg: Was im Ziel liegt, ist vollständig.
    let arrived = names_in(files.remote.join("album"));
    assert!(arrived.len() >= 3 && arrived.len() < 24, "{arrived:?}");
    assert!(arrived.iter().all(|name| !name.ends_with(".nexus-part")), "{arrived:?}");
    for name in &arrived {
        mark(files.remote.join("album").join(name));
    }

    files.again(1, 2, "upload", &source, "/", sent);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(end["resumed"], (arrived.len() * 2 * MIB) as u64, "{end}");
    assert_eq!(end["bytes"], 24 * 2 * MIB, "{end}");
    assert_eq!(end["total"], 24 * 2 * MIB, "{end}");
    for file in 0..24 {
        let name = format!("f-{file:02}.bin");
        let target = files.remote.join("album").join(&name);
        if arrived.contains(&name) {
            assert!(is_marked(&target, &content), "{name} wurde noch einmal geschickt");
        } else {
            assert!(std::fs::read(&target).unwrap() == content, "{name}");
        }
    }
}

#[test]
fn a_folder_uploaded_again_sends_what_is_no_longer_the_same_on_either_side() {
    let files = Files::connected();
    let source = files.local.join("album");
    let content = album(&source, 24);

    files.start(1, "upload", &source, "/");
    let sent = files.cancelled_at(1, SOME_OF_THE_ALBUM);
    let arrived = names_in(files.remote.join("album"));
    assert!(arrived.len() >= 3 && arrived.len() < 24, "{arrived:?}");
    for name in &arrived {
        mark(files.remote.join("album").join(name));
    }

    // Die erste Datei wurde hier inzwischen geändert, die zweite auf dem Server gekürzt, die dritte dort gelöscht.
    let newer: Vec<u8> = content.iter().map(|byte| byte.wrapping_add(1)).collect();
    replace(source.join(&arrived[0]), &newer);
    write(files.remote.join("album").join(&arrived[1]), &content[..MIB]);
    std::fs::remove_file(files.remote.join("album").join(&arrived[2])).unwrap();

    files.again(1, 2, "upload", &source, "/", sent);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(end["resumed"], ((arrived.len() - 3) * 2 * MIB) as u64, "{end}");
    let target = files.remote.join("album");
    assert!(std::fs::read(target.join(&arrived[0])).unwrap() == newer);
    assert!(std::fs::read(target.join(&arrived[1])).unwrap() == content);
    assert!(std::fs::read(target.join(&arrived[2])).unwrap() == content);
    for name in &arrived[3..] {
        assert!(is_marked(target.join(name), &content), "{name}");
    }
}

#[test]
fn without_the_word_of_the_app_a_folder_is_sent_completely() {
    let files = Files::connected();
    let source = files.local.join("album");
    let content = album(&source, 24);

    files.start(1, "upload", &source, "/");
    let sent = files.cancelled_at(1, SOME_OF_THE_ALBUM);
    let arrived = names_in(files.remote.join("album"));
    assert!(arrived.len() >= 3, "{arrived:?}");
    for name in &arrived {
        mark(files.remote.join("album").join(name));
    }

    // Kein neuer Versuch, sondern ein neuer Upload desselben Ordners (die Frage „überschreiben?“ ist gestellt und
    // beantwortet): Er schickt alles.
    files.start(2, "upload", &source, "/");
    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), None);
    for file in 0..24 {
        let name = format!("f-{file:02}.bin");
        assert!(
            std::fs::read(files.remote.join("album").join(&name)).unwrap() == content,
            "{name}"
        );
    }

    // Und was der abgebrochene Versuch geschafft hatte, ist damit vergessen – auch für einen, der sich später
    // als neuer Versuch ausgibt.
    for name in &arrived {
        mark(files.remote.join("album").join(name));
    }
    files.again(1, 3, "upload", &source, "/", sent);
    assert_eq!(files.finished(3)["state"], "completed");
    assert_eq!(files.resumed(3), None);
    assert!(std::fs::read(files.remote.join("album").join(&arrived[0])).unwrap() == content);
}

#[test]
fn a_file_of_a_folder_that_was_under_way_continues_where_it_stopped() {
    let files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.local.join("ordner").join("big.bin"), &content);
    write(files.local.join("ordner").join("klein.txt"), "klein");
    // Die Datei gibt es schon: Sie wird an Ort und Stelle überschrieben, ein Abbruch lässt sie halb zurück.
    write(files.remote.join("ordner").join("big.bin"), "alt");

    files.start(1, "upload", &files.local.join("ordner"), "/");
    let sent = files.cancelled_at(1, 4 * MIB);
    let there = std::fs::metadata(files.remote.join("ordner").join("big.bin"))
        .unwrap()
        .len();
    assert!(there > 0 && there < content.len() as u64, "{there}");
    mark(files.remote.join("ordner").join("big.bin"));

    files.again(1, 2, "upload", &files.local.join("ordner"), "/", sent);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    let from = end["resumed"].as_u64().expect("der zweite Versuch setzt fort");
    // Weit genug, dass die Marke am Anfang nicht in dem Stück liegt, das vor dem Fortsetzen verglichen wird.
    assert!(
        from > 2 * MIB as u64 && from <= sent,
        "fortgesetzt ab {from} von {sent}"
    );
    assert!(
        is_marked(files.remote.join("ordner").join("big.bin"), &content),
        "der Anfang wurde noch einmal geschickt"
    );
    assert_eq!(
        std::fs::read_to_string(files.remote.join("ordner").join("klein.txt")).unwrap(),
        "klein"
    );
}

#[test]
fn a_folder_downloaded_again_does_not_load_what_is_already_here() {
    let files = Files::connected();
    let content = album(&files.remote.join("album"), 24);
    let target = files.local.join("out");
    std::fs::create_dir_all(&target).unwrap();

    files.start(1, "download", &target, "/album");
    let loaded = files.cancelled_at(1, SOME_OF_THE_ALBUM);

    // Fertig sind einige; die, die gerade liefen, haben ihre Teil-Datei behalten.
    let (partials, arrived): (Vec<String>, Vec<String>) = names_in(target.join("album"))
        .into_iter()
        .partition(|name| name.ends_with(".nexus-part"));
    assert!(arrived.len() >= 3 && arrived.len() < 24, "{arrived:?}");
    for name in &arrived {
        mark(target.join("album").join(name));
    }
    // Teil-Dateien, in denen schon etwas steht, werden fortgesetzt – auch ihre Marke muss am Ende noch stehen.
    let mut partly = 0;
    let mut continued = Vec::new();
    for name in &partials {
        let length = std::fs::metadata(target.join("album").join(name)).unwrap().len();
        if length > 0 {
            mark(target.join("album").join(name));
            partly += length;
            continued.push(name.trim_end_matches(".nexus-part").to_owned());
        }
    }

    // Eine der Dateien ist auf dem Server inzwischen eine andere – gleich groß, aber später geändert.
    let newer: Vec<u8> = content.iter().map(|byte| byte.wrapping_add(1)).collect();
    replace(files.remote.join("album").join(&arrived[0]), &newer);
    // Und eine fehlt hier wieder.
    std::fs::remove_file(target.join("album").join(&arrived[1])).unwrap();

    files.again(1, 2, "download", &target, "/album", loaded);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(end["resumed"], ((arrived.len() - 2) * 2 * MIB) as u64 + partly, "{end}");
    assert_eq!(end["bytes"], 24 * 2 * MIB, "{end}");
    assert!(std::fs::read(target.join("album").join(&arrived[0])).unwrap() == newer);
    assert!(std::fs::read(target.join("album").join(&arrived[1])).unwrap() == content);
    assert!(
        names_in(target.join("album"))
            .iter()
            .all(|name| !name.ends_with(".nexus-part"))
    );
    for file in 0..24 {
        let name = format!("f-{file:02}.bin");
        if arrived[2..].contains(&name) || continued.contains(&name) {
            assert!(
                is_marked(target.join("album").join(&name), &content),
                "{name} wurde noch einmal geladen"
            );
        } else if !arrived.contains(&name) {
            assert!(
                std::fs::read(target.join("album").join(&name)).unwrap() == content,
                "{name}"
            );
        }
    }
}

/// Lädt `/big.bin` (48 MiB), bis die Verbindung reißt, und verbindet neu. Liefert, was der Kern liegen ließ.
fn download_cut_off(files: &mut Files, content: &[u8]) -> (u64, PathBuf, Running) {
    write(files.remote.join("big.bin"), content);
    files.start(1, "download", &files.local, "/big.bin");
    files.reached(1, 4 * MIB);
    files.fixture.cut_connections();
    let end = files.finished(1);
    assert_eq!(end["state"], "failed", "{end}");
    // Meist merkt der Kern den Abriss sofort; bleibt die Leitung nur stumm, wartet er vergeblich auf eine Antwort.
    let message = end["message"].as_str().unwrap();
    assert!(
        message.starts_with("connection") || message.starts_with("timeout"),
        "{end}"
    );

    // Anders als beim Abbruch von Hand bleibt die Teil-Datei liegen – mit allem, was ankam.
    let partial = files.local.join("big.bin.nexus-part");
    let left = std::fs::metadata(&partial).unwrap().len();
    assert_eq!(left, end["bytes"].as_u64().unwrap(), "{end}");
    assert!(left >= 4 * MIB as u64 && left < content.len() as u64, "{left}");
    assert!(!files.local.join("big.bin").exists());
    (left, partial, files.reconnect())
}

#[test]
fn a_download_whose_connection_broke_continues_in_what_was_left_behind() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (left, partial, _server) = download_cut_off(&mut files, &content);
    mark(&partial);

    files.again(2, 2, "download", &files.local, "/big.bin", left);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(left));
    assert_eq!(end["bytes"], content.len());
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der Anfang wurde noch einmal geladen"
    );
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn a_download_starts_over_when_the_file_on_the_server_is_no_longer_the_same() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (left, _, _server) = download_cut_off(&mut files, &content);
    let newer: Vec<u8> = content.iter().map(|byte| byte.wrapping_add(1)).collect();
    replace(files.remote.join("big.bin"), &newer);

    files.again(2, 2, "download", &files.local, "/big.bin", left);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), None);
    assert!(std::fs::read(files.local.join("big.bin")).unwrap() == newer);
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn without_the_word_of_the_app_a_download_starts_from_the_beginning() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (_, partial, _server) = download_cut_off(&mut files, &content);
    mark(&partial);

    files.fixture.ok(json!({
        "op": "transfer.start", "session": 2, "transfer": 2, "direction": "download",
        "local": files.local, "remote": "/big.bin",
    }));

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), None);
    assert!(std::fs::read(files.local.join("big.bin")).unwrap() == content);
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn a_broken_download_continues_after_the_app_was_closed_and_opened_again() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (left, partial, _server) = download_cut_off(&mut files, &content);
    mark(&partial);

    // Die App wird geschlossen und wieder geöffnet: Was der Versuch geschafft hat, steht im Datenordner.
    files.fixture.restart_core();
    assert_eq!(std::fs::metadata(&partial).unwrap().len(), left);
    let _again = files.reconnect_as(3);
    files.again(3, 2, "download", &files.local, "/big.bin", left);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(left));
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der Anfang wurde noch einmal geladen"
    );
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn a_folder_upload_skips_what_arrived_even_after_the_app_was_closed_and_opened_again() {
    let mut files = Files::connected();
    let source = files.local.join("album");
    let content = album(&source, 24);
    files.start(1, "upload", &source, "/");
    let sent = files.cancelled_at(1, SOME_OF_THE_ALBUM);
    let arrived = names_in(files.remote.join("album"));
    assert!(arrived.len() >= 3 && arrived.len() < 24, "{arrived:?}");
    for name in &arrived {
        mark(files.remote.join("album").join(name));
    }

    files.fixture.restart_core();
    let _server = files.reconnect_as(3);
    files.again(3, 2, "upload", &source, "/", sent);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(end["resumed"], (arrived.len() * 2 * MIB) as u64, "{end}");
    for file in 0..24 {
        let name = format!("f-{file:02}.bin");
        let target = files.remote.join("album").join(&name);
        if arrived.contains(&name) {
            assert!(is_marked(&target, &content), "{name} wurde noch einmal geschickt");
        } else {
            assert!(std::fs::read(&target).unwrap() == content, "{name}");
        }
    }
}

#[test]
fn a_download_that_was_running_when_the_app_closed_continues_afterwards() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.remote.join("big.bin"), &content);
    files.start(1, "download", &files.local, "/big.bin");
    files.reached(1, 4 * MIB);

    // Niemand hat abgebrochen, und die Verbindung stand: Die App wurde einfach geschlossen.
    files.fixture.restart_core();
    let partial = files.local.join("big.bin.nexus-part");
    let left = std::fs::metadata(&partial).unwrap().len();
    assert!(left >= 4 * MIB as u64 && left < content.len() as u64, "{left}");
    mark(&partial);

    let _server = files.reconnect_as(3);
    files.again(3, 2, "download", &files.local, "/big.bin", left);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(left));
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der Anfang wurde noch einmal geladen"
    );
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn a_transfer_the_app_suspends_before_closing_its_tab_continues_afterwards() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.remote.join("big.bin"), &content);
    files.start(1, "download", &files.local, "/big.bin");
    files.reached(1, 4 * MIB);

    // So endet die App: erst die Transfers anhalten, dann die Tabs schließen. Das Schließen allein wäre ein
    // Abbruch und räumte die Teil-Datei weg.
    files.fixture.ok(json!({ "op": "transfer.suspend" }));
    files.fixture.ok(json!({ "op": "session.close", "session": 1 }));
    let end = files.finished(1);
    assert_eq!(end["state"], "failed", "{end}");
    assert!(end["message"].as_str().unwrap().starts_with("connection"), "{end}");

    files.fixture.restart_core();
    let partial = files.local.join("big.bin.nexus-part");
    let left = std::fs::metadata(&partial).unwrap().len();
    assert!(left >= 4 * MIB as u64 && left < content.len() as u64, "{left}");
    mark(&partial);

    let _server = files.reconnect_as(3);
    files.again(3, 2, "download", &files.local, "/big.bin", left);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(left));
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der Anfang wurde noch einmal geladen"
    );
}

/// Was ein Kern hinterlässt, der mitten in einem Download abgestürzt ist: eine Teil-Datei, deren Anfang stimmt
/// (die ersten 20 MiB, das erste Byte markiert), dahinter Müll – und in `trails.json`, bis wohin sie sicher auf der
/// Platte stand. Danach startet ein neuer Kern über demselben Datenordner.
fn crashed_download(files: &mut Files, content: &[u8]) -> (PathBuf, u64) {
    let safe = 20 * MIB;
    let remote = files.remote.join("big.bin");
    write(&remote, content);
    let partial = files.local.join("big.bin.nexus-part");
    let mut left = content[..safe].to_vec();
    left[0] ^= 0xff;
    left.extend(std::iter::repeat_n(0xee, 8 * MIB));
    write(&partial, &left);

    let modified = std::fs::metadata(&remote)
        .unwrap()
        .modified()
        .unwrap()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs();
    let trails = json!({ "version": 1, "trails": [{
        "direction": "download", "local": files.local, "remote": "/big.bin", "name": null,
        "files": { "/big.bin": {
            "source": { "size": content.len(), "modified": { "secs": modified, "nanos": 0 } },
            "reached": { "writing": { "partial": partial, "safe": safe } },
        } },
    }] });
    write(
        files.fixture.directory.join("data").join("trails.json"),
        trails.to_string(),
    );
    files.fixture.restart_core();
    (partial, safe as u64)
}

#[test]
fn a_download_continues_after_a_crash_from_what_was_safely_on_disk() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (partial, safe) = crashed_download(&mut files, &content);
    assert!(partial.exists());

    let _server = files.reconnect_as(3);
    files.again(3, 2, "download", &files.local, "/big.bin", 1);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(safe));
    assert!(
        is_marked(files.local.join("big.bin"), &content),
        "der sichere Anfang wurde noch einmal geladen, oder der Müll dahinter blieb stehen"
    );
    assert_eq!(names_in(&files.local), ["big.bin"]);
}

#[test]
fn what_a_crashed_download_left_behind_goes_away_when_the_app_no_longer_lists_it() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (partial, _) = crashed_download(&mut files, &content);
    write(files.local.join("bleibt.txt"), "bleibt");

    files.fixture.ok(json!({ "op": "transfer.retain", "jobs": [] }));

    assert!(!partial.exists());
    assert_eq!(names_in(&files.local), ["bleibt.txt"]);
}

#[test]
fn an_upload_continues_after_a_crash_from_what_the_server_has_and_the_trail_names() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let local = files.local.join("big.bin");
    write(&local, &content);

    // Was ein Kern hinterlässt, der mitten im Upload abgestürzt ist: auf dem Server die Teil-Datei mit dem, was
    // ankam (20 MiB, erstes Byte markiert), in `trails.json` die zuletzt mitgeschriebene Zahl – mehr, als ankam.
    let mut arrived = content[..20 * MIB].to_vec();
    arrived[0] ^= 0xff;
    write(files.remote.join("big.bin.nexus-part"), &arrived);
    let modified = std::fs::metadata(&local)
        .unwrap()
        .modified()
        .unwrap()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap();
    let source = local.to_string_lossy().into_owned();
    let trails = json!({ "version": 1, "trails": [{
        "direction": "upload", "local": local, "remote": "/", "name": null,
        "files": { source: {
            "source": { "size": content.len(), "modified": { "secs": modified.as_secs(), "nanos": modified.subsec_nanos() } },
            "reached": { "sent": 23 * MIB },
        } },
    }] });
    write(
        files.fixture.directory.join("data").join("trails.json"),
        trails.to_string(),
    );
    files.fixture.restart_core();

    // Die App weiß nach dem Absturz keine Zahl; sie sagt nur „baue darauf auf“.
    let _server = files.reconnect_as(3);
    files.again(3, 2, "upload", &local, "/", 1);

    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(2), Some(20 * MIB as u64));
    assert!(
        is_marked(files.remote.join("big.bin"), &content),
        "der Anfang wurde noch einmal geschickt"
    );
    assert_eq!(
        names_in(&files.remote)
            .iter()
            .filter(|name| name.starts_with("big"))
            .count(),
        1
    );
}

#[test]
fn a_folder_of_empty_files_counts_its_progress_in_files() {
    let files = Files::connected();
    for index in 0..30 {
        write(files.remote.join("leer").join(format!("{index:02}.txt")), "");
        write(files.local.join("hoch").join(format!("{index:02}.txt")), "");
    }
    let target = files.local.join("runter");
    std::fs::create_dir_all(&target).unwrap();

    files.start(1, "download", &target, "/leer");
    files.start(2, "upload", &files.local.join("hoch"), "/");
    for transfer in [1, 2] {
        let end = files.finished(transfer);
        assert_eq!(end["state"], "completed", "{end}");
        assert_eq!(
            (&end["total"], &end["files"], &end["files_done"]),
            (&json!(0), &json!(30), &json!(30)),
            "{end}"
        );

        // Unterwegs wächst die Zahl nur, und nie über die aller Dateien.
        let done: Vec<u64> = files
            .fixture
            .recorder
            .all("transfer.state")
            .iter()
            .filter(|event| event["transfer"] == transfer)
            .map(|event| event["files_done"].as_u64().unwrap())
            .collect();
        assert!(done.windows(2).all(|pair| pair[0] <= pair[1]), "{done:?}");
        assert!(done.iter().all(|count| *count <= 30), "{done:?}");
    }

    // Eine einzelne Datei ist eine Datei.
    write(files.remote.join("eine.txt"), "");
    files.start(3, "download", &files.local, "/eine.txt");
    let end = files.finished(3);
    assert_eq!((&end["files"], &end["files_done"]), (&json!(1), &json!(1)), "{end}");
}

#[test]
fn a_running_download_is_written_down_before_it_ends() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    write(files.remote.join("big.bin"), &content);
    files.start(1, "download", &files.local, "/big.bin");
    files.reached(1, 4 * MIB);

    // Was ein Absturz jetzt hinterließe: Die Spur nennt die Teil-Datei, schon bevor der Transfer endet.
    let saved = std::fs::read_to_string(files.fixture.directory.join("data").join("trails.json")).unwrap();
    let saved: Value = serde_json::from_str(&saved).unwrap();
    let writing = &saved["trails"][0]["files"]["/big.bin"]["reached"]["writing"];
    assert_eq!(
        Path::new(writing["partial"].as_str().unwrap()),
        files.local.join("big.bin.nexus-part"),
        "{saved}"
    );
    assert!(writing["safe"].as_u64().is_some(), "{saved}");

    assert_eq!(files.finished(1)["state"], "completed");
    files.fixture.restart_core();
    let saved = std::fs::read_to_string(files.fixture.directory.join("data").join("trails.json")).unwrap();
    assert_eq!(saved, r#"{"version":1,"trails":[]}"#);
}

#[test]
fn what_a_broken_download_left_behind_goes_away_when_the_app_no_longer_lists_the_transfer() {
    let mut files = Files::connected();
    let content = pattern(48 * MIB);
    let (left, partial, _server) = download_cut_off(&mut files, &content);
    write(files.local.join("bleibt.txt"), "bleibt");
    let job = json!({ "direction": "download", "local": files.local, "remote": "/big.bin" });
    let other = json!({ "direction": "upload", "local": files.local.join("anderes"), "remote": "/", "name": "x" });

    // Solange die App den Transfer noch zeigt, bleibt liegen, womit er weitergehen kann – auch über einen Neustart.
    files
        .fixture
        .ok(json!({ "op": "transfer.retain", "jobs": [other, job] }));
    assert!(partial.exists());
    files.fixture.restart_core();
    files.fixture.ok(json!({ "op": "transfer.retain", "jobs": [job] }));
    assert!(partial.exists());

    // Nimmt der Benutzer die Zeile aus der Liste, ist die Teil-Datei nur noch Müll.
    files.fixture.ok(json!({ "op": "transfer.retain", "jobs": [] }));
    assert_eq!(names_in(&files.local), ["bleibt.txt"]);

    // Und ein Transfer, der sich danach als neuer Versuch ausgibt, findet nichts mehr vor.
    let _again = files.reconnect_as(3);
    files.again(3, 2, "download", &files.local, "/big.bin", left);
    assert_eq!(files.finished(2)["state"], "completed");
    assert_eq!(files.resumed(2), None);
    assert!(std::fs::read(files.local.join("big.bin")).unwrap() == content);
}

#[cfg(unix)]
#[test]
fn a_directory_full_of_links_is_listed_completely() {
    let files = Files::connected();
    let directory = files.remote.join("links");
    std::fs::create_dir_all(&directory).unwrap();
    for index in 0..50 {
        std::os::unix::fs::symlink(files.remote.join("www"), directory.join(format!("ordner-{index:02}"))).unwrap();
    }
    std::os::unix::fs::symlink(files.remote.join("gibt-es-nicht"), directory.join("tot")).unwrap();

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/links" }));
    let entries = result["entries"].as_array().unwrap();

    assert_eq!(entries.len(), 51);
    assert!(entries.iter().all(|entry| entry["link"] == true));
    assert_eq!(
        entries.iter().filter(|entry| entry["kind"] == "directory").count(),
        50,
        "ein Link auf einen Ordner lässt sich betreten"
    );
    assert_eq!(entries.last().unwrap()["name"], "tot");
    assert_eq!(entries.last().unwrap()["kind"], "other");
}

/// Ein Ordner `/links` mit einer Datei, einem Unterordner und Links darauf in allen Arten: relativ, absolut, auf
/// den Ordner, ins Leere und im Kreis. Gibt den Pfad der Datei auf dem Rechner des Testservers zurück – so steht
/// er im absoluten Link.
#[cfg(unix)]
fn links_of_every_kind(files: &Files) -> PathBuf {
    let directory = files.remote.join("links");
    write(directory.join("datei"), "x");
    std::fs::create_dir_all(directory.join("sub")).unwrap();
    let file = directory.join("datei");
    let link = |target: &Path, name: &str| std::os::unix::fs::symlink(target, directory.join(name)).unwrap();
    link(Path::new("datei"), "rel");
    link(&file, "abs");
    link(Path::new("sub"), "ordner_link");
    link(Path::new("fehlt"), "tot");
    link(Path::new("kreis_b"), "kreis_a");
    link(Path::new("kreis_a"), "kreis_b");
    file
}

#[cfg(unix)]
#[test]
fn a_link_names_its_target_and_one_into_nothing_is_marked() {
    let files = Files::connected();
    let file = links_of_every_kind(&files);

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/links" }));
    let entries = result["entries"].as_array().unwrap();
    let by_name = |name: &str| {
        entries
            .iter()
            .find(|entry| entry["name"] == name)
            .unwrap_or_else(|| panic!("{name} fehlt: {result}"))
    };
    assert_eq!(entries.len(), 8, "{result}");

    // Das Ziel steht da, wie es auf dem Server im Link steht – nicht aufgelöst.
    let relative = by_name("rel");
    assert_eq!(relative["target"], "datei", "{relative}");
    assert_eq!((&relative["kind"], &relative["link"]), (&json!("file"), &json!(true)));
    assert!(relative.get("dangling").is_none(), "{relative}");
    assert_eq!(by_name("abs")["target"], file.to_str().unwrap());

    // Ein Link auf einen Ordner lässt sich betreten und nennt trotzdem sein Ziel.
    assert_eq!(by_name("ordner_link")["kind"], "directory");
    assert_eq!(by_name("ordner_link")["target"], "sub");

    // Ins Leere: als tot gekennzeichnet (und weiter „anderes“); das Ziel kennt der Link trotzdem.
    let dead = by_name("tot");
    assert_eq!(dead["dangling"], true, "{dead}");
    assert_eq!(dead["kind"], "other");
    assert_eq!(dead["target"], "fehlt");

    // Eine Schleife zeigt nicht ins Leere – das Ziel ist da, es führt nur nirgends hin.
    let circle = by_name("kreis_a");
    assert!(circle.get("dangling").is_none(), "{circle}");
    assert_eq!(circle["kind"], "other");
    assert_eq!(circle["target"], "kreis_b");

    // Was kein Link ist, nennt weder Ziel noch „tot“.
    let plain = by_name("datei");
    assert!(
        plain.get("target").is_none() && plain.get("dangling").is_none(),
        "{plain}"
    );
    assert!(by_name("sub").get("target").is_none());
}

#[cfg(unix)]
#[test]
fn the_target_of_a_single_link_can_be_asked_for() {
    let files = Files::connected();
    let file = links_of_every_kind(&files);

    let relative = files.sftp(json!({ "op": "sftp.readlink", "path": "/links/rel" }));
    assert_eq!(
        (&relative["ok"], &relative["target"]),
        (&json!(true), &json!("datei")),
        "{relative}"
    );

    let absolute = files.sftp(json!({ "op": "sftp.readlink", "path": "/links/abs" }));
    assert_eq!(absolute["target"], file.to_str().unwrap(), "{absolute}");

    // Auch ein Link ins Leere hat ein Ziel – nur ist dort nichts.
    let dead = files.sftp(json!({ "op": "sftp.readlink", "path": "/links/tot" }));
    assert_eq!(
        (&dead["ok"], &dead["target"]),
        (&json!(true), &json!("fehlt")),
        "{dead}"
    );

    // Eine Datei ist kein Link, und was fehlt, fehlt.
    let plain = files.sftp(json!({ "op": "sftp.readlink", "path": "/links/datei" }));
    assert_eq!(plain["ok"], false, "{plain}");
    assert!(plain.get("target").is_none(), "{plain}");
    let missing = files.sftp(json!({ "op": "sftp.readlink", "path": "/links/gibt-es-nicht" }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found"))
    );
}

/// OpenSSH sagt zu einer Schleife von Links „No such file“, wie zu einem Link ins Leere (der Testserver ebenso):
/// Erst ein Blick auf die Kette der Links dahinter trennt beides. Der Kern geht ihr höchstens acht Schritte nach.
#[cfg(unix)]
#[test]
fn a_chain_of_links_is_followed_to_tell_nothing_from_a_loop() {
    let files = Files::connected();
    let directory = files.remote.join("chains");
    std::fs::create_dir_all(directory.join("sub")).unwrap();
    write(directory.join("datei"), "x");
    let link = |target: &str, name: &str| std::os::unix::fs::symlink(target, directory.join(name)).unwrap();
    // Eine Kette, die im Nichts endet, und eine, die auf eine Datei trifft.
    link("kette_b", "kette_a");
    link("kette_c", "kette_b");
    link("fehlt", "kette_c");
    link("ok_b", "ok_a");
    link("datei", "ok_b");
    // Schleifen: auf sich selbst, im Kreis, und über einen anderen Ordner (relativ vom Ordner des Links aus).
    link("selbst", "selbst");
    link("kreis_b", "kreis_a");
    link("kreis_a", "kreis_b");
    link("sub/zurueck", "schleife");
    std::os::unix::fs::symlink("../schleife", directory.join("sub").join("zurueck")).unwrap();
    // Ein Link in einen anderen Ordner, dort ins Leere.
    link("sub/weg", "nach_sub");
    std::os::unix::fs::symlink("../fehlt", directory.join("sub").join("weg")).unwrap();
    // Eine Kette aus zehn Gliedern, die im Nichts endet: länger als der Kern ihr nachgeht.
    for index in 0..9 {
        link(&format!("lang_{}", index + 1), &format!("lang_{index}"));
    }
    link("fehlt", "lang_9");

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/chains" }));
    let entries = result["entries"].as_array().unwrap();
    let dangling = |name: &str| {
        let entry = entries
            .iter()
            .find(|entry| entry["name"] == name)
            .unwrap_or_else(|| panic!("{name} fehlt: {result}"));
        entry.get("dangling").is_some_and(|dead| dead == true)
    };

    for name in ["kette_a", "kette_b", "kette_c", "nach_sub", "lang_9"] {
        assert!(dangling(name), "{name} zeigt ins Leere");
    }

    for name in ["ok_a", "ok_b", "selbst", "kreis_a", "kreis_b", "schleife"] {
        assert!(!dangling(name), "{name} zeigt nicht ins Leere");
    }

    // Höchstens acht Schritte: Von `lang_2` bis zum Ende fehlt es nach acht, von `lang_1` nach neun.
    assert!(dangling("lang_2"));
    assert!(!dangling("lang_1"));
    assert!(!dangling("lang_0"));
    // Was durch ist, hat sein Ziel trotzdem genannt; ein Link auf eine Datei bleibt eine Datei.
    let ok = entries.iter().find(|entry| entry["name"] == "ok_a").unwrap();
    assert_eq!((&ok["kind"], &ok["target"]), (&json!("file"), &json!("ok_b")), "{ok}");
}

#[test]
fn only_a_link_has_a_target_to_ask_for() {
    let files = Files::connected();

    // Weder eine Datei noch ein Ordner noch etwas Fehlendes hat eines – und keine Antwort nennt eines.
    let file = files.sftp(json!({ "op": "sftp.readlink", "path": "/readme.txt" }));
    assert_eq!(file["ok"], false, "{file}");
    assert!(file.get("target").is_none(), "{file}");
    let directory = files.sftp(json!({ "op": "sftp.readlink", "path": "/www" }));
    assert_eq!(directory["ok"], false, "{directory}");
    let missing = files.sftp(json!({ "op": "sftp.readlink", "path": "/nope" }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found"))
    );

    // Gewöhnliche Einträge in der Liste nennen kein Ziel und sind nie „tot“.
    let result = files.sftp(json!({ "op": "sftp.list", "path": "/" }));
    for entry in result["entries"].as_array().unwrap() {
        assert!(
            entry.get("target").is_none() && entry.get("dangling").is_none(),
            "{entry}"
        );
    }
}

#[test]
fn a_named_pipe_is_listed_but_never_opened() {
    let files = Files::connected();
    write(files.remote.join("queue.fifo"), "");
    write(files.remote.join("www").join("events.fifo"), "");

    let result = files.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let pipe = result["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == "queue.fifo")
        .expect("die Pipe steht in der Liste");
    assert_eq!(
        (&pipe["kind"], &pipe["link"]),
        (&json!("other"), &json!(false)),
        "{pipe}"
    );

    // Öffnete der Kern sie, bliebe der Server darin stehen – und der Transfer endete erst mit der Frist, als „timeout“.
    files.start(1, "download", &files.local, "/queue.fifo");
    let download = files.finished(1);
    assert_eq!(download["state"], "failed", "{download}");
    assert!(
        download["message"].as_str().unwrap().starts_with("not_a_file"),
        "{download}"
    );
    assert!(!files.local.join("queue.fifo").exists());

    // Auch hineingeschrieben wird nicht.
    write(files.local.join("queue.fifo"), "x");
    files.start(2, "upload", &files.local.join("queue.fifo"), "/");
    let upload = files.finished(2);
    assert_eq!(upload["state"], "failed", "{upload}");
    assert!(
        upload["message"].as_str().unwrap().starts_with("not_a_file"),
        "{upload}"
    );

    // In einem Ordner wird sie ausgelassen, der Rest kommt an.
    let target = files.local.join("copy");
    std::fs::create_dir_all(&target).unwrap();
    files.start(3, "download", &target, "/www");
    let folder = files.finished(3);
    assert_eq!(folder["state"], "completed", "{folder}");
    assert!(target.join("www").join("index.html").is_file());
    assert!(!target.join("www").join("events.fifo").exists());

    // Und der Dateibrowser antwortet weiter.
    assert_eq!(files.names("/www"), ["css", "events.fifo", "index.html"]);
}

#[test]
fn names_windows_cannot_hold_arrive_under_a_name_it_can() {
    let files = Files::connected();
    // Der Testserver legt sie in ein echtes Verzeichnis – unter Windows also nur Namen, die es dort geben kann.
    // Was der Kern aus den anderen macht, prüfen die Tests von `local_name`.
    write(files.remote.join("mail").join("Makefile"), "gross");
    write(files.remote.join("mail").join("notes.txt"), "n");
    if cfg!(unix) {
        write(files.remote.join("mail").join("makefile"), "klein");
        write(files.remote.join("mail").join("1700.M1.host:2,S"), "mail");
        write(files.remote.join("mail").join("CON"), "geraet");
    }

    files.start(1, "download", &files.local, "/mail");
    let last = files.finished(1);
    assert_eq!(last["state"], "completed", "{last}");

    let target = files.local.join("mail");
    assert_eq!(std::fs::read_to_string(target.join("Makefile")).unwrap(), "gross");
    assert_eq!(std::fs::read_to_string(target.join("notes.txt")).unwrap(), "n");
    if cfg!(unix) {
        assert_eq!(std::fs::read_to_string(target.join("makefile (2)")).unwrap(), "klein");
        assert_eq!(
            std::fs::read_to_string(target.join("1700.M1.host_2,S")).unwrap(),
            "mail"
        );
        assert_eq!(std::fs::read_to_string(target.join("_CON")).unwrap(), "geraet");
    }
}

#[test]
fn the_core_tells_what_a_download_would_create_before_loading_anything() {
    let files = Files::connected();
    write(files.remote.join("www").join("empty").join(".keep"), "");
    std::fs::remove_file(files.remote.join("www").join("empty").join(".keep")).unwrap();
    write(files.remote.join("www").join("events.fifo"), "");
    if cfg!(unix) {
        write(files.remote.join("www").join("a:b.txt"), "doppelpunkt");
        write(files.remote.join("www").join("Site.CSS"), "gross");
        write(files.remote.join("www").join("site.css"), "klein");
    }

    let result = files.sftp(json!({ "op": "sftp.tree", "path": "/www" }));
    assert_eq!(result["ok"], true, "{result}");
    let tree: Vec<(String, String, bool, u64)> = result["tree"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| {
            (
                entry["remote"].as_str().unwrap().to_owned(),
                entry["local"].as_str().unwrap().to_owned(),
                entry["directory"].as_bool().unwrap(),
                entry["size"].as_u64().unwrap(),
            )
        })
        .collect();

    // Erst die Verzeichnisse, jedes vor seinem Inhalt, dann die Dateien; die Pipe fehlt, wie beim Download.
    let own =
        |remote: &str, local: &str, directory: bool, size: u64| (remote.to_owned(), local.to_owned(), directory, size);
    let mut expected = vec![
        own("/www", "www", true, 0),
        own("/www/css", "www/css", true, 0),
        own("/www/empty", "www/empty", true, 0),
    ];
    if cfg!(unix) {
        expected.push(own("/www/Site.CSS", "www/Site.CSS", false, 5));
        expected.push(own("/www/a:b.txt", "www/a_b.txt", false, 11));
    }
    expected.push(own("/www/index.html", "www/index.html", false, 11));
    if cfg!(unix) {
        expected.push(own("/www/site.css", "www/site (2).css", false, 5));
    }
    expected.push(own("/www/css/site.css", "www/css/site.css", false, 7));
    assert_eq!(tree, expected);
    assert!(
        result["tree"][3]["modified"].as_u64().unwrap() > 1_600_000_000,
        "{result}"
    );

    // Der Download legt genau das an – unter denselben Namen.
    files.start(1, "download", &files.local, "/www");
    let last = files.finished(1);
    assert_eq!(last["state"], "completed", "{last}");
    for (_, local, directory, size) in &tree {
        let path = files.local.join(local);
        assert_eq!(path.is_dir(), *directory, "{local}");
        if !directory {
            assert_eq!(std::fs::metadata(&path).unwrap().len(), *size, "{local}");
        }
    }

    // Eine einzelne Datei ist ihr eigener Baum; was es nicht gibt oder keine Datei ist, sagt warum.
    let single = files.sftp(json!({ "op": "sftp.tree", "path": "/readme.txt" }));
    assert_eq!(single["tree"].as_array().unwrap().len(), 1, "{single}");
    assert_eq!(
        (
            &single["tree"][0]["local"],
            &single["tree"][0]["directory"],
            &single["tree"][0]["size"]
        ),
        (&json!("readme.txt"), &json!(false), &json!(5)),
        "{single}"
    );
    let missing = files.sftp(json!({ "op": "sftp.tree", "path": "/nichts" }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found")),
        "{missing}"
    );
    // Das Wurzelverzeichnis lässt sich aufzählen, auch wenn es hier keinen Namen hätte: Für den Ordner selbst
    // steht ein Platzhalter, alles darunter heißt wie sonst.
    let root = files.sftp(json!({ "op": "sftp.tree", "path": "/" }));
    assert_eq!(root["ok"], true, "{root}");
    assert_eq!(
        (&root["tree"][0]["remote"], &root["tree"][0]["local"]),
        (&json!("/"), &json!("_")),
        "{root}"
    );
    assert!(
        root["tree"]
            .as_array()
            .unwrap()
            .iter()
            .any(|entry| entry["remote"] == "/www/index.html" && entry["local"] == "_/www/index.html"),
        "{root}"
    );

    let pipe = files.sftp(json!({ "op": "sftp.tree", "path": "/www/events.fifo" }));
    assert_eq!(
        (&pipe["ok"], &pipe["error"]),
        (&json!(false), &json!("not_a_file")),
        "{pipe}"
    );
}

#[test]
fn a_tree_with_many_directories_is_listed_in_the_order_one_after_another_would_give() {
    let files = Files::connected();
    // Mehr Verzeichnisse, als auf einmal aufgelistet werden, über drei Ebenen – mit Namen, deren Reihenfolge nicht
    // die ist, in der sie angelegt wurden.
    for a in (0..45).rev() {
        for b in ["zeta", "alpha"] {
            write(
                files
                    .remote
                    .join("tree")
                    .join(format!("d{a:02}"))
                    .join(b)
                    .join("leaf.txt"),
                format!("{a}{b}"),
            );
        }
        write(files.remote.join("tree").join(format!("d{a:02}")).join("note.txt"), "n");
    }
    write(files.remote.join("tree").join("top.txt"), "t");

    // Was herauskäme, wenn ein Verzeichnis nach dem anderen an die Reihe käme: Ebene für Ebene, jedes
    // Verzeichnis nach Namen sortiert; erst alle Verzeichnisse, dann die Dateien in der Reihenfolge ihrer Ordner.
    let mut directories = vec!["tree".to_owned()];
    let mut expected_files = Vec::new();
    let mut index = 0;
    while index < directories.len() {
        let directory = directories[index].clone();
        index += 1;
        let mut names: Vec<_> = std::fs::read_dir(files.remote.join(&directory))
            .unwrap()
            .map(|entry| entry.unwrap())
            .map(|entry| {
                (
                    entry.file_name().into_string().unwrap(),
                    entry.file_type().unwrap().is_dir(),
                )
            })
            .collect();
        names.sort();
        for (name, is_directory) in names {
            let path = format!("{directory}/{name}");
            if is_directory {
                directories.push(path);
            } else {
                expected_files.push(path);
            }
        }
    }
    assert_eq!((directories.len(), expected_files.len()), (136, 136));
    let expected: Vec<String> = directories.into_iter().chain(expected_files).collect();

    for _ in 0..3 {
        let result = files.sftp(json!({ "op": "sftp.tree", "path": "/tree" }));
        assert_eq!(result["ok"], true, "{result}");
        let listed: Vec<String> = result["tree"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| entry["local"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(listed, expected);
    }

    // Der Download legt genau diesen Baum an.
    files.start(1, "download", &files.local, "/tree");
    let last = files.finished(1);
    assert_eq!(last["state"], "completed", "{last}");
    for path in &expected {
        assert!(files.local.join(path).exists(), "{path}");
    }
    assert_eq!(
        std::fs::read_to_string(files.local.join("tree").join("d07").join("zeta").join("leaf.txt")).unwrap(),
        "7zeta"
    );
}

#[test]
fn the_core_says_where_a_download_would_write() {
    let files = Files::connected();

    let target = files
        .fixture
        .ok(json!({ "op": "transfer.target", "local": files.local, "remote": "/logs/app-12:00.log" }));
    assert_eq!(target["path"], json!(files.local.join("app-12_00.log")), "{target}");

    let refused = files
        .fixture
        .request(json!({ "op": "transfer.target", "local": files.local, "remote": "/" }));
    assert_eq!(
        (&refused["ok"], &refused["error"]),
        (&json!(false), &json!("unsafe_name")),
        "{refused}"
    );
}

#[test]
fn an_upload_can_arrive_under_another_name() {
    let files = Files::connected();
    write(files.local.join("plan 12_30.txt"), "geändert");
    write(files.remote.join("plan.txt"), "alt");

    // So geht eine zum Bearbeiten geholte Datei zurück, die auf dem Server anders heißt als hier.
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "upload",
        "local": files.local.join("plan 12_30.txt"), "remote": "/", "name": "plan.txt",
    }));
    let last = files.finished(1);
    assert_eq!(last["state"], "completed", "{last}");
    assert_eq!(
        std::fs::read_to_string(files.remote.join("plan.txt")).unwrap(),
        "geändert"
    );
    assert!(!files.remote.join("plan 12_30.txt").exists());

    // Der Name bleibt ein Name: kein Weg aus dem Zielverzeichnis hinaus.
    for (transfer, name) in [(2, "../plan.txt"), (3, ".."), (4, "")] {
        files.fixture.ok(json!({
            "op": "transfer.start", "session": 1, "transfer": transfer, "direction": "upload",
            "local": files.local.join("plan 12_30.txt"), "remote": "/www", "name": name,
        }));
        let refused = files.finished(transfer);
        assert_eq!(refused["state"], "failed", "{name:?}: {refused}");
        assert!(
            refused["message"].as_str().unwrap().starts_with("unsafe_name"),
            "{refused}"
        );
    }
    assert_eq!(
        std::fs::read_to_string(files.remote.join("plan.txt")).unwrap(),
        "geändert"
    );
}

#[test]
fn a_folder_with_many_small_files_travels_completely_in_both_directions() {
    let files = Files::connected();

    // Mehrere Dateien sind zugleich unterwegs; am Ende muss trotzdem jede an ihrem Platz stehen, Byte für Byte.
    let source = files.local.join("many");
    for folder in 0..6 {
        for file in 0..40 {
            write(
                source
                    .join(format!("dir-{folder}"))
                    .join("deep")
                    .join(format!("f-{file:02}.bin")),
                pattern(100 + folder * 700 + file * 13),
            );
        }
        std::fs::create_dir_all(source.join(format!("dir-{folder}")).join("empty")).unwrap();
    }
    let expected: u64 = (0..6)
        .flat_map(|folder| (0..40).map(move |file| (100 + folder * 700 + file * 13) as u64))
        .sum();

    // Ein Teil des Ziels steht schon: Dort wird nachgesehen und überschrieben, im Rest entsteht alles neu.
    write(
        files.remote.join("many").join("dir-0").join("deep").join("f-00.bin"),
        "alt",
    );
    write(
        files.remote.join("many").join("dir-0").join("deep").join("bleibt.txt"),
        "bleibt",
    );

    files.start(1, "upload", &source, "/");
    let up = files.finished(1);
    assert_eq!(
        (&up["state"], &up["bytes"], &up["total"]),
        (&json!("completed"), &json!(expected), &json!(expected)),
        "{up}"
    );
    for folder in 0..6 {
        for file in 0..40 {
            let name = format!("f-{file:02}.bin");
            let remote = files
                .remote
                .join("many")
                .join(format!("dir-{folder}"))
                .join("deep")
                .join(&name);
            assert_eq!(
                std::fs::read(&remote).unwrap(),
                pattern(100 + folder * 700 + file * 13),
                "{remote:?}"
            );
        }
        assert!(
            files
                .remote
                .join("many")
                .join(format!("dir-{folder}"))
                .join("empty")
                .is_dir()
        );
    }
    assert_eq!(
        std::fs::read_to_string(files.remote.join("many").join("dir-0").join("deep").join("bleibt.txt")).unwrap(),
        "bleibt"
    );

    let back = files.local.join("back");
    std::fs::create_dir_all(&back).unwrap();
    files.start(2, "download", &back, "/many");
    let down = files.finished(2);
    assert_eq!(down["state"], "completed", "{down}");
    let mut count = 0;
    for folder in 0..6 {
        for entry in std::fs::read_dir(back.join("many").join(format!("dir-{folder}")).join("deep")).unwrap() {
            let entry = entry.unwrap();
            let name = entry.file_name().into_string().unwrap();
            assert!(!name.ends_with(".nexus-part"), "{name}");
            if let Some(number) = name.strip_prefix("f-").and_then(|rest| rest.strip_suffix(".bin")) {
                let file: usize = number.parse().unwrap();
                assert_eq!(
                    std::fs::read(entry.path()).unwrap(),
                    pattern(100 + folder * 700 + file * 13),
                    "{name}"
                );
                count += 1;
            }
        }
    }
    assert_eq!(count, 240);
}

#[test]
fn a_file_that_fails_in_a_folder_stops_the_rest_and_leaves_no_partial_files() {
    let files = Files::connected();
    for file in 0..60 {
        write(
            files.remote.join("mixed").join(format!("f-{file:02}.bin")),
            pattern(2000),
        );
    }

    // Im Ziel liegt, wo eine der Dateien hin soll, schon ein Ordner – diese eine kann nicht ankommen.
    let target = files.local.join("out");
    std::fs::create_dir_all(target.join("mixed").join("f-30.bin")).unwrap();

    files.start(1, "download", &target, "/mixed");
    let last = files.finished(1);
    assert_eq!(last["state"], "failed", "{last}");

    // Was daneben schon lief, ist fertig geworden oder hat hinter sich aufgeräumt.
    let names: Vec<String> = std::fs::read_dir(target.join("mixed"))
        .unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap())
        .collect();
    assert!(names.iter().all(|name| !name.ends_with(".nexus-part")), "{names:?}");
    assert!(
        names.len() < 61,
        "nach dem Fehler beginnt keine neue Datei mehr: {names:?}"
    );
}

/// Ein Eintrag aus der Liste eines Verzeichnisses.
fn entry_of(files: &Files, path: &str, name: &str) -> Value {
    let result = files.sftp(json!({ "op": "sftp.list", "path": path }));
    result["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == name)
        .unwrap_or_else(|| panic!("{name} fehlt in {path}: {result}"))
        .clone()
}

#[test]
fn permissions_for_a_whole_folder_keep_its_directories_enterable() {
    let files = Files::connected();

    files.done(json!({ "op": "sftp.chmod", "path": "/www", "mode": 0o640, "recursive": true }));

    // Dateien bekommen genau die Zahl; Verzeichnisse dazu das Recht zum Betreten, wo gelesen werden darf.
    assert_eq!(entry_of(&files, "/", "www")["permissions"], 0o750);
    assert_eq!(entry_of(&files, "/www", "css")["permissions"], 0o750);
    assert_eq!(entry_of(&files, "/www", "index.html")["permissions"], 0o640);
    assert_eq!(entry_of(&files, "/www/css", "site.css")["permissions"], 0o640);
    assert_eq!(
        entry_of(&files, "/", "readme.txt")["permissions"],
        0o644,
        "nur der genannte Baum"
    );

    // Ohne „rekursiv“ gilt die Zahl für den Ordner allein und genau so, wie sie dasteht.
    files.done(json!({ "op": "sftp.chmod", "path": "/www", "mode": 0o700 }));
    assert_eq!(entry_of(&files, "/", "www")["permissions"], 0o700);
    assert_eq!(entry_of(&files, "/www", "index.html")["permissions"], 0o640);
}

#[test]
fn owner_and_group_can_be_changed_by_name_or_number() {
    let files = Files::connected();
    write(
        files.remote.join("etc").join("passwd"),
        "root:x:0:0:root:/root:/bin/bash\ndemo:x:1000:1000::/home/demo:/bin/sh\nwww-data:x:33:33::/var/www:/usr/sbin/nologin\n",
    );
    write(
        files.remote.join("etc").join("group"),
        "root:x:0:\nstaff:x:1000:demo\nwww-data:x:33:\n",
    );
    let owners = |path: &str, name: &str| {
        let entry = entry_of(&files, path, name);
        (
            entry["owner"].as_str().unwrap().to_owned(),
            entry["group"].as_str().unwrap().to_owned(),
        )
    };
    let pair = |owner: &str, group: &str| (owner.to_owned(), group.to_owned());
    assert_eq!(owners("/", "readme.txt"), pair("demo", "staff"));

    // Nur die Gruppe: Der Besitzer bleibt – obwohl SFTP beide nur gemeinsam überträgt.
    files.done(json!({ "op": "sftp.chown", "path": "/readme.txt", "group": "www-data" }));
    assert_eq!(owners("/", "readme.txt"), pair("demo", "www-data"));

    files.done(json!({ "op": "sftp.chown", "path": "/readme.txt", "owner": "root" }));
    assert_eq!(owners("/", "readme.txt"), pair("root", "www-data"));

    // Eine Nummer geht auch – etwa wenn der Server den Namen nicht in /etc/passwd führt.
    files.done(json!({ "op": "sftp.chown", "path": "/readme.txt", "owner": "4711", "group": "0" }));
    assert_eq!(owners("/", "readme.txt"), pair("4711", "root"));

    // Ein ganzer Baum.
    files.done(
        json!({ "op": "sftp.chown", "path": "/www", "owner": "www-data", "group": "www-data", "recursive": true }),
    );
    assert_eq!(owners("/", "www"), pair("www-data", "www-data"));
    assert_eq!(owners("/www", "index.html"), pair("www-data", "www-data"));
    assert_eq!(owners("/www/css", "site.css"), pair("www-data", "www-data"));
    assert_eq!(owners("/", "Zebra.log"), pair("demo", "staff"), "nur der genannte Baum");

    // Einen Namen, den der Server nicht kennt, gibt es nicht als Besitzer – und nichts ändert sich.
    let unknown = files.sftp(json!({ "op": "sftp.chown", "path": "/Zebra.log", "owner": "niemand" }));
    assert_eq!(
        (&unknown["ok"], &unknown["error"], &unknown["message"]),
        (&json!(false), &json!("unknown_owner"), &json!("niemand"))
    );
    assert_eq!(owners("/", "Zebra.log"), pair("demo", "staff"));
}

/// Die Obergrenze selbst, nicht herabgesetzt: Der Testserver nennt für `million` schneller als jedes Dateisystem
/// 1 000 100 erfundene Einträge. An einem echten Server mit so vielen Dateien griff vorher die Frist (2026-10-11).
///
/// Läuft nur auf Wunsch und optimiert – unoptimiert dauert eine Million Einträge länger, als das Gerüst wartet:
/// `cargo test --release -p nexus-core --test sftp -- --ignored a_directory_of_more`
#[test]
#[ignore = "eine Million Einträge: nur mit --release und --ignored"]
fn a_directory_of_more_than_a_million_entries_is_given_up_at_the_limit() {
    let files = Files::connected();
    std::fs::create_dir_all(files.remote.join("million")).unwrap();

    let started = std::time::Instant::now();
    files
        .fixture
        .ok(json!({ "op": "sftp.list", "session": 1, "request": 900, "path": "/million" }));
    let refused = files
        .fixture
        .recorder
        .find_within(std::time::Duration::from_secs(150), "sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == 900
        });
    assert_eq!(
        (&refused["ok"], &refused["error"]),
        (&json!(false), &json!("too_many")),
        "{refused}"
    );
    println!("given up after {:?}", started.elapsed());

    // Die Sitzung trägt weiter, auch während die letzten Antworten der aufgegebenen Liste noch eintreffen.
    assert_eq!(files.names("/www"), ["css", "index.html"]);
}

#[test]
fn a_file_system_that_stopped_answering_gets_a_new_channel() {
    let files = Files::connected();
    std::fs::create_dir_all(files.remote.join("stuck")).unwrap();

    // Der Server beantwortet die Anfrage nie – wie bei einem hängenden Netzlaufwerk. Nach der Frist gibt der Kern auf.
    let stuck = files.sftp(json!({ "op": "sftp.list", "path": "/stuck" }));
    assert_eq!(
        (&stuck["ok"], &stuck["error"]),
        (&json!(false), &json!("timeout")),
        "{stuck}"
    );

    // Ein neuer Kanal auf derselben Verbindung, ohne neue Anmeldung – und die Shell hat davon nichts gemerkt.
    files.done(json!({ "op": "sftp.reopen" }));
    assert_eq!(files.names("/www"), ["css", "index.html"]);
    files.fixture.recorder.output(1, "$ ");
    assert!(files.fixture.core.write(1, b"echo lebt\r"));
    assert!(files.fixture.recorder.output(1, "\r\n$ ").contains("lebt"));
    assert!(
        files
            .fixture
            .recorder
            .all("session.state")
            .iter()
            .all(|event| event["state"] != "closed")
    );

    let unknown = files
        .fixture
        .request(json!({ "op": "sftp.reopen", "session": 99, "request": 1 }));
    assert_eq!(unknown["error"], "unknown_session");
}

fn variable(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

/// Eine Sitzung zu einem echten Server – für das, was der Testserver nur nachspielt. `None`, wenn die Umgebung
/// keinen nennt; die Tests damit laufen also nur auf Wunsch. Verlangt sind `NEXUS_REAL_HOST`, `NEXUS_REAL_USER`,
/// `NEXUS_REAL_FINGERPRINT` (`SHA256:…` des Host-Schlüssels, wie ihn der Betreiber nennt – er wird verglichen,
/// nicht blind angenommen) und `NEXUS_REAL_DIR`: ein leerer Ordner dort, in den der Test schreiben darf. Dazu
/// `NEXUS_REAL_KEY` (Datei des privaten Schlüssels) oder `NEXUS_REAL_PASSWORD`, und nach Bedarf `NEXUS_REAL_PORT`.
/// Der zweite Wert ist der Ordner.
fn real_server() -> Option<(Files, String)> {
    let (host, user) = (variable("NEXUS_REAL_HOST")?, variable("NEXUS_REAL_USER")?);
    let (fingerprint, directory) = (variable("NEXUS_REAL_FINGERPRINT")?, variable("NEXUS_REAL_DIR")?);
    let port: u16 = variable("NEXUS_REAL_PORT").map_or(22, |port| port.parse().unwrap());

    let fixture = Fixture::new(Options::default());
    let profile =
        fixture.profile(json!({ "host": host, "port": port, "user": user, "key_path": variable("NEXUS_REAL_KEY") }));
    if let Some(password) = variable("NEXUS_REAL_PASSWORD") {
        fixture.secrets.set(&password_key(&profile), &user, &password).unwrap();
    }

    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["fingerprint"], fingerprint.as_str(), "{prompt}");
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    let files = Files {
        remote: fixture.directory.join("remote"),
        local: fixture.directory.join("local"),
        fixture,
        next_request: std::cell::Cell::new(1),
    };
    std::fs::create_dir_all(&files.local).unwrap();
    assert_eq!(
        files.names(&directory),
        Vec::<String>::new(),
        "the folder must be empty"
    );
    Some((files, directory))
}

/// Ein ZIP-Archiv mit einer einzigen, leeren Datei – von Hand gebaut, damit der Test kein Programm dafür braucht
/// (ohne Inhalt ist die Prüfsumme 0).
fn zip_with_an_empty_file(name: &str) -> Vec<u8> {
    let name = name.as_bytes();
    let length = (name.len() as u16).to_le_bytes();
    // Version 2.0, keine Merker, unkomprimiert, Zeit und Datum (1.1.1980), Prüfsumme und beide Größen 0.
    let common = [&[20, 0, 0, 0, 0, 0, 0, 0, 0x21, 0][..], &[0; 12], &length, &[0, 0]].concat();
    let local = [&b"PK\x03\x04"[..], &common, name].concat();
    // Im Verzeichnis folgen Kommentarlänge, Datenträger, Attribute und die Stelle des Eintrags (0): 14 Bytes.
    let central = [&b"PK\x01\x02\x14\x00"[..], &common, &[0; 14], name].concat();
    let end = [
        &b"PK\x05\x06\x00\x00\x00\x00\x01\x00\x01\x00"[..],
        &(central.len() as u32).to_le_bytes(),
        &(local.len() as u32).to_le_bytes(),
        &[0, 0],
    ]
    .concat();
    [local, central, end].concat()
}

/// Packen, Entpacken und die Größe an einem echten Server ([`real_server`]): Dort laufen wirklich `tar`, `unzip`
/// und `du` – GNU, BusyBox oder die der BSDs –, der Testserver schreibt die Befehle bloß mit. Mit Namen,
/// an denen eine Befehlszeile gern scheitert: Leerzeichen, Apostroph, führender Bindestrich.
#[test]
fn packing_and_unpacking_work_on_a_real_server() {
    let Some((files, directory)) = real_server() else {
        return;
    };
    let at = |name: &str| format!("{directory}/{name}");
    let originals = ["-dash.txt", "a b.txt", "it's.txt", "sub dir"];
    for name in ["a b.txt", "-dash.txt", "it's.txt"] {
        files.done(json!({ "op": "sftp.touch", "path": at(name) }));
    }
    files.done(json!({ "op": "sftp.mkdir", "path": at("sub dir") }));
    files.done(json!({ "op": "sftp.touch", "path": at("sub dir/in side.txt") }));

    // Packen – auch in ein Archiv, dessen Name wie eine Option aussieht. Ein vorhandenes wird nicht ersetzt.
    for archive in ["my archive.tar.gz", "-lead.tar.gz"] {
        files.done(json!({ "op": "sftp.pack", "directory": directory, "names": originals, "archive": archive }));
    }
    let again = files.sftp(
        json!({ "op": "sftp.pack", "directory": directory, "names": ["a b.txt"], "archive": "my archive.tar.gz" }),
    );
    assert_eq!(
        (&again["ok"], &again["error"]),
        (&json!(false), &json!("command_failed")),
        "{again}"
    );
    println!("pack over an existing archive: {}", again["message"]);

    // Entpacken bringt alles zurück, samt Unterordner.
    for name in originals {
        files.done(json!({ "op": "sftp.delete", "path": at(name) }));
    }
    let mut left = files.names(&directory);
    left.sort();
    assert_eq!(left, ["-lead.tar.gz", "my archive.tar.gz"]);
    files.done(json!({ "op": "sftp.unpack", "directory": directory, "archive": "-lead.tar.gz" }));
    let mut back = files.names(&directory);
    back.sort();
    assert_eq!(
        back,
        [
            "-dash.txt",
            "-lead.tar.gz",
            "a b.txt",
            "it's.txt",
            "my archive.tar.gz",
            "sub dir"
        ]
    );
    assert_eq!(files.names(&at("sub dir")), ["in side.txt"]);

    // Noch einmal über das Vorhandene: Je nach `tar` ein Fehler oder stilles Auslassen – ersetzt wird nichts.
    let over = files.sftp(json!({ "op": "sftp.unpack", "directory": directory, "archive": "my archive.tar.gz" }));
    println!("unpack over existing files: ok={} {}", over["ok"], over["message"]);

    // Ein ZIP – wo es `unzip` gibt.
    let zip = files.local.join("-zip file.zip");
    std::fs::write(&zip, zip_with_an_empty_file("from zip.txt")).unwrap();
    files.start(1, "upload", &zip, &directory);
    let uploaded = files.finished(1);
    assert_eq!(uploaded["state"], "completed", "{uploaded}");
    let unzipped = files.sftp(json!({ "op": "sftp.unpack", "directory": directory, "archive": "-zip file.zip" }));
    println!("unzip: ok={} {}", unzipped["ok"], unzipped["message"]);
    if unzipped["ok"] == true {
        assert!(files.names(&directory).contains(&"from zip.txt".to_owned()));
        files.done(json!({ "op": "sftp.delete", "path": at("from zip.txt") }));
    }

    // Ein ZIP, das aus dem Ordner hinaus will: Es entsteht nichts darüber – gleich, ob `unzip` den Eintrag
    // überspringt oder der Kern das Archiv vorher ablehnt (mit `7z`).
    let parent = directory.rsplit_once('/').map_or("/", |(parent, _)| parent).to_owned();
    let evil = files.local.join("evil.zip");
    std::fs::write(&evil, zip_with_an_empty_file("../escape-nx.txt")).unwrap();
    files.start(2, "upload", &evil, &directory);
    assert_eq!(files.finished(2)["state"], "completed");
    let refused = files.sftp(json!({ "op": "sftp.unpack", "directory": directory, "archive": "evil.zip" }));
    println!("unzip ../: ok={} {}", refused["ok"], refused["message"]);
    assert!(
        !files.names(&parent).contains(&"escape-nx.txt".to_owned()),
        "the archive wrote outside: {refused}"
    );
    files.done(json!({ "op": "sftp.delete", "path": at("evil.zip") }));

    // Die Größe des Ordners: Jedes `du` nennt eine Zahl, und der Ordner mit zwei Archiven ist nicht leer.
    let size = files.sftp(json!({ "op": "sftp.size", "path": directory }));
    assert_eq!(size["ok"], true, "{size}");
    assert!(size["size"].as_u64().unwrap() > 0, "{size}");
    println!("size of the folder: {}", size["size"]);

    for name in originals
        .iter()
        .copied()
        .chain(["-lead.tar.gz", "my archive.tar.gz", "-zip file.zip"])
    {
        files.done(json!({ "op": "sftp.delete", "path": at(name) }));
    }
    assert_eq!(files.names(&directory), Vec::<String>::new());
}

/// Was die Seiten eines Servers zeigen, an einem echten Server ([`real_server`] mit `NEXUS_REAL_PAGES`): die
/// Auslastung, das Journal und eine Protokolldatei. Was es dort nicht gibt (kein Journal, ein Journal nur für das
/// eigene Konto), sagt der Test, statt zu scheitern; mit `NEXUS_REAL_EXPECT` (durch Kommas getrennt: `journal`,
/// `logfile`) wird es verlangt;
/// `NEXUS_REAL_OS` nennt, was als System dastehen soll.
#[test]
fn the_pages_of_a_server_work_on_a_real_server() {
    if variable("NEXUS_REAL_PAGES").is_none() {
        return;
    }
    let Some((files, _directory)) = real_server() else {
        return;
    };
    let expect = variable("NEXUS_REAL_EXPECT").unwrap_or_default();
    let expected = |page: &str| expect.split(',').any(|wanted| wanted.trim() == page);

    files
        .fixture
        .ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));

    // Ein Konto, das nur Dateien darf (`NEXUS_REAL_NO_SHELL`, bei Synology DSM jedes ohne Adminrechte): keine
    // Auslastung, und jede Seite sagt, dass es keine Befehle gibt – keine wartet ins Leere.
    if variable("NEXUS_REAL_NO_SHELL").is_some() {
        let silent = files
            .fixture
            .recorder
            .find_within(std::time::Duration::from_secs(60), "no load", |event| {
                event["event"] == "monitor.unavailable"
            });
        println!("monitor: {silent}");
        files
            .fixture
            .ok(json!({ "op": "monitor.set", "session": 1, "enabled": false }));
        let request = json!({ "op": "logs.read", "lines": 5 });
        let answer = files.sftp(request.clone());
        println!("{}: {} {}", request["op"], answer["error"], answer["message"]);
        assert!(
            answer["ok"] == false && matches!(answer["error"].as_str(), Some("no_exec" | "no_posix_shell")),
            "{request} -> {answer}"
        );
        return;
    }

    let stats = files
        .fixture
        .recorder
        .find_within(std::time::Duration::from_secs(60), "two samples", |event| {
            event["event"] == "monitor.stats" && event["memory_total"].as_u64().unwrap_or(0) > 0
        });
    files
        .fixture
        .ok(json!({ "op": "monitor.set", "session": 1, "enabled": false }));
    println!(
        "monitor: os={} kernel={} cpu={} cores={} memory={}/{} disks={} interfaces={} load={} machine={}",
        stats["os"],
        stats["kernel"],
        stats["cpu"],
        stats["core_count"],
        stats["memory_used"],
        stats["memory_total"],
        stats["disks"],
        stats["interfaces"],
        stats["load"],
        stats["machine"]
    );
    assert!(stats["disk_total"].as_u64().unwrap_or(0) > 0, "{stats}");
    if let Some(os) = variable("NEXUS_REAL_OS") {
        assert!(
            stats["os"].as_str().unwrap_or_default().starts_with(&os),
            "{}",
            stats["os"]
        );
    }

    let journal = files.sftp(json!({ "op": "logs.read", "lines": 50 }));
    println!(
        "journal: ok={} lines={} limited={} {}",
        journal["ok"],
        journal["logs"].as_array().map_or(0, Vec::len),
        journal["limited"],
        journal["message"]
    );
    if expected("journal") {
        assert_eq!(journal["ok"], true, "{journal}");
    }

    if let Some(file) = variable("NEXUS_REAL_LOGFILE") {
        let read = files.sftp(json!({ "op": "logs.read", "path": file, "lines": 20 }));
        println!(
            "log file: ok={} lines={} {}",
            read["ok"],
            read["logs"].as_array().map_or(0, Vec::len),
            read["message"]
        );
        if expected("logfile") {
            assert_eq!(read["ok"], true, "{read}");
        }
    }
}

/// Ein Windows-Server ([`real_server`] mit `NEXUS_REAL_WINDOWS`): Der Kern erkennt ihn, misst die Auslastung mit
/// PowerShell und führt dort aus, was es unter Windows gibt – Prüfsumme, Belegung, Kopieren und Verschieben. Mit
/// Namen, die in `cmd.exe` ein Befehl wären.
#[test]
fn a_windows_server_measures_and_runs_its_commands() {
    if variable("NEXUS_REAL_WINDOWS").is_none() {
        return;
    }
    let Some((files, directory)) = real_server() else {
        return;
    };
    let at = |name: &str| format!("{directory}/{name}");

    let probe = files.sftp(json!({ "op": "commands.probe" }));
    assert_eq!(
        (&probe["ok"], &probe["line"]),
        (&json!(true), &json!("windows")),
        "{probe}"
    );

    // Ein Befehl der App ist PowerShell; ein Programm, das scheitert, nennt seinen Rückgabewert.
    let run = files.sftp(json!({ "op": "exec.run", "command": "Write-Output 'über'; cmd /c exit 3", "seconds": 60 }));
    assert_eq!(
        (&run["run"]["status"], run["run"]["stdout"].as_str().map(str::trim)),
        (&json!(3), Some("über")),
        "{run}"
    );

    // Die Auslastung kommt in derselben Form wie von Linux.
    files
        .fixture
        .ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    let stats = files
        .fixture
        .recorder
        .find_within(std::time::Duration::from_secs(60), "two samples", |event| {
            event["event"] == "monitor.stats" && event["memory_total"].as_u64().unwrap_or(0) > 0
        });
    println!("monitor: {stats}");
    assert!(stats["disk_total"].as_u64().unwrap_or(0) > 0, "{stats}");
    files
        .fixture
        .ok(json!({ "op": "monitor.set", "session": 1, "enabled": false }));

    // Prüfsumme und Belegung – an einem Namen, der in cmd.exe zwei Befehle wäre.
    let name = "a&calc&b's.txt";
    files.done(json!({ "op": "sftp.write", "path": at(name), "data": "aGVsbG8=" }));
    let sums = files.sftp(json!({ "op": "sftp.checksum", "paths": [at(name), at("missing.txt")] }));
    assert_eq!(
        sums["sums"],
        json!(["2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", null]),
        "{sums}"
    );
    // Genau 1 KiB ist 1 KiB, nicht 2 (bis 2026-10-19 rundete das Skript kaufmännisch).
    let kib = "kib.bin";
    files.done(json!({ "op": "sftp.write", "path": at(kib), "data": format!("{}QQ==", "QUFB".repeat(341)) }));
    let usage = files.sftp(json!({ "op": "sftp.usage", "path": directory }));
    assert_eq!(usage["ok"], true, "{usage}");
    let listed = usage["usage"].as_array().unwrap();
    assert!(listed.iter().any(|entry| entry["path"] == at(name)), "{usage}");
    assert!(
        listed
            .iter()
            .any(|entry| entry["path"] == at(kib) && entry["bytes"] == 1024),
        "{usage}"
    );
    files.done(json!({ "op": "sftp.delete", "path": at(kib) }));
    // Ein Ordner, den es nicht gibt, belegt nicht 0 KiB – er fehlt.
    let missing = files.sftp(json!({ "op": "sftp.usage", "path": at("missing") }));
    assert_eq!(missing["ok"], false, "{missing}");

    // Kopieren und Verschieben am Server.
    files.done(json!({ "op": "sftp.copy", "pairs": [{ "from": at(name), "to": at("copy [1].txt") }] }));
    files.done(
        json!({ "op": "sftp.copy", "move": true, "pairs": [{ "from": at("copy [1].txt"), "to": at("moved.txt") }] }),
    );
    let mut names = files.names(&directory);
    names.sort();
    assert_eq!(names, [name, "moved.txt"]);

    for leftover in [name, "moved.txt"] {
        files.done(json!({ "op": "sftp.delete", "path": at(leftover) }));
    }
    assert_eq!(files.names(&directory), Vec::<String>::new());
}

/// „Verknüpfung anlegen“ an einem echten Server ([`real_server`]): Dort liest OpenSSH die beiden Pfade der Anfrage
/// vertauscht, und der Testserver spielt das nur nach. Der Test räumt hinter sich auf (außer mit `NEXUS_REAL_KEEP`).
#[test]
fn a_link_points_the_right_way_on_a_real_server() {
    let Some((files, directory)) = real_server() else {
        return;
    };
    let (target, link) = (format!("{directory}/target.txt"), format!("{directory}/link.txt"));

    files.done(json!({ "op": "sftp.touch", "path": target }));
    files.done(json!({ "op": "sftp.symlink", "path": link, "to": target }));

    // Der Link ist der Link und das Ziel das Ziel – nicht umgekehrt, und nichts wurde ersetzt.
    let listed = files.sftp(json!({ "op": "sftp.list", "path": directory }));
    let entries: Vec<(&str, bool)> = listed["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| (entry["name"].as_str().unwrap(), entry["link"].as_bool().unwrap()))
        .collect();
    assert_eq!(entries, [("link.txt", true), ("target.txt", false)], "{listed}");

    let taken = files.sftp(json!({ "op": "sftp.symlink", "path": link, "to": target }));
    assert_eq!((&taken["ok"], &taken["error"]), (&json!(false), &json!("exists")));
    let missing = files.sftp(json!({ "op": "sftp.symlink", "path": format!("{directory}/other.txt"), "to": format!("{directory}/nope.txt") }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found"))
    );

    // Ohne NEXUS_REAL_KEEP wieder weg: erst der Link, dann sein Ziel.
    if variable("NEXUS_REAL_KEEP").is_none() {
        files.done(json!({ "op": "sftp.delete", "path": link }));
        files.done(json!({ "op": "sftp.delete", "path": target }));
        assert_eq!(files.names(&directory), Vec::<String>::new());
    }
}

#[test]
fn a_bandwidth_limit_slows_transfers_down_and_can_be_lifted() {
    let files = Files::connected();
    let content = pattern(2 * MIB);
    write(files.local.join("slow.bin"), &content);
    write(files.local.join("fast.bin"), &content);

    // 1 MiB je Sekunde: Zwei MiB brauchen dann gut eine Sekunde (der letzte Block wartet nicht mehr auf den nächsten).
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": MIB }));
    let started = std::time::Instant::now();
    files.start(1, "upload", &files.local.join("slow.bin"), "/");
    assert_eq!(files.finished(1)["state"], "completed");
    let limited = started.elapsed();
    assert!(limited >= std::time::Duration::from_millis(1500), "{limited:?}");
    assert!(std::fs::read(files.remote.join("slow.bin")).unwrap() == content);

    // Ohne Grenze geht dasselbe wieder so schnell, wie die Leitung es hergibt.
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 0 }));
    let started = std::time::Instant::now();
    files.start(2, "upload", &files.local.join("fast.bin"), "/");
    assert_eq!(files.finished(2)["state"], "completed");
    assert!(started.elapsed() < limited, "{:?} vs {limited:?}", started.elapsed());
}

/// Ein zweiter Server als Sitzung 2, dessen SFTP-Wurzel `root` ist – das Ziel für „von Server zu Server“.
fn second_server(files: &Files, root: &Path) -> Running {
    std::fs::create_dir_all(root).unwrap();
    let server = files.fixture.another_server(
        2,
        Options {
            sftp_root: Some(root.to_path_buf()),
            ..with_password()
        },
    );
    let profile = files
        .fixture
        .profile(json!({ "name": "ziel", "port": server.address.port() }));
    files
        .fixture
        .secrets
        .set(&password_key(&profile), "demo", "geheim")
        .unwrap();
    files
        .fixture
        .ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24 }));
    files.fixture.trust_host_key();
    files.fixture.recorder.find("Ziel verbunden", |event| {
        event["event"] == "session.state" && event["session"] == 2 && event["state"] == "connected"
    });
    server
}

#[test]
fn a_transfer_between_two_servers_continues_where_it_was_halted() {
    let files = Files::connected();
    let other = files.fixture.directory.join("other");
    let _server = second_server(&files, &other);
    let content = pattern(32 * MIB);
    write(files.remote.join("big.bin"), &content);
    let relay = |transfer: u64, remote: &str, resume: Option<u64>| {
        let mut request = json!({
            "op": "transfer.start", "session": 1, "transfer": transfer, "direction": "relay",
            "local": "/", "remote": remote, "target_session": 2,
        });
        if let Some(resume) = resume {
            request["resume"] = json!(resume);
        }
        files.fixture.ok(request);
    };

    // Gebremst, damit sich der Transfer mittendrin anhalten lässt; angehalten endet er wie bei einem Abriss –
    // beide Verbindungen stehen noch, und die Teil-Datei am Ziel bleibt trotzdem liegen.
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 8 * MIB }));
    relay(1, "/big.bin", None);
    files.reached(1, 4 * MIB);
    files.fixture.ok(json!({ "op": "transfer.suspend" }));
    let end = files.finished(1);
    assert_eq!(end["state"], "failed", "{end}");
    let partial = other.join("big.bin.nexus-part");
    let left = std::fs::metadata(&partial).unwrap().len();
    assert!(left > 0 && left < content.len() as u64, "{left}");
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 0 }));

    // Der neue Versuch geht in der Teil-Datei weiter.
    relay(2, "/big.bin", Some(1));
    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    // Der Server schreibt nach dem Anhalten noch zu Ende, was unterwegs war: Weiter geht es dort, wo die Teil-Datei
    // dann wirklich endet – mindestens an der eben gemessenen Stelle.
    let from = files.resumed(2).expect("der zweite Versuch setzt fort");
    assert!(from >= left && from < content.len() as u64, "{from} nach {left}");
    assert!(std::fs::read(other.join("big.bin")).unwrap() == content);
    assert!(!partial.exists());

    // Ohne das Wort der App beginnt er von vorn, auch wenn am Ziel schon alles liegt.
    relay(3, "/big.bin", None);
    assert_eq!(files.finished(3)["state"], "completed");
    assert_eq!(files.resumed(3), None);

    // Eine Teil-Datei, die nicht zu dieser Quelle gehört, wird nicht fortgesetzt.
    std::fs::remove_file(other.join("big.bin")).unwrap();
    write(&partial, vec![7u8; 2 * MIB]);
    relay(4, "/big.bin", Some(1));
    assert_eq!(files.finished(4)["state"], "completed");
    assert_eq!(files.resumed(4), None);
    assert!(std::fs::read(other.join("big.bin")).unwrap() == content);
}

#[test]
fn a_new_attempt_between_two_servers_leaves_what_is_already_complete_at_the_target() {
    let files = Files::connected();
    let other = files.fixture.directory.join("other");
    let _server = second_server(&files, &other);
    let (first, second) = (pattern(MIB), pattern(2 * MIB));
    write(files.remote.join("set").join("a.bin"), &first);
    write(files.remote.join("set").join("deep").join("b.bin"), &second);
    write(files.remote.join("set").join("empty.txt"), "");

    // Am Ziel liegt a.bin schon richtig; b.bin ist gleich groß, aber etwas anderes.
    write(other.join("set").join("a.bin"), &first);
    write(other.join("set").join("deep").join("b.bin"), vec![1u8; 2 * MIB]);
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "relay",
        "local": "/", "remote": "/set", "target_session": 2, "resume": 1,
    }));

    let end = files.finished(1);
    assert_eq!(end["state"], "completed", "{end}");
    assert_eq!(files.resumed(1), Some(MIB as u64));
    assert!(std::fs::read(other.join("set").join("deep").join("b.bin")).unwrap() == second);
    assert!(other.join("set").join("empty.txt").exists());
}

#[test]
fn a_halted_upload_keeps_its_partial_file_and_continues_in_it() {
    let files = Files::connected();
    let content = pattern(32 * MIB);
    write(files.local.join("big.bin"), &content);
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 8 * MIB }));
    files.start(1, "upload", &files.local.join("big.bin"), "/");
    files.reached(1, 4 * MIB);

    // Angehalten, nicht abgebrochen: Die Verbindung steht noch, und die Teil-Datei bleibt am Server liegen.
    files.fixture.ok(json!({ "op": "transfer.suspend" }));
    let end = files.finished(1);
    assert_eq!(end["state"], "failed", "{end}");
    let sent = end["bytes"].as_u64().unwrap();
    let partial = files.remote.join("big.bin.nexus-part");
    assert!(std::fs::metadata(&partial).unwrap().len() > 0);
    files
        .fixture
        .ok(json!({ "op": "transfer.limit", "bytes_per_second": 0 }));

    files.retry(2, &files.local.join("big.bin"), "/", sent as usize);
    let end = files.finished(2);
    assert_eq!(end["state"], "completed", "{end}");
    assert!(files.resumed(2).is_some_and(|from| from > 0), "{:?}", files.resumed(2));
    assert!(std::fs::read(files.remote.join("big.bin")).unwrap() == content);
    assert!(!partial.exists());
}

/// „Schlüssel erzeugen und hinterlegen“ an einem echten Server ([`real_server`]): Der Eintrag in `authorized_keys`
/// entsteht dort durch einen Befehl an der Shell des Kontos – bash, die `ash` von BusyBox, fish –, und danach muss
/// sich mit dem neuen Schlüssel anmelden lassen. Der Kommentar des Schlüssels ist `nexus-real-test`: An ihm lässt
/// sich die Zeile danach wieder aus `authorized_keys` nehmen (das kann der Test nicht selbst).
#[test]
fn a_new_key_is_installed_on_a_real_server_and_signs_in() {
    let Some((files, _)) = real_server() else {
        return;
    };
    let (host, user) = (
        variable("NEXUS_REAL_HOST").unwrap(),
        variable("NEXUS_REAL_USER").unwrap(),
    );
    let port: u16 = variable("NEXUS_REAL_PORT").map_or(22, |port| port.parse().unwrap());

    let generated = files
        .fixture
        .ok(json!({ "op": "key.generate", "name": "id_real_test", "comment": "nexus-real-test" }));
    let public_key = generated["public_key"].as_str().unwrap().to_owned();

    // Zweimal: Beim zweiten Mal steht die Zeile schon dort und kommt nicht noch einmal dazu.
    files.done(json!({ "op": "key.install", "public_key": public_key }));
    files.done(json!({ "op": "key.install", "public_key": public_key }));

    // Eine neue Verbindung – nur mit dem neuen Schlüssel, ohne Agenten und ohne gespeichertes Passwort. Den
    // Host-Schlüssel kennt der Kern schon von der ersten.
    let profile = files.fixture.profile(json!({
        "name": "mit neuem Schlüssel", "host": host, "port": port, "user": user,
        "key_path": generated["path"], "use_agent": false,
    }));
    files
        .fixture
        .ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24 }));
    let end = files
        .fixture
        .recorder
        .find("Anmeldung mit dem neuen Schlüssel", |event| {
            event["event"] == "session.state"
                && event["session"] == 2
                && matches!(event["state"].as_str(), Some("connected" | "closed"))
        });
    assert_eq!(end["state"], "connected", "{end}");
    assert!(
        !files
            .fixture
            .recorder
            .all("prompt.password")
            .iter()
            .any(|event| event["session"] == 2)
    );
}

#[test]
fn a_file_is_followed_until_the_app_stops_it() {
    let files = Files::connected();
    write(files.remote.join("app.log"), "one\ntwo\n");
    let data = |needle: &str| {
        files.fixture.recorder.find("the text of the file", |event| {
            event["event"] == "follow.data"
                && event["follow"] == 7
                && event["text"].as_str().is_some_and(|text| text.contains(needle))
        })
    };

    files
        .fixture
        .ok(json!({ "op": "follow.start", "session": 1, "follow": 7, "path": "/app.log" }));
    data("one\ntwo\n");

    // Was dazukommt, kommt nach – auch ein Zeichen, das mehrere Bytes braucht.
    let mut log = std::fs::OpenOptions::new()
        .append(true)
        .open(files.remote.join("app.log"))
        .unwrap();
    std::io::Write::write_all(&mut log, "drei – grün\n".as_bytes()).unwrap();
    data("drei – grün\n");

    files.fixture.ok(json!({ "op": "follow.stop", "follow": 7 }));
    let end = files.fixture.recorder.event("follow.end");
    assert_eq!((&end["follow"], &end["error"]), (&json!(7), &Value::Null), "{end}");

    // Noch einmal stoppen ist kein Fehler; eine Datei, die es nicht gibt, endet von selbst mit der Meldung.
    files.fixture.ok(json!({ "op": "follow.stop", "follow": 7 }));
    files
        .fixture
        .ok(json!({ "op": "follow.start", "session": 1, "follow": 8, "path": "/nichts.log" }));
    let missing = files.fixture.recorder.event("follow.end");
    assert_eq!(
        (&missing["follow"], &missing["error"]),
        (&json!(8), &json!("command_failed")),
        "{missing}"
    );
    assert!(
        missing["message"].as_str().unwrap().contains("No such file"),
        "{missing}"
    );

    let relative = files
        .fixture
        .request(json!({ "op": "follow.start", "session": 1, "follow": 9, "path": "app.log" }));
    assert_eq!(relative["error"], "unsafe_name", "{relative}");
}

#[test]
fn a_file_is_read_in_parts_and_a_small_one_written_whole() {
    let files = Files::connected();
    let base64 = |text: &str| {
        // Nur für diesen Test: der Kern selbst bringt seine eigene Umsetzung mit.
        let alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let mut out = String::new();
        for chunk in text.as_bytes().chunks(3) {
            let bits = u32::from(chunk[0]) << 16
                | u32::from(*chunk.get(1).unwrap_or(&0)) << 8
                | u32::from(*chunk.get(2).unwrap_or(&0));
            for index in 0..4 {
                out.push(if index <= chunk.len() {
                    alphabet[(bits >> (18 - 6 * index) & 63) as usize] as char
                } else {
                    '='
                });
            }
        }
        out
    };

    // Der Anfang, dann ein Stück aus der Mitte, dann über das Ende hinaus – die Größe der ganzen Datei steht immer dabei.
    let start = files.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "length": 4 }));
    assert_eq!(
        (&start["data"], &start["size"]),
        (&json!(base64("<h1>")), &json!(11)),
        "{start}"
    );
    let middle = files.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "offset": 4, "length": 2 }));
    assert_eq!(middle["data"], base64("hi"), "{middle}");
    let end = files.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "offset": 9, "length": 100 }));
    assert_eq!(end["data"], base64("1>"), "{end}");
    let beyond = files.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "offset": 500, "length": 10 }));
    assert_eq!((&beyond["ok"], &beyond["data"]), (&json!(true), &json!("")), "{beyond}");

    // Ein Ordner und eine Pipe sind keine Dateien zum Lesen; was es nicht gibt, gibt es nicht.
    let folder = files.sftp(json!({ "op": "sftp.read", "path": "/www", "length": 10 }));
    assert_eq!(folder["error"], "not_a_file", "{folder}");
    let missing = files.sftp(json!({ "op": "sftp.read", "path": "/nichts", "length": 10 }));
    assert_eq!(missing["error"], "not_found", "{missing}");

    // Schreiben ersetzt den Inhalt ganz – auch durch einen kürzeren – und legt eine neue Datei an.
    files.done(json!({ "op": "sftp.write", "path": "/www/index.html", "data": base64("neu – grün") }));
    assert_eq!(
        std::fs::read_to_string(files.remote.join("www").join("index.html")).unwrap(),
        "neu – grün"
    );
    files.done(json!({ "op": "sftp.write", "path": "/www/new.conf", "data": "" }));
    assert_eq!(std::fs::read(files.remote.join("www").join("new.conf")).unwrap(), b"");

    let folder = files.sftp(json!({ "op": "sftp.write", "path": "/www", "data": "" }));
    assert_eq!(folder["error"], "not_a_file", "{folder}");
    let garbage = files
        .fixture
        .request(json!({ "op": "sftp.write", "session": 1, "request": 990, "path": "/www/x", "data": "%%%" }));
    assert_eq!(garbage["error"], "bad_request", "{garbage}");
}

/// Verbindet mit einem Server, dessen Konto `sudo` benutzen darf; unter `sudo` zeigt SFTP ein anderes Verzeichnis.
fn with_sudo(password: Option<&str>) -> (Fixture, PathBuf) {
    let password = password.map(str::to_owned);
    let fixture = Fixture::with(move |directory| Options {
        sftp_root: Some(directory.join("remote")),
        sudo: Some(nexus_testserver::Sudo {
            password,
            root: directory.join("as-root"),
        }),
        ..with_password()
    });
    let root = fixture.directory.join("as-root");
    write(fixture.directory.join("remote").join("mine.txt"), "user");
    write(root.join("shadow"), "root only");
    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    (fixture, root)
}

fn sftp_result(fixture: &Fixture, number: u64, mut request: Value) -> Value {
    request["session"] = json!(1);
    request["request"] = json!(number);
    fixture.ok(request);
    fixture.recorder.find("sftp.result", |event| {
        event["event"] == "sftp.result" && event["request"] == number
    })
}

fn listed(result: &Value) -> Vec<&str> {
    result["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| entry["name"].as_str().unwrap())
        .collect()
}

#[test]
fn the_file_browser_becomes_root_through_sudo_and_goes_back() {
    let (fixture, root) = with_sudo(None);
    assert_eq!(
        listed(&sftp_result(&fixture, 1, json!({ "op": "sftp.list", "path": "/" }))),
        ["mine.txt"]
    );

    let elevated = sftp_result(&fixture, 2, json!({ "op": "sftp.elevate" }));
    assert_eq!(elevated["ok"], true, "{elevated}");
    assert_eq!(
        listed(&sftp_result(&fixture, 3, json!({ "op": "sftp.list", "path": "/" }))),
        ["shadow"]
    );

    // Als root lässt sich auch schreiben, was dem Benutzer nicht gehört.
    let written = sftp_result(
        &fixture,
        4,
        json!({ "op": "sftp.write", "path": "/shadow", "data": "bmV1" }),
    );
    assert_eq!(written["ok"], true, "{written}");
    assert_eq!(std::fs::read_to_string(root.join("shadow")).unwrap(), "neu");

    // Zurück zum eigenen Konto.
    let back = sftp_result(&fixture, 5, json!({ "op": "sftp.reopen" }));
    assert_eq!(back["ok"], true, "{back}");
    assert_eq!(
        listed(&sftp_result(&fixture, 6, json!({ "op": "sftp.list", "path": "/" }))),
        ["mine.txt"]
    );
}

#[test]
fn sudo_that_wants_a_password_gets_it_on_its_input_and_a_wrong_one_changes_nothing() {
    let (fixture, _root) = with_sudo(Some("s3cret pw"));

    // Ohne Passwort: Die Antwort sagt, dass eines fehlt – und der Dateibrowser bleibt der des Benutzers.
    let asked = sftp_result(&fixture, 1, json!({ "op": "sftp.elevate" }));
    assert_eq!(
        (&asked["ok"], &asked["error"]),
        (&json!(false), &json!("sudo_password")),
        "{asked}"
    );
    assert_eq!(
        listed(&sftp_result(&fixture, 2, json!({ "op": "sftp.list", "path": "/" }))),
        ["mine.txt"]
    );

    let wrong = sftp_result(&fixture, 3, json!({ "op": "sftp.elevate", "password": "falsch" }));
    assert_eq!(
        (&wrong["ok"], &wrong["error"]),
        (&json!(false), &json!("sudo_refused")),
        "{wrong}"
    );
    assert!(wrong["message"].as_str().unwrap().contains("Sorry"), "{wrong}");
    assert_eq!(
        listed(&sftp_result(&fixture, 4, json!({ "op": "sftp.list", "path": "/" }))),
        ["mine.txt"]
    );

    // Ein Passwort mit Zeilenumbruch wäre zwei Zeilen Eingabe.
    let odd = sftp_result(&fixture, 5, json!({ "op": "sftp.elevate", "password": "a\nb" }));
    assert_eq!(odd["error"], "sudo_refused", "{odd}");

    let right = sftp_result(&fixture, 6, json!({ "op": "sftp.elevate", "password": "s3cret pw" }));
    assert_eq!(right["ok"], true, "{right}");
    assert_eq!(
        listed(&sftp_result(&fixture, 7, json!({ "op": "sftp.list", "path": "/" }))),
        ["shadow"]
    );
}

#[test]
fn without_sudo_on_the_server_the_answer_says_so() {
    let files = Files::connected();
    let result = files.sftp(json!({ "op": "sftp.elevate" }));
    assert_eq!(
        (&result["ok"], &result["error"]),
        (&json!(false), &json!("no_sftp_server")),
        "{result}"
    );
    assert_eq!(files.names("/"), ["www", "readme.txt", "Zebra.log"]);
}

#[test]
fn a_very_large_directory_is_listed_completely_and_in_order() {
    // Ein Server schickt ein Verzeichnis in vielen Antworten; keine davon darf fehlen oder doppelt ankommen.
    // Zum Messen: NEXUS_MANY=100000 mit --release und --nocapture nennt die Zeit.
    let files = Files::connected();
    let count: usize = std::env::var("NEXUS_MANY")
        .ok()
        .and_then(|text| text.parse().ok())
        .unwrap_or(3000);
    let directory = files.remote.join("many");
    std::fs::create_dir(&directory).unwrap();
    for number in 0..count {
        std::fs::write(directory.join(format!("file-{number}.txt")), b"").unwrap();
    }

    let started = std::time::Instant::now();
    let names = files.names("/many");
    eprintln!("{count} entries listed in {:?}", started.elapsed());

    assert_eq!(names.len(), count);
    // Natürlich sortiert: file-2 vor file-10.
    assert_eq!(names[..3], ["file-0.txt", "file-1.txt", "file-2.txt"]);
    assert_eq!(names[count - 1], format!("file-{}.txt", count - 1));
}

/// Kopieren, Verschieben, Prüfsumme, Belegung und Mitlesen an einem echten Server ([`real_server`]): Dort laufen
/// wirklich `cp`, `mv`, `du`, `tail` und das Programm für die Prüfsumme, das es gerade gibt – GNU, BusyBox oder
/// die der BSDs. Mit Namen, an denen eine Befehlszeile gern scheitert.
#[test]
fn copying_sums_usage_and_following_work_on_a_real_server() {
    let Some((files, directory)) = real_server() else {
        return;
    };
    let at = |name: &str| format!("{directory}/{name}");
    // Die Liste nennt Ordner vor Dateien – hier zählt nur, was da ist.
    let sorted = |path: &str| {
        let mut names = files.names(path);
        names.sort();
        names
    };
    files.done(json!({ "op": "sftp.mkdir", "path": at("src dir") }));
    files.done(json!({ "op": "sftp.mkdir", "path": at("src dir/sub") }));
    files.done(json!({ "op": "sftp.write", "path": at("src dir/a b.txt"), "data": "YWJj" }));
    files.done(json!({ "op": "sftp.write", "path": at("src dir/sub/-x"), "data": "eA==" }));

    // Kopieren: ein Ordner samt Inhalt und eine einzelne Datei, in einem Zug.
    files.done(json!({ "op": "sftp.copy", "pairs": [
        { "from": at("src dir"), "to": at("copy of it's") },
        { "from": at("src dir/sub/-x"), "to": at("-y") },
    ] }));
    assert_eq!(sorted(&directory), ["-y", "copy of it's", "src dir"]);
    assert_eq!(sorted(&at("copy of it's")), ["a b.txt", "sub"]);
    assert_eq!(files.names(&at("copy of it's/sub")), ["-x"]);

    // Verschieben.
    files.done(
        json!({ "op": "sftp.copy", "move": true, "pairs": [{ "from": at("-y"), "to": at("src dir/moved here") }] }),
    );
    assert_eq!(sorted(&at("src dir")), ["a b.txt", "moved here", "sub"]);

    // Was es nicht gibt, lässt sich nicht kopieren – und das sagt die Antwort.
    let missing = files.sftp(json!({ "op": "sftp.copy", "pairs": [{ "from": at("nichts"), "to": at("x") }] }));
    assert_eq!(missing["ok"], false, "{missing}");

    // Prüfsumme: SHA-256 von „abc“, für die Kopie dieselbe; für einen Ordner und für nichts gibt es keine.
    let abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    let sums = files.sftp(json!({ "op": "sftp.checksum", "paths": [at("src dir/a b.txt"), at("src dir"), at("nichts"), at("copy of it's/a b.txt")] }));
    assert_eq!(sums["sums"], json!([abc, null, null, abc]), "{sums}");

    // Belegung: je Eintrag eine Zeile mit Pfad und Bytes, auch für Namen mit Leerzeichen und Apostroph.
    let usage = files.sftp(json!({ "op": "sftp.usage", "path": directory }));
    assert_eq!(usage["ok"], true, "{usage}");
    println!("usage: {}", usage["usage"]);
    let lines = usage["usage"].as_array().unwrap();
    for name in ["copy of it's", "src dir"] {
        assert!(
            lines.iter().any(|line| line["path"] == at(name).as_str()),
            "{name}: {usage}"
        );
    }
    assert!(lines.iter().all(|line| line["bytes"].as_u64().is_some()), "{usage}");

    // Mitlesen: was schon dasteht kommt an; nach dem Aufhören endet der Befehl am Server ohne Fehler.
    files.done(json!({ "op": "sftp.write", "path": at("app's log.txt"), "data": "b25lCnR3bwo=" }));
    files
        .fixture
        .ok(json!({ "op": "follow.start", "session": 1, "follow": 7, "path": at("app's log.txt") }));
    let first = files.fixture.recorder.find("the followed lines", |event| {
        event["event"] == "follow.end"
            || (event["event"] == "follow.data"
                && event["follow"] == 7
                && event["text"].as_str().is_some_and(|text| text.contains("two")))
    });
    assert_eq!(first["event"], "follow.data", "{first}");
    files.fixture.ok(json!({ "op": "follow.stop", "follow": 7 }));
    let end = files.fixture.recorder.event("follow.end");
    assert_eq!((&end["follow"], &end["error"]), (&json!(7), &Value::Null), "{end}");

    for name in ["src dir", "copy of it's", "app's log.txt"] {
        files.done(json!({ "op": "sftp.delete", "path": at(name) }));
    }
    assert_eq!(files.names(&directory), Vec::<String>::new());
}

/// Kopieren an einem echten Konto, dem der Server keine Befehle erlaubt ([`real_server`] mit einem Konto, das nur
/// SFTP darf – `ForceCommand internal-sftp` bei OpenSSH): Dort gibt es kein `cp`; der Kern liest und schreibt
/// über SFTP. Verweise bleiben Verweise, Rechte wandern mit.
#[test]
fn copying_without_a_shell_goes_over_sftp_on_a_real_server() {
    let Some((files, directory)) = real_server() else {
        return;
    };
    let at = |name: &str| format!("{directory}/{name}");
    let sorted = |path: &str| {
        let mut names = files.names(path);
        names.sort();
        names
    };
    files.done(json!({ "op": "sftp.mkdir", "path": at("src dir") }));
    files.done(json!({ "op": "sftp.mkdir", "path": at("src dir/sub") }));
    files.done(json!({ "op": "sftp.write", "path": at("src dir/a b.txt"), "data": "YWJj" }));
    files.done(json!({ "op": "sftp.write", "path": at("src dir/sub/-x"), "data": "eA==" }));
    files.done(json!({ "op": "sftp.chmod", "path": at("src dir/a b.txt"), "mode": 0o640 }));
    files.done(json!({ "op": "sftp.symlink", "path": at("src dir/link"), "to": at("src dir/a b.txt") }));

    let pairs = json!({ "op": "sftp.copy", "pairs": [
        { "from": at("src dir"), "to": at("copy of it's") },
        { "from": at("src dir/sub/-x"), "to": at("-y") },
    ] });
    let first = files.sftp(pairs.clone());
    // Der SFTP-Server von Synology DSM liest keinen Link (`readlink` sagt immer „No such file“): Dann nennt die
    // Antwort den Link, und es geht ohne ihn weiter.
    let readable = first["message"]
        .as_str()
        .is_none_or(|said| !said.contains("cannot read this link"));
    if !readable {
        println!("links: {}", first["message"]);
        files.done(json!({ "op": "sftp.delete", "path": at("copy of it's") }));
        files.done(json!({ "op": "sftp.delete", "path": at("src dir/link") }));
        files.done(pairs);
    } else {
        assert_eq!(first["ok"], true, "{first}");
    }
    assert_eq!(sorted(&directory), ["-y", "copy of it's", "src dir"]);
    let expected: &[&str] = if readable {
        &["a b.txt", "link", "sub"]
    } else {
        &["a b.txt", "sub"]
    };
    assert_eq!(sorted(&at("copy of it's")), expected);
    assert_eq!(sorted(&at("copy of it's/sub")), ["-x"]);

    let copied = files.sftp(json!({ "op": "sftp.list", "path": at("copy of it's") }));
    println!("copy: {}", copied["entries"]);
    let entry = |name: &str| {
        copied["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|entry| entry["name"] == name)
            .unwrap()
            .clone()
    };
    // Die Rechte der Quelle – 0640, außer der Server nimmt kein `chmod` an (Synology DSM mit ACLs).
    let source = files.sftp(json!({ "op": "sftp.list", "path": at("src dir") }));
    let rights = source["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == "a b.txt")
        .unwrap()["permissions"]
        .clone();
    assert_eq!(
        (&entry("a b.txt")["size"], &entry("a b.txt")["permissions"]),
        (&json!(3), &rights)
    );
    if readable {
        assert_eq!(entry("link")["link"], true);
    }
    let content =
        files.sftp(json!({ "op": "sftp.read", "path": at("copy of it's/a b.txt"), "offset": 0, "length": 10 }));
    assert_eq!(content["data"], "YWJj", "{content}");

    // Was es am Ziel schon gibt, bleibt.
    let again = files.sftp(json!({ "op": "sftp.copy", "pairs": [{ "from": at("src dir/a b.txt"), "to": at("-y") }] }));
    assert_eq!(
        (&again["ok"], &again["error"]),
        (&json!(false), &json!("exists")),
        "{again}"
    );

    for name in ["src dir", "copy of it's", "-y"] {
        files.done(json!({ "op": "sftp.delete", "path": at(name) }));
    }
    assert_eq!(files.names(&directory), Vec::<String>::new());
}

/// Die Regeln für das, was es am Ziel schon gibt (`keep`): Statt alles zu überschreiben, bleibt stehen, was gleich
/// groß und nicht älter ist (`same`) bzw. was nicht älter ist (`newer`) – für jede Datei einzeln, auch im Ordner.
#[test]
fn a_rule_keeps_what_is_already_there_instead_of_overwriting_it() {
    let files = Files::connected();
    let aged = |path: &std::path::Path, content: &str, seconds_ago: u64| {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, content).unwrap();
        std::fs::File::options()
            .write(true)
            .open(path)
            .unwrap()
            .set_modified(std::time::SystemTime::now() - std::time::Duration::from_secs(seconds_ago))
            .unwrap();
    };
    let read = |path: std::path::PathBuf| std::fs::read_to_string(path).unwrap();
    let (here, there) = (files.local.join("site"), files.remote.join("site"));
    let start = |transfer: u64, direction: &str, local: &std::path::Path, remote: &str, keep: &str| {
        files.fixture.ok(json!({
            "op": "transfer.start", "session": 1, "transfer": transfer, "direction": direction,
            "local": local.display().to_string(), "remote": remote, "keep": keep,
        }));
        let end = files.finished(transfer);
        assert_eq!(end["state"], "completed", "{end}");
        end
    };

    // Hier vier Dateien; am Server drei davon schon: eine gleich große und jüngere, eine anders große und
    // jüngere, eine gleich große, aber ältere.
    aged(&here.join("same.txt"), "12345", 1000);
    aged(&here.join("changed.txt"), "new content", 1000);
    aged(&here.join("sub").join("older.txt"), "fresh", 100);
    aged(&here.join("new.txt"), "brand new", 1000);
    aged(&there.join("same.txt"), "abcde", 500);
    aged(&there.join("changed.txt"), "x", 500);
    aged(&there.join("sub").join("older.txt"), "stale", 5000);

    // „Gleiche überspringen“: Nur was gleich groß und nicht älter ist, bleibt.
    let end = start(1, "upload", &here, "/", "same");
    assert_eq!(read(there.join("same.txt")), "abcde");
    assert_eq!(read(there.join("changed.txt")), "new content");
    assert_eq!(read(there.join("sub").join("older.txt")), "fresh");
    assert_eq!(read(there.join("new.txt")), "brand new");
    assert_eq!((&end["files"], &end["files_done"]), (&json!(4), &json!(4)), "{end}");

    // „Nur Neuere“: Auch eine anders große Datei bleibt, wenn sie am Ziel nicht älter ist.
    aged(&there.join("changed.txt"), "x", 500);
    aged(&there.join("sub").join("older.txt"), "stale", 5000);
    start(2, "upload", &here, "/", "newer");
    assert_eq!(read(there.join("changed.txt")), "x");
    assert_eq!(read(there.join("sub").join("older.txt")), "fresh");
    assert_eq!(read(there.join("same.txt")), "abcde");

    // Dasselbe beim Herunterladen: Was hier schon so liegt, bleibt; der Rest kommt.
    let down = files.local.join("down");
    aged(&down.join("site").join("same.txt"), "LOCAL", 10);
    aged(&down.join("site").join("new.txt"), "tiny", 10);
    start(3, "download", &down, "/site", "same");
    assert_eq!(read(down.join("site").join("same.txt")), "LOCAL");
    assert_eq!(read(down.join("site").join("new.txt")), "brand new");
    assert_eq!(read(down.join("site").join("sub").join("older.txt")), "fresh");

    // Eine Regel, die es nicht gibt, ist keine Anfrage.
    let odd = files.fixture.request(json!({
        "op": "transfer.start", "session": 1, "transfer": 4, "direction": "upload",
        "local": here.display().to_string(), "remote": "/", "keep": "everything",
    }));
    assert_eq!(odd["ok"], false, "{odd}");
}

/// Ausschlüsse: Was auf ein Muster passt, bleibt bei einem Ordner-Transfer aus – in jede Richtung, auch von Server
/// zu Server –, und ein ausgeschlossener Ordner wird nicht betreten. Die Auskunft vorab nennt, was fehlen würde.
#[test]
fn a_folder_transfer_leaves_out_what_the_patterns_name() {
    let files = Files::connected();
    let project = |root: &std::path::Path| {
        write(root.join("src").join("main.rs"), "fn main() {}");
        write(root.join("src").join("old.tmp"), "tmp");
        write(root.join(".git").join("config"), "[core]");
        write(
            root.join("web").join("node_modules").join("left-pad").join("index.js"),
            "x",
        );
        write(root.join("web").join("app.js"), "app");
        write(root.join("build").join("app.map"), "map");
        write(root.join("build").join("app.js"), "built");
        write(root.join("notes.TMP"), "tmp");
    };
    let patterns = json!([".git", "node_modules", "*.tmp", "build/*.map"]);
    let arrived = |root: &std::path::Path| {
        assert_eq!(
            std::fs::read_to_string(root.join("src").join("main.rs")).unwrap(),
            "fn main() {}"
        );
        assert_eq!(std::fs::read_to_string(root.join("web").join("app.js")).unwrap(), "app");
        assert_eq!(
            std::fs::read_to_string(root.join("build").join("app.js")).unwrap(),
            "built"
        );
        for missing in [".git", "web/node_modules", "src/old.tmp", "build/app.map", "notes.TMP"] {
            assert!(!root.join(missing).exists(), "{missing} ist mitgekommen");
        }
    };

    // Hinauf.
    project(&files.local.join("proj"));
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "upload",
        "local": files.local.join("proj"), "remote": "/", "exclude": patterns,
    }));
    let up = files.finished(1);
    assert_eq!(up["state"], "completed", "{up}");
    assert_eq!((&up["total"], &up["files"]), (&json!(12 + 3 + 5), &json!(3)), "{up}");
    arrived(&files.remote.join("proj"));

    // Die Auskunft vorab: der Baum ohne das Ausgelassene – und, auf Wunsch, genau das Ausgelassene (jeweils der
    // oberste Eintrag; in einen ausgelassenen Ordner wird nicht hineingesehen).
    project(&files.remote.join("full"));
    let names = |result: &serde_json::Value| -> Vec<(String, bool)> {
        let mut names: Vec<_> = result["tree"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| {
                (
                    entry["remote"].as_str().unwrap().to_owned(),
                    entry["directory"].as_bool().unwrap(),
                )
            })
            .collect();
        names.sort();
        names
    };
    let left = files.sftp(json!({ "op": "sftp.tree", "path": "/full", "exclude": patterns, "excluded": true }));
    assert_eq!(
        names(&left),
        [
            ("/full/.git".to_owned(), true),
            ("/full/build/app.map".to_owned(), false),
            ("/full/notes.TMP".to_owned(), false),
            ("/full/src/old.tmp".to_owned(), false),
            ("/full/web/node_modules".to_owned(), true),
        ],
        "{left}"
    );
    let kept = files.sftp(json!({ "op": "sftp.tree", "path": "/full", "exclude": patterns }));
    assert_eq!(kept["tree"].as_array().unwrap().len(), 4 + 3, "{kept}");
    assert_eq!(
        files.sftp(json!({ "op": "sftp.tree", "path": "/full" }))["tree"]
            .as_array()
            .unwrap()
            .len(),
        7 + 8
    );

    // Herunter.
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 2, "direction": "download",
        "local": files.local, "remote": "/full", "exclude": patterns,
    }));
    let down = files.finished(2);
    assert_eq!(down["state"], "completed", "{down}");
    assert_eq!(down["files"], 3, "{down}");
    arrived(&files.local.join("full"));

    // Von Server zu Server (hier: derselbe Server als Ziel).
    files.fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 3, "direction": "relay",
        "local": "/copy", "remote": "/full", "target_session": 1, "exclude": patterns,
    }));
    std::fs::create_dir_all(files.remote.join("copy")).unwrap();
    let across = files.finished(3);
    assert_eq!(across["state"], "completed", "{across}");
    arrived(&files.remote.join("copy").join("full"));

    // Ohne Muster geht alles mit, wie immer.
    files.start(4, "download", &files.local.join("all"), "/full");
    std::fs::create_dir_all(files.local.join("all")).unwrap();
    assert_eq!(files.finished(4)["files"], 8);

    // Ein Muster, das keines sein kann, startet nichts.
    for bad in [json!(["../x"]), json!([""]), json!([r"a\b"])] {
        let refused = files.fixture.request(json!({
            "op": "transfer.start", "session": 1, "transfer": 9, "direction": "download",
            "local": files.local, "remote": "/full", "exclude": bad,
        }));
        assert_eq!(refused["error"], "invalid_exclude", "{refused}");
        let tree = files
            .fixture
            .request(json!({ "op": "sftp.tree", "session": 1, "request": 999, "path": "/full", "exclude": bad }));
        assert_eq!(tree["error"], "invalid_exclude", "{tree}");
    }
}
