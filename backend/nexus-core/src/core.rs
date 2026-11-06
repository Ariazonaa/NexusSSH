//! Der Kern als Rust-Objekt: nimmt Anfragen an, hält die Sitzungen und meldet Ereignisse.
//!
//! [`crate::ffi`] ist nur die C-Hülle um [`Core`]; Tests sprechen den Kern direkt hier an.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::time::{Duration, Instant};

use russh_sftp::client::SftpSession;
use tokio::runtime::Runtime;
use tokio::sync::{mpsc, oneshot, watch};
use tokio::task::AbortHandle;

use crate::hostkeys::HostKeys;
use crate::profiles::{ChainError, ProfileStore};
use crate::protocol::{Answer, CloseReason, Entry, Event, ProfileInfo, Request, Response, SessionState, TreeEntry};
use crate::secrets::{SecretStore, passphrase_key};
use crate::session::{self, Command, Link, Route};
use crate::transfers::{self, Halt, Job};
use crate::{exec, keys, sftp, sftp::Failure, sshconfig};

/// Was eine `sftp.*`-Anfrage außer „gelungen“ zurückgibt: die Einträge eines Verzeichnisses oder einen Baum.
type SftpAnswer = (Option<Vec<Entry>>, Option<Vec<TreeEntry>>);

/// Empfänger für alles, was der Kern von sich aus meldet. Wird von beliebigen Threads aufgerufen.
pub trait EventSink: Send + Sync + 'static {
    /// Ein Ereignis ([`Event`]) als JSON.
    fn event(&self, json: &str);

    /// Ausgabe des Terminals einer Sitzung, rohe Bytes.
    fn output(&self, session: u64, data: &[u8]);
}

/// Wo der Kern seine Dateien findet.
#[derive(Clone, Debug)]
pub struct CoreConfig {
    /// Datenordner der App: hier liegen `profiles.json` und die eigene `known_hosts`.
    pub data_dir: PathBuf,
    /// `~/.ssh` des Benutzers: dessen `known_hosts` wird gelesen, dessen Standardschlüssel werden angeboten.
    /// `None` schaltet beides ab – und den Import aus `~/.ssh/config`.
    pub ssh_dir: Option<PathBuf>,
    /// Wo der SSH-Agent zu finden ist: unter Windows der Name einer Named Pipe, sonst der Pfad eines Sockets.
    /// `None` nimmt den üblichen Ort (OpenSSH-Agent von Windows, dann Pageant; sonst `SSH_AUTH_SOCK`).
    pub agent: Option<String>,
    /// Wo der X-Server dieses Rechners lauscht (`host:port`), für X11-Forwarding. `None` nimmt Display 0
    /// (`127.0.0.1:6000`).
    pub x11: Option<String>,
}

impl CoreConfig {
    /// Datenordner wie angegeben, `~/.ssh` aus dem Benutzerverzeichnis.
    pub fn for_user(data_dir: PathBuf) -> Self {
        Self {
            data_dir,
            ssh_dir: std::env::home_dir().map(|home| home.join(".ssh")),
            agent: None,
            x11: None,
        }
    }
}

/// Der laufende Kern. Fällt er, enden alle Sitzungen.
pub struct Core {
    // Option nur, damit Drop das Runtime herausnehmen kann.
    runtime: Option<Runtime>,
    shared: Arc<Shared>,
}

struct SessionEntry {
    commands: mpsc::UnboundedSender<Command>,
    task: AbortHandle,
}

struct PendingPrompt {
    session: u64,
    reply: oneshot::Sender<Answer>,
}

struct TransferEntry {
    session: u64,
    /// Wartenummer in der Warteschlange ([`transfers::Slots`]).
    ticket: u64,
    halt: watch::Sender<Option<Halt>>,
}

impl TransferEntry {
    /// Sagt dem Transfer, warum er endet. Es gilt der erste Grund: Hat die App ihn beim Beenden angehalten, wird
    /// daraus kein Abbruch mehr, wenn danach ihre Tabs schließen.
    fn halt(&self, halt: Halt) {
        self.halt.send_if_modified(|current| {
            let first = current.is_none();
            if first {
                *current = Some(halt);
            }
            first
        });
    }
}

/// Was sich Kern und Sitzungen teilen.
pub(crate) struct Shared {
    pub(crate) sink: Arc<dyn EventSink>,
    pub(crate) secrets: Arc<dyn SecretStore>,
    pub(crate) host_keys: HostKeys,
    /// TLS-Zertifikate, denen der Benutzer zugestimmt hat (FTPS).
    pub(crate) tls_trust: crate::ftp::TlsTrust,
    pub(crate) ssh_dir: Option<PathBuf>,
    pub(crate) agent: Option<String>,
    /// Wo der X-Server dieses Rechners lauscht; `None` = Display 0. Die App kann das Display ändern (`x11.display`).
    pub(crate) x11: Mutex<Option<String>>,
    /// Lebenszeichen für Profile ohne eigene Angabe: Abstand in Sekunden (0 = keine) und wie viele unbeantwortet
    /// bleiben dürfen (`keepalive.set`).
    pub(crate) keepalive: Mutex<(u32, u32)>,
    /// Schlüssel, die in diesem Lauf schon entsperrt wurden (`keys.unlocked`).
    pub(crate) unlocked: crate::unlocked::Unlocked,
    pub(crate) transfer_slots: transfers::Slots,
    /// Die Obergrenze für die Bandbreite aller Transfers.
    pub(crate) limiter: transfers::Limiter,
    /// Was abgebrochene Transfers geschafft haben – für den nächsten Versuch.
    pub(crate) trails: transfers::Trails,
    profiles: Mutex<ProfileStore>,
    sessions: Mutex<HashMap<u64, SessionEntry>>,
    /// SFTP-Kanäle der verbundenen Sitzungen, die einen haben.
    sftp: Mutex<HashMap<u64, Arc<SftpSession>>>,
    /// Die Verbindung jeder verbundenen Sitzung; mehrere Sitzungen können dieselbe halten.
    links: Mutex<HashMap<u64, Arc<Link>>>,
    /// Benutzer- und Gruppennamen des Servers je Sitzung, beim ersten Auflisten gelesen.
    names: Mutex<HashMap<u64, Arc<sftp::Names>>>,
    /// Je Sitzung die Nummer des letzten Auftrags, den SFTP-Kanal zu wechseln („als root“, „neu öffnen“). Zwei
    /// davon können zugleich unterwegs sein; es gilt der zuletzt verlangte, nicht der zuletzt fertige.
    switches: Mutex<HashMap<u64, u64>>,
    transfers: Mutex<HashMap<u64, TransferEntry>>,
    /// Laufendes Mitlesen von Dateien: Wer hier sendet (oder den Sender fallen lässt), beendet es.
    follows: Mutex<HashMap<u64, oneshot::Sender<()>>>,
    prompts: Mutex<HashMap<u64, PendingPrompt>>,
    next_prompt: AtomicU64,
    /// Sitzungen, deren Aufbau ausführlich gemeldet wird (`session.open` mit `trace`).
    traced: Mutex<std::collections::HashSet<u64>>,
    /// Welche Tunnel gerade laufen, als (Profil, Tunnel): Jeder steht nur einmal, auch bei zwei Verbindungen zum
    /// selben Profil – der zweite könnte an demselben Port nie lauschen.
    tunnels: Mutex<std::collections::HashSet<(String, String)>>,
    /// Sitzungen, deren Verbindung gerade still neu entsteht (unter einem Mosh-Terminal): Für die App bleiben sie
    /// verbunden, also keine Zustandsmeldungen ([`Shared::quiet`]).
    quiet: Mutex<std::collections::HashSet<u64>>,
    /// Die Threads der Mosh-Treiber – der Kern wartet beim Ende kurz auf sie.
    pub(crate) mosh_threads: crate::mosh::Threads,
}

/// Solange der Wert lebt, meldet eine Sitzung keine Zustände ([`Shared::quiet`]).
pub(crate) struct Quiet<'a> {
    shared: &'a Shared,
    session: u64,
}

impl Drop for Quiet<'_> {
    fn drop(&mut self) {
        lock(&self.shared.quiet).remove(&self.session);
    }
}

