//! Das Pseudo-Terminal unter Windows: eine Pseudokonsole (ConPTY) über zwei anonyme Pipes.
//!
//! Die Shell läuft in einem Job-Objekt mit `KILL_ON_JOB_CLOSE`: Was sie startet, auch mit `start /b` im
//! Hintergrund, endet mit dem Tab. Bekannte Fallen von ConPTY, die hier abgefangen sind:
//! - Der Kindprozess bekäme sonst die umgeleiteten Standardkanäle des Kerns statt der Pseudokonsole – deshalb
//!   `STARTF_USESTDHANDLES` mit ungültigen Handles und keine vererbten Handles.
//! - Die Ausgabe-Pipe endet nicht, wenn die Shell endet, sondern erst mit `ClosePseudoConsole`; und das wartet
//!   vor Windows 11 24H2, bis die Ausgabe gelesen ist. Beim Schließen liest der Lesethread deshalb weiter (und
//!   verwirft), bis die Pipe endet; geschlossen wird in einem eigenen Thread, der niemanden aufhält.
//! - Lesen und Schreiben blockieren; beides läuft in eigenen Threads. Der Kanal zum Leser ist begrenzt
//!   ([`CHUNKS`] × [`CHUNK`]): Ist er voll, wartet der Lesethread, die Pseudokonsole und mit ihr die Shell – das
//!   ist die Bremse, wenn das Terminal nicht nachkommt.

use std::ffi::{OsStr, OsString};
use std::fs::File;
use std::io::{self, Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use tokio::sync::{mpsc, watch};
use windows_sys::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE, S_OK, WAIT_OBJECT_0};
use windows_sys::Win32::System::Console::{COORD, ClosePseudoConsole, CreatePseudoConsole, HPCON, ResizePseudoConsole};
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation, SetInformationJobObject,
    TerminateJobObject,
};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, CreateProcessW, DeleteProcThreadAttributeList,
    EXTENDED_STARTUPINFO_PRESENT, GetExitCodeProcess, INFINITE, InitializeProcThreadAttributeList,
    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, PROCESS_INFORMATION, ResumeThread, STARTF_USESTDHANDLES, STARTUPINFOEXW,
    TerminateProcess, UpdateProcThreadAttribute, WaitForSingleObject,
};

use super::{Shell, clamp_size, command_line};

/// So viel liest der Lesethread auf einmal.
const CHUNK: usize = 64 * 1024;

/// So viele Stücke liegen höchstens zwischen Lesethread und Sitzung.
const CHUNKS: usize = 8;

/// So oft sieht der Lesethread nach, ob der volle Kanal wieder Platz hat (oder geschlossen wird).
const FULL_PAUSE: Duration = Duration::from_millis(5);

/// Eine Shell in einer Pseudokonsole.
pub(crate) struct Pty {
    console: Option<Console>,
    job: Option<OwnedHandle>,
    input: std::sync::mpsc::Sender<Vec<u8>>,
    exit: watch::Receiver<Option<Option<i32>>>,
    closing: Arc<AtomicBool>,
}

/// Was die Shell ausgibt; `None` heißt: Die Pseudokonsole ist geschlossen.
pub(crate) struct PtyReader {
    output: mpsc::Receiver<Vec<u8>>,
}

/// Die Pseudokonsole selbst; geschlossen wird sie nur über [`Pty::close`].
struct Console(HPCON);

