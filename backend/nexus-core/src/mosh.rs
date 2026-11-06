//! Das Terminal eines SSH-Profils über Mosh (UDP), mit der eingecheckten Bibliothek `mosh-client` (siehe dort
//! `PATCHES.md`).
//!
//! Angemeldet wird wie immer über SSH ([`crate::session`]). Auf der stehenden Verbindung startet der Kern
//! `mosh-server new …` – eine feste Zeile aus [`crate::exec::MOSH_START`] und [`start_line`], nie Text des Profils
//! oder des Servers – und liest aus der Antwort Port und Schlüssel ([`connect_record`]). Danach läuft das Terminal
//! über UDP zur Adresse, an der auch die SSH-Verbindung ankam; die SSH-Verbindung bleibt für Dateien, Auslastung
//! und Tunnel. Reißt sie, läuft das Terminal weiter, und der Kern baut sie neu auf, sobald Mosh wieder Antworten
//! bekommt ([`drive`]).
//!
//! **Der Schlüssel der Sitzung steht nirgends:** Er bleibt in der Bibliothek (`Bootstrap`, `Zeroizing`), die
//! Ausgabe von `mosh-server` wird nach dem Lesen überschrieben, und keine Meldung, kein Protokoll und keine Datei
//! nennt ihn. Jeder Treiber einer Sitzung läuft auf einem eigenen Thread mit eigener Laufzeit ([`Terminal`]): Ein
//! feindseliger Server kann je Anweisung Sekunden Rechenzeit kosten – das soll nicht den Kern aufhalten.

use std::future::Future;
use std::net::IpAddr;
use std::pin::Pin;
use std::sync::Arc;
use std::time::{Duration, Instant};

use mosh_client::{
    Bootstrap, BootstrapError, PredictionMode, Session, SessionError, SessionExit, SessionInterruption,
    SessionReachability, SessionState,
};
use russh_sftp::client::SftpSession;
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::sync::{oneshot, watch};
use zeroize::Zeroize;

use crate::core::Shared;
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, MoshState};
use crate::session::{Command, Link};

/// Steht vor der Ausgabe von `mosh-server` (siehe [`crate::exec::MOSH_START`]).
const MARKER: &str = "@nexus-mosh";

/// Die eine Zeile, wenn es am Server kein `mosh-server` gibt.
const MISSING: &str = "@nexus-no-mosh";

/// Die Locales, mit denen `mosh-server` startet – der Reihe nach, die zweite nur, wenn er die erste ablehnt.
pub(crate) const LOCALES: [&str; 2] = ["C.UTF-8", "en_US.UTF-8"];

/// So lehnt `mosh-server` eine Locale ab, die es am Server nicht gibt oder die kein UTF-8 spricht.
const LOCALE_REFUSED: &str = "needs a UTF-8 native locale";

/// So lange darf der Start von `mosh-server` dauern (samt der Frage, ob die Shell POSIX spricht).
const START_LIMIT: Duration = Duration::from_secs(30);

/// So viel Eingabe nimmt die Bibliothek je Befehl; mehr geht in Stücken.
const MAX_INPUT: usize = 64 * 1024;

/// Größer als 500 × 200 Zellen nimmt die Bibliothek kein Terminal an (100 000 Zellen – mit diesen beiden Grenzen
/// ist das von selbst eingehalten).
const MAX_COLUMNS: u32 = 500;
const MAX_ROWS: u32 = 200;

/// So oft sieht die Sitzung nach, ob ihre SSH-Verbindung noch steht – wie eine Sitzung ohne Shell.
const ALIVE_CHECK: Duration = Duration::from_secs(2);

/// Höchstens so oft versucht der Kern von selbst, die SSH-Verbindung neu aufzubauen.
const RELINK_EVERY: Duration = Duration::from_secs(30);

/// So oft meldet eine stumme Sitzung, wie lange sie schon nichts gehört hat.
const SILENCE_REPORT: Duration = Duration::from_secs(5);