// Ein vergifteter Mutex (Panic in einem anderen Thread) soll den Kern nicht für immer blockieren: Die Daten
// darunter sind einfache Tabellen, die auch nach einem Abbruch brauchbar sind.
fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl Shared {
    /// Belegt einen Tunnel für eine Verbindung; `false`, wenn er schon läuft.
    pub(crate) fn claim_tunnel(&self, profile: &str, tunnel: &str) -> bool {
        lock(&self.tunnels).insert((profile.to_owned(), tunnel.to_owned()))
    }

    pub(crate) fn release_tunnel(&self, profile: &str, tunnel: &str) {
        lock(&self.tunnels).remove(&(profile.to_owned(), tunnel.to_owned()));
    }

    /// Die Verbindung einer Sitzung, falls sie eine hat.
    pub(crate) fn link_of(&self, session: u64) -> Option<Arc<Link>> {
        lock(&self.links).get(&session).cloned()
    }

    /// Die Verbindung zu diesem Profil, über die der Tunnel gerade läuft.
    pub(crate) fn tunnel_owner(&self, profile: &str, tunnel: &str) -> Option<Arc<Link>> {
        lock(&self.links)
            .values()
            .find(|link| link.profile_id == profile && link.tunnels.runs(tunnel))
            .cloned()
    }

    pub(crate) fn emit(&self, event: Event) {
        if let Ok(json) = serde_json::to_string(&event) {
            self.sink.event(&json);
        }
    }

    /// Lebenszeichen für die Verbindung zu diesem Server: was sein Profil nennt, sonst das Allgemeine. `None` als
    /// Abstand heißt: keine schicken.
    pub(crate) fn keepalive(&self, profile: &crate::profiles::Profile) -> (Option<Duration>, usize) {
        let (interval, max) = *lock(&self.keepalive);
        let interval = profile.keepalive_interval.unwrap_or(interval);
        let max = profile.keepalive_max.unwrap_or(max);
        (
            (interval > 0).then(|| Duration::from_secs(interval.into())),
            max as usize,
        )
    }

    /// Meldet einen Zustand des Aufbaus. `hop` nennt den Server, um den es geht, wenn es mehrere sind.
    /// Meldet eine Zeile über den Aufbau der Verbindung – nur für Sitzungen, die das verlangt haben. Der Text
    /// entsteht erst dann; hinein gehört nie ein Geheimnis.
    pub(crate) fn trace(&self, session: u64, text: impl FnOnce() -> String) {
        if lock(&self.traced).contains(&session) {
            self.emit(Event::SessionTrace { session, text: text() });
        }
    }

    pub(crate) fn state(&self, session: u64, state: SessionState, hop: Option<String>) {
        if lock(&self.quiet).contains(&session) {
            return;
        }

        self.emit(Event::SessionState {
            session,
            state,
            reason: None,
            message: None,
            exit_status: None,
            trouble: None,
            home: None,
            hop,
        });
    }

    /// Hält die Zustandsmeldungen einer Sitzung zurück, solange der Rückgabewert lebt – für eine Verbindung, die
    /// unter einem laufenden Mosh-Terminal neu entsteht. Rückfragen kommen weiter.
    pub(crate) fn quiet(&self, session: u64) -> Quiet<'_> {
        lock(&self.quiet).insert(session);
        Quiet { shared: self, session }
    }

    /// Stellt dem Benutzer eine Rückfrage und wartet auf die Antwort. `None`, wenn sie nie kommt, weil die
    /// Sitzung vorher endet.
    pub(crate) async fn ask(&self, session: u64, build: impl FnOnce(u64) -> Event) -> Option<Answer> {
        let prompt = self.next_prompt.fetch_add(1, Ordering::Relaxed);
        let (reply, answer) = oneshot::channel();
        lock(&self.prompts).insert(prompt, PendingPrompt { session, reply });
        self.emit(build(prompt));
        answer.await.ok()
    }

    /// Die stehende Verbindung einer anderen Sitzung zum Mitbenutzen – nur, wenn sie zum selben Profil gehört
    /// und auf dem Weg entstand, den das Profil heute beschreibt.
    pub(crate) fn link(&self, session: u64, profile_id: &str, route: &Route) -> Option<Arc<Link>> {
        lock(&self.links)
            .get(&session)
            .filter(|link| link.profile_id == profile_id && &link.route == route)
            .cloned()
    }

    /// Beginnt einen Wechsel des SFTP-Kanals einer Sitzung und nennt seine Nummer.
    fn begin_switch(&self, session: u64) -> u64 {
        let mut switches = lock(&self.switches);
        let turn = switches.entry(session).or_insert(0);
        *turn += 1;
        *turn
    }

    /// Ob dieser Wechsel noch der zuletzt verlangte ist – nur dann darf sein Kanal eingetragen werden.
    fn is_latest_switch(&self, session: u64, turn: u64) -> bool {
        lock(&self.switches).get(&session) == Some(&turn)
    }

    /// Meldet die stehende Verbindung, merkt sie sich samt ihrem SFTP-Kanal und vermerkt sie im Profil.
    pub(crate) fn connected(
        &self,
        session: u64,
        profile_id: &str,
        link: Arc<Link>,
        files: Option<(Arc<SftpSession>, String)>,
    ) {
        // Nur eintragen, solange es die Sitzung noch gibt – sonst bliebe die Verbindung für immer liegen.
        let alive = lock(&self.sessions).contains_key(&session);
        if alive {
            lock(&self.links).insert(session, link);
        }

        let home = files.map(|(sftp, home)| {
            if alive {
                lock(&self.sftp).insert(session, sftp);
            }

            home
        });

        self.emit(Event::SessionState {
            session,
            state: SessionState::Connected,
            reason: None,
            message: None,
            exit_status: None,
            trouble: None,
            home,
            hop: None,
        });
        if lock(&self.profiles).touch(profile_id).is_ok() {
            self.emit(Event::ProfilesChanged);
        }
    }

    /// Meldet eine stehende Verbindung, die nur Dateien hat (FTP): kein Terminal, keine SSH-Verbindung dahinter –
    /// also nichts zu teilen und keine Befehle.
    pub(crate) fn connected_files_only(&self, session: u64, profile_id: &str, sftp: Arc<SftpSession>, home: String) {
        if lock(&self.sessions).contains_key(&session) {
            lock(&self.sftp).insert(session, sftp);

            // Namen zu Benutzernummern gibt es hier nicht – nach `/etc/passwd` wird gar nicht erst gefragt.
            lock(&self.names).insert(session, Arc::new(sftp::Names::default()));
        }

        self.emit(Event::SessionState {
            session,
            state: SessionState::Connected,
            reason: None,
            message: None,
            exit_status: None,
            trouble: None,
            home: Some(home),
            hop: None,
        });
        self.emit(Event::SessionFilesOnly {
            session,
            exit_status: None,
        });
        if lock(&self.profiles).touch(profile_id).is_ok() {
            self.emit(Event::ProfilesChanged);
        }
    }

    /// Meldet eine stehende Verbindung, die keine SSH-Verbindung ist (Telnet): ohne Dateien, ohne etwas, das
    /// sich teilen ließe.
    pub(crate) fn connected_without_link(&self, session: u64, profile_id: &str) {
        self.state(session, SessionState::Connected, None);
        if lock(&self.profiles).touch(profile_id).is_ok() {
            self.emit(Event::ProfilesChanged);
        }
    }

    /// Trägt einen beendeten Transfer aus.
    /// Die Namen zu den Benutzer- und Gruppennummern des Servers – einmal je Sitzung geholt.
    async fn names(&self, session: u64, sftp: &SftpSession) -> Arc<sftp::Names> {
        let known = lock(&self.names).get(&session).cloned();
        match known {
            Some(names) => names,
            None => {
                let names = Arc::new(sftp::Names::load(sftp).await);
                if lock(&self.sessions).contains_key(&session) {
                    lock(&self.names).insert(session, Arc::clone(&names));
                }

                names
            }
        }
    }

    pub(crate) fn forget_transfer(&self, transfer: u64) {
        lock(&self.transfers).remove(&transfer);
    }

    fn sftp_result(&self, request: u64, result: Result<Option<Vec<Entry>>, Failure>) {
        self.sftp_answer(request, result.map(|entries| (entries, None)));
    }

    fn sftp_tree(&self, request: u64, result: Result<Vec<TreeEntry>, Failure>) {
        self.sftp_answer(request, result.map(|tree| (None, Some(tree))));
    }

    /// Das Ergebnis einer Suche: die Treffer und, wenn sie vorzeitig aufhörte, nach wie vielen angesehenen
    /// Einträgen (`seen`).
    fn sftp_found(&self, request: u64, result: Result<(Vec<TreeEntry>, Option<u64>), Failure>) {
        match result {
            Ok((tree, seen)) => self.emit(Event::SftpResult {
                request,
                ok: true,
                entries: None,
                mux: None,
                logs: None,
                line: None,
                commands: None,
                run: None,
                limited: false,
                tree: Some(tree),
                size: None,
                usage: None,
                sums: None,
                data: None,
                target: None,
                seen,
                error: None,
                message: None,
            }),
            Err(failure) => self.sftp_answer(request, Err(failure)),
        }
    }

    /// Wo der X-Server dieses Rechners lauscht – Display 0, wenn die App kein anderes eingestellt hat.
    pub(crate) fn x_server(&self) -> String {
        lock(&self.x11)
            .clone()
            .unwrap_or_else(|| crate::x11::DEFAULT_SERVER.to_owned())
    }

    fn exec_result(&self, request: u64, result: Result<exec::Answer, Failure>) {
        let (mut size, mut usage, mut sums, mut data, mut mux) = (None, None, None, None, None);
        let (mut logs, mut limited) = (None, false);
        let mut line = None;
        let (mut commands, mut target, mut run) = (None, None, None);
        let (ok, error, message) = match result {
            Ok(answer) => {
                match answer {
                    exec::Answer::Nothing => {}
                    exec::Answer::Mux(list) => mux = Some(list),
                    exec::Answer::Logs(lines, partly) => (logs, limited) = (Some(lines), partly),
                    exec::Answer::Line(text) => line = Some(text),
                    exec::Answer::Commands(list) => commands = Some(list),
                    exec::Answer::Size(bytes) => size = bytes,
                    exec::Answer::Usage(list) => usage = Some(list),
                    exec::Answer::Sums(list) => sums = Some(list),
                    exec::Answer::Data(part, whole) => (data, size) = (Some(part), Some(whole)),
                    exec::Answer::Target(text) => target = Some(text),
                    exec::Answer::Run(output) => run = Some(output),
                }
                (true, None, None)
            }
            Err(failure) => (false, Some(failure.code), Some(failure.message)),
        };
        self.emit(Event::SftpResult {
            request,
            ok,
            entries: None,
            mux,
            logs,
            line,
            commands,
            run,
            limited,
            tree: None,
            size,
            usage,
            sums,
            data,
            target,
            seen: None,
            error,
            message,
        });
    }

    fn sftp_answer(&self, request: u64, result: Result<SftpAnswer, Failure>) {
        self.emit(match result {
            Ok((entries, tree)) => Event::SftpResult {
                request,
                ok: true,
                entries,
                mux: None,
                logs: None,
                line: None,
                commands: None,
                run: None,
                limited: false,
                tree,
                size: None,
                usage: None,
                sums: None,
                data: None,
                target: None,
                seen: None,
                error: None,
                message: None,
            },
            Err(failure) => Event::SftpResult {
                request,
                ok: false,
                entries: None,
                mux: None,
                logs: None,
                line: None,
                commands: None,
                run: None,
                limited: false,
                tree: None,
                size: None,
                usage: None,
                sums: None,
                data: None,
                target: None,
                seen: None,
                error: Some(failure.code),
                message: Some(failure.message),
            },
        });
    }

    /// Meldet das Ende einer Sitzung – genau einmal, egal ob sie von selbst endet oder geschlossen wird.
    pub(crate) fn finish(
        &self,
        session: u64,
        reason: CloseReason,
        message: Option<String>,
        exit_status: Option<u32>,
        trouble: Option<&'static str>,
    ) {
        let Some(entry) = lock(&self.sessions).remove(&session) else {
            return;
        };

        // Benutzt eine andere Sitzung dieselbe Verbindung mit (geteiltes Terminal, zweiter Tab), bleibt die
        // Verbindung stehen – dann laufen die Transfers weiter und gehören fortan dieser Sitzung. Sonst enden sie
        // mit der Verbindung und melden das selbst: als abgebrochen, wenn der Benutzer die Sitzung geschlossen hat,
        // sonst als gescheitert – dann behalten sie, womit ein neuer Versuch weitermachen kann.
        let halt = if reason == CloseReason::User {
            Halt::Cancelled
        } else {
            Halt::Lost
        };
        let link = self.release_link(session, halt);

        // Erst jetzt fällt, worüber die Transfers laufen: Sie wissen dann schon, warum – sonst sähe ein Schließen
        // durch den Benutzer für sie aus wie eine abgerissene Verbindung.
        entry.task.abort();
        lock(&self.prompts).retain(|_, pending| pending.session != session);
        drop(link);

        self.emit(Event::SessionState {
            session,
            state: SessionState::Closed,
            reason: Some(reason),
            message,
            exit_status,
            trouble,
            home: None,
            hop: None,
        });
    }

    /// Nimmt einer Sitzung ihre Verbindung samt SFTP-Kanal ab, ohne sie fallen zu lassen (das tut, wer den
    /// Rückgabewert fallen lässt). Benutzt eine andere Sitzung dieselbe Verbindung mit, gehören die Transfers fortan
    /// ihr; sonst erfahren sie mit `halt`, warum sie enden.
    fn release_link(&self, session: u64, halt: Halt) -> Option<Arc<Link>> {
        let (link, heir) = {
            let mut links = lock(&self.links);
            let link = links.remove(&session);
            let heir = link.as_ref().and_then(|link| {
                links
                    .iter()
                    .find(|(_, other)| Arc::ptr_eq(other, link))
                    .map(|(other, _)| *other)
            });
            (link, heir)
        };
        for transfer in lock(&self.transfers)
            .values_mut()
            .filter(|transfer| transfer.session == session)
        {
            match heir {
                Some(heir) => transfer.session = heir,
                None => transfer.halt(halt),
            }
        }

        lock(&self.sftp).remove(&session);
        lock(&self.names).remove(&session);
        link
    }

    /// Die SSH-Verbindung unter einem Mosh-Terminal ist abgerissen: Die Sitzung bleibt, ihre Dateien und
    /// Transfers nicht (die Transfers wie bei jedem Abriss – mit dem, womit ein neuer Versuch weitermachen kann).
    pub(crate) fn lose_link(&self, session: u64) {
        drop(self.release_link(session, Halt::Lost));
        self.emit(Event::SessionLinkLost { session, message: None });
    }

    /// Die SSH-Verbindung unter einem Mosh-Terminal steht wieder – eingetragen wie beim ersten Mal, nur gemeldet
    /// als `session.linked` statt als neuer Zustand.
    pub(crate) fn relinked(&self, session: u64, link: Arc<Link>, files: Option<(Arc<SftpSession>, String)>) {
        let alive = lock(&self.sessions).contains_key(&session);
        if !alive {
            return;
        }

        lock(&self.links).insert(session, link);
        let home = files.map(|(sftp, home)| {
            lock(&self.sftp).insert(session, sftp);
            home
        });
        self.emit(Event::SessionLinked { session, home });
    }
}

