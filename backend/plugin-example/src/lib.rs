//! Beispiel-Plugin: zeigt die Plattenbelegung des Servers und warnt, wenn in der Ausgabe „No space left on device“
//! steht. Es nutzt alles, was ein Plugin kann:
//!
//! - einen Befehl in der Palette („Show disk usage“),
//! - einen Befehl am Server (`df -hP`, läuft erst nach Freigabe – Recht `commands`),
//! - eine Datei der Sitzung (`/etc/os-release` für den Namen des Systems – Recht `files`),
//! - ein Muster auf der Ausgabe (Recht `watch`) mit einem Hinweis (Recht `notify`),
//! - eigene Daten (wie oft eine volle Platte gemeldet wurde).
//!
//! Bauen: `cargo build -p nexus-plugin-example --release --target wasm32-unknown-unknown`, dann
//! `target/wasm32-unknown-unknown/release/nexus_plugin_example.wasm` als `disk-usage.nxplugin` speichern.

use nexus_plugin::serde_json::json;
use nexus_plugin::{Input, Kind, Output, View};

nexus_plugin::plugin!(MANIFEST, handle);

/// Die Beschreibung: Kennung, Name, Rechte, Befehle, Muster. Steht als Abschnitt `nexus-plugin` im Modul.
pub const MANIFEST: &str = r#"{
  "format": 1,
  "id": "nexusssh.example.disk-usage",
  "name": "Disk usage",
  "version": "1.0.0",
  "author": "NexusSSH",
  "description": "Shows the disk usage of the server and warns when a disk is full.",
  "permissions": ["notify", "commands", "files", "watch"],
  "commands": [{ "id": "usage", "title": "Show disk usage" }],
  "watch": [{ "id": "full", "pattern": "No space left on device", "ignoreCase": true }]
}"#;

/// Ein Aufruf: Befehl aus der Palette, das Ergebnis eines Schritts oder passende Zeilen.
pub fn handle(input: &Input) -> Output {
    match input.kind() {
        Kind::Command => Output::new().run("df -hP", 30).state(json!({ "step": "df" })),
        Kind::Result if input.state()["step"] == "df" => after_df(input),
        Kind::Result if input.state()["step"] == "os" => show(input),
        Kind::Watch => warn(input),
        _ => Output::new(),
    }
}

// df lief (oder nicht): Zeilen merken, dann den Namen des Systems lesen.
fn after_df(input: &Input) -> Output {
    let outcome = input.outcome();
    if let Some(error) = outcome.error() {
        let text = match error {
            "declined" => text(input, "The command was declined.", "Der Befehl wurde abgelehnt."),
            "permission" => text(
                input,
                "The plugin may not run commands.",
                "Das Plugin darf keine Befehle ausführen.",
            ),
            _ => text(input, "df could not run.", "df ließ sich nicht ausführen."),
        };
        return Output::new().view(View::new().title(&title(input)).text(&format!("{text} ({error})")));
    }

    Output::new()
        .read("/etc/os-release", 0)
        .state(json!({ "step": "os", "rows": disks(outcome.stdout()) }))
}

// Das Ergebnis: Tabelle der Platten, Überschrift mit dem Namen des Systems (wenn es ihn lesen durfte).
fn show(input: &Input) -> Output {
    let system = input.outcome().text().and_then(pretty_name);
    let rows: Vec<Vec<String>> = input.state()["rows"]
        .as_array()
        .into_iter()
        .flatten()
        .map(|row| {
            row.as_array()
                .into_iter()
                .flatten()
                .map(|cell| cell.as_str().unwrap_or_default().to_owned())
                .collect()
        })
        .collect();
    let mut view = View::new()
        .title(&title(input))
        .table(&["Filesystem", "Size", "Used", "Free", "Use%", "Mounted on"], &rows);
    if let Some(system) = system {
        view = view.text(&system);
    }

    Output::new().view(view)
}

// Eine volle Platte: Hinweis und Eintrag, gezählt in den eigenen Daten.
fn warn(input: &Input) -> Output {
    let Some(line) = input.lines().next() else {
        return Output::new();
    };

    let count = input.storage()["full"].as_u64().unwrap_or(0) + input.lines().count() as u64;
    let notice = format!(
        "{}: {}",
        line.server,
        text(input, "a disk is full", "eine Platte ist voll")
    );
    Output::new()
        .notify(&notice)
        .log(&format!("{notice} – {}", line.text))
        .storage(json!({ "full": count }))
}

