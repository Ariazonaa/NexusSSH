//! Host-Zertifikate: Ein Server, dessen Schlüssel eine bekannte Zertifizierungsstelle unterschrieben hat, wird ohne
//! Rückfrage angenommen – und nur dann.

mod common;

use std::time::{SystemTime, UNIX_EPOCH};

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::{Options, host_key};
use russh::keys::Certificate;
use russh::keys::ssh_key::certificate::{Builder, CertType};
use serde_json::json;

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

/// Ein Zertifikat für den Host-Schlüssel des Testservers, unterschrieben vom Schlüssel mit diesem Samen.
fn certificate(ca_seed: u8, principal: &str, valid_after: u64, valid_before: u64) -> Certificate {
    let mut builder = Builder::new_with_random_nonce(
        &mut russh::keys::key::safe_rng(),
        host_key(1).public_key().key_data().clone(),
        valid_after,
        valid_before,
    )
    .unwrap();
    builder.serial(1).unwrap();
    builder.key_id("nexus-test-host").unwrap();
    builder.cert_type(CertType::Host).unwrap();
    builder.valid_principal(principal).unwrap();
    builder.sign(&host_key(ca_seed)).unwrap()
}

fn server_with(certificate: Certificate) -> Fixture {
    Fixture::new(Options {
        host_certificates: vec![certificate],
        ..with_password()
    })
}

/// Trägt die Zertifizierungsstelle in die `known_hosts` des Benutzers ein – die Datei, die die App nur liest.
fn trust_authority(fixture: &Fixture, pattern: &str, ca_seed: u8) {
    let key = host_key(ca_seed).public_key().to_openssh().unwrap();
    std::fs::write(
        fixture.directory.join("ssh").join("known_hosts"),
        format!("# von Hand\n@cert-authority {pattern} {key}\n"),
    )
    .unwrap();
}

fn profile_with_password(fixture: &Fixture) -> String {
    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    profile
}

#[test]
fn a_certificate_from_a_known_authority_is_accepted_without_asking() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    trust_authority(&fixture, &format!("[127.0.0.1]:{}", fixture.port), 200);
    let profile = profile_with_password(&fixture);

    fixture.open(1, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");

    assert!(!fixture.recorder.saw("prompt.host_key"));
    // Gemerkt wird nichts: Das Vertrauen steckt in der Stelle, nicht in diesem einen Schlüssel.
    assert_eq!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Unknown
    );
}

#[test]
fn a_wildcard_pattern_covers_the_server_too() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    trust_authority(&fixture, "10.9.9.9,127.0.0.*", 200);
    let profile = profile_with_password(&fixture);

    fixture.open(1, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.host_key"));
}

#[test]
fn without_an_authority_for_this_server_nothing_changes() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    trust_authority(&fixture, "*.example.org", 200);
    let profile = profile_with_password(&fixture);

    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["changed"], false);
    assert_eq!(prompt["algorithm"], "ssh-ed25519");
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("core.warning"));
}

/// Ein Zertifikat, das nicht hält, macht den Server nicht unerreichbar und nicht vertrauenswürdig: Der Kern sagt,
/// warum es nicht gilt, und fragt dann wie bei jedem unbekannten Schlüssel.
fn falls_back_to_asking(fixture: &Fixture, expected: &str) {
    let profile = profile_with_password(fixture);
    fixture.open(1, &profile);

    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(
        prompt["fingerprint"],
        nexus_core::hostkeys::fingerprint(host_key(1).public_key())
    );
    let warning = fixture
        .recorder
        .find("the reason the certificate was not accepted", |event| {
            event["code"] == "host_certificate_invalid"
        });
    assert!(warning["message"].as_str().unwrap().contains(expected), "{warning}");

    // Lehnt der Benutzer ab, endet die Sitzung wie immer.
    fixture.answer(&prompt, json!({}));
    assert_eq!(fixture.recorder.state("closed")["reason"], "host_key_rejected");
}

#[test]
fn an_expired_certificate_is_not_trusted() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 7200, now() - 3600));
    trust_authority(&fixture, "*", 200);
    falls_back_to_asking(&fixture, "does not hold");
}

#[test]
fn a_certificate_signed_by_someone_else_is_not_trusted() {
    let fixture = server_with(certificate(201, "127.0.0.1", now() - 60, now() + 3600));
    trust_authority(&fixture, "*", 200);
    falls_back_to_asking(&fixture, "does not hold");
}

#[test]
fn a_certificate_for_another_name_is_not_trusted() {
    let fixture = server_with(certificate(200, "web01.example.org", now() - 60, now() + 3600));
    trust_authority(&fixture, "*", 200);
    falls_back_to_asking(&fixture, "not issued for 127.0.0.1");
}

fn probe(fixture: &Fixture, request: u64) -> serde_json::Value {
    let profile = json!({ "name": "test", "host": "127.0.0.1", "port": fixture.port, "user": "demo" });
    fixture.ok(json!({ "op": "probe", "request": request, "profile": profile }));
    fixture.recorder.find("the result of the probe", |event| {
        event["event"] == "probe.result" && event["request"] == request
    })
}

