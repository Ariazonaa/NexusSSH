//! Protokolle des Servers ansehen: das Journal von systemd (`journalctl`) und Textdateien wie die unter `/var/log`.
//!
//! Die Befehle baut der Kern; die App nennt nur, was sie sehen will – einen Zeitraum aus einer festen Liste, eine
//! Schwere als Zahl, den Namen einer Einheit, einen Pfad. Der Name geht durch [`unit_name`] und wie der Pfad durch
//! [`crate::exec::quote`]. Was zurückkommt, ist Text des Servers: Die Leser hier kürzen ihn und nehmen die
//! Steuerzeichen heraus, bevor er zur App geht.
//!
//! Beide Befehle liefern die **neuesten Zeilen zuerst**: Die Ausgabe eines Befehls ist begrenzt
//! ([`crate::exec::MAX_OUTPUT`]), und was dann fehlt, soll das Älteste sein. Die Leser drehen die Reihenfolge wieder
//! um.

use serde::{Deserialize, Serialize};

use crate::exec::{Answer, Output, quote};
use crate::sftp::Failure;

/// So viele Zeilen liest der Kern höchstens, und so viele ohne Angabe.
pub(crate) const MAX_LINES: u32 = 2000;
pub(crate) const DEFAULT_LINES: u32 = 500;
/// Länger wird eine Zeile für die App nicht.
const MAX_MESSAGE: usize = 4000;
const MAX_SOURCE: usize = 64;

/// Steht statt Einträgen da, wenn der Server kein `journalctl` hat.
const NO_JOURNAL: &str = "@nojournal";
/// Steht vor den Einträgen, wenn das Konto nur seine eigenen sieht (nicht in `systemd-journal` oder `adm`).
const LIMITED: &str = "@limited";

/// Die Felder, die der Kern liest – weniger Felder, mehr Einträge im selben Platz. Ältere Fassungen von systemd
/// (vor 236) kennen die Auswahl nicht; dann kommt alles.
const FIELDS: &str = "PRIORITY,MESSAGE,SYSLOG_IDENTIFIER,_COMM,_SYSTEMD_UNIT";

/// Wie weit das Journal zurückreichen soll – eine feste Auswahl, damit nie Text der App in der Befehlszeile steht.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
pub enum Since {
    /// Die letzten 15 Minuten.
    #[serde(rename = "15m")]
    Minutes15,
    /// Die letzte Stunde.
    #[serde(rename = "1h")]
    Hour,
    /// Die letzten sechs Stunden.
    #[serde(rename = "6h")]
    Hours6,
    /// Der letzte Tag.
    #[serde(rename = "1d")]
    Day,
    /// Die letzte Woche.
    #[serde(rename = "7d")]
    Week,
}

impl Since {
    fn argument(self) -> &'static str {
        match self {
            Self::Minutes15 => "-15min",
            Self::Hour => "-1h",
            Self::Hours6 => "-6h",
            Self::Day => "-1d",
            Self::Week => "-7d",
        }
    }
}

/// Eine Zeile eines Protokolls.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct LogLine {
    /// Wann, in Sekunden seit 1970; 0, wenn die Quelle es nicht nennt (Dateien).
    #[serde(skip_serializing_if = "is_zero")]
    pub time: u64,
    /// Schwere wie bei syslog, 0 (Notfall) bis 7 (Fehlersuche); fehlt, wenn die Quelle keine nennt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub priority: Option<u8>,
    /// Wer es geschrieben hat (Programm oder Einheit); leer bei Dateien.
    #[serde(skip_serializing_if = "String::is_empty")]
    pub source: String,
    /// Der Text – Ausgabe des Servers, also als reiner Text zu zeigen.
    pub message: String,
}

#[allow(clippy::trivially_copy_pass_by_ref)]
fn is_zero(value: &u64) -> bool {
    *value == 0
}