impl Core {
    /// Startet den Kern. Scheitert nur, wenn der Datenordner nicht nutzbar ist.
    pub fn new(config: CoreConfig, secrets: Arc<dyn SecretStore>, sink: Arc<dyn EventSink>) -> Result<Self, String> {
        std::fs::create_dir_all(&config.data_dir)
            .map_err(|error| format!("cannot create {}: {error}", config.data_dir.display()))?;
        let (mut profiles, set_aside) = ProfileStore::load(config.data_dir.join("profiles.json"))
            .map_err(|error| format!("cannot read profiles.json: {error}"))?;

        // Vorläufige Profile („Schnellverbinden“) eines Laufs, der nicht ordentlich endete: weg damit, samt dem, was
        // im Schlüsselbund zu ihnen liegt.
        for id in profiles.drop_transient() {
            crate::secrets::forget_password(&*secrets, &id);
            secrets.delete(&passphrase_key(&id));
        }

        // Zuerst, bevor irgendetwas ein Profil ändern kann: Jedes gespeicherte Passwort gehört zu dem Ziel, das
        // sein Profil jetzt hat (Passwörter aus einer älteren Version tragen den Vermerk noch nicht).
        crate::secrets::bind_unbound(&*secrets, profiles.list());

        // Verknüpfte Profile folgen ihrer Datei: bei jedem Start neu gelesen. Scheitert das Schreiben, bleibt es beim
        // gespeicherten Stand – verbinden lässt sich damit trotzdem.
        if let Some(ssh_dir) = &config.ssh_dir
            && profiles.list().iter().any(|profile| profile.ssh_alias.is_some())
        {
            let _ = sshconfig::refresh(&mut profiles, &sshconfig::Config::load(ssh_dir));
        }
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("nexus-core")
            .enable_all()
            .build()
            .map_err(|error| format!("cannot start the runtime: {error}"))?;

        let shared = Arc::new(Shared {
            sink,
            secrets,
            host_keys: HostKeys::new(
                config.data_dir.join("known_hosts"),
                config.ssh_dir.as_ref().map(|directory| directory.join("known_hosts")),
            ),
            tls_trust: crate::ftp::TlsTrust::load(config.data_dir.join("tls_trust.json")),
            ssh_dir: config.ssh_dir,
            agent: config.agent,
            x11: Mutex::new(config.x11),
            keepalive: Mutex::new(crate::profiles::KEEPALIVE_DEFAULT),
            unlocked: crate::unlocked::Unlocked::default(),
            transfer_slots: transfers::Slots::default(),
            limiter: transfers::Limiter::default(),
            trails: transfers::Trails::load(config.data_dir.join("trails.json")),
            profiles: Mutex::new(profiles),
            sessions: Mutex::new(HashMap::new()),
            sftp: Mutex::new(HashMap::new()),
            links: Mutex::new(HashMap::new()),
            names: Mutex::new(HashMap::new()),
            switches: Mutex::new(HashMap::new()),
            transfers: Mutex::new(HashMap::new()),
            follows: Mutex::new(HashMap::new()),
            prompts: Mutex::new(HashMap::new()),
            next_prompt: AtomicU64::new(1),
            traced: Mutex::default(),
            tunnels: Mutex::default(),
            quiet: Mutex::default(),
            mosh_threads: crate::mosh::Threads::default(),
        });

        if let Some(path) = set_aside {
            shared.emit(Event::Warning {
                code: "profiles_unreadable",
                message: format!("profiles.json could not be read and was moved to {}", path.display()),
            });
        }

        runtime.spawn(watch_config(Arc::downgrade(&shared)));

        Ok(Self {
            runtime: Some(runtime),
            shared,
        })
    }

