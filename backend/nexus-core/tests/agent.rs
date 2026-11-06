//! Der SSH-Agent: Anmeldung mit seinen Schlüsseln und Agent Forwarding. Der Agent der Tests ist der von russh,
//! erreichbar wie ein echter (Named Pipe unter Windows, Unix-Socket sonst).

mod common;

use common::{Fixture, authorized, user_key};
use nexus_testserver::Options;
use serde_json::json;

fn key_only() -> Options {
    Options {
        authorized_keys: authorized(),
        ..Options::default()
    }
}

#[test]
fn a_key_from_the_agent_signs_in_without_any_key_file() {
    let fixture = Fixture::new(key_only());
    fixture.start_agent(&[user_key()]);
    let profile = fixture.profile(json!({ "use_agent": true }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.secret"));
}

/// Fünf Schlüssel, die der Server nicht kennt, vor dem richtigen – wie in einem Passwort-Manager mit vielen.
fn crowded_agent(fixture: &Fixture) {
    use russh::keys::PrivateKey;
    use russh::keys::ssh_key::private::Ed25519Keypair;

    let mut keys: Vec<PrivateKey> = (1..=5u8)
        .map(|seed| PrivateKey::from(Ed25519Keypair::from_seed(&[seed; 32])))
        .collect();
    keys.push(user_key());
    fixture.start_agent(&keys);
}

#[test]
fn a_public_key_file_in_the_profile_picks_that_key_from_a_crowded_agent() {
    // Wie mit Bitwarden: Auf der Platte liegt nur der öffentliche Teil, der geheime beim Agenten.
    let fixture = Fixture::new(key_only());
    crowded_agent(&fixture);
    let key_path = fixture.directory.join("server.pub");
    std::fs::write(&key_path, user_key().public_key().to_openssh().unwrap()).unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.secret"));
    assert!(
        !fixture.recorder.saw("core.warning"),
        "eine Datei mit dem öffentlichen Teil ist kein Fehler"
    );
}

#[test]
fn a_key_file_whose_key_the_agent_holds_is_never_unlocked() {
    // Die Datei ist verschlüsselt; ihr öffentlicher Teil steht vorn unverschlüsselt. Hält der Agent den Schlüssel,
    // signiert er – nach der Passphrase fragt niemand.
    use russh::keys::ssh_key::LineEnding;

    let fixture = Fixture::new(key_only());
    crowded_agent(&fixture);
    let key_path = fixture.directory.join("mein_schluessel");
    let encrypted = user_key().encrypt(&mut russh::keys::key::safe_rng(), "sesam").unwrap();
    std::fs::write(&key_path, encrypted.to_openssh(LineEnding::LF).unwrap().as_bytes()).unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.secret"));
}

#[test]
fn an_outdated_public_key_beside_the_key_file_does_not_keep_the_file_from_being_used() {
    // Neben der Schlüsseldatei liegt ein `.pub`, das zu einem anderen Schlüssel gehört – und den hält der Agent.
    // Maßgeblich ist, was in der Schlüsseldatei selbst steht: Sie meldet an.
    use russh::keys::PrivateKey;
    use russh::keys::ssh_key::LineEnding;
    use russh::keys::ssh_key::private::Ed25519Keypair;

    let fixture = Fixture::new(key_only());
    let other = PrivateKey::from(Ed25519Keypair::from_seed(&[7; 32]));
    fixture.start_agent(std::slice::from_ref(&other));
    let key_path = fixture.directory.join("mein_schluessel");
    std::fs::write(&key_path, user_key().to_openssh(LineEnding::LF).unwrap().as_bytes()).unwrap();
    std::fs::write(
        fixture.directory.join("mein_schluessel.pub"),
        other.public_key().to_openssh().unwrap(),
    )
    .unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
}

#[test]
fn the_public_key_beside_a_missing_key_file_still_picks_the_key_from_the_agent() {
    // Das Profil nennt `schluessel`, auf der Platte liegt nur `schluessel.pub` – der geheime Teil ist beim Agenten.
    let fixture = Fixture::new(key_only());
    crowded_agent(&fixture);
    std::fs::write(
        fixture.directory.join("schluessel.pub"),
        user_key().public_key().to_openssh().unwrap(),
    )
    .unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": fixture.directory.join("schluessel") }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.secret"));
}