/// Die Anfangszeile hinter [`crate::exec::MOSH_START`]: feste Wörter und geprüfte Zahlen. `-s` bindet
/// `mosh-server` an die Adresse, an der die SSH-Verbindung ankam (sonst lauschte er auf allen), `-c 256` sagt ihm,
/// dass das Terminal 256 Farben kann. Eine Locale, die nicht in [`LOCALES`] steht, gibt es hier nicht.
pub(crate) fn start_line(ports: Option<(u16, u16)>, locale: &str) -> String {
    let locale = LOCALES
        .iter()
        .find(|known| **known == locale)
        .copied()
        .unwrap_or(LOCALES[0]);
    let mut line = format!("mosh-server new -s -c 256 -l LANG={locale}");
    match ports {
        Some((from, to)) if from == to => line.push_str(&format!(" -p {from}")),
        Some((from, to)) => line.push_str(&format!(" -p {from}:{to}")),
        None => {}
    }

    line
}

/// Warum keine Mosh-Sitzung zustande kam. Der Kern fällt dann auf das Terminal über SSH zurück und sagt warum.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum MoshStart {
    /// Am Server gibt es kein `mosh-server`.
    Missing,
    /// `mosh-server` fand keine Locale mit UTF-8.
    Locale,
    /// Der Server führt keine Befehle aus, wie der Kern sie schickt (keine POSIX-Shell, nur SFTP).
    NoCommands(String),
    /// Die Antwort von `mosh-server` hatte nicht genau die eine erwartete Form.
    Reply(BootstrapError),
    /// Es lief etwas anderes schief (Frist, Verbindung, kein UDP-Socket).
    Failed(String),
    /// `mosh-server` lief, aber binnen 15 s kam kein UDP-Paket von ihm an.
    Blocked,
}

impl MoshStart {
    /// Die Art der Warnung an die App (`core.warning`).
    pub(crate) fn code(&self) -> &'static str {
        match self {
            Self::Blocked => "mosh_blocked",
            Self::Locale => "mosh_locale",
            _ => "mosh_unavailable",
        }
    }
}

impl std::fmt::Display for MoshStart {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Missing => formatter.write_str("mosh-server is not installed on the server"),
            Self::Locale => formatter.write_str("mosh-server found no UTF-8 locale on the server"),
            Self::NoCommands(message) => write!(formatter, "the server runs no commands for mosh: {message}"),
            Self::Reply(error) => write!(formatter, "mosh-server answered in an unexpected form: {error}"),
            Self::Failed(message) => formatter.write_str(message),
            Self::Blocked => formatter.write_str("no UDP packet from mosh-server arrived within 15 seconds"),
        }
    }
}

/// Liest die Ausgabe des Starts: alles nach der letzten Zeile [`MARKER`] (ohne sie die ganze Ausgabe) muss genau
/// einen Datensatz `MOSH CONNECT <port> <schlüssel>` enthalten – zwei, ein kaputter Port oder Schlüssel sind ein
/// Fehler, nie ein Raten. Die Grenzen (4 KiB, 64 Zeilen) und die Prüfung der Form hat `Bootstrap::parse`.
/// `server` ist die Adresse der SSH-Verbindung: Mosh geht zu demselben Server, nie zu einer Adresse, die er nennt.
pub(crate) fn connect_record(server: IpAddr, output: &[u8]) -> Result<Bootstrap, MoshStart> {
    let mut rest = output;
    let mut missing = false;
    let mut offset = 0;
    for line in output.split_inclusive(|byte| *byte == b'\n') {
        offset += line.len();
        let text = line.strip_suffix(b"\n").unwrap_or(line);
        let text = text.strip_suffix(b"\r").unwrap_or(text);
        if text == MARKER.as_bytes() {
            rest = &output[offset..];
            missing = false;
        } else if text == MISSING.as_bytes() {
            missing = true;
        }
    }

    if missing {
        return Err(MoshStart::Missing);
    }

    Bootstrap::parse(server, rest).map_err(MoshStart::Reply)
}

/// Die Größe, die die Bibliothek annimmt: höchstens 500 × 200, mindestens 1 × 1. Ein größeres Fenster zeigt dann
/// rechts bzw. unten nichts – besser, als dass `resize` scheitert.
pub(crate) fn fitting(columns: u32, rows: u32) -> (u32, u32) {
    (columns.clamp(1, MAX_COLUMNS), rows.clamp(1, MAX_ROWS))
}

/// Die Vorhersage beim Tippen, wie die App sie nennt (`session.open` mit `mosh_predict`); ohne oder unbekannt wie
/// bei `mosh` selbst adaptiv.
pub(crate) fn prediction(name: Option<&str>) -> PredictionMode {
    match name {
        Some("always") => PredictionMode::Always,
        Some("never") => PredictionMode::Never,
        _ => PredictionMode::Adaptive,
    }
}

