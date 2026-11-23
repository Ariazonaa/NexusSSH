//! Löschvorgänge (`delete.start`) gegen den SFTP-Teil von `nexus-testserver`: wie weit sie sind, Anhalten und
//! Scheitern. Langsam zu löschen sind Dateien mit der Endung `.slow` (`nexus_testserver` `SLOW_REMOVE`) – daran
//! lässt sich dem Zähler zusehen und ein Löschvorgang mittendrin anhalten.

mod common;

use std::path::{Path, PathBuf};

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::Options;
use serde_json::{Value, json};

/// Verbundene Sitzung 1 mit SFTP; das zweite Feld erscheint dem Kern als `/`.
fn connected() -> (Fixture, PathBuf) {
    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let remote = fixture.directory.join("remote");
    std::fs::create_dir_all(&remote).unwrap();

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    (fixture, remote)
}

fn write(path: impl AsRef<Path>) {
    std::fs::create_dir_all(path.as_ref().parent().unwrap()).unwrap();
    std::fs::write(path, "x").unwrap();
}

fn start(fixture: &Fixture, delete: u64, paths: &[&str]) {
    fixture.ok(json!({ "op": "delete.start", "session": 1, "delete": delete, "paths": paths }));
}

/// Wartet auf den Endzustand eines Löschvorgangs.
fn ended(fixture: &Fixture, delete: u64) -> Value {
    fixture.recorder.find("Ende des Löschvorgangs", |event| {
        event["event"] == "delete.state"
            && event["delete"] == delete
            && matches!(event["state"].as_str(), Some("done" | "failed" | "cancelled"))
    })
}

/// Alle Meldungen eines Löschvorgangs, der Reihe nach.
fn states(fixture: &Fixture, delete: u64) -> Vec<Value> {
    fixture
        .recorder
        .all("delete.state")
        .into_iter()
        .filter(|event| event["delete"] == delete)
        .collect()
}

fn count_in(directory: impl AsRef<Path>) -> usize {
    std::fs::read_dir(directory).unwrap().count()
}

#[test]
fn a_folder_goes_away_entry_by_entry_while_the_count_climbs() {
    let (fixture, remote) = connected();
    // 40 langsame Dateien (über eine Sekunde), 10 Ordner mit je 10 schnellen, dazu eine einzelne Datei.
    for number in 0..40 {
        write(remote.join("big").join("slow").join(format!("{number}.slow")));
    }
    for folder in 0..10 {
        for number in 0..10 {
            write(
                remote
                    .join("big")
                    .join("many")
                    .join(format!("sub-{folder}"))
                    .join(format!("{number}.txt")),
            );
        }
    }
    write(remote.join("single.txt"));
    write(remote.join("stays.txt"));
    // Dateien, Ordner darunter, die Ordner `slow`, `many` und `big` selbst – und die einzelne Datei.
    let total = 40 + 100 + 10 + 3 + 1;

    let began = std::time::Instant::now();
    start(&fixture, 7, &["/big", "/single.txt"]);
    let end = ended(&fixture, 7);
    let took = began.elapsed();

    assert_eq!(end["state"], "done", "{end}");
    assert_eq!(end["removed"], total, "{end}");
    assert!(end.get("error").is_none(), "{end}");
    assert!(!remote.join("big").exists());
    assert!(!remote.join("single.txt").exists());
    assert!(remote.join("stays.txt").exists());

    // Gleich zu Beginn „läuft, 0“; dazwischen steigt der Zähler, nie zurück; das Ende genau einmal, zuletzt.
    let states = states(&fixture, 7);
    assert_eq!(states[0]["state"], "running", "{states:#?}");
    assert_eq!(states[0]["removed"], 0, "{states:#?}");
    let counts: Vec<u64> = states.iter().map(|state| state["removed"].as_u64().unwrap()).collect();
    assert!(counts.is_sorted(), "{counts:?}");
    assert!(
        counts.iter().any(|&count| count > 0 && count < total),
        "kein Zwischenstand: {counts:?}"
    );
    let ends = states.iter().filter(|state| state["state"] != "running").count();
    assert_eq!(ends, 1, "{states:#?}");
    assert_eq!(states.last().unwrap()["state"], "done");

    // Nicht öfter als alle 250 ms (`DELETE_REPORT`), nie eine je Eintrag. Gemessen an der Dauer, nicht an einer festen
    // Zahl: Auf einem ausgelasteten Runner dauerte es länger, und die Grenze 20 riss mit 21 Meldungen (2026-11-21).
    // Dazu die Meldung zu Beginn, die am Ende und eine angefangene Spanne.
    let most = (took.as_millis() / nexus_core::core::DELETE_REPORT.as_millis()) as usize + 3;
    assert!(
        states.len() <= most && states.len() < total as usize,
        "{} Meldungen in {took:?} (höchstens {most})",
        states.len()
    );
}

