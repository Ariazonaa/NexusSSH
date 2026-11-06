//! Was der Sicherheitsdurchgang vom 2026-10-10 gefunden hat, als Tests: Ein gespeichertes Passwort folgt seinem
//! Profil nicht zu einem anderen Ziel, und auf einem Server, dessen Anmelde-Shell Befehle nicht wie eine
//! POSIX-Shell liest, führt der Kern keinen aus.

mod common;

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key, target_key};
use nexus_testserver::Options;
use serde_json::json;

#[test]
fn a_saved_password_is_not_sent_after_the_profile_points_somewhere_else() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));

    // Der Benutzer meldet sich an und lässt das Passwort speichern.
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let prompt = fixture.recorder.event("prompt.secret");
    fixture.answer(&prompt, json!({ "values": ["geheim"], "save": true }));
    fixture.recorder.find("connected", |event| {
        event["session"] == 1 && event["state"] == "connected"
    });
    assert_eq!(fixture.secrets.get(&password_key(&profile)).as_deref(), Some("geheim"));
    assert_eq!(
        fixture.secrets.get(&target_key(&profile)).as_deref(),
        Some(format!("ssh|demo@127.0.0.1:{}", fixture.port).as_str())
    );
    fixture.ok(json!({ "op": "session.close", "session": 1 }));

    // Dasselbe Profil (dieselbe Kennung) zeigt jetzt auf einen anderen Host – wie nach dem Import einer Datei, die
    // jemand anderes geschrieben hat. Hier ist es derselbe Testserver unter anderem Namen; für den Kern ist es ein
    // anderes Ziel.
    fixture.ok(json!({ "op": "profiles.save", "profile": {
        "id": profile, "name": "test", "host": "localhost", "port": fixture.port, "user": "demo", "use_agent": false
    } }));

    // Schon die Liste zeigt kein gespeichertes Passwort mehr, und es ist wirklich weg.
    let listed = fixture.ok(json!({ "op": "profiles.list" }));
    assert_eq!(listed["profiles"][0]["saved_secret"], false, "{listed}");
    assert_eq!(fixture.secrets.get(&password_key(&profile)), None);

    // Beim Verbinden wird gefragt, statt das alte Passwort zu schicken.
    fixture.ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24 }));
    let host_key = fixture.recorder.find("the new host", |event| {
        event["event"] == "prompt.host_key" && event["host"] == "localhost"
    });
    fixture.answer(&host_key, json!({ "accept": true }));
    let asked = fixture.recorder.find("a question for the password", |event| {
        event["event"] == "prompt.secret" && event["session"] == 2
    });
    assert_eq!(asked["kind"], "password", "{asked}");
    assert!(asked["host"].as_str().unwrap().starts_with("localhost"), "{asked}");
    fixture.answer(&asked, json!({ "values": ["geheim"], "save": false }));
}

