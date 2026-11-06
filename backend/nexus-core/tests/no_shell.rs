//! Konten ohne Shell: Der Server lässt nur SFTP zu (`ForceCommand internal-sftp`), die Shell ist `nologin`, oder er
//! lehnt die Bitte um eine Shell ab. Die Sitzung bleibt dann für Dateien offen, statt zu enden.

mod common;

use std::time::Duration;

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::{Options, SFTP_ONLY_MESSAGE, Shell};
use serde_json::{Value, json};

/// Sitzung 1 an einem Server mit dieser Antwort auf die Shell; mit oder ohne SFTP. Liefert das Ereignis `connected`.
fn connect(shell: Shell, sftp: bool) -> (Fixture, Value) {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: sftp.then(|| directory.join("remote")),
        shell,
        ..with_password()
    });
    std::fs::create_dir_all(fixture.directory.join("remote").join("www")).unwrap();
    std::fs::write(fixture.directory.join("remote").join("readme.txt"), "hello").unwrap();

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    let connected = fixture.recorder.state("connected");
    (fixture, connected)
}

fn names(fixture: &Fixture, request: u64) -> Vec<String> {
    fixture.ok(json!({ "op": "sftp.list", "session": 1, "request": request, "path": "/" }));
    let result = fixture.recorder.find("sftp.result", |event| {
        event["event"] == "sftp.result" && event["request"] == request
    });
    assert_eq!(result["ok"], true, "{result}");
    result["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| entry["name"].as_str().unwrap().to_owned())
        .collect()
}

#[test]
fn an_account_that_may_only_use_sftp_keeps_its_files() {
    let (fixture, connected) = connect(Shell::SftpOnly, true);
    assert_eq!(connected["home"], "/", "{connected}");

    // Was der Server statt einer Shell sagt, steht im Terminal; danach gibt es keine Shell, aber weiter Dateien.
    fixture.recorder.output(1, SFTP_ONLY_MESSAGE);
    let files_only = fixture.recorder.event("session.files_only");
    assert_eq!(
        (&files_only["session"], &files_only["exit_status"]),
        (&json!(1), &json!(1))
    );

    assert_eq!(names(&fixture, 1), ["www", "readme.txt"]);

    // Getipptes verpufft, ohne die Sitzung zu beenden.
    assert!(fixture.core.write(1, b"ls\r"));
    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 100, "rows": 30 }));
    assert_eq!(names(&fixture, 2), ["www", "readme.txt"]);
    assert!(
        fixture
            .recorder
            .all("session.state")
            .iter()
            .all(|event| event["state"] != "closed")
    );

    // Zu Ende ist sie erst, wenn der Benutzer sie schließt.
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "user", "{closed}");
}

#[test]
fn a_refused_shell_keeps_the_files_too() {
    let (fixture, connected) = connect(Shell::Refused, true);
    assert_eq!(connected["home"], "/", "{connected}");

    let files_only = fixture.recorder.event("session.files_only");
    assert!(files_only.get("exit_status").is_none(), "{files_only}");
    assert_eq!(names(&fixture, 1), ["www", "readme.txt"]);
}

#[test]
fn without_files_a_session_without_a_shell_ends() {
    // Nur SFTP erlaubt, aber SFTP gibt es nicht: Dann bleibt nichts, wofür die Sitzung offen sein müsste.
    let (fixture, connected) = connect(Shell::SftpOnly, false);
    assert!(connected.get("home").is_none(), "{connected}");
    let closed = fixture.recorder.state("closed");
    assert_eq!(
        (&closed["reason"], &closed["exit_status"]),
        (&json!("remote"), &json!(1)),
        "{closed}"
    );
    assert!(!fixture.recorder.saw("session.files_only"));

    let (fixture, _) = connect(Shell::Refused, false);
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connection_lost", "{closed}");
    assert_eq!(closed["message"], "the server refused to start a shell");
}

#[test]
fn a_shell_someone_typed_into_simply_ends() {
    let (fixture, _) = connect(Shell::Interactive, true);
    fixture.recorder.output(1, "$ ");

    // `exit` beendet die Sitzung wie bisher – samt Dateien.
    assert!(fixture.core.write(1, b"exit\r"));
    let closed = fixture.recorder.state("closed");
    assert_eq!(
        (&closed["reason"], &closed["exit_status"]),
        (&json!("remote"), &json!(0)),
        "{closed}"
    );
    assert!(!fixture.recorder.saw("session.files_only"));
}

#[test]
fn a_lost_connection_ends_the_session_without_a_shell() {
    let (mut fixture, _) = connect(Shell::SftpOnly, true);
    fixture.recorder.event("session.files_only");

    // Es gibt keinen Kanal der Shell mehr, der das Ende melden könnte – der Kern muss es selbst merken.
    fixture.cut_connections();
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connection_lost", "{closed}");

    // Und danach ist die Sitzung wirklich weg.
    std::thread::sleep(Duration::from_millis(50));
    let after = fixture.request(json!({ "op": "sftp.list", "session": 1, "request": 9, "path": "/" }));
    assert_eq!(after["ok"], false, "{after}");
}