/// Startet `mosh-server` über die stehende Verbindung und liest seine Antwort. Lehnt er die erste Locale ab, folgt
/// ein zweiter Versuch mit der nächsten aus [`LOCALES`].
pub(crate) async fn start(
    shared: &Shared,
    session: u64,
    link: &Link,
    profile: &Profile,
) -> Result<Bootstrap, MoshStart> {
    let Some(server) = link.peer.map(|address| address.ip()) else {
        return Err(MoshStart::Failed("the address of the server is unknown".to_owned()));
    };

    let ports = profile.mosh_port_range();
    for locale in LOCALES {
        let line = start_line(ports, locale);
        shared.trace(session, || format!("starting {line}"));
        let command = crate::exec::posix(&format!("{}{line}", crate::exec::MOSH_START));
        let mut output = match link.exec_within(&command, START_LIMIT).await {
            Ok(output) => output,
            Err(failure) if matches!(failure.code, "no_posix_shell" | "no_exec") => {
                return Err(MoshStart::NoCommands(failure.message));
            }
            Err(failure) => {
                return Err(MoshStart::Failed(format!(
                    "mosh-server did not start: {}",
                    failure.message
                )));
            }
        };

        // Die Ausgabe trägt den Schlüssel: Sie wird gleich nach dem Lesen überschrieben.
        let record = connect_record(server, output.stdout.as_bytes());
        output.stdout.zeroize();
        match record {
            Ok(bootstrap) => {
                let address = bootstrap.server_addr();
                shared.trace(session, || format!("mosh-server waits for UDP at {address}"));
                return Ok(bootstrap);
            }
            Err(MoshStart::Reply(BootstrapError::MissingConnectRecord)) if output.stderr.contains(LOCALE_REFUSED) => {
                shared.trace(session, || format!("mosh-server refused the locale {locale}"));
            }
            Err(MoshStart::Reply(BootstrapError::MissingConnectRecord)) if output.status != Some(0) => {
                return Err(MoshStart::Failed(format!(
                    "mosh-server did not start (status {}): {}",
                    output
                        .status
                        .map_or_else(|| "none".to_owned(), |status| status.to_string()),
                    crate::session::printable(output.stderr.lines().last().unwrap_or_default())
                )));
            }
            Err(other) => return Err(other),
        }
    }

    Err(MoshStart::Locale)
}

/// Die Threads der Mosh-Treiber eines Kerns. Endet der Kern (die App schließt), wartet er kurz auf sie
/// ([`Threads::wait`]): Sonst endete der Prozess, bevor ein Treiber `mosh-server` um das Ende gebeten hat – und
/// der liefe am Server weiter, ohne dass je wieder jemand kommt.
#[derive(Clone, Default)]
pub(crate) struct Threads(Arc<(std::sync::Mutex<usize>, std::sync::Condvar)>);

impl Threads {
    fn enter(&self) -> Running {
        *self.0.0.lock().unwrap_or_else(std::sync::PoisonError::into_inner) += 1;
        Running(self.clone())
    }

    /// Wartet, bis alle Treiber zu Ende sind, höchstens `limit`.
    pub(crate) fn wait(&self, limit: Duration) {
        let (count, ended) = &*self.0;
        let count = count.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let _ = ended.wait_timeout_while(count, limit, |count| *count > 0);
    }
}

/// Zählt einen laufenden Treiber, bis er fällt.
struct Running(Threads);

impl Drop for Running {
    fn drop(&mut self) {
        let (count, ended) = &*self.0.0;
        let mut count = count.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        *count = count.saturating_sub(1);
        ended.notify_all();
    }
}

/// Eine laufende Mosh-Sitzung: der Griff der Bibliothek hier, ihr Treiber auf einem eigenen Thread.
///
/// Fällt der Wert (Tab zu, Kern endet), bittet er `mosh-server` noch um das Ende und gibt den Griff dem Thread
/// mit: Der schickt die Bitte, wartet auf die Bestätigung (höchstens die Frist der Bibliothek, 4 s) und endet dann
/// – sonst bliebe `mosh-server` am Server stehen.
pub(crate) struct Terminal {
    session: Option<Session>,
    park: std::sync::mpsc::Sender<Session>,
    ended: oneshot::Receiver<Result<SessionExit, SessionError>>,
}

