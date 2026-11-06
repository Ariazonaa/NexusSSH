//! Die Suche hört nach einer Höchstzahl angesehener Einträge auf – und sagt es. Eine eigene Testdatei, also ein
//! eigener Prozess: Die Grenze ist für den Test herabgesetzt und gälte sonst auch für die Tests daneben.

mod common;

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::Options;
use serde_json::{Value, json};

#[test]
fn a_search_that_stops_early_says_after_how_many_entries() {
    nexus_core::set_find_limit_for_tests(50);

    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let remote = fixture.directory.join("remote");
    // Ebene für Ebene: Der nahe Treffer liegt unter den ersten fünfzig, der ferne dahinter.
    std::fs::create_dir_all(remote.join("big").join("deep")).unwrap();
    std::fs::create_dir_all(remote.join("small")).unwrap();
    std::fs::write(remote.join("big").join("needle-near.txt"), b"").unwrap();
    for number in 0..200 {
        std::fs::write(remote.join("big").join(format!("file-{number:03}")), b"").unwrap();
    }
    std::fs::write(remote.join("big").join("deep").join("needle-far.txt"), b"").unwrap();
    std::fs::write(remote.join("small").join("needle.txt"), b"").unwrap();

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.find("connected", |event| {
        event["session"] == 1 && event["state"] == "connected"
    });
    let find = |request: u64, path: &str| -> Value {
        fixture.ok(json!({ "op": "sftp.find", "session": 1, "request": request, "path": path, "name": "needle" }));
        fixture.recorder.find("the answer to the search", |event| {
            event["event"] == "sftp.result" && event["request"] == request
        })
    };

    // Abgeschnitten: was bis dahin gefunden war, kommt – samt der Zahl, nach der Schluss war.
    let cut = find(1, "/big");
    assert_eq!(cut["ok"], true, "{cut}");
    assert_eq!(cut["seen"], 50, "{cut}");
    let hits: Vec<&str> = cut["tree"]
        .as_array()
        .unwrap()
        .iter()
        .map(|hit| hit["remote"].as_str().unwrap())
        .collect();
    assert!(!hits.contains(&"/big/deep/needle-far.txt"), "{cut}");

    // Vollständig: keine Zahl.
    let whole = find(2, "/small");
    assert_eq!(whole["tree"].as_array().unwrap().len(), 1, "{whole}");
    assert!(whole.get("seen").is_none(), "{whole}");
}
