//! Upload und Download über SFTP, mit Warteschlange, Fortschritt und Abbruch (Spec §7).
//!
//! Ein Transfer ist eine Datei oder ein ganzes Verzeichnis. Höchstens [`CONCURRENT`] laufen gleichzeitig, die
//! übrigen warten – in der Reihenfolge, in der sie eingereiht wurden ([`Slots`]). Den Stand meldet der Kern als
//! `transfer.state`-Ereignis.

use std::collections::{HashMap, HashSet, VecDeque};
use std::future::Future;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::Poll;
use std::time::{Duration, Instant};

use russh_sftp::client::SftpSession;
use russh_sftp::protocol::OpenFlags;
use serde::{Deserialize, Serialize};
use tokio::io::{AsyncReadExt, AsyncSeekExt, AsyncWriteExt};
use tokio::sync::{Notify, watch};

use crate::core::Shared;
use crate::excludes::Excludes;
use crate::protocol::{Direction, Event, TransferPhase, TreeEntry};
use crate::sftp::{Failure, base_name, is_special, join, not_a_file};

/// Wie viele Transfers gleichzeitig laufen.
pub(crate) const CONCURRENT: usize = 2;

/// Endung der Datei, in die ein Transfer schreibt, bis er vollständig ist. Die App kennt sie auch
/// (`VirtualDrop.PartialSuffix`): Beim Ziehen in ein anderes Programm liest sie die Teil-Datei mit.
const PARTIAL_SUFFIX: &str = ".nexus-part";

const CHUNK: usize = 256 * 1024;

/// So viel vor der Stelle, an der ein Upload fortgesetzt wird, vergleicht der Kern mit der lokalen Datei. Schreiben
/// läuft dem Server voraus: Bis zu 16 Pakete von 32 KiB sind unterwegs, ohne bestätigt zu sein, und reißt die
/// Verbindung, ist offen, welche davon noch angekommen sind. Was fehlen oder nur halb angekommen sein kann, liegt
/// also im letzten halben MiB vor dem Ende – der Vergleich reicht mit Abstand darüber hinaus.
const RESUME_CHECK: u64 = 1024 * 1024;

/// Wie viele Dateien eines Verzeichnisses zugleich unterwegs sind. Bei vielen kleinen Dateien bestimmt nicht die
/// Leitung das Tempo, sondern das Warten auf die Antworten des Servers – mehrere Dateien nebeneinander verkürzen
/// es entsprechend. Der Server sieht dabei höchstens so viele offene Dateien.
const FILES_AT_ONCE: usize = 8;

/// Über FTP nur so viele: Dort hat jede Datei ihre eigene Verbindung, und viele Server lassen je Benutzer oder
/// Adresse nur eine Handvoll zu – mit zwei Transfers zugleich und der Verbindung für die Listen sind es so fünf.
const FILES_AT_ONCE_FEW: usize = 2;

/// Wie viele Dateien ein Transfer über diese Sitzungen zugleich überträgt.
fn files_at_once(sessions: &[&SftpSession]) -> usize {
    let few = sessions
        .iter()
        .any(|sftp| crate::metered::of(sftp).is_some_and(|meter| meter.few()));
    if few { FILES_AT_ONCE_FEW } else { FILES_AT_ONCE }
}

/// Mehr Einträge nennt [`tree`] nicht. Wer einen Baum vorab wissen will, hält ihn als Ganzes im Speicher und reicht
/// ihn weiter (der Explorer bekommt je Eintrag gut ein halbes Kilobyte) – darüber ist „Herunterladen“ der Weg.
pub(crate) const TREE_LIMIT: usize = 20_000;

/// Mehr Einträge lädt ein einzelner Ordner-Download nicht – weit über jedem echten Ordner, aber ein Ende.
pub(crate) const DOWNLOAD_LIMIT: usize = 2_000_000;
const PROGRESS_INTERVAL: Duration = Duration::from_millis(150);

/// Wie viele Verzeichnisse zugleich aufgelistet werden, wenn ein Baum erfasst wird – aus demselben Grund wie
/// [`FILES_AT_ONCE`]: Jedes Auflisten ist ein Hin und Her von mehreren Nachrichten, und bei Hunderten von
/// Verzeichnissen bestimmt das Warten darauf die Dauer. Gemessen über eine Leitung mit 23 ms Laufzeit: 421
/// Verzeichnisse brauchen einzeln 40 s, mit 8 zugleich 5,2 s, mit 16 zugleich 2,7 s. Mehr als bei den Dateien,
/// weil ein Verzeichnis nach wenigen Nachrichten wieder geschlossen ist.
const DIRECTORIES_AT_ONCE: usize = 16;

/// So viele Verzeichnisse erfasst [`plan`] an einem Stück, bevor wieder gefragt wird, ob abgebrochen wurde oder
/// die Obergrenze erreicht ist.
const DIRECTORIES_PER_ROUND: usize = 4 * DIRECTORIES_AT_ONCE;

/// Die Obergrenze für die Bandbreite aller Transfers zusammen; 0 = keine.
///
/// Jeder Block, der übertragen wurde, bucht seine Zeit auf einer gemeinsamen Uhr: Der nächste darf erst weiter, wenn
/// die Leitung bei der eingestellten Rate so weit wäre. So teilen sich mehrere Transfers die Grenze von selbst, und
/// wer eine Weile nichts schickt, spart dabei nichts an – eine Pause ergibt keinen Schub danach.
pub(crate) struct Limiter {
    rate: AtomicU64,
    free_at: Mutex<Instant>,
}

impl Default for Limiter {
    fn default() -> Self {
        Self {
            rate: AtomicU64::new(0),
            free_at: Mutex::new(Instant::now()),
        }
    }
}

impl Limiter {
    /// Setzt die Grenze in Bytes je Sekunde; 0 hebt sie auf. Gilt sofort, auch für laufende Transfers.
    pub(crate) fn set(&self, bytes_per_second: u64) {
        self.rate.store(bytes_per_second, Ordering::Relaxed);
    }

    /// Wie lange nach `bytes` übertragenen Bytes zu warten ist, von `now` an.
    fn delay(&self, bytes: usize, now: Instant) -> Duration {
        let rate = self.rate.load(Ordering::Relaxed);
        if rate == 0 || bytes == 0 {
            return Duration::ZERO;
        }

        let mut free_at = lock(&self.free_at);
        let start = (*free_at).max(now);
        *free_at = start + Duration::from_secs_f64(bytes as f64 / rate as f64);
        *free_at - now
    }

    /// Wartet, bis die Leitung bei der eingestellten Rate so weit wäre – höchstens ein paar Sekunden am Stück,
    /// damit ein Abbruch und eine geänderte Grenze nicht lange auf sich warten lassen.
    pub(crate) async fn take(&self, bytes: usize) {
        let wait = self.delay(bytes, Instant::now()).min(Duration::from_secs(5));
        if !wait.is_zero() {
            tokio::time::sleep(wait).await;
        }
    }
}

/// Die Plätze der Warteschlange. Wer zuerst eingereiht wurde, läuft zuerst – unabhängig davon, in welcher
/// Reihenfolge die Aufgaben der Transfers zum Zug kommen. Deshalb zieht ein Transfer seine Wartenummer schon in
/// der Anfrage ([`Slots::enqueue`]) und nicht erst, wenn seine Aufgabe läuft.
#[derive(Default)]
pub(crate) struct Slots {
    state: Mutex<SlotState>,
    changed: Notify,
}

#[derive(Default)]
struct SlotState {
    running: usize,
    /// Die Wartenden in der Reihenfolge, in der sie an die Reihe kommen.
    waiting: Vec<u64>,
    next_ticket: u64,
}

/// Ein belegter Platz; er wird frei, wenn der Wert fällt.
pub(crate) struct Slot<'a>(&'a Slots);

impl Slots {
    fn state(&self) -> std::sync::MutexGuard<'_, SlotState> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Reiht einen Transfer ein und liefert seine Wartenummer.
    pub(crate) fn enqueue(&self) -> u64 {
        let mut state = self.state();
        let ticket = state.next_ticket;
        state.next_ticket += 1;
        state.waiting.push(ticket);
        ticket
    }

    /// Wartet, bis ein Platz frei ist und niemand mit kleinerer Nummer davor steht.
    pub(crate) async fn acquire(&self, ticket: u64) -> Slot<'_> {
        loop {
            // Erst zum Wecken anmelden, dann nachsehen – sonst ginge ein Wecken dazwischen verloren.
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();

            {
                let mut state = self.state();
                if state.running < CONCURRENT && state.waiting.first() == Some(&ticket) {
                    state.waiting.remove(0);
                    state.running += 1;
                    drop(state);

                    // Der Nächste in der Reihe kann jetzt vorn stehen.
                    self.changed.notify_waiters();
                    return Slot(self);
                }
            }

            notified.await;
        }
    }

    /// Nimmt einen wartenden Transfer aus der Reihe (Abbruch, bevor er an der Reihe war).
    pub(crate) fn leave(&self, ticket: u64) {
        self.state().waiting.retain(|waiting| *waiting != ticket);
        self.changed.notify_waiters();
    }

    /// Stellt einen wartenden Transfer vor einen anderen wartenden – oder, ohne `before`, ans Ende der Reihe.
    /// `false`, wenn einer der beiden nicht (mehr) wartet: Dann bleibt die Reihe, wie sie ist.
    pub(crate) fn move_before(&self, ticket: u64, before: Option<u64>) -> bool {
        let mut state = self.state();
        let waits = |waiting: &[u64], wanted: u64| waiting.contains(&wanted);
        if !waits(&state.waiting, ticket)
            || before.is_some_and(|before| before == ticket || !waits(&state.waiting, before))
        {
            return false;
        }

        state.waiting.retain(|waiting| *waiting != ticket);
        let at = before
            .and_then(|before| state.waiting.iter().position(|waiting| *waiting == before))
            .unwrap_or(state.waiting.len());
        state.waiting.insert(at, ticket);
        drop(state);

        // Wer jetzt vorn steht, hat sich geändert.
        self.changed.notify_waiters();
        true
    }
}

impl Drop for Slot<'_> {
    fn drop(&mut self) {
        self.0.state().running -= 1;
        self.0.changed.notify_waiters();
    }
}

/// Was die App zu einem Transfer angibt.
pub(crate) struct Job {
    pub(crate) transfer: u64,
    /// Wartenummer aus [`Slots::enqueue`].
    pub(crate) ticket: u64,
    pub(crate) direction: Direction,
    /// Upload: die lokale Datei bzw. das Verzeichnis. Download: das lokale Zielverzeichnis.
    pub(crate) local: PathBuf,
    /// Upload: das Zielverzeichnis auf dem Server. Download: die Datei bzw. das Verzeichnis dort.
    pub(crate) remote: String,
    /// Der Name am Ziel, wenn er nicht der der Quelle sein soll: beim Upload im Zielverzeichnis des Servers, beim
    /// Download im lokalen Zielverzeichnis.
    pub(crate) name: Option<String>,
    /// Bei einem Ordner: nur diese Dateien übertragen, genannt als Pfade unterhalb des Ordners der Quelle, mit
    /// `/` getrennt. Alles andere bleibt, wie es ist – so wird aus einem Ordner-Transfer ein Abgleich.
    pub(crate) only: Option<Vec<String>>,
    /// Bei einem Ordner: Was auf diese Muster passt, bleibt aus ([`crate::excludes`]). Leer: alles geht mit.
    pub(crate) exclude: Excludes,
    /// Von Server zu Server ([`Direction::Relay`]): der Dateikanal des Ziels. `remote` nennt dann die Quelle auf
    /// dem Server der Sitzung, `local` das Zielverzeichnis auf dem anderen.
    pub(crate) relay: Option<Arc<SftpSession>>,
    /// Ein neuer Versuch: so viel hatte der abgebrochene schon übertragen. Nur dann zählt, was der Kern von ihm
    /// noch weiß ([`Trail`]) – und beim Upload einer einzelnen Datei geht es an dieser Stelle weiter
    /// ([`resume_point`]).
    pub(crate) resume: Option<u64>,
    /// Mehr Bytes überträgt der Transfer nicht (`too_large`); `None` = keine Grenze.
    pub(crate) limit: Option<u64>,
    /// Welche vorhandenen Dateien am Ziel bleiben, statt überschrieben zu werden; `None` = keine.
    pub(crate) keep: Option<crate::protocol::Keep>,
}

impl Job {
    /// Woran der Kern einen neuen Versuch desselben Transfers erkennt.
    fn key(&self) -> Key {
        (
            self.direction,
            self.local.clone(),
            self.remote.clone(),
            self.name.clone(),
        )
    }
}

/// Warum ein Transfer aufhören soll, bevor er fertig ist.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Halt {
    /// Der Benutzer hat ihn abgebrochen oder seine Sitzung geschlossen.
    Cancelled,
    /// Die Verbindung, über die er lief, ist zu Ende.
    Lost,
}

enum Stop {
    Cancelled,
    Failed(Failure),
}

