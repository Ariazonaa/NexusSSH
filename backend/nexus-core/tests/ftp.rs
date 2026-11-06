//! FTP und FTPS als Dateiziel: Anmeldung, Dateien, Transfers – und die Frage nach einem Zertifikat, das das
//! System nicht kennt. Gegenstelle ist der kleine FTP-Server des Testservers.

mod common;

use std::path::PathBuf;

use common::Fixture;
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::{FtpOptions, Options, start_ftp};
use serde_json::{Value, json};
use tokio::runtime::Runtime;

struct Ftp {
    fixture: Fixture,
    /// Erscheint dem Kern per FTP als `/`.
    remote: PathBuf,
    profile: String,
    port: u16,
    next: std::cell::Cell<u64>,
    /// Die Sitzung, die zuletzt geöffnet wurde – ihr gelten Zustand und Dateibefehle.
    session: std::cell::Cell<u64>,
    _runtime: Runtime,
}

/// Ein selbst signiertes Zertifikat für 127.0.0.1 – eines, das kein System kennt.
fn certificate() -> (Vec<u8>, Vec<u8>) {
    let made = rcgen::generate_simple_self_signed(vec!["127.0.0.1".to_owned()]).unwrap();
    (made.cert.der().to_vec(), made.signing_key.serialize_der())
}

impl Ftp {
    fn start(tls: Option<(Vec<u8>, Vec<u8>)>, plain_list: bool) -> Self {
        Self::start_with(tls, plain_list, false)
    }

    /// Mit `active_only` lehnt der Server den passiven Modus ab, und das Profil verlangt den aktiven.
    fn start_with(tls: Option<(Vec<u8>, Vec<u8>)>, plain_list: bool, active_only: bool) -> Self {
        Self::start_as(tls, plain_list, active_only, false, false)
    }

    /// Mit `refuse_active` verlangt das Profil den aktiven Modus, und der Server lehnt `PORT` und `EPRT` ab.
    /// Mit `latin1` schreibt der Server Dateinamen als ISO-8859-1, und das Profil nennt diese Kodierung.
    fn start_as(
        tls: Option<(Vec<u8>, Vec<u8>)>,
        plain_list: bool,
        active_only: bool,
        refuse_active: bool,
        latin1: bool,
    ) -> Self {
        Self::start_like(tls, plain_list, active_only, refuse_active, latin1, false, false)
    }

    /// Mit `forgets_restart` vergisst der Server die Stelle von `REST`, sobald ein anderer Befehl dazwischenkommt.
    /// Mit `ipv6` lauscht er auf `::1` und lehnt `PASV` ab.
    fn start_like(
        tls: Option<(Vec<u8>, Vec<u8>)>,
        plain_list: bool,
        active_only: bool,
        refuse_active: bool,
        latin1: bool,
        forgets_restart: bool,
        ipv6: bool,
    ) -> Self {
        Self::launch(FtpOptions {
            port: 0,
            login: ("demo".to_owned(), "geheim".to_owned()),
            root: std::path::PathBuf::new(),
            tls,
            plain_list,
            active_only,
            refuse_active,
            blocked_active: false,
            latin1,
            forgets_restart,
            ipv6,
        })
    }

    /// Startet den Server mit `options` (den Ordner setzt diese Funktion) und ein Profil, das zu ihm passt: ohne
    /// TLS, wenn er keines hat, im aktiven Modus, wenn eine der Optionen dazu ihn verlangt.
    fn launch(mut options: FtpOptions) -> Self {
        let fixture = Fixture::new(Options::default());
        let remote = fixture.directory.join("ftp");
        std::fs::create_dir_all(remote.join("www")).unwrap();
        std::fs::write(remote.join("readme.txt"), "hello").unwrap();
        std::fs::write(remote.join("www").join("index.html"), "<h1>hi</h1>").unwrap();
        options.root = remote.clone();

        let runtime = Runtime::new().unwrap();
        let secure = options.tls.is_some();
        let (latin1, ipv6) = (options.latin1, options.ipv6);
        let active = options.active_only || options.refuse_active || options.blocked_active;
        let address = runtime.block_on(start_ftp(options)).unwrap();

        let host = if ipv6 { "::1" } else { "127.0.0.1" };
        let mut profile =
            json!({ "name": "ftp", "host": host, "port": address.port(), "user": "demo", "protocol": "ftp" });
        if !secure {
            profile["ftp_tls"] = json!("none");
        }
        if latin1 {
            profile["ftp_encoding"] = json!("latin1");
        }
        if active {
            profile["ftp_active"] = json!(true);
        }

        let profile = fixture.ok(json!({ "op": "profiles.save", "profile": profile }))["profile"]["id"]
            .as_str()
            .unwrap()
            .to_owned();
        Self {
            fixture,
            remote,
            profile,
            port: address.port(),
            next: std::cell::Cell::new(1),
            session: std::cell::Cell::new(1),
            _runtime: runtime,
        }
    }

    fn open(&self, session: u64) {
        self.session.set(session);
        self.fixture.ok(
            json!({ "op": "session.open", "session": session, "profile_id": self.profile, "cols": 80, "rows": 24 }),
        );
    }

    fn connected(&self) -> Value {
        let session = self.session.get();
        self.fixture.recorder.find("connected", |event| {
            event["event"] == "session.state"
                && event["session"] == session
                && matches!(event["state"].as_str(), Some("connected" | "closed"))
        })
    }

    fn sftp(&self, mut request: Value) -> Value {
        let number = self.next.get();
        self.next.set(number + 1);
        request["session"] = json!(self.session.get());
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
        let mut names: Vec<String> = result["entries"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| entry["name"].as_str().unwrap().to_owned())
            .collect();
        names.sort();
        names
    }
}