#[test]
fn an_authority_added_in_the_app_is_listed_trusted_and_can_be_removed_again() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    let profile = profile_with_password(&fixture);
    let key = host_key(200).public_key().to_openssh().unwrap();
    let print = nexus_core::hostkeys::fingerprint(host_key(200).public_key());

    // Der Verbindungstest davor: ein unbekannter Server.
    assert_eq!(probe(&fixture, 1)["key_state"], "unknown");

    // Was keine Muster sind oder kein Schlüssel ist, kommt nicht in die Datei.
    for (patterns, key, error) in [
        ("", key.as_str(), "invalid_patterns"),
        ("a b", key.as_str(), "invalid_patterns"),
        ("*\n@cert-authority *", key.as_str(), "invalid_patterns"),
        ("!*.example.org", key.as_str(), "invalid_patterns"),
        ("*", "ssh-ed25519 kein-schlüssel", "invalid_key"),
        ("*", "", "invalid_key"),
    ] {
        let refused = fixture.request(json!({ "op": "hostkeys.authority", "patterns": patterns, "key": key }));
        assert_eq!(refused["error"], error, "{patterns:?}: {refused}");
    }
    assert_eq!(
        fixture.request(json!({ "op": "hostkeys.list" }))["host_keys"],
        json!([])
    );

    // Mit Kommentar eingetragen, ohne ihn gespeichert; zweimal dasselbe ergibt eine Zeile.
    let patterns = format!("[127.0.0.1]:{},*.example.org", fixture.port);
    for _ in 0..2 {
        let added = fixture.request(json!({
            "op": "hostkeys.authority", "patterns": patterns, "key": format!("{key} ca@firma"),
        }));
        assert_eq!(added["ok"], true, "{added}");
        assert_eq!(added["host_keys"][0]["fingerprint"], print.as_str(), "{added}");
    }
    let listed = fixture.request(json!({ "op": "hostkeys.list" }));
    assert_eq!(
        listed["host_keys"],
        json!([{ "name": patterns, "algorithm": "ssh-ed25519", "fingerprint": print, "authority": true }]),
        "{listed}"
    );

    // Der Verbindungstest nennt den Server jetzt vertraut über das Zertifikat, und verbunden wird ohne Rückfrage.
    assert_eq!(probe(&fixture, 2)["key_state"], "certified");
    fixture.open(1, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.host_key"));

    // Über den Weg für Server lässt sich die Stelle nicht entfernen – und nicht mit einem fremden Fingerabdruck.
    for forget in [
        json!({ "op": "hostkeys.forget", "name": "@cert-authority" }),
        json!({ "op": "hostkeys.forget", "name": patterns }),
        json!({ "op": "hostkeys.forget", "name": patterns, "authority": "SHA256:fremd" }),
    ] {
        assert_eq!(fixture.request(forget.clone())["error"], "not_found", "{forget}");
    }
    fixture.ok(json!({ "op": "hostkeys.forget", "name": patterns, "authority": print }));
    assert_eq!(
        fixture.request(json!({ "op": "hostkeys.list" }))["host_keys"],
        json!([])
    );
    assert_eq!(probe(&fixture, 3)["key_state"], "unknown");
}

#[test]
fn a_revoked_key_stays_revoked_even_with_a_certificate_that_holds() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    let authority = host_key(200).public_key().to_openssh().unwrap();
    let server = host_key(1).public_key().to_openssh().unwrap();
    std::fs::write(
        fixture.directory.join("ssh").join("known_hosts"),
        format!("@cert-authority * {authority}\n@revoked * {server}\n"),
    )
    .unwrap();
    let profile = profile_with_password(&fixture);

    assert_eq!(probe(&fixture, 1)["key_state"], "changed");
    fixture.open(1, &profile);
    assert_eq!(fixture.recorder.state("closed")["reason"], "host_key_rejected");
    assert!(!fixture.recorder.saw("prompt.host_key"));
}

#[test]
fn the_probe_names_a_certificate_that_does_not_hold_as_what_its_key_is() {
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 7200, now() - 3600));
    trust_authority(&fixture, "*", 200);

    assert_eq!(probe(&fixture, 1)["key_state"], "unknown");
}