impl Stop {
    /// Ob die Verbindung schuld ist – dann hat das, was schon übertragen ist, seinen Wert behalten.
    fn lost(&self) -> bool {
        matches!(self, Self::Failed(failure) if matches!(failure.code, "connection" | "timeout"))
    }
}

/// Größe und Änderungszeit einer Datei: Daran erkennt ein neuer Versuch, ob die Quelle noch dieselbe ist.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
struct Stamp {
    size: u64,
    /// Seit 1970. Ohne Zeit gilt eine Datei nie als dieselbe.
    modified: Option<Duration>,
}

impl Stamp {
    fn local(metadata: &std::fs::Metadata) -> Self {
        Self {
            size: metadata.len(),
            modified: metadata
                .modified()
                .ok()
                .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok()),
        }
    }

    fn remote(file: &Planned) -> Self {
        Self {
            size: file.size,
            modified: file.modified.map(Duration::from_secs),
        }
    }

    fn same(self, other: Self) -> bool {
        self.modified.is_some() && self == other
    }
}

/// Wie weit eine Datei bei einem Versuch kam.
#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum Reached {
    /// Sie ist vollständig übertragen.
    Whole,
    /// Upload: So viele Bytes waren geschickt, als der Versuch endete.
    Sent(u64),
    /// Download: Die Teil-Datei liegt noch auf diesem Rechner, so lang, wie sie ist.
    Kept(PathBuf),
    /// Download, der gerade läuft: Die Teil-Datei wird geschrieben, und bis `safe` steht sie sicher auf der Platte
    /// ([`SYNC_BYTES`]). Liest ein Kern das aus der Datei, ist der davor mittendrin geendet (Absturz): Dann gilt
    /// nur, was sicher war – dahinter kann nach einem Absturz von Windows Beliebiges stehen.
    Writing { partial: PathBuf, safe: u64 },
}

impl Reached {
    /// Die Teil-Datei, die nur dieser Spur wegen auf der Platte liegt.
    fn partial(&self) -> Option<&Path> {
        match self {
            Self::Kept(partial) | Self::Writing { partial, .. } => Some(partial),
            Self::Whole | Self::Sent(_) => None,
        }
    }
}

#[derive(Serialize, Deserialize)]
struct Mark {
    /// Wie die Quelle aussah, als die Datei übertragen wurde.
    source: Stamp,
    reached: Reached,
}

/// Was ein Versuch geschafft hat, Datei für Datei – benannt nach ihrer Quelle: beim Upload der lokale Pfad, beim
/// Download der auf dem Server. Ein neuer Versuch überspringt damit, was vollständig drüben ist, und setzt fort,
/// was unterwegs war. Fällt eine Spur, verschwinden mit ihr die Teil-Dateien, die nur ihretwegen liegen geblieben
/// sind – wer eine Spur nur weglegen will, gibt sie [`Trails::keep`].
#[derive(Default)]
struct Trail(HashMap<String, Mark>);

impl Drop for Trail {
    fn drop(&mut self) {
        for mark in self.0.values() {
            if let Some(partial) = mark.reached.partial() {
                remove_partial(partial);
            }
        }
    }
}

/// Räumt eine Teil-Datei weg – und nur eine solche: Der Pfad kann aus der gespeicherten Datei stammen, und was dort
/// steht, soll den Kern nie dazu bringen, etwas anderes zu löschen.
fn remove_partial(path: &Path) {
    if path.to_string_lossy().ends_with(PARTIAL_SUFFIX) {
        let _ = std::fs::remove_file(path);
    }
}

/// Richtung, lokaler Pfad, Pfad auf dem Server und der Name im Ziel – wie in [`Job`].
pub(crate) type Key = (Direction, PathBuf, String, Option<String>);

/// So viele abgebrochene Transfers merkt sich der Kern; bei mehr fällt der älteste heraus.
const TRAILS: usize = 32;

/// Die Spuren der Transfers, die nicht zu Ende gekommen sind – bis zum nächsten Versuch, auch über das Ende des
/// Kerns hinaus: Sie stehen in `trails.json` im Datenordner. Dort steht auch, wie weit die laufenden Transfers
/// gerade sind (höchstens alle [`SAVE_INTERVAL`] neu geschrieben, und am Ende jedes Transfers): Stürzt die App ab,
/// findet der nächste Start den Stand von kurz davor – samt den Teil-Dateien, die dabei entstanden sind.
#[derive(Default)]
pub(crate) struct Trails {
    /// Ohne Datei leben die Spuren nur im Speicher.
    path: Option<PathBuf>,
    all: Mutex<VecDeque<(Key, Trail)>>,
    /// Die Spuren der laufenden Transfers, nach ihrer Nummer. Jeder Transfer schreibt in seine eigene.
    running: Mutex<Vec<(u64, Key, Held<Trail>)>>,
    /// Ob im Speicher etwas anderes steht als in der Datei.
    unsaved: AtomicBool,
    /// Wann die Datei zuletzt geschrieben wurde.
    saved_at: Mutex<Option<Instant>>,
}

/// So oft schreiben laufende Transfers ihren Stand höchstens in die Datei – bei tausenden kleiner Dateien wäre
/// sie sonst ständig in Arbeit.
const SAVE_INTERVAL: Duration = Duration::from_secs(1);

/// Nach so vielen Bytes, spätestens nach [`SYNC_INTERVAL`], schreibt ein Download seine Teil-Datei fest auf die
/// Platte und merkt sich, bis wohin sie sicher stimmt ([`Reached::Writing`]).
const SYNC_BYTES: u64 = 64 * 1024 * 1024;
const SYNC_INTERVAL: Duration = Duration::from_secs(5);

/// Was ein laufender Transfer und [`Trails`] gemeinsam halten.
type Held<T> = Arc<Mutex<T>>;

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// `trails.json`, wie sie geschrieben wird.
#[derive(Serialize)]
struct Saving<'a> {
    version: u32,
    trails: Vec<SavedTrail<&'a Path, &'a str, &'a HashMap<String, Mark>>>,
}

/// `trails.json`, wie sie gelesen wird.
#[derive(Deserialize)]
struct Saved {
    version: u32,
    trails: Vec<SavedTrail<PathBuf, String, HashMap<String, Mark>>>,
}

#[derive(Serialize, Deserialize)]
struct SavedTrail<P, S, F> {
    direction: Direction,
    local: P,
    remote: S,
    #[serde(default)]
    name: Option<S>,
    files: F,
}

const TRAILS_VERSION: u32 = 1;

impl Trails {
    /// Liest die Spuren aus dem Datenordner. Eine fehlende oder unlesbare Datei heißt: Es gibt keine – dann
    /// beginnt eben jeder Transfer von vorn.
    pub(crate) fn load(path: PathBuf) -> Self {
        let saved = std::fs::read_to_string(&path)
            .ok()
            .and_then(|text| serde_json::from_str::<Saved>(&text).ok())
            .filter(|saved| saved.version == TRAILS_VERSION);
        let all = saved
            .into_iter()
            .flat_map(|saved| saved.trails)
            .map(|trail| {
                (
                    (trail.direction, trail.local, trail.remote, trail.name),
                    Trail(trail.files),
                )
            })
            .collect();
        Self {
            path: Some(path),
            all: Mutex::new(all),
            running: Mutex::default(),
            unsaved: AtomicBool::new(false),
            saved_at: Mutex::default(),
        }
    }

    fn all(&self) -> std::sync::MutexGuard<'_, VecDeque<(Key, Trail)>> {
        lock(&self.all)
    }

    /// Ein Transfer beginnt: Seine Spur steht ab jetzt mit in der Datei.
    fn start(&self, transfer: u64, key: Key, trail: Arc<Mutex<Trail>>) {
        lock(&self.running).push((transfer, key, trail));
    }

    /// Ein Transfer ist zu Ende; was aus seiner Spur wird, entscheidet er selbst ([`Trails::keep`]).
    fn finish(&self, transfer: u64) {
        lock(&self.running).retain(|(number, _, _)| *number != transfer);
        self.unsaved.store(true, Ordering::Relaxed);
    }

    /// Ein laufender Transfer ist weitergekommen. Geschrieben wird höchstens alle [`SAVE_INTERVAL`].
    fn changed(&self) {
        self.unsaved.store(true, Ordering::Relaxed);
        let due = lock(&self.saved_at).is_none_or(|saved| saved.elapsed() >= SAVE_INTERVAL);
        if due {
            self.save();
        }
    }

    /// Holt die Spur eines Transfers heraus. Wer sie nicht benutzt, lässt sie fallen – ein Transfer, der ohne sie
    /// neu beginnt, macht sie ungültig.
    fn take(&self, key: &Key) -> Option<Trail> {
        let mut all = self.all();
        let index = all.iter().position(|(other, _)| other == key)?;
        self.unsaved.store(true, Ordering::Relaxed);
        all.remove(index).map(|(_, trail)| trail)
    }

    fn keep(&self, key: Key, trail: Trail) {
        let mut all = self.all();
        if let Some(index) = all.iter().position(|(other, _)| other == &key)
            && let Some((_, mut old)) = all.remove(index)
        {
            // Der alte Stand fällt – aber nicht die Teil-Dateien, die auch der neue kennt.
            old.0
                .retain(|source, _| !matches!(trail.0.get(source), Some(mark) if mark.reached.partial().is_some()));
        }

        all.push_back((key, trail));
        while all.len() > TRAILS {
            all.pop_front();
        }

        self.unsaved.store(true, Ordering::Relaxed);
    }

    /// Behält nur die Spuren dieser Transfers: Die übrigen zeigt die App nicht mehr, niemand wird sie fortsetzen.
    pub(crate) fn retain(&self, keys: &[Key]) {
        {
            let mut all = self.all();
            let before = all.len();
            all.retain(|(key, _)| keys.contains(key));
            if all.len() != before {
                self.unsaved.store(true, Ordering::Relaxed);
            }
        }

        self.save();
    }

    /// Schreibt die Spuren in den Datenordner, wenn sich seit dem letzten Mal etwas geändert hat.
    pub(crate) fn save(&self) {
        let Some(path) = &self.path else {
            return;
        };
        // Wer die gespeicherten Spuren hält, schreibt – so überholt nie ein älterer Stand einen neueren.
        let all = self.all();
        if !self.unsaved.swap(false, Ordering::Relaxed) {
            return;
        }

        let running = lock(&self.running);
        let current: Vec<_> = running.iter().map(|(_, key, trail)| (key, lock(trail))).collect();
        let saving = Saving {
            version: TRAILS_VERSION,
            trails: all
                .iter()
                .map(|(key, trail)| (key, trail))
                .chain(current.iter().map(|(key, trail)| (*key, &**trail)))
                .filter(|(_, trail)| !trail.0.is_empty())
                .map(|((direction, local, remote, name), trail)| SavedTrail {
                    direction: *direction,
                    local: local.as_path(),
                    remote: remote.as_str(),
                    name: name.as_deref(),
                    files: &trail.0,
                })
                .collect(),
        };

        // Geht das Schreiben schief, bleibt der Stand im Speicher gültig; nur ein Neustart wüsste nichts davon.
        if let Ok(text) = serde_json::to_string(&saving) {
            let _ = crate::profiles::write_atomically(path, &text);
        }
        *lock(&self.saved_at) = Some(Instant::now());
    }
}

impl Drop for Trails {
    fn drop(&mut self) {
        // Der Kern endet, die Spuren bleiben gültig: Sie stehen in der Datei, und die Teil-Dateien bleiben liegen –
        // auch die der Transfers, die nicht mehr rechtzeitig zu Ende kamen.
        self.save();
        for (_, trail) in self.all().iter_mut() {
            trail.0.clear();
        }
        for (_, _, trail) in lock(&self.running).iter() {
            lock(trail).0.clear();
        }
    }
}

impl From<Failure> for Stop {
    fn from(failure: Failure) -> Self {
        Self::Failed(failure)
    }
}

impl From<russh_sftp::client::error::Error> for Stop {
    fn from(error: russh_sftp::client::error::Error) -> Self {
        Self::Failed(error.into())
    }
}

impl From<std::io::Error> for Stop {
    fn from(error: std::io::Error) -> Self {
        Self::Failed(error.into())
    }
}

struct Progress<'a> {
    shared: &'a Shared,
    transfer: u64,
    halt: watch::Receiver<Option<Halt>>,
    // Mehrere Dateien eines Verzeichnisses laufen nebeneinander und zählen alle hier hinein.
    bytes: AtomicU64,
    total: AtomicU64,
    /// Fertige und alle Dateien – der Fortschritt, wenn es keine Bytes gibt.
    files_done: AtomicU64,
    files: AtomicU64,
    /// So viel war von einem Versuch davor schon übertragen und ging nicht noch einmal über die Leitung; 0 = alles
    /// von vorn.
    resumed: AtomicU64,
    reported: Mutex<Instant>,
    /// Obergrenze für die Bytes dieses Transfers, siehe [`Job::limit`].
    limit: Option<u64>,
    /// Was am Ziel bleibt, siehe [`Job::keep`].
    keep: Option<crate::protocol::Keep>,
    /// Was ein Ordner-Transfer auslässt, siehe [`Job::exclude`].
    exclude: Excludes,
    /// Zu Beginn, was der Versuch davor geschafft hat; jede Datei nimmt ihren Eintrag heraus und trägt ein, wie
    /// weit sie diesmal kam. [`Trails`] schreibt sie unterwegs mit in die Datei.
    trail: Arc<Mutex<Trail>>,
}