/// Der Name einer Einheit von systemd (`nginx.service`, `getty@tty1`), wie ihn `journalctl -u` bekommt: nur, was
/// ein solcher Name sein kann. Muster (`*`) gehören nicht dazu.
pub(crate) fn unit_name(text: &str) -> Option<&str> {
    let text = text.trim();
    (!text.is_empty()
        && text.len() <= 128
        && !text.starts_with('-')
        && text
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || "@._:-\\".contains(character)))
    .then_some(text)
}

fn line_count(lines: Option<u32>) -> u32 {
    lines.unwrap_or(DEFAULT_LINES).clamp(1, MAX_LINES)
}

/// Der Befehl für das Journal; `None`, wenn Schwere oder Einheit nichts sind, was `journalctl` bekommen darf.
pub(crate) fn journal_command(
    lines: Option<u32>,
    priority: Option<u8>,
    since: Option<Since>,
    unit: Option<&str>,
) -> Option<String> {
    let mut options = format!("-n {}", line_count(lines));
    if let Some(priority) = priority {
        if priority > 7 {
            return None;
        }

        options.push_str(&format!(" -p {priority}"));
    }

    if let Some(since) = since {
        options.push_str(&format!(" --since={}", since.argument()));
    }

    if let Some(unit) = unit.filter(|unit| !unit.trim().is_empty()) {
        options.push_str(&format!(" -u {}", quote(unit_name(unit)?)?));
    }

    // Erst die Frage, ob das Konto alles sieht (`-q` unten unterdrückt den Hinweis dazu), dann die Einträge –
    // mit der Auswahl der Felder und, wo `journalctl` die nicht kennt, ohne.
    Some(format!(
        "export LC_ALL=C; command -v journalctl >/dev/null 2>&1 || {{ echo {NO_JOURNAL}; exit 0; }}; \
         journalctl --no-pager -n 0 2>&1 >/dev/null | grep -q \"not seeing messages\" && echo {LIMITED}; \
         journalctl --no-pager -q -r -o json --output-fields={FIELDS} {options} 2>/dev/null || \
         journalctl --no-pager -q -r -o json {options}"
    ))
}

/// Der Befehl für die letzten Zeilen einer Datei; `None`, wenn der Pfad keiner ist. Nur eine gewöhnliche Datei
/// wird gelesen – an einer Pipe bliebe `tail` stehen. Eine `.gz` (rotierte Protokolle) wird dafür entpackt.
pub(crate) fn file_command(path: &str, lines: Option<u32>) -> Option<String> {
    if !path.starts_with('/') {
        return None;
    }

    let (path, lines) = (quote(path)?, line_count(lines));
    Some(format!(
        "[ -f {path} ] && [ -r {path} ] || {{ echo \"not a readable file\" >&2; exit 1; }}; \
         case {path} in *.gz) command -v gzip >/dev/null 2>&1 || {{ echo \"gzip is missing\" >&2; exit 1; }} ;; esac; \
         case {path} in *.gz) gzip -dc {path} 2>/dev/null | tail -n {lines} ;; *) tail -n {lines} {path} ;; esac | \
         awk '{{ line[NR] = $0 }} END {{ for (i = NR; i > 0; i--) print line[i] }}'"
    ))
}

/// Liest, was [`journal_command`] ausgibt. Kein `journalctl` ist ein Fehler (`no_journal`), den die App erklärt.
pub(crate) fn read_journal(output: &Output) -> Result<Answer, Failure> {
    let (lines, limited, missing) = parse_journal(&output.stdout);
    if missing {
        return Err(Failure {
            code: "no_journal",
            message: "the server has no journalctl".to_owned(),
        });
    }

    Ok(Answer::Logs(lines, limited))
}

/// Die Einträge des Journals (älteste zuerst), ob das Konto nur einen Teil sieht, und ob es gar kein Journal gibt.
/// Eine Zeile, die sich nicht lesen lässt – auch die letzte, wenn die Ausgabe mittendrin abbrach –, fehlt.
pub(crate) fn parse_journal(text: &str) -> (Vec<LogLine>, bool, bool) {
    let (mut lines, mut limited, mut missing) = (Vec::new(), false, false);
    for line in text.lines() {
        match line.trim() {
            NO_JOURNAL => missing = true,
            LIMITED => limited = true,
            entry if lines.len() < MAX_LINES as usize => lines.extend(journal_entry(entry)),
            _ => {}
        }
    }

    lines.reverse();
    (lines, limited, missing)
}

