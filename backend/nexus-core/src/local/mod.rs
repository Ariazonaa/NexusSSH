//! Lokales Terminal: eine Shell dieses Rechners in einem Tab.
//!
//! Gestartet wird nur, was der Kern selbst gefunden hat ([`shells`]): unter Windows PowerShell 7, Windows
//! PowerShell, `cmd`, WSL (je Distribution) und Git Bash, unter Unix die Anmelde-Shell und die Shells aus
//! `/etc/shells`. Das Profil nennt allein eine Kennung (`pwsh`, `wsl:Ubuntu`, `login` …, geprüft mit
//! [`valid_id`]), nie ein Programm oder Argumente – die stehen je Art fest hier. Kein Text aus einem Profil, einer
//! Export-Datei oder einer Richtlinie wird je Programm oder Argument; einzig der Startordner, den der Benutzer
//! gewählt hat, geht als Argument an `wsl.exe` (`--cd`), nachdem er als vorhandener Ordner geprüft ist.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};

#[cfg(windows)]
mod conpty;
pub(crate) mod session;
#[cfg(unix)]
mod unix;
#[cfg(unix)]
mod unixpty;
#[cfg(windows)]
mod windows;

#[cfg(windows)]
pub(crate) use conpty::{Pty, PtyReader};
#[cfg(unix)]
pub(crate) use unixpty::{Pty, PtyReader};

#[cfg(unix)]
use unix as platform;
#[cfg(windows)]
use windows as platform;

/// So lang darf der Name einer WSL-Distribution sein.
const MAX_DISTRIBUTION: usize = 64;

/// So lang darf der Pfad einer Shell unter Unix sein.
const MAX_PATH: usize = 255;

/// Die Kennungen, die keine Distribution und kein Pfad sind.
const KINDS: [&str; 6] = ["pwsh", "powershell", "cmd", "wsl", "gitbash", "login"];

/// Eine Shell dieses Rechners, wie der Kern sie startet.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Shell {
    /// Kennung, wie sie im Profil steht.
    pub id: String,
    /// Anzeigename (englisch; die App kann ihn je Kennung übersetzen).
    pub name: String,
    /// Das Programm, mit vollem Pfad – aus der eigenen Erkennung.
    pub program: PathBuf,
    /// Die festen Argumente dieser Art.
    pub args: Vec<OsString>,
}

impl Shell {
    /// Die Argumente für einen Start in `dir`. Nur WSL nimmt den Startordner als Argument (`--cd`); die anderen
    /// bekommen ihn als Arbeitsverzeichnis des Prozesses.
    pub(crate) fn args_in(&self, dir: Option<&Path>) -> Vec<OsString> {
        let mut args = self.args.clone();
        let wsl = self.id == "wsl" || self.id.starts_with("wsl:");
        if let (true, Some(dir)) = (wsl, dir)
            && let Some(at) = args.iter().position(|arg| arg == "--cd")
            && let Some(target) = args.get_mut(at + 1)
        {
            *target = dir.as_os_str().to_owned();
        }

        args
    }
}

/// Die Shells dieses Rechners, die gängigste zuerst. Fragt unter Windows `wsl.exe` nach den Distributionen (mit
/// Frist) – das kann eine Sekunde dauern.
pub(crate) fn shells() -> Vec<Shell> {
    platform::shells(true)
}

/// Die Shell zu einer Kennung – nur, wenn es sie auf diesem Rechner gibt.
pub(crate) fn shell(id: &str) -> Option<Shell> {
    if !valid_id(id) {
        return None;
    }

    // Nach WSL wird nur gefragt, wenn eine Distribution gemeint ist.
    platform::shells(id.starts_with("wsl"))
        .into_iter()
        .find(|shell| shell.id == id)
}

/// Der Ordner des Benutzers: Dort startet eine Shell ohne Startordner.
pub(crate) fn home() -> Option<PathBuf> {
    platform::home()
}

/// Die Größe eines Terminals, wie beide Arten von Pseudo-Terminal sie nehmen: Spalten und Zeilen, je mindestens
/// 1 und höchstens 32767 (`COORD` unter Windows rechnet mit `i16`).
pub(crate) fn clamp_size(cols: u32, rows: u32) -> (u16, u16) {
    let side = |value: u32| u16::try_from(value.clamp(1, 0x7fff)).unwrap_or(1);
    (side(cols), side(rows))
}