impl Progress<'_> {
    fn trail(&self) -> std::sync::MutexGuard<'_, Trail> {
        lock(&self.trail)
    }

    /// Nimmt eine Datei aus der Spur – ihre Teil-Datei ist schon weg.
    fn forget(&self, source: &str) {
        let removed = self.trail().0.remove(source);
        if removed.is_some() {
            self.shared.trails.changed();
        }
    }

    /// Wie weit die Datei beim Versuch davor kam – wenn ihre Quelle seither dieselbe geblieben ist.
    fn earlier(&self, source: &str, stamp: Stamp) -> Option<Reached> {
        let mark = self.trail().0.remove(source)?;
        if mark.source.same(stamp) {
            return Some(mark.reached);
        }

        // Die Quelle ist eine andere geworden: Was von der alten liegen blieb, passt zu nichts mehr.
        if let Some(partial) = mark.reached.partial() {
            remove_partial(partial);
        }

        None
    }

    fn note(&self, source: &str, stamp: Stamp, reached: Reached) {
        self.trail()
            .0
            .insert(source.to_owned(), Mark { source: stamp, reached });
        self.shared.trails.changed();
    }

    /// Eine Datei, die der Versuch davor vollständig übertragen hat, geht nicht noch einmal über die Leitung.
    fn skip(&self, bytes: u64) {
        self.resumed.fetch_add(bytes, Ordering::Relaxed);
        self.bytes.fetch_add(bytes, Ordering::Relaxed);
        self.report();
    }

    fn emit(&self, phase: TransferPhase, message: Option<String>) {
        *self.reported.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = Instant::now();
        self.shared.emit(Event::TransferState {
            transfer: self.transfer,
            state: phase,
            bytes: self.bytes.load(Ordering::Relaxed),
            total: self.total.load(Ordering::Relaxed),
            files_done: self.files_done.load(Ordering::Relaxed),
            files: self.files.load(Ordering::Relaxed),
            resumed: Some(self.resumed.load(Ordering::Relaxed)).filter(|from| *from > 0),
            message,
        });
    }

    /// Eine Datei geht ab `from` weiter: Was davor liegt, ist schon am Ziel und zählt als übertragen.
    fn resume(&self, from: u64) {
        self.resumed.fetch_add(from, Ordering::Relaxed);
        self.bytes.fetch_add(from, Ordering::Relaxed);
        self.emit(TransferPhase::Running, None);
    }

    /// Nimmt weitere Bytes in die Gesamtmenge auf.
    fn expect(&self, bytes: u64) {
        self.total.fetch_add(bytes, Ordering::Relaxed);
    }

    /// So viele Dateien überträgt der Transfer; steht fest, wenn der Baum erfasst ist.
    fn expect_files(&self, files: usize) {
        self.files.fetch_add(files as u64, Ordering::Relaxed);
    }

    /// Eine Datei ist fertig – übertragen oder übersprungen. Bei lauter leeren Dateien ist das der einzige
    /// Fortschritt, deshalb wird er auch gemeldet.
    fn file_done(&self) {
        self.files_done.fetch_add(1, Ordering::Relaxed);
        self.report();
    }

    /// Bremst nach einem übertragenen Block, wenn eine Obergrenze für die Bandbreite gilt ([`Limiter`]).
    async fn throttle(&self, bytes: usize) {
        self.shared.limiter.take(bytes).await;
    }

    /// Zählt weiter, meldet höchstens alle [`PROGRESS_INTERVAL`] und prüft, ob abgebrochen wurde.
    fn advance(&self, bytes: usize) -> Result<(), Stop> {
        let so_far = self.bytes.fetch_add(bytes as u64, Ordering::Relaxed) + bytes as u64;

        // Die Grenze gilt dem, was wirklich ankommt – nicht der Größe, die der Server vorher genannt hat.
        if self.limit.is_some_and(|limit| so_far > limit) {
            return Err(Stop::Failed(Failure {
                code: "too_large",
                message: "the server sent more than the size it had announced allows".to_owned(),
            }));
        }

        self.check()?;
        self.report();
        Ok(())
    }

    /// Meldet den Stand, wenn die letzte Meldung [`PROGRESS_INTERVAL`] her ist.
    fn report(&self) {
        let due = self
            .reported
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .elapsed()
            >= PROGRESS_INTERVAL;
        if due {
            self.emit(TransferPhase::Running, None);
        }
    }

    /// Ob der Benutzer den Transfer abgebrochen hat (Abbrechen, Tab zu) – auch wenn die Datei, die gerade lief, das
    /// als Fehler der Verbindung erlebt hat.
    fn cancelled(&self) -> bool {
        *self.halt.borrow() == Some(Halt::Cancelled)
    }

    fn check(&self) -> Result<(), Stop> {
        match *self.halt.borrow() {
            None => Ok(()),
            Some(Halt::Cancelled) => Err(Stop::Cancelled),
            Some(Halt::Lost) => Err(Stop::Failed(Failure {
                code: "connection",
                message: "the connection was closed".to_owned(),
            })),
        }
    }
}

/// Läuft, bis der Transfer fertig, gescheitert oder abgebrochen ist, und meldet das Ende.
pub(crate) async fn run(
    shared: Arc<Shared>,
    sftp: Arc<SftpSession>,
    job: Job,
    mut halt: watch::Receiver<Option<Halt>>,
) {
    // Was der Versuch davor geschafft hat, gilt nur für einen, den die App als neuen Versuch ausweist. Jeder andere
    // Transfer derselben Dateien beginnt von vorn – und was der alte liegen ließ, ist damit überholt.
    let key = job.key();
    let earlier = shared.trails.take(&key).filter(|_| job.resume.is_some());
    let progress = Progress {
        shared: &shared,
        transfer: job.transfer,
        halt: halt.clone(),
        bytes: AtomicU64::new(0),
        total: AtomicU64::new(0),
        files_done: AtomicU64::new(0),
        files: AtomicU64::new(0),
        resumed: AtomicU64::new(0),
        reported: Mutex::new(Instant::now()),
        trail: Arc::new(Mutex::new(earlier.unwrap_or_default())),
        limit: job.limit,
        keep: job.keep,
        exclude: job.exclude.clone(),
    };
    shared.trails.start(job.transfer, key.clone(), progress.trail.clone());
    progress.emit(TransferPhase::Queued, None);

    // Auf einen freien Platz warten – oder auf den Abbruch, je nachdem, was zuerst kommt.
    let slot = tokio::select! {
        slot = shared.transfer_slots.acquire(job.ticket) => Some(slot),
        _ = halt.wait_for(Option::is_some) => {
            shared.transfer_slots.leave(job.ticket);
            None
        }
    };

    let outcome = match slot {
        Some(_slot) => {
            progress.emit(TransferPhase::Running, None);
            // Auf einer langsamen Leitung liefe sonst die Frist der vorausgeschickten Anfragen ab.
            let _patient = crate::metered::patient(&sftp);
            let _patient_target = job.relay.as_deref().map(crate::metered::patient);
            match job.direction {
                Direction::Upload => {
                    upload(
                        &sftp,
                        &job.local,
                        &job.remote,
                        job.name.as_deref(),
                        job.only.as_deref(),
                        job.resume,
                        &progress,
                    )
                    .await
                }
                Direction::Relay => match (&job.relay, job.local.to_str()) {
                    (Some(target), Some(directory)) => {
                        relay(
                            &sftp,
                            &job.remote,
                            job.name.as_deref(),
                            target,
                            directory,
                            job.resume.is_some(),
                            &progress,
                        )
                        .await
                    }
                    _ => Err(unsafe_name(&job.local.display().to_string()).into()),
                },
                Direction::Download => {
                    download(
                        &sftp,
                        &job.remote,
                        &job.local,
                        job.name.as_deref(),
                        job.only.as_deref(),
                        &progress,
                    )
                    .await
                }
            }
        }
        None => Err(progress.check().err().unwrap_or(Stop::Cancelled)),
    };

    // Schließt der Benutzer die Sitzung, erlebt die Datei, die gerade läuft, das als Fehler der Verbindung.
    // Gewollt war es trotzdem.
    let outcome = match outcome {
        Err(Stop::Failed(_)) if progress.cancelled() => Err(Stop::Cancelled),
        other => other,
    };

    // Erst festhalten, was dieser Versuch geschafft hat (nach einem guten Ende fällt die Spur), dann den Transfer
    // vergessen, dann das Ende melden: Wer das Ende gesehen hat, findet den Transfer im Kern nicht mehr vor – und
    // für einen neuen Versuch schon alles vor. Wer wartet, bis der Kern keinen Transfer mehr kennt (sein Ende), kann
    // sich darauf verlassen, dass die Spuren gespeichert sind.
    let trail = std::mem::take(&mut *progress.trail());
    shared.trails.finish(job.transfer);
    if outcome.is_err() && !trail.0.is_empty() {
        shared.trails.keep(key, trail);
    } else {
        drop(trail);
    }

    shared.trails.save();
    shared.forget_transfer(job.transfer);

    match outcome {
        Ok(()) => {
            progress
                .bytes
                .store(progress.total.load(Ordering::Relaxed), Ordering::Relaxed);
            progress.emit(TransferPhase::Completed, None);
        }
        Err(Stop::Cancelled) => progress.emit(TransferPhase::Cancelled, None),
        Err(Stop::Failed(failure)) => progress.emit(
            TransferPhase::Failed,
            Some(format!("{}: {}", failure.code, failure.message)),
        ),
    }
}

/// Lässt alle Aufgaben nebeneinander laufen und wartet, bis jede fertig ist.
async fn join_all<F: Future>(futures: Vec<F>) -> Vec<F::Output> {
    let mut running: Vec<_> = futures.into_iter().map(|future| (Box::pin(future), None)).collect();
    std::future::poll_fn(|context| {
        let mut pending = false;
        for (future, output) in &mut running {
            if output.is_none() {
                match future.as_mut().poll(context) {
                    Poll::Ready(value) => *output = Some(value),
                    Poll::Pending => pending = true,
                }
            }
        }

        if pending { Poll::Pending } else { Poll::Ready(()) }
    })
    .await;
    running.into_iter().filter_map(|(_, output)| output).collect()
}

/// Arbeitet die Dateien eines Verzeichnisses ab, höchstens `at_once` zugleich ([`files_at_once`]). Scheitert eine oder wird
/// abgebrochen, beginnt keine neue mehr; die laufenden kommen noch zu ihrem Ende und räumen dabei ihre Teil-Datei
/// selbst weg – deshalb wird hier auf alle gewartet, statt beim ersten Fehler alles fallen zu lassen.
async fn each<T, W, R>(items: Vec<T>, at_once: usize, work: W) -> Result<(), Stop>
where
    W: Fn(T) -> R,
    R: Future<Output = Result<(), Stop>>,
{
    map(items, at_once, work).await.map(|_| ())
}

/// Wie [`each`], mit einem Ergebnis je Eintrag: Höchstens `at_once` laufen zugleich, die Ergebnisse stehen in der
/// Reihenfolge der Einträge – gleich, welcher zuerst fertig war.
async fn map<T, W, R, O>(items: Vec<T>, at_once: usize, work: W) -> Result<Vec<O>, Stop>
where
    W: Fn(T) -> R,
    R: Future<Output = Result<O, Stop>>,
{
    let results: Mutex<Vec<Option<O>>> = Mutex::new(items.iter().map(|_| None).collect());
    let queue = Mutex::new(items.into_iter().enumerate());
    let stopped = AtomicBool::new(false);
    let (queue, results, stopped, work) = (&queue, &results, &stopped, &work);
    let worker = || async move {
        loop {
            if stopped.load(Ordering::Relaxed) {
                return Ok(());
            }

            let next = queue.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).next();
            let Some((index, item)) = next else {
                return Ok(());
            };
            match work(item).await {
                Ok(output) => results.lock().unwrap_or_else(|poisoned| poisoned.into_inner())[index] = Some(output),
                Err(stop) => {
                    stopped.store(true, Ordering::Relaxed);
                    return Err(stop);
                }
            }
        }
    };

    let workers: Vec<_> = (0..at_once).map(|_| worker()).collect();
    join_all(workers).await.into_iter().collect::<Result<(), Stop>>()?;
    Ok(results
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .drain(..)
        .flatten()
        .collect())
}