#[test]
fn plain_ftp_signs_in_with_the_saved_password_warns_and_shows_the_files() {
    let ftp = Ftp::start(None, false);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();

    ftp.open(1);
    let connected = ftp.connected();
    assert_eq!(
        (&connected["state"], &connected["home"]),
        (&json!("connected"), &json!("/")),
        "{connected}"
    );

    // Kein Terminal – und der Hinweis, dass hier nichts verschlüsselt ist.
    ftp.fixture
        .recorder
        .find("no terminal", |event| event["event"] == "session.files_only");
    let warning = ftp
        .fixture
        .recorder
        .find("the warning", |event| event["event"] == "core.warning");
    assert_eq!(warning["code"], "ftp_unencrypted", "{warning}");

    // Die Auslastung gibt es über FTP nicht – das sagt die Sitzung, statt die App warten zu lassen.
    ftp.fixture
        .ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    ftp.fixture
        .recorder
        .find("no load", |event| event["event"] == "monitor.unavailable");

    assert_eq!(ftp.names("/"), ["readme.txt", "www"]);
    assert_eq!(ftp.names("/www"), ["index.html"]);
    let listed = ftp.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let readme = listed["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == "readme.txt")
        .unwrap();
    assert_eq!(
        (&readme["kind"], &readme["size"]),
        (&json!("file"), &json!(5)),
        "{readme}"
    );

    // Anlegen, umbenennen (ohne zu überschreiben), löschen.
    ftp.done(json!({ "op": "sftp.mkdir", "path": "/my folder" }));
    ftp.done(json!({ "op": "sftp.touch", "path": "/my folder/new.txt" }));
    assert!(ftp.remote.join("my folder").join("new.txt").is_file());
    ftp.done(json!({ "op": "sftp.rename", "from": "/my folder/new.txt", "to": "/my folder/renamed.txt" }));
    let taken = ftp.sftp(json!({ "op": "sftp.rename", "from": "/readme.txt", "to": "/my folder/renamed.txt" }));
    assert_eq!(taken["ok"], false, "{taken}");
    ftp.done(json!({ "op": "sftp.delete", "path": "/my folder" }));
    assert!(!ftp.remote.join("my folder").exists());

    // Ein Name mit Zeilenumbruch wäre in der Steuerverbindung ein zweiter Befehl – er kommt dort nie an.
    let injected = ftp.sftp(json!({ "op": "sftp.mkdir", "path": "/a\r\nDELE readme.txt" }));
    assert_eq!(injected["ok"], false, "{injected}");
    assert!(ftp.remote.join("readme.txt").is_file());
}

#[cfg(unix)]
#[test]
fn a_link_in_the_plain_list_names_its_target() {
    let ftp = Ftp::start(None, true);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    std::os::unix::fs::symlink("readme.txt", ftp.remote.join("latest")).unwrap();
    ftp.open(1);
    ftp.connected();

    // `LIST` nennt das Ziel hinter dem Namen: Es steht an dem Eintrag und lässt sich einzeln erfragen.
    let listed = ftp.sftp(json!({ "op": "sftp.list", "path": "/" }));
    let link = listed["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["name"] == "latest")
        .unwrap_or_else(|| panic!("der Link fehlt: {listed}"));
    assert_eq!(
        (&link["link"], &link["target"]),
        (&json!(true), &json!("readme.txt")),
        "{link}"
    );
    assert!(link.get("dangling").is_none(), "{link}");

    let target = ftp.sftp(json!({ "op": "sftp.readlink", "path": "/latest" }));
    assert_eq!(
        (&target["ok"], &target["target"]),
        (&json!(true), &json!("readme.txt")),
        "{target}"
    );

    // Eine Datei ist kein Link, und was es nicht gibt, gibt es nicht – über FTP weiß die Liste nicht mehr.
    let file = ftp.sftp(json!({ "op": "sftp.readlink", "path": "/readme.txt" }));
    assert_eq!(
        (&file["ok"], &file["error"]),
        (&json!(false), &json!("unsupported")),
        "{file}"
    );
    let missing = ftp.sftp(json!({ "op": "sftp.readlink", "path": "/nope" }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found")),
        "{missing}"
    );

    // Die Wurzel hat keinen Namen in einer Liste: Die Antwort kommt, ohne dass der Server danach gefragt wird.
    let root = ftp.sftp(json!({ "op": "sftp.readlink", "path": "/" }));
    assert_eq!(
        (&root["ok"], &root["error"]),
        (&json!(false), &json!("not_found")),
        "{root}"
    );
}

#[test]
fn ftp_reads_parts_writes_files_and_transfers_folders() {
    let ftp = Ftp::start(None, true);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(1);
    let state = ftp.connected();
    assert_eq!(state["state"], "connected", "{state}");

    // Dieser Server kennt nur die gewöhnliche Liste – sie wird genauso gelesen.
    assert_eq!(ftp.names("/"), ["readme.txt", "www"]);

    // Stücke lesen (Betrachter) und kleine Dateien schreiben (Editor).
    let middle = ftp.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "offset": 4, "length": 2 }));
    assert_eq!(
        (&middle["data"], &middle["size"]),
        (&json!("aGk="), &json!(11)),
        "{middle}"
    );
    ftp.done(json!({ "op": "sftp.write", "path": "/new.bin", "data": "AAH/Zm9vCg==" }));
    assert_eq!(
        std::fs::read(ftp.remote.join("new.bin")).unwrap(),
        [0, 1, 255, b'f', b'o', b'o', b'\n']
    );
    ftp.done(json!({ "op": "sftp.write", "path": "/new.bin", "data": "" }));
    assert_eq!(std::fs::read(ftp.remote.join("new.bin")).unwrap(), b"");

    // Ein Ordner mit mehreren Dateien hin und zurück – mehrere Übertragungen zugleich, jede auf ihrer Verbindung.
    let here = ftp.fixture.directory.join("local");
    std::fs::create_dir_all(here.join("up").join("sub")).unwrap();
    let content: Vec<u8> = (0..600_000u32).map(|index| (index % 251) as u8).collect();
    for name in ["a.bin", "b.bin", "c.bin"] {
        std::fs::write(here.join("up").join(name), &content).unwrap();
    }
    std::fs::write(here.join("up").join("sub").join("small.txt"), "klein").unwrap();

    let finished = |transfer: u64| {
        ftp.fixture.recorder.find("the end of the transfer", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        })
    };
    ftp.fixture.ok(json!({
        "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
        "local": here.join("up").display().to_string(), "remote": "/",
    }));
    let up = finished(1);
    assert_eq!(up["state"], "completed", "{up}");
    for name in ["a.bin", "b.bin", "c.bin"] {
        assert_eq!(
            std::fs::read(ftp.remote.join("up").join(name)).unwrap(),
            content,
            "{name}"
        );
    }
    assert_eq!(
        std::fs::read_to_string(ftp.remote.join("up").join("sub").join("small.txt")).unwrap(),
        "klein"
    );

    std::fs::create_dir_all(here.join("down")).unwrap();
    ftp.fixture.ok(json!({
        "op": "transfer.start", "transfer": 2, "session": 1, "direction": "download",
        "local": here.join("down").display().to_string(), "remote": "/up",
    }));
    let down = finished(2);
    assert_eq!(down["state"], "completed", "{down}");
    assert_eq!(
        std::fs::read(here.join("down").join("up").join("b.bin")).unwrap(),
        content
    );
}

