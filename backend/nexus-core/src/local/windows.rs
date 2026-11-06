//! Die Shells eines Windows-Rechners: PowerShell 7, Windows PowerShell, `cmd`, WSL (je Distribution), Git Bash.
//!
//! Jedes Programm kommt aus einem festen Ort – dem Systemordner, dem Registereintrag „App Paths“ von PowerShell 7
//! oder dem Programmordner –, nie aus dem Suchpfad.

use std::ffi::OsString;
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::ERROR_SUCCESS;
use windows_sys::Win32::System::Registry::{HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};
use windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;

use super::{Shell, wsl_names};

/// So lange darf `wsl.exe` für die Liste der Distributionen brauchen.
const WSL_LIST_TIMEOUT: Duration = Duration::from_secs(3);

/// Mehr liest der Kern von dieser Liste nicht.
const MAX_WSL_LIST: u64 = 64 * 1024;

/// Die Shells dieses Rechners; nach den Distributionen von WSL wird nur mit `wsl` gefragt.
pub(super) fn shells(wsl: bool) -> Vec<Shell> {
    let mut shells = Vec::new();
    let mut add = |id: &str, name: &str, program: PathBuf, args: &[&str]| {
        shells.push(Shell {
            id: id.to_owned(),
            name: name.to_owned(),
            program,
            args: args.iter().map(OsString::from).collect(),
        });
    };

    if let Some(pwsh) = pwsh() {
        add("pwsh", "PowerShell 7", pwsh, &["-NoLogo"]);
    }

    let system = system_dir();
    if let Some(system) = &system {
        let powershell = system.join(r"WindowsPowerShell\v1.0\powershell.exe");
        if powershell.is_file() {
            add("powershell", "Windows PowerShell", powershell, &["-NoLogo"]);
        }

        let cmd = system.join("cmd.exe");
        if cmd.is_file() {
            add("cmd", "Command Prompt", cmd, &[]);
        }
    }

    if let Some(wsl_exe) = system
        .map(|system| system.join("wsl.exe"))
        .filter(|path| wsl && path.is_file())
    {
        let names = distributions(&wsl_exe);
        if !names.is_empty() {
            add("wsl", "WSL", wsl_exe.clone(), &["--cd", "~"]);
        }

        for name in names {
            add(
                &format!("wsl:{name}"),
                &format!("WSL: {name}"),
                wsl_exe.clone(),
                &["-d", &name, "--cd", "~"],
            );
        }
    }

    let git = [
        program_files(),
        known_dir("LOCALAPPDATA").map(|local| local.join("Programs")),
    ]
    .into_iter()
    .flatten()
    .map(|base| base.join(r"Git\bin\bash.exe"))
    .find(|path| path.is_file());
    if let Some(bash) = git {
        add("gitbash", "Git Bash", bash, &["--login", "-i"]);
    }

    shells
}

/// Der Ordner des Benutzers.
pub(super) fn home() -> Option<PathBuf> {
    known_dir("USERPROFILE").filter(|path| path.is_dir())
}

/// PowerShell 7: wo der Installer es eingetragen hat (`App Paths`, für den Rechner oder den Benutzer), sonst am
/// üblichen Ort im Programmordner. Den Eintrag des Benutzers kann jedes Programm des Benutzers setzen; läuft die
/// App mit Adminrechten, gilt er deshalb nicht – sonst startete sie, was dort steht, erhöht (Hinweis von
/// Perplexity, 2026-11-06).
fn pwsh() -> Option<PathBuf> {
    let usable = |path: &PathBuf| {
        path.is_absolute()
            && path.is_file()
            && path
                .file_name()
                .is_some_and(|name| name.eq_ignore_ascii_case("pwsh.exe"))
    };

    let roots: &[HKEY] = if is_elevated() {
        &[HKEY_LOCAL_MACHINE]
    } else {
        &[HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER]
    };
    roots
        .iter()
        .copied()
        .filter_map(app_path)
        .chain(program_files().map(|base| base.join(r"PowerShell\7\pwsh.exe")))
        .find(usable)
}