async fn upload(
    sftp: &SftpSession,
    local: &Path,
    remote_directory: &str,
    name: Option<&str>,
    only: Option<&[String]>,
    resume: Option<u64>,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    // Ohne eigenen Namen heißt das Ziel wie die lokale Datei. Ein genannter Name muss ein einzelner Name sein –
    // das Ziel liegt immer im genannten Verzeichnis.
    let name = match name {
        Some(name) if name.is_empty() || name == "." || name == ".." || name.contains('/') => {
            return Err(unsafe_name(name).into());
        }
        Some(name) => name,
        None => local
            .file_name()
            .and_then(|name| name.to_str())
            .ok_or_else(|| unsafe_name(&local.display().to_string()))?,
    };
    let target = join(remote_directory, name);

    let metadata = tokio::fs::symlink_metadata(local).await?;
    if !metadata.is_dir() {
        // Ob die Datei hier noch dieselbe ist, hat die App geprüft, bevor sie `resume` nannte.
        progress.expect(metadata.len());
        progress.expect_files(1);
        let file = Outgoing {
            local: local.to_owned(),
            remote: target,
            stamp: Stamp::local(&metadata),
        };
        // Wie viel geschickt war, nennt die App – nach einem Absturz weiß es nur die Spur des Kerns (unterwegs
        // mitgeschrieben, siehe [`copy_up`]). Die ist dann die genauere Zahl.
        let earlier = progress
            .earlier(&file.source(), file.stamp)
            .or(resume.map(Reached::Sent));
        return upload_file(sftp, &file, false, earlier, progress).await;
    }

    // Bei einem Verzeichnis sagt die Zahl der App nichts über die einzelne Datei: Mehrere liefen nebeneinander.
    // Wie weit jede kam, weiß der Kern selbst ([`Trail`]) – und prüft auch selbst, ob sie hier noch dieselbe ist.

    // Erst den ganzen Baum erfassen: Dann steht die Gesamtgröße fest, bevor das erste Byte fließt.
    let mut directories = vec![(local.to_owned(), target.clone())];
    let mut files = Vec::new();
    let mut index = 0;
    while index < directories.len() {
        let (directory, remote) = directories[index].clone();
        index += 1;
        let mut entries = tokio::fs::read_dir(&directory).await?;
        while let Some(entry) = entries.next_entry().await? {
            progress.check()?;
            let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
                continue;
            };
            // Was der Benutzer ausschließt, bleibt hier – und ein ausgeschlossener Ordner wird nicht betreten.
            if below(&join(&remote, &name), &target).is_some_and(|relative| progress.exclude.skips(relative)) {
                continue;
            }

            // Links werden übersprungen statt verfolgt: Ein Link im Kreis würde nie enden.
            let kind = entry.file_type().await?;
            if kind.is_dir() {
                directories.push((entry.path(), join(&remote, &name)));
            } else if kind.is_file() {
                let metadata = entry.metadata().await?;
                files.push(Outgoing {
                    local: entry.path(),
                    remote: join(&remote, &name),
                    stamp: Stamp::local(&metadata),
                });
            }
        }
    }

    // Ein Abgleich schickt nur die genannten Dateien – und legt nur die Verzeichnisse an, in denen eine von ihnen
    // liegt.
    if let Some(only) = only {
        let wanted: HashSet<&str> = only.iter().map(String::as_str).collect();
        files.retain(|file| below(&file.remote, &target).is_some_and(|relative| wanted.contains(relative)));
        directories
            .retain(|(_, remote)| *remote == target || files.iter().any(|file| below(&file.remote, remote).is_some()));
    }

    for file in &files {
        progress.expect(file.stamp.size);
    }
    progress.expect_files(files.len());

    // Verzeichnisse der Reihe nach, Eltern vor Kindern. In einem, das gerade erst entstanden ist, kann noch nichts
    // liegen: Dort braucht weder ein Unterverzeichnis noch eine Datei die Frage, ob es sie schon gibt – bei
    // tausenden Dateien ist das ein Viertel aller Anfragen.
    let mut created = HashSet::new();
    for (index, (_, remote)) in directories.iter().enumerate() {
        progress.check()?;
        let parent_created = index > 0 && created.contains(parent(remote));
        if parent_created || !sftp.try_exists(remote.as_str()).await? {
            sftp.create_dir(remote.as_str()).await?;
            created.insert(remote.as_str());
        }
    }

    let created = &created;
    each(files, files_at_once(&[sftp]), |file| async move {
        // In einem Verzeichnis, das gerade erst entstanden ist, liegt auch vom Versuch davor nichts mehr.
        let fresh = created.contains(parent(&file.remote));
        let earlier = progress.earlier(&file.source(), file.stamp).filter(|_| !fresh);
        upload_file(sftp, &file, fresh, earlier, progress).await
    })
    .await
}

/// Der Pfad von `path` unterhalb des Verzeichnisses `directory`; `None`, wenn er nicht darin liegt.
fn below<'a>(path: &'a str, directory: &str) -> Option<&'a str> {
    path.strip_prefix(directory)?.strip_prefix('/')
}

/// Das Verzeichnis, in dem ein SFTP-Pfad liegt.
fn parent(path: &str) -> &str {
    path.rsplit_once('/').map_or("", |(parent, _)| parent)
}

/// Eine Datei, die ein Upload schickt.
struct Outgoing {
    local: PathBuf,
    remote: String,
    /// Wie sie aussah, als der Transfer sie erfasst hat.
    stamp: Stamp,
}

impl Outgoing {
    /// Unter diesem Namen steht sie in der [`Trail`].
    fn source(&self) -> String {
        self.local.to_string_lossy().into_owned()
    }
}

/// Lädt eine Datei hoch und hält fest, wie weit sie kam. `fresh`: Das Zielverzeichnis hat dieser Transfer selbst
/// angelegt – die Datei kann es dort noch nicht geben. `earlier`: wie weit sie bei einem Versuch davor kam.
async fn upload_file(
    sftp: &SftpSession,
    file: &Outgoing,
    fresh: bool,
    earlier: Option<Reached>,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    // Die Regel des Benutzers für das, was es am Ziel schon gibt: Bleibt die Datei dort, zählt sie als erledigt.
    if !fresh
        && let Some(keep) = progress.keep
        && let Ok(existing) = sftp.metadata(file.remote.as_str()).await
        && !is_special(&existing)
        && keep.leaves(
            file.stamp.size,
            file.stamp.modified.map(|modified| modified.as_secs()),
            existing.size,
            existing.mtime.map(u64::from),
        )
    {
        progress.skip(file.stamp.size);
        progress.note(&file.source(), file.stamp, Reached::Whole);
        progress.file_done();
        return Ok(());
    }

    let mut sent = 0;
    let result = send_file(sftp, file, fresh, earlier, &mut sent, progress).await;
    match &result {
        Ok(()) => {
            progress.note(&file.source(), file.stamp, Reached::Whole);
            progress.file_done();
        }
        Err(_) if sent > 0 => progress.note(&file.source(), file.stamp, Reached::Sent(sent)),
        Err(_) => {}
    }

    result
}

async fn send_file(
    sftp: &SftpSession,
    file: &Outgoing,
    fresh: bool,
    earlier: Option<Reached>,
    sent: &mut u64,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    let (local, remote) = (file.local.as_path(), file.remote.as_str());
    let resume = match earlier {
        Some(Reached::Sent(bytes)) => Some(bytes),
        _ => None,
    };
    // Unterwegs festhalten, wie viel geschickt ist: Stürzt die App ab, weiß der nächste Versuch sonst nichts davon.
    let noted = |sent| progress.note(&file.source(), file.stamp, Reached::Sent(sent));

    // Eine vorhandene Datei wird an Ort und Stelle überschrieben: Nur so behält sie Besitzer, Rechte und harte
    // Links. Bricht das ab, bleibt sie unvollständig zurück – ein neuer Versuch setzt dort fort.
    if !fresh {
        match sftp.metadata(remote).await {
            // In eine Pipe oder ein Gerät wird nicht geschrieben – siehe `is_special`.
            Ok(existing) if is_special(&existing) => return Err(not_a_file(remote).into()),
            // Der Versuch davor hat sie vollständig geschickt, und sie ist dort noch so groß wie hier.
            Ok(existing) if earlier == Some(Reached::Whole) && existing.size == Some(file.stamp.size) => {
                progress.skip(file.stamp.size);
                return Ok(());
            }
            Ok(existing) => {
                let from = resume_point(sftp, local, remote, existing.size, resume).await;
                return copy_up(sftp, local, remote, from, sent, progress, &noted).await;
            }
            Err(error) => {
                let failure = Failure::from(error);
                if failure.code != "not_found" {
                    return Err(failure.into());
                }
            }
        }
    }

    // Eine neue Datei entsteht erst unter einem Nachbarnamen: Ein abgebrochener Upload hinterlässt dann nichts.
    // Nur wenn die Verbindung riss, ließ sich die Teil-Datei nicht mehr wegräumen – dann geht es in ihr weiter.
    let partial = format!("{remote}{PARTIAL_SUFFIX}");
    let from = match resume {
        Some(_) => match sftp.metadata(partial.as_str()).await {
            Ok(left) if !is_special(&left) => resume_point(sftp, local, &partial, left.size, resume).await,
            _ => 0,
        },
        None => 0,
    };
    let result = match copy_up(sftp, local, &partial, from, sent, progress, &noted).await {
        Ok(()) => settle(sftp, &partial, remote).await,
        Err(stop) => Err(stop),
    };
    // Weggeräumt wird die Teil-Datei, wenn der Transfer abgebrochen wurde oder an etwas anderem scheiterte. Endet
    // er wie bei einem Abriss – auch wenn die App ihn anhält und die Verbindung noch steht –, bleibt sie liegen: In
    // ihr geht es beim nächsten Versuch weiter.
    if matches!(&result, Err(stop) if !stop.lost()) {
        let _ = sftp.remove_file(partial).await;
    }

    result
}

/// Ab welchem Byte ein abgebrochener Upload weitergehen kann – 0, wenn er von vorn beginnen muss.
///
/// `sent` ist, was der abgebrochene Versuch geschickt hat, `size` die Größe dessen, was auf dem Server liegt.
/// Weiter geht es am kleineren von beiden: Mehr als geschickt wurde, stammt nicht von hier, und mehr als drüben
/// liegt, ist nicht angekommen. Davor muss der Server haben, was in der lokalen Datei steht – verglichen wird das
/// Stück vor der Stelle ([`RESUME_CHECK`]), nicht die ganze Datei: Die noch einmal zu holen, kostete, was das
/// Fortsetzen spart. Passt etwas nicht oder lässt es sich nicht prüfen, beginnt der Upload von vorn.
async fn resume_point(sftp: &SftpSession, local: &Path, remote: &str, size: Option<u64>, sent: Option<u64>) -> u64 {
    let (Some(size), Some(sent)) = (size, sent) else {
        return 0;
    };
    let Ok(source) = tokio::fs::metadata(local).await else {
        return 0;
    };
    // Mehr als die lokale Datei kann der abgebrochene Versuch nicht hinterlassen haben – das hat jemand anders
    // geschrieben. Fortsetzen ließe dessen Ende stehen.
    if size > source.len() {
        return 0;
    }

    let from = size.min(sent);
    let start = from.saturating_sub(RESUME_CHECK);
    match same_bytes(sftp, local, remote, start, (from - start) as usize).await {
        Ok(true) => from,
        _ => 0,
    }
}

/// Ob Server und lokale Datei an derselben Stelle dasselbe enthalten.
async fn same_bytes(sftp: &SftpSession, local: &Path, remote: &str, start: u64, length: usize) -> Result<bool, Stop> {
    if length == 0 {
        return Ok(false);
    }

    let (mut here, mut there) = (vec![0; length], vec![0; length]);
    let mut source = tokio::fs::File::open(local).await?;
    source.seek(std::io::SeekFrom::Start(start)).await?;
    source.read_exact(&mut here).await?;

    let mut target = sftp.open(remote).await?;
    target.seek(std::io::SeekFrom::Start(start)).await.map_err(remote_io)?;
    target.read_exact(&mut there).await.map_err(remote_io)?;
    Ok(here == there)
}

/// Schreibt die lokale Datei auf den Server – ab `from`; was davor liegt, ist schon dort ([`resume_point`]).
/// `sent` nennt auch nach einem Fehler, bis wohin geschickt wurde, `noted` erfährt es unterwegs höchstens alle
/// [`SAVE_INTERVAL`]. Mehr als die Wahrheit ist das nie: Die Zahl hinkt hinterher, und was sie nennt, ist geschickt
/// – ob es ankam, prüft ein neuer Versuch ohnehin am Server.
async fn copy_up(
    sftp: &SftpSession,
    local: &Path,
    remote: &str,
    from: u64,
    sent: &mut u64,
    progress: &Progress<'_>,
    noted: &(dyn Fn(u64) + Sync),
) -> Result<(), Stop> {
    *sent = from;
    let mut source = tokio::fs::File::open(local).await?;
    let mut target = if from == 0 {
        sftp.create(remote).await?
    } else {
        // Öffnen, ohne zu kürzen: Was der abgebrochene Versuch geschrieben hat, bleibt stehen.
        let mut target = sftp.open_with_flags(remote, OpenFlags::WRITE).await?;
        source.seek(std::io::SeekFrom::Start(from)).await?;
        target.seek(std::io::SeekFrom::Start(from)).await.map_err(remote_io)?;
        progress.resume(from);
        target
    };
    let mut buffer = vec![0; CHUNK];
    let mut noted_at = Instant::now();
    loop {
        let read = source.read(&mut buffer).await?;
        if read == 0 {
            break;
        }

        target.write_all(&buffer[..read]).await.map_err(remote_io)?;
        *sent += read as u64;
        if noted_at.elapsed() >= SAVE_INTERVAL {
            noted(*sent);
            noted_at = Instant::now();
        }

        progress.advance(read)?;
        progress.throttle(read).await;
    }

    target.shutdown().await.map_err(remote_io)?;
    Ok(())
}

