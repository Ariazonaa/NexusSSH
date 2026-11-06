//! Die Sitzung „lokal“: eine Shell dieses Rechners statt einer Verbindung.
//!
//! Wie Telnet und die serielle Schnittstelle eine schmale Sitzung: keine Anmeldung, kein gespeichertes Passwort,
//! keine Dateien, keine Auslastung. Gestartet wird nur eine Shell, die [`super::shell`] zur Kennung im Profil
//! findet; der Startordner wird vorher nachgesehen – mit Frist, denn er kann auf einem Netzlaufwerk liegen, das
//! nicht antwortet.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::mpsc::UnboundedReceiver;
use tokio::sync::oneshot;
use tokio::time::{Instant, timeout};

use super::{Pty, PtyReader};
use crate::core::Shared;
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, SessionState};
use crate::session::Command;

/// So lange darf das Nachsehen nach dem Startordner dauern; danach startet die Shell im Ordner des Benutzers.
const FOLDER_PATIENCE: Duration = Duration::from_secs(3);

/// Nach dem Ende der Shell wird noch abgeholt, was sie zuletzt schrieb – bis so lange nichts mehr kommt …
const LAST_WORDS_SILENCE: Duration = Duration::from_millis(200);

/// … und höchstens so lange (ein Hintergrundprogramm kann das Terminal weiter beschreiben).
const LAST_WORDS_LIMIT: Duration = Duration::from_secs(2);

/// Wo die Shell startet.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Folder {
    /// Der gewünschte Ordner; es gibt ihn.
    Wanted(PathBuf),
    /// Den gewünschten Ordner gibt es nicht, oder er antwortete nicht rechtzeitig.
    Missing,
    /// Es wurde keiner gewünscht.
    Unset,
}

/// Sieht nach, ob es den Startordner gibt – in einem eigenen Thread mit Frist: Hängt ein Netzlaufwerk, bleibt
/// nur dieser Thread stehen, nicht die Sitzung und nicht das Ende des Kerns.
pub(crate) async fn start_folder(
    wanted: Option<&str>,
    patience: Duration,
    exists: impl FnOnce(&Path) -> bool + Send + 'static,
) -> Folder {
    let Some(wanted) = wanted else {
        return Folder::Unset;
    };

    let path = PathBuf::from(wanted);
    if !path.is_absolute() {
        return Folder::Missing;
    }

    let (sender, receiver) = oneshot::channel();
    let asked = path.clone();
    std::thread::spawn(move || {
        let _ = sender.send(exists(&asked));
    });
    match timeout(patience, receiver).await {
        Ok(Ok(true)) => Folder::Wanted(path),
        _ => Folder::Missing,
    }
}

