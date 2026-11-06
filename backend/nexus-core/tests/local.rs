//! Das lokale Terminal: eine Shell dieses Rechners als Sitzung – öffnen, tippen, Größe, Ende mit Rückgabewert.
//!
//! Gestartet wird unter Windows `cmd`, unter Unix `/bin/sh`: Beide schreiben beim Beenden nichts in den Ordner
//! des Benutzers (keine Befehlsgeschichte), anders als etwa bash als Anmelde-Shell.

mod common;

use common::Fixture;
use nexus_testserver::Options;
use serde_json::{Value, json};

/// Die Shell der Tests.
const SHELL: &str = if cfg!(windows) { "cmd" } else { "/bin/sh" };

/// Ein Befehl, dessen Ausgabe „4711“ enthält, ohne dass das Echo es schon zeigt – und das Zeilenende.
const ASK: &[u8] = if cfg!(windows) {
    b"echo 47^11\r"
} else {
    b"echo $((4700 + 11))\n"
};

/// Der Befehl, der das Arbeitsverzeichnis nennt.
const WHERE: &[u8] = if cfg!(windows) { b"cd\r" } else { b"pwd\n" };

fn exit(code: u8) -> Vec<u8> {
    format!("exit {code}{}", if cfg!(windows) { "\r" } else { "\n" }).into_bytes()
}

fn local(fixture: &Fixture, extra: Value) -> String {
    let mut profile = json!({ "name": "lokal", "protocol": "local", "host": SHELL });
    for (key, value) in extra.as_object().unwrap() {
        profile[key] = value.clone();
    }

    fixture.profile(profile)
}

/// Der Ordner, wie die Shell ihn nennt (unter Unix ohne Verweise darin).
fn shown(path: &std::path::Path) -> String {
    if cfg!(windows) {
        path.display().to_string()
    } else {
        path.canonicalize().unwrap().display().to_string()
    }
}

#[test]
fn a_local_terminal_opens_answers_and_closes_with_the_exit_code_of_its_shell() {
    let fixture = Fixture::new(Options::default());
    let folder = fixture.directory.join("start");
    std::fs::create_dir_all(&folder).unwrap();
    let id = local(&fixture, json!({ "local_dir": folder.display().to_string() }));

    let saved = &fixture.ok(json!({ "op": "profiles.list" }))["profiles"][0];
    assert_eq!(
        (saved["protocol"].as_str(), saved["user"].as_str()),
        (Some("local"), Some(""))
    );

    fixture.open(1, &id);
    fixture.recorder.state("connected");

    assert!(fixture.core.write(1, ASK));
    fixture.recorder.output(1, "4711");
    assert!(fixture.core.write(1, WHERE));
    fixture.recorder.output(1, &shown(&folder));

    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 120, "rows": 40 }));
    fixture.ok(json!({ "op": "session.flow", "session": 1, "paused": true }));
    fixture.ok(json!({ "op": "session.flow", "session": 1, "paused": false }));

    // Kein Server, nichts zu messen – das sagt die Sitzung, statt zu schweigen.
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    let silent = fixture.recorder.event("monitor.unavailable");
    assert!(
        silent["message"].as_str().unwrap().contains("local terminal"),
        "{silent}"
    );
    assert_ne!(silent["retrying"], true);

    assert!(fixture.core.write(1, &exit(3)));
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "remote", "{closed}");
    assert_eq!(closed["exit_status"], 3, "{closed}");
}

#[test]
fn closing_the_tab_ends_the_shell() {
    let fixture = Fixture::new(Options::default());
    let id = local(&fixture, json!({}));
    fixture.open(1, &id);
    fixture.recorder.state("connected");
    assert!(fixture.core.write(1, ASK));
    fixture.recorder.output(1, "4711");

    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "user", "{closed}");
}

#[test]
fn an_unknown_or_vanished_shell_is_a_failed_connection() {
    let fixture = Fixture::new(Options::default());

    // Eine Distribution, die es (nicht mehr) gibt: Das Profil ist in Ordnung, nur die Shell fehlt hier.
    let id = local(&fixture, json!({ "host": "wsl:gibtsnicht" }));
    fixture.open(1, &id);
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connect_failed", "{closed}");
    assert!(
        closed["message"].as_str().unwrap().contains("not available"),
        "{closed}"
    );

    // Was keine Kennung einer Shell ist, nimmt der Kern gar nicht erst an – auch keinen Pfad zu einem Programm.
    for host in ["evil", "C:\\Windows\\System32\\calc.exe", "cmd /c calc", "wsl:a b"] {
        let response = fixture.request(json!({
            "op": "profiles.save",
            "profile": { "name": "x", "protocol": "local", "host": host }
        }));
        assert_eq!(response["error"], "invalid_profile", "{host}: {response}");
        assert_eq!(response["message"], "host", "{host}: {response}");
    }
}

#[test]
fn a_missing_start_folder_falls_back_to_home_and_says_so() {
    let fixture = Fixture::new(Options::default());
    let gone = fixture.directory.join("gibt-es-nicht");
    let id = local(&fixture, json!({ "local_dir": gone.display().to_string() }));

    fixture.open(1, &id);
    fixture.recorder.state("connected");
    let warning = fixture.recorder.find("local_dir_missing", |event| {
        event["event"] == "core.warning" && event["code"] == "local_dir_missing"
    });
    assert!(warning["message"].as_str().unwrap().contains("lokal"), "{warning}");

    let home = std::env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" }).unwrap();
    assert!(fixture.core.write(1, WHERE));
    fixture.recorder.output(1, &shown(std::path::Path::new(&home)));
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
}

#[test]
fn a_local_terminal_has_no_network_path_to_test() {
    let fixture = Fixture::new(Options::default());
    fixture.ok(json!({
        "op": "probe",
        "request": 5,
        "profile": { "name": "x", "protocol": "local", "host": SHELL }
    }));
    let result = fixture.recorder.event("probe.result");
    assert_eq!(result["request"], 5);
    assert!(
        result["message"].as_str().unwrap().contains("local terminal"),
        "{result}"
    );
}

#[test]
fn the_shells_of_this_computer_are_listed() {
    let fixture = Fixture::new(Options::default());
    let shells = fixture.ok(json!({ "op": "local.shells" }))["shells"].clone();
    let shells = shells.as_array().unwrap();
    let usual = if cfg!(windows) { "cmd" } else { "login" };
    assert!(shells.iter().any(|shell| shell["id"] == usual), "{shells:?}");

    // Nur Kennung und Name – Pfade und Argumente kennt allein der Kern.
    for shell in shells {
        let keys: Vec<&String> = shell.as_object().unwrap().keys().collect();
        assert_eq!(keys, ["id", "name"], "{shell}");
    }
}
