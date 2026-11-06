//! Das Pseudo-Terminal unter Unix: `openpty`, die Shell als Anführer einer eigenen Sitzung mit dem Terminal als
//! steuerndem Terminal.
//!
//! Die Shell startet als Anmelde-Shell (`argv[0]` mit `-` davor). Endet der Tab, bekommt alles in ihrer Sitzung
//! `SIGHUP` (und `SIGCONT`, damit Angehaltene es auch merken) – auch Hintergrundjobs, die eine Shell mit
//! Jobsteuerung in eine eigene Prozessgruppe steckt. Die Sitzung findet der Kern unter Linux über `/proc`;
//! anderswo erreicht das Signal nur die Prozessgruppe der Shell. Endet die Shell darauf nicht binnen
//! [`HANGUP_GRACE`], bekommt sie `SIGKILL`.

use std::ffi::OsString;
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use tokio::io::unix::AsyncFd;
use tokio::sync::{mpsc, watch};

use super::{Shell, clamp_size};

/// So viel liest der Leser auf einmal.
const CHUNK: usize = 64 * 1024;

/// So lange hat die Shell nach `SIGHUP` Zeit, bevor sie `SIGKILL` bekommt.
const HANGUP_GRACE: Duration = Duration::from_secs(3);

/// Gehalten vom Öffnen des Terminals bis zum Start der Shell.
static SPAWNING: Mutex<()> = Mutex::new(());

/// Eine Shell in einem Pseudo-Terminal.
pub(crate) struct Pty {
    master: Arc<AsyncFd<OwnedFd>>,
    input: mpsc::UnboundedSender<Vec<u8>>,
    process: Arc<Process>,
    exit: watch::Receiver<Option<Option<i32>>>,
}

/// Was die Shell ausgibt; `None` heißt: Das Terminal ist zu (die Shell und alles, was es offen hielt, ist weg).
pub(crate) struct PtyReader {
    master: Arc<AsyncFd<OwnedFd>>,
    buffer: Vec<u8>,
}

/// Die Shell als Prozess. `reaped` steht, sobald ihr Ende abgeholt ist – vorher kann ihre Nummer nicht neu
/// vergeben sein, ein Signal trifft also nie einen fremden Prozess.
struct Process {
    pid: libc::pid_t,
    reaped: Mutex<bool>,
}

impl Pty {
    /// Startet die Shell in `dir` (sonst im Arbeitsverzeichnis des Kerns) mit genau der Umgebung `env`. Muss in
    /// einem Runtime von tokio laufen.
    pub(crate) fn spawn(
        shell: &Shell,
        dir: Option<&Path>,
        size: (u32, u32),
        env: &[(OsString, OsString)],
    ) -> io::Result<(Pty, PtyReader)> {
        // Zwei Terminals, die zugleich starten, sollen einander die Enden nicht vererben (siehe `pair`).
        let spawning = SPAWNING.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let (master, slave) = open(size)?;
        let name = shell
            .program
            .file_name()
            .map_or_else(|| OsString::from("sh"), ToOwned::to_owned);
        let mut login = OsString::from("-");
        login.push(name);

        let mut command = Command::new(&shell.program);
        command
            .arg0(login)
            .args(shell.args_in(dir))
            .env_clear()
            .envs(env.iter().map(|(name, value)| (name, value)))
            .stdin(Stdio::from(slave.try_clone()?))
            .stdout(Stdio::from(slave.try_clone()?))
            .stderr(Stdio::from(slave));
        if let Some(dir) = dir {
            command.current_dir(dir);
        }

        // SAFETY: Zwischen `fork` und `exec` laufen nur `setsid` und `ioctl` – beide async-signal-safe. Die
        // Standardkanäle sind dann schon das Terminal: Es wird das steuernde Terminal der neuen Sitzung.
        unsafe {
            command.pre_exec(|| {
                if libc::setsid() < 0 || libc::ioctl(0, libc::TIOCSCTTY as _, 0) < 0 {
                    return Err(io::Error::last_os_error());
                }

                Ok(())
            });
        }

        let child = command.spawn()?;
        // Die Enden des Terminals stecken in `command`; mit ihm fallen sie, nur die Shell hält sie noch.
        drop(command);
        drop(spawning);
        let process = Arc::new(Process {
            pid: libc::pid_t::try_from(child.id()).map_err(io::Error::other)?,
            reaped: Mutex::new(false),
        });
        // Das Ende holt `wait_in_background` selbst ab; `Child` wird nicht gebraucht.
        drop(child);
        let exit = wait_in_background(Arc::clone(&process));

        // SAFETY: `OwnedFd` besitzt die Nummer, hält sie offen, bis das `AsyncFd` fällt, und nennt immer dieselbe
        // (`as_raw_fd`) – das verlangt `register` (tokio #8587; `AsyncFd::new` ist seit 1.54 abgekündigt).
        let master = Arc::new(unsafe { AsyncFd::register(master) }?);
        let input = write_in_background(Arc::clone(&master));
        Ok((
            Pty {
                master: Arc::clone(&master),
                input,
                process,
                exit,
            },
            PtyReader {
                master,
                buffer: vec![0; CHUNK],
            },
        ))
    }