fn journal_entry(line: &str) -> Option<LogLine> {
    let entry: serde_json::Value = serde_json::from_str(line).ok()?;
    let entry = entry.as_object()?;
    let text = |name: &str| entry.get(name).and_then(serde_json::Value::as_str);
    // Was kein UTF-8 ist, schreibt journalctl als Liste von Bytes.
    let message = match entry.get("MESSAGE")? {
        serde_json::Value::String(message) => message.clone(),
        serde_json::Value::Array(bytes) => String::from_utf8_lossy(
            &bytes
                .iter()
                .take(MAX_MESSAGE * 4)
                .filter_map(|byte| byte.as_u64().and_then(|byte| u8::try_from(byte).ok()))
                .collect::<Vec<_>>(),
        )
        .into_owned(),
        _ => return None,
    };

    // Mikrosekunden seit 1970, als Text.
    let time = text("__REALTIME_TIMESTAMP")
        .and_then(|micros| micros.parse::<u64>().ok())
        .map_or(0, |micros| micros / 1_000_000);
    let priority = match entry.get("PRIORITY") {
        Some(serde_json::Value::String(digit)) => digit.parse::<u8>().ok(),
        Some(serde_json::Value::Number(number)) => number.as_u64().and_then(|number| u8::try_from(number).ok()),
        _ => None,
    }
    .filter(|priority| *priority <= 7);
    let source = text("SYSLOG_IDENTIFIER")
        .or_else(|| text("_COMM"))
        .or_else(|| text("_SYSTEMD_UNIT"))
        .unwrap_or_default();
    Some(LogLine {
        time,
        priority,
        source: shown(source, MAX_SOURCE),
        message: shown(&message, MAX_MESSAGE),
    })
}

/// Liest, was [`file_command`] ausgibt: je Zeile ein Eintrag ohne Zeit und Schwere, älteste zuerst.
pub(crate) fn read_file(output: &Output) -> Answer {
    Answer::Logs(parse_file(&output.stdout), false)
}

pub(crate) fn parse_file(text: &str) -> Vec<LogLine> {
    let mut lines: Vec<LogLine> = text
        .lines()
        .take(MAX_LINES as usize)
        .map(|line| LogLine {
            time: 0,
            priority: None,
            source: String::new(),
            message: shown(line, MAX_MESSAGE),
        })
        .collect();
    lines.reverse();
    lines
}

