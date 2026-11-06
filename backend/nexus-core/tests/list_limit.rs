//! Die Obergrenze für die Einträge einer Verzeichnisliste. Eine eigene Testdatei, also ein eigener Prozess: Die
//! Grenze ist für den Test herabgesetzt und gälte sonst auch für die Tests daneben.

mod common;

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::Options;
use serde_json::{Value, json};

#[test]
fn a_directory_that_lists_more_than_the_limit_is_given_up_and_the_session_stays_usable() {
    nexus_core::set_list_limit_for_tests(500);

    let fixture = Fixture::with(|directory| Options {
        sftp_root: Some(directory.join("remote")),
        ..with_password()
    });
    let remote = fixture.directory.join("remote");
    std::fs::create_dir_all(remote.join("endless")).unwrap();
    std::fs::create_dir_all(remote.join("small")).unwrap();
    for number in 0..3000 {
        std::fs::write(remote.join("endless").join(format!("file-{number}")), b"").unwrap();
    }
    // Klein genug, dass auch die eine Antwort der aufgegebenen Liste, die noch unterwegs ist (200 Einträge),
    // zusammen mit dieser unter der Grenze bleibt – der Zähler gilt für die Sitzung, nicht für die Anfrage.
    for number in 0..100 {
        std::fs::write(remote.join("small").join(format!("file-{number}")), b"").unwrap();
    }

    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.find("connected", |event| {
        event["session"] == 1 && event["state"] == "connected"
    });

    let list = |request: u64, path: &str| -> Value {
        fixture.ok(json!({ "op": "sftp.list", "session": 1, "request": request, "path": path }));
        fixture.recorder.find("the answer to the listing", |event| {
            event["event"] == "sftp.result" && event["request"] == request
        })
    };

    let refused = list(1, "/endless");
    assert_eq!(refused["ok"], false, "{refused}");
    assert_eq!(refused["error"], "too_many", "{refused}");

    // Die Sitzung trägt weiter: Antworten der aufgegebenen Liste, die noch eintreffen, stören die nächste nicht.
    let listed = list(2, "/small");
    assert_eq!(listed["ok"], true, "{listed}");
    assert_eq!(listed["entries"].as_array().unwrap().len(), 100);

    let again = list(3, "/endless");
    assert_eq!(again["error"], "too_many", "{again}");
}
