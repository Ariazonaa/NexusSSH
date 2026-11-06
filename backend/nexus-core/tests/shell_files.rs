//! Dateien ohne SFTP: Der Server bietet kein SFTP an, führt aber Befehle aus – dann arbeitet der Dateibrowser über
//! `ls`, `tail`, `cat`, `mv` und Verwandte. Der Testserver führt die Befehle dafür wirklich aus (`sh`); gibt es
//! auf dem Rechner keines, laufen diese Tests nicht.

mod common;

use std::path::PathBuf;

use common::{Fixture, with_password};
use nexus_core::secrets::{SecretStore, password_key};
use nexus_testserver::Options;
use serde_json::{Value, json};

struct Shell {
    fixture: Fixture,
    /// Das Verzeichnis, in dem die Befehle laufen – auf diesem Rechner.
    local: PathBuf,
    /// Dasselbe Verzeichnis, wie die Shell des Servers es nennt (unter Windows `/c/…`).
    home: String,
    next: std::cell::Cell<u64>,
}

impl Shell {
    fn connected() -> Option<Self> {
        if !nexus_testserver::has_shell() {
            eprintln!("no sh on this machine – skipped");
            return None;
        }

        let fixture = Fixture::with(|directory| Options {
            sftp_root: None,
            real_shell: Some(directory.join("home")),
            command_log: Some(directory.join("commands.log")),
            ..with_password()
        });
        let local = fixture.directory.join("home");
        std::fs::create_dir_all(local.join("www")).unwrap();
        std::fs::write(local.join("readme.txt"), "hello").unwrap();
        std::fs::write(local.join("www").join("index.html"), "<h1>hi</h1>").unwrap();

        let profile = fixture.profile(json!({}));
        fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
        fixture.open(1, &profile);
        fixture.trust_host_key();
        fixture.recorder.state("authenticating");
        let connected = fixture.recorder.state("connected");
        let home = connected["home"].as_str().expect("files without SFTP").to_owned();
        Some(Self {
            fixture,
            local,
            home,
            next: std::cell::Cell::new(1),
        })
    }

    fn sftp(&self, mut request: Value) -> Value {
        let number = self.next.get();
        self.next.set(number + 1);
        request["session"] = json!(1);
        request["request"] = json!(number);
        self.fixture.ok(request);
        self.fixture.recorder.find("sftp.result", |event| {
            event["event"] == "sftp.result" && event["request"] == number
        })
    }

    fn done(&self, request: Value) {
        let result = self.sftp(request.clone());
        assert_eq!(result["ok"], true, "{request} -> {result}");
    }

    fn path(&self, name: &str) -> String {
        format!("{}/{name}", self.home.trim_end_matches('/'))
    }

    fn entries(&self, name: &str) -> Vec<Value> {
        let result = self.sftp(json!({ "op": "sftp.list", "path": self.path(name) }));
        assert_eq!(result["ok"], true, "{result}");
        result["entries"].as_array().unwrap().clone()
    }

    fn names(&self, name: &str) -> Vec<String> {
        self.entries(name)
            .iter()
            .map(|entry| entry["name"].as_str().unwrap().to_owned())
            .collect()
    }
}

#[test]
fn without_sftp_the_file_browser_works_through_commands() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    // Die App erfährt, dass es der langsamere Weg ist.
    let warning = shell
        .fixture
        .recorder
        .find("the hint", |event| event["event"] == "core.warning");
    assert_eq!(warning["code"], "files_over_shell", "{warning}");

    // Auflisten: Ordner und Dateien mit Art und Größe.
    let listed = shell.entries("");
    let readme = listed
        .iter()
        .find(|entry| entry["name"] == "readme.txt")
        .expect("readme.txt");
    assert_eq!(
        (&readme["kind"], &readme["size"]),
        (&json!("file"), &json!(5)),
        "{readme}"
    );
    let www = listed.iter().find(|entry| entry["name"] == "www").expect("www");
    assert_eq!(www["kind"], "directory", "{www}");
    assert_eq!(shell.names("www"), ["index.html"]);

    // Anlegen, umbenennen, löschen – auch mit Leerzeichen und Apostroph im Namen.
    shell.done(json!({ "op": "sftp.mkdir", "path": shell.path("my folder") }));
    shell.done(json!({ "op": "sftp.touch", "path": shell.path("my folder/it's new.txt") }));
    assert_eq!(shell.names("my folder"), ["it's new.txt"]);
    shell.done(json!({ "op": "sftp.rename", "from": shell.path("my folder/it's new.txt"), "to": shell.path("my folder/renamed.txt") }));
    assert!(shell.local.join("my folder").join("renamed.txt").is_file());

    // Umbenennen auf einen Namen, den es schon gibt, scheitert wie bei SFTP – nichts wird überschrieben.
    let taken = shell.sftp(
        json!({ "op": "sftp.rename", "from": shell.path("readme.txt"), "to": shell.path("my folder/renamed.txt") }),
    );
    assert_eq!(taken["ok"], false, "{taken}");
    assert_eq!(
        std::fs::read_to_string(shell.local.join("readme.txt")).unwrap(),
        "hello"
    );

    shell.done(json!({ "op": "sftp.delete", "path": shell.path("my folder") }));
    assert!(!shell.local.join("my folder").exists());

    let missing = shell.sftp(json!({ "op": "sftp.list", "path": shell.path("nothing here") }));
    assert_eq!(missing["error"], "not_found", "{missing}");
}