impl Pty {
    /// Startet die Shell in `dir` (sonst im Arbeitsverzeichnis des Kerns) mit genau der Umgebung `env`.
    pub(crate) fn spawn(
        shell: &Shell,
        dir: Option<&Path>,
        size: (u32, u32),
        env: &[(OsString, OsString)],
    ) -> io::Result<(Pty, PtyReader)> {
        let (input_read, input_write) = pipe()?;
        let (output_read, output_write) = pipe()?;
        let (cols, rows) = clamp_size(size.0, size.1);
        let mut console: HPCON = 0;
        // SAFETY: Beide Handles sind offen und gehören uns; die Pseudokonsole dupliziert sie.
        let result = unsafe {
            CreatePseudoConsole(
                coord(cols, rows),
                input_read.as_raw_handle(),
                output_write.as_raw_handle(),
                0,
                &raw mut console,
            )
        };
        if result != S_OK {
            return Err(io::Error::from_raw_os_error(result));
        }

        let console = Console(console);
        // Die Enden der Pseudokonsole gehören jetzt ihr; behielte der Kern sie, endete die Ausgabe nie.
        drop((input_read, output_write));

        let job = job()?;
        let process = start(shell, dir, env, &console, &job)?;

        let closing = Arc::new(AtomicBool::new(false));
        let (sender, output) = mpsc::channel(CHUNKS);
        read_in_background(File::from(output_read), sender, Arc::clone(&closing));
        let input = write_in_background(File::from(input_write));
        let exit = wait_in_background(process);

        Ok((
            Pty {
                console: Some(console),
                job: Some(job),
                input,
                exit,
                closing,
            },
            PtyReader { output },
        ))
    }

    /// Schickt Getipptes an die Shell. Wartet nie: Geschrieben wird in einem eigenen Thread, damit eine Shell, die
    /// gerade nicht liest, die Sitzung nicht aufhält.
    pub(crate) fn write(&self, bytes: &[u8]) -> io::Result<()> {
        self.input
            .send(bytes.to_vec())
            .map_err(|_| io::Error::new(io::ErrorKind::BrokenPipe, "the shell has ended"))
    }

    /// Das Terminal hat eine neue Größe.
    pub(crate) fn resize(&self, cols: u32, rows: u32) -> io::Result<()> {
        let Some(Console(console)) = &self.console else {
            return Ok(());
        };

        let (cols, rows) = clamp_size(cols, rows);
        // SAFETY: Die Pseudokonsole ist offen, solange `self` sie hält.
        let result = unsafe { ResizePseudoConsole(*console, coord(cols, rows)) };
        if result == S_OK {
            Ok(())
        } else {
            Err(io::Error::from_raw_os_error(result))
        }
    }

    /// Wartet, bis die Shell endet, und nennt ihren Rückgabewert. Lässt sich beliebig oft fragen.
    pub(crate) async fn wait(&self) -> Option<i32> {
        let mut exit = self.exit.clone();
        match exit.wait_for(Option::is_some).await {
            Ok(code) => code.flatten(),
            Err(_) => None,
        }
    }

    /// Beendet alles, was in der Pseudokonsole läuft, und schließt sie – in einem eigenen Thread, dessen Ende
    /// die Rückgabe nennt. [`Drop`] tut dasselbe, ohne darauf zu warten – nur die Tests warten.
    #[cfg(test)]
    fn close(mut self) -> std::thread::JoinHandle<()> {
        self.shut_down()
    }

    fn shut_down(&mut self) -> std::thread::JoinHandle<()> {
        // Ab jetzt verwirft der Lesethread, was kommt – sonst wartete `ClosePseudoConsole` auf einen Leser,
        // den es nicht mehr gibt.
        self.closing.store(true, Ordering::Relaxed);
        let (console, job) = (self.console.take(), self.job.take());
        std::thread::spawn(move || {
            if let Some(job) = &job {
                // SAFETY: Der Job gehört uns. Er beendet die Shell und alles, was sie gestartet hat.
                unsafe { TerminateJobObject(job.as_raw_handle(), 1) };
            }

            if let Some(Console(console)) = console {
                // SAFETY: Die Pseudokonsole wird genau hier und genau einmal geschlossen.
                unsafe { ClosePseudoConsole(console) };
            }

            // Mit dem letzten Handle endet der Job; `KILL_ON_JOB_CLOSE` fängt ab, was dazwischen noch entstand.
            drop(job);
        })
    }

    /// Nur für Tests: eine zweite Hand am Job-Objekt, um nachzusehen, was darin noch läuft.
    #[cfg(test)]
    fn job_for_tests(&self) -> OwnedHandle {
        self.job.as_ref().expect("job").try_clone().expect("duplicate the job")
    }
}

impl Drop for Pty {
    fn drop(&mut self) {
        if self.console.is_some() || self.job.is_some() {
            drop(self.shut_down());
        }
    }
}