/// Ob ein Text eine Kennung einer Shell sein kann: eine der festen Arten, `wsl:` mit einem schlichten Namen oder –
/// nur unter Unix – ein schlichter absoluter Pfad. Ob es die Shell gibt, sagt erst [`shell`].
pub(crate) fn valid_id(id: &str) -> bool {
    if KINDS.contains(&id) {
        return true;
    }

    if let Some(name) = id.strip_prefix("wsl:") {
        return plain_distribution(name);
    }

    cfg!(unix) && plain_path(id)
}

/// Ein Name, wie WSL ihn für eine Distribution vergibt: Buchstaben, Ziffern, `.`, `_`, `-`, nicht mit einem Punkt
/// oder Strich vorn (er wird zum Argument hinter `-d`).
fn plain_distribution(name: &str) -> bool {
    (1..=MAX_DISTRIBUTION).contains(&name.len())
        && !name.starts_with(['.', '-'])
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// Ein absoluter Pfad ohne Leerzeichen, Sonderzeichen, `.` und `..` – so steht eine Shell in `/etc/shells`.
fn plain_path(path: &str) -> bool {
    let Some(rest) = path.strip_prefix('/') else {
        return false;
    };

    path.len() <= MAX_PATH
        && path
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'/' | b'+' | b'-'))
        && rest
            .split('/')
            .all(|part| !part.is_empty() && part != "." && part != "..")
}

/// Die Namen der Distributionen aus der Ausgabe von `wsl.exe --list --quiet`: UTF-16LE mit BOM, Nullbytes und
/// CRLF – oder UTF-8, wenn `WSL_UTF8` gesetzt ist. Was kein schlichter Name ist (ein Satz wie „no installed
/// distributions“, ein Name mit Leerzeichen), fällt weg.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn wsl_names(output: &[u8]) -> Vec<String> {
    let text = if output.starts_with(&[0xff, 0xfe]) || output.contains(&0) {
        let body = output.strip_prefix(&[0xff, 0xfe]).unwrap_or(output);
        let units: Vec<u16> = body
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect();
        String::from_utf16_lossy(&units)
    } else {
        String::from_utf8_lossy(output).into_owned()
    };

    let mut names: Vec<String> = Vec::new();
    for name in text.split(['\r', '\n', '\0']).map(str::trim) {
        if plain_distribution(name) && !names.iter().any(|known| known == name) {
            names.push(name.to_owned());
        }
    }

    names
}

/// Die Befehlszeile für `CreateProcessW`, nach den Regeln, mit denen `CommandLineToArgvW` (und die C-Laufzeit)
/// sie wieder zerlegt: Das Programm steht in Anführungszeichen, ein Argument mit Leerzeichen, Tabulator oder
/// Anführungszeichen auch; Rückstriche vor einem Anführungszeichen und am Ende werden verdoppelt.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn command_line(program: &Path, args: &[OsString]) -> Vec<u16> {
    const QUOTE: u16 = b'"' as u16;
    const BACKSLASH: u16 = b'\\' as u16;

    let mut line = vec![QUOTE];
    line.extend(wide(program.as_os_str()));
    line.push(QUOTE);
    for arg in args {
        line.push(u16::from(b' '));
        let units = wide(arg);
        let special = |unit: &u16| matches!(*unit, 0x20 | 0x09 | 0x0a | 0x0b | QUOTE);
        if !units.is_empty() && !units.iter().any(special) {
            line.extend(units);
            continue;
        }

        line.push(QUOTE);
        let mut backslashes = 0;
        for unit in units {
            if unit == BACKSLASH {
                backslashes += 1;
            } else {
                if unit == QUOTE {
                    line.extend(std::iter::repeat_n(BACKSLASH, backslashes + 1));
                }

                backslashes = 0;
            }

            line.push(unit);
        }

        line.extend(std::iter::repeat_n(BACKSLASH, backslashes));
        line.push(QUOTE);
    }

    line
}

#[cfg(windows)]
fn wide(text: &OsStr) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    text.encode_wide().collect()
}

#[cfg(not(windows))]
#[allow(dead_code)] // Nur für die Tests von `command_line`.
fn wide(text: &OsStr) -> Vec<u16> {
    text.to_string_lossy().encode_utf16().collect()
}

/// Die Umgebung, in der eine Shell startet ([`environment_from`] mit der des Kerns).
pub(crate) fn environment() -> Vec<(OsString, OsString)> {
    environment_from(std::env::vars_os())
}

