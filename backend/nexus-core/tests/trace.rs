//! Das ausführliche Protokoll einer Verbindung (`session.open` mit `trace`): was ausgehandelt wurde und welche
//! Anmeldung versucht wird – und dass darin nie ein Geheimnis steht.

mod common;

use common::{Fixture, with_password};
use serde_json::json;

fn connect(fixture: &Fixture, session: u64, profile: &str, trace: bool, passwords: &[&str]) {
    fixture.ok(json!({
        "op": "session.open", "session": session, "profile_id": profile, "cols": 80, "rows": 24, "trace": trace,
    }));
    if session == 1 {
        fixture.trust_host_key();
    }

    for (attempt, password) in passwords.iter().enumerate() {
        let prompt = fixture.recorder.find("a question for the password", |event| {
            event["event"] == "prompt.secret" && event["session"] == session && event["attempt"] == attempt + 1
        });
        fixture.answer(&prompt, json!({ "values": [password], "save": false }));
    }

    fixture.recorder.find("connected", |event| {
        event["event"] == "session.state" && event["session"] == session && event["state"] == "connected"
    });
}

fn lines(fixture: &Fixture, session: u64) -> Vec<String> {
    fixture
        .recorder
        .all("session.trace")
        .iter()
        .filter(|event| event["session"] == session)
        .map(|event| event["text"].as_str().unwrap().to_owned())
        .collect()
}

#[test]
fn a_traced_connection_tells_what_was_negotiated_and_tried_but_never_a_secret() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));

    // Erst ein falsches, dann das richtige Passwort: Beide Versuche stehen im Protokoll, keines der Passwörter.
    connect(&fixture, 1, &profile, true, &["falsch-und-geheim", "geheim"]);

    let lines = lines(&fixture, 1);
    let all = lines.join("\n");
    let has = |part: &str| {
        assert!(
            lines.iter().any(|line| line.contains(part)),
            "fehlt „{part}“ in:\n{all}"
        )
    };
    has("connected to demo@127.0.0.1");
    has("asking for host keys of kind any");
    has("server is SSH-2.0-");
    has("key exchange ");
    has("cipher ");
    has("the server allows these ways to sign in: ");
    has("a typed password (attempt 1): rejected; the server still allows ");
    has("a typed password (attempt 2): accepted");
    has("signed in as demo");

    assert!(!all.contains("geheim"), "ein Passwort steht im Protokoll:\n{all}");
}

#[test]
fn nothing_is_traced_unless_asked_for() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));

    connect(&fixture, 1, &profile, false, &["geheim"]);

    assert!(lines(&fixture, 1).is_empty());
}

#[test]
fn keepalive_follows_the_profile_and_otherwise_what_the_app_set() {
    let fixture = Fixture::new(with_password());
    let keepalive = |session: u64| {
        lines(&fixture, session)
            .into_iter()
            .find(|line| line.contains("keepalive"))
            .unwrap_or_default()
    };

    // Ohne Angabe: alle 30 Sekunden, drei dürfen unbeantwortet bleiben.
    let plain = fixture.profile(json!({}));
    connect(&fixture, 1, &plain, true, &["geheim"]);
    assert!(
        keepalive(1).ends_with("keepalive every 30 s, the connection counts as lost after 3 unanswered"),
        "{}",
        keepalive(1)
    );

    // Die App nennt etwas anderes: Das gilt für Verbindungen, die danach entstehen.
    fixture.ok(json!({ "op": "keepalive.set", "interval": 10, "max": 6 }));
    connect(&fixture, 2, &plain, true, &["geheim"]);
    assert!(
        keepalive(2).contains("every 10 s") && keepalive(2).contains("after 6 unanswered"),
        "{}",
        keepalive(2)
    );

    // Das Profil geht vor – auch mit „keine“.
    let own = fixture.profile(json!({ "name": "eigen", "keepalive_interval": 120, "keepalive_max": 1 }));
    connect(&fixture, 3, &own, true, &["geheim"]);
    assert!(
        keepalive(3).contains("every 120 s") && keepalive(3).contains("after 1 unanswered"),
        "{}",
        keepalive(3)
    );
    let none = fixture.profile(json!({ "name": "still", "keepalive_interval": 0 }));
    connect(&fixture, 4, &none, true, &["geheim"]);
    assert!(keepalive(4).ends_with("no keepalive"), "{}", keepalive(4));

    // Was kein Abstand und keine Zahl sein kann, nimmt der Kern nicht an – es bleibt beim Vorigen.
    for (interval, max) in [(1, 3), (4000, 3), (30, 0), (30, 21)] {
        let refused = fixture.request(json!({ "op": "keepalive.set", "interval": interval, "max": max }));
        assert_eq!(refused["error"], "invalid_keepalive", "{refused}");
    }
    connect(&fixture, 5, &plain, true, &["geheim"]);
    assert!(keepalive(5).contains("every 10 s"), "{}", keepalive(5));
}

#[test]
fn the_terminal_type_and_environment_of_a_profile_reach_the_server() {
    let fixture = Fixture::new(with_password());
    let typed = |session: u64, line: &str| assert!(fixture.core.write(session, format!("{line}\r").as_bytes()));
    let shown = |session: u64, marker: &str| fixture.recorder.output(session, marker);

    // Ohne Angabe: das übliche Terminal, keine Variablen.
    let plain = fixture.profile(json!({}));
    connect(&fixture, 1, &plain, true, &["geheim"]);
    typed(1, "env");
    typed(1, "echo ende-1");
    let text = shown(1, "ende-1");
    assert!(text.contains("TERM=xterm-256color"), "{text}");
    assert!(!text.contains("LANG="), "{text}");

    // Mit Angabe: Der Server sieht das Terminal und die Variablen, die er annimmt – die andere nicht. Im
    // Protokoll der Verbindung stehen die Namen, nie die Werte.
    let own = fixture.profile(json!({
        "name": "alt", "term": "vt100",
        "env": [
            { "name": "LANG", "value": "de_DE.ISO-8859-1" },
            { "name": "NEXUS_TOKEN", "value": "streng-geheim" },
            { "name": "EDITOR", "value": "vi" },
        ],
    }));
    connect(&fixture, 2, &own, true, &["geheim"]);
    typed(2, "env");
    typed(2, "echo ende-2");
    let text = shown(2, "ende-2");
    assert!(text.contains("TERM=vt100"), "{text}");
    assert!(
        text.contains("LANG=de_DE.ISO-8859-1") && text.contains("NEXUS_TOKEN=streng-geheim"),
        "{text}"
    );
    assert!(!text.contains("EDITOR"), "{text}");
    let traced = lines(&fixture, 2).join("\n");
    assert!(
        traced.contains("sent environment variables: LANG, NEXUS_TOKEN, EDITOR"),
        "{traced}"
    );
    assert!(
        !traced.contains("streng-geheim") && !traced.contains("ISO-8859"),
        "{traced}"
    );
}