/// Über IPv6 gibt es kein `PASV` (RFC 2428) – IIS lehnt es ab, und jede Liste scheiterte (gefunden 2026-10-15).
/// Der Kern nimmt dort `EPSV`: Listen, Lesen, Schreiben und Ordner gehen wie über IPv4, mit und ohne TLS.
#[test]
fn over_ipv6_the_data_connections_use_extended_passive_mode() {
    if std::net::TcpListener::bind("[::1]:0").is_err() {
        eprintln!("kein IPv6 auf diesem Rechner – übersprungen");
        return;
    }
    for tls in [None, Some(certificate())] {
        let secure = tls.is_some();
        let ftp = Ftp::start_like(tls, false, false, false, false, false, true);
        ftp.fixture
            .secrets
            .set(&password_key(&ftp.profile), "demo", "geheim")
            .unwrap();
        ftp.open(1);
        if secure {
            let asked = ftp.fixture.recorder.event("prompt.host_key");
            ftp.fixture.answer(&asked, json!({ "accept": true }));
        }
        let state = ftp.connected();
        assert_eq!(state["state"], "connected", "tls {secure}: {state}");

        assert_eq!(ftp.names("/"), ["readme.txt", "www"], "tls {secure}");
        let read = ftp.sftp(json!({ "op": "sftp.read", "path": "/readme.txt", "offset": 0, "length": 5 }));
        assert_eq!(read["data"], "aGVsbG8=", "tls {secure}: {read}");
        ftp.done(json!({ "op": "sftp.write", "path": "/six.txt", "data": "c2l4" }));
        assert_eq!(std::fs::read(ftp.remote.join("six.txt")).unwrap(), b"six");

        let here = ftp.fixture.directory.join("down");
        std::fs::create_dir_all(&here).unwrap();
        ftp.fixture.ok(json!({
            "op": "transfer.start", "transfer": 1, "session": 1, "direction": "download",
            "local": here.display().to_string(), "remote": "/www",
        }));
        let down = ftp.fixture.recorder.find("the end of the download", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == 1
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        });
        assert_eq!(down["state"], "completed", "tls {secure}: {down}");
        assert_eq!(
            std::fs::read_to_string(here.join("www").join("index.html")).unwrap(),
            "<h1>hi</h1>"
        );
    }
}

/// Ein Server, der die Stelle von `REST` vergisst, sobald `PASV` dazwischenkommt (FileZilla Server): Gelesen wird
/// trotzdem ab der richtigen Stelle, und an eine Datei wird angehängt, statt sie durch ihr Ende zu ersetzen.
/// Seit suppaftp 12.2.1 schickt die Bibliothek `REST` erst unmittelbar vor dem Befehl der Übertragung – dann kommt
/// nichts mehr dazwischen, die Probe sagt „hält“, und einen Hinweis gibt es bei keinem der beiden Server.
#[test]
fn a_server_that_forgets_the_restart_position_still_gives_and_takes_the_right_bytes() {
    for forgets in [true, false] {
        let ftp = Ftp::start_like(None, false, false, false, false, forgets, false);
        ftp.fixture
            .secrets
            .set(&password_key(&ftp.profile), "demo", "geheim")
            .unwrap();
        // Was am Server liegt, bevor die Sitzung hinsieht: eine ganze Datei und die erste Hälfte eines Uploads.
        let content: Vec<u8> = (0..300_000u32).map(|index| (index % 239) as u8).collect();
        std::fs::write(ftp.remote.join("big.bin"), &content).unwrap();
        std::fs::write(ftp.remote.join("half.bin"), &content[..150_000]).unwrap();
        ftp.open(1);
        assert_eq!(ftp.connected()["state"], "connected");

        for offset in [4usize, 70_000, 299_990] {
            let part = ftp.sftp(json!({ "op": "sftp.read", "path": "/big.bin", "offset": offset, "length": 6 }));
            let expected = base64(&content[offset..offset + 6]);
            assert_eq!(
                part["data"],
                expected.as_str(),
                "forgets {forgets}, offset {offset}: {part}"
            );
        }
        let middle = ftp.sftp(json!({ "op": "sftp.read", "path": "/www/index.html", "offset": 4, "length": 2 }));
        assert_eq!(middle["data"], "aGk=", "forgets {forgets}: {middle}");

        // Ein Upload, der mittendrin abriss, wird fortgesetzt: Am Server liegt die erste Hälfte.
        let here = ftp.fixture.directory.join("local");
        std::fs::create_dir_all(&here).unwrap();
        std::fs::write(here.join("half.bin"), &content).unwrap();
        ftp.fixture.ok(json!({
            "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
            "local": here.join("half.bin").display().to_string(), "remote": "/", "resume": 150_000,
        }));
        let up = ftp.fixture.recorder.find("the end of the transfer", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == 1
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        });
        assert_eq!(up["state"], "completed", "forgets {forgets}: {up}");
        assert!(
            std::fs::read(ftp.remote.join("half.bin")).unwrap() == content,
            "forgets {forgets}: the file on the server is not the local one"
        );

        // Die Stelle hält jetzt auch der vergessliche Server (siehe oben): kein Hinweis.
        let told: Vec<Value> = ftp
            .fixture
            .recorder
            .all("core.warning")
            .into_iter()
            .filter(|warning| warning["code"] == "ftp_no_restart")
            .collect();
        assert!(told.is_empty(), "forgets {forgets}: {told:?}");
    }
}