#[test]
fn a_cancelled_deletion_stops_after_the_current_entry_and_what_is_left_stays() {
    let (fixture, remote) = connected();
    // Hundert langsame Dateien: gut drei Sekunden – genug Zeit, um mittendrin anzuhalten.
    for number in 0..100 {
        write(remote.join("slow").join(format!("{number}.slow")));
    }

    start(&fixture, 3, &["/slow"]);

    // Dieselbe Nummer noch einmal, solange er läuft: abgelehnt, der erste läuft weiter.
    let twice = fixture.request(json!({ "op": "delete.start", "session": 1, "delete": 3, "paths": ["/slow"] }));
    assert_eq!(twice["error"], "delete_exists", "{twice}");

    fixture.recorder.find("Zwischenstand", |event| {
        event["event"] == "delete.state"
            && event["delete"] == 3
            && event["removed"].as_u64().is_some_and(|count| count > 0)
    });
    fixture.ok(json!({ "op": "delete.cancel", "delete": 3 }));
    let end = ended(&fixture, 3);

    assert_eq!(end["state"], "cancelled", "{end}");
    let removed = end["removed"].as_u64().unwrap() as usize;
    assert!(removed > 0 && removed < 100, "{end}");
    // Was gezählt ist, ist weg – und nur das; der Ordner bleibt mit dem Rest.
    assert_eq!(count_in(remote.join("slow")), 100 - removed);

    // Anhalten, was schon zu Ende ist oder nie lief: kein Fehler.
    fixture.ok(json!({ "op": "delete.cancel", "delete": 3 }));
    fixture.ok(json!({ "op": "delete.cancel", "delete": 999 }));
    assert_eq!(
        states(&fixture, 3)
            .iter()
            .filter(|state| state["state"] != "running")
            .count(),
        1
    );
}

#[test]
fn a_deletion_that_fails_says_why_and_keeps_what_it_already_removed() {
    let (fixture, remote) = connected();
    write(remote.join("first.txt"));
    write(remote.join("after.txt"));

    start(&fixture, 1, &["/first.txt", "/nope", "/after.txt"]);
    let end = ended(&fixture, 1);

    assert_eq!(end["state"], "failed", "{end}");
    assert_eq!(end["error"], "not_found", "{end}");
    assert!(
        end["message"].as_str().is_some_and(|message| !message.is_empty()),
        "{end}"
    );
    assert_eq!(end["removed"], 1, "{end}");
    assert!(!remote.join("first.txt").exists());
    assert!(
        remote.join("after.txt").exists(),
        "nach dem Fehler wird nichts mehr gelöscht"
    );

    // Ohne Dateikanal gibt es keinen Löschvorgang – die Antwort sagt es gleich.
    let unknown = fixture.request(json!({ "op": "delete.start", "session": 42, "delete": 2, "paths": ["/after.txt"] }));
    assert_eq!(unknown["error"], "no_sftp", "{unknown}");
    assert!(remote.join("after.txt").exists());
}