/// Etwas, das ein Download anlegt.
struct Planned {
    remote: String,
    local: PathBuf,
    /// Bei Verzeichnissen 0.
    size: u64,
    modified: Option<u64>,
}

/// Was ein Download anlegt, in der Reihenfolge, in der er es tut.
struct Plan {
    /// Jedes Verzeichnis vor denen, die in ihm liegen.
    directories: Vec<Planned>,
    files: Vec<Planned>,
    /// Was die Ausschlüsse ausgelassen haben – jeweils der oberste Eintrag, mit „ist ein Verzeichnis“.
    skipped: Vec<(Planned, bool)>,
}

/// Erfasst, was ein Download von `remote` unter `target` anlegt. Der Download selbst und die Auskunft vorab
/// ([`tree`]) gehen beide hier durch – so nennt die Auskunft genau die Namen, unter denen die Dateien dann ankommen.
/// `seen` bekommt die Größe jedes Eintrags (bei Verzeichnissen 0) und kann abbrechen.
async fn plan(
    sftp: &SftpSession,
    remote: &str,
    target: PathBuf,
    exclude: &Excludes,
    mut seen: impl FnMut(u64) -> Result<(), Stop>,
) -> Result<Plan, Stop> {
    let attributes = sftp.metadata(remote).await?;
    if is_special(&attributes) {
        return Err(not_a_file(remote).into());
    }

    let top = Planned {
        remote: remote.to_owned(),
        local: target,
        size: 0,
        modified: attributes.mtime.map(u64::from),
    };
    if !attributes.file_type().is_dir() {
        let size = crate::sftp::shown_size(attributes.size);
        seen(size)?;
        return Ok(Plan {
            directories: Vec::new(),
            files: vec![Planned { size, ..top }],
            skipped: Vec::new(),
        });
    }

    seen(0)?;
    let mut directories = vec![top];
    let mut files = Vec::new();
    let mut skipped = Vec::new();
    let mut listed = 0;
    while listed < directories.len() {
        // Die nächsten Verzeichnisse zugleich auflisten. Ausgewertet werden sie danach der Reihe nach: So steht
        // alles dort, wo es auch stünde, wenn ein Verzeichnis nach dem anderen an die Reihe käme.
        let round: Vec<_> = directories[listed..]
            .iter()
            .take(DIRECTORIES_PER_ROUND)
            .map(|directory| (directory.remote.clone(), directory.local.clone()))
            .collect();
        listed += round.len();
        let listings = map(round, DIRECTORIES_AT_ONCE, |(directory, local)| async move {
            // In fester Reihenfolge: Welcher von zwei Namen, die hier zusammenfallen, den Zusatz bekommt, hängt
            // dann nicht davon ab, wie der Server sie gerade aufzählt.
            let mut entries: Vec<_> = crate::sftp::read_dir(sftp, directory.as_str()).await?.collect();
            entries.sort_by_key(|entry| entry.file_name());
            Ok::<_, Stop>((local, entries))
        })
        .await?;

        for (local, entries) in listings {
            let mut taken = Taken::default();
            for entry in entries {
                // Links werden übersprungen statt verfolgt, Pipes und Geräte nie geöffnet (`is_special`).
                let kind = entry.file_type();
                let metadata = entry.metadata();
                let wanted = kind.is_dir() || (!kind.is_symlink() && !is_special(&metadata));
                if !wanted {
                    continue;
                }

                // Der Name kommt vom Server und wird Teil eines lokalen Pfads – siehe `local_name`.
                let file_name = entry.file_name();
                // Ausschlüsse: Was ausgelassen wird, zählt nicht – belegt aber weiter seinen Namen auf diesem Rechner.
                // Sonst hieße eine andere Datei mit Ausschluss anders als ohne (`a:b.txt` und `a_b.txt` fallen hier
                // zusammen) und überschriebe, was ein früherer Download unter dem Namen abgelegt hat (Hinweis von
                // Codex).
                let entry_path = entry.path();
                if below(&entry_path, remote.trim_end_matches('/')).is_some_and(|relative| exclude.skips(relative)) {
                    let claimed = local_name(&file_name).map(|name| taken.claim(name));
                    skipped.push((
                        Planned {
                            remote: entry_path,
                            local: local.join(claimed.unwrap_or_default()),
                            size: 0,
                            modified: metadata.mtime.map(u64::from),
                        },
                        kind.is_dir(),
                    ));
                    continue;
                }

                let name = taken.claim(local_name(&file_name).ok_or_else(|| unsafe_name(&file_name))?);
                let planned = Planned {
                    remote: entry.path(),
                    local: local.join(name),
                    size: if kind.is_dir() {
                        0
                    } else {
                        crate::sftp::shown_size(metadata.size)
                    },
                    modified: metadata.mtime.map(u64::from),
                };
                seen(planned.size)?;
                if kind.is_dir() {
                    directories.push(planned);
                } else {
                    files.push(planned);
                }
            }
        }
    }

    Ok(Plan {
        directories,
        files,
        skipped,
    })
}

async fn download(
    sftp: &SftpSession,
    remote: &str,
    local_directory: &Path,
    name: Option<&str>,
    only: Option<&[String]>,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    // Ein genannter Name muss einer sein, den dieser Rechner so nimmt – das Ziel liegt immer im genannten Ordner.
    let target = match name {
        Some(name) if local_name(name).as_deref() == Some(name) => local_directory.join(name),
        Some(name) => return Err(unsafe_name(name).into()),
        None => download_target(local_directory, remote).ok_or_else(|| unsafe_name(remote))?,
    };
    // Auch ein Download hat eine Grenze: Ein Server, der in jedem Verzeichnis ein weiteres nennt, ließe den Plan
    // sonst endlos wachsen, bevor ein Byte geschrieben ist.
    let mut count = 0usize;
    let mut plan = plan(sftp, remote, target, &progress.exclude, |size| {
        progress.check()?;
        count += 1;
        if count > DOWNLOAD_LIMIT {
            return Err(Stop::Failed(Failure {
                code: "too_many",
                message: format!("more than {DOWNLOAD_LIMIT} entries"),
            }));
        }

        if only.is_none() {
            progress.expect(size);
        }

        Ok(())
    })
    .await?;

    // Ein Abgleich holt nur die genannten Dateien – und legt nur die Ordner an, in denen eine von ihnen liegt.
    if let Some(only) = only {
        let wanted: HashSet<&str> = only.iter().map(String::as_str).collect();
        plan.files
            .retain(|file| below(&file.remote, remote).is_some_and(|relative| wanted.contains(relative)));
        let files = &plan.files;
        plan.directories.retain(|directory| {
            directory.remote == remote
                || files
                    .iter()
                    .any(|file| below(&file.remote, &directory.remote).is_some())
        });
        for file in &plan.files {
            progress.expect(file.size);
        }
    }
    progress.expect_files(plan.files.len());

    for directory in &plan.directories {
        progress.check()?;
        tokio::fs::create_dir_all(&directory.local).await?;
    }

    each(plan.files, files_at_once(&[sftp]), |file| async move {
        download_file(sftp, &file, progress).await
    })
    .await
}

/// Eine Datei, die von einem Server zum anderen geht.
struct Passing {
    source: String,
    target: String,
    size: u64,
    /// Wann die Quelle zuletzt geändert wurde, in Sekunden – für die Regel des Benutzers ([`Progress::keep`]).
    modified: Option<u64>,
}

/// Von Server zu Server: Datei oder Verzeichnis `remote` des einen in das Verzeichnis `directory` des anderen –
/// gelesen und geschrieben in einem Zug, ohne die Platte dieses Rechners. Die beiden Server müssen einander nicht
/// erreichen; die Bytes laufen über den Kern. Namen bleiben, wie der Quell-Server sie nennt (hier wird nichts
/// zu einem lokalen Pfad) – außer dem obersten, wenn `name` einen anderen nennt („umbenennen“ statt
/// überschreiben). Links werden übersprungen, Pipes und Geräte nie geöffnet – wie beim Download.
///
/// Ein neuer Versuch (`resume`) baut auf dem auf, was am Ziel schon liegt: Eine Datei, die dort vollständig ist,
/// bleibt, und in einer Teil-Datei geht es weiter ([`relay_point`]). Eine Spur wie bei Up- und Download gibt es
/// hier nicht – verglichen wird, was auf den beiden Servern steht.
async fn relay(
    source: &SftpSession,
    remote: &str,
    name: Option<&str>,
    target: &SftpSession,
    directory: &str,
    resume: bool,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    // Wie beim Upload: Ein genannter Name muss ein einzelner Name sein – das Ziel liegt im genannten Verzeichnis.
    let name = match name {
        Some(name) if crate::sftp::leaves_directory(name) => return Err(unsafe_name(name).into()),
        Some(name) => name,
        None => remote
            .trim_end_matches('/')
            .rsplit('/')
            .next()
            .filter(|name| !name.is_empty() && *name != "." && *name != "..")
            .ok_or_else(|| unsafe_name(remote))?,
    };
    let top = join(directory, name);

    let attributes = source.metadata(remote).await?;
    if is_special(&attributes) {
        return Err(not_a_file(remote).into());
    }

    if !attributes.file_type().is_dir() {
        let size = crate::sftp::shown_size(attributes.size);
        progress.expect(size);
        progress.expect_files(1);
        let file = Passing {
            source: remote.to_owned(),
            target: top,
            size,
            modified: attributes.mtime.map(u64::from),
        };
        return relay_file(source, target, &file, resume, progress).await;
    }

    // Erst den ganzen Baum erfassen: Dann steht die Gesamtgröße fest, bevor das erste Byte fließt.
    let mut directories = vec![(remote.trim_end_matches('/').to_owned(), top)];
    let mut files = Vec::new();
    let mut index = 0;
    while index < directories.len() {
        let (from, to) = directories[index].clone();
        index += 1;
        progress.check()?;
        let mut entries: Vec<_> = crate::sftp::read_dir(source, from.as_str()).await?.collect();
        entries.sort_by_key(|entry| entry.file_name());
        for entry in entries {
            let (kind, metadata, name) = (entry.file_type(), entry.metadata(), entry.file_name());
            if kind.is_symlink() || name.contains('/') || name == "." || name == ".." {
                continue;
            }

            if below(&entry.path(), remote.trim_end_matches('/'))
                .is_some_and(|relative| progress.exclude.skips(relative))
            {
                continue;
            }

            if kind.is_dir() {
                directories.push((entry.path(), join(&to, &name)));
            } else if !is_special(&metadata) {
                let size = crate::sftp::shown_size(metadata.size);
                progress.expect(size);
                files.push(Passing {
                    source: entry.path(),
                    target: join(&to, &name),
                    size,
                    modified: metadata.mtime.map(u64::from),
                });
            }
        }
    }
    progress.expect_files(files.len());

    for (_, to) in &directories {
        progress.check()?;
        if !target.try_exists(to.as_str()).await? {
            target.create_dir(to.as_str()).await?;
        }
    }

    each(files, files_at_once(&[source, target]), |file| async move {
        relay_file(source, target, &file, resume, progress).await
    })
    .await
}