    /// Führt eine Anfrage aus und liefert die Antwort als JSON.
    pub fn request(&self, json: &str) -> String {
        let response = match serde_json::from_str::<Request>(json) {
            Ok(request) => self.handle(request),
            Err(error) => Response::error("bad_request", error.to_string()),
        };
        serde_json::to_string(&response).unwrap_or_else(|_| r#"{"ok":false,"error":"internal"}"#.to_owned())
    }

    /// Eingabe des Benutzers an die Shell einer Sitzung. `false`, wenn es die Sitzung nicht gibt.
    pub fn write(&self, session: u64, data: &[u8]) -> bool {
        match lock(&self.shared.sessions).get(&session) {
            Some(entry) => entry.commands.send(Command::Input(data.to_vec())).is_ok(),
            None => false,
        }
    }

    fn handle(&self, request: Request) -> Response {
        let shared = &self.shared;
        match request {
            Request::ProfilesList => Response {
                ok: true,
                profiles: Some(
                    lock(&shared.profiles)
                        .list()
                        .iter()
                        .map(|profile| ProfileInfo {
                            // Ein Passwort, das zu einem anderen Ziel gehört, zählt nicht – und ist danach vergessen.
                            saved_secret: crate::secrets::saved_password(&*shared.secrets, profile).is_some()
                                || shared.secrets.get(&passphrase_key(&profile.id)).is_some(),
                            profile: profile.clone(),
                        })
                        .collect(),
                ),
                ..Response::default()
            },

            Request::SecretsForget { id } => {
                crate::secrets::forget_password(&*shared.secrets, &id);
                shared.secrets.delete(&passphrase_key(&id));
                Response::ok()
            }

            Request::ProfilesSave { profile } => {
                let profile = match (*profile).normalized() {
                    Ok(profile) => profile,
                    Err(field) => return Response::error("invalid_profile", field),
                };
                let mut profiles = lock(&shared.profiles);
                match profiles.save(profile) {
                    Ok(saved) => {
                        // Ein verknüpftes Profil bekommt gleich, was in der Datei steht – auch wenn die Verknüpfung
                        // eben erst gesetzt wurde. Die Antwort nennt das Profil, wie es danach gespeichert ist.
                        if let (Some(_), Some(ssh_dir)) = (&saved.ssh_alias, &shared.ssh_dir) {
                            let _ = sshconfig::refresh(&mut profiles, &sshconfig::Config::load(ssh_dir));
                        }

                        let stored = profiles.list().iter().find(|profile| profile.id == saved.id).cloned();
                        Response {
                            ok: true,
                            profile: Some(stored.unwrap_or(saved)),
                            ..Response::default()
                        }
                    }
                    Err(error) => Response::error("storage", error.to_string()),
                }
            }

            Request::ProfilesDelete { id } => match lock(&shared.profiles).delete(&id) {
                Ok(_) => {
                    // Mit dem Profil gehen auch seine Geheimnisse – sonst blieben sie verwaist im Schlüsselbund.
                    crate::secrets::forget_password(&*shared.secrets, &id);
                    shared.secrets.delete(&passphrase_key(&id));
                    Response::ok()
                }
                Err(error) => Response::error("storage", error.to_string()),
            },

            Request::SessionOpen {
                session,
                profile_id,
                cols,
                rows,
                share,
                trace,
                mosh_predict,
            } => {
                if trace {
                    lock(&shared.traced).insert(session);
                } else {
                    lock(&shared.traced).remove(&session);
                }

                // Der Weg zum Server: erst die Sprung-Server, zuletzt das Profil selbst.
                let chain = match lock(&shared.profiles).chain(&profile_id) {
                    Ok(chain) => chain,
                    Err(ChainError::UnknownProfile) => return Response::error("unknown_profile", profile_id),
                    Err(ChainError::JumpMissing) => {
                        return Response::error("jump_missing", "the jump host profile no longer exists");
                    }
                    Err(ChainError::Loop) => {
                        return Response::error("jump_loop", "the jump hosts refer to each other in a circle");
                    }
                    Err(ChainError::TooLong) => return Response::error("jump_loop", "too many jump hosts in a row"),
                };
                let Some(runtime) = &self.runtime else {
                    return Response::error("internal", "runtime is gone");
                };

                // Die Sitzung wird unter dem Lock eingetragen: Endet sie sofort, wartet ihr `finish` hier,
                // statt eine noch nicht eingetragene Sitzung zu „beenden“.
                let mut sessions = lock(&shared.sessions);
                if sessions.contains_key(&session) {
                    return Response::error("session_exists", session.to_string());
                }

                let (commands, receiver) = mpsc::unbounded_channel();
                let task = runtime.spawn(session::run(
                    Arc::clone(shared),
                    session,
                    chain,
                    share,
                    (cols.max(1), rows.max(1)),
                    crate::mosh::prediction(mosh_predict.as_deref()),
                    receiver,
                ));
                sessions.insert(
                    session,
                    SessionEntry {
                        commands,
                        task: task.abort_handle(),
                    },
                );
                Response::ok()
            }

            Request::SessionResize { session, cols, rows } => match lock(&shared.sessions).get(&session) {
                Some(entry) => {
                    let _ = entry.commands.send(Command::Resize(cols.max(1), rows.max(1)));
                    Response::ok()
                }
                None => Response::error("unknown_session", session.to_string()),
            },

            Request::SessionRelink { session } => match lock(&shared.sessions).get(&session) {
                Some(entry) => {
                    let _ = entry.commands.send(Command::Relink);
                    Response::ok()
                }
                None => Response::error("unknown_session", session.to_string()),
            },

            Request::SessionClose { session } => {
                shared.finish(session, CloseReason::User, None, None, None);
                Response::ok()
            }

            Request::SftpList { session, request, path } => {
                let shared = Arc::clone(shared);
                self.sftp(session, request, move |sftp| async move {
                    let names = shared.names(session, &sftp).await;
                    sftp::list(&sftp, &path, &names).await.map(Some)
                })
            }

            Request::SftpMkdir { session, request, path } => self.sftp(session, request, move |sftp| async move {
                sftp::make_directory(&sftp, &path).await.map(|()| None)
            }),

            Request::SftpTouch { session, request, path } => self.sftp(session, request, move |sftp| async move {
                sftp::create_file(&sftp, &path).await.map(|()| None)
            }),

            Request::SftpSymlink {
                session,
                request,
                path,
                to,
            } => self.sftp(session, request, move |sftp| async move {
                sftp::symlink(&sftp, &path, &to).await.map(|()| None)
            }),

            Request::SftpReadlink { session, request, path } => self.file(session, request, move |sftp| async move {
                sftp::readlink(&sftp, &path).await.map(exec::Answer::Target)
            }),

            Request::SftpRename {
                session,
                request,
                from,
                to,
            } => self.sftp(session, request, move |sftp| async move {
                sftp::rename(&sftp, &from, &to).await.map(|()| None)
            }),

            Request::SftpChmod {
                session,
                request,
                path,
                mode,
                recursive,
            } => self.sftp(session, request, move |sftp| async move {
                let change = sftp::Change {
                    mode: Some(mode),
                    ..sftp::Change::default()
                };
                sftp::change(&sftp, &path, change, recursive).await.map(|()| None)
            }),

            Request::SftpChown {
                session,
                request,
                path,
                owner,
                group,
                recursive,
            } => {
                let shared = Arc::clone(shared);
                self.sftp(session, request, move |sftp| async move {
                    // SFTP kennt nur Nummern; die Namen dazu stehen in /etc/passwd und /etc/group des Servers.
                    let names = shared.names(session, &sftp).await;
                    let unknown = |name: &str| Failure {
                        code: "unknown_owner",
                        message: name.to_owned(),
                    };
                    let uid = match owner.as_deref().map(str::trim).filter(|name| !name.is_empty()) {
                        Some(name) => Some(names.user_id(name).ok_or_else(|| unknown(name))?),
                        None => None,
                    };
                    let gid = match group.as_deref().map(str::trim).filter(|name| !name.is_empty()) {
                        Some(name) => Some(names.group_id(name).ok_or_else(|| unknown(name))?),
                        None => None,
                    };
                    if uid.is_none() && gid.is_none() {
                        return Ok(None);
                    }

                    let change = sftp::Change { mode: None, uid, gid };
                    sftp::change(&sftp, &path, change, recursive).await.map(|()| None)
                })
            }

            Request::SftpReopen { session, request } => {
                let (Some(link), Some(runtime)) = (lock(&shared.links).get(&session).cloned(), &self.runtime) else {
                    return Response::error("unknown_session", session.to_string());
                };

                let shared = Arc::clone(shared);
                let turn = shared.begin_switch(session);
                runtime.spawn(async move {
                    let opened = link.open_sftp().await;
                    if !shared.is_latest_switch(session, turn) {
                        // Inzwischen wurde etwas anderes verlangt (etwa „als root“): Dieser Kanal gilt nicht mehr.
                        shared.sftp_result(
                            request,
                            Err(Failure {
                                code: "superseded",
                                message: "another change of the file channel was requested meanwhile".to_owned(),
                            }),
                        );
                        return;
                    }

                    let result = match opened {
                        Some((sftp, _home)) => {
                            // Nur eintragen, solange es die Sitzung noch gibt. Der alte Kanal fällt damit weg; was
                            // noch auf ihm unterwegs ist, endet mit seiner Frist.
                            if lock(&shared.sessions).contains_key(&session) {
                                lock(&shared.sftp).insert(session, sftp);
                                lock(&shared.names).remove(&session);
                            }

                            Ok(None)
                        }
                        None => {
                            // Kein neuer Kanal: Dann bleibt auch der alte nicht – er kann der von root gewesen
                            // sein, und die App zeigt ab jetzt die Sicht des Benutzers. Lieber kein Dateizugriff
                            // als einer mit Rechten, von denen niemand mehr weiß.
                            lock(&shared.sftp).remove(&session);
                            lock(&shared.names).remove(&session);
                            Err(Failure {
                                code: "no_sftp",
                                message: "the server did not open a new SFTP channel".to_owned(),
                            })
                        }
                    };
                    shared.sftp_result(request, result);
                });
                Response::ok()
            }

            Request::SftpTree {
                session,
                request,
                path,
                exclude,
                excluded,
            } => {
                // Ein Muster, das keines sein kann: lieber gar nicht antworten als etwas anderes auslassen.
                let Some(exclude) = crate::excludes::Excludes::new(&exclude.unwrap_or_default()) else {
                    return Response::error("invalid_exclude", "an exclusion pattern is not valid");
                };
                let (Some(sftp), Some(runtime)) = (lock(&shared.sftp).get(&session).cloned(), &self.runtime) else {
                    return Response::error("no_sftp", session.to_string());
                };

                let shared = Arc::clone(shared);
                runtime.spawn(async move {
                    shared.sftp_tree(request, transfers::tree(&sftp, &path, &exclude, excluded).await);
                });
                Response::ok()
            }

            Request::SftpFind {
                session,
                request,
                path,
                name,
            } => {
                let (Some(sftp), Some(runtime)) = (lock(&shared.sftp).get(&session).cloned(), &self.runtime) else {
                    return Response::error("no_sftp", session.to_string());
                };

                let shared = Arc::clone(shared);
                runtime.spawn(async move {
                    shared.sftp_found(request, sftp::find(&sftp, &path, &name).await);
                });
                Response::ok()
            }

            Request::SftpPack {
                session,
                request,
                directory,
                names,
                archive,
            } => self.exec_paths(
                session,
                request,
                vec![directory],
                move |paths| {
                    Ok((
                        exec::pack_command(&paths[0], &names, &archive)?,
                        crate::winserver::pack(&paths[0], &names, &archive).ok(),
                    ))
                },
                |_, _| Ok(exec::Answer::Nothing),
            ),

            Request::SftpUnpack {
                session,
                request,
                directory,
                archive,
            } => self.exec_paths(
                session,
                request,
                vec![directory],
                move |paths| {
                    Ok((
                        exec::unpack_command(&paths[0], &archive)?,
                        crate::winserver::unpack(&paths[0], &archive).ok(),
                    ))
                },
                |_, _| Ok(exec::Answer::Nothing),
            ),

            Request::SftpSize { session, request, path } => self.exec_paths(
                session,
                request,
                vec![path],
                |paths| Ok((exec::size_command(&paths[0])?, crate::winserver::size(&paths[0]).ok())),
                |output, _| Ok(exec::Answer::Size(exec::parse_size(&output.stdout))),
            ),

            // Die Antwort nennt die Einträge unter dem Pfad, wie die App ihn kennt (dem des SFTP-Servers).
            Request::SftpUsage { session, request, path } => self.exec_paths(
                session,
                request,
                vec![path.clone()],
                |paths| Ok((exec::usage_command(&paths[0])?, crate::winserver::usage(&paths[0]).ok())),
                move |output, _| Ok(exec::Answer::Usage(exec::parse_usage(&output.stdout, &path))),
            ),

            Request::SftpChecksum {
                session,
                request,
                paths,
            } => {
                let count = paths.len();
                self.exec_paths(
                    session,
                    request,
                    paths,
                    |paths| Ok((exec::checksum_command(paths)?, crate::winserver::checksums(paths).ok())),
                    move |output, _| Ok(exec::Answer::Sums(exec::parse_checksums(&output.stdout, count))),
                )
            }

            Request::SftpCopy {
                session,
                request,
                pairs,
                moving,
            } => {
                let pairs: Vec<(String, String)> = pairs.into_iter().map(|pair| (pair.from, pair.to)).collect();
                if let Err(failure) = exec::copy_command(&pairs, moving) {
                    return Response::error(failure.code, failure.message);
                }
                let Some(runtime) = &self.runtime else {
                    return Response::error("unknown_session", session.to_string());
                };
                let (link, sftp) = (
                    lock(&shared.links).get(&session).cloned(),
                    lock(&shared.sftp).get(&session).cloned(),
                );
                if link.is_none() && sftp.is_none() {
                    return Response::error("unknown_session", session.to_string());
                }

                // Mit Shell kopiert `cp` am Server. Wo es keine Befehle gibt – das Konto darf nur SFTP, oder es
                // ist FTP –, kopiert der Kern selbst: lesen und gleich wieder schreiben. Verschieben braucht das
                // nicht: Das versucht die App vorher mit dem Umbenennen von SFTP.
                let shared = Arc::clone(shared);
                runtime.spawn(async move {
                    // Unter Windows dasselbe mit PowerShell ([`crate::winserver::copy`]); die Pfade so, wie die
                    // Shell sie nennt (bei DSM anders als der SFTP-Server).
                    let ran = match &link {
                        Some(link) => {
                            let flat: Vec<String> =
                                pairs.iter().flat_map(|(from, to)| [from.clone(), to.clone()]).collect();
                            let mapped = link.shell_paths(&flat).await;
                            let shell: Vec<(String, String)> = mapped
                                .chunks(2)
                                .map(|pair| (pair[0].clone(), pair[1].clone()))
                                .collect();
                            let windows = crate::winserver::copy(&pairs, moving).ok();
                            Some(match exec::copy_command(&shell, moving) {
                                Ok(command) => link
                                    .exec_either(&command, windows.as_deref())
                                    .await
                                    .and_then(exec::succeeded),
                                Err(failure) => Err(failure),
                            })
                        }
                        None => None,
                    };
                    let result = match (ran, sftp) {
                        (Some(Ok(_)), _) => Ok(exec::Answer::Nothing),
                        (Some(Err(failure)), Some(sftp))
                            if !moving && matches!(failure.code, "no_exec" | "no_posix_shell") =>
                        {
                            copy_over_sftp(&shared, &sftp, &pairs).await
                        }
                        (None, Some(sftp)) if !moving => copy_over_sftp(&shared, &sftp, &pairs).await,
                        (Some(Err(failure)), _) => Err(failure),
                        (None, _) => Err(crate::sftp::Failure {
                            code: "no_exec",
                            message: "the server does not run commands for this account".to_owned(),
                        }),
                    };
                    shared.exec_result(request, result);
                });
                Response::ok()
            }

            Request::SftpElevate {
                session,
                request,
                password,
            } => {
                let (Some(link), Some(runtime)) = (lock(&shared.links).get(&session).cloned(), &self.runtime) else {
                    return Response::error("unknown_session", session.to_string());
                };

                let shared = Arc::clone(shared);
                let turn = shared.begin_switch(session);
                runtime.spawn(async move {
                    let opened = link.open_root_sftp(password.as_deref()).await;
                    if !shared.is_latest_switch(session, turn) {
                        // Inzwischen hat der Benutzer root wieder verlassen: Der Kanal von root wird nicht
                        // eingetragen – er fiele sonst nach dem des Benutzers an dessen Stelle.
                        shared.sftp_result(
                            request,
                            Err(Failure {
                                code: "superseded",
                                message: "another change of the file channel was requested meanwhile".to_owned(),
                            }),
                        );
                        return;
                    }

                    let result = opened.map(|sftp| {
                        // Wie beim Neuöffnen: nur eintragen, solange es die Sitzung noch gibt. Der Kanal des
                        // Benutzers fällt damit weg, bis `sftp.reopen` ihn wieder holt.
                        if lock(&shared.sessions).contains_key(&session) {
                            lock(&shared.sftp).insert(session, sftp);
                            lock(&shared.names).remove(&session);
                        }

                        None
                    });
                    shared.sftp_result(request, result);
                });
                Response::ok()
            }

            Request::SftpRead {
                session,
                request,
                path,
                offset,
                length,
            } => self.file(session, request, move |sftp| async move {
                let (data, size) = sftp::read_part(&sftp, &path, offset, length).await?;
                Ok(exec::Answer::Data(sftp::to_base64(&data), size))
            }),

            Request::SftpWrite {
                session,
                request,
                path,
                data,
            } => match sftp::from_base64(&data) {
                Some(data) => self.file(session, request, move |sftp| async move {
                    sftp::write_whole(&sftp, &path, &data)
                        .await
                        .map(|()| exec::Answer::Nothing)
                }),
                None => Response::error("bad_request", "data is not base64"),
            },

            Request::FollowStart { session, follow, path } => {
                if let Err(failure) = exec::follow_command(&path) {
                    return Response::error(failure.code, failure.message);
                }
                let (Some(link), Some(runtime)) = (lock(&shared.links).get(&session).cloned(), &self.runtime) else {
                    return Response::error("unknown_session", session.to_string());
                };

                let (stop, stopped) = oneshot::channel();
                lock(&shared.follows).insert(follow, stop);
                let shared = Arc::clone(&self.shared);
                runtime.spawn(async move {
                    let sink = Arc::clone(&shared);
                    let shell = link.shell_paths(std::slice::from_ref(&path)).await;
                    let result = match exec::follow_command(&shell[0]) {
                        Ok(command) => {
                            link.follow(&exec::posix(&command), stopped, move |text| {
                                sink.emit(Event::FollowData { follow, text })
                            })
                            .await
                        }
                        Err(failure) => Err(failure),
                    };
                    lock(&shared.follows).remove(&follow);
                    let (error, message) = match result {
                        Ok(()) => (None, None),
                        Err(failure) => (Some(failure.code), Some(failure.message)),
                    };
                    shared.emit(Event::FollowEnd { follow, error, message });
                });
                Response::ok()
            }

            Request::FollowStop { follow } => {
                if let Some(stop) = lock(&shared.follows).remove(&follow) {
                    let _ = stop.send(());
                }

                Response::ok()
            }

            Request::LogsRead {
                session,
                request,
                path,
                since,
                priority,
                unit,
                lines,
            } => match path {
                Some(path) => self.exec_paths(
                    session,
                    request,
                    vec![path],
                    move |paths| match crate::logs::file_command(&paths[0], lines) {
                        Some(command) => Ok((command, None)),
                        None => Err(Failure {
                            code: "invalid_request",
                            message: "not a path of a file".to_owned(),
                        }),
                    },
                    |output, _| Ok(crate::logs::read_file(output)),
                ),
                None => match crate::logs::journal_command(lines, priority, since, unit.as_deref()) {
                    Some(command) => self.exec_checked(session, request, command, crate::logs::read_journal),
                    None => Response::error("invalid_request", "not a unit or a priority".to_owned()),
                },
            },

            // Antwortet mit der Art der Shell (`posix` oder `windows`): Unter Windows gibt es nur einen Teil der
            // Befehle, und die App blendet den Rest aus.
            Request::CommandsProbe { session, request } => self.exec_either(
                session,
                request,
                ":".to_owned(),
                Some(crate::winserver::script("$null")),
                |_, shell| {
                    Ok(exec::Answer::Line(
                        if shell == crate::session::Shell::Windows {
                            "windows"
                        } else {
                            "posix"
                        }
                        .to_owned(),
                    ))
                },
            ),

            Request::CommandsShell { session, request } => self.exec_either(
                session,
                request,
                exec::LOGIN_SHELL.to_owned(),
                Some(crate::winserver::script("'windows'")),
                |output, _| Ok(exec::Answer::Line(output.stdout.trim().to_owned())),
            ),

            Request::ExecRun {
                session,
                request,
                command,
                seconds,
            } => {
                let (Some(link), Some(runtime)) = (lock(&self.shared.links).get(&session).cloned(), &self.runtime)
                else {
                    return Response::error("unknown_session", session.to_string());
                };

                let shared = Arc::clone(&self.shared);
                runtime.spawn(async move {
                    let result = link.run_command(&command, seconds).await;
                    shared.exec_result(request, result.map(exec::Answer::Run));
                });
                Response::ok()
            }

            Request::CommandsList { session, request } => {
                self.exec(session, request, exec::COMMAND_NAMES.to_owned(), |output| {
                    exec::Answer::Commands(exec::parse_command_names(&output.stdout))
                })
            }

            Request::MuxList { session, request } => {
                self.exec(session, request, exec::MUX_SESSIONS.to_owned(), |output| {
                    exec::Answer::Mux(exec::parse_mux(&output.stdout))
                })
            }

            Request::KeyInstall {
                session,
                request,
                public_key,
            } => match keys::install_command(&public_key) {
                Ok(command) => self.exec(session, request, command, |_| exec::Answer::Nothing),
                Err(failure) => Response::error(failure.code, failure.message),
            },

            Request::SftpDelete { session, request, path } => self.sftp(session, request, move |sftp| async move {
                sftp::delete(&sftp, &path).await.map(|()| None)
            }),

            Request::TransferStart {
                session,
                transfer,
                direction,
                local,
                remote,
                name,
                only,
                exclude,
                target_session,
                resume,
                limit,
                keep,
            } => {
                let (Some(sftp), Some(runtime)) = (lock(&shared.sftp).get(&session).cloned(), &self.runtime) else {
                    return Response::error("no_sftp", session.to_string());
                };

                // Ein Muster, das keines sein kann: lieber nicht übertragen als etwas anderes auslassen als gemeint.
                let Some(exclude) = crate::excludes::Excludes::new(&exclude.unwrap_or_default()) else {
                    return Response::error("invalid_exclude", "an exclusion pattern is not valid");
                };

                // Von Server zu Server braucht es den Dateikanal einer zweiten Sitzung – und nur dann.
                let relay = match (direction, target_session) {
                    (crate::protocol::Direction::Relay, Some(target)) => match lock(&shared.sftp).get(&target).cloned()
                    {
                        Some(sftp) => Some(sftp),
                        None => return Response::error("no_sftp", target.to_string()),
                    },
                    (crate::protocol::Direction::Relay, None) => {
                        return Response::error("bad_request", "a relay needs a target session");
                    }
                    _ => None,
                };

                let mut transfers = lock(&shared.transfers);
                if transfers.contains_key(&transfer) {
                    return Response::error("transfer_exists", transfer.to_string());
                }

                let (halt, halted) = watch::channel(None);
                // Die Wartenummer gibt es schon hier: Die Reihenfolge der Anfragen zählt, nicht die, in der die
                // Aufgaben der Transfers zum Zug kommen.
                let ticket = shared.transfer_slots.enqueue();
                transfers.insert(transfer, TransferEntry { session, ticket, halt });
                runtime.spawn(transfers::run(
                    Arc::clone(shared),
                    sftp,
                    Job {
                        transfer,
                        ticket,
                        direction,
                        local: PathBuf::from(local),
                        remote,
                        name,
                        only,
                        exclude,
                        relay,
                        resume,
                        limit,
                        keep,
                    },
                    halted,
                ));
                Response::ok()
            }

            Request::TransferTarget { local, remote } => {
                match transfers::download_target(&PathBuf::from(local), &remote) {
                    Some(target) => Response {
                        ok: true,
                        path: Some(target.display().to_string()),
                        ..Response::default()
                    },
                    None => Response::error("unsafe_name", remote),
                }
            }

            Request::TransferRetain { jobs } => {
                let keys: Vec<transfers::Key> = jobs
                    .into_iter()
                    .map(|job| (job.direction, PathBuf::from(job.local), job.remote, job.name))
                    .collect();
                shared.trails.retain(&keys);
                Response::ok()
            }

            Request::Probe { request, profile } => {
                let Some(runtime) = &self.runtime else {
                    return Response::error("no_runtime", String::new());
                };
                let profile = match (*profile).normalized() {
                    Ok(profile) => profile,
                    Err(field) => return Response::error("invalid_profile", field),
                };

                let shared = Arc::clone(shared);
                runtime.spawn(async move {
                    let report = crate::probe::run(&profile, &shared.host_keys).await;
                    shared.emit(Event::ProbeResult {
                        request,
                        step: report.step,
                        addresses: report.addresses,
                        address: report.address,
                        connect_ms: report.connect_ms,
                        software: report.software,
                        key_algorithm: report.key_algorithm,
                        fingerprint: report.fingerprint,
                        key_state: report.key_state,
                        methods: report.methods,
                        trouble: report.trouble,
                        message: report.message,
                    });
                });
                Response::ok()
            }

            Request::KeysUnlocked { remember } => {
                match remember {
                    Some(enabled) => shared.unlocked.set_enabled(enabled),
                    None => shared.unlocked.forget(),
                }

                Response::ok()
            }

            Request::LocalShells => Response {
                ok: true,
                shells: Some(
                    crate::local::shells()
                        .into_iter()
                        .map(|shell| crate::protocol::LocalShell {
                            id: shell.id,
                            name: shell.name,
                        })
                        .collect(),
                ),
                ..Response::default()
            },

            Request::TunnelSet { session, tunnel, on } => {
                let (Some(link), Some(runtime)) = (shared.link_of(session), &self.runtime) else {
                    return Response::error("not_connected", session.to_string());
                };

                // Was der Tunnel ist, steht im gespeicherten Profil – die App nennt nur seine Kennung.
                let wanted = lock(&shared.profiles)
                    .get(&link.profile_id)
                    .and_then(|profile| profile.tunnels.iter().find(|candidate| candidate.id == tunnel).cloned());
                match (wanted, on) {
                    (Some(wanted), true) => {
                        runtime.spawn(async move { link.tunnels.start(&link, &wanted).await });
                    }
                    (None, true) => return Response::error("unknown_tunnel", tunnel),
                    // Anhalten geht auch, wenn der Tunnel inzwischen aus dem Profil gelöscht ist. Stehen zwei
                    // Verbindungen zum selben Profil, trägt ihn vielleicht die andere – dann dort.
                    (_, false) => {
                        let owner = shared.tunnel_owner(&link.profile_id, &tunnel).unwrap_or(link);
                        runtime.spawn(async move { owner.tunnels.stop(&owner, &tunnel).await });
                    }
                }

                Response::ok()
            }

            Request::KeepaliveSet { interval, max } => {
                if !crate::profiles::keepalive_interval(interval) || !crate::profiles::KEEPALIVE_MAX.contains(&max) {
                    return Response::error("invalid_keepalive", format!("{interval}/{max}"));
                }

                *lock(&shared.keepalive) = (interval, max);
                Response::ok()
            }

            Request::X11Display { display } => {
                if display > crate::x11::MAX_DISPLAY {
                    return Response::error("invalid_display", display.to_string());
                }

                *lock(&shared.x11) = Some(crate::x11::server_of(display));
                Response::ok()
            }

            Request::TransferLimit { bytes_per_second } => {
                shared.limiter.set(bytes_per_second);
                Response::ok()
            }

            Request::TransferSuspend => {
                for entry in lock(&shared.transfers).values() {
                    entry.halt(Halt::Lost);
                }
                Response::ok()
            }

            Request::TransferPause { transfer } => match lock(&shared.transfers).get(&transfer) {
                Some(entry) => {
                    entry.halt(Halt::Lost);
                    Response::ok()
                }
                None => Response::error("unknown_transfer", transfer.to_string()),
            },

            Request::TransferMove { transfer, before } => {
                let transfers = lock(&shared.transfers);
                let ticket = |transfer: u64| transfers.get(&transfer).map(|entry| entry.ticket);
                match (ticket(transfer), before.map(ticket)) {
                    (None, _) | (_, Some(None)) => Response::error("unknown_transfer", transfer.to_string()),
                    (Some(moved), before) => {
                        if shared.transfer_slots.move_before(moved, before.flatten()) {
                            Response::ok()
                        } else {
                            Response::error("not_waiting", transfer.to_string())
                        }
                    }
                }
            }

            Request::TransferCancel { transfer } => match lock(&shared.transfers).get(&transfer) {
                Some(entry) => {
                    entry.halt(Halt::Cancelled);
                    Response::ok()
                }
                None => Response::error("unknown_transfer", transfer.to_string()),
            },

            Request::SessionFlow { session, paused } => match lock(&shared.sessions).get(&session) {
                Some(entry) => {
                    let _ = entry.commands.send(Command::Flow(paused));
                    Response::ok()
                }
                None => Response::error("unknown_session", session.to_string()),
            },

            Request::MonitorSet { session, enabled } => match lock(&shared.sessions).get(&session) {
                Some(entry) => {
                    let _ = entry.commands.send(Command::Monitor(enabled));
                    Response::ok()
                }
                None => Response::error("unknown_session", session.to_string()),
            },

            Request::HostKeysList => Response {
                ok: true,
                host_keys: Some(shared.host_keys.list()),
                ..Response::default()
            },

            Request::HostKeysForget { name, authority } => {
                let forgotten = match &authority {
                    Some(print) => shared.host_keys.forget_authority(&name, print),
                    None => shared.host_keys.forget(&name),
                };
                match forgotten {
                    Ok(true) => Response::ok(),
                    Ok(false) => Response::error("not_found", name),
                    Err(error) => Response::error("failed", error.to_string()),
                }
            }

            Request::HostKeysAuthority { patterns, key } => {
                use crate::hostkeys::AuthorityError;
                match shared.host_keys.trust_authority(&patterns, &key) {
                    Ok(added) => Response {
                        ok: true,
                        host_keys: Some(vec![added]),
                        ..Response::default()
                    },
                    Err(AuthorityError::Patterns) => {
                        Response::error("invalid_patterns", "these are not host name patterns")
                    }
                    Err(AuthorityError::Key) => Response::error("invalid_key", "this is not a public key"),
                    Err(AuthorityError::File(error)) => Response::error("failed", error.to_string()),
                }
            }

            Request::KeyGenerate {
                name,
                passphrase,
                comment,
            } => {
                let Some(ssh_dir) = &shared.ssh_dir else {
                    return Response::error("no_ssh_dir", "there is no ~/.ssh directory");
                };

                match keys::generate(ssh_dir, &name, passphrase.as_deref(), &comment) {
                    Ok(generated) => Response {
                        ok: true,
                        path: Some(generated.path.display().to_string()),
                        public_key: Some(generated.public_key),
                        ..Response::default()
                    },
                    Err(failure) => Response::error(failure.code, failure.message),
                }
            }

            Request::KeyList { paths } => Response {
                ok: true,
                keys: Some(crate::keyfiles::list(shared.ssh_dir.as_deref(), &paths)),
                ..Response::default()
            },

            Request::KeyPassphrase {
                path,
                passphrase,
                new_passphrase,
            } => {
                let path = std::path::PathBuf::from(path);
                match crate::keyfiles::change_passphrase(&path, passphrase.as_deref(), new_passphrase.as_deref()) {
                    // Was entsperrt im Speicher lag, gehört zur alten Datei – `Unlocked` erkennt sie an Größe und
                    // Zeit nicht wieder und fragt beim nächsten Verbinden neu.
                    Ok(()) => Response {
                        ok: true,
                        ..Response::default()
                    },
                    Err(failure) => Response::error(failure.code, failure.message),
                }
            }

            Request::KeyConvert {
                path,
                passphrase,
                format,
                name,
            } => match crate::keyfiles::convert(std::path::Path::new(&path), passphrase.as_deref(), format, &name) {
                Ok(written) => Response {
                    ok: true,
                    path: Some(written.display().to_string()),
                    ..Response::default()
                },
                Err(failure) => Response::error(failure.code, failure.message),
            },

            Request::SshConfigRead => {
                let Some(ssh_dir) = &shared.ssh_dir else {
                    return Response {
                        ok: true,
                        hosts: Some(Vec::new()),
                        ..Response::default()
                    };
                };

                let config = sshconfig::Config::load(ssh_dir);
                let mut hosts = config.hosts;
                sshconfig::mark_existing(&mut hosts, &lock(&shared.profiles));
                Response {
                    ok: true,
                    hosts: Some(hosts),
                    unchecked: (!config.unchecked.is_empty()).then_some(config.unchecked),
                    path: Some(sshconfig::config_path(ssh_dir).display().to_string()),
                    ..Response::default()
                }
            }

            Request::SshConfigRefresh => {
                let Some(ssh_dir) = &shared.ssh_dir else {
                    return Response {
                        ok: true,
                        imported: Some(0),
                        ..Response::default()
                    };
                };

                let config = sshconfig::Config::load(ssh_dir);
                match sshconfig::refresh(&mut lock(&shared.profiles), &config) {
                    Ok(changed) => {
                        if changed > 0 {
                            shared.emit(Event::ProfilesChanged);
                        }

                        Response {
                            ok: true,
                            imported: Some(changed),
                            ..Response::default()
                        }
                    }
                    Err(error) => Response::error("storage", error.to_string()),
                }
            }

            Request::SshConfigImport { names, linked } => {
                let Some(ssh_dir) = &shared.ssh_dir else {
                    return Response::error("no_ssh_dir", "there is no ~/.ssh directory");
                };

                let config = sshconfig::Config::load(ssh_dir);
                match sshconfig::import(&mut lock(&shared.profiles), &config, &names, linked) {
                    Ok(created) => Response {
                        ok: true,
                        imported: Some(created.len()),
                        ..Response::default()
                    },
                    Err(error) => Response::error("storage", error.to_string()),
                }
            }

            Request::PromptAnswer { prompt, answer } => match lock(&shared.prompts).remove(&prompt) {
                Some(pending) => {
                    let _ = pending.reply.send(answer);
                    Response::ok()
                }
                None => Response::error("unknown_prompt", prompt.to_string()),
            },
        }
    }
}

impl Core {
    /// Stößt eine Dateioperation an; ihr Ergebnis kommt als `sftp.result`.
    fn sftp<F, R>(&self, session: u64, request: u64, operation: F) -> Response
    where
        F: FnOnce(Arc<SftpSession>) -> R + Send + 'static,
        R: Future<Output = Result<Option<Vec<Entry>>, Failure>> + Send,
    {
        let (Some(sftp), Some(runtime)) = (lock(&self.shared.sftp).get(&session).cloned(), &self.runtime) else {
            return Response::error("no_sftp", session.to_string());
        };

        let shared = Arc::clone(&self.shared);
        runtime.spawn(async move {
            let result = operation(sftp).await;
            shared.sftp_result(request, result);
        });
        Response::ok()
    }