    /// Schickt Getipptes an die Shell. Wartet nie: Eine Shell, die gerade nicht liest, hält die Sitzung nicht auf.
    pub(crate) fn write(&self, bytes: &[u8]) -> io::Result<()> {
        self.input
            .send(bytes.to_vec())
            .map_err(|_| io::Error::new(io::ErrorKind::BrokenPipe, "the shell has ended"))
    }

    /// Das Terminal hat eine neue Größe; die Shell bekommt `SIGWINCH`.
    pub(crate) fn resize(&self, cols: u32, rows: u32) -> io::Result<()> {
        let size = window(cols, rows);
        // SAFETY: Der Master ist offen, solange `self` lebt; `size` lebt über den Aufruf hinaus.
        if unsafe {
            libc::ioctl(
                self.master.get_ref().as_raw_fd(),
                libc::TIOCSWINSZ as _,
                &raw const size,
            )
        } < 0
        {
            return Err(io::Error::last_os_error());
        }

        Ok(())
    }

    /// Wartet, bis die Shell endet, und nennt ihren Rückgabewert (`None` nach einem Signal). Lässt sich beliebig
    /// oft fragen.
    pub(crate) async fn wait(&self) -> Option<i32> {
        let mut exit = self.exit.clone();
        match exit.wait_for(Option::is_some).await {
            Ok(code) => code.flatten(),
            Err(_) => None,
        }
    }
}

impl Drop for Pty {
    fn drop(&mut self) {
        let process = Arc::clone(&self.process);
        let exit = self.exit.clone();
        process.hang_up();
        // Wer `SIGHUP` übergeht, bekommt nach einer Weile `SIGKILL` – aber nur die Shell selbst.
        std::thread::spawn(move || {
            let deadline = Instant::now() + HANGUP_GRACE;
            while exit.borrow().is_none() && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(50));
            }

            process.signal(libc::SIGKILL);
        });
    }
}

impl PtyReader {
    /// Das nächste Stück Ausgabe; `None`, wenn das Terminal zu ist.
    pub(crate) async fn read(&mut self) -> Option<Vec<u8>> {
        loop {
            let mut ready = self.master.readable().await.ok()?;
            let buffer = &mut self.buffer;
            // SAFETY: Der Puffer gehört uns und ist so lang wie angegeben.
            let read = ready.try_io(|master| {
                let count =
                    unsafe { libc::read(master.get_ref().as_raw_fd(), buffer.as_mut_ptr().cast(), buffer.len()) };
                usize::try_from(count).map_err(|_| io::Error::last_os_error())
            });
            match read {
                // Linux meldet das Ende mit `EIO`, sobald niemand mehr das Terminal offen hält.
                Ok(Ok(0) | Err(_)) => return None,
                Ok(Ok(count)) => return Some(self.buffer[..count].to_vec()),
                Err(_would_block) => {}
            }
        }
    }
}

