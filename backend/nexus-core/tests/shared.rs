//! Mehrere Sitzungen auf einer Verbindung (geteiltes Terminal, „duplizieren“), das Banner vor der Anmeldung und
//! das Anhalten der Ausgabe.

mod common;

use std::time::{Duration, Instant};

use common::{Fixture, PATIENCE, with_password};
use nexus_testserver::Options;
use serde_json::json;

/// Öffnet Sitzung 1 mit Passwort und wartet, bis ihre Shell steht.
fn first_session(fixture: &Fixture, profile: &str) {
    fixture.open(1, profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture.recorder.find("verbunden", |event| {
        event["state"] == "connected" && event["session"] == 1
    });
    fixture.recorder.output(1, "$ ");
}

fn open_shared(fixture: &Fixture, session: u64, profile: &str, share: u64) {
    fixture.ok(json!({
        "op": "session.open", "session": session, "profile_id": profile, "cols": 100, "rows": 30, "share": share,
    }));
}

/// Nummer der Verbindung, wie der Testserver sie der Shell dieser Sitzung nennt.
fn connection_of(fixture: &Fixture, session: u64) -> String {
    assert!(fixture.core.write(session, b"conn\r"));
    let output = fixture.recorder.output(session, "\r\n$ ");
    output
        .lines()
        .find_map(|line| line.trim().strip_prefix("connection "))
        .unwrap_or_else(|| panic!("keine Verbindungsnummer in {output:?}"))
        .to_owned()
}

#[test]
fn a_second_session_shares_the_connection_without_signing_in_again() {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.clone()),
        ..with_password()
    });
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    let prompts = fixture.recorder.all("prompt.secret").len();

    open_shared(&fixture, 2, &profile, 1);
    let connected = fixture.recorder.find("zweite Sitzung", |event| {
        event["state"] == "connected" && event["session"] == 2
    });
    assert!(
        connected["home"].is_string(),
        "auch die zweite Sitzung hat ihren Dateizugriff"
    );
    fixture.recorder.output(2, "$ ");

    assert_eq!(
        fixture.recorder.all("prompt.secret").len(),
        prompts,
        "keine neue Anmeldung"
    );
    assert_eq!(fixture.recorder.all("prompt.host_key").len(), 1);
    assert!(
        !fixture
            .recorder
            .all("session.state")
            .iter()
            .any(|event| event["session"] == 2 && event["state"] == "authenticating"),
        "die zweite Sitzung meldet sich nicht an"
    );
    assert_eq!(connection_of(&fixture, 1), connection_of(&fixture, 2));

    // Jede Shell hat ihre eigene Größe und ihre eigene Eingabe.
    assert!(fixture.core.write(2, b"size\r"));
    assert!(fixture.recorder.output(2, "\r\n$ ").contains("100x30"));
    assert!(fixture.core.write(1, b"size\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("80x24"));
}

#[test]
fn closing_the_first_session_keeps_the_shared_connection_alive() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    open_shared(&fixture, 2, &profile, 1);
    fixture.recorder.output(2, "$ ");
    let connection = connection_of(&fixture, 2);

    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    fixture.recorder.find("erste Sitzung zu", |event| {
        event["state"] == "closed" && event["session"] == 1
    });

    assert!(fixture.core.write(2, b"echo noch da\r"));
    assert!(fixture.recorder.output(2, "\r\n$ ").contains("\r\nnoch da\r\n"));
    assert_eq!(connection_of(&fixture, 2), connection);

    // Auch eine dritte Sitzung kann jetzt noch bei der zweiten mitfahren.
    open_shared(&fixture, 3, &profile, 2);
    fixture.recorder.output(3, "$ ");
    assert_eq!(connection_of(&fixture, 3), connection);
    assert_eq!(fixture.recorder.all("prompt.secret").len(), 1);
}

#[test]
fn exit_in_one_shell_ends_only_that_session() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    open_shared(&fixture, 2, &profile, 1);
    fixture.recorder.output(2, "$ ");

    assert!(fixture.core.write(2, b"exit\r"));
    let closed = fixture.recorder.find("zweite Sitzung zu", |event| {
        event["state"] == "closed" && event["session"] == 2
    });
    assert_eq!(closed["reason"], "remote");

    assert!(fixture.core.write(1, b"echo lebt\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nlebt\r\n"));
}