/// Was der Kern dem Server bisher an Befehlen geschickt hat, eine Zeile je Befehl.
#[cfg(unix)]
fn commands_of(shell: &Shell) -> String {
    std::fs::read_to_string(shell.fixture.directory.join("commands.log")).unwrap_or_default()
}

#[cfg(unix)]
#[test]
fn without_sftp_a_listing_costs_no_command_for_the_target_of_a_link_but_the_target_can_be_asked_for() {
    let Some(shell) = Shell::connected() else {
        return;
    };
    std::os::unix::fs::symlink("www", shell.local.join("ordner_link")).unwrap();
    std::os::unix::fs::symlink("readme.txt", shell.local.join("datei_link")).unwrap();
    std::os::unix::fs::symlink("gibt-es-nicht", shell.local.join("tot")).unwrap();

    let listed = shell.entries("");
    let by_name = |name: &str| {
        listed
            .iter()
            .find(|entry| entry["name"] == name)
            .unwrap_or_else(|| panic!("{name} fehlt"))
    };

    // Ein Link zeigt, was hinter ihm steckt – ein Link auf einen Ordner lässt sich betreten …
    assert_eq!(
        (&by_name("ordner_link")["kind"], &by_name("ordner_link")["link"]),
        (&json!("directory"), &json!(true))
    );
    assert_eq!(by_name("datei_link")["kind"], "file");
    assert_eq!(by_name("tot")["kind"], "other");

    // … aber sein Ziel und ob er ins Leere zeigt, stehen in der Liste nicht: Das kostete je Link einen Befehl, und
    // die Befehle laufen hier einer nach dem anderen.
    for name in ["ordner_link", "datei_link", "tot"] {
        let entry = by_name(name);
        assert!(
            entry.get("target").is_none() && entry.get("dangling").is_none(),
            "{entry}"
        );
    }
    let commands = commands_of(&shell);
    assert!(!commands.contains("readlink"), "{commands}");

    // Auf Nachfrage geht es: Dann ist es ein Befehl.
    let asked = shell.sftp(json!({ "op": "sftp.readlink", "path": shell.path("ordner_link") }));
    assert_eq!(
        (&asked["ok"], &asked["target"]),
        (&json!(true), &json!("www")),
        "{asked}"
    );
    assert!(commands_of(&shell).contains("readlink"));
}

#[test]
fn without_sftp_file_contents_are_read_in_parts_and_written() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    // Lesen in Stücken, wie der Betrachter es tut: Anfang, Mitte, über das Ende hinaus.
    let start = shell.sftp(json!({ "op": "sftp.read", "path": shell.path("www/index.html"), "length": 4 }));
    assert_eq!(
        (&start["data"], &start["size"]),
        (&json!("PGgxPg=="), &json!(11)),
        "{start}"
    );
    let middle =
        shell.sftp(json!({ "op": "sftp.read", "path": shell.path("www/index.html"), "offset": 4, "length": 2 }));
    assert_eq!(middle["data"], "aGk=", "{middle}");
    let beyond =
        shell.sftp(json!({ "op": "sftp.read", "path": shell.path("www/index.html"), "offset": 500, "length": 10 }));
    assert_eq!((&beyond["ok"], &beyond["data"]), (&json!(true), &json!("")), "{beyond}");

    // Schreiben: neu anlegen, dann durch Kürzeres ersetzen – Bytes bleiben, wie sie sind (auch Nullbytes).
    shell.done(json!({ "op": "sftp.write", "path": shell.path("new.bin"), "data": "AAH/Zm9vCg==" }));
    assert_eq!(
        std::fs::read(shell.local.join("new.bin")).unwrap(),
        [0, 1, 255, b'f', b'o', b'o', b'\n']
    );
    shell.done(json!({ "op": "sftp.write", "path": shell.path("new.bin"), "data": "aGk=" }));
    assert_eq!(std::fs::read(shell.local.join("new.bin")).unwrap(), b"hi");
}

