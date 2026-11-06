//! Verbindungen über Sprung-Server (`ProxyJump`): Der Kern meldet sich erst am Sprung-Server an, lässt sich von
//! dort zum Ziel durchreichen und baut darüber die eigentliche SSH-Verbindung auf.

mod common;

use common::{Fixture, with_password};
use nexus_core::hostkeys::{Verdict, fingerprint};
use nexus_testserver::{Options, host_key};
use serde_json::json;

fn jump_server() -> Options {
    Options {
        forwarding: true,
        ..with_password()
    }
}

fn target_server() -> Options {
    Options {
        password: Some(("app".into(), "ziel".into())),
        ..Options::default()
    }
}

#[test]
fn a_session_through_a_jump_host_checks_and_signs_in_at_both_servers() {
    let fixture = Fixture::new(jump_server());
    let target_server = fixture.another_server(2, target_server());
    let target_port = target_server.address.port();

    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let target = fixture.profile(json!({ "name": "target", "port": target_port, "user": "app", "jump": bastion }));

    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": target, "cols": 80, "rows": 24 }));

    // Erst der Sprung-Server: Jede Meldung nennt, um welchen Server es geht.
    let jump_label = format!("demo@127.0.0.1:{}", fixture.port);
    assert_eq!(fixture.recorder.state("connecting")["hop"], jump_label);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["port"], fixture.port);
    assert_eq!(prompt["fingerprint"], fingerprint(host_key(1).public_key()));
    fixture.answer(&prompt, json!({ "accept": true }));
    assert_eq!(fixture.recorder.state("authenticating")["hop"], jump_label);
    let secret = fixture.recorder.event("prompt.secret");
    assert_eq!(secret["user"], "demo");
    assert_eq!(
        secret["host"],
        format!("127.0.0.1:{}", fixture.port),
        "mit Port: beide Server haben dieselbe Adresse"
    );
    fixture.answer(&secret, json!({ "values": ["geheim"] }));

    // Dann das Ziel, mit eigenem Host-Schlüssel und eigener Anmeldung.
    let target_label = format!("app@127.0.0.1:{target_port}");
    assert_eq!(fixture.recorder.state("connecting")["hop"], target_label);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["port"], target_port);
    assert_eq!(prompt["fingerprint"], fingerprint(host_key(2).public_key()));
    fixture.answer(&prompt, json!({ "accept": true }));
    assert_eq!(fixture.recorder.state("authenticating")["hop"], target_label);
    let secret = fixture.recorder.event("prompt.secret");
    assert_eq!(secret["user"], "app");
    fixture.answer(&secret, json!({ "values": ["ziel"] }));

    fixture.recorder.state("connected");
    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"echo durch den tunnel\r"));
    assert!(
        fixture
            .recorder
            .output(1, "\r\n$ ")
            .contains("\r\ndurch den tunnel\r\n")
    );

    // Beide Server sind jetzt bekannt, jeder mit seinem eigenen Schlüssel.
    let known = fixture.own_known_hosts();
    assert_eq!(
        known.check("127.0.0.1", fixture.port, host_key(1).public_key()),
        Verdict::Trusted
    );
    assert_eq!(
        known.check("127.0.0.1", target_port, host_key(2).public_key()),
        Verdict::Trusted
    );

    // Vermerkt wird die Verbindung beim Ziel, nicht beim Sprung-Server.
    let profiles = fixture.ok(json!({ "op": "profiles.list" }));
    let last = |name: &str| {
        profiles["profiles"]
            .as_array()
            .unwrap()
            .iter()
            .find(|profile| profile["name"] == name)
            .unwrap()["last_connected"]
            .clone()
    };
    assert!(last("target").is_u64());
    assert!(last("bastion").is_null());

    assert!(fixture.core.write(1, b"exit\r"));
    assert_eq!(fixture.recorder.state("closed")["reason"], "remote");
}

#[test]
fn a_jump_host_that_does_not_forward_ends_the_session_with_a_clear_reason() {
    // Der Sprung-Server nimmt die Anmeldung an, reicht aber keine Verbindungen weiter.
    let fixture = Fixture::new(with_password());
    let target_server = fixture.another_server(2, target_server());

    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let target = fixture
        .profile(json!({ "name": "target", "port": target_server.address.port(), "user": "app", "jump": bastion }));

    fixture.open(1, &target);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));

    let closed = fixture
        .recorder
        .find("Ende der Sitzung", |event| event["state"] == "closed");
    assert_eq!(closed["reason"], "connect_failed");
    assert!(
        closed["message"]
            .as_str()
            .unwrap()
            .starts_with("bastion could not reach"),
        "{closed}"
    );
}

#[test]
fn a_failed_sign_in_at_the_jump_host_names_it() {
    let fixture = Fixture::new(jump_server());
    let target_server = fixture.another_server(2, target_server());

    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let target = fixture
        .profile(json!({ "name": "target", "port": target_server.address.port(), "user": "app", "jump": bastion }));

    fixture.open(1, &target);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({}));

    let closed = fixture
        .recorder
        .find("Ende der Sitzung", |event| event["state"] == "closed");
    assert_eq!(closed["reason"], "auth_failed");
    assert_eq!(
        closed["message"],
        format!("jump host demo@127.0.0.1:{}: the sign-in was cancelled", fixture.port)
    );
    assert!(
        fixture.recorder.all("prompt.host_key").len() == 1,
        "das Ziel wurde nie angesprochen"
    );
}

#[test]
fn an_unreachable_target_behind_the_jump_host_is_a_connect_failure() {
    let fixture = Fixture::new(jump_server());

    // Ein Port, auf dem sicher niemand lauscht: kurz belegen und wieder freigeben.
    let free_port = std::net::TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port();

    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let target = fixture.profile(json!({ "name": "target", "port": free_port, "jump": bastion }));

    fixture.open(1, &target);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));

    let closed = fixture
        .recorder
        .find("Ende der Sitzung", |event| event["state"] == "closed");
    assert_eq!(closed["reason"], "connect_failed");
}

#[test]
fn a_missing_or_circular_jump_profile_is_refused_before_connecting() {
    let fixture = Fixture::new(jump_server());

    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let target = fixture.profile(json!({ "name": "target", "jump": bastion }));
    fixture.ok(json!({ "op": "profiles.delete", "id": bastion }));

    let response =
        fixture.request(json!({ "op": "session.open", "session": 1, "profile_id": target, "cols": 80, "rows": 24 }));
    assert_eq!(response["ok"], false);
    assert_eq!(response["error"], "jump_missing");

    // a → b → a
    let first = fixture.profile(json!({ "name": "a" }));
    let second = fixture.profile(json!({ "name": "b", "jump": first }));
    fixture.ok(json!({ "op": "profiles.save", "profile": {
        "id": first, "name": "a", "host": "127.0.0.1", "port": fixture.port, "user": "demo", "jump": second,
    } }));
    let response =
        fixture.request(json!({ "op": "session.open", "session": 2, "profile_id": first, "cols": 80, "rows": 24 }));
    assert_eq!(response["error"], "jump_loop");

    let response = fixture.request(json!({ "op": "profiles.save", "profile": {
        "id": first, "name": "a", "host": "127.0.0.1", "user": "demo", "jump": first,
    } }));
    assert_eq!(response["error"], "invalid_profile");
    assert_eq!(response["message"], "jump");

    assert!(
        !fixture.recorder.saw("session.state"),
        "nichts davon hat eine Verbindung begonnen"
    );
}