/// Eine Datei von Server zu Server. Eine neue entsteht erst unter einem Nachbarnamen und bekommt ihren Namen, wenn
/// sie vollständig ist; eine vorhandene wird an Ort und Stelle überschrieben – wie beim Upload.
async fn relay_file(
    source: &SftpSession,
    target: &SftpSession,
    file: &Passing,
    resume: bool,
    progress: &Progress<'_>,
) -> Result<(), Stop> {
    let exists = match target.metadata(file.target.as_str()).await {
        Ok(existing) if is_special(&existing) => return Err(not_a_file(&file.target).into()),
        // Die Regel des Benutzers für das, was es am Ziel schon gibt: Bleibt die Datei dort, zählt sie als erledigt.
        Ok(existing)
            if progress.keep.is_some_and(|keep| {
                keep.leaves(file.size, file.modified, existing.size, existing.mtime.map(u64::from))
            }) =>
        {
            progress.skip(file.size);
            progress.file_done();
            return Ok(());
        }
        // Ein neuer Versuch: Was am Ziel schon vollständig liegt – gleich groß und am Ende gleich –, bleibt.
        Ok(existing)
            if resume
                && existing.size == Some(file.size)
                && same_on_both(source, &file.source, target, &file.target, file.size).await =>
        {
            progress.skip(file.size);
            progress.file_done();
            return Ok(());
        }
        Ok(_) => true,
        Err(error) => {
            let failure = Failure::from(error);
            if failure.code != "not_found" {
                return Err(failure.into());
            }

            false
        }
    };
    let written = if exists {
        file.target.clone()
    } else {
        format!("{}{PARTIAL_SUFFIX}", file.target)
    };

    // In der Teil-Datei eines abgebrochenen Versuchs geht es weiter – wenn sie zu dieser Quelle gehört.
    let start = if resume && !exists {
        relay_point(source, target, file, &written).await
    } else {
        0
    };

    let copied = async {
        let mut from = source.open(file.source.as_str()).await?;
        let mut to = if start == 0 {
            target.create(written.as_str()).await?
        } else {
            // Öffnen, ohne zu kürzen: Was der abgebrochene Versuch geschrieben hat, bleibt stehen.
            let mut to = target.open_with_flags(written.as_str(), OpenFlags::WRITE).await?;
            from.seek(std::io::SeekFrom::Start(start)).await.map_err(remote_io)?;
            to.seek(std::io::SeekFrom::Start(start)).await.map_err(remote_io)?;
            progress.resume(start);
            to
        };
        let mut buffer = vec![0; CHUNK];
        let mut left = file.size.saturating_sub(start);
        loop {
            let read = from.read(&mut buffer).await.map_err(remote_io)?;
            if read == 0 {
                break;
            }

            to.write_all(&buffer[..read]).await.map_err(remote_io)?;

            // Wächst die Quelle unterwegs, zählt für den Fortschritt nur, was angekündigt war.
            let counted = (read as u64).min(left);
            left -= counted;
            progress.advance(counted as usize)?;
            progress.throttle(counted as usize).await;
        }

        to.shutdown().await.map_err(remote_io)?;
        Ok::<(), Stop>(())
    }
    .await;

    let result = match (copied, exists) {
        (Ok(()), false) => settle(target, &written, &file.target).await,
        (result, _) => result,
    };
    match &result {
        Ok(()) => progress.file_done(),
        // Wie beim Upload: Nach einem Abriss (oder angehalten) bleibt die Teil-Datei für den nächsten Versuch.
        Err(stop) if !exists && !stop.lost() => {
            let _ = target.remove_file(written).await;
        }
        Err(_) => {}
    }

    result
}

/// Legt eine fertige Teil-Datei unter ihren Namen. Den gab es beim Blick davor nicht – entsteht er unterwegs (über
/// FTP genügt dafür, dass die Liste des Servers ein paar Sekunden alt war), überschreiben manche Server beim
/// Umbenennen nicht (IIS, der Testserver). Dann scheitert der Transfer mit genau diesem Grund statt mit „Failure“;
/// still überschrieben wird nicht – was mit Vorhandenem geschieht, hat der Benutzer für eine Datei entschieden, die
/// es da noch nicht gab. Ein neuer Versuch sieht sie und fragt wie gewohnt.
async fn settle(sftp: &SftpSession, partial: &str, target: &str) -> Result<(), Stop> {
    match sftp.rename(partial, target).await {
        Ok(()) => Ok(()),
        Err(error) => match sftp.metadata(target).await {
            Ok(_) => Err(Failure {
                code: "exists",
                message: format!("{target} appeared on the server during the transfer"),
            }
            .into()),
            Err(_) => Err(Stop::from(error)),
        },
    }
}

/// Ab welchem Byte ein abgebrochener Transfer zwischen zwei Servern weitergehen kann – 0, wenn er von vorn beginnen
/// muss. Die Teil-Datei am Ziel darf nicht länger sein als die Quelle, und ihr Ende (das letzte MiB,
/// [`RESUME_CHECK`]) muss dem entsprechen, was die Quelle an derselben Stelle hat.
async fn relay_point(source: &SftpSession, target: &SftpSession, file: &Passing, partial: &str) -> u64 {
    match target.metadata(partial).await {
        Ok(left) if !is_special(&left) => match left.size {
            Some(size)
                if size > 0 && size <= file.size && same_on_both(source, &file.source, target, partial, size).await =>
            {
                size
            }
            _ => 0,
        },
        _ => 0,
    }
}

/// Ob zwei Dateien auf zwei Servern vor der Stelle `end` dasselbe enthalten – verglichen wird das letzte MiB davor.
/// Lässt es sich nicht prüfen, gilt es als verschieden.
async fn same_on_both(source: &SftpSession, path: &str, target: &SftpSession, other: &str, end: u64) -> bool {
    let start = end.saturating_sub(RESUME_CHECK);
    let length = (end - start) as usize;
    if length == 0 {
        return true;
    }

    matches!(
        (
            read_at(source, path, start, length).await,
            read_at(target, other, start, length).await
        ),
        (Some(here), Some(there)) if here == there
    )
}

/// Liest `length` Bytes einer Datei des Servers ab `start`; `None`, wenn das nicht geht.
async fn read_at(sftp: &SftpSession, path: &str, start: u64, length: usize) -> Option<Vec<u8>> {
    let mut file = sftp.open(path).await.ok()?;
    file.seek(std::io::SeekFrom::Start(start)).await.ok()?;
    let mut bytes = vec![0; length];
    file.read_exact(&mut bytes).await.ok()?;
    Some(bytes)
}

/// Was ein Download von `remote` anlegen würde: erst die Verzeichnisse, dann die Dateien, mit den Namen, die sie auf
/// diesem Rechner bekämen – ohne etwas zu laden. Mehr als [`TREE_LIMIT`] Einträge ergeben `too_many`.
/// Was auf `exclude` passt, fehlt darin und wird nicht betreten. Mit `excluded` kommt stattdessen genau das zurück,
/// was ausgelassen würde (jeweils der oberste Eintrag) – damit die App vor einem Transfer zeigen kann, was fehlt.
pub(crate) async fn tree(
    sftp: &SftpSession,
    remote: &str,
    exclude: &Excludes,
    excluded: bool,
) -> Result<Vec<TreeEntry>, Failure> {
    // Das Wurzelverzeichnis hat keinen Namen, unter dem es hier ankäme – herunterladen lässt es sich deshalb nicht.
    // Aufzählen aber schon (ein Abgleich mit `/` braucht das): Dann steht ein Platzhalter für den Ordner selbst.
    let target = match download_target(Path::new(""), remote) {
        Some(target) => target,
        None if !remote.is_empty() && remote.chars().all(|c| c == '/') => PathBuf::from("_"),
        None => return Err(unsafe_name(remote)),
    };
    let mut count = 0;
    let plan = plan(sftp, remote, target, exclude, |_| {
        count += 1;
        if count > TREE_LIMIT {
            return Err(Stop::Failed(Failure {
                code: "too_many",
                message: format!("more than {TREE_LIMIT} entries"),
            }));
        }

        Ok(())
    })
    .await;
    let plan = match plan {
        Ok(plan) => plan,
        Err(Stop::Failed(failure)) => return Err(failure),
        Err(Stop::Cancelled) => {
            return Err(Failure {
                code: "failed",
                message: "cancelled".to_owned(),
            });
        }
    };

    let entry = |planned: Planned, directory: bool| TreeEntry {
        remote: planned.remote,
        // Immer mit `/`, egal welches Trennzeichen dieser Rechner benutzt.
        local: planned
            .local
            .iter()
            .map(|part| part.to_string_lossy())
            .collect::<Vec<_>>()
            .join("/"),
        directory,
        size: planned.size,
        modified: planned.modified,
    };
    if excluded {
        return Ok(plan
            .skipped
            .into_iter()
            .map(|(planned, directory)| entry(planned, directory))
            .collect());
    }

    Ok(plan
        .directories
        .into_iter()
        .map(|planned| entry(planned, true))
        .chain(plan.files.into_iter().map(|planned| entry(planned, false)))
        .collect())
}

async fn download_file(sftp: &SftpSession, file: &Planned, progress: &Progress<'_>) -> Result<(), Stop> {
    // Erst in eine Nachbardatei: Ein abgebrochener Download hinterlässt nie eine halbe Datei unter dem
    // richtigen Namen und zerstört auch keine vorhandene.
    let (remote, local) = (file.remote.as_str(), file.local.as_path());
    let partial = partial_path(local);
    let stamp = Stamp::remote(file);

    // Die Regel des Benutzers für das, was es hier schon gibt: Bleibt die Datei, zählt sie als erledigt.
    if let Some(keep) = progress.keep
        && let Ok(here) = tokio::fs::metadata(local).await
        && here.is_file()
        && keep.leaves(
            file.size,
            file.modified,
            Some(here.len()),
            here.modified()
                .ok()
                .and_then(|modified| modified.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|modified| modified.as_secs()),
        )
    {
        progress.skip(file.size);
        progress.note(remote, stamp, Reached::Whole);
        progress.file_done();
        return Ok(());
    }

    // Was ein Versuch davor von dieser Datei geholt hat, gilt weiter, solange sie auf dem Server dieselbe ist.
    let mut from = 0;
    match progress.earlier(remote, stamp) {
        Some(Reached::Whole) => {
            let here = tokio::fs::metadata(local).await;
            if here.is_ok_and(|here| here.is_file() && here.len() == file.size) {
                progress.skip(file.size);
                progress.note(remote, stamp, Reached::Whole);
                progress.file_done();
                return Ok(());
            }
        }
        Some(Reached::Kept(left)) if left == partial => {
            let left = tokio::fs::metadata(&partial).await;
            from = left.map_or(0, |left| left.len()).min(file.size);
        }
        // Der Kern davor ist mitten im Schreiben geendet: Was hinter `safe` steht, hat vielleicht nie die Platte
        // erreicht. Ist die Teil-Datei kürzer, stimmt etwas nicht – dann von vorn.
        Some(Reached::Writing { partial: left, safe }) if left == partial => {
            let left = tokio::fs::metadata(&partial).await;
            if left.is_ok_and(|left| left.len() >= safe) {
                from = safe.min(file.size);
            }
        }
        // Liegt die Teil-Datei von damals woanders, als dieser Versuch schreibt, wird sie nicht mehr gebraucht.
        Some(reached) => {
            if let Some(left) = reached.partial() {
                remove_partial(left);
            }
        }
        None => {}
    }

    // Von jetzt an kennt die Spur die Teil-Datei: Endet der Kern mittendrin, räumt der nächste sie weg oder macht
    // in ihr weiter.
    progress.note(
        remote,
        stamp,
        Reached::Writing {
            partial: partial.clone(),
            safe: from,
        },
    );
    let safe = |safe| {
        progress.note(
            remote,
            stamp,
            Reached::Writing {
                partial: partial.clone(),
                safe,
            },
        );
    };
    let result = match copy_down(sftp, remote, &partial, from, progress, &safe).await {
        Ok(()) => tokio::fs::rename(&partial, local).await.map_err(Stop::from),
        Err(stop) => Err(stop),
    };
    match &result {
        Ok(()) => {
            progress.note(remote, stamp, Reached::Whole);
            progress.file_done();
        }
        // Die Verbindung ist weg oder der Benutzer hat abgebrochen, aber was angekommen ist, stimmt: Ein neuer
        // Versuch macht in der Teil-Datei weiter. Sie bleibt, solange der Kern sich an sie erinnert ([`Trail`]) –
        // nimmt der Benutzer den Transfer aus der Liste, verschwindet sie (`transfer.retain`).
        Err(stop) if stop.lost() || progress.cancelled() || matches!(stop, Stop::Cancelled) => {
            progress.note(remote, stamp, Reached::Kept(partial))
        }
        // Bei jedem anderen Ende – die Platte voll, am Ziel liegt ein Ordner, der Server verweigert – bleibt nichts
        // liegen.
        Err(_) => {
            let _ = tokio::fs::remove_file(&partial).await;
            progress.forget(remote);
        }
    }

    result
}