/// Eine lokale Sitzung: Shell finden, starten, dann Bytes hin und her, bis die Shell endet (`Ok` mit ihrem
/// Rückgabewert) oder der Benutzer den Tab schließt (dann fällt diese Aufgabe und mit ihr der [`Pty`]).
pub(crate) async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    size: (u32, u32),
    mut commands: UnboundedReceiver<Command>,
) -> Result<Option<u32>, (CloseReason, String)> {
    shared.state(session, SessionState::Connecting, None);
    let id = profile.host.clone();
    // Unter Windows fragt das `wsl.exe` (mit Frist) – nicht im Thread des Runtimes.
    let shell = tokio::task::spawn_blocking(move || super::shell(&id))
        .await
        .ok()
        .flatten()
        .ok_or_else(|| {
            (
                CloseReason::ConnectFailed,
                format!("{:?}: this shell is not available here", profile.host),
            )
        })?;

    let folder = match start_folder(profile.local_dir.as_deref(), FOLDER_PATIENCE, |path| path.is_dir()).await {
        Folder::Wanted(folder) => Some(folder),
        Folder::Missing => {
            shared.emit(Event::Warning {
                code: "local_dir_missing",
                message: format!(
                    "{}: the start folder is missing or did not answer in time; the shell starts in the home folder",
                    profile.name
                ),
            });
            super::home()
        }
        Folder::Unset => super::home(),
    };

    let (pty, mut reader) = Pty::spawn(&shell, folder.as_deref(), size, &super::environment())
        .map_err(|error| (CloseReason::ConnectFailed, format!("{}: {error}", shell.name)))?;
    shared.connected_without_link(session, &profile.id);

    let mut paused = false;
    loop {
        tokio::select! {
            // Angehalten wird nichts abgeholt; der Rückstau bremst die Shell.
            bytes = reader.read(), if !paused => match bytes {
                Some(bytes) => shared.sink.output(session, &bytes),
                // Das Terminal ist zu: Die Shell ist weg oder geht gleich.
                None => return Ok(exit_status(timeout(LAST_WORDS_LIMIT, pty.wait()).await.ok().flatten())),
            },
            code = pty.wait() => {
                last_words(shared, session, &mut reader).await;
                return Ok(exit_status(code));
            }
            command = commands.recv() => match command {
                // Endet die Shell gerade, geht Getipptes ins Leere; ihr Ende kommt gleich über `wait`.
                Some(Command::Input(bytes)) => {
                    let _ = pty.write(&bytes);
                }
                Some(Command::Resize(columns, rows)) => {
                    let _ = pty.resize(columns, rows);
                }
                Some(Command::Flow(pause)) => paused = pause,
                Some(Command::Monitor(true)) => shared.emit(Event::MonitorUnavailable {
                    session,
                    message: "a local terminal has no server to measure".to_owned(),
                    retrying: false,
                }),
                Some(Command::Monitor(false) | Command::Relink) => {}
                // Der Sender lebt, solange die Sitzung eingetragen ist; hierher kommt man praktisch nicht.
                None => std::future::pending().await,
            },
        }
    }
}

/// Holt ab, was die Shell zuletzt schrieb – sonst fehlte das Ende von `cmd /c …`.
async fn last_words(shared: &Shared, session: u64, reader: &mut PtyReader) {
    let deadline = Instant::now() + LAST_WORDS_LIMIT;
    while let Ok(Some(bytes)) = timeout(
        LAST_WORDS_SILENCE.min(deadline.saturating_duration_since(Instant::now())),
        reader.read(),
    )
    .await
    {
        shared.sink.output(session, &bytes);
    }
}

/// Der Rückgabewert, wie das Protokoll ihn nennt. Unter Windows ist er ohnehin ein `u32` (`0xC000013A` …).
fn exit_status(code: Option<i32>) -> Option<u32> {
    code.map(|code| code as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn a_start_folder_that_does_not_answer_is_given_up_after_its_time() {
        let started = std::time::Instant::now();
        let hanging = start_folder(Some(&absolute()), Duration::from_millis(200), |_| {
            std::thread::sleep(Duration::from_secs(5));
            true
        })
        .await;
        assert_eq!(hanging, Folder::Missing);
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    #[tokio::test]
    async fn a_start_folder_is_taken_only_if_it_exists_and_is_absolute() {
        let here = std::env::temp_dir();
        assert_eq!(
            start_folder(here.to_str(), FOLDER_PATIENCE, |path| path.is_dir()).await,
            Folder::Wanted(here.clone())
        );
        let gone = here.join("nexus-gibt-es-nicht-4711");
        assert_eq!(
            start_folder(gone.to_str(), FOLDER_PATIENCE, |path| path.is_dir()).await,
            Folder::Missing
        );
        assert_eq!(
            start_folder(Some("relativ"), FOLDER_PATIENCE, |_| true).await,
            Folder::Missing
        );
        assert_eq!(start_folder(None, FOLDER_PATIENCE, |_| true).await, Folder::Unset);
    }

    #[test]
    fn an_exit_code_of_windows_keeps_its_bits() {
        assert_eq!(exit_status(Some(0xC000_013A_u32 as i32)), Some(0xC000_013A));
        assert_eq!(exit_status(Some(3)), Some(3));
        assert_eq!(exit_status(None), None);
    }

    fn absolute() -> String {
        std::env::temp_dir().display().to_string()
    }
}