#[test]
fn a_revoked_authority_certifies_nothing() {
    // Die Stelle steht in der eigenen Datei, ihr Widerruf in der des Benutzers: Das Zertifikat hält dann nicht
    // mehr, und der Kern bittet gar nicht erst darum – der Server ist ein unbekannter wie jeder andere.
    let fixture = server_with(certificate(200, "127.0.0.1", now() - 60, now() + 3600));
    let authority = host_key(200).public_key().to_openssh().unwrap();
    fixture.ok(json!({ "op": "hostkeys.authority", "patterns": "*", "key": authority }));
    assert_eq!(probe(&fixture, 1)["key_state"], "certified");

    std::fs::write(
        fixture.directory.join("ssh").join("known_hosts"),
        format!("@revoked * {authority}\n"),
    )
    .unwrap();
    assert_eq!(probe(&fixture, 2)["key_state"], "unknown");

    let profile = profile_with_password(&fixture);
    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["changed"], false, "{prompt}");
    fixture.answer(&prompt, json!({}));
    assert_eq!(fixture.recorder.state("closed")["reason"], "host_key_rejected");
}

/// Echte OpenSSH-Server mit `HostCertificate` – der Testserver zeigt Zertifikate nur aus derselben Bibliothek, mit
/// der der Kern sie liest. Läuft nur auf Wunsch: `NEXUS_REAL_HOSTCERT_PORTS` nennt drei Ports auf 127.0.0.1
/// (gültiges Zertifikat, abgelaufenes, eines einer fremden Stelle – jeweils ein sshd mit `HostCertificate`, dessen
/// Zertifikat für `127.0.0.1` ausgestellt ist), `NEXUS_REAL_HOSTCERT_CA` die Datei mit dem
/// öffentlichen Schlüssel der Stelle und `NEXUS_REAL_HOSTCERT_KEY` den privaten Schlüssel für `certuser`.
#[test]
fn real_openssh_servers_show_host_certificates_the_core_checks() {
    let variable = |name: &str| std::env::var(name).ok().filter(|value| !value.is_empty());
    let (Some(ports), Some(authority), Some(key)) = (
        variable("NEXUS_REAL_HOSTCERT_PORTS"),
        variable("NEXUS_REAL_HOSTCERT_CA"),
        variable("NEXUS_REAL_HOSTCERT_KEY"),
    ) else {
        return;
    };
    let ports: Vec<u16> = ports.split(',').map(|port| port.trim().parse().unwrap()).collect();
    let [valid, expired, foreign] = ports[..] else {
        panic!("three ports are needed");
    };
    let authority = std::fs::read_to_string(authority).unwrap();

    let fixture = Fixture::new(with_password());
    let state = |request: u64, port: u16| {
        let profile = json!({ "name": "real", "host": "127.0.0.1", "port": port, "user": "certuser" });
        fixture.ok(json!({ "op": "probe", "request": request, "profile": profile }));
        let result = fixture.recorder.find("the result of the probe", |event| {
            event["event"] == "probe.result" && event["request"] == request
        });
        assert_eq!(result["key_algorithm"], "ssh-ed25519", "{result}");
        result["key_state"].as_str().unwrap().to_owned()
    };

    // Ohne Stelle sind alle drei unbekannte Server.
    assert_eq!(
        [state(1, valid), state(2, expired), state(3, foreign)],
        ["unknown", "unknown", "unknown"]
    );

    fixture.ok(json!({ "op": "hostkeys.authority", "patterns": "127.0.0.1", "key": authority }));
    assert_eq!(
        [state(4, valid), state(5, expired), state(6, foreign)],
        ["certified", "unknown", "unknown"]
    );

    let open = |session: u64, port: u16| {
        let profile = fixture.profile(json!({
            "name": format!("real-{port}"), "port": port, "user": "certuser", "key_path": key,
        }));
        fixture.ok(json!({
            "op": "session.open", "session": session, "profile_id": profile, "cols": 80, "rows": 24,
        }));
    };

    // Das gültige Zertifikat: verbunden, ohne Rückfrage, und nichts wird gemerkt.
    open(1, valid);
    fixture.recorder.find("connected", |event| {
        event["event"] == "session.state" && event["session"] == 1 && event["state"] == "connected"
    });
    assert!(!fixture.recorder.saw("prompt.host_key"));
    assert!(!fixture.recorder.saw("core.warning"));
    assert_eq!(
        fixture.request(json!({ "op": "hostkeys.list" }))["host_keys"]
            .as_array()
            .unwrap()
            .len(),
        1
    );

    // Abgelaufen oder von einer fremden Stelle: Der Kern sagt, dass es nicht hält, und fragt wie bei jedem
    // unbekannten Schlüssel.
    for (session, port) in [(2, expired), (3, foreign)] {
        open(session, port);
        let prompt = fixture.recorder.find("the question for the host key", |event| {
            event["event"] == "prompt.host_key" && event["session"] == session
        });
        assert_eq!(prompt["changed"], false, "{prompt}");
        let warnings = fixture.recorder.all("core.warning");
        assert!(
            warnings
                .iter()
                .any(|warning| warning["code"] == "host_certificate_invalid"
                    && warning["message"].as_str().unwrap().contains("does not hold")),
            "{warnings:?}"
        );
        fixture.answer(&prompt, json!({}));
        fixture.recorder.find("closed", |event| {
            event["event"] == "session.state" && event["session"] == session && event["state"] == "closed"
        });
    }
}