/// Schreibt die Datei vom Server in die Teil-Datei – ab `from`; was davor liegt, hat ein Versuch davor geholt.
/// Unterwegs schreibt sie die Teil-Datei immer wieder fest auf die Platte und nennt `safe`, bis wohin sie stimmt.
async fn copy_down(
    sftp: &SftpSession,
    remote: &str,
    partial: &Path,
    from: u64,
    progress: &Progress<'_>,
    safe: &(dyn Fn(u64) + Sync),
) -> Result<(), Stop> {
    let mut source = sftp.open(remote).await?;
    let mut target = if from == 0 {
        tokio::fs::File::create(partial).await?
    } else {
        let mut target = tokio::fs::OpenOptions::new().write(true).open(partial).await?;
        // Was hinter `from` steht, gilt nicht (nach einem Absturz) – es wird ohnehin neu geschrieben.
        target.set_len(from).await?;
        target.seek(std::io::SeekFrom::Start(from)).await?;
        source.seek(std::io::SeekFrom::Start(from)).await.map_err(remote_io)?;
        progress.resume(from);
        target
    };
    let mut buffer = vec![0; CHUNK];
    let (mut written, mut unsynced, mut synced_at) = (from, 0, Instant::now());
    let copied: Result<(), Stop> = async {
        loop {
            let read = source.read(&mut buffer).await.map_err(remote_io)?;
            if read == 0 {
                return Ok(());
            }

            target.write_all(&buffer[..read]).await?;
            written += read as u64;
            unsynced += read as u64;
            if unsynced >= SYNC_BYTES || synced_at.elapsed() >= SYNC_INTERVAL {
                target.flush().await?;
                target.sync_data().await?;
                safe(written);
                (unsynced, synced_at) = (0, Instant::now());
            }

            progress.advance(read)?;
            progress.throttle(read).await;
        }
    }
    .await;

    // Auch nach einem Fehler: Was gezählt ist, steht dann in der Teil-Datei, bevor jemand nach ihrer Größe fragt.
    let flushed = target.flush().await;
    copied.and(flushed.map_err(Stop::from))
}

/// Fehler beim Lesen oder Schreiben einer Datei **auf dem Server**. `russh-sftp` reicht sie als `io::Error` durch –
/// ohne diese Unterscheidung sähen sie aus wie ein Fehler der lokalen Platte („Lesen oder Schreiben auf diesem
/// Rechner ist gescheitert“), auch wenn nur die Verbindung abgerissen ist.
fn remote_io(error: std::io::Error) -> Stop {
    let message = error.to_string();
    let lower = message.to_lowercase();
    let code = if error.kind() == std::io::ErrorKind::TimedOut {
        "timeout"
    } else if ["dropped", "closed", "connection", "session"]
        .iter()
        .any(|mark| lower.contains(mark))
    {
        "connection"
    } else if lower.contains("permission denied") {
        "permission_denied"
    } else {
        "failed"
    };
    Stop::Failed(Failure { code, message })
}

fn partial_path(target: &Path) -> PathBuf {
    let mut name = target.file_name().unwrap_or_default().to_owned();
    name.push(PARTIAL_SUFFIX);
    target.with_file_name(name)
}

fn unsafe_name(name: &str) -> Failure {
    Failure {
        code: "unsafe_name",
        message: format!("refusing the file name {name:?}"),
    }
}