impl Process {
    /// `SIGHUP` und `SIGCONT` an alles in der Sitzung der Shell.
    fn hang_up(&self) {
        let reaped = self.reaped.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if !*reaped {
            // SAFETY: Die Shell ist noch nicht abgeholt; ihre Nummer ist die ihrer Sitzung und Prozessgruppe.
            unsafe {
                libc::killpg(self.pid, libc::SIGHUP);
                libc::killpg(self.pid, libc::SIGCONT);
            }
        }

        drop(reaped);
        for member in members_of_session(self.pid) {
            hang_up_member(member, self.pid);
        }
    }

    /// Ein Signal an die Shell selbst – nur, solange ihr Ende nicht abgeholt ist.
    fn signal(&self, signal: libc::c_int) {
        let reaped = self.reaped.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if !*reaped {
            // SAFETY: Solange die Shell nicht abgeholt ist, gehört die Nummer ihr.
            unsafe { libc::kill(self.pid, signal) };
        }
    }
}

/// `SIGHUP` und `SIGCONT` an einen Prozess, den die Liste der Sitzung nannte. Er kann inzwischen geendet und
/// seine Nummer einem fremden Prozess überlassen haben (Hinweis von Perplexity, 2026-11-06): Unter Linux hält
/// erst ein pidfd ihn fest, danach wird noch einmal nachgesehen, ob er zur Sitzung gehört – ein Signal über den
/// pidfd erreicht nie einen anderen. Ohne pidfd (Linux vor 5.3) bleibt nur das Nachsehen direkt vor `kill`.
fn hang_up_member(pid: libc::pid_t, session: libc::pid_t) {
    let in_session = || {
        std::fs::read_to_string(format!("/proc/{pid}/stat"))
            .ok()
            .and_then(|stat| session_of(&stat))
            == Some(session)
    };

    #[cfg(target_os = "linux")]
    {
        // SAFETY: Ein Systemaufruf ohne Zeiger; das Ergebnis ist eine neue Nummer oder −1.
        let fd = unsafe { libc::syscall(libc::SYS_pidfd_open, pid, 0) };
        if let Ok(fd) = libc::c_int::try_from(fd)
            && fd >= 0
        {
            // SAFETY: frisch geöffnet, gehört nur uns.
            let fd = unsafe { OwnedFd::from_raw_fd(fd) };
            if in_session() {
                for signal in [libc::SIGHUP, libc::SIGCONT] {
                    // SAFETY: `fd` ist offen; ohne `siginfo` und Flags.
                    unsafe {
                        libc::syscall(
                            libc::SYS_pidfd_send_signal,
                            fd.as_raw_fd(),
                            signal,
                            std::ptr::null::<libc::siginfo_t>(),
                            0,
                        );
                    }
                }
            }

            return;
        }

        if io::Error::last_os_error().raw_os_error() != Some(libc::ENOSYS) {
            // Der Prozess ist schon weg (oder nicht zu fassen): nichts zu tun.
            return;
        }
    }

    if in_session() {
        // SAFETY: Nur Signale an einen Prozess, der eben noch zur Sitzung der Shell gehörte.
        unsafe {
            libc::kill(pid, libc::SIGHUP);
            libc::kill(pid, libc::SIGCONT);
        }
    }
}