#[test]
fn without_a_usable_connection_the_session_connects_on_its_own() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    let other = fixture.profile(json!({ "name": "anderes Profil" }));
    first_session(&fixture, &profile);

    // Eine Sitzung, die es nicht gibt, und eine zu einem anderen Profil: beides ist kein Grund zum Mitfahren.
    for (session, target, share) in [(2, &profile, 99), (3, &other, 1)] {
        open_shared(&fixture, session, target, share);
        let secret = fixture.recorder.event("prompt.secret");
        assert_eq!(secret["session"], session);
        fixture.answer(&secret, json!({ "values": ["geheim"] }));
        fixture.recorder.output(session, "$ ");
        assert_ne!(connection_of(&fixture, session), connection_of(&fixture, 1));
    }
}

#[test]
fn a_server_that_allows_no_more_channels_gets_a_connection_of_its_own() {
    // Ein Kanal je Verbindung: Die Shell der ersten Sitzung belegt ihn schon.
    let fixture = Fixture::new(Options {
        max_sessions: Some(1),
        ..with_password()
    });
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);

    open_shared(&fixture, 2, &profile, 1);
    let secret = fixture.recorder.event("prompt.secret");
    assert_eq!(secret["session"], 2, "die volle Verbindung wird nicht mitbenutzt");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture.recorder.output(2, "$ ");
    assert_ne!(connection_of(&fixture, 1), connection_of(&fixture, 2));
}

#[test]
fn a_profile_that_points_elsewhere_by_now_does_not_share_the_old_connection() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);

    // Das Profil zeigt inzwischen auf einen anderen Server; die erste Sitzung hängt noch am alten.
    let elsewhere = fixture.another_server(
        7,
        Options {
            banner: Some("der neue Server\n".to_owned()),
            ..with_password()
        },
    );
    fixture.ok(json!({ "op": "profiles.save", "profile": {
        "id": profile, "name": "test", "host": "127.0.0.1", "port": elsewhere.address.port(), "user": "demo",
        "use_agent": false,
    }}));

    // Mitfahren hieße: eine Shell auf dem alten Server unter dem Namen des neuen. Stattdessen entsteht eine
    // eigene Verbindung – mit Schlüssel-Prüfung und Anmeldung beim neuen Server.
    open_shared(&fixture, 2, &profile, 1);
    fixture.trust_host_key();
    let banner = fixture.recorder.event("session.banner");
    assert_eq!(
        (&banner["session"], &banner["text"]),
        (&json!(2), &json!("der neue Server\n"))
    );
    let secret = fixture.recorder.event("prompt.secret");
    assert_eq!(secret["session"], 2);
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture.recorder.output(2, "$ ");

    // Die erste Sitzung läuft ungestört auf ihrem Server weiter.
    assert!(fixture.core.write(1, b"echo alt\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nalt\r\n"));
}

#[test]
fn the_banner_arrives_before_the_sign_in_and_only_as_plain_text() {
    let fixture = Fixture::new(Options {
        banner: Some("Zutritt nur für Befugte.\r\n\x1b[31mrot\x1b[0m und \x07Klingel\nzweite Zeile\n".to_owned()),
        ..with_password()
    });
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();

    let banner = fixture.recorder.event("session.banner");
    assert_eq!(banner["session"], 1);
    assert_eq!(
        banner["text"], "Zutritt nur für Befugte.\n[31mrot[0m und Klingel\nzweite Zeile\n",
        "Steuerzeichen sind entfernt, Zeilenumbrüche bleiben"
    );

    // Erst danach wird nach dem Passwort gefragt.
    let events = fixture.recorder.all("session.banner").len();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    assert_eq!(fixture.recorder.all("session.banner").len(), events);
}

#[test]
fn a_server_without_a_banner_sends_none() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    assert!(!fixture.recorder.saw("session.banner"));
}

#[test]
fn paused_output_stops_and_continues_where_it_left_off() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);

    // Viel Ausgabe anstoßen und sofort anhalten.
    assert!(fixture.core.write(1, b"flood 200000\r"));
    fixture.recorder.output(1, "flood 00000001");
    fixture.ok(json!({ "op": "session.flow", "session": 1, "paused": true }));

    // Was schon unterwegs war, kommt noch an; danach steht die Ausgabe.
    let settled = settle(&fixture, 1);
    std::thread::sleep(Duration::from_millis(600));
    assert_eq!(fixture.recorder.output_len(1), settled, "angehalten fließt nichts mehr");
    assert!(
        !fixture.recorder.output_contains(1, "flood done"),
        "der Rest wartet noch auf dem Server"
    );

    fixture.ok(json!({ "op": "session.flow", "session": 1, "paused": false }));
    let rest = fixture.recorder.output(1, "flood done");
    assert!(rest.contains("flood 00200000"), "nichts ist verloren gegangen");

    let response = fixture.request(json!({ "op": "session.flow", "session": 9, "paused": true }));
    assert_eq!(response["error"], "unknown_session");
}