/// Text des Servers für die Anzeige: ohne Steuerzeichen (ein Tabulator wird ein Leerzeichen), ohne Zeichen, die die
/// Schreibrichtung umkehren, gekürzt.
fn shown(text: &str, limit: usize) -> String {
    text.chars()
        .map(|character| if character == '\t' { ' ' } else { character })
        .filter(|character| {
            !character.is_control() && !matches!(character, '\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}')
        })
        .take(limit)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_journal_command_holds_only_fixed_words_numbers_and_a_quoted_unit() {
        let plain = journal_command(None, None, None, None).unwrap();
        assert!(plain.contains("journalctl --no-pager -q -r -o json --output-fields="));
        assert!(plain.contains(" -n 500 "));
        assert!(plain.ends_with("journalctl --no-pager -q -r -o json -n 500"));

        let all = journal_command(Some(99_999), Some(3), Some(Since::Hours6), Some(" nginx.service ")).unwrap();
        assert!(
            all.ends_with("-o json -n 2000 -p 3 --since=-6h -u 'nginx.service'"),
            "{all}"
        );
        assert!(
            journal_command(Some(0), None, None, Some(""))
                .unwrap()
                .ends_with("-n 1")
        );

        // Was keine Schwere und kein Name einer Einheit ist, ergibt keinen Befehl.
        assert_eq!(journal_command(None, Some(8), None, None), None);
        for unit in [
            "a b",
            "x; rm -rf /",
            "$(id)",
            "-u",
            "a'b",
            "*",
            "a\nb",
            &"x".repeat(129),
        ] {
            assert_eq!(journal_command(None, None, None, Some(unit)), None, "{unit:?}");
        }

        assert_eq!(unit_name("getty@tty1.service"), Some("getty@tty1.service"));
        assert_eq!(
            unit_name("dev-disk-by\\x2duuid.swap"),
            Some("dev-disk-by\\x2duuid.swap")
        );
    }

    #[test]
    fn the_file_command_quotes_the_path_and_refuses_what_is_no_path() {
        let command = file_command("/var/log/it's here.log", Some(50)).unwrap();
        assert!(
            command.starts_with(r"[ -f '/var/log/it'\''s here.log' ] && [ -r "),
            "{command}"
        );
        assert!(command.contains(r"tail -n 50 '/var/log/it'\''s here.log' ;;"));
        assert!(command.contains("gzip -dc "));

        assert_eq!(file_command("var/log/syslog", None), None);
        assert_eq!(file_command("", None), None);
        assert_eq!(file_command("/var/log/a\nb", None), None);
    }

    #[test]
    fn journal_entries_come_back_oldest_first_with_time_priority_and_source() {
        let text = concat!(
            "@limited\n",
            r#"{"__REALTIME_TIMESTAMP":"1760000002000000","PRIORITY":"3","SYSLOG_IDENTIFIER":"sshd","MESSAGE":"second\u001b[31m\tred"}"#,
            "\n",
            r#"{"__REALTIME_TIMESTAMP":"1760000001999999","PRIORITY":6,"_COMM":"cron","MESSAGE":[104,105,255]}"#,
            "\n",
            r#"{"MESSAGE":"no time, no priority","_SYSTEMD_UNIT":"a.service","PRIORITY":"9"}"#,
            "\n",
            "not json\n",
            r#"{"PRIORITY":"3"}"#,
            "\n",
            r#"{"__REALTIME_TIMESTAMP":"17600"#,
        );

        let (lines, limited, missing) = parse_journal(text);

        assert!(limited);
        assert!(!missing);
        assert_eq!(
            lines,
            [
                LogLine {
                    time: 0,
                    priority: None,
                    source: "a.service".to_owned(),
                    message: "no time, no priority".to_owned()
                },
                LogLine {
                    time: 1_760_000_001,
                    priority: Some(6),
                    source: "cron".to_owned(),
                    message: "hi\u{fffd}".to_owned()
                },
                LogLine {
                    time: 1_760_000_002,
                    priority: Some(3),
                    source: "sshd".to_owned(),
                    message: "second[31m red".to_owned()
                },
            ]
        );

        assert_eq!(parse_journal("@nojournal\n"), (Vec::new(), false, true));
        let output = |stdout: &str| Output {
            status: Some(0),
            stdout: stdout.to_owned(),
            stderr: String::new(),
        };
        assert_eq!(read_journal(&output("@nojournal\n")).unwrap_err().code, "no_journal");
        assert_eq!(read_journal(&output("")).unwrap(), Answer::Logs(Vec::new(), false));
    }

    #[test]
    fn lines_of_a_file_come_back_oldest_first_cleaned_and_shortened() {
        let long = "x".repeat(MAX_MESSAGE + 10);
        let lines = parse_file(&format!("newest \u{202e}evil\nmiddle\u{7}\n{long}\n"));

        assert_eq!(
            lines.iter().map(|line| line.message.len()).collect::<Vec<_>>(),
            [MAX_MESSAGE, 6, 11]
        );
        assert_eq!(lines[2].message, "newest evil");
        assert!(
            lines
                .iter()
                .all(|line| line.time == 0 && line.priority.is_none() && line.source.is_empty())
        );
    }
}