impl PtyReader {
    /// Das nächste Stück Ausgabe; `None`, wenn die Pseudokonsole geschlossen ist.
    pub(crate) async fn read(&mut self) -> Option<Vec<u8>> {
        self.output.recv().await
    }
}

fn coord(cols: u16, rows: u16) -> COORD {
    // `clamp_size` hält beide unter 32768.
    COORD {
        X: i16::try_from(cols).unwrap_or(i16::MAX),
        Y: i16::try_from(rows).unwrap_or(i16::MAX),
    }
}

/// Eine anonyme Pipe: Leseende, Schreibende. Nicht vererbbar.
fn pipe() -> io::Result<(OwnedHandle, OwnedHandle)> {
    let (mut read, mut write): (HANDLE, HANDLE) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: Beide Zeiger zeigen auf Speicher hier; ohne Sicherheitsangaben sind die Handles nicht vererbbar.
    if unsafe { CreatePipe(&raw mut read, &raw mut write, std::ptr::null(), 0) } == 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: Beide Handles sind frisch und gehören nur uns.
    Ok(unsafe { (OwnedHandle::from_raw_handle(read), OwnedHandle::from_raw_handle(write)) })
}

/// Ein Job-Objekt, das beim Schließen seines letzten Handles alles darin beendet.
fn job() -> io::Result<OwnedHandle> {
    // SAFETY: Ohne Namen und Sicherheitsangaben; das Handle gehört danach uns.
    let handle = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
    if handle.is_null() {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: frisch angelegt, gehört nur uns.
    let job = unsafe { OwnedHandle::from_raw_handle(handle) };
    // SAFETY: Die Struktur ist reine Daten; Nullen heißen „keine Grenze“.
    let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    // SAFETY: Zeiger und Größe gehören zur Struktur oben.
    let set = unsafe {
        SetInformationJobObject(
            job.as_raw_handle(),
            JobObjectExtendedLimitInformation,
            (&raw const limits).cast(),
            size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        )
    };
    if set == 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(job)
}

/// Startet die Shell angehalten in der Pseudokonsole, steckt sie in den Job und lässt sie dann laufen. Zurück
/// kommt das Handle des Prozesses.
fn start(
    shell: &Shell,
    dir: Option<&Path>,
    env: &[(OsString, OsString)],
    console: &Console,
    job: &OwnedHandle,
) -> io::Result<OwnedHandle> {
    let mut attributes = Attributes::with_console(console)?;
    let mut line = command_line(&shell.program, &shell.args_in(dir));
    line.push(0);
    let application = terminated(shell.program.as_os_str());
    let directory = dir.map(|dir| terminated(dir.as_os_str()));
    let environment = environment_block(env);

    // SAFETY: Reine Daten; Nullen sind ein gültiger Anfang.
    let mut startup: STARTUPINFOEXW = unsafe { std::mem::zeroed() };
    startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    // Ohne das bekäme die Shell die Standardkanäle des Kerns statt der Pseudokonsole.
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = INVALID_HANDLE_VALUE;
    startup.StartupInfo.hStdOutput = INVALID_HANDLE_VALUE;
    startup.StartupInfo.hStdError = INVALID_HANDLE_VALUE;
    startup.lpAttributeList = attributes.list();

    // SAFETY: Reine Daten, von `CreateProcessW` gefüllt.
    let mut process: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: Alle Texte sind nullterminiert und leben über den Aufruf hinaus; die Befehlszeile ist veränderbar,
    // wie `CreateProcessW` es verlangt. Keine Handles werden vererbt.
    let created = unsafe {
        CreateProcessW(
            application.as_ptr(),
            line.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
            environment.as_ptr().cast(),
            directory
                .as_ref()
                .map_or(std::ptr::null(), |directory| directory.as_ptr()),
            &raw const startup.StartupInfo,
            &raw mut process,
        )
    };
    if created == 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: Beide Handles sind frisch und gehören nur uns.
    let (handle, thread) = unsafe {
        (
            OwnedHandle::from_raw_handle(process.hProcess),
            OwnedHandle::from_raw_handle(process.hThread),
        )
    };
    // SAFETY: Job und Prozess sind offen; der Prozess ist angehalten und hat noch nichts gestartet.
    if unsafe { AssignProcessToJobObject(job.as_raw_handle(), handle.as_raw_handle()) } == 0 {
        let error = io::Error::last_os_error();
        // SAFETY: Der Prozess gehört uns und lief nie.
        unsafe { TerminateProcess(handle.as_raw_handle(), 1) };
        return Err(error);
    }

    // SAFETY: Der Thread ist der erste des Prozesses, angehalten durch `CREATE_SUSPENDED`.
    if unsafe { ResumeThread(thread.as_raw_handle()) } == u32::MAX {
        let error = io::Error::last_os_error();
        // SAFETY: wie oben.
        unsafe { TerminateProcess(handle.as_raw_handle(), 1) };
        return Err(error);
    }

    Ok(handle)
}

/// Die Liste der Attribute für `CreateProcessW` mit der Pseudokonsole darin.
struct Attributes {
    // `usize` statt `u8`: Die Liste will ausgerichteten Speicher.
    buffer: Vec<usize>,
}

impl Attributes {
    fn with_console(console: &Console) -> io::Result<Self> {
        let mut size = 0;
        // SAFETY: Erste Frage nur nach der Größe; scheitert erwartungsgemäß mit „zu klein“.
        unsafe { InitializeProcThreadAttributeList(std::ptr::null_mut(), 1, 0, &raw mut size) };
        let mut attributes = Self {
            buffer: vec![0; size.div_ceil(size_of::<usize>()).max(1)],
        };
        // SAFETY: Der Puffer ist mindestens `size` Bytes groß.
        if unsafe { InitializeProcThreadAttributeList(attributes.list(), 1, 0, &raw mut size) } == 0 {
            return Err(io::Error::last_os_error());
        }

        // SAFETY: Die Liste ist eingerichtet; für die Pseudokonsole ist der Wert das Handle selbst, nicht ein
        // Zeiger darauf. Die Pseudokonsole lebt länger als die Liste.
        let updated = unsafe {
            UpdateProcThreadAttribute(
                attributes.list(),
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                console.0 as *const std::ffi::c_void,
                size_of::<HPCON>(),
                std::ptr::null_mut(),
                std::ptr::null(),
            )
        };
        if updated == 0 {
            let error = io::Error::last_os_error();
            // SAFETY: eingerichtet, also auch abzubauen.
            unsafe { DeleteProcThreadAttributeList(attributes.list()) };
            attributes.buffer.clear();
            return Err(error);
        }

        Ok(attributes)
    }

    fn list(&mut self) -> *mut std::ffi::c_void {
        self.buffer.as_mut_ptr().cast()
    }
}

impl Drop for Attributes {
    fn drop(&mut self) {
        if !self.buffer.is_empty() {
            // SAFETY: Die Liste wurde eingerichtet und wird genau hier abgebaut.
            unsafe { DeleteProcThreadAttributeList(self.list()) };
        }
    }
}

fn terminated(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain([0]).collect()
}

/// Der Umgebungsblock für `CreateProcessW`: `NAME=Wert` je Eintrag, nach Namen ohne Rücksicht auf die Schreibung
/// sortiert (so erwartet Windows ihn), mit einem leeren Eintrag am Ende. Was ein NUL enthält, fällt weg – es
/// zerschnitte den Block.
fn environment_block(env: &[(OsString, OsString)]) -> Vec<u16> {
    let mut entries: Vec<(String, Vec<u16>)> = env
        .iter()
        .filter(|(name, value)| {
            !name.is_empty() && !name.encode_wide().any(|unit| unit == 0) && !value.encode_wide().any(|unit| unit == 0)
        })
        .map(|(name, value)| {
            let mut entry: Vec<u16> = name.encode_wide().collect();
            entry.push(u16::from(b'='));
            entry.extend(value.encode_wide());
            (name.to_string_lossy().to_uppercase(), entry)
        })
        .collect();
    entries.sort_by(|a, b| a.0.cmp(&b.0));

    let mut block: Vec<u16> = Vec::new();
    for (_, entry) in entries {
        block.extend(entry);
        block.push(0);
    }

    if block.is_empty() {
        block.push(0);
    }

    block.push(0);
    block
}

/// Liest die Ausgabe der Pseudokonsole, bis die Pipe endet. Ist der Kanal voll, wartet der Thread (die Bremse);
/// gibt es niemanden mehr, der liest, oder wird geschlossen, liest er weiter und verwirft – damit
/// `ClosePseudoConsole` nicht auf einen Leser wartet.
fn read_in_background(mut pipe: File, sender: mpsc::Sender<Vec<u8>>, closing: Arc<AtomicBool>) {
    std::thread::spawn(move || {
        let mut buffer = vec![0_u8; CHUNK];
        let mut listening = true;
        loop {
            let count = match pipe.read(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(count) => count,
            };

            let mut pending = Some(buffer[..count].to_vec());
            while let Some(chunk) = pending.take() {
                if !listening || closing.load(Ordering::Relaxed) {
                    break;
                }

                match sender.try_send(chunk) {
                    Ok(()) => {}
                    Err(mpsc::error::TrySendError::Full(back)) => {
                        pending = Some(back);
                        std::thread::sleep(FULL_PAUSE);
                    }
                    Err(mpsc::error::TrySendError::Closed(_)) => listening = false,
                }
            }
        }
    });
}

/// Schreibt, was die Sitzung schickt, in die Pseudokonsole. Endet, wenn der [`Pty`] fällt oder die Pipe bricht.
fn write_in_background(mut pipe: File) -> std::sync::mpsc::Sender<Vec<u8>> {
    let (sender, receiver) = std::sync::mpsc::channel::<Vec<u8>>();
    std::thread::spawn(move || {
        for bytes in receiver {
            if pipe.write_all(&bytes).is_err() {
                break;
            }
        }
    });
    sender
}

/// Wartet auf das Ende der Shell und meldet ihren Rückgabewert.
fn wait_in_background(process: OwnedHandle) -> watch::Receiver<Option<Option<i32>>> {
    let (sender, receiver) = watch::channel(None);
    std::thread::spawn(move || {
        // SAFETY: Das Handle gehört diesem Thread, bis er endet.
        let code = unsafe {
            if WaitForSingleObject(process.as_raw_handle(), INFINITE) != WAIT_OBJECT_0 {
                None
            } else {
                let mut code = 0_u32;
                // Windows nennt Rückgabewerte als `u32` (0xC000013A …); als `i32` gespeichert, gehen sie
                // unverändert zurück.
                (GetExitCodeProcess(process.as_raw_handle(), &raw mut code) != 0).then_some(code as i32)
            }
        };
        let _ = sender.send(Some(code));
    });
    receiver
}

#[cfg(test)]
mod tests {
    use std::ffi::OsString;
    use std::time::{Duration, Instant};

    use windows_sys::Win32::System::JobObjects::{
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, JobObjectBasicAccountingInformation, QueryInformationJobObject,
    };

    use super::*;
    use crate::local::{Shell, environment, shell};

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

    fn cmd() -> Shell {
        shell("cmd").expect("cmd.exe")
    }

    /// Wie viele Prozesse im Job-Objekt noch laufen.
    fn active(job: &OwnedHandle) -> u32 {
        // SAFETY: Die Struktur ist reine Daten, der Job gehört dem Test.
        unsafe {
            let mut info: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = std::mem::zeroed();
            let ok = QueryInformationJobObject(
                job.as_raw_handle(),
                JobObjectBasicAccountingInformation,
                (&raw mut info).cast(),
                size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
                std::ptr::null_mut(),
            );
            assert_ne!(ok, 0, "{}", io::Error::last_os_error());
            info.ActiveProcesses
        }
    }

    fn eventually(what: &str, mut done: impl FnMut() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(10);
        while !done() {
            assert!(Instant::now() < deadline, "{what}");
            std::thread::sleep(Duration::from_millis(50));
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_shell_in_a_pty_answers_and_reports_its_exit_code() {
        let (pty, mut reader) = Pty::spawn(&cmd(), None, (100, 30), &environment()).unwrap();
        // Das Echo zeigt „47^11“, erst cmd macht daraus 4711.
        pty.write(b"echo 47^11\r").unwrap();
        until(&mut reader, "4711").await;

        pty.write(b"exit 3\r").unwrap();
        assert_eq!(tokio::time::timeout(PATIENCE, pty.wait()).await.unwrap(), Some(3));
        // Ein zweites Fragen nennt dasselbe.
        assert_eq!(pty.wait().await, Some(3));
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_size_of_the_pty_reaches_the_shell() {
        let powershell = shell("powershell").expect("Windows PowerShell");
        let (pty, mut reader) = Pty::spawn(&powershell, None, (100, 30), &environment()).unwrap();
        pty.write(b"'w=' + [Console]::WindowWidth\r").unwrap();
        until(&mut reader, "w=100").await;

        pty.resize(120, 40).unwrap();
        pty.write(b"'w=' + [Console]::WindowWidth + ',h=' + [Console]::WindowHeight\r")
            .unwrap();
        until(&mut reader, "w=120,h=40").await;
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_shell_starts_in_the_folder_it_is_given() {
        let folder = std::env::temp_dir().join(format!("nexus-pty-dir {}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        let (pty, mut reader) = Pty::spawn(&cmd(), Some(&folder), (200, 30), &environment()).unwrap();
        pty.write(b"cd\r").unwrap();
        until(&mut reader, &folder.display().to_string()).await;
        drop((pty, reader));
        let _ = std::fs::remove_dir(&folder);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_shell_that_ends_at_once_is_closed_cleanly() {
        let quick = Shell {
            args: ["/c", "exit 7"].map(OsString::from).to_vec(),
            ..cmd()
        };
        let (pty, mut reader) = Pty::spawn(&quick, None, (80, 24), &environment()).unwrap();
        assert_eq!(tokio::time::timeout(PATIENCE, pty.wait()).await.unwrap(), Some(7));

        // Danach geht nichts mehr hinüber, aber nichts scheitert laut, und das Ende ist schnell.
        let _ = pty.write(b"echo late\r");
        let _ = pty.resize(90, 20);
        let started = Instant::now();
        let closed = pty.close();
        while tokio::time::timeout(Duration::from_secs(5), reader.read())
            .await
            .unwrap()
            .is_some()
        {}
        assert!(started.elapsed() < Duration::from_secs(5));
        closed.join().unwrap();
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn dropping_the_pty_ends_what_the_shell_started() {
        let (pty, mut reader) = Pty::spawn(&cmd(), None, (100, 30), &environment()).unwrap();
        let job = pty.job_for_tests();
        pty.write(b"start /b ping -n 100 127.0.0.1 >nul\r").unwrap();
        pty.write(b"echo started^!\r").unwrap();
        until(&mut reader, "started!").await;
        eventually("ping runs beside cmd", || active(&job) >= 2);

        drop(pty);
        drop(reader);
        eventually("nothing of the shell runs any more", || active(&job) == 0);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn closing_while_the_shell_floods_output_does_not_hang() {
        let (pty, mut reader) = Pty::spawn(&cmd(), None, (100, 30), &environment()).unwrap();
        let job = pty.job_for_tests();
        pty.write(b"for /L %i in (1,0,2) do @echo 0123456789abcdefghij0123456789abcdefghij\r")
            .unwrap();
        until(&mut reader, "abcdefghij0123456789abcdefghij").await;

        // Niemand liest mehr: Der Kanal ist bald voll, und die Shell schreibt weiter.
        tokio::time::sleep(Duration::from_millis(300)).await;
        let started = Instant::now();
        let closed = pty.close();
        drop(reader);
        assert!(started.elapsed() < Duration::from_secs(2));

        let (done, finished) = std::sync::mpsc::channel();
        std::thread::spawn(move || done.send(closed.join().is_ok()));
        assert_eq!(finished.recv_timeout(Duration::from_secs(2)), Ok(true));
        eventually("nothing of the shell runs any more", || active(&job) == 0);
    }

    #[test]
    fn the_environment_block_is_sorted_and_ends_twice() {
        let env = [("b", "2"), ("A", "1"), ("bad\0name", "x"), ("C", "with\0nul")]
            .map(|(name, value)| (OsString::from(name), OsString::from(value)));
        assert_eq!(String::from_utf16_lossy(&environment_block(&env)), "A=1\0b=2\0\0");
        assert_eq!(String::from_utf16_lossy(&environment_block(&[])), "\0\0");
    }
}