/// Macht aus einem Dateinamen, den der **Server** geliefert hat, einen Namen für diesen Rechner – bevor er Teil
/// eines lokalen Pfads wird.
///
/// Ein böswilliger Server könnte beim Download eines Verzeichnisses Namen wie `..\..\Startup\x.bat` melden und so
/// außerhalb des Zielordners schreiben. Heraus kommt deshalb immer ein einzelner, gewöhnlicher Namensteil. Ein
/// `/`, `.` oder `..` kann kein Name sein und wird abgelehnt (`None`). Was auf einem Server ein alltäglicher Name
/// ist, unter Windows aber nicht geht, wird ersetzt statt abgelehnt – sonst ließe sich schon ein Maildir oder
/// `backup-12:00.tar` nicht holen: `\`, `:` (Laufwerk, Datenstrom), `* ? " < > |` und Steuerzeichen werden zu `_`,
/// ebenso Punkte und Leerzeichen am Ende (Windows schnitte sie ab); ein Gerätename wie `CON` bekommt `_` davor.
pub(crate) fn local_name(name: &str) -> Option<String> {
    const DEVICES: [&str; 22] = [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1",
        "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    if name.is_empty() || name == "." || name == ".." || name.contains('/') {
        return None;
    }

    let mut local: String = name
        .chars()
        .map(|c| {
            // Auch Zeichen, die die Schreibrichtung umkehren oder eine Zeile vortäuschen: Der Explorer zeigte
            // `rechnung<U+202E>fdp.exe` sonst als `rechnungexe.pdf`.
            if matches!(c, '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
                || c.is_control()
                || matches!(c, '\u{061C}' | '\u{200E}' | '\u{200F}' | '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}' | '\u{2028}' | '\u{2029}')
            {
                '_'
            } else {
                c
            }
        })
        .collect();

    let kept = local.trim_end_matches(['.', ' ']).len();
    let cut = local.len() - kept;
    local.truncate(kept);
    local.extend(std::iter::repeat_n('_', cut));

    let stem = local.split('.').next().unwrap_or_default().trim_end();
    if DEVICES.iter().any(|device| device.eq_ignore_ascii_case(stem)) {
        local.insert(0, '_');
    }

    Some(local)
}

/// Wohin ein Download von `remote` in `local_directory` schreibt: die Datei oder der Ordner, der dort entsteht.
/// `None`, wenn sich aus dem Namen keiner für diesen Rechner machen lässt ([`local_name`]).
pub(crate) fn download_target(local_directory: &Path, remote: &str) -> Option<PathBuf> {
    local_name(base_name(remote)).map(|name| local_directory.join(name))
}

/// Die lokalen Namen eines Verzeichnisses, das gerade heruntergeladen wird. Jeder wird nur einmal vergeben – ohne
/// Rücksicht auf Groß- und Kleinschreibung, wie es das Dateisystem von Windows hält: Liegen auf dem Server
/// `Makefile` und `makefile` nebeneinander (oder `a:b` und `a_b`), überschriebe sonst eine Datei die andere.
#[derive(Default)]
struct Taken(HashSet<String>);

impl Taken {
    /// Liefert den Namen, wenn er noch frei ist, sonst mit einer Nummer vor der Endung: `makefile (2)`.
    fn claim(&mut self, name: String) -> String {
        if self.0.insert(name.to_lowercase()) {
            return name;
        }

        let (stem, extension) = match name.rfind('.') {
            Some(dot) if dot > 0 => name.split_at(dot),
            _ => (name.as_str(), ""),
        };
        let mut number = 2;
        loop {
            let candidate = format!("{stem} ({number}){extension}");
            if self.0.insert(candidate.to_lowercase()) {
                return candidate;
            }

            number += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use tokio::time::timeout;

    use super::*;

    const SHORT: Duration = Duration::from_millis(50);
    const LONG: Duration = Duration::from_secs(5);

    #[test]
    fn the_limit_books_every_block_on_one_clock_and_a_pause_earns_no_burst() {
        let limiter = Limiter::default();
        let now = Instant::now();

        // Ohne Grenze wartet niemand.
        assert_eq!(limiter.delay(1_000_000, now), Duration::ZERO);

        // 1000 Bytes je Sekunde: Der erste Block von 500 kostet eine halbe Sekunde, ein zweiter – auch von einem
        // anderen Transfer – steht dahinter an.
        limiter.set(1000);
        assert_eq!(limiter.delay(500, now), Duration::from_millis(500));
        assert_eq!(limiter.delay(250, now), Duration::from_millis(750));

        // Nach einer langen Pause beginnt die Rechnung neu, statt Guthaben auszuzahlen.
        let later = now + Duration::from_secs(60);
        assert_eq!(limiter.delay(1000, later), Duration::from_secs(1));

        // Die Grenze lässt sich jederzeit ändern oder aufheben.
        limiter.set(0);
        assert_eq!(limiter.delay(1_000_000, later), Duration::ZERO);
    }

    #[tokio::test]
    async fn slots_go_in_the_order_of_enqueueing_not_of_asking() {
        let slots = Slots::default();
        let tickets: Vec<u64> = (0..4).map(|_| slots.enqueue()).collect();

        // Der zuletzt Eingereihte fragt zuerst – und muss trotzdem warten, obwohl beide Plätze frei sind.
        let last = slots.acquire(tickets[3]);
        tokio::pin!(last);
        assert!(timeout(SHORT, &mut last).await.is_err());

        let first = slots.acquire(tickets[0]).await;
        let second = slots.acquire(tickets[1]).await;

        // Beide Plätze sind belegt: Der dritte wartet, bis einer frei wird.
        let third = slots.acquire(tickets[2]);
        tokio::pin!(third);
        assert!(timeout(SHORT, &mut third).await.is_err());
        drop(first);
        let third = timeout(LONG, &mut third).await.expect("der dritte rückt nach");

        assert!(timeout(SHORT, &mut last).await.is_err());
        drop(second);
        let fourth = timeout(LONG, &mut last).await.expect("der vierte rückt nach");
        drop((third, fourth));
    }

    #[tokio::test]
    async fn a_transfer_that_leaves_the_queue_does_not_block_those_behind_it() {
        let slots = Slots::default();
        let tickets: Vec<u64> = (0..4).map(|_| slots.enqueue()).collect();
        let first = slots.acquire(tickets[0]).await;
        let _second = slots.acquire(tickets[1]).await;

        let fourth = slots.acquire(tickets[3]);
        tokio::pin!(fourth);
        drop(first);
        assert!(
            timeout(SHORT, &mut fourth).await.is_err(),
            "der dritte steht noch davor"
        );

        // Der dritte wird abgebrochen, bevor er an der Reihe war.
        slots.leave(tickets[2]);
        let _fourth = timeout(LONG, &mut fourth).await.expect("der vierte rückt nach");
    }

    #[tokio::test]
    async fn a_waiting_transfer_can_be_moved_ahead_of_another_or_to_the_end() {
        let slots = Slots::default();
        let tickets: Vec<u64> = (0..5).map(|_| slots.enqueue()).collect();
        let first = slots.acquire(tickets[0]).await;
        let second = slots.acquire(tickets[1]).await;

        // Es warten 2, 3, 4. Der letzte kommt nach vorn, der erste ans Ende: 4, 3, 2.
        assert!(slots.move_before(tickets[4], Some(tickets[2])));
        assert!(slots.move_before(tickets[2], None));

        // Wer läuft, wartet nicht mehr – weder als der, der umzieht, noch als der, vor dem einer stehen soll.
        assert!(!slots.move_before(tickets[0], Some(tickets[3])));
        assert!(!slots.move_before(tickets[3], Some(tickets[0])));
        assert!(!slots.move_before(tickets[3], Some(tickets[3])));

        let third = slots.acquire(tickets[2]);
        tokio::pin!(third);
        drop(first);
        let _moved = timeout(LONG, slots.acquire(tickets[4]))
            .await
            .expect("der vorgezogene läuft zuerst");
        assert!(timeout(SHORT, &mut third).await.is_err(), "der zurückgestellte wartet");
        drop(second);
        let _next = timeout(LONG, slots.acquire(tickets[3]))
            .await
            .expect("dann der mittlere");
        assert!(timeout(SHORT, &mut third).await.is_err());
    }

    #[test]
    fn a_local_name_cannot_pretend_to_be_another_kind_of_file() {
        // U+202E dreht die Schreibrichtung: Der Explorer zeigte „rechnungexe.pdf“ für eine .exe.
        assert_eq!(
            local_name("rechnung\u{202E}fdp.exe").as_deref(),
            Some("rechnung_fdp.exe")
        );
        for hidden in [
            '\u{061C}', '\u{200E}', '\u{200F}', '\u{202A}', '\u{202D}', '\u{2066}', '\u{2069}', '\u{2028}', '\u{2029}',
        ] {
            assert_eq!(
                local_name(&format!("a{hidden}b")).as_deref(),
                Some("a_b"),
                "{:04x}",
                hidden as u32
            );
        }

        // Gewöhnliche Schrift von rechts nach links bleibt, wie sie ist.
        assert_eq!(local_name("שלום.txt").as_deref(), Some("שלום.txt"));
    }

    #[test]
    fn ordinary_names_stay_as_they_are() {
        for name in [
            "notes.txt",
            ".bashrc",
            "my file (1).tar.gz",
            "ünïcödé",
            "console.log",
            "a..b",
            "$(reboot)",
            "-rf",
            "semi;colon&and",
        ] {
            assert_eq!(local_name(name).as_deref(), Some(name), "{name}");
        }
    }

    #[test]
    fn names_that_are_no_names_are_refused() {
        for name in ["", ".", "..", "../evil", "a/b", "/etc/passwd"] {
            assert_eq!(local_name(name), None, "{name:?}");
        }
    }

    #[test]
    fn names_windows_cannot_hold_are_replaced_by_a_single_harmless_one() {
        for (name, expected) in [
            ("backup-12:00.tar", "backup-12_00.tar"),
            ("1700000000.M1P2.host:2,S", "1700000000.M1P2.host_2,S"),
            ("..\\evil", ".._evil"),
            ("..\\..\\Startup\\x.bat", ".._.._Startup_x.bat"),
            ("C:evil", "C_evil"),
            ("file.txt:stream", "file.txt_stream"),
            ("nul", "_nul"),
            ("NUL.txt", "_NUL.txt"),
            ("com1", "_com1"),
            ("CON", "_CON"),
            ("con .txt", "_con .txt"),
            ("trailing.", "trailing_"),
            ("trailing ", "trailing_"),
            ("dots...", "dots___"),
            ("...", "___"),
            ("new\nline", "new_line"),
            ("tab\tname", "tab_name"),
            ("what?", "what_"),
            ("pipe|", "pipe_"),
            ("quote\"s", "quote_s"),
            ("<a>*", "_a__"),
        ] {
            let local = local_name(name).unwrap_or_else(|| panic!("{name:?} wird abgelehnt"));
            assert_eq!(local, expected, "{name:?}");

            // Was herauskommt, ist selbst ein Name, an dem nichts mehr zu ändern ist – also genau ein Teil.
            assert_eq!(local_name(&local).as_deref(), Some(expected), "{name:?}");
            assert_eq!(Path::new(&local).components().count(), 1, "{name:?}");
        }
    }

    #[test]
    fn the_target_of_a_download_is_the_local_name_inside_the_chosen_folder() {
        let downloads = Path::new("downloads");
        assert_eq!(
            download_target(downloads, "/var/log/app.log"),
            Some(downloads.join("app.log"))
        );
        assert_eq!(
            download_target(downloads, "/var/backup-12:00/"),
            Some(downloads.join("backup-12_00"))
        );
        assert_eq!(download_target(downloads, "/"), None);
        assert_eq!(download_target(downloads, "/var/.."), None);
    }

    #[test]
    fn names_that_fall_together_get_a_number() {
        let mut taken = Taken::default();
        assert_eq!(taken.claim("Makefile".to_owned()), "Makefile");
        assert_eq!(taken.claim("makefile".to_owned()), "makefile (2)");
        assert_eq!(taken.claim("MAKEFILE".to_owned()), "MAKEFILE (3)");
        assert_eq!(taken.claim("a_b.txt".to_owned()), "a_b.txt");
        assert_eq!(taken.claim("a_b.txt".to_owned()), "a_b (2).txt");
        assert_eq!(taken.claim(".profile".to_owned()), ".profile");
        assert_eq!(taken.claim(".profile".to_owned()), ".profile (2)");
        // Auch der Name mit Nummer kann schon vergeben sein.
        assert_eq!(taken.claim("a_b (2).txt".to_owned()), "a_b (2) (2).txt");
        assert_eq!(taken.claim("a_b.txt".to_owned()), "a_b (3).txt");
    }

    #[test]
    fn errors_of_the_remote_file_are_not_blamed_on_the_local_disk() {
        let code = |error: std::io::Error| match remote_io(error) {
            Stop::Failed(failure) => failure.code,
            Stop::Cancelled => "cancelled",
        };

        // So meldet sich eine abgerissene Verbindung, während eine Datei gelesen wird.
        assert_eq!(code(std::io::Error::other("sender dropped")), "connection");
        assert_eq!(code(std::io::Error::other("Channel closed")), "connection");
        assert_eq!(code(std::io::Error::from(std::io::ErrorKind::TimedOut)), "timeout");
        assert_eq!(code(std::io::Error::other("Permission denied")), "permission_denied");
        assert_eq!(code(std::io::Error::other("Failure")), "failed");
    }

    fn key(name: &str) -> Key {
        (
            Direction::Download,
            PathBuf::from("downloads"),
            format!("/srv/{name}"),
            None,
        )
    }

    /// Eine Spur mit einer liegen gebliebenen Teil-Datei, die es wirklich gibt.
    fn trail_with_partial(directory: &Path, name: &str) -> (Trail, PathBuf) {
        let partial = directory.join(format!("{name}{PARTIAL_SUFFIX}"));
        std::fs::write(&partial, "halb").unwrap();
        let source = Stamp {
            size: 8,
            modified: Some(Duration::from_secs(1)),
        };
        let mut trail = Trail::default();
        trail.0.insert(
            format!("/srv/{name}"),
            Mark {
                source,
                reached: Reached::Kept(partial.clone()),
            },
        );
        trail.0.insert(
            "/srv/fertig".to_owned(),
            Mark {
                source,
                reached: Reached::Whole,
            },
        );
        (trail, partial)
    }

    fn scratch(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!("nexus-core-trails-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(&directory).unwrap();
        directory
    }

    #[test]
    fn a_trail_is_handed_out_once_and_only_for_its_own_transfer() {
        let directory = scratch("once");
        let trails = Trails::default();
        let (trail, partial) = trail_with_partial(&directory, "a.bin");
        trails.keep(key("a.bin"), trail);

        assert!(trails.take(&key("b.bin")).is_none());
        let taken = trails.take(&key("a.bin")).expect("die Spur des Transfers");
        assert_eq!(taken.0.len(), 2);
        assert!(trails.take(&key("a.bin")).is_none());

        // Solange jemand die Spur hält, bleibt die Teil-Datei; fällt sie ungenutzt, geht die Teil-Datei mit.
        assert!(partial.exists());
        drop(taken);
        assert!(!partial.exists());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn partial_files_go_away_with_the_trail_that_kept_them() {
        let directory = scratch("gone");
        let other = directory.join("fremd.txt");
        std::fs::write(&other, "bleibt").unwrap();
        let trails = Trails::default();

        // Mehr, als sich der Kern merkt: Die ältesten fallen heraus, ihre Teil-Dateien verschwinden.
        let partials: Vec<PathBuf> = (0..TRAILS + 2)
            .map(|number| {
                let name = format!("f-{number}.bin");
                let (trail, partial) = trail_with_partial(&directory, &name);
                trails.keep(key(&name), trail);
                partial
            })
            .collect();
        assert!(!partials[0].exists() && !partials[1].exists());
        assert!(partials[2..].iter().all(|partial| partial.exists()));

        // Ein neuer Stand desselben Transfers ersetzt den alten, statt neben ihm zu stehen.
        let (again, _) = trail_with_partial(&directory, "f-5.bin");
        trails.keep(key("f-5.bin"), again);
        assert!(partials[2..].iter().all(|partial| partial.exists()));

        // Zeigt die App einen Transfer nicht mehr, bleibt nichts liegen, was nur er hätte fortsetzen können.
        trails.retain(&[key("f-5.bin"), key("f-7.bin"), key("nie-gesehen.bin")]);
        for (number, partial) in partials.iter().enumerate() {
            assert_eq!(partial.exists(), number == 5 || number == 7, "{number}");
        }

        trails.retain(&[]);
        assert!(partials.iter().all(|partial| !partial.exists()));
        assert!(other.exists());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn trails_outlive_the_core_in_its_data_folder() {
        let directory = scratch("saved");
        let file = directory.join("trails.json");
        let (trail, partial) = trail_with_partial(&directory, "a.bin");
        let sent = Stamp {
            size: 5_000_000_000,
            modified: Some(Duration::new(1_700_000_000, 123_456_700)),
        };
        let mut upload = Trail::default();
        upload.0.insert(
            "C:\\Daten\\groß.iso".to_owned(),
            Mark {
                source: sent,
                reached: Reached::Sent(4_294_967_297),
            },
        );
        let upload_key = (
            Direction::Upload,
            PathBuf::from("C:\\Daten"),
            "/srv".to_owned(),
            Some("anders".to_owned()),
        );

        {
            let trails = Trails::load(file.clone());
            trails.keep(key("a.bin"), trail);
            trails.keep(upload_key.clone(), upload);
            trails.save();
        }

        // Der Kern ist zu Ende – die Teil-Datei bleibt liegen, denn der nächste kann sie fortsetzen.
        assert!(partial.exists());
        let trails = Trails::load(file.clone());
        let download = trails.take(&key("a.bin")).expect("die Spur des Downloads");
        assert_eq!(download.0["/srv/a.bin"].reached, Reached::Kept(partial.clone()));
        assert_eq!(download.0["/srv/fertig"].reached, Reached::Whole);
        assert_eq!(download.0["/srv/a.bin"].source.modified, Some(Duration::from_secs(1)));
        let upload = trails.take(&upload_key).expect("die Spur des Uploads");
        assert_eq!(upload.0["C:\\Daten\\groß.iso"].reached, Reached::Sent(4_294_967_297));
        assert!(upload.0["C:\\Daten\\groß.iso"].source.same(sent), "auf die Nanosekunde");

        // Herausgenommen und nicht zurückgelegt: Nach dem Ende des Transfers steht das auch in der Datei.
        drop((download, upload));
        trails.save();
        assert!(Trails::load(file).take(&key("a.bin")).is_none());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_file_of_trails_that_cannot_be_read_means_there_are_none() {
        let directory = scratch("broken");
        for (name, content) in [
            ("kaputt.json", "{ das ist kein JSON"),
            ("neuer.json", r#"{"version":99,"trails":[]}"#),
            ("fehlt.json", ""),
        ] {
            let file = directory.join(name);
            if !content.is_empty() {
                std::fs::write(&file, content).unwrap();
            }

            let trails = Trails::load(file.clone());
            assert!(trails.take(&key("a.bin")).is_none(), "{name}");

            // Und der Kern arbeitet weiter: Die nächste Spur überschreibt die Datei.
            let (trail, _) = trail_with_partial(&directory, "a.bin");
            trails.keep(key("a.bin"), trail);
            trails.save();
            assert!(Trails::load(file).take(&key("a.bin")).is_some(), "{name}");
        }

        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn only_partial_files_are_ever_removed_for_a_trail() {
        let directory = scratch("guard");
        let precious = directory.join("wichtig.txt");
        std::fs::write(&precious, "bleibt").unwrap();
        let file = directory.join("trails.json");

        // Jemand hat die Datei von Hand geändert: Als liegen gebliebene Teil-Datei steht dort etwas ganz anderes.
        let saved = serde_json::json!({ "version": 1, "trails": [{
            "direction": "download", "local": "downloads", "remote": "/srv/a.bin", "name": null,
            "files": { "/srv/a.bin": { "source": { "size": 1, "modified": { "secs": 1, "nanos": 0 } }, "reached": { "kept": precious } } },
        }] });
        std::fs::write(&file, saved.to_string()).unwrap();

        let trails = Trails::load(file);
        assert!(trails.take(&key("a.bin")).is_some());
        trails.retain(&[]);
        assert!(precious.exists());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_running_transfer_is_written_down_and_outlives_a_core_that_ends_in_the_middle() {
        let directory = scratch("running");
        let file = directory.join("trails.json");
        let partial = directory.join("a.bin.nexus-part");
        std::fs::write(&partial, "angefangen").unwrap();
        let stamp = Stamp {
            size: 100,
            modified: Some(Duration::from_secs(1)),
        };
        let writing = Reached::Writing {
            partial: partial.clone(),
            safe: 4,
        };

        {
            let trails = Trails::load(file.clone());
            let running = Arc::new(Mutex::new(Trail::default()));
            trails.start(7, key("a.bin"), running.clone());
            lock(&running).0.insert(
                "/srv/a.bin".to_owned(),
                Mark {
                    source: stamp,
                    reached: Reached::Writing {
                        partial: partial.clone(),
                        safe: 4,
                    },
                },
            );
            trails.changed();

            // Der Kern endet, bevor der Transfer zu Ende kam – etwa, weil er nicht mehr rechtzeitig fertig wurde.
            drop(trails);
            drop(running);
        }

        assert!(partial.exists(), "die Teil-Datei gehört dem nächsten Kern");
        let trails = Trails::load(file);
        let found = trails.take(&key("a.bin")).expect("die Spur des laufenden Transfers");
        assert_eq!(found.0["/srv/a.bin"].reached, writing);

        // Wer sie nicht fortsetzt, lässt sie fallen – und mit ihr die Teil-Datei.
        drop(found);
        assert!(!partial.exists());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_transfer_that_ends_takes_its_running_trail_out_of_the_file() {
        let directory = scratch("finished");
        let file = directory.join("trails.json");
        let trails = Trails::load(file.clone());
        let running = Arc::new(Mutex::new(Trail::default()));
        trails.start(7, key("a.bin"), running.clone());
        lock(&running).0.insert(
            "/srv/a.bin".to_owned(),
            Mark {
                source: Stamp {
                    size: 1,
                    modified: None,
                },
                reached: Reached::Whole,
            },
        );
        trails.changed();
        assert!(std::fs::read_to_string(&file).unwrap().contains("/srv/a.bin"));

        trails.finish(7);
        trails.save();
        assert!(Trails::load(file).take(&key("a.bin")).is_none());
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_file_counts_as_the_same_only_with_the_same_size_and_time() {
        let stamp = |size, seconds: Option<u64>| Stamp {
            size,
            modified: seconds.map(Duration::from_secs),
        };
        assert!(stamp(5, Some(100)).same(stamp(5, Some(100))));
        assert!(!stamp(5, Some(100)).same(stamp(6, Some(100))));
        assert!(!stamp(5, Some(100)).same(stamp(5, Some(101))));
        // Nennt der Server keine Zeit, lässt sich nicht sagen, ob die Datei noch dieselbe ist.
        assert!(!stamp(5, None).same(stamp(5, None)));
    }

    #[test]
    fn the_partial_file_sits_next_to_its_target() {
        assert_eq!(
            partial_path(Path::new("downloads").join("backup.tar.gz").as_path()),
            Path::new("downloads").join("backup.tar.gz.nexus-part")
        );
    }
}
