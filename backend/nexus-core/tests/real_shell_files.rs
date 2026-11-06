//! Dateizugriff über Befehle (ohne SFTP) an einem echten Server. Eine eigene Testdatei, also ein eigener Prozess:
//! Der Test stellt SFTP für den ganzen Kern ab und gälte sonst auch für die Tests daneben.

mod common;

use common::Fixture;
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::Options;
use serde_json::{Value, json};

fn variable(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

/// Ein echter Server, wenn die Umgebung einen nennt (dieselben Angaben wie für die Tests in `sftp.rs`:
/// `NEXUS_REAL_HOST`, `NEXUS_REAL_USER`, `NEXUS_REAL_FINGERPRINT`, `NEXUS_REAL_DIR`, dazu `NEXUS_REAL_KEY` oder
/// `NEXUS_REAL_PASSWORD` und nach Bedarf `NEXUS_REAL_PORT`) – sonst läuft der Test leer durch. Der Kern benutzt
/// hier kein SFTP, obwohl der Server es anböte: Listen, Lesen, Schreiben, Umbenennen und Löschen laufen über
/// `ls`, `cat`, `dd`, `mv` und `rm` des Servers – GNU, BusyBox oder die der BSDs.
#[test]
fn files_work_over_commands_on_a_real_server() {
    let (Some(host), Some(user), Some(fingerprint), Some(directory)) = (
        variable("NEXUS_REAL_HOST"),
        variable("NEXUS_REAL_USER"),
        variable("NEXUS_REAL_FINGERPRINT"),
        variable("NEXUS_REAL_DIR"),
    ) else {
        return;
    };
    let port: u16 = variable("NEXUS_REAL_PORT").map_or(22, |port| port.parse().unwrap());
    nexus_core::set_no_sftp_for_tests(true);

    let fixture = Fixture::new(Options::default());
    let profile =
        fixture.profile(json!({ "host": host, "port": port, "user": user, "key_path": variable("NEXUS_REAL_KEY") }));
    if let Some(password) = variable("NEXUS_REAL_PASSWORD") {
        fixture.secrets.set(&password_key(&profile), &user, &password).unwrap();
    }

    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["fingerprint"], fingerprint.as_str(), "{prompt}");
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");

    // Der Kern sagt, dass er hier über Befehle arbeitet.
    let warning = fixture
        .recorder
        .find("the warning about files over the shell", |event| {
            event["event"] == "core.warning" && event["code"] == "files_over_shell"
        });
    assert_eq!(warning["code"], "files_over_shell");

    let next = std::cell::Cell::new(1u64);
    let sftp = |mut request: Value| -> Value {
        let number = next.get();
        next.set(number + 1);
        request["session"] = json!(1);
        request["request"] = json!(number);
        fixture.ok(request);
        fixture.recorder.find("sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == number
        })
    };
    let done = |request: Value| {
        let result = sftp(request.clone());
        assert_eq!(result["ok"], true, "{request} -> {result}");
    };
    let entries = |path: &str| -> Vec<Value> {
        let result = sftp(json!({ "op": "sftp.list", "path": path }));
        assert_eq!(result["ok"], true, "{path} -> {result}");
        let mut entries = result["entries"].as_array().unwrap().clone();
        entries.sort_by(|left, right| left["name"].as_str().cmp(&right["name"].as_str()));
        entries
    };
    let names = |path: &str| -> Vec<String> {
        entries(path)
            .iter()
            .map(|entry| entry["name"].as_str().unwrap().to_owned())
            .collect()
    };
    let at = |name: &str| format!("{directory}/{name}");

    assert_eq!(names(&directory), Vec::<String>::new(), "the folder must be empty");

    // Anlegen und Schreiben – mit Namen, an denen eine Befehlszeile gern scheitert.
    done(json!({ "op": "sftp.mkdir", "path": at("sub dir") }));
    done(json!({ "op": "sftp.write", "path": at("it's a file.txt"), "data": "aGVsbG8gd29ybGQ=" }));
    done(json!({ "op": "sftp.write", "path": at("sub dir/-dash"), "data": "AAH/Zm9vCg==" }));
    done(json!({ "op": "sftp.touch", "path": at(".hidden") }));

    // Die Liste: Namen, Art und Größe, wie `ls -l` des Servers sie nennt.
    let listed = entries(&directory);
    println!("listing: {}", Value::Array(listed.clone()));
    assert_eq!(
        listed
            .iter()
            .map(|entry| (
                entry["name"].as_str().unwrap(),
                entry["kind"].as_str().unwrap(),
                entry["size"].as_u64()
            ))
            .filter(|entry| entry.1 == "file")
            .collect::<Vec<_>>(),
        [(".hidden", "file", Some(0)), ("it's a file.txt", "file", Some(11))]
    );
    assert!(
        listed
            .iter()
            .any(|entry| entry["name"] == "sub dir" && entry["kind"] == "directory"),
        "{listed:?}"
    );
    assert!(
        listed
            .iter()
            .all(|entry| entry["modified"].as_u64().is_some_and(|time| time > 1_600_000_000))
    );

    // Ein Stück lesen, auch Bytes, die kein Text sind.
    let part = sftp(json!({ "op": "sftp.read", "path": at("it's a file.txt"), "offset": 6, "length": 5 }));
    assert_eq!(
        (&part["data"], &part["size"]),
        (&json!("d29ybGQ="), &json!(11)),
        "{part}"
    );
    let binary = sftp(json!({ "op": "sftp.read", "path": at("sub dir/-dash"), "offset": 0, "length": 100 }));
    assert_eq!(binary["data"], "AAH/Zm9vCg==", "{binary}");

    // Überschreiben macht kürzer, Umbenennen und Löschen wirken.
    done(json!({ "op": "sftp.write", "path": at("it's a file.txt"), "data": "aGk=" }));
    let short = sftp(json!({ "op": "sftp.read", "path": at("it's a file.txt"), "offset": 0, "length": 100 }));
    assert_eq!((&short["data"], &short["size"]), (&json!("aGk="), &json!(2)), "{short}");
    done(json!({ "op": "sftp.rename", "from": at("it's a file.txt"), "to": at("sub dir/moved it") }));
    assert_eq!(names(&at("sub dir")), ["-dash", "moved it"]);

    // Was es nicht gibt, ist ein Fehler mit seinem Namen – kein leeres Ergebnis.
    let missing = sftp(json!({ "op": "sftp.list", "path": at("nichts") }));
    assert_eq!(
        (&missing["ok"], &missing["error"]),
        (&json!(false), &json!("not_found")),
        "{missing}"
    );

    done(json!({ "op": "sftp.delete", "path": at("sub dir") }));
    done(json!({ "op": "sftp.delete", "path": at(".hidden") }));
    assert_eq!(names(&directory), Vec::<String>::new());
}
