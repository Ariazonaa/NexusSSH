//! Die Shells eines Unix-Rechners: die Anmelde-Shell des Benutzers und die aus `/etc/shells`.
//!
//! Eine Shell, die nicht in `/etc/shells` steht, startet der Kern nicht – auch nicht als Anmelde-Shell (ein Konto
//! mit `/usr/bin/env` oder einem Programm als „Shell“). So hält es auch `chsh`.

use std::ffi::CStr;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};

use super::{Shell, valid_id};

/// Was in `/etc/shells` stehen kann, aber keine Shell zum Arbeiten ist.
const NO_SHELL: [&str; 4] = ["nologin", "false", "true", "git-shell"];

/// Die Shells dieses Rechners, die Anmelde-Shell zuerst.
pub(super) fn shells(_wsl: bool) -> Vec<Shell> {
    // Ohne `/etc/shells` gilt, was die C-Bibliothek dann annimmt (`getusershell`).
    let listed = std::fs::read_to_string("/etc/shells")
        .map(|text| shells_from(&text))
        .unwrap_or_else(|_| vec!["/bin/sh".to_owned(), "/bin/csh".to_owned()]);
    let login = account()
        .map(|(shell, _)| shell)
        .or_else(|| std::env::var("SHELL").ok().filter(|shell| !shell.is_empty()));
    collect(login.as_deref(), &listed, usable)
}

/// Der Ordner des Benutzers.
pub(super) fn home() -> Option<PathBuf> {
    account()
        .map(|(_, home)| PathBuf::from(home))
        .or_else(|| std::env::var_os("HOME").map(PathBuf::from))
        .filter(|path| path.is_absolute() && path.is_dir())
}

/// Die Pfade aus dem Text von `/etc/shells`: eine je Zeile, ohne Kommentare, nur schlichte absolute Pfade.
pub(super) fn shells_from(text: &str) -> Vec<String> {
    text.lines()
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.starts_with('#'))
        .filter(|line| valid_id(line))
        .map(str::to_owned)
        .collect()
}

/// Die Shells aus Anmelde-Shell und Liste. `usable` nennt zu einem Pfad, wohin er wirklich führt, wenn dort ein
/// ausführbares Programm liegt – so steht `/bin/bash` neben `/usr/bin/bash` nur einmal da.
fn collect(login: Option<&str>, listed: &[String], usable: impl Fn(&Path) -> Option<PathBuf>) -> Vec<Shell> {
    let name_of = |path: &str| path.rsplit('/').next().unwrap_or(path).to_owned();
    let mut shells = Vec::new();
    let mut seen: Vec<PathBuf> = Vec::new();
    if let Some(login) = login.filter(|login| listed.iter().any(|path| path == login))
        && let Some(target) = usable(Path::new(login))
    {
        shells.push(Shell {
            id: "login".to_owned(),
            name: name_of(login),
            program: PathBuf::from(login),
            args: Vec::new(),
        });
        seen.push(target);
    }

    for path in listed {
        let name = name_of(path);
        if NO_SHELL.contains(&name.as_str()) {
            continue;
        }

        if let Some(target) = usable(Path::new(path))
            && !seen.contains(&target)
        {
            shells.push(Shell {
                id: path.clone(),
                name,
                program: PathBuf::from(path),
                args: Vec::new(),
            });
            seen.push(target);
        }
    }

    shells
}

/// Wohin ein Pfad führt, wenn dort ein ausführbares Programm liegt.
fn usable(path: &Path) -> Option<PathBuf> {
    let target = std::fs::canonicalize(path).ok()?;
    let metadata = std::fs::metadata(&target).ok()?;
    (metadata.is_file() && metadata.permissions().mode() & 0o111 != 0).then_some(target)
}

/// Anmelde-Shell und Heimatordner aus der Benutzerdatenbank.
fn account() -> Option<(String, String)> {
    let mut buffer = vec![0 as libc::c_char; 16 * 1024];
    // SAFETY: `passwd` ist eine reine C-Struktur; Nullen sind ein gültiger Anfang, `getpwuid_r` füllt sie.
    let mut entry: libc::passwd = unsafe { std::mem::zeroed() };
    let mut result: *mut libc::passwd = std::ptr::null_mut();
    // SAFETY: Alle Zeiger zeigen auf Speicher, der hier lebt; der Puffer ist so groß, wie angegeben.
    let code = unsafe {
        libc::getpwuid_r(
            libc::getuid(),
            &raw mut entry,
            buffer.as_mut_ptr(),
            buffer.len(),
            &raw mut result,
        )
    };
    if code != 0 || result.is_null() {
        return None;
    }

    let text = |pointer: *const libc::c_char| {
        // SAFETY: Die Zeiger zeigen bei Erfolg auf nullterminierte Texte im Puffer, der noch lebt.
        (!pointer.is_null())
            .then(|| unsafe { CStr::from_ptr(pointer) })
            .and_then(|text| text.to_str().ok())
            .filter(|text| !text.is_empty())
            .map(str::to_owned)
    };
    Some((text(entry.pw_shell)?, text(entry.pw_dir)?))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_path_is_a_shell_only_if_etc_shells_lists_it() {
        let listed = shells_from(
            "# /etc/shells: valid login shells\n/bin/sh\n\n  /bin/bash  \n/usr/bin/bash\n/usr/bin/fish\n\
             /usr/sbin/nologin\nbin/zsh\n/bin/ba sh\n/opt/../bin/evil\n",
        );
        assert_eq!(
            listed,
            [
                "/bin/sh",
                "/bin/bash",
                "/usr/bin/bash",
                "/usr/bin/fish",
                "/usr/sbin/nologin"
            ]
        );

        // `/usr/bin/bash` führt zu `/bin/bash`; `/usr/bin/fish` gibt es auf diesem Rechner nicht.
        let usable = |path: &Path| match path.to_str() {
            Some("/bin/sh" | "/usr/bin/env" | "/usr/sbin/nologin") => Some(path.to_owned()),
            Some("/bin/bash" | "/usr/bin/bash") => Some(PathBuf::from("/usr/bin/bash")),
            _ => None,
        };
        let ids = |shells: Vec<Shell>| shells.into_iter().map(|shell| shell.id).collect::<Vec<_>>();

        assert_eq!(ids(collect(Some("/bin/bash"), &listed, usable)), ["login", "/bin/sh"]);
        let login = &collect(Some("/bin/bash"), &listed, usable)[0];
        assert_eq!(
            (login.name.as_str(), login.program.as_path()),
            ("bash", Path::new("/bin/bash"))
        );

        // Eine Anmelde-Shell, die nicht in der Liste steht, wird nicht gestartet.
        assert_eq!(
            ids(collect(Some("/usr/bin/env"), &listed, usable)),
            ["/bin/sh", "/bin/bash"]
        );
        assert_eq!(ids(collect(None, &listed, usable)), ["/bin/sh", "/bin/bash"]);
    }

    #[test]
    fn this_computer_has_a_login_shell_and_a_home() {
        let found = shells(false);
        assert_eq!(found.first().map(|shell| shell.id.as_str()), Some("login"), "{found:?}");
        assert!(home().is_some());
    }
}
