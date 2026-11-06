//! Vorläufige Profile („Schnellverbinden“): Sie verhalten sich wie jedes Profil, überleben aber keinen Neustart.

mod common;

use common::{Fixture, with_password};
use nexus_core::secrets::SecretStore;
use serde_json::json;

#[test]
fn a_transient_profile_connects_like_any_other_and_is_gone_after_a_restart() {
    let mut fixture = Fixture::new(with_password());
    let kept = fixture.profile(json!({ "name": "bleibt" }));
    let quick = fixture.profile(json!({ "name": "demo@127.0.0.1", "transient": true }));

    let listed = fixture.ok(json!({ "op": "profiles.list" }));
    let profiles = listed["profiles"].as_array().unwrap();
    assert_eq!(profiles.len(), 2);
    assert!(
        profiles
            .iter()
            .any(|profile| profile["id"] == quick.as_str() && profile["transient"] == true)
    );
    // Ein gewöhnliches Profil trägt das Feld gar nicht.
    assert!(
        profiles
            .iter()
            .any(|profile| profile["id"] == kept.as_str() && profile.get("transient").is_none())
    );

    fixture.open(1, &quick);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"], "save": true }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.ok(json!({ "op": "session.close", "session": 1 }));

    // Der Kern endet, ohne dass jemand aufgeräumt hätte – beim nächsten Start ist das vorläufige Profil weg, und
    // mit ihm sein gespeichertes Passwort.
    fixture.restart_core();
    let listed = fixture.ok(json!({ "op": "profiles.list" }));
    let profiles = listed["profiles"].as_array().unwrap();
    assert_eq!(profiles.len(), 1);
    assert_eq!(profiles[0]["id"], kept.as_str());
    assert_eq!(fixture.secrets.get(&nexus_core::secrets::password_key(&quick)), None);

    // Behalten heißt: dasselbe Profil ohne die Marke speichern.
    let again = fixture.profile(json!({ "name": "noch eins", "transient": true }));
    fixture.ok(json!({ "op": "profiles.save", "profile": {
        "id": again, "name": "noch eins", "host": "127.0.0.1", "port": fixture.port, "user": "demo", "use_agent": false,
    } }));
    fixture.restart_core();
    assert_eq!(
        fixture.ok(json!({ "op": "profiles.list" }))["profiles"]
            .as_array()
            .unwrap()
            .len(),
        2
    );
}