/// Ob dieser Prozess mit erhöhten Rechten läuft. Lässt es sich nicht feststellen, gilt er als erhöht – dann fällt
/// nur der Eintrag des Benutzers weg.
fn is_elevated() -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::Security::{GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation};
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    let mut token: HANDLE = std::ptr::null_mut();
    // SAFETY: Das Pseudo-Handle des eigenen Prozesses; `token` lebt hier.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw mut token) } == 0 {
        return true;
    }

    let mut elevation = TOKEN_ELEVATION { TokenIsElevated: 0 };
    let mut length = 0u32;
    // SAFETY: Zeiger und Größe gehören zu `elevation`; `token` ist offen.
    let asked = unsafe {
        GetTokenInformation(
            token,
            TokenElevation,
            (&raw mut elevation).cast(),
            size_of::<TOKEN_ELEVATION>() as u32,
            &raw mut length,
        )
    };
    // SAFETY: `token` gehört uns.
    unsafe { CloseHandle(token) };
    asked == 0 || elevation.TokenIsElevated != 0
}

/// Der Eintrag von `pwsh.exe` unter `App Paths` (der Standardwert), ohne Anführungszeichen.
fn app_path(root: HKEY) -> Option<PathBuf> {
    let key: Vec<u16> = r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\pwsh.exe"
        .encode_utf16()
        .chain([0])
        .collect();
    let mut buffer = vec![0_u16; 1024];
    let mut size = u32::try_from(buffer.len() * 2).ok()?;
    // SAFETY: Schlüssel ist nullterminiert, der Puffer gehört uns und `size` nennt seine Größe in Bytes. Ohne Name
    // des Werts gilt der Standardwert; ein REG_EXPAND_SZ wird dabei aufgelöst.
    let status = unsafe {
        RegGetValueW(
            root,
            key.as_ptr(),
            std::ptr::null(),
            RRF_RT_REG_SZ,
            std::ptr::null_mut(),
            buffer.as_mut_ptr().cast(),
            &raw mut size,
        )
    };
    if status != ERROR_SUCCESS {
        return None;
    }

    buffer.truncate(size as usize / 2);
    let text = String::from_utf16(&buffer).ok()?;
    let text = text.trim_end_matches('\0').trim().trim_matches('"');
    (!text.is_empty()).then(|| PathBuf::from(text))
}

/// Der Systemordner (`C:\Windows\System32`), wie Windows ihn nennt – nicht aus der Umgebung.
fn system_dir() -> Option<PathBuf> {
    let mut buffer = [0_u16; 512];
    // SAFETY: Der Puffer gehört uns, seine Länge steht daneben.
    let length = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
    (1..buffer.len())
        .contains(&length)
        .then(|| PathBuf::from(String::from_utf16_lossy(&buffer[..length])))
}

/// Der Programmordner für 64-Bit-Programme.
fn program_files() -> Option<PathBuf> {
    known_dir("ProgramW6432").or_else(|| known_dir("ProgramFiles"))
}

/// Ein absoluter Ordner aus einer Umgebungsvariable des Kerns.
fn known_dir(name: &str) -> Option<PathBuf> {
    std::env::var_os(name)
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
}

/// Fragt `wsl.exe --list --quiet` nach den Distributionen – ohne Fenster und mit Frist. Ohne WSL oder ohne
/// Distribution antwortet es mit einem Satz und einem Fehlercode; dann gibt es keine.
fn distributions(wsl: &Path) -> Vec<String> {
    let mut command = Command::new(wsl);
    command
        .args(["--list", "--quiet"])
        .env_remove("WSL_UTF8")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW);
    let Ok(mut child) = command.spawn() else {
        return Vec::new();
    };

    let Some(stdout) = child.stdout.take() else {
        let _ = child.kill();
        let _ = child.wait();
        return Vec::new();
    };
    let reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stdout.take(MAX_WSL_LIST).read_to_end(&mut bytes);
        bytes
    });

    let deadline = Instant::now() + WSL_LIST_TIMEOUT;
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break Some(status),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(20)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                break None;
            }
        }
    };

    let bytes = reader.join().unwrap_or_default();
    match status {
        Some(status) if status.success() => wsl_names(&bytes),
        _ => Vec::new(),
    }
}