    /// Wie [`Self::sftp`], für Operationen, die mehr zurückgeben als Einträge – ein Stück einer Datei.
    fn file<F, R>(&self, session: u64, request: u64, operation: F) -> Response
    where
        F: FnOnce(Arc<SftpSession>) -> R + Send + 'static,
        R: Future<Output = Result<exec::Answer, Failure>> + Send,
    {
        let (Some(sftp), Some(runtime)) = (lock(&self.shared.sftp).get(&session).cloned(), &self.runtime) else {
            return Response::error("no_sftp", session.to_string());
        };

        let shared = Arc::clone(&self.shared);
        runtime.spawn(async move {
            let result = operation(sftp).await;
            shared.exec_result(request, result);
        });
        Response::ok()
    }

    /// Führt einen Befehl auf dem Server der Sitzung aus; das Ergebnis kommt als `sftp.result` – mit dem, was
    /// `read` aus der Ausgabe liest (die Prozessliste), sonst nur als gelungen oder gescheitert.
    fn exec<F>(&self, session: u64, request: u64, command: String, read: F) -> Response
    where
        F: FnOnce(&exec::Output) -> exec::Answer + Send + 'static,
    {
        self.exec_checked(session, request, command, |output| Ok(read(output)))
    }

    /// Wie [`Self::exec_either`] für Befehle mit Pfaden des SFTP-Servers: `build` baut aus den Pfaden den
    /// POSIX-Befehl und das Skript für Windows. Geprüft wird vorab mit den Pfaden, wie sie kamen (ein Fehler kommt
    /// gleich als Antwort); gebaut wird mit den Pfaden der Shell ([`crate::session::Link::shell_paths`] – bei DSM
    /// heißen sie dort anders).
    fn exec_paths<B, F>(&self, session: u64, request: u64, paths: Vec<String>, build: B, read: F) -> Response
    where
        B: Fn(&[String]) -> Result<(String, Option<String>), Failure> + Send + 'static,
        F: FnOnce(&exec::Output, crate::session::Shell) -> Result<exec::Answer, Failure> + Send + 'static,
    {
        if let Err(failure) = build(&paths) {
            return Response::error(failure.code, failure.message);
        }
        let (Some(link), Some(runtime)) = (lock(&self.shared.links).get(&session).cloned(), &self.runtime) else {
            return Response::error("unknown_session", session.to_string());
        };

        let shared = Arc::clone(&self.shared);
        runtime.spawn(async move {
            let mapped = link.shell_paths(&paths).await;
            let result = match build(&mapped) {
                Ok((posix, windows)) => link
                    .exec_either(&posix, windows.as_deref())
                    .await
                    .and_then(exec::succeeded),
                Err(failure) => Err(failure),
            };
            // Lief der Befehl, steht die Shell fest; sonst wird `read` gar nicht gefragt – nicht neu proben.
            let shell = link.settled_shell().unwrap_or(crate::session::Shell::Neither);
            shared.exec_result(request, result.and_then(|output| read(&output, shell)));
        });
        Response::ok()
    }