/// Eine Datei entsteht am Server, nachdem der Kern die Liste gelesen hat (die gilt ein paar Sekunden): Der Upload
/// hielt sie für neu, schrieb in die Teil-Datei, und das Umbenennen scheiterte – mit „Failure“. Jetzt sagt der
/// Transfer, woran es lag; die Datei am Server bleibt, wie sie war, und keine Teil-Datei bleibt liegen.
#[test]
fn a_file_that_appears_during_an_upload_is_named_as_the_reason_and_left_alone() {
    let ftp = Ftp::start(None, false);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(1);
    assert_eq!(ftp.connected()["state"], "connected");

    assert_eq!(ftp.names("/"), ["readme.txt", "www"]);
    std::fs::write(ftp.remote.join("late.txt"), "from someone else").unwrap();
    let here = ftp.fixture.directory.join("late.txt");
    std::fs::write(&here, "mine").unwrap();
    ftp.fixture.ok(json!({
        "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
        "local": here.display().to_string(), "remote": "/",
    }));
    let up = ftp.fixture.recorder.find("the end of the transfer", |event| {
        event["event"] == "transfer.state"
            && event["transfer"] == 1
            && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
    });

    assert_eq!(up["state"], "failed", "{up}");
    assert!(up["message"].as_str().unwrap_or_default().starts_with("exists"), "{up}");
    assert_eq!(
        std::fs::read_to_string(ftp.remote.join("late.txt")).unwrap(),
        "from someone else"
    );
    assert!(
        !std::fs::read_dir(&ftp.remote).unwrap().any(|entry| entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .ends_with(".nexus-part")),
        "a part file was left behind"
    );
}

fn base64(bytes: &[u8]) -> String {
    const LETTERS: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut text = String::new();
    for chunk in bytes.chunks(3) {
        let group = chunk.iter().fold(0u32, |all, byte| all << 8 | u32::from(*byte)) << (8 * (3 - chunk.len()));
        for index in 0..4 {
            if index <= chunk.len() {
                text.push(LETTERS[(group >> (18 - 6 * index) & 63) as usize] as char);
            } else {
                text.push('=');
            }
        }
    }

    text
}

#[test]
fn a_wrong_password_is_asked_again_and_a_cancelled_question_ends_the_session() {
    let ftp = Ftp::start(None, false);
    ftp.open(1);

    let first = ftp.fixture.recorder.event("prompt.secret");
    assert_eq!(
        (&first["kind"], &first["attempt"]),
        (&json!("password"), &json!(1)),
        "{first}"
    );
    ftp.fixture.answer(&first, json!({ "values": ["falsch"] }));
    let second = ftp.fixture.recorder.event("prompt.secret");
    assert_eq!(second["attempt"], 2, "{second}");
    ftp.fixture
        .answer(&second, json!({ "values": ["geheim"], "save": true }));
    let state = ftp.connected();
    assert_eq!(state["state"], "connected", "{state}");
    assert_eq!(
        ftp.fixture.secrets.get(&password_key(&ftp.profile)).as_deref(),
        Some("geheim")
    );

    // Eine zweite Sitzung, deren Frage niemand beantwortet haben will.
    ftp.fixture.secrets.delete(&password_key(&ftp.profile));
    ftp.open(2);
    let asked = ftp.fixture.recorder.event("prompt.secret");
    ftp.fixture.answer(&asked, json!({}));
    let closed = ftp.fixture.recorder.find("the end", |event| {
        event["event"] == "session.state" && event["session"] == 2 && event["state"] == "closed"
    });
    assert_eq!(closed["reason"], "auth_failed", "{closed}");
}

