//! Profile, die ihrem Eintrag in `~/.ssh/config` folgen: beim Speichern, und solange der Kern läuft.

mod common;

use std::fs;

use common::Fixture;
use nexus_testserver::Options;
use serde_json::{Value, json};

fn write_config(fixture: &Fixture, name: &str, text: &str) {
    fs::write(fixture.directory.join("ssh").join(name), text).unwrap();
}

fn save(fixture: &Fixture, profile: Value) -> Value {
    fixture.ok(json!({ "op": "profiles.save", "profile": profile }))["profile"].clone()
}

fn stored(fixture: &Fixture, id: &Value) -> Value {
    fixture.ok(json!({ "op": "profiles.list" }))["profiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|profile| &profile["id"] == id)
        .unwrap()
        .clone()
}

#[test]
fn a_profile_linked_while_saving_takes_its_values_from_the_file_at_once() {
    let fixture = Fixture::new(Options::default());
    write_config(
        &fixture,
        "config",
        "Host web\n  HostName 10.0.0.1\n  User deploy\n  Port 2200\n",
    );

    let saved = save(
        &fixture,
        json!({ "name": "web", "host": "old.example.org", "port": 22, "user": "someone", "group": "Kunden", "ssh_alias": "web" }),
    );

    assert_eq!(
        (&saved["host"], &saved["port"], &saved["user"]),
        (&json!("10.0.0.1"), &json!(2200), &json!("deploy")),
        "{saved}"
    );
    // Was die Datei nicht bestimmt, bleibt.
    assert_eq!(saved["group"], "Kunden");
    assert_eq!(saved["ssh_alias"], "web");
}

#[test]
fn a_linked_profile_follows_the_file_while_the_core_runs() {
    let fixture = Fixture::new(Options::default());
    write_config(&fixture, "config", "Host web\n  HostName 10.0.0.1\n  User deploy\n");
    let saved = save(
        &fixture,
        json!({ "name": "web", "host": "x", "port": 22, "user": "x", "ssh_alias": "web" }),
    );

    write_config(
        &fixture,
        "config",
        "Host web\n  HostName 10.0.0.77\n  User root\n  Port 2022\n",
    );
    fixture.recorder.event("profiles.changed");

    let now = stored(&fixture, &saved["id"]);
    assert_eq!(
        (&now["host"], &now["port"], &now["user"]),
        (&json!("10.0.0.77"), &json!(2022), &json!("root")),
        "{now}"
    );
}

#[test]
fn a_change_in_an_included_file_is_noticed_too() {
    let fixture = Fixture::new(Options::default());
    write_config(&fixture, "config", "Include hosts.conf\n");
    write_config(&fixture, "hosts.conf", "Host web\n  HostName 10.0.0.1\n  User deploy\n");
    let saved = save(
        &fixture,
        json!({ "name": "web", "host": "x", "port": 22, "user": "x", "ssh_alias": "web" }),
    );
    assert_eq!(saved["host"], "10.0.0.1");

    write_config(
        &fixture,
        "hosts.conf",
        "Host web\n  HostName included.example.org\n  User deploy\n",
    );
    fixture.recorder.event("profiles.changed");

    assert_eq!(stored(&fixture, &saved["id"])["host"], "included.example.org");
}

#[test]
fn with_an_identity_the_file_only_says_where_the_server_is() {
    let fixture = Fixture::new(Options::default());
    write_config(
        &fixture,
        "config",
        "Host web\n  HostName 10.0.0.1\n  User deploy\n  IdentityFile /keys/from_file\n",
    );

    let saved = save(
        &fixture,
        json!({
            "name": "web", "host": "x", "port": 22, "user": "me", "key_path": "/keys/mine",
            "identity": "work", "ssh_alias": "web"
        }),
    );

    assert_eq!(saved["host"], "10.0.0.1");
    assert_eq!(
        (&saved["user"], &saved["key_path"]),
        (&json!("me"), &json!("/keys/mine")),
        "{saved}"
    );
}

#[test]
fn a_file_that_matches_an_include_pattern_only_later_is_noticed_too() {
    let fixture = Fixture::new(Options::default());
    fs::create_dir_all(fixture.directory.join("ssh").join("config.d")).unwrap();
    write_config(
        &fixture,
        "config",
        "Include config.d/*.conf\nHost web\n  User fallback\n",
    );
    let saved = save(
        &fixture,
        json!({ "name": "web", "host": "x", "port": 22, "user": "x", "ssh_alias": "web" }),
    );
    // Noch nennt keine Datei den Server: Der Name ist die Adresse.
    assert_eq!(saved["host"], "web");

    // Der Beobachter hat den Stand einmal festgehalten, bevor die neue Datei entsteht.
    std::thread::sleep(std::time::Duration::from_secs(4));
    write_config(&fixture, "config.d/web.conf", "Host web\n  HostName late.example.org\n");
    fixture.recorder.event("profiles.changed");

    assert_eq!(stored(&fixture, &saved["id"])["host"], "late.example.org");
}