#[test]
fn without_sftp_whole_files_and_folders_are_transferred() {
    let Some(shell) = Shell::connected() else {
        return;
    };
    let here = shell.fixture.directory.join("local");
    std::fs::create_dir_all(here.join("up").join("sub")).unwrap();

    // Größer als ein Stück, mit allen Bytewerten – so zeigt sich, ob Stücke richtig aneinandergehängt werden.
    let content: Vec<u8> = (0..700_000u32).map(|index| (index % 251) as u8).collect();
    std::fs::write(here.join("up").join("big.bin"), &content).unwrap();
    std::fs::write(here.join("up").join("sub").join("small.txt"), "klein").unwrap();

    let finished = |transfer: u64| {
        shell.fixture.recorder.find("the end of the transfer", |event| {
            event["event"] == "transfer.state"
                && event["transfer"] == transfer
                && matches!(event["state"].as_str(), Some("completed" | "failed" | "cancelled"))
        })
    };

    shell.fixture.ok(json!({
        "op": "transfer.start", "transfer": 1, "session": 1, "direction": "upload",
        "local": here.join("up").display().to_string(), "remote": shell.home,
    }));
    let up = finished(1);
    assert_eq!(up["state"], "completed", "{up}");
    assert_eq!(std::fs::read(shell.local.join("up").join("big.bin")).unwrap(), content);
    assert_eq!(
        std::fs::read_to_string(shell.local.join("up").join("sub").join("small.txt")).unwrap(),
        "klein"
    );

    shell.fixture.ok(json!({
        "op": "transfer.start", "transfer": 2, "session": 1, "direction": "download",
        "local": here.join("down").display().to_string(), "remote": shell.path("up"),
    }));
    std::fs::create_dir_all(here.join("down")).unwrap();
    let down = finished(2);
    assert_eq!(down["state"], "completed", "{down}");
    assert_eq!(
        std::fs::read(here.join("down").join("up").join("big.bin")).unwrap(),
        content
    );
}

#[test]
fn the_last_lines_of_a_log_file_come_oldest_first_and_only_from_a_plain_file() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    // Ein Name, der in einer Shell ein Befehl wäre, wenn er nicht in Anführungszeichen stünde.
    let name = "app's & $(touch injected).log";
    let lines: String = (1..=8).map(|number| format!("line {number}\n")).collect();
    std::fs::write(
        shell.local.join(name),
        format!("{lines}colour \u{1b}[31mred\u{1b}[0m\n"),
    )
    .unwrap();

    let result = shell.sftp(json!({ "op": "logs.read", "path": shell.path(name), "lines": 3 }));

    assert_eq!(result["ok"], true, "{result}");
    assert_eq!(
        result["logs"],
        json!([{ "message": "line 7" }, { "message": "line 8" }, { "message": "colour [31mred[0m" }]),
        "the last three, oldest first, without control characters"
    );
    assert!(result.get("limited").is_none());
    assert!(!shell.local.join("injected").exists());

    // Ein Verzeichnis und etwas, das es nicht gibt, sind keine Datei zum Lesen.
    for path in [shell.path("www"), shell.path("missing.log")] {
        let result = shell.sftp(json!({ "op": "logs.read", "path": path }));
        assert_eq!(result["ok"], false, "{result}");
        assert_eq!(result["error"], "command_failed");
    }

    // Was kein Pfad, keine Einheit oder keine Schwere ist, wird abgelehnt, bevor ein Befehl entsteht.
    for request in [
        json!({ "op": "logs.read", "path": "relative.log" }),
        json!({ "op": "logs.read", "unit": "x; touch injected" }),
        json!({ "op": "logs.read", "unit": "$(touch injected)" }),
        json!({ "op": "logs.read", "priority": 9 }),
    ] {
        let mut request = request;
        request["session"] = json!(1);
        request["request"] = json!(900);
        let response = shell.fixture.request(request.clone());
        assert_eq!(response["ok"], false, "{request} -> {response}");
    }

    assert!(!shell.local.join("injected").exists());
}

#[test]
fn a_server_without_journalctl_says_so() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    // Auf dem Rechner, auf dem die Tests laufen, kann es ein Journal geben (Linux) – dann ist die Antwort eine Liste.
    let result = shell.sftp(json!({ "op": "logs.read", "since": "1h", "priority": 4, "lines": 5 }));
    if result["ok"] == true {
        assert!(result["logs"].is_array(), "{result}");
    } else {
        assert_eq!(result["error"], "no_journal", "{result}");
    }
}