#[test]
fn no_command_runs_on_a_server_whose_login_shell_is_not_posix() {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        foreign_shell: true,
        ..with_password()
    });
    std::fs::create_dir_all(fixture.directory.join("remote")).unwrap();
    std::fs::write(fixture.directory.join("remote").join("a&calc&b"), "x").unwrap_or_default();
    std::fs::write(fixture.directory.join("remote").join("readme.txt"), "hello").unwrap();

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.find("connected", |event| {
        event["session"] == 1 && event["state"] == "connected"
    });

    // SFTP selbst geht – das sind keine Befehle.
    fixture.ok(json!({ "op": "sftp.list", "session": 1, "request": 1, "path": "/" }));
    let listed = fixture.recorder.find("the listing", |event| {
        event["event"] == "sftp.result" && event["request"] == 1
    });
    assert_eq!(listed["ok"], true, "{listed}");

    // Alles, was einen Befehl mit einem Namen darin bräuchte, wird abgelehnt, bevor er gesendet ist.
    for (request, body) in [
        (2, json!({ "op": "sftp.checksum", "paths": ["/readme.txt"] })),
        (3, json!({ "op": "sftp.usage", "path": "/" })),
        // Die Frage, ob Befehle gehen, bekommt dieselbe Antwort – danach blendet die App aus, was sie bräuchte.
        (40, json!({ "op": "commands.probe" })),
        // Protokolle lesen ebenso: das Journal und eine Datei.
        (41, json!({ "op": "logs.read" })),
        (42, json!({ "op": "logs.read", "path": "/var/log/syslog" })),
        // Verschieben über einen Befehl ebenso – die App versucht es vorher mit dem Umbenennen von SFTP.
        (
            4,
            json!({ "op": "sftp.copy", "pairs": [{ "from": "/readme.txt", "to": "/moved.txt" }], "move": true }),
        ),
    ] {
        let mut body = body;
        body["session"] = json!(1);
        body["request"] = json!(request);
        fixture.ok(body);
        let result = fixture.recorder.find("the refusal", |event| {
            event["event"] == "sftp.result" && event["request"] == request
        });
        assert_eq!(
            (&result["ok"], &result["error"]),
            (&json!(false), &json!("no_posix_shell")),
            "{result}"
        );
    }

    assert!(!fixture.directory.join("remote").join("moved.txt").exists());

    // Kopieren geht trotzdem – ohne Befehl: Der Kern liest und schreibt über SFTP. Ein Name, der an einer
    // fremden Shell ein Befehl wäre, ist dabei nur ein Name.
    let remote = fixture.directory.join("remote");
    std::fs::create_dir_all(remote.join("www").join("sub")).unwrap();
    std::fs::write(remote.join("www").join("sub").join("deep.txt"), "deep").unwrap();
    let copy = |request: u64, from: &str, to: &str| {
        fixture
            .ok(json!({ "op": "sftp.copy", "session": 1, "request": request, "pairs": [{ "from": from, "to": to }] }));
        fixture.recorder.find("the answer to the copy", |event| {
            event["event"] == "sftp.result" && event["request"] == request
        })
    };
    let file = copy(5, "/readme.txt", "/copy & more.txt");
    assert_eq!(file["ok"], true, "{file}");
    assert_eq!(
        std::fs::read_to_string(remote.join("copy & more.txt")).unwrap(),
        "hello"
    );
    let folder = copy(6, "/www", "/www (2)");
    assert_eq!(folder["ok"], true, "{folder}");
    assert_eq!(
        std::fs::read_to_string(remote.join("www (2)").join("sub").join("deep.txt")).unwrap(),
        "deep"
    );

    // Was es am Ziel schon gibt, wird nicht überschrieben, und in sich selbst hinein kopiert niemand.
    let again = copy(7, "/readme.txt", "/copy & more.txt");
    assert_eq!(
        (&again["ok"], &again["error"]),
        (&json!(false), &json!("exists")),
        "{again}"
    );
    let inside = fixture.request(
        json!({ "op": "sftp.copy", "session": 1, "request": 8, "pairs": [{ "from": "/www", "to": "/www/inside" }] }),
    );
    assert_eq!(inside["ok"], false, "{inside}");

    // Auch nicht auf einem Umweg: Ein Pfad mit `..` könnte derselbe Ordner unter anderem Namen sein.
    let detour = copy(9, "/www/../www", "/www/sub/again");
    assert_eq!(
        (&detour["ok"], &detour["error"]),
        (&json!(false), &json!("bad_request")),
        "{detour}"
    );
    assert!(!remote.join("www").join("sub").join("again").exists());
    assert!(!remote.join("www").join("inside").exists());
}

#[test]
fn a_download_with_a_limit_stops_when_the_server_sends_more() {
    // „Bearbeiten“ und der Betrachter prüfen die Größe an der Liste – die nennt aber nur, was der Server behauptet.
    // Die Grenze gilt deshalb dem, was wirklich ankommt.
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let (remote, local) = (fixture.directory.join("remote"), fixture.directory.join("local"));
    std::fs::create_dir_all(&remote).unwrap();
    std::fs::create_dir_all(&local).unwrap();
    std::fs::write(remote.join("big.bin"), vec![7u8; 300_000]).unwrap();
    std::fs::write(remote.join("small.txt"), "hello").unwrap();

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.find("connected", |event| {
        event["session"] == 1 && event["state"] == "connected"
    });

    let ended = |transfer: u64| {
        fixture.recorder.find("the end of the transfer", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        })
    };

    fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 1, "direction": "download",
        "local": local, "remote": "/big.bin", "limit": 100_000,
    }));
    let stopped = ended(1);
    assert_eq!(stopped["state"], "failed", "{stopped}");
    assert!(
        stopped["message"].as_str().unwrap().starts_with("too_large"),
        "{stopped}"
    );
    assert!(stopped["bytes"].as_u64().unwrap() < 300_000, "{stopped}");
    assert!(
        !local.join("big.bin").exists(),
        "nothing half-loaded is left as the file"
    );

    // Unter der Grenze geht alles wie immer.
    fixture.ok(json!({
        "op": "transfer.start", "session": 1, "transfer": 2, "direction": "download",
        "local": local, "remote": "/small.txt", "limit": 100_000,
    }));
    assert_eq!(ended(2)["state"], "completed");
    assert_eq!(std::fs::read(local.join("small.txt")).unwrap(), b"hello");
}