#[test]
fn a_failed_sign_in_says_when_the_agent_held_more_keys_than_were_offered() {
    // Vier Schlüssel im Agenten, drei werden angeboten (jeder zählt beim Server als Versuch). Scheitert die
    // Anmeldung, erfährt der Benutzer, dass einer nie an der Reihe war – und kann ihn im Profil nennen.
    use russh::keys::PrivateKey;
    use russh::keys::ssh_key::private::Ed25519Keypair;

    let fixture = Fixture::new(key_only());
    let keys: Vec<PrivateKey> = (1..=4u8)
        .map(|seed| PrivateKey::from(Ed25519Keypair::from_seed(&[seed; 32])))
        .collect();
    fixture.start_agent(&keys);
    let profile = fixture.profile(json!({ "use_agent": true }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
    let warning = fixture
        .recorder
        .find("the hint about keys that were not offered", |event| {
            event["event"] == "core.warning" && event["code"] == "agent_more_keys"
        });
    assert_eq!(
        warning["message"],
        "test: 3 keys of the SSH agent were offered, 1 more were not"
    );
}

#[test]
fn a_signature_the_agent_declines_does_not_stall_the_sign_in() {
    // Der Server will den Schlüssel, der Agent signiert nicht (der Benutzer hat im Passwort-Manager abgelehnt).
    // Die Anmeldung geht mit dem Nächsten weiter – hier mit der Frage nach dem Passwort –, statt stehen zu bleiben.
    let fixture = Fixture::new(Options {
        password: Some(("demo".into(), "geheim".into())),
        ..key_only()
    });
    fixture.start_declining_agent(&[user_key()]);
    let profile = fixture.profile(json!({ "use_agent": true }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture.recorder.state("connected");
}

#[test]
fn a_declined_signature_for_the_key_of_the_profile_ends_the_sign_in_with_the_reason() {
    // Das Profil nennt den Schlüssel, der Agent hält ihn, der Benutzer lehnt ab. Andere Schlüssel des Agenten
    // werden danach nicht mehr durchprobiert, und im Protokoll steht, woran es lag.
    use russh::keys::PrivateKey;
    use russh::keys::ssh_key::private::Ed25519Keypair;

    let fixture = Fixture::new(key_only());
    let other = PrivateKey::from(Ed25519Keypair::from_seed(&[9; 32]));
    fixture.start_declining_agent(&[other, user_key()]);
    let key_path = fixture.directory.join("server.pub");
    std::fs::write(&key_path, user_key().public_key().to_openssh().unwrap()).unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.ok(
        json!({ "op": "session.open", "session": 1, "profile_id": profile, "cols": 80, "rows": 24, "trace": true }),
    );
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
    let warning = fixture.recorder.find("the hint that the agent did not sign", |event| {
        event["event"] == "core.warning" && event["code"] == "agent_declined"
    });
    assert_eq!(warning["message"], "test: the SSH agent did not sign");

    let about_keys: Vec<String> = fixture
        .recorder
        .all("session.trace")
        .iter()
        .filter_map(|event| event["text"].as_str().map(str::to_owned))
        .filter(|text| text.contains("agent key ") || text.contains("holds only the public key"))
        .collect();
    assert_eq!(about_keys.len(), 1, "{about_keys:?}");
    assert!(about_keys[0].contains("the agent did not sign"), "{about_keys:?}");
}

#[test]
fn a_locked_agent_is_asked_for_its_keys_once_and_named_as_the_likely_reason() {
    // Ein gesperrter Passwort-Manager lässt die Frage nach den Schlüsseln offen und lehnt dann ab. Das Profil nennt
    // einen Schlüssel – gefragt wird trotzdem nur einmal, nicht je Runde, und am Ende steht, woran es wohl lag.
    use std::sync::atomic::Ordering;
    use std::time::Duration;

    let fixture = Fixture::new(key_only());
    let asked = fixture.start_locked_agent(Duration::from_millis(3300));
    let key_path = fixture.directory.join("server.pub");
    std::fs::write(&key_path, user_key().public_key().to_openssh().unwrap()).unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
    let warning = fixture.recorder.find("the hint about a locked agent", |event| {
        event["event"] == "core.warning" && event["code"] == "agent_locked"
    });
    assert_eq!(
        warning["message"],
        "test: the SSH agent named no keys after a long wait"
    );
    assert_eq!(asked.load(Ordering::SeqCst), 1);
}

#[test]
fn a_public_key_file_without_its_key_in_the_agent_is_no_key_at_all() {
    // Nur der öffentliche Teil und kein Agent, der den geheimen hält: Damit lässt sich nicht anmelden – und die
    // Datei wird auch nicht als kaputter Schlüssel gemeldet oder nach einer Passphrase gefragt.
    let fixture = Fixture::new(key_only());
    fixture.start_agent(&[]);
    let key_path = fixture.directory.join("server.pub");
    std::fs::write(&key_path, user_key().public_key().to_openssh().unwrap()).unwrap();
    let profile = fixture.profile(json!({ "use_agent": true, "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
    assert!(!fixture.recorder.saw("prompt.secret"));
    assert!(!fixture.recorder.saw("core.warning"));
}

#[test]
fn without_a_running_agent_the_sign_in_moves_on() {
    // Kein Agent gestartet: Der Kern darf daran nicht hängen bleiben, sondern scheitert mangels anderer Methode.
    let fixture = Fixture::new(key_only());
    let profile = fixture.profile(json!({ "use_agent": true }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
}

#[test]
fn a_profile_that_does_not_use_the_agent_never_asks_it() {
    let fixture = Fixture::new(key_only());
    fixture.start_agent(&[user_key()]);
    let profile = fixture.profile(json!({ "use_agent": false }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
}

fn forwarding_server() -> Options {
    Options {
        agent_forwarding: true,
        ..key_only()
    }
}

fn connect(fixture: &Fixture, profile: &str) {
    fixture.open(1, profile);
    fixture.trust_host_key();
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.recorder.output(1, "$ ");
}

#[test]
fn agent_forwarding_lets_the_server_use_the_local_agent() {
    let fixture = Fixture::new(forwarding_server());
    fixture.start_agent(&[user_key()]);
    let profile = fixture.profile(json!({ "use_agent": true, "forward_agent": true }));
    connect(&fixture, &profile);

    // Der Befehl `agent` des Testservers öffnet einen Agent-Kanal und fragt nach den Schlüsseln.
    assert!(fixture.core.write(1, b"agent\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("agent: 1 key(s)"));
}

#[test]
fn without_permission_in_the_profile_the_server_cannot_reach_the_agent() {
    // Derselbe Server versucht es ungefragt – das Profil erlaubt es nicht, also lehnt der Kern den Kanal ab.
    let fixture = Fixture::new(forwarding_server());
    fixture.start_agent(&[user_key()]);
    let profile = fixture.profile(json!({ "use_agent": true }));
    connect(&fixture, &profile);

    assert!(fixture.core.write(1, b"agent\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("agent: no answer"));
}

#[test]
fn forwarding_without_an_agent_warns_and_still_connects() {
    let fixture = Fixture::new(Options {
        password: Some(("demo".into(), "geheim".into())),
        agent_forwarding: true,
        ..Options::default()
    });
    let profile = fixture.profile(json!({ "forward_agent": true }));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");

    let warning = fixture
        .recorder
        .find("Warnung", |event| event["event"] == "core.warning");
    assert_eq!(warning["code"], "agent_unavailable");
}

#[test]
fn a_jump_host_never_gets_the_agent() {
    // Sprung-Server und Ziel sind derselbe Testserver, der den Agenten ungefragt anspricht. Erlaubt ist das
    // Forwarding nur im Profil des Sprung-Servers – für die Verbindung dorthin gilt es trotzdem nicht, weil sie
    // hier nur als Sprung dient; das Ziel hat keine Erlaubnis.
    let fixture = Fixture::new(Options {
        forwarding: true,
        ..forwarding_server()
    });
    fixture.start_agent(&[user_key()]);
    let bastion = fixture.profile(json!({ "name": "bastion", "use_agent": true, "forward_agent": true }));
    let target = fixture.profile(json!({ "name": "target", "use_agent": true, "jump": bastion }));

    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": target, "cols": 80, "rows": 24 }));
    fixture.trust_host_key();
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.recorder.output(1, "$ ");

    assert!(fixture.core.write(1, b"agent\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("agent: no answer"));
}