#[test]
fn ftps_asks_about_a_certificate_the_system_does_not_know_and_remembers_the_answer() {
    let mut ftp = Ftp::start(Some(certificate()), false);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();

    // Abgelehnt: keine Verbindung, kein Passwort unterwegs.
    ftp.open(1);
    let asked = ftp.fixture.recorder.event("prompt.host_key");
    assert_eq!(
        (&asked["algorithm"], &asked["changed"]),
        (&json!("x509"), &json!(false)),
        "{asked}"
    );
    let fingerprint = asked["fingerprint"].as_str().unwrap().to_owned();
    assert!(fingerprint.starts_with("SHA256:"), "{asked}");
    ftp.fixture.answer(&asked, json!({ "accept": false }));
    let refused = ftp.connected();
    assert_eq!(
        (&refused["state"], &refused["reason"]),
        (&json!("closed"), &json!("host_key_rejected")),
        "{refused}"
    );

    // Zugestimmt: verbunden, die Dateien sind da – auch über die verschlüsselte Datenverbindung.
    ftp.open(2);
    let again = ftp.fixture.recorder.event("prompt.host_key");
    assert_eq!(again["fingerprint"], fingerprint.as_str());
    ftp.fixture.answer(&again, json!({ "accept": true }));
    let state = ftp.connected();
    assert_eq!(state["state"], "connected", "{state}");
    assert_eq!(ftp.names("/"), ["readme.txt", "www"]);
    ftp.done(json!({ "op": "sftp.write", "path": "/secure.txt", "data": "aGk=" }));
    assert_eq!(std::fs::read(ftp.remote.join("secure.txt")).unwrap(), b"hi");

    // Beim nächsten Mal wird nicht mehr gefragt – auch nach einem Neustart des Kerns.
    ftp.fixture.ok(json!({ "op": "session.close", "session": 2 }));
    ftp.fixture.restart_core();
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(3);
    let state = ftp.connected();
    assert_eq!(state["state"], "connected", "{state}");
    assert!(
        !ftp.fixture
            .recorder
            .all("prompt.host_key")
            .iter()
            .any(|event| event["prompt"] != asked["prompt"] && event["prompt"] != again["prompt"])
    );

    // Zeigt der Server später ein anderes Zertifikat, wird wieder gefragt – mit dem Hinweis auf das bisherige.
    ftp.fixture.ok(json!({ "op": "session.close", "session": 3 }));
    let trust = ftp.fixture.directory.join("data").join("tls_trust.json");
    let server = format!("127.0.0.1:{}", ftp.port);
    std::fs::write(&trust, json!({ server: "SHA256:something-else" }).to_string()).unwrap();
    ftp.fixture.restart_core();
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(4);
    let changed = ftp.fixture.recorder.event("prompt.host_key");
    assert_eq!(
        (
            &changed["changed"],
            &changed["previous_fingerprint"],
            &changed["fingerprint"]
        ),
        (&json!(true), &json!("SHA256:something-else"), &json!(fingerprint)),
        "{changed}"
    );
    ftp.fixture.answer(&changed, json!({ "accept": false }));
}

#[test]
fn ftps_does_not_fall_back_to_plain_ftp_when_the_server_has_no_tls() {
    // Das Profil verlangt TLS (die Vorgabe); der Server kann es nicht.
    let ftp = Ftp::start(None, false);
    ftp.fixture.ok(json!({ "op": "profiles.save", "profile": {
        "id": ftp.profile, "name": "ftp", "host": "127.0.0.1", "port": ftp.port, "user": "demo", "protocol": "ftp"
    } }));
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();

    ftp.open(1);
    let closed = ftp.connected();
    assert_eq!(
        (&closed["state"], &closed["reason"]),
        (&json!("closed"), &json!("connect_failed")),
        "{closed}"
    );
    assert!(!ftp.fixture.recorder.saw("prompt.secret"));
}

#[test]
fn in_active_mode_the_server_opens_the_data_connections_plain_and_over_tls() {
    for tls in [None, Some(certificate())] {
        let secure = tls.is_some();
        let ftp = Ftp::start_with(tls, false, true);
        ftp.fixture
            .secrets
            .set(&password_key(&ftp.profile), "demo", "geheim")
            .unwrap();
        ftp.open(1);
        if secure {
            let asked = ftp.fixture.recorder.event("prompt.host_key");
            ftp.fixture.answer(&asked, json!({ "accept": true }));
        }
        let state = ftp.connected();
        assert_eq!(state["state"], "connected", "{state}");

        // Dieser Server lehnt `PASV` ab: Jede Liste und jede Datei hier kam über eine Verbindung, die er aufbaute.
        assert_eq!(ftp.names("/"), ["readme.txt", "www"]);
        ftp.done(json!({ "op": "sftp.write", "path": "/active.txt", "data": "aGk=" }));
        assert_eq!(std::fs::read(ftp.remote.join("active.txt")).unwrap(), b"hi");
        let part = ftp.sftp(json!({ "op": "sftp.read", "path": "/readme.txt", "offset": 1, "length": 3 }));
        assert_eq!(part["data"], "ZWxs", "{part}");
    }

    // Ohne den Wunsch im Profil bleibt es beim passiven Modus – an diesem Server also ohne Liste.
    let ftp = Ftp::start_with(None, false, true);
    let mut passive = json!({ "id": ftp.profile, "name": "ftp", "host": "127.0.0.1", "port": ftp.port, "user": "demo", "protocol": "ftp", "ftp_tls": "none" });
    passive["ftp_active"] = json!(false);
    ftp.fixture.ok(json!({ "op": "profiles.save", "profile": passive }));
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(1);
    assert_eq!(ftp.connected()["state"], "connected");
    let refused = ftp.sftp(json!({ "op": "sftp.list", "path": "/" }));
    assert_eq!(refused["ok"], false, "{refused}");
}