/// Startet einen Download von `/big.bin` und wartet, bis die ersten Daten geflossen sind.
fn start_download(fixture: &Fixture, session: u64, transfer: u64, local: &std::path::Path) {
    fixture.ok(json!({
        "op": "transfer.start", "session": session, "transfer": transfer, "direction": "download",
        "local": local.to_string_lossy(), "remote": "/big.bin",
    }));
    fixture.recorder.find("erste Daten", |event| {
        event["event"] == "transfer.state"
            && event["transfer"] == transfer
            && event["bytes"].as_u64().is_some_and(|bytes| bytes > 0)
    });
}

fn finished(fixture: &Fixture, transfer: u64) -> serde_json::Value {
    fixture.recorder.find("Ende des Transfers", |event| {
        event["event"] == "transfer.state"
            && event["transfer"] == transfer
            && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
    })
}

#[test]
fn a_transfer_outlives_its_session_while_another_one_shares_the_connection() {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let (remote, first, second) = (
        fixture.directory.join("remote"),
        fixture.directory.join("first"),
        fixture.directory.join("second"),
    );
    for directory in [&remote, &first, &second] {
        std::fs::create_dir_all(directory).unwrap();
    }
    let content: Vec<u8> = (0..48 * 1024 * 1024usize)
        .map(|index| (index * 31 + index / 251) as u8)
        .collect();
    std::fs::write(remote.join("big.bin"), &content).unwrap();

    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    open_shared(&fixture, 2, &profile, 1);
    fixture.recorder.output(2, "$ ");

    // Der Tab, der den Transfer gestartet hat, schließt – die Verbindung steht noch, also läuft er zu Ende.
    start_download(&fixture, 1, 1, &first);
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    assert_eq!(finished(&fixture, 1)["state"], "completed");
    assert!(
        std::fs::read(first.join("big.bin")).unwrap() == content,
        "die Datei ist vollständig angekommen"
    );

    // Jetzt gehört er zur zweiten Sitzung: Mit ihr endet die Verbindung, und damit ein laufender Transfer.
    start_download(&fixture, 2, 2, &second);
    fixture.ok(json!({ "op": "session.close", "session": 2 }));
    let last = finished(&fixture, 2);
    assert!(matches!(last["state"].as_str(), Some("cancelled" | "failed")), "{last}");
    assert!(!second.join("big.bin").exists());
}

#[test]
fn a_transfer_inherited_from_a_closed_session_ends_with_the_last_one() {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let (remote, local) = (fixture.directory.join("remote"), fixture.directory.join("local"));
    std::fs::create_dir_all(&remote).unwrap();
    std::fs::create_dir_all(&local).unwrap();
    std::fs::write(remote.join("big.bin"), vec![7u8; 96 * 1024 * 1024]).unwrap();

    let profile = fixture.profile(json!({}));
    first_session(&fixture, &profile);
    open_shared(&fixture, 2, &profile, 1);
    fixture.recorder.output(2, "$ ");

    start_download(&fixture, 1, 1, &local);
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    fixture.ok(json!({ "op": "session.close", "session": 2 }));

    let last = finished(&fixture, 1);
    assert!(matches!(last["state"].as_str(), Some("cancelled" | "failed")), "{last}");
    assert!(
        !local.join("big.bin").exists(),
        "keine halbe Datei unter dem richtigen Namen"
    );
}

/// Wartet, bis die Ausgabe der Sitzung eine Weile nicht mehr wächst, und liefert ihre Länge.
fn settle(fixture: &Fixture, session: u64) -> usize {
    let deadline = Instant::now() + PATIENCE;
    let mut length = fixture.recorder.output_len(session);
    loop {
        std::thread::sleep(Duration::from_millis(300));
        let now = fixture.recorder.output_len(session);
        if now == length {
            return now;
        }

        assert!(Instant::now() < deadline, "die Ausgabe kommt nicht zur Ruhe");
        length = now;
    }
}