/// `exec.run` (MCP): Text der App läuft als ein Befehl, und die Antwort nennt beide Ströme und den Rückgabewert –
/// auch wenn er scheitert, denn genau das will wissen, wer einen Fehler sucht.
#[test]
fn a_command_of_the_app_reports_both_streams_and_its_status() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    let result = shell.sftp(json!({
        "op": "exec.run",
        "command": r"echo 'it'\''s out'; echo err >&2; echo !; exit 3",
        "seconds": 20,
    }));

    assert_eq!(result["ok"], true, "{result}");
    assert_eq!(result["run"]["status"], 3, "{result}");
    assert_eq!(result["run"]["stdout"], "it's out\n!\n", "{result}");
    assert_eq!(result["run"]["stderr"], "err\n", "{result}");
    assert_eq!(result["run"]["timed_out"], false, "{result}");
    assert_eq!(result["run"]["cut"], false, "{result}");
}

/// Überschreitet ein Befehl seine Frist, bleibt, was er bis dahin geschrieben hat, und die Antwort sagt es.
#[test]
fn a_command_past_its_time_keeps_what_it_printed() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    let started = std::time::Instant::now();
    let result = shell.sftp(json!({ "op": "exec.run", "command": "echo early; sleep 12", "seconds": 2 }));

    assert_eq!(result["ok"], true, "{result}");
    assert_eq!(result["run"]["timed_out"], true, "{result}");
    assert_eq!(result["run"]["stdout"], "early\n", "{result}");
    assert!(
        started.elapsed() < std::time::Duration::from_secs(10),
        "{:?}",
        started.elapsed()
    );
}

/// Mehr Ausgabe als die Grenze kommt nicht hinüber, und die Antwort sagt, dass etwas fehlt.
#[test]
fn a_command_with_too_much_output_is_cut_and_says_so() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    let result = shell.sftp(json!({
        "op": "exec.run",
        "command": r"head -c 400000 /dev/zero | tr '\0' x; echo ende",
        "seconds": 30,
    }));

    assert_eq!(result["ok"], true, "{result}");
    assert_eq!(result["run"]["cut"], true);

    // Anfang und Ende bleiben: Beim Suchen eines Fehlers steht das Wichtige am Schluss.
    let stdout = result["run"]["stdout"].as_str().unwrap();
    assert!(stdout.starts_with("xxx"), "{}", &stdout[..20]);
    assert!(stdout.ends_with("ende\n"), "{}", &stdout[stdout.len() - 20..]);
    assert!(stdout.contains("bytes left out"));
    assert!(stdout.len() < 49 * 1024, "{}", stdout.len());
}

/// Ein leerer Befehl und einer mit einem Nullzeichen gehen gar nicht erst hinaus.
#[test]
fn a_command_that_cannot_be_one_is_refused() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    for command in ["", "  \n ", "echo a\0b"] {
        let result = shell.sftp(json!({ "op": "exec.run", "command": command, "seconds": 5 }));
        assert_eq!(
            (&result["ok"], &result["error"]),
            (&json!(false), &json!("invalid_command")),
            "{result}"
        );
    }

    let long = "x".repeat(17 * 1024);
    let result = shell.sftp(json!({ "op": "exec.run", "command": long, "seconds": 5 }));
    assert_eq!(
        (&result["ok"], &result["error"]),
        (&json!(false), &json!("invalid_command")),
        "{result}"
    );
}

/// Ein Befehl bekommt keine Eingabe: Wer sie liest, endet gleich, statt bis zur Frist zu warten.
#[test]
fn a_command_that_reads_its_input_ends_at_once() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    let result = shell.sftp(json!({ "op": "exec.run", "command": "cat; echo done", "seconds": 20 }));

    assert_eq!(result["run"]["stdout"], "done\n", "{result}");
    assert_eq!(result["run"]["timed_out"], false, "{result}");
}

/// Die Anmelde-Shell nennt ein fester Befehl: Daran hängt, ob ein Befehl der App in die Shell getippt wird.
#[test]
fn the_login_shell_is_named_by_a_fixed_command() {
    let Some(shell) = Shell::connected() else {
        return;
    };

    let result = shell.sftp(json!({ "op": "commands.shell" }));

    assert_eq!(result["ok"], true, "{result}");
    assert!(result["line"].is_string(), "{result}");
}