impl Terminal {
    /// Startet den Treiber und wartet, bis das erste Paket von `mosh-server` angekommen ist. Kommt binnen 15 s
    /// keines (Firewall, VPN), ist das [`MoshStart::Blocked`].
    pub(crate) async fn attach(
        threads: &Threads,
        bootstrap: Bootstrap,
        size: (u32, u32),
        predict: PredictionMode,
    ) -> Result<Self, MoshStart> {
        let (columns, rows) = fitting(size.0, size.1);
        let (ready_tx, ready) = oneshot::channel();
        let (ended_tx, ended) = oneshot::channel();
        let (park, parked) = std::sync::mpsc::channel::<Session>();
        let running = threads.enter();
        std::thread::Builder::new()
            .name("nexus-mosh".to_owned())
            .spawn(move || {
                let _running = running;
                let runtime = match tokio::runtime::Builder::new_current_thread().enable_all().build() {
                    Ok(runtime) => runtime,
                    Err(error) => {
                        let _ = ready_tx.send(Err(format!("cannot start a runtime for mosh: {error}")));
                        return;
                    }
                };
                runtime.block_on(async move {
                    match Session::connect_with_prediction_mode(bootstrap, columns, rows, predict).await {
                        Ok((session, task)) => {
                            if ready_tx.send(Ok(session)).is_ok() {
                                let _ = ended_tx.send(task.run().await);
                            }
                        }
                        Err(error) => {
                            let _ = ready_tx.send(Err(format!("mosh could not start: {error}")));
                        }
                    }
                });

                // Erst jetzt fällt ein Griff, den die Sitzung beim Ende hier abgegeben hat.
                drop(parked);
            })
            .map_err(|error| MoshStart::Failed(format!("cannot start a thread for mosh: {error}")))?;

        let session = match ready.await {
            Ok(Ok(session)) => session,
            Ok(Err(message)) => return Err(MoshStart::Failed(message)),
            Err(_) => return Err(MoshStart::Failed("mosh ended before it started".to_owned())),
        };
        let mut terminal = Self {
            session: Some(session),
            park,
            ended,
        };
        terminal.attached().await?;
        Ok(terminal)
    }

    /// Wartet auf das erste Paket des Servers – oder darauf, dass der Treiber aufgibt.
    async fn attached(&mut self) -> Result<(), MoshStart> {
        let Some(session) = self.session.as_mut() else {
            return Err(MoshStart::Failed("mosh is gone".to_owned()));
        };
        loop {
            if session.state() == SessionState::Active {
                return Ok(());
            }

            tokio::select! {
                state = session.state_changed() => {
                    if state == SessionState::Active {
                        return Ok(());
                    }
                }
                ended = &mut self.ended => {
                    return Err(match ended {
                        Ok(Err(SessionError::ConnectionTimeout)) => MoshStart::Blocked,
                        Ok(Err(error)) => MoshStart::Failed(format!("mosh ended: {error}")),
                        Ok(Ok(_)) | Err(_) => MoshStart::Failed("mosh ended before the server answered".to_owned()),
                    });
                }
            }
        }
    }

    fn session(&mut self) -> &mut Session {
        // Bis zum Drop ist der Griff immer da.
        self.session.as_mut().expect("mosh session is alive")
    }
}

impl Drop for Terminal {
    fn drop(&mut self) {
        if let Some(session) = self.session.take() {
            session.close();
            let _ = self.park.send(session);
        }
    }
}

/// Eine Aufgabe, die mit ihrem Griff endet (die Auslastung einer SSH-Verbindung).
struct Abort(tokio::task::JoinHandle<()>);

impl Drop for Abort {
    fn drop(&mut self) {
        self.0.abort();
    }
}

type Files = Option<(Arc<SftpSession>, String)>;
type Relinking<'a> = Pin<Box<dyn Future<Output = Result<(Arc<Link>, Files), (CloseReason, String)>> + Send + 'a>>;

/// Eine neue SSH-Verbindung samt Dateibrowser für ein laufendes Mosh-Terminal.
fn relink<'a>(shared: &'a Arc<Shared>, session: u64, chain: &'a [Profile]) -> Relinking<'a> {
    Box::pin(async move {
        let link = crate::session::relink(shared, session, chain).await?;
        let files = match chain.last() {
            Some(profile) => crate::session::open_files(shared, &link, profile).await,
            None => None,
        };
        Ok((link, files))
    })
}