/// Ein alter Server schreibt Dateinamen als ISO-8859-1. Mit der Kodierung im Profil stehen die Umlaute richtig in
/// der Liste, und was die App nennt – lesen, schreiben, umbenennen, löschen, übertragen –, kommt beim Server als
/// derselbe Name an.
#[test]
fn file_names_in_latin1_are_listed_and_addressed_with_the_encoding_of_the_profile() {
    for plain_list in [false, true] {
        let ftp = Ftp::start_as(None, plain_list, false, false, true);
        std::fs::create_dir_all(ftp.remote.join("Bücher")).unwrap();
        std::fs::write(ftp.remote.join("Größe.txt"), "maß").unwrap();
        std::fs::write(ftp.remote.join("Bücher").join("café.txt"), "au lait").unwrap();
        ftp.fixture
            .secrets
            .set(&password_key(&ftp.profile), "demo", "geheim")
            .unwrap();
        ftp.open(1);
        assert_eq!(ftp.connected()["state"], "connected");

        assert_eq!(ftp.names("/"), ["Bücher", "Größe.txt", "readme.txt", "www"]);
        assert_eq!(ftp.names("/Bücher"), ["café.txt"]);

        let read = ftp.sftp(json!({ "op": "sftp.read", "path": "/Bücher/café.txt", "offset": 0, "length": 100 }));
        assert_eq!(read["data"], "YXUgbGFpdA==", "{read}");
        ftp.done(json!({ "op": "sftp.write", "path": "/Übung.txt", "data": "Zm9v" }));
        assert_eq!(std::fs::read(ftp.remote.join("Übung.txt")).unwrap(), b"foo");
        ftp.done(json!({ "op": "sftp.rename", "from": "/Übung.txt", "to": "/Bücher/Lösung.txt" }));
        assert!(ftp.remote.join("Bücher").join("Lösung.txt").exists());
        ftp.done(json!({ "op": "sftp.mkdir", "path": "/Müll" }));
        assert!(ftp.remote.join("Müll").is_dir());
        ftp.done(json!({ "op": "sftp.delete", "path": "/Größe.txt" }));
        assert!(!ftp.remote.join("Größe.txt").exists());

        // Ein Ordner hin und zurück, mit Umlauten im Namen des Ordners und der Datei.
        let here = ftp.fixture.directory.join("local");
        std::fs::create_dir_all(here.join("Fotos März")).unwrap();
        std::fs::write(here.join("Fotos März").join("Straße.jpg"), "bild").unwrap();
        let finished = |transfer: u64| {
            ftp.fixture.recorder.find("the end of the transfer", |event| {
                event["event"] == "transfer.state"
                    && event["transfer"] == transfer
                    && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
            })
        };
        ftp.fixture.ok(json!({
            "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
            "local": here.join("Fotos März").display().to_string(), "remote": "/",
        }));
        let up = finished(1);
        assert_eq!(up["state"], "completed", "{up}");
        assert_eq!(
            std::fs::read(ftp.remote.join("Fotos März").join("Straße.jpg")).unwrap(),
            b"bild"
        );
        std::fs::create_dir_all(here.join("zurück")).unwrap();
        ftp.fixture.ok(json!({
            "op": "transfer.start", "transfer": 2, "session": 1, "direction": "download",
            "local": here.join("zurück").display().to_string(), "remote": "/Bücher",
        }));
        let down = finished(2);
        assert_eq!(down["state"], "completed", "{down}");
        assert_eq!(
            std::fs::read(here.join("zurück").join("Bücher").join("café.txt")).unwrap(),
            b"au lait"
        );

        // Ein Zeichen, das es in ISO-8859-1 nicht gibt: Der Name wird nicht verschickt, auch nicht als ein
        // anderer – das Schreiben scheitert, und auf dem Server entsteht nichts.
        let before = std::fs::read_dir(&ftp.remote).unwrap().count();
        let refused = ftp.sftp(json!({ "op": "sftp.write", "path": "/€uro.txt", "data": "Zm9v" }));
        assert_eq!(refused["ok"], false, "{refused}");
        assert_eq!(std::fs::read_dir(&ftp.remote).unwrap().count(), before);
    }
}

/// Hinter einem VPN oder NAT nennt die App dem Server eine Adresse, unter der er sie nicht sieht; vsftpd lehnt das
/// ab („500 Illegal EPRT command.“, gesehen 2026-10-12). Ohne Hinweis stünde beim Benutzer nur „Failure“.
#[test]
fn a_server_that_refuses_the_active_mode_is_named_as_the_reason() {
    let ftp = Ftp::start_as(None, false, false, true, false);
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(1);
    assert_eq!(ftp.connected()["state"], "connected");

    let refused = ftp.sftp(json!({ "op": "sftp.list", "path": "/" }));
    assert_eq!(refused["ok"], false, "{refused}");
    let warning = ftp.fixture.recorder.find("the hint about the active mode", |event| {
        event["event"] == "core.warning" && event["code"] == "ftp_active"
    });
    let message = warning["message"].as_str().unwrap();
    assert!(
        message.starts_with("ftp: no data connection from the server in active mode"),
        "{message}"
    );
    assert!(message.contains("500 Illegal"), "{message}");
}

/// Eine Firewall vor diesem Rechner verwirft die Datenverbindung des Servers still: Er nimmt `PORT` an und kommt
/// nie an. Dann darf die Liste nicht erst nach einem zweiten Versuch scheitern – das dauerte länger, als die Anfrage
/// wartet, und der Hinweis käme nie an (gefunden 2026-10-15 hinter einer Firewall auf einem Testrechner).
#[test]
fn a_firewall_that_drops_the_active_data_connection_is_named_as_the_reason() {
    let ftp = Ftp::launch(FtpOptions {
        port: 0,
        login: ("demo".to_owned(), "geheim".to_owned()),
        root: std::path::PathBuf::new(),
        tls: None,
        plain_list: false,
        active_only: true,
        refuse_active: false,
        blocked_active: true,
        latin1: false,
        forgets_restart: false,
        ipv6: false,
    });
    ftp.fixture
        .secrets
        .set(&password_key(&ftp.profile), "demo", "geheim")
        .unwrap();
    ftp.open(1);
    assert_eq!(ftp.connected()["state"], "connected");

    let started = std::time::Instant::now();
    let failed = ftp.sftp(json!({ "op": "sftp.list", "path": "/" }));
    assert_eq!(failed["ok"], false, "{failed}");
    assert_ne!(failed["error"], "timeout", "{failed}");
    assert!(
        started.elapsed() < std::time::Duration::from_secs(10),
        "{:?}",
        started.elapsed()
    );
    let warning = ftp.fixture.recorder.find("the hint about the active mode", |event| {
        event["event"] == "core.warning" && event["code"] == "ftp_active"
    });
    let message = warning["message"].as_str().unwrap();
    assert!(
        message.starts_with("ftp: no data connection from the server in active mode"),
        "{message}"
    );
}