/// Die Zeilen von `df -hP` als Tabelle: Kopfzeile weg, der Einhängepunkt darf Leerzeichen enthalten.
pub fn disks(output: &str) -> Vec<Vec<String>> {
    output
        .lines()
        .skip(1)
        .filter_map(|line| {
            let parts: Vec<&str> = line.split_whitespace().collect();
            (parts.len() >= 6).then(|| {
                let mut row: Vec<String> = parts[..5].iter().map(|part| (*part).to_owned()).collect();
                row.push(parts[5..].join(" "));
                row
            })
        })
        .collect()
}

/// `PRETTY_NAME` aus `/etc/os-release`.
pub fn pretty_name(text: &str) -> Option<String> {
    text.lines()
        .find_map(|line| line.strip_prefix("PRETTY_NAME="))
        .map(|value| value.trim().trim_matches('"').to_owned())
        .filter(|value| !value.is_empty())
}

fn title(input: &Input) -> String {
    let what = text(input, "Disk usage", "Plattenbelegung");
    input
        .server()
        .map_or_else(|| what.to_owned(), |server| format!("{what} – {server}"))
}

fn text<'a>(input: &Input, english: &'a str, german: &'a str) -> &'a str {
    if input.language() == "de" { german } else { english }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_plugin::serde_json::Value;

    fn input(value: Value) -> Input {
        Input::parse(value.to_string().as_bytes())
    }

    const DF: &str = "Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1        20G   12G  7.0G  63% /\n/dev/sdb1       100G   99G  1.0G  99% /mnt/my data\n";

    #[test]
    fn the_description_is_json_the_app_takes() {
        let manifest: Value = nexus_plugin::serde_json::from_str(MANIFEST).unwrap();
        assert_eq!(manifest["format"], 1);
        assert_eq!(manifest["watch"][0]["id"], "full");
    }

    #[test]
    fn a_command_asks_for_df_then_reads_the_system_then_shows_the_table() {
        let first: Value = nexus_plugin::serde_json::from_str(
            &handle(&input(json!({ "kind": "command", "command": "usage" }))).to_json(),
        )
        .unwrap();
        assert_eq!(first["run"]["command"], "df -hP");

        let second: Value = nexus_plugin::serde_json::from_str(
            &handle(&input(
                json!({ "kind": "result", "state": first["state"], "result": { "status": 0, "stdout": DF } }),
            ))
            .to_json(),
        )
        .unwrap();
        assert_eq!(second["read"]["path"], "/etc/os-release");
        assert_eq!(second["state"]["rows"][1][5], "/mnt/my data");

        let third: Value = nexus_plugin::serde_json::from_str(
            &handle(&input(json!({
                "kind": "result", "language": "de", "session": { "server": "web01" }, "state": second["state"],
                "result": { "text": "NAME=Debian\nPRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\n" }
            })))
            .to_json(),
        )
        .unwrap();
        assert_eq!(third["view"]["title"], "Plattenbelegung – web01");
        assert_eq!(third["view"]["text"], "Debian GNU/Linux 13 (trixie)");
        assert_eq!(third["view"]["table"]["rows"][0][4], "63%");
    }

    #[test]
    fn a_declined_command_says_so() {
        let answer: Value = nexus_plugin::serde_json::from_str(
            &handle(&input(
                json!({ "kind": "result", "state": { "step": "df" }, "result": { "error": "declined" } }),
            ))
            .to_json(),
        )
        .unwrap();
        assert!(answer["view"]["text"].as_str().unwrap().contains("declined"));
        assert!(answer.get("read").is_none());
    }

    #[test]
    fn a_full_disk_in_the_output_is_counted_and_reported() {
        let answer: Value = nexus_plugin::serde_json::from_str(
            &handle(&input(json!({
                "kind": "watch", "storage": { "full": 2 },
                "lines": [{ "pattern": "full", "server": "web01", "text": "cp: error writing 'x': No space left on device" }]
            })))
            .to_json(),
        )
        .unwrap();
        assert_eq!(answer["notify"], "web01: a disk is full");
        assert_eq!(answer["storage"]["full"], 3);
    }
}