/// Die Umgebung der App ohne `WEBVIEW2_*` – die App setzt dort ihren eigenen Datenordner, und ein Programm aus der
/// Shell soll ihn nicht erben –, dazu `TERM_PROGRAM=NexusSSH` und unter Unix `TERM` und `COLORTERM` für ein
/// Terminal mit allen Farben.
pub(crate) fn environment_from(vars: impl Iterator<Item = (OsString, OsString)>) -> Vec<(OsString, OsString)> {
    let mut own = vec![("TERM_PROGRAM", "NexusSSH")];
    if cfg!(unix) {
        own.extend([("TERM", "xterm-256color"), ("COLORTERM", "truecolor")]);
    }

    let mut env: Vec<(OsString, OsString)> = vars
        .filter(|(name, _)| {
            let name = name.to_string_lossy().to_ascii_uppercase();
            !name.starts_with("WEBVIEW2_") && own.iter().all(|(set, _)| *set != name)
        })
        .collect();
    env.extend(
        own.into_iter()
            .map(|(name, value)| (OsString::from(name), OsString::from(value))),
    );
    env
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_known_kinds_of_shell_and_plain_distribution_names_are_ids() {
        for good in [
            "pwsh",
            "powershell",
            "cmd",
            "wsl",
            "wsl:Ubuntu-24.04",
            "wsl:a_b.c",
            "gitbash",
            "login",
        ] {
            assert!(valid_id(good), "{good}");
        }

        let long = format!("wsl:{}", "a".repeat(65));
        for bad in [
            "",
            "wsl:",
            "wsl:../x",
            "wsl:a b",
            "wsl:a;b",
            "wsl:a\"b",
            "wsl:ä",
            "C:\\evil.exe",
            "pwsh -c calc",
            "PWSH",
            "bash",
            long.as_str(),
            &"wsl:".repeat(30),
        ] {
            assert!(!valid_id(bad), "{bad}");
        }
    }

    #[test]
    fn a_path_is_an_id_only_on_unix_and_only_a_plain_absolute_one() {
        assert_eq!(valid_id("/bin/bash"), cfg!(unix));
        assert_eq!(valid_id("/usr/local/bin/fish-3.7+x"), cfg!(unix));
        for bad in [
            "bin/bash",
            "/bin/ba sh",
            "/bin/bash;id",
            "/bin/$(id)",
            "/bin/../tmp/x",
            "/bin/./bash",
            "//bin/bash",
            "/bin/bash/",
            "/",
        ] {
            assert!(!valid_id(bad), "{bad}");
        }

        assert!(!valid_id(&format!("/{}", "a".repeat(255))));
    }

    #[test]
    fn the_list_of_wsl_comes_in_utf16_with_odd_bits() {
        let raw = b"\xff\xfeU\0b\0u\0n\0t\0u\0\r\0\n\0\0\0d\0e\0b\0i\0a\0n\0\r\0\n\0";
        assert_eq!(wsl_names(raw), ["Ubuntu", "debian"]);

        // Mit `WSL_UTF8=1` schreibt wsl.exe UTF-8.
        assert_eq!(
            wsl_names(b"Ubuntu-24.04\r\nkali-linux\n\n"),
            ["Ubuntu-24.04", "kali-linux"]
        );
    }

    #[test]
    fn what_is_no_plain_distribution_name_does_not_come_through() {
        let utf16 = |text: &str| -> Vec<u8> {
            let mut bytes = vec![0xff, 0xfe];
            bytes.extend(text.encode_utf16().flat_map(u16::to_le_bytes));
            bytes
        };

        // So antwortet ein Windows ohne WSL – oder ohne Distribution: ein Satz, kein Name.
        assert!(
            wsl_names(&utf16(
                "Windows Subsystem for Linux has no installed distributions.\r\n"
            ))
            .is_empty()
        );
        assert_eq!(
            wsl_names(&utf16("Ubuntu\r\nmy distro\r\n../etc\r\nx;calc\r\nDebian\r\n")),
            ["Ubuntu", "Debian"]
        );
        // Kaputtes UTF-16 (ungerade Länge, einzelne Ersatzhälfte) ergibt nichts Falsches.
        assert!(
            wsl_names(b"\xff\xfeU\0\x00\xd8b\0")
                .iter()
                .all(|name| valid_id(&format!("wsl:{name}")))
        );
        assert!(wsl_names(&[]).is_empty());
    }

    #[test]
    fn arguments_survive_the_windows_command_line() {
        let line = |args: &[&str]| {
            let args: Vec<OsString> = args.iter().map(OsString::from).collect();
            String::from_utf16_lossy(&command_line(Path::new("C:\\w\\wsl.exe"), &args))
        };

        assert_eq!(line(&["--cd", "C:\\x y\\"]), "\"C:\\w\\wsl.exe\" --cd \"C:\\x y\\\\\"");
        assert_eq!(line(&["a b"]), "\"C:\\w\\wsl.exe\" \"a b\"");
        assert_eq!(line(&["a\"b"]), "\"C:\\w\\wsl.exe\" \"a\\\"b\"");
        assert_eq!(line(&["a\\\\\"b"]), "\"C:\\w\\wsl.exe\" \"a\\\\\\\\\\\"b\"");
        assert_eq!(line(&["C:\\x\\"]), "\"C:\\w\\wsl.exe\" C:\\x\\");
        assert_eq!(line(&[""]), "\"C:\\w\\wsl.exe\" \"\"");
        assert_eq!(line(&["a\tb"]), "\"C:\\w\\wsl.exe\" \"a\tb\"");
        assert_eq!(line(&[]), "\"C:\\w\\wsl.exe\"");
    }

    #[test]
    fn the_shell_gets_the_environment_of_the_app_without_its_webview_settings() {
        let vars = [
            ("PATH", "/bin"),
            ("WEBVIEW2_USER_DATA_FOLDER", "x"),
            ("webview2_additional_browser_arguments", "--remote-debugging-port=9"),
            ("TERM_PROGRAM", "other"),
            ("LANG", "de_DE.UTF-8"),
        ]
        .map(|(name, value)| (OsString::from(name), OsString::from(value)));
        let env = environment_from(vars.into_iter());
        let get = |name: &str| {
            env.iter()
                .filter(|(key, _)| key == OsStr::new(name))
                .map(|(_, value)| value.to_string_lossy().into_owned())
                .collect::<Vec<_>>()
        };

        assert_eq!(get("PATH"), ["/bin"]);
        assert_eq!(get("LANG"), ["de_DE.UTF-8"]);
        assert_eq!(get("TERM_PROGRAM"), ["NexusSSH"]);
        assert!(
            env.iter()
                .all(|(key, _)| !key.to_string_lossy().to_uppercase().starts_with("WEBVIEW2_"))
        );
        if cfg!(unix) {
            assert_eq!(get("TERM"), ["xterm-256color"]);
            assert_eq!(get("COLORTERM"), ["truecolor"]);
        }
    }

    #[test]
    fn a_shell_is_found_by_its_id_and_only_then() {
        let found = shells();
        assert!(!found.is_empty());
        for shell in &found {
            assert!(valid_id(&shell.id), "{shell:?}");
            assert!(shell.program.is_absolute() && shell.program.is_file(), "{shell:?}");
            assert!(!shell.name.is_empty());
        }

        let usual = if cfg!(windows) { "cmd" } else { "login" };
        assert!(found.iter().any(|shell| shell.id == usual), "{found:?}");
        assert_eq!(shell("wsl:gibtsnicht-4711"), None);
        assert_eq!(shell("evil"), None);
        assert_eq!(shell(&found[0].id).map(|shell| shell.id), Some(found[0].id.clone()));
    }

    #[test]
    fn only_wsl_takes_the_start_folder_as_an_argument() {
        let wsl = Shell {
            id: "wsl:Ubuntu".into(),
            name: "WSL: Ubuntu".into(),
            program: PathBuf::from("C:\\Windows\\System32\\wsl.exe"),
            args: ["-d", "Ubuntu", "--cd", "~"].map(OsString::from).to_vec(),
        };
        assert_eq!(
            wsl.args_in(Some(Path::new("D:\\src"))),
            ["-d", "Ubuntu", "--cd", "D:\\src"].map(OsString::from)
        );
        assert_eq!(wsl.args_in(None), ["-d", "Ubuntu", "--cd", "~"].map(OsString::from));

        let cmd = Shell {
            id: "cmd".into(),
            name: "Command Prompt".into(),
            program: PathBuf::from("C:\\Windows\\System32\\cmd.exe"),
            args: Vec::new(),
        };
        assert!(cmd.args_in(Some(Path::new("D:\\src"))).is_empty());
    }
}