/// Führt das Terminal über Mosh, bis die Shell endet oder Mosh aufgibt. `link` ist die stehende SSH-Verbindung
/// (schon gemeldet und eingetragen); reißt sie, läuft das Terminal weiter, und eine neue entsteht, sobald Mosh
/// wieder Antworten bekommt – von selbst höchstens alle 30 s, auf `session.relink` sofort.
pub(crate) async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    chain: &[Profile],
    link: Arc<Link>,
    mut terminal: Terminal,
    mut commands: UnboundedReceiver<Command>,
) -> Result<Option<u32>, (CloseReason, String)> {
    shared.emit(Event::SessionMosh {
        session,
        state: MoshState::Running,
        silent_for: None,
    });
    shared.trace(session, || "the terminal runs over mosh".to_owned());

    let (watching, watched) = watch::channel(false);
    let mut link = Some(link);
    let mut _monitor = link
        .as_ref()
        .map(|link| Abort(crate::session::watch_load(shared, session, link, watched.clone())));
    let mut reachability = terminal.session().subscribe_reachability();
    let mut reachability_open = true;
    let mut responsive = true;
    let mut silent_since: Option<Instant> = None;
    let mut silence_reported = Instant::now();
    let mut paused = false;
    let mut output_done = false;
    let mut relinking: Option<Relinking<'_>> = None;
    let mut last_attempt: Option<Instant> = None;
    // Von selbst neu verbinden – nicht mehr, nachdem der Benutzer eine Rückfrage abgelehnt hat oder die Anmeldung
    // scheiterte; dann erst wieder auf `session.relink`.
    let mut automatic = true;
    let mut tick = tokio::time::interval(ALIVE_CHECK);
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

    let relink = || relink(shared, session, chain);

    loop {
        // Die Teile der Bibliothek leben im `Terminal`; geliehen wird je Durchgang.
        let mosh = terminal.session.as_mut().expect("mosh session is alive");
        tokio::select! {
            output = mosh.next_output(), if !paused && !output_done => match output {
                Some(data) => shared.sink.output(session, &data),
                // Der Treiber ist zu Ende; warum, sagt gleich `ended`.
                None => output_done = true,
            },

            command = commands.recv() => match command {
                Some(Command::Input(bytes)) => {
                    for piece in bytes.chunks(MAX_INPUT) {
                        // Geschlossen heißt: Der Treiber endet gerade – das meldet `ended`.
                        let _ = mosh.send_input(piece.to_vec()).await;
                    }
                }
                Some(Command::Resize(columns, rows)) => {
                    let (columns, rows) = fitting(columns, rows);
                    let _ = mosh.resize(columns, rows).await;
                }
                Some(Command::Monitor(enabled)) => {
                    let _ = watching.send(enabled);
                }
                Some(Command::Flow(pause)) => paused = pause,
                Some(Command::Relink) => {
                    automatic = true;
                    if link.is_none() && relinking.is_none() {
                        last_attempt = Some(Instant::now());
                        relinking = Some(relink());
                    }
                }
                // Der Sender lebt, solange die Sitzung eingetragen ist.
                None => std::future::pending::<()>().await,
            },

            changed = reachability.changed(), if reachability_open => {
                // Fällt der Treiber weg, sagt gleich `ended` warum.
                let Some(now) = changed else {
                    reachability_open = false;
                    continue;
                };
                match now {
                    SessionReachability::Responsive => {
                        responsive = true;
                        if silent_since.take().is_some() {
                            shared.trace(session, || "mosh hears the server again".to_owned());
                            shared.emit(Event::SessionMosh { session, state: MoshState::Running, silent_for: None });
                        }
                    }
                    SessionReachability::Interrupted { reason } => {
                        responsive = false;
                        // Die Bibliothek merkt die Stille erst nach einer Weile; so lange ist sie schon still.
                        let quiet = match reason {
                            SessionInterruption::NoRecentReply => Duration::from_secs(10),
                            _ => Duration::from_millis(6_500),
                        };
                        let since = Instant::now().checked_sub(quiet).unwrap_or_else(Instant::now);
                        let since = *silent_since.get_or_insert(since);
                        silence_reported = Instant::now();
                        shared.emit(Event::SessionMosh {
                            session,
                            state: MoshState::Silent,
                            silent_for: Some(since.elapsed().as_secs()),
                        });
                    }
                    _ => {}
                }
            },

            ended = &mut terminal.ended => {
                return match ended {
                    Ok(Ok(SessionExit::RemoteClosed)) => Ok(None),
                    Ok(Ok(_)) => Err((CloseReason::User, String::new())),
                    Ok(Err(error)) => Err((CloseReason::ConnectionLost, format!("mosh: {error}"))),
                    Err(_) => Err((CloseReason::ConnectionLost, "mosh ended unexpectedly".to_owned())),
                };
            },

            outcome = async { relinking.as_mut().expect("guarded").await }, if relinking.is_some() => {
                relinking = None;
                match outcome {
                    Ok((new, files)) => {
                        shared.trace(session, || "the SSH connection is back".to_owned());
                        _monitor = Some(Abort(crate::session::watch_load(shared, session, &new, watched.clone())));
                        if let Some(profile) = chain.last() {
                            crate::session::start_tunnels(&new, profile);
                        }
                        shared.relinked(session, Arc::clone(&new), files);
                        link = Some(new);
                    }
                    Err((reason, message)) => {
                        automatic &= !matches!(reason, CloseReason::AuthFailed | CloseReason::HostKeyRejected);
                        shared.emit(Event::SessionLinkLost { session, message: Some(message) });
                    }
                }
            },

            _ = tick.tick() => {
                if link.as_ref().is_some_and(|link| link.is_closed()) {
                    link = None;
                    _monitor = None;
                    shared.trace(session, || "the SSH connection was lost; the terminal goes on over mosh".to_owned());
                    shared.lose_link(session);
                }

                if link.is_none()
                    && relinking.is_none()
                    && automatic
                    && responsive
                    && last_attempt.is_none_or(|attempt| attempt.elapsed() >= RELINK_EVERY)
                {
                    last_attempt = Some(Instant::now());
                    relinking = Some(relink());
                }

                if let Some(since) = silent_since
                    && silence_reported.elapsed() >= SILENCE_REPORT
                {
                    silence_reported = Instant::now();
                    shared.emit(Event::SessionMosh {
                        session,
                        state: MoshState::Silent,
                        silent_for: Some(since.elapsed().as_secs()),
                    });
                }
            },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::Ipv4Addr;

    const KEY: &str = "4NeCCgvZFe2RnPgrcU1PQw";

    fn record(output: &[u8]) -> Result<Bootstrap, MoshStart> {
        connect_record(Ipv4Addr::LOCALHOST.into(), output)
    }

    #[test]
    fn the_start_line_is_fixed_text_and_checked_numbers() {
        assert_eq!(start_line(None, "C.UTF-8"), "mosh-server new -s -c 256 -l LANG=C.UTF-8");
        assert_eq!(
            start_line(Some((60001, 60010)), "C.UTF-8"),
            "mosh-server new -s -c 256 -l LANG=C.UTF-8 -p 60001:60010"
        );
        assert_eq!(
            start_line(Some((60001, 60001)), "en_US.UTF-8"),
            "mosh-server new -s -c 256 -l LANG=en_US.UTF-8 -p 60001"
        );
        // Eine Locale, die nicht in der Liste steht, kommt nie in die Zeile.
        assert_eq!(
            start_line(None, "x; rm -rf ~"),
            "mosh-server new -s -c 256 -l LANG=C.UTF-8"
        );
    }

    #[test]
    fn the_start_command_finds_mosh_server_only_in_fixed_places_and_marks_its_output() {
        let command = format!(
            "{}{}",
            crate::exec::MOSH_START,
            start_line(Some((60001, 60010)), "C.UTF-8")
        );
        assert!(
            command.contains(&format!("echo {MARKER}; exec mosh-server new ")),
            "{command}"
        );
        assert!(command.contains(&format!("echo {MISSING}; exit 127")), "{command}");
        assert!(command.starts_with("PATH=\"${PATH:+$PATH:}/usr/local/bin:/opt/homebrew/bin:/usr/bin\"; "));
        // Kein `!` (csh) und kein einfaches Anführungszeichen, das `posix` umschreiben müsste.
        assert!(!command.contains('!') && !command.contains('\''), "{command}");
        assert_eq!(crate::exec::posix(&command), format!("sh -c '{command}'"));
    }

    #[test]
    fn only_one_clean_connect_record_is_taken() {
        assert!(record(format!("warning: x\r\nMOSH CONNECT 60001 {KEY}\r\n").as_bytes()).is_ok());
        let bad: [&[u8]; 6] = [
            b"MOSH CONNECT 0 4NeCCgvZFe2RnPgrcU1PQw\n",
            b"MOSH CONNECT 60001 kurz\n",
            b"MOSH CONNECT 60001 4NeCCgvZFe2RnPgrcU1PQw\nMOSH CONNECT 60002 4NeCCgvZFe2RnPgrcU1PQw\n",
            b"",
            b"MOSH CONNECT 60001 4NeCCgvZFe2RnPgrcU1PQw extra\n",
            b"MOSH  CONNECT 60001 4NeCCgvZFe2RnPgrcU1PQw\n",
        ];
        for output in bad {
            assert!(record(output).is_err(), "{:?}", String::from_utf8_lossy(output));
        }
    }

    #[test]
    fn what_comes_before_the_marker_is_not_read() {
        // Eine `~/.bashrc`, die etwas ausgibt – auch etwas, das wie ein Datensatz aussieht – zählt nicht.
        let output = format!(
            "Welcome!\nMOSH CONNECT 1 AAAAAAAAAAAAAAAAAAAAAA\n{MARKER}\n\r\nMOSH CONNECT 60004 {KEY}\r\n\
             mosh-server (mosh 1.4.0)\n"
        );
        let bootstrap = record(output.as_bytes()).unwrap();
        assert_eq!(bootstrap.server_addr(), "127.0.0.1:60004".parse().unwrap());

        // Zu viel nach der Marke ist ein Fehler, keine Suche.
        let noisy = format!("{MARKER}\n{}MOSH CONNECT 60004 {KEY}\n", "x\n".repeat(80));
        assert_eq!(
            record(noisy.as_bytes()).err(),
            Some(MoshStart::Reply(BootstrapError::TooManyLines))
        );
    }

    #[test]
    fn a_server_without_mosh_server_says_so() {
        assert_eq!(
            record(format!("motd\n{MISSING}\n").as_bytes()).err(),
            Some(MoshStart::Missing)
        );
        assert_eq!(MoshStart::Missing.code(), "mosh_unavailable");
        assert_eq!(MoshStart::Blocked.code(), "mosh_blocked");
        assert_eq!(MoshStart::Locale.code(), "mosh_locale");
    }

    #[test]
    fn mosh_goes_to_the_address_of_the_ssh_connection() {
        let output = format!("MOSH CONNECT 60001 {KEY}\n");
        let bootstrap = connect_record("::1".parse().unwrap(), output.as_bytes()).unwrap();
        assert_eq!(bootstrap.server_addr(), "[::1]:60001".parse().unwrap());
    }

    #[test]
    fn no_message_names_the_key() {
        let bootstrap = record(format!("MOSH CONNECT 60001 {KEY}\n").as_bytes()).unwrap();
        assert!(!format!("{bootstrap:?}").contains(KEY));
        for start in [
            MoshStart::Missing,
            MoshStart::Locale,
            MoshStart::Blocked,
            MoshStart::Reply(BootstrapError::InvalidKey),
            MoshStart::Reply(BootstrapError::AmbiguousConnectRecord),
        ] {
            assert!(!start.to_string().contains(KEY));
        }
    }

    #[test]
    fn the_size_stays_within_what_mosh_accepts() {
        assert_eq!(fitting(80, 24), (80, 24));
        assert_eq!(fitting(0, 0), (1, 1));
        assert_eq!(fitting(4000, 1000), (500, 200));
        for (columns, rows) in [(500, 200), (u32::MAX, u32::MAX), (1, u32::MAX), (u32::MAX, 1)] {
            let (columns, rows) = fitting(columns, rows);
            assert!(columns <= 500 && rows <= 200 && columns * rows <= 100_000);
        }
    }

    #[test]
    fn the_prediction_follows_the_app_and_is_adaptive_otherwise() {
        assert_eq!(prediction(Some("always")), PredictionMode::Always);
        assert_eq!(prediction(Some("never")), PredictionMode::Never);
        assert_eq!(prediction(Some("adaptive")), PredictionMode::Adaptive);
        assert_eq!(prediction(Some("sometimes")), PredictionMode::Adaptive);
        assert_eq!(prediction(None), PredictionMode::Adaptive);
    }
}