/// Die Prozesse der Sitzung `session` (außer dem Kern selbst) – unter Linux aus `/proc`, anderswo keine.
fn members_of_session(session: libc::pid_t) -> Vec<libc::pid_t> {
    let Ok(entries) = std::fs::read_dir("/proc") else {
        return Vec::new();
    };

    let own = libc::pid_t::try_from(std::process::id()).unwrap_or(0);
    entries
        .flatten()
        .filter_map(|entry| entry.file_name().to_str()?.parse::<libc::pid_t>().ok())
        .filter(|pid| *pid != own)
        .filter(|pid| {
            std::fs::read_to_string(format!("/proc/{pid}/stat"))
                .ok()
                .and_then(|stat| session_of(&stat))
                == Some(session)
        })
        .collect()
}

/// Die Sitzung aus einer Zeile von `/proc/<pid>/stat`: das vierte Feld nach dem Namen in Klammern (der Name kann
/// selbst Klammern und Leerzeichen enthalten, deshalb ab der letzten).
fn session_of(stat: &str) -> Option<libc::pid_t> {
    let (_, rest) = stat.rsplit_once(')')?;
    rest.split_whitespace().nth(3)?.parse().ok()
}

fn window(cols: u32, rows: u32) -> libc::winsize {
    let (cols, rows) = clamp_size(cols, rows);
    libc::winsize {
        ws_row: rows,
        ws_col: cols,
        ws_xpixel: 0,
        ws_ypixel: 0,
    }
}

/// Ein neues Pseudo-Terminal in dieser Größe: Master (nicht blockierend) und Slave, beide nicht vererbbar.
fn open(size: (u32, u32)) -> io::Result<(OwnedFd, OwnedFd)> {
    let size = window(size.0, size.1);
    let (master, slave) = pair(&size)?;

    // SAFETY: `master` ist offen.
    unsafe {
        let flags = libc::fcntl(master.as_raw_fd(), libc::F_GETFL);
        if flags < 0 || libc::fcntl(master.as_raw_fd(), libc::F_SETFL, flags | libc::O_NONBLOCK) < 0 {
            return Err(io::Error::last_os_error());
        }
    }

    Ok((master, slave))
}

/// Master und Slave, schon beim Öffnen mit `O_CLOEXEC`: Zwischen einem `openpty` und dem nachträglichen
/// `FD_CLOEXEC` erbte jeder Prozess, den ein anderer Thread gerade startet, beide Enden – und das Terminal endete
/// nicht mehr, solange er lief (Hinweis von Perplexity, 2026-11-06). `TIOCGPTPEER` braucht Linux 4.13; davor
/// öffnet der Kern den Slave über seinen Namen.
#[cfg(target_os = "linux")]
fn pair(size: &libc::winsize) -> io::Result<(OwnedFd, OwnedFd)> {
    let flags = libc::O_RDWR | libc::O_NOCTTY | libc::O_CLOEXEC;
    // SAFETY: Ohne Zeiger; das Ergebnis ist eine neue Nummer oder −1.
    let master = unsafe { libc::posix_openpt(flags) };
    if master < 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: frisch geöffnet, gehört nur uns.
    let master = unsafe { OwnedFd::from_raw_fd(master) };
    // SAFETY: `master` ist offen.
    if unsafe { libc::grantpt(master.as_raw_fd()) } < 0 || unsafe { libc::unlockpt(master.as_raw_fd()) } < 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: `master` ist offen; die Flags gelten für den neuen Slave.
    let mut slave = unsafe { libc::ioctl(master.as_raw_fd(), libc::TIOCGPTPEER as _, flags) };
    if slave < 0 {
        let mut name = [0 as libc::c_char; 128];
        // SAFETY: Der Puffer gehört uns und ist so lang wie angegeben; `ptsname_r` schreibt mit NUL am Ende.
        let failed = unsafe { libc::ptsname_r(master.as_raw_fd(), name.as_mut_ptr(), name.len()) };
        if failed != 0 {
            return Err(io::Error::from_raw_os_error(failed));
        }

        // SAFETY: `name` ist nullterminiert.
        slave = unsafe { libc::open(name.as_ptr(), flags) };
        if slave < 0 {
            return Err(io::Error::last_os_error());
        }
    }

    // SAFETY: frisch geöffnet, gehört nur uns.
    let slave = unsafe { OwnedFd::from_raw_fd(slave) };
    // SAFETY: `master` ist offen, `size` lebt hier.
    if unsafe { libc::ioctl(master.as_raw_fd(), libc::TIOCSWINSZ as _, std::ptr::from_ref(size)) } < 0 {
        return Err(io::Error::last_os_error());
    }

    Ok((master, slave))
}