    /// Wie [`Self::exec_checked`] – mit einem Skript für Windows-Server daneben ([`crate::winserver`]), das in
    /// derselben Form antwortet; ohne Skript (`None`) gilt dort `no_posix_shell`. `read` bekommt dazu, welche Shell
    /// geantwortet hat.
    fn exec_either<F>(&self, session: u64, request: u64, posix: String, windows: Option<String>, read: F) -> Response
    where
        F: FnOnce(&exec::Output, crate::session::Shell) -> Result<exec::Answer, Failure> + Send + 'static,
    {
        let (Some(link), Some(runtime)) = (lock(&self.shared.links).get(&session).cloned(), &self.runtime) else {
            return Response::error("unknown_session", session.to_string());
        };

        let shared = Arc::clone(&self.shared);
        runtime.spawn(async move {
            let result = link
                .exec_either(&posix, windows.as_deref())
                .await
                .and_then(exec::succeeded);
            let shell = link.settled_shell().unwrap_or(crate::session::Shell::Neither);
            shared.exec_result(request, result.and_then(|output| read(&output, shell)));
        });
        Response::ok()
    }

    /// Wie [`Self::exec`], für Befehle, deren Ausgabe selbst sagen kann, dass es nicht ging.
    fn exec_checked<F>(&self, session: u64, request: u64, command: String, read: F) -> Response
    where
        F: FnOnce(&exec::Output) -> Result<exec::Answer, Failure> + Send + 'static,
    {
        let (Some(link), Some(runtime)) = (lock(&self.shared.links).get(&session).cloned(), &self.runtime) else {
            return Response::error("unknown_session", session.to_string());
        };

        let shared = Arc::clone(&self.shared);
        runtime.spawn(async move {
            let result = link.exec(&exec::posix(&command)).await.and_then(exec::succeeded);
            shared.exec_result(request, result.and_then(|output| read(&output)));
        });
        Response::ok()
    }
}

// Kopiert die Paare der Reihe nach über SFTP ([`crate::sftp::copy`]); das erste, das scheitert, beendet es.
async fn copy_over_sftp(
    shared: &Shared,
    sftp: &russh_sftp::client::SftpSession,
    pairs: &[(String, String)],
) -> Result<exec::Answer, crate::sftp::Failure> {
    for (from, to) in pairs {
        crate::sftp::copy(sftp, from, to, &shared.limiter).await?;
    }

    Ok(exec::Answer::Nothing)
}

/// So lange wartet ein Kern, der endet, auf seine laufenden Transfers.
const TRANSFERS_END: Duration = Duration::from_secs(3);

/// So oft wird nachgesehen, ob sich `~/.ssh/config` geändert hat.
const CONFIG_CHECK: Duration = Duration::from_secs(3);

/// Verknüpfte Profile folgen ihrer Datei auch, während die App läuft. Nachgesehen wird nur, solange es ein
/// verknüpftes Profil gibt, und gelesen nur, wenn Größe oder Änderungszeit einer der Dateien anders ist – auch die
/// der eingebundenen (`Include`). Die Datei wird dabei wie immer nur gelesen.
async fn watch_config(shared: Weak<Shared>) {
    let mut files: Vec<PathBuf> = Vec::new();
    let mut seen: Option<sshconfig::Stamp> = None;
    loop {
        tokio::time::sleep(CONFIG_CHECK).await;
        let Some(shared) = shared.upgrade() else {
            return;
        };
        let Some(ssh_dir) = &shared.ssh_dir else {
            return;
        };

        if !lock(&shared.profiles)
            .list()
            .iter()
            .any(|profile| profile.ssh_alias.is_some())
        {
            seen = None;
            continue;
        }

        if files.is_empty() {
            files.push(ssh_dir.join("config"));
        }

        // Auch beim ersten Mal wird gelesen: Was sich seit dem Start oder dem Speichern geändert hat, ginge sonst
        // bis zur nächsten Änderung verloren. Stimmt schon alles, ändert sich dabei nichts.
        let before = sshconfig::stamp(&files);
        if seen.as_ref() == Some(&before) {
            continue;
        }

        // Der Stand gilt von vor dem Lesen – was währenddessen geschrieben wird, fällt beim nächsten Mal auf.
        // Bindet die Datei jetzt andere ein, wird noch einmal gelesen, bis die Liste steht.
        let config = sshconfig::Config::load(ssh_dir);
        if config.files == files {
            seen = Some(before);
        } else {
            files.clone_from(&config.files);
            seen = None;
        }

        let changed = sshconfig::refresh(&mut lock(&shared.profiles), &config);
        if matches!(changed, Ok(changed) if changed > 0) {
            shared.emit(Event::ProfilesChanged);
        }
    }
}

impl Drop for Core {
    fn drop(&mut self) {
        // Laufende Transfers enden wie bei einer abgerissenen Verbindung – und erfahren das, bevor sie fällt: Dann
        // halten sie fest, wie weit sie kamen, und der nächste Kern kann dort weitermachen.
        for transfer in lock(&self.shared.transfers).values() {
            transfer.halt(Halt::Lost);
        }

        // Sitzungen ohne Ereignis beenden: Wer den Kern fallen lässt, hört nicht mehr zu.
        for (_, entry) in lock(&self.shared.sessions).drain() {
            entry.task.abort();
        }

        lock(&self.shared.prompts).clear();
        lock(&self.shared.sftp).clear();
        lock(&self.shared.links).clear();

        // Ein Transfer trägt sich erst aus, wenn sein Stand gespeichert ist.
        let deadline = Instant::now() + TRANSFERS_END;
        while !lock(&self.shared.transfers).is_empty() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(10));
        }

        if let Some(runtime) = self.runtime.take() {
            runtime.shutdown_timeout(Duration::from_secs(2));
        }

        // Ein Mosh-Terminal bittet `mosh-server` beim Ende um seines; das soll noch hinausgehen, bevor der Prozess
        // endet – sonst liefe er am Server weiter. Höchstens zwei Sekunden: Ein Server, der nicht antwortet, hält
        // das Ende der App nicht auf.
        self.shared.mosh_threads.wait(Duration::from_secs(2));
    }
}
