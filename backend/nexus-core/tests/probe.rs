//! Der Verbindungstest gegen den Testserver: Er sagt, wie weit der Weg trägt – ohne sich anzumelden, ohne zu
//! fragen und ohne etwas zu merken.

mod common;

use common::{Fixture, with_password};
use nexus_testserver::{Options, host_key};
use serde_json::{Value, json};

fn probe(fixture: &Fixture, request: u64, extra: Value) -> Value {
    let mut profile = json!({ "name": "test", "host": "127.0.0.1", "port": fixture.port, "user": "demo" });
    for (key, value) in extra.as_object().unwrap() {
        profile[key] = value.clone();
    }

    fixture.ok(json!({ "op": "probe", "request": request, "profile": profile }));
    fixture.recorder.find("the result of the probe", |event| {
        event["event"] == "probe.result" && event["request"] == request
    })
}

#[test]
fn a_reachable_server_shows_its_key_and_what_it_accepts_without_anyone_signing_in() {
    let fixture = Fixture::new(with_password());

    let result = probe(&fixture, 1, json!({}));

    assert_eq!(result["step"], "methods", "{result}");
    assert_eq!(result["addresses"], json!(["127.0.0.1"]));
    assert_eq!(result["address"], "127.0.0.1");
    assert!(result["connect_ms"].as_u64().is_some());
    assert!(result["software"].as_str().unwrap().starts_with("SSH-2.0-"), "{result}");
    assert_eq!(result["key_algorithm"], "ssh-ed25519");
    assert_eq!(
        result["fingerprint"],
        nexus_core::hostkeys::fingerprint(host_key(1).public_key())
    );
    assert_eq!(result["key_state"], "unknown");
    assert!(
        result["methods"].as_array().unwrap().contains(&json!("password")),
        "{result}"
    );
    assert!(result.get("trouble").is_none());

    // Niemand wurde gefragt, nichts wurde gemerkt: Der Server ist danach so unbekannt wie zuvor.
    assert!(!fixture.recorder.saw("prompt.host_key"));
    assert!(!fixture.recorder.saw("prompt.secret"));
    assert_eq!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Unknown
    );
}

#[test]
fn a_server_the_user_trusted_before_is_reported_as_known() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let prompt = fixture.recorder.event("prompt.secret");
    fixture.answer(&prompt, json!({ "values": ["geheim"], "save": false }));
    fixture.recorder.state("connected");

    assert_eq!(probe(&fixture, 2, json!({}))["key_state"], "trusted");
}

#[test]
fn the_report_says_how_far_it_got_and_why_it_stopped() {
    let fixture = Fixture::new(with_password());
    let free = std::net::TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port();

    // Der Name löst sich auf, aber auf dem Port lauscht niemand.
    let closed = probe(&fixture, 1, json!({ "port": free }));
    assert_eq!(
        (&closed["step"], &closed["trouble"]),
        (&json!("resolve"), &json!("refused")),
        "{closed}"
    );
    assert_eq!(closed["addresses"], json!(["127.0.0.1"]));

    // Den Namen gibt es nicht.
    let unknown = probe(&fixture, 2, json!({ "host": "gibt-es-nicht.invalid" }));
    assert_eq!(
        (&unknown["step"], &unknown["trouble"]),
        (&json!(""), &json!("name")),
        "{unknown}"
    );

    // Über einen Sprung-Server ließe sich nur mit einer Anmeldung dort prüfen.
    let jump = fixture.profile(json!({ "name": "sprung" }));
    let through = probe(&fixture, 3, json!({ "jump": jump }));
    assert_eq!(
        (&through["step"], &through["trouble"]),
        (&json!(""), &json!("jump")),
        "{through}"
    );

    // Ein Profil, das so nicht gespeichert werden könnte, wird gar nicht erst geprüft.
    let invalid =
        fixture.request(json!({ "op": "probe", "request": 4, "profile": { "host": "two words", "user": "u" } }));
    assert_eq!(
        (&invalid["error"], &invalid["message"]),
        (&json!("invalid_profile"), &json!("host"))
    );
}

#[test]
fn an_old_device_is_recognised_and_tested_with_the_older_algorithms() {
    let fixture = Fixture::new(Options {
        old_device: true,
        ..with_password()
    });

    // Es stellt sich vor, aber es gibt kein gemeinsames Verfahren.
    let plain = probe(&fixture, 1, json!({}));
    assert_eq!(
        (&plain["step"], &plain["trouble"]),
        (&json!("greeting"), &json!("algorithms")),
        "{plain}"
    );
    assert!(plain["software"].as_str().unwrap().starts_with("SSH-2.0-"));

    let legacy = probe(&fixture, 2, json!({ "legacy_algorithms": true }));
    assert_eq!(legacy["step"], "methods", "{legacy}");
    assert_eq!(legacy["key_state"], "unknown");
}

#[test]
fn a_port_that_does_not_speak_ssh_is_named_as_such() {
    // Ein Dienst, der sich anders vorstellt.
    let listener = std::net::TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        use std::io::Write;
        for mut stream in listener.incoming().flatten() {
            let _ = stream.write_all(b"220 mail.example.org ESMTP ready\r\n");
            std::thread::sleep(std::time::Duration::from_millis(300));
        }
    });
    let fixture = Fixture::new(with_password());

    let result = probe(&fixture, 1, json!({ "port": port }));

    assert_eq!(
        (&result["step"], &result["trouble"]),
        (&json!("connect"), &json!("not_ssh")),
        "{result}"
    );
    assert_eq!(result["software"], "220 mail.example.org ESMTP ready");
}