/// Ein echter FTP-Server, wenn die Umgebung einen nennt (sonst läuft der Test leer durch): `NEXUS_REAL_FTP_PORT`,
/// `NEXUS_REAL_FTP_USER`, `NEXUS_REAL_FTP_PASSWORD` und `NEXUS_REAL_FTP_DIR` – ein Ordner dort, in den der Test
/// schreiben darf. Dazu nach Bedarf `NEXUS_REAL_FTP_HOST` (sonst 127.0.0.1), `NEXUS_REAL_FTP_TLS` (`none` für
/// unverschlüsselt, `implicit` für TLS ab dem ersten Byte) und `NEXUS_REAL_FTP_FINGERPRINT` (`SHA256:…` des
/// Zertifikats, wie der Betreiber es nennt – es wird verglichen, nicht blind angenommen),
/// `NEXUS_REAL_FTP_ENCODING` (`latin1` oder `windows1252`: so schreibt der Server Dateinamen),
/// `NEXUS_REAL_FTP_ACTIVE` (irgendein Wert: der aktive Modus – der Server muss diesen Rechner erreichen) und
/// `NEXUS_REAL_FTP_SEES` (ein Name, der im Ordner schon liegt und in der Liste genau so stehen muss – für eine
/// Datei, die dort mit Bytes dieser Kodierung angelegt wurde). Mit `NEXUS_REAL_FTP_BLOCKED` steht dieser Rechner
/// hinter etwas, das den aktiven Modus nicht durchlässt: Dann wird nur verlangt, dass der Hinweis dazu kommt. Der
/// Test räumt hinter sich auf.
#[test]
fn a_real_ftp_server_lists_takes_and_returns_files() {
    let variable = |name: &str| std::env::var(name).ok().filter(|value| !value.is_empty());
    let (Some(port), Some(user), Some(password), Some(directory)) = (
        variable("NEXUS_REAL_FTP_PORT"),
        variable("NEXUS_REAL_FTP_USER"),
        variable("NEXUS_REAL_FTP_PASSWORD"),
        variable("NEXUS_REAL_FTP_DIR"),
    ) else {
        return;
    };
    let host = variable("NEXUS_REAL_FTP_HOST").unwrap_or_else(|| "127.0.0.1".to_owned());
    let tls = variable("NEXUS_REAL_FTP_TLS");

    let fixture = Fixture::new(Options::default());
    let mut profile =
        json!({ "name": "real", "host": host, "port": port.parse::<u16>().unwrap(), "user": user, "protocol": "ftp" });
    if let Some(tls) = &tls {
        profile["ftp_tls"] = json!(tls);
    }
    if variable("NEXUS_REAL_FTP_ACTIVE").is_some() {
        profile["ftp_active"] = json!(true);
    }
    if let Some(encoding) = variable("NEXUS_REAL_FTP_ENCODING") {
        profile["ftp_encoding"] = json!(encoding);
    }
    let profile = fixture.ok(json!({ "op": "profiles.save", "profile": profile }))["profile"]["id"]
        .as_str()
        .unwrap()
        .to_owned();
    fixture.secrets.set(&password_key(&profile), &user, &password).unwrap();
    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": profile, "cols": 80, "rows": 24 }));

    if tls.as_deref() != Some("none") {
        let asked = fixture.recorder.event("prompt.host_key");
        assert_eq!(asked["algorithm"], "x509", "{asked}");
        if let Some(expected) = variable("NEXUS_REAL_FTP_FINGERPRINT") {
            assert_eq!(asked["fingerprint"], expected.as_str(), "{asked}");
        }
        fixture.answer(&asked, json!({ "accept": true }));
    }
    let state = fixture.recorder.find("connected", |event| {
        event["event"] == "session.state" && matches!(event["state"].as_str(), Some("connected" | "closed"))
    });
    assert_eq!(state["state"], "connected", "{state}");

    // Hinter einem Router, der den aktiven Modus nicht durchlässt (`NEXUS_REAL_FTP_BLOCKED`): Die Liste scheitert,
    // und der Hinweis zum aktiven Modus nennt den Grund – statt nur „Failure“.
    if variable("NEXUS_REAL_FTP_BLOCKED").is_some() {
        fixture.ok(json!({ "op": "sftp.list", "session": 1, "request": 1, "path": "." }));
        let listed = fixture.recorder.find("sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == 1
        });
        assert_eq!(listed["ok"], false, "{listed}");
        let warning = fixture.recorder.find("the hint about the active mode", |event| {
            event["event"] == "core.warning" && event["code"] == "ftp_active"
        });
        println!("Hinweis: {}", warning["message"]);
        return;
    }

    let next = std::cell::Cell::new(1u64);
    let sftp = |mut request: Value| -> Value {
        let number = next.get();
        next.set(number + 1);
        request["session"] = json!(1);
        request["request"] = json!(number);
        fixture.ok(request);
        fixture.recorder.find("sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == number
        })
    };
    let done = |request: Value| {
        let result = sftp(request.clone());
        assert_eq!(result["ok"], true, "{request} -> {result}");
    };
    let entries = |path: &str| -> Vec<(String, Value)> {
        let result = sftp(json!({ "op": "sftp.list", "path": path }));
        assert_eq!(result["ok"], true, "{path} -> {result}");
        let mut entries: Vec<(String, Value)> = result["entries"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| (entry["name"].as_str().unwrap().to_owned(), entry.clone()))
            .collect();
        entries.sort_by(|left, right| left.0.cmp(&right.0));
        entries
    };
    let names = |path: &str| -> Vec<String> { entries(path).into_iter().map(|entry| entry.0).collect() };
    let finished = |transfer: u64| {
        fixture.recorder.find("the end of the transfer", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        })
    };

    // Was im Ordner des Benutzers liegt – zum Ansehen, wie der Server Namen, Größen und Arten nennt.
    let home = sftp(json!({ "op": "sftp.list", "path": "." }));
    assert_eq!(home["ok"], true, "{home}");
    println!("Verzeichnis: {}", home["path"]);
    for entry in home["entries"].as_array().unwrap() {
        println!("  {entry}");
    }

    let work = format!("{}/nexus-{}", directory.trim_end_matches('/'), std::process::id());
    done(json!({ "op": "sftp.mkdir", "path": work }));

    // Eine Datei, die schon dort liegt, mit ihrem Namen in der Kodierung des Servers.
    if let Some(expected) = variable("NEXUS_REAL_FTP_SEES") {
        let listed = names(&directory);
        assert!(listed.contains(&expected), "{expected} fehlt in {listed:?}");
        let read = sftp(
            json!({ "op": "sftp.read", "path": format!("{}/{expected}", directory.trim_end_matches('/')), "offset": 0, "length": 100 }),
        );
        assert_eq!(read["ok"], true, "{read}");
        println!("gesehen und gelesen: {expected} ({} Bytes)", read["size"]);
    }

    // Kleine Datei schreiben, ein Stück lesen, umbenennen – auch mit Leerzeichen und Umlaut im Namen.
    done(json!({ "op": "sftp.write", "path": format!("{work}/note.txt"), "data": "aGVsbG8gd29ybGQ=" }));
    let part = sftp(json!({ "op": "sftp.read", "path": format!("{work}/note.txt"), "offset": 6, "length": 5 }));
    assert_eq!(
        (&part["data"], &part["size"]),
        (&json!("d29ybGQ="), &json!(11)),
        "{part}"
    );
    done(json!({ "op": "sftp.rename", "from": format!("{work}/note.txt"), "to": format!("{work}/über uns.txt") }));
    assert_eq!(names(&work), ["über uns.txt"]);

    // Kopieren am Server: Über FTP gibt es kein `cp` – der Kern liest und schreibt selbst.
    done(
        json!({ "op": "sftp.copy", "pairs": [{ "from": format!("{work}/über uns.txt"), "to": format!("{work}/kopie.txt") }] }),
    );
    assert_eq!(names(&work), ["kopie.txt", "über uns.txt"]);
    let copy = sftp(json!({ "op": "sftp.read", "path": format!("{work}/kopie.txt"), "offset": 0, "length": 100 }));
    assert_eq!(copy["data"], "aGVsbG8gd29ybGQ=", "{copy}");
    done(json!({ "op": "sftp.delete", "path": format!("{work}/kopie.txt") }));

    // Ein Ordner hin und zurück: mehrere Dateien zugleich, jede auf ihrer Datenverbindung.
    let here = fixture.directory.join("local");
    std::fs::create_dir_all(here.join("up").join("sub dir")).unwrap();
    let content: Vec<u8> = (0..2_500_000u32).map(|index| (index % 251) as u8).collect();
    for name in ["a.bin", "b.bin", "c.bin", "d.bin"] {
        std::fs::write(here.join("up").join(name), &content).unwrap();
    }
    std::fs::write(here.join("up").join("sub dir").join("größe.txt"), "klein").unwrap();
    std::fs::write(here.join("up").join("empty.txt"), "").unwrap();
    fixture.ok(json!({
        "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
        "local": here.join("up").display().to_string(), "remote": work,
    }));
    let up = finished(1);
    assert_eq!(up["state"], "completed", "{up}");
    let listed = entries(&format!("{work}/up"));
    println!("Nach dem Upload:");
    for (_, entry) in &listed {
        println!("  {entry}");
    }
    assert_eq!(
        listed.iter().map(|entry| entry.0.as_str()).collect::<Vec<_>>(),
        ["a.bin", "b.bin", "c.bin", "d.bin", "empty.txt", "sub dir"]
    );
    assert_eq!(listed[0].1["size"], 2_500_000, "{}", listed[0].1);
    assert_eq!(names(&format!("{work}/up/sub dir")), ["größe.txt"]);

    std::fs::create_dir_all(here.join("down")).unwrap();
    fixture.ok(json!({
        "op": "transfer.start", "transfer": 2, "session": 1, "direction": "download",
        "local": here.join("down").display().to_string(), "remote": format!("{work}/up"),
    }));
    let down = finished(2);
    assert_eq!(down["state"], "completed", "{down}");
    for name in ["a.bin", "d.bin"] {
        assert_eq!(
            std::fs::read(here.join("down").join("up").join(name)).unwrap(),
            content,
            "{name}"
        );
    }
    assert_eq!(
        std::fs::read_to_string(here.join("down").join("up").join("sub dir").join("größe.txt")).unwrap(),
        "klein"
    );
    assert_eq!(
        std::fs::read(here.join("down").join("up").join("empty.txt")).unwrap(),
        b""
    );

    // Aufräumen: der ganze Ordner samt Inhalt.
    done(json!({ "op": "sftp.delete", "path": work }));
    let own = work.rsplit('/').next().unwrap().to_owned();
    assert!(!names(&directory).contains(&own));
}