/// Die BSDs: `openpty`, dann `FD_CLOEXEC`. OpenBSD nimmt `O_CLOEXEC` bei `posix_openpt` nicht an. Das Zeitfenster
/// dazwischen schließt [`SPAWNING`] für die eigenen Terminals; ein Prozess, den anderswo ein Thread startet, kann
/// hier noch erben.
#[cfg(not(target_os = "linux"))]
fn pair(size: &libc::winsize) -> io::Result<(OwnedFd, OwnedFd)> {
    let (mut master, mut slave) = (-1, -1);
    // SAFETY: Alle Zeiger zeigen auf Speicher hier; Name und Einstellungen braucht es nicht.
    if unsafe {
        libc::openpty(
            &raw mut master,
            &raw mut slave,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::from_ref(size).cast_mut(),
        )
    } < 0
    {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: Beide sind frisch geöffnet und gehören nur uns.
    let (master, slave) = unsafe { (OwnedFd::from_raw_fd(master), OwnedFd::from_raw_fd(slave)) };
    for fd in [&master, &slave] {
        // SAFETY: `fd` ist offen.
        if unsafe { libc::fcntl(fd.as_raw_fd(), libc::F_SETFD, libc::FD_CLOEXEC) } < 0 {
            return Err(io::Error::last_os_error());
        }
    }

    Ok((master, slave))
}

/// Schreibt, was die Sitzung schickt, ins Terminal – so schnell, wie die Shell es abnimmt. Endet, wenn der
/// [`Pty`] fällt oder das Terminal zu ist.
fn write_in_background(master: Arc<AsyncFd<OwnedFd>>) -> mpsc::UnboundedSender<Vec<u8>> {
    let (sender, mut receiver) = mpsc::unbounded_channel::<Vec<u8>>();
    tokio::spawn(async move {
        while let Some(bytes) = receiver.recv().await {
            let mut written = 0;
            while written < bytes.len() {
                let Ok(mut ready) = master.writable().await else {
                    return;
                };
                let rest = &bytes[written..];
                // SAFETY: `rest` lebt über den Aufruf hinaus und ist so lang wie angegeben.
                let write = ready.try_io(|master| {
                    let count = unsafe { libc::write(master.get_ref().as_raw_fd(), rest.as_ptr().cast(), rest.len()) };
                    usize::try_from(count).map_err(|_| io::Error::last_os_error())
                });
                match write {
                    Ok(Ok(count)) => written += count,
                    Ok(Err(_)) => return,
                    Err(_would_block) => {}
                }
            }
        }
    });
    sender
}

/// Wartet auf das Ende der Shell, ohne es gleich abzuholen (`WNOWAIT`); abgeholt wird erst unter der Sperre, unter
/// der auch Signale an die Shell gehen.
fn wait_in_background(process: Arc<Process>) -> watch::Receiver<Option<Option<i32>>> {
    let (sender, receiver) = watch::channel(None);
    std::thread::spawn(move || {
        loop {
            // SAFETY: Reine Daten, von `waitid` gefüllt.
            let mut info: libc::siginfo_t = unsafe { std::mem::zeroed() };
            // SAFETY: Die Shell ist ein Kind dieses Prozesses; `info` lebt hier.
            let waited = unsafe {
                libc::waitid(
                    libc::P_PID,
                    process.pid as libc::id_t,
                    &raw mut info,
                    libc::WEXITED | libc::WNOWAIT,
                )
            };
            if waited == 0 || io::Error::last_os_error().kind() != io::ErrorKind::Interrupted {
                break;
            }
        }

        let mut reaped = process.reaped.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let mut status = 0;
        // SAFETY: Die Shell ist ein Kind dieses Prozesses und noch nicht abgeholt.
        let code = (unsafe { libc::waitpid(process.pid, &raw mut status, 0) } == process.pid
            && libc::WIFEXITED(status))
        .then(|| libc::WEXITSTATUS(status));
        *reaped = true;
        drop(reaped);
        let _ = sender.send(Some(code));
    });
    receiver
}

#[cfg(test)]
mod tests {
    use std::ffi::OsString;
    use std::path::PathBuf;
    use std::time::{Duration, Instant};

    use super::*;
    use crate::local::environment;

    const PATIENCE: Duration = Duration::from_secs(30);

    /// Liest, bis die Ausgabe `needle` enthält; danach steht alles Gelesene in der Rückgabe.
    async fn until(reader: &mut PtyReader, needle: &str) -> String {
        let mut seen = Vec::new();
        let deadline = Instant::now() + PATIENCE;
        while !String::from_utf8_lossy(&seen).contains(needle) {
            let left = deadline.saturating_duration_since(Instant::now());
            match tokio::time::timeout(left, reader.read()).await {
                Ok(Some(bytes)) => seen.extend(bytes),
                _ => panic!("no {needle:?} in {:?}", String::from_utf8_lossy(&seen)),
            }
        }

        String::from_utf8_lossy(&seen).into_owned()
    }

    /// `/bin/sh` mit eigenem Heimatordner im Temp: Als Anmelde-Shell liest es sonst das `.profile` des Benutzers.
    fn sh(args: &[&str]) -> (Shell, Vec<(OsString, OsString)>, PathBuf) {
        let home = std::env::temp_dir().join(format!("nexus-pty-{}-{}", std::process::id(), rand::random::<u32>()));
        std::fs::create_dir_all(&home).unwrap();
        let mut env: Vec<(OsString, OsString)> = environment()
            .into_iter()
            .filter(|(name, _)| name != "HOME" && name != "ENV")
            .collect();
        env.push(("HOME".into(), home.clone().into_os_string()));
        let shell = Shell {
            id: "/bin/sh".into(),
            name: "sh".into(),
            program: PathBuf::from("/bin/sh"),
            args: args.iter().map(OsString::from).collect(),
        };
        (shell, env, home)
    }

    fn gone(pid: libc::pid_t) -> bool {
        // Weg ist auch, was nur noch als Zombie auf sein Abholen wartet.
        match std::fs::read_to_string(format!("/proc/{pid}/stat")) {
            Ok(stat) => stat
                .rsplit_once(')')
                .is_some_and(|(_, rest)| rest.trim_start().starts_with('Z')),
            // SAFETY: nur eine Frage, kein Signal.
            Err(_) => (unsafe { libc::kill(pid, 0) }) != 0,
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_shell_in_a_pty_answers_and_reports_its_exit_code() {
        let (shell, env, home) = sh(&[]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (100, 30), &env).unwrap();
        // Das Echo zeigt die Rechnung, erst die Shell macht daraus 4711.
        pty.write(b"echo $((4700 + 11))\n").unwrap();
        until(&mut reader, "4711").await;

        pty.write(b"exit 3\n").unwrap();
        assert_eq!(tokio::time::timeout(PATIENCE, pty.wait()).await.unwrap(), Some(3));
        assert_eq!(pty.wait().await, Some(3));
        drop((pty, reader));
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_size_of_the_pty_reaches_the_shell() {
        let (shell, env, home) = sh(&[]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (100, 30), &env).unwrap();
        pty.write(b"echo size=$(stty size)\n").unwrap();
        until(&mut reader, "size=30 100").await;

        pty.resize(120, 40).unwrap();
        pty.write(b"echo size=$(stty size)\n").unwrap();
        until(&mut reader, "size=40 120").await;
        drop((pty, reader));
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_shell_is_a_login_shell_in_its_folder_with_its_terminal() {
        let (shell, env, home) = sh(&[]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (100, 30), &env).unwrap();
        pty.write(b"echo \"at=$(pwd) zero=$0 term=$TERM tty=$(tty)\"\n")
            .unwrap();
        let seen = until(&mut reader, "tty=/dev/").await;
        assert!(
            seen.contains(&format!("at={}", home.canonicalize().unwrap().display())),
            "{seen}"
        );
        assert!(seen.contains("zero=-sh"), "{seen}");
        assert!(seen.contains("term=xterm-256color"), "{seen}");
        drop((pty, reader));
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_shell_that_ends_at_once_is_closed_cleanly() {
        let (shell, env, home) = sh(&["-c", "exit 7"]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (80, 24), &env).unwrap();
        assert_eq!(tokio::time::timeout(PATIENCE, pty.wait()).await.unwrap(), Some(7));

        let _ = pty.write(b"echo late\n");
        let _ = pty.resize(90, 20);
        let started = Instant::now();
        drop(pty);
        while tokio::time::timeout(Duration::from_secs(5), reader.read())
            .await
            .unwrap()
            .is_some()
        {}
        assert!(started.elapsed() < Duration::from_secs(5));
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn dropping_the_pty_ends_what_the_shell_started() {
        let (shell, env, home) = sh(&[]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (100, 30), &env).unwrap();
        let shell_pid = pty.process.pid;
        pty.write(b"sleep 100 &\n").unwrap();
        pty.write(b"echo \"pid=$!.\"\n").unwrap();
        let seen = until(&mut reader, ".\r\n").await;
        let sleeper: libc::pid_t = seen
            .rsplit("pid=")
            .next()
            .and_then(|rest| rest.split('.').next())
            .and_then(|pid| pid.parse().ok())
            .unwrap_or_else(|| panic!("no pid in {seen:?}"));
        assert!(!gone(sleeper));

        drop(pty);
        drop(reader);
        let deadline = Instant::now() + Duration::from_secs(10);
        while !(gone(sleeper) && gone(shell_pid)) {
            assert!(Instant::now() < deadline, "sleep {sleeper} or the shell still runs");
            tokio::time::sleep(Duration::from_millis(50)).await;
        }

        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn closing_while_the_shell_floods_output_does_not_hang() {
        let (shell, env, home) = sh(&[]);
        let (pty, mut reader) = Pty::spawn(&shell, Some(&home), (100, 30), &env).unwrap();
        let shell_pid = pty.process.pid;
        pty.write(b"while :; do echo 0123456789abcdefghij0123456789abcdefghij; done\n")
            .unwrap();
        until(&mut reader, "abcdefghij0123456789abcdefghij").await;

        // Niemand liest mehr: Das Terminal ist bald voll, und die Shell steht beim Schreiben.
        tokio::time::sleep(Duration::from_millis(300)).await;
        let started = Instant::now();
        drop(pty);
        drop(reader);
        assert!(started.elapsed() < Duration::from_secs(2));
        let deadline = Instant::now() + Duration::from_secs(10);
        while !gone(shell_pid) {
            assert!(Instant::now() < deadline, "the shell still runs");
            tokio::time::sleep(Duration::from_millis(50)).await;
        }

        let _ = std::fs::remove_dir_all(home);
    }

    #[test]
    fn the_session_of_a_process_is_read_behind_its_name() {
        assert_eq!(session_of("42 (a) b) c) S 1 42 4711 34816 …"), Some(4711));
        assert_eq!(session_of("42 (bash) S 1 42"), None);
        assert_eq!(session_of("kaputt"), None);
    }
}
