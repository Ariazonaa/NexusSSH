//! Eine SSH-Sitzung von Anfang bis Ende: verbinden (auch über Sprung-Server), Host-Schlüssel prüfen, anmelden,
//! Shell, Datenaustausch.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use russh::client::{self, ChannelOpenHandle, Handle, KeyboardInteractiveAuthResponse, Msg};
use russh::keys::agent::client::AgentClient;
use russh::keys::{self, PrivateKey, PrivateKeyWithHashAlg, PublicKeyOrCertificate};
use russh::{Channel, ChannelMsg, ChannelOpenFailure, Disconnect, MethodKind, MethodSet, SshId};
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::sync::watch;
use tokio::time::{sleep, timeout};

use crate::core::Shared;
use crate::hostkeys::{Verdict, fingerprint};
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, SecretField, SecretKind, SessionState};
use crate::secrets::{passphrase_key, password_key};

use crate::dial::CONNECT_TIMEOUT;
const AGENT_TIMEOUT: Duration = Duration::from_secs(5);

/// So lange darf der Agent für die Liste seiner Schlüssel brauchen. Der Agent eines Passwort-Managers (unterstützt ist
/// Bitwarden) antwortet erst, wenn der Benutzer ihn entsperrt hat – mit der kurzen Frist gälte er bis dahin als
/// nicht vorhanden. Bitwarden wartet selbst eine Minute auf das Entsperren und lehnt dann ab (gemessen
/// 2026-10-12); die Frist hier liegt knapp darüber, damit seine Antwort entscheidet und nicht diese Uhr. Das
/// Signieren selbst hat keine Frist: Dort wartet der Manager auf die Freigabe.
const AGENT_LIST_TIMEOUT: Duration = Duration::from_secs(70);

/// Nennt der Agent erst nach dieser Zeit keinen Schlüssel, war er wohl gesperrt: Ein leerer antwortet sofort.
const AGENT_SILENCE: Duration = Duration::from_secs(3);

/// In diesem Abstand wird nachgesehen, ob der Agent eine Signatur abgelehnt hat ([`sign_in_with_agent`]).
const AGENT_REFUSAL_CHECK: Duration = Duration::from_millis(50);

// Server brechen nach wenigen Fehlversuchen ab (OpenSSH: MaxAuthTries 6). Jeder angebotene Schlüssel zählt,
// deshalb nicht den ganzen Agenten durchprobieren und beim Passwort nicht endlos fragen.
const MAX_AGENT_KEYS: usize = 3;
const MAX_SECRET_ATTEMPTS: u32 = 3;
const MAX_INTERACTIVE_ROUNDS: u32 = 10;

/// Länger wird ein Banner nicht gezeigt.
const MAX_BANNER: usize = 4000;

/// Endet die Shell so schnell, ohne dass etwas getippt wurde, gilt das Konto als eines ohne Shell.
const NO_SHELL_WITHIN: Duration = Duration::from_secs(10);

/// So oft sieht eine Sitzung ohne Shell nach, ob ihre Verbindung noch steht.
const ALIVE_CHECK: Duration = Duration::from_secs(2);

/// Was die App einer laufenden Sitzung schickt.
pub(crate) enum Command {
    Input(Vec<u8>),
    Resize(u32, u32),
    /// Auslastung des Servers melden oder damit aufhören.
    Monitor(bool),
    /// Ausgabe anhalten (`true`) oder weiterlaufen lassen: Das Terminal kommt mit dem Zeichnen nicht nach.
    Flow(bool),
    /// Die SSH-Verbindung unter einem Mosh-Terminal jetzt neu aufbauen (`session.relink`); andere Sitzungen
    /// überhören es.
    Relink,
}

/// Wie ein Server die Befehle des Kerns liest ([`Link::shell`]).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Shell {
    /// `sh -c '…'` wie gedacht (Linux, die BSDs, macOS – gleich, ob die Anmelde-Shell bash, fish oder csh ist).
    Posix,
    /// OpenSSH unter Windows: Befehle als kodiertes PowerShell-Skript ([`crate::winserver`]).
    Windows,
    /// Keines davon (ein Gerät mit eigener Befehlszeile, ein Konto ohne Befehle).
    Neither,
    /// Noch nicht zu sagen: Eine Probe blieb in ihrer Frist ohne Antwort. Wird nicht gemerkt – die nächste Frage
    /// probt neu ([`Link::shell`]).
    Unknown,
}

fn no_posix_shell() -> crate::sftp::Failure {
    crate::sftp::Failure {
        code: "no_posix_shell",
        message: "the server's login shell does not read commands like a POSIX shell".to_owned(),
    }
}

fn shell_unknown() -> crate::sftp::Failure {
    crate::sftp::Failure {
        code: "timeout",
        message: "the server did not say in time how it reads commands".to_owned(),
    }
}

/// So lange darf die Probe auf POSIX dauern. Ist die Anmelde-Shell PowerShell (OpenSSH unter Windows mit
/// `DefaultShell`), startet für jeden Befehl erst ein ganzes PowerShell, bevor es `sh` nicht findet – auf einer
/// ausgelasteten VM 15 bis 20 Sekunden.
const POSIX_PROBE_LIMIT: Duration = Duration::from_secs(20);

/// So lange darf die Probe auf PowerShell dauern. Am Windows-Gast mit Desktop galt eine ganze Verbindung als „keine
/// Shell“, als sie auf den frisch gestarteten, ausgelasteten Gast traf (die App startete daneben, Defender prüfte
/// sie) – mit 30 s Frist und einer stummen Probe, die als Nein galt (gesehen 2026-10-20). Im Leerlauf antworten
/// beide Proben in 3 bis 9 s, auch bei der ersten Anmeldung eines Kontos.
const WINDOWS_PROBE_LIMIT: Duration = Duration::from_secs(60);

/// So oft darf die Probe stumm bleiben, bevor ihr Schweigen als „keines davon“ gilt – ein Gerät, das auf einen
/// Befehl nie antwortet, kostete sonst jede Anfrage eine volle Frist.
const MAX_SHELL_SILENCES: u8 = 3;

/// Was eine Probe ergab: die erwartete Antwort, etwas anderes – oder in der Frist gar nichts.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Probe {
    Yes,
    No,
    Silent,
}

fn probe_outcome(result: &Result<crate::exec::Output, crate::sftp::Failure>, answer: &str) -> Probe {
    match result {
        Ok(output) if output.stdout.trim() == answer => Probe::Yes,
        Err(failure) if failure.code == "timeout" => Probe::Silent,
        _ => Probe::No,
    }
}

/// Eine stehende, angemeldete SSH-Verbindung zum Ziel samt den Verbindungen zu den Sprung-Servern davor.
///
/// Mehrere Sitzungen können sie sich teilen (geteiltes Terminal, „duplizieren“): Jede öffnet darauf ihre eigenen
/// Kanäle. Die Verbindung endet, wenn die letzte Sitzung sie fallen lässt.
pub(crate) struct Link {
    /// Das Profil, zu dem die Verbindung gehört – geteilt wird nur innerhalb desselben Profils.
    pub(crate) profile_id: String,
    /// Der Weg, auf dem die Verbindung entstand. Wird ein Profil geändert, während eine Sitzung dazu offen ist,
    /// zeigt die stehende Verbindung noch auf den alten Server – dann darf sie niemand mehr mitbenutzen.
    pub(crate) route: Route,
    // Option nur, damit Drop die Verbindung herausnehmen kann.
    handle: Option<Handle<Client>>,
    outer: Vec<Handle<Client>>,
    /// Das Geheimnis für X11-Forwarding auf dieser Verbindung; `None`, wenn das Profil es nicht erlaubt.
    x11: Option<crate::x11::Cookie>,
    /// Wie der Server die Befehle des Kerns liest – einmal je Verbindung festgestellt ([`Link::shell`]).
    shell: tokio::sync::OnceCell<Shell>,
    /// Wie oft die Probe dafür bisher stumm blieb ([`MAX_SHELL_SILENCES`]).
    shell_silences: std::sync::atomic::AtomicU8,
    /// Wie Pfade des SFTP-Servers in der Shell heißen, wo das anders ist (DSM) – einmal je Verbindung gefragt.
    shares: tokio::sync::OnceCell<Option<crate::exec::SharePaths>>,
    /// Was das Profil erlaubte, als die Verbindung entstand: Agent- und X11-Forwarding. Die Verbindung nimmt
    /// solche Kanäle des Servers an, solange sie steht – wer das im Profil inzwischen abgeschaltet hat, darf sie
    /// deshalb nicht mehr mitbenutzen.
    pub(crate) forwards: (bool, bool),
    /// Die Portweiterleitungen, die über diese Verbindung laufen. Sie enden mit ihr.
    pub(crate) tunnels: crate::tunnels::Tunnels,
    /// Die Adresse des Servers, an der die Verbindung ankam – nur ohne Sprung-Server. Mosh geht zu derselben.
    pub(crate) peer: Option<std::net::SocketAddr>,
    /// Wie viele Kanäle die Messung der Auslastung auf dieser Verbindung gerade offen hat ([`Self::measuring`]).
    measuring: watch::Sender<usize>,
    /// Wie viele Befehle gerade darauf warten, dass die Messung ihnen Platz macht ([`Self::making_room`]).
    held: watch::Sender<usize>,
}

/// Zählt, solange es lebt, einen Wert in einem `watch` hoch – für [`Link::measuring`] und [`Link::making_room`].
pub(crate) struct Counted<'a>(&'a watch::Sender<usize>);

impl<'a> Counted<'a> {
    fn new(count: &'a watch::Sender<usize>) -> Self {
        count.send_modify(|count| *count += 1);
        Self(count)
    }
}

impl Drop for Counted<'_> {
    fn drop(&mut self) {
        self.0.send_modify(|count| *count = count.saturating_sub(1));
    }
}

/// So lange wartet ein Befehl höchstens darauf, dass die Messung ihren Kanal schließt.
const MAKE_ROOM_WAIT: Duration = Duration::from_secs(5);

impl Link {
    /// Für die Messung der Auslastung, solange ihr Kanal offen ist: Ein Befehl, der keinen Kanal bekommt, weiß
    /// dann, dass sie ihm Platz machen kann ([`Self::making_room`]).
    pub(crate) fn measuring(&self) -> Counted<'_> {
        Counted::new(&self.measuring)
    }

    /// Wie viele Befehle gerade auf Platz warten: Ist es mehr als keiner, schließt die Messung ihren Kanal und
    /// beginnt erst wieder, wenn es keiner mehr ist.
    pub(crate) fn held(&self) -> watch::Receiver<usize> {
        self.held.subscribe()
    }

    /// Führt `run` aus. Lehnt der Server den Kanal dafür ab ([`crate::exec::NO_CHANNEL`]) und hat die Messung der
    /// Auslastung gerade einen offen, macht sie Platz: Sie schließt ihren Kanal, der Befehl läuft, danach misst
    /// sie weiter. An einem Server mit `MaxSessions 3` belegten Shell, Dateien und Messung alle drei, und die
    /// Belegung eines Ordners scheiterte mit „connection failed“ (gefunden 2026-10-24).
    async fn making_room<T, F, Fut>(&self, run: F) -> Result<T, crate::sftp::Failure>
    where
        F: Fn() -> Fut,
        Fut: std::future::Future<Output = Result<T, crate::sftp::Failure>>,
    {
        match run().await {
            Err(failure) if failure.code == crate::exec::NO_CHANNEL && *self.measuring.borrow() > 0 => {
                let _held = Counted::new(&self.held);
                let mut measuring = self.measuring.subscribe();
                let _ = timeout(MAKE_ROOM_WAIT, measuring.wait_for(|count| *count == 0)).await;
                run().await
            }
            other => other,
        }
    }

    /// Wie der Server Befehle liest: Liest die Anmelde-Shell `sh -c '…'` wie eine POSIX-Shell
    /// ([`crate::exec::posix_probe`])? Sonst: Führt er ein kodiertes PowerShell-Skript aus
    /// ([`crate::winserver::probe`] – OpenSSH unter Windows)? Festgestellt wird das einmal je Verbindung. **Bleibt
    /// eine Probe in ihrer Frist stumm, ist das kein Nein:** Dann gilt für diese Frage [`Shell::Unknown`], und die
    /// nächste probt neu – erst nach [`MAX_SHELL_SILENCES`] stummen Proben steht „keines davon“ fest. Vorher galt
    /// ein langsamer Server für die ganze Verbindung als einer ohne Befehle.
    pub(crate) async fn shell(&self) -> Shell {
        let settled = self
            .shell
            .get_or_try_init(|| async {
                // Ob noch einmal gefragt werden darf, wenn diese Probe stumm blieb.
                let again = || self.shell_silences.fetch_add(1, Ordering::Relaxed) + 1 < MAX_SHELL_SILENCES;
                let ask = |probe: String, answer: &'static str, limit: Duration| async move {
                    let asked = self
                        .making_room(|| crate::exec::run(self.handle(), &probe, limit))
                        .await;
                    probe_outcome(&asked, answer)
                };
                match ask(crate::exec::posix_probe(), crate::exec::POSIX_ANSWER, POSIX_PROBE_LIMIT).await {
                    Probe::Yes => return Ok(Shell::Posix),
                    Probe::Silent if again() => return Err(()),
                    _ => {}
                }
                match ask(crate::winserver::probe(), crate::winserver::ANSWER, WINDOWS_PROBE_LIMIT).await {
                    Probe::Yes => Ok(Shell::Windows),
                    Probe::Silent if again() => Err(()),
                    _ => Ok(Shell::Neither),
                }
            })
            .await;
        settled.copied().unwrap_or(Shell::Unknown)
    }

    /// Was [`Self::shell`] festgestellt hat, ohne zu proben – `None`, solange es nicht feststeht.
    pub(crate) fn settled_shell(&self) -> Option<Shell> {
        self.shell.get().copied()
    }

    /// Vor jedem POSIX-Befehl: Liest die Anmelde-Shell ihn so wie gedacht? Wenn nicht, läuft er nicht – in einem
    /// Namen könnte sonst einer stecken.
    async fn posix(&self) -> Result<(), crate::sftp::Failure> {
        match self.shell().await {
            Shell::Posix => Ok(()),
            Shell::Unknown => Err(shell_unknown()),
            Shell::Windows | Shell::Neither => Err(no_posix_shell()),
        }
    }

    /// Die Pfade so, wie die Shell des Servers sie nennt – Pfade des SFTP-Servers, die ein Befehl bekommen soll.
    /// Nur bei DSM ist das etwas anderes ([`crate::exec::SharePaths`]); sonst bleiben sie, wie sie sind. Gefragt
    /// wird nur eine POSIX-Shell – und erst, wenn feststeht, dass es eine ist (sonst merkte sich die Verbindung
    /// „keine Freigaben“, nur weil die Probe noch lief).
    pub(crate) async fn shell_paths(&self, paths: &[String]) -> Vec<String> {
        if self.shell().await != Shell::Posix {
            return paths.to_vec();
        }

        let shares = self
            .shares
            .get_or_init(|| async {
                let command = crate::exec::posix(crate::exec::SHARES);
                let asked = self
                    .making_room(|| crate::exec::run(self.handle(), &command, std::time::Duration::from_secs(10)))
                    .await
                    .ok()?;
                crate::exec::SharePaths::parse(&asked.stdout)
            })
            .await;
        paths
            .iter()
            .map(|path| {
                shares
                    .as_ref()
                    .map_or_else(|| path.clone(), |shares| shares.shell_path(path))
            })
            .collect()
    }

    /// Führt den Befehl aus, der zur Shell des Servers passt: den POSIX-Befehl (über `sh -c`) oder das kodierte
    /// PowerShell-Skript, wo es eines gibt. Ohne Skript für Windows gilt dort, was für jeden anderen Server ohne
    /// POSIX-Shell gilt (`no_posix_shell`).
    pub(crate) async fn exec_either(
        &self,
        posix: &str,
        windows: Option<&str>,
    ) -> Result<crate::exec::Output, crate::sftp::Failure> {
        match (self.shell().await, windows) {
            (Shell::Posix, _) => {
                let command = crate::exec::posix(posix);
                self.making_room(|| crate::exec::run(self.handle(), &command, crate::exec::LIMIT))
                    .await
            }
            (Shell::Windows, Some(windows)) => {
                self.making_room(|| crate::exec::run(self.handle(), windows, crate::exec::LIMIT))
                    .await
            }
            (Shell::Unknown, _) => Err(shell_unknown()),
            _ => Err(no_posix_shell()),
        }
    }

    /// Führt Text der App als Befehl aus (`exec.run`), in der Shell, die der Server hat. Die Frist gilt schon für die
    /// Frage nach der Shell: Sie kann bei einem stummen Server sonst länger dauern als der ganze Befehl.
    pub(crate) async fn run_command(
        &self,
        command: &str,
        seconds: u64,
    ) -> Result<crate::exec::RunOutput, crate::sftp::Failure> {
        crate::exec::check_command(command)?;
        let limit = Duration::from_secs(seconds.clamp(1, crate::exec::LIMIT.as_secs()));
        let line = match timeout(limit, self.shell()).await {
            Ok(Shell::Posix) => crate::exec::posix(command),
            Ok(Shell::Windows) => crate::winserver::user_command(command),
            Ok(Shell::Unknown) | Err(_) => return Err(shell_unknown()),
            Ok(Shell::Neither) => return Err(no_posix_shell()),
        };
        self.making_room(|| crate::exec::run_command(self.handle(), &line, limit))
            .await
    }

    /// Öffnet auf der stehenden Verbindung einen neuen SFTP-Kanal; `None`, wenn der Server keinen zulässt.
    pub(crate) async fn open_sftp(self: &Arc<Self>) -> Option<(Arc<russh_sftp::client::SftpSession>, String)> {
        match crate::sftp::open(self.handle()).await {
            Some(files) => Some(files),
            // Kein SFTP: Dann über Befehle, wenn der Server die erlaubt.
            None => crate::shellfs::open(Arc::clone(self)).await,
        }
    }

    /// Führt auf der stehenden Verbindung einen Befehl aus und wartet auf sein Ende.
    pub(crate) async fn exec(&self, command: &str) -> Result<crate::exec::Output, crate::sftp::Failure> {
        self.posix().await?;
        self.making_room(|| crate::exec::run(self.handle(), command, crate::exec::LIMIT))
            .await
    }

    /// Wie [`Self::exec`], mit einer eigenen Frist – für einen Versuch, auf den niemand lange warten soll.
    pub(crate) async fn exec_within(
        &self,
        command: &str,
        limit: std::time::Duration,
    ) -> Result<crate::exec::Output, crate::sftp::Failure> {
        self.posix().await?;
        self.making_room(|| crate::exec::run(self.handle(), command, limit))
            .await
    }

    /// Führt einen Befehl aus und liefert seine Ausgabe als rohe Bytes; `input` wird ihm vorher geschickt.
    pub(crate) async fn exec_raw(
        &self,
        command: &str,
        input: Option<&[u8]>,
        limit_output: usize,
    ) -> Result<crate::exec::RawOutput, crate::sftp::Failure> {
        self.posix().await?;
        self.making_room(|| crate::exec::run_raw(self.handle(), command, input, limit_output, crate::exec::LIMIT))
            .await
    }

    /// Öffnet einen SFTP-Kanal mit den Rechten von root (`sudo`), siehe [`crate::elevate`].
    pub(crate) async fn open_root_sftp(
        &self,
        password: Option<&str>,
    ) -> Result<Arc<russh_sftp::client::SftpSession>, crate::sftp::Failure> {
        crate::elevate::open(self.handle(), password).await
    }

    /// Führt einen Befehl aus und reicht seine Ausgabe laufend weiter, bis er endet oder `stop` kommt.
    pub(crate) async fn follow(
        &self,
        command: &str,
        stop: tokio::sync::oneshot::Receiver<()>,
        emit: impl FnMut(String),
    ) -> Result<(), crate::sftp::Failure> {
        self.posix().await?;
        crate::exec::follow(self.handle(), command, stop, emit).await
    }

    /// Öffnet einen Kanal, über den der Server zu `host:port` verbindet (`direct-tcpip`) – für die Tunnel.
    /// `from` nennt, wer auf dieser Seite gefragt hat.
    pub(crate) async fn open_direct(
        &self,
        host: String,
        port: u16,
        from: std::net::SocketAddr,
    ) -> Result<impl AsyncRead + AsyncWrite + Unpin + Send + 'static, russh::Error> {
        let channel = self
            .handle()
            .channel_open_direct_tcpip(host, u32::from(port), from.ip().to_string(), u32::from(from.port()))
            .await?;
        Ok(channel.into_stream())
    }

    /// Bittet den Server, an einem Port zu lauschen und Verbindungen hierher weiterzureichen (`tcpip-forward`).
    pub(crate) async fn forward_port(&self, address: &str, port: u32) -> Result<(), russh::Error> {
        self.handle().tcpip_forward(address, port).await.map(|_| ())
    }

    /// Nimmt die Bitte von [`Self::forward_port`] zurück.
    pub(crate) async fn cancel_forward(&self, address: &str, port: u32) -> Result<(), russh::Error> {
        self.handle().cancel_tcpip_forward(address, port).await
    }

    fn handle(&self) -> &Handle<Client> {
        // Bis zum Drop ist die Verbindung immer da.
        self.handle.as_ref().expect("link is alive")
    }

    /// Ob die Verbindung abgerissen ist.
    pub(crate) fn is_closed(&self) -> bool {
        self.handle().is_closed()
    }
}

impl Drop for Link {
    fn drop(&mut self) {
        // Ordentlich verabschieden: erst das Ziel, dann die Sprung-Server von innen nach außen. Drop kann nicht
        // warten, deshalb als eigene Aufgabe – läuft kein Runtime mehr (der Kern endet), fällt die Verbindung
        // mit ihren Handles ohnehin.
        let (handle, outer) = (self.handle.take(), std::mem::take(&mut self.outer));
        if let Ok(runtime) = tokio::runtime::Handle::try_current() {
            runtime.spawn(async move {
                if let Some(handle) = handle {
                    let _ = handle.disconnect(Disconnect::ByApplication, "", "en").await;
                }

                for jump in outer.iter().rev() {
                    let _ = jump.disconnect(Disconnect::ByApplication, "", "en").await;
                }
            });
        }
    }
}

/// Der Weg zu einem Server: je Station Host, Port und Benutzer, die Sprung-Server zuerst.
pub(crate) type Route = Vec<(String, u16, String)>;

pub(crate) fn route(chain: &[Profile]) -> Route {
    chain
        .iter()
        .map(|server| (server.host.clone(), server.port, server.user.clone()))
        .collect()
}

struct Failure {
    reason: CloseReason,
    message: String,
    /// Woran ein gescheiterter Aufbau lag, soweit bekannt (siehe [`crate::dial::Trouble`]).
    trouble: Option<&'static str>,
}

impl Failure {
    fn new(reason: CloseReason, message: impl Into<String>) -> Self {
        Self {
            reason,
            message: message.into(),
            trouble: None,
        }
    }

    /// Hält fest, woran der Aufbau lag.
    fn because(mut self, trouble: &'static str) -> Self {
        self.trouble = Some(trouble);
        self
    }

    /// Nennt in der Meldung den Sprung-Server, an dem es lag.
    fn at(mut self, hop: Option<String>) -> Self {
        if let Some(hop) = hop {
            self.message = format!("jump host {hop}: {}", self.message);
        }

        self
    }
}

// Fehler von russh nach der Anmeldung bedeuten praktisch immer: Die Verbindung ist weg.
impl From<russh::Error> for Failure {
    fn from(error: russh::Error) -> Self {
        Self::new(CloseReason::ConnectionLost, error.to_string())
    }
}

/// Läuft, bis die Sitzung endet, und meldet das Ende. `chain` ist der Weg zum Server: erst die Sprung-Server,
/// zuletzt das Ziel. `share` nennt eine andere Sitzung, deren stehende Verbindung mitbenutzt werden soll.
pub(crate) async fn run(
    shared: Arc<Shared>,
    session: u64,
    chain: Vec<Profile>,
    share: Option<u64>,
    size: (u32, u32),
    predict: mosh_client::PredictionMode,
    commands: UnboundedReceiver<Command>,
) {
    match drive(&shared, session, &chain, share, size, predict, commands).await {
        Ok(exit_status) => shared.finish(session, CloseReason::Remote, None, exit_status, None),
        // Ein Ende, das der Benutzer wollte (ein Mosh-Terminal, das ihm zuvorkam), braucht keine Erklärung.
        Err(failure) if failure.reason == CloseReason::User => {
            shared.finish(session, CloseReason::User, None, None, None)
        }
        Err(failure) => shared.finish(session, failure.reason, Some(failure.message), None, failure.trouble),
    }
}

async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    chain: &[Profile],
    share: Option<u64>,
    size: (u32, u32),
    predict: mosh_client::PredictionMode,
    mut commands: UnboundedReceiver<Command>,
) -> Result<Option<u32>, Failure> {
    let Some(profile) = chain.last() else {
        return Err(Failure::new(CloseReason::UnknownProfile, "no profile"));
    };

    // Telnet ist eine eigene, viel schmalere Sitzung: keine Anmeldung, kein zweiter Kanal, nichts zu teilen.
    if profile.is_telnet() {
        return crate::telnet::drive(shared, session, profile, size, commands)
            .await
            .map(|()| None)
            .map_err(|(reason, message)| Failure::new(reason, message));
    }

    // FTP und FTPS: nur Dateien, über eine ganz andere Verbindung.
    if profile.is_ftp() {
        return crate::ftp::drive(shared, session, profile, commands)
            .await
            .map(|()| None)
            .map_err(|(reason, message)| Failure::new(reason, message));
    }

    // Ebenso die serielle Schnittstelle: gar keine Verbindung im Netz. Sie endet nie von selbst.
    if profile.is_serial() {
        return match crate::serial::drive(shared, session, profile, commands).await {
            Ok(()) => Ok(None),
            Err((reason, message)) => Err(Failure::new(reason, message)),
        };
    }

    // Und das lokale Terminal: eine Shell dieses Rechners, die mit ihrem Rückgabewert endet.
    if profile.is_local() {
        return crate::local::session::drive(shared, session, profile, size, commands)
            .await
            .map_err(|(reason, message)| Failure::new(reason, message));
    }

    // UDP geht nicht durch einen Sprung-Server; das Profil verbietet das schon, hier nur für den Fall, dass eine
    // Datei von Hand bearbeitet wurde.
    if profile.mosh && chain.len() > 1 {
        return Err(Failure::new(
            CloseReason::ConnectFailed,
            "mosh does not go through a jump host",
        ));
    }

    // Eine stehende Verbindung zum selben Profil mitbenutzen – ohne neue Anmeldung. Sie muss auf demselben Weg
    // zum selben Server führen wie das Profil heute. Lässt der Server darauf keinen weiteren Kanal zu (OpenSSH:
    // MaxSessions), gibt es doch eine eigene Verbindung.
    let mut link = None;
    let mut channel = None;
    let wanted = route(chain);
    if let Some(existing) = share
        .and_then(|other| shared.link(other, &profile.id, &wanted))
        .filter(|existing| existing.forwards == (profile.forward_agent, profile.forward_x11))
    {
        shared.state(session, SessionState::Connecting, None);
        if profile.mosh {
            // Ein Mosh-Terminal braucht auf der Verbindung nur einen Befehl, keinen Kanal für die Shell.
            link = Some(existing);
        } else if let Ok(Ok(opened)) = timeout(CONNECT_TIMEOUT, existing.handle().channel_open_session()).await {
            (link, channel) = (Some(existing), Some(opened));
        }
    }

    let link = match link {
        Some(link) => link,
        None => Arc::new(connect(shared, session, chain).await?),
    };

    // Das Terminal über Mosh. Geht das nicht – kein `mosh-server`, kein UDP binnen 15 s –, bleibt es beim Kanal
    // der SSH-Verbindung, und die App erfährt warum.
    if profile.mosh {
        let started = match crate::mosh::start(shared, session, &link, profile).await {
            Ok(bootstrap) => crate::mosh::Terminal::attach(&shared.mosh_threads, bootstrap, size, predict).await,
            Err(failed) => Err(failed),
        };
        match started {
            Ok(terminal) => {
                let files = open_files(shared, &link, profile).await;
                shared.connected(session, &profile.id, Arc::clone(&link), files);
                start_tunnels(&link, profile);
                return crate::mosh::drive(shared, session, chain, link, terminal, commands)
                    .await
                    .map_err(|(reason, message)| Failure::new(reason, message));
            }
            Err(failed) => {
                shared.trace(session, || format!("no mosh: {failed}; the terminal runs over SSH"));
                shared.emit(Event::Warning {
                    code: failed.code(),
                    message: format!("{}: {failed}; the terminal runs over SSH", profile.name),
                });
            }
        }
    }

    let handle = link.handle();
    let channel = match channel {
        Some(channel) => channel,
        None => handle.channel_open_session().await?,
    };

    channel
        .request_pty(
            false,
            profile.term.as_deref().unwrap_or(crate::profiles::DEFAULT_TERM),
            size.0,
            size.1,
            0,
            0,
            &[],
        )
        .await?;

    // Umgebungsvariablen des Profils, vor der Shell und ohne Rückantwort (wie beim Agenten): Der Server nimmt nur
    // an, was sein `AcceptEnv` erlaubt, und sagt zu allem anderen nichts. Im Protokoll stehen nur die Namen –
    // ein Wert kann etwas sein, das niemand lesen soll.
    for variable in &profile.env {
        channel
            .set_env(false, variable.name.as_str(), variable.value.as_str())
            .await?;
    }
    if !profile.env.is_empty() {
        shared.trace(session, || {
            let names: Vec<&str> = profile.env.iter().map(|variable| variable.name.as_str()).collect();
            format!("sent environment variables: {}", names.join(", "))
        });
    }

    // Agent Forwarding nur, wenn das Profil es will und ein Agent da ist. Ohne Rückantwort, damit eine Ablehnung
    // des Servers nicht mit der Antwort auf die Shell-Anfrage verwechselt wird.
    if profile.forward_agent {
        if connect_agent(shared.agent.as_deref()).await.is_some() {
            channel.agent_forward(false).await?;
        } else {
            shared.emit(Event::Warning {
                code: "agent_unavailable",
                message: format!("{}: agent forwarding is on, but no SSH agent is running", profile.name),
            });
        }
    }

    // X11-Forwarding ebenso nur mit Erlaubnis – und nur, wenn hier ein X-Server läuft: Sonst setzte der Server
    // `DISPLAY`, und jedes grafische Programm scheiterte erst beim Verbinden.
    if let Some(cookie) = link.x11.filter(|_| profile.forward_x11) {
        let server = shared.x_server();
        if crate::x11::available(&server).await {
            channel
                .request_x11(false, false, crate::x11::PROTOCOL, crate::x11::hex(&cookie), 0)
                .await?;
        } else {
            shared.emit(Event::Warning {
                code: "x11_unavailable",
                message: format!(
                    "{}: X11 forwarding is on, but no X server is listening at {server}",
                    profile.name
                ),
            });
        }
    }

    channel.request_shell(true).await?;
    let shell_requested = Instant::now();

    let files = open_files(shared, &link, profile).await;
    let has_files = files.is_some();
    shared.connected(session, &profile.id, Arc::clone(&link), files);
    start_tunnels(&link, profile);

    // Ob der Benutzer schon etwas getippt hat – und ob die Shell inzwischen weg ist (siehe unten).
    let typed = AtomicBool::new(false);
    let shell_gone = AtomicBool::new(false);

    // Die Auslastung läuft über einen weiteren Kanal, und nur, solange die App sie zeigt.
    let (watching, watched) = watch::channel(false);
    let monitor = crate::monitor::run(shared, session, handle, &link, watched);

    // Kommt das Terminal mit dem Zeichnen nicht nach, hält die App die Ausgabe an.
    let (flow, mut paused) = watch::channel(false);

    // Getrennte Hälften: Stockt das Senden (Fenster des Servers voll), wird trotzdem weiter gelesen.
    let (mut reader, writer) = channel.split();
    let read = async {
        let mut exit_status = None;
        let mut closed_cleanly = false;
        let mut shell_started = false;
        loop {
            // Angehalten wird nichts mehr abgeholt; der Rückstau bremst den Server. Endet die Sitzung
            // währenddessen, endet sie über `finish` – hier muss niemand darauf warten.
            if *paused.borrow_and_update() && paused.wait_for(|paused| !*paused).await.is_err() {
                std::future::pending::<()>().await;
            }

            match reader.wait().await {
                Some(ChannelMsg::Data { data }) | Some(ChannelMsg::ExtendedData { data, .. }) => {
                    shell_started = true;
                    shared.sink.output(session, &data);
                }
                Some(ChannelMsg::Success) => shell_started = true,
                Some(ChannelMsg::Failure) if !shell_started => return Ok(ShellEnd::Refused),
                Some(ChannelMsg::ExitStatus { exit_status: status }) => exit_status = Some(status),
                Some(ChannelMsg::Eof) | Some(ChannelMsg::Close) => closed_cleanly = true,
                Some(_) => {}
                None if closed_cleanly || exit_status.is_some() => return Ok(ShellEnd::Exited(exit_status)),
                None => {
                    return Err(Failure::new(CloseReason::ConnectionLost, "the connection was lost"));
                }
            }
        }
    };
    let write = async {
        while let Some(command) = commands.recv().await {
            match command {
                // Ohne Shell gibt es niemanden, der Eingaben oder eine neue Größe haben will.
                Command::Input(_) | Command::Resize(..) if shell_gone.load(Ordering::Relaxed) => {}
                Command::Input(bytes) => {
                    typed.store(true, Ordering::Relaxed);
                    writer.data(&bytes[..]).await?;
                }
                Command::Resize(columns, rows) => writer.window_change(columns, rows, 0, 0).await?,
                Command::Monitor(enabled) => {
                    let _ = watching.send(enabled);
                }
                Command::Flow(paused) => {
                    let _ = flow.send(paused);
                }
                // Nur ein Mosh-Terminal baut seine Verbindung neu auf.
                Command::Relink => {}
            }
        }

        // Der Sender lebt, solange die Sitzung eingetragen ist; hierher kommt man praktisch nicht.
        std::future::pending::<Result<(), Failure>>().await
    };

    // Die Verbindung selbst endet, wenn die letzte Sitzung ihren `Link` fallen lässt.
    tokio::pin!(read, write, monitor);
    let end = tokio::select! {
        outcome = &mut read => outcome?,
        outcome = &mut write => return outcome.map(|()| None),
        never = &mut monitor => match never {},
    };

    // Ein Konto ohne Shell – nur SFTP erlaubt (`ForceCommand internal-sftp`), Shell `nologin`: Der Server lehnt die
    // Shell ab oder beendet sie sofort. Stehen die Dateien, bleibt die Sitzung dafür offen. Eine Shell, in der
    // schon getippt wurde oder die eine Weile lief, ist dagegen einfach zu Ende (`exit`, Zeitablauf).
    let no_shell = match end {
        ShellEnd::Refused => true,
        ShellEnd::Exited(_) => !typed.load(Ordering::Relaxed) && shell_requested.elapsed() < NO_SHELL_WITHIN,
    };
    if !(has_files && no_shell) {
        return match end {
            ShellEnd::Refused => Err(Failure::new(
                CloseReason::ConnectionLost,
                "the server refused to start a shell",
            )),
            ShellEnd::Exited(exit_status) => Ok(exit_status),
        };
    }

    shell_gone.store(true, Ordering::Relaxed);
    shared.emit(Event::SessionFilesOnly {
        session,
        exit_status: match end {
            ShellEnd::Refused => None,
            ShellEnd::Exited(exit_status) => exit_status,
        },
    });

    // Ohne Kanal der Shell meldet niemand mehr das Ende der Verbindung – also nachsehen.
    loop {
        tokio::select! {
            outcome = &mut write => return outcome.map(|()| None),
            never = &mut monitor => match never {},
            () = sleep(ALIVE_CHECK) => {
                if handle.is_closed() {
                    return Err(Failure::new(CloseReason::ConnectionLost, "the connection was lost"));
                }
            }
        }
    }
}

/// Öffnet den Dateibrowser über einen weiteren Kanal der Verbindung. Lehnt der Server SFTP ab, steht die Sitzung
/// trotzdem – nur ohne Dateien, oder über Befehle (viele Geräte, BusyBox ohne `sftp-server`): langsamer, aber es
/// geht.
pub(crate) async fn open_files(
    shared: &Shared,
    link: &Arc<Link>,
    profile: &Profile,
) -> Option<(Arc<russh_sftp::client::SftpSession>, String)> {
    if let Some(files) = crate::sftp::open(link.handle()).await {
        return Some(files);
    }

    let files = crate::shellfs::open(Arc::clone(link)).await;
    if files.is_some() {
        shared.emit(Event::Warning {
            code: "files_over_shell",
            message: format!(
                "{}: the server offers no SFTP; files are handled through shell commands, which is slower",
                profile.name
            ),
        });
    }

    files
}

/// Startet die Tunnel des Profils, die mit der Verbindung stehen sollen. Nebenher, damit ein Server, der sich mit
/// der Antwort Zeit lässt, die Sitzung nicht aufhält; was schon läuft (geteilte Verbindung), bleibt, wie es ist.
pub(crate) fn start_tunnels(link: &Arc<Link>, profile: &Profile) {
    let wanted: Vec<_> = profile.tunnels.iter().filter(|tunnel| tunnel.auto).cloned().collect();
    if !wanted.is_empty() {
        let link = Arc::clone(link);
        tokio::spawn(async move {
            for tunnel in &wanted {
                link.tunnels.start(&link, tunnel).await;
            }
        });
    }
}

/// Die Auslastung über diese Verbindung, als eigene Aufgabe – für ein Mosh-Terminal, dessen Verbindung wechseln
/// kann. Wer den Griff hat, beendet sie (`abort`).
pub(crate) fn watch_load(
    shared: &Arc<Shared>,
    session: u64,
    link: &Arc<Link>,
    watched: watch::Receiver<bool>,
) -> tokio::task::JoinHandle<()> {
    let (shared, link) = (Arc::clone(shared), Arc::clone(link));
    tokio::spawn(async move { match crate::monitor::run(&shared, session, link.handle(), &link, watched).await {} })
}

/// Baut die Verbindung einer Sitzung neu auf, deren Terminal ohne sie weiterläuft (Mosh): wie beim ersten Mal,
/// samt Rückfragen, aber ohne Zustandsmeldungen – die Sitzung bleibt für die App verbunden.
pub(crate) async fn relink(
    shared: &Arc<Shared>,
    session: u64,
    chain: &[Profile],
) -> Result<Arc<Link>, (CloseReason, String)> {
    let _quiet = shared.quiet(session);
    connect(shared, session, chain)
        .await
        .map(Arc::new)
        .map_err(|failure| (failure.reason, failure.message))
}

/// Wie die Shell einer Sitzung zu Ende ging.
enum ShellEnd {
    /// Der Server hat die Bitte um eine Shell abgelehnt.
    Refused,
    /// Die Shell ist zu Ende, mit dem Rückgabewert, falls der Server ihn gemeldet hat.
    Exited(Option<u32>),
}

/// Baut die Verbindung zum Ziel auf: TCP zum ersten Server, dann Server für Server Schlüssel prüfen und anmelden.
async fn connect(shared: &Arc<Shared>, session: u64, chain: &[Profile]) -> Result<Link, Failure> {
    let Some((profile, jumps)) = chain.split_last() else {
        return Err(Failure::new(CloseReason::UnknownProfile, "no profile"));
    };
    let first = jumps.first().unwrap_or(profile);

    // Bei einer Verbindung über Sprung-Server nennt jede Meldung den Server, um den es gerade geht.
    let hop = |server: &Profile| (!jumps.is_empty()).then(|| label(server));
    shared.state(session, SessionState::Connecting, hop(first));

    // Welche Adresse und wie lange gewartet wird, sagt das Profil des ersten Servers auf dem Weg.
    let stream = match crate::dial::tcp(first).await {
        Ok(stream) => stream,
        Err(failed) => {
            return Err(Failure::new(CloseReason::ConnectFailed, failed.message)
                .because(failed.trouble.name())
                .at(hop(first)));
        }
    };
    // Tastendrücke sofort senden, nicht auf weitere Daten warten.
    let _ = stream.set_nodelay(true);
    // Ohne Sprung-Server ist das der Server selbst – Mosh geht später zu derselben Adresse.
    let peer = stream.peer_addr().ok().filter(|_| jumps.is_empty());
    shared.trace(session, || match stream.peer_addr() {
        Ok(address) => format!("connected to {} at {address}", label(first)),
        Err(_) => format!("connected to {}", label(first)),
    });

    // Die Sprung-Server der Reihe nach: Jeder öffnet einen Kanal zum nächsten, und darüber läuft die nächste
    // SSH-Verbindung – Ende zu Ende verschlüsselt, der Sprung-Server sieht nur den Datenstrom. Ihre Verbindungen
    // bleiben in `outer` am Leben, solange die Sitzung steht.
    let mut outer = Vec::new();
    // Das Geheimnis für X11 entsteht je Verbindung neu; `establish` gibt es nur dem Ziel, nie einem Sprung-Server.
    let x11 = profile.forward_x11.then(crate::x11::new_cookie);
    // Ebenso die Liste der Ports, um deren Weiterleitung der Server gebeten wird: Nur dafür darf er Kanäle öffnen.
    let remotes = crate::tunnels::Remotes::default();
    let mut handle = establish(
        shared,
        session,
        first,
        hop(first),
        jumps.is_empty(),
        (x11, &remotes),
        stream,
    )
    .await
    .map_err(|failure| failure.at(hop(first)))?;
    for (index, server) in chain.iter().enumerate().skip(1) {
        shared.state(session, SessionState::Connecting, hop(server));
        let via = &chain[index - 1];
        shared.trace(session, || {
            format!(
                "opening a tunnel through {} to {}:{}",
                via.name, server.host, server.port
            )
        });
        let tunnel = match timeout(
            CONNECT_TIMEOUT,
            handle.channel_open_direct_tcpip(server.host.clone(), u32::from(server.port), "127.0.0.1", 0),
        )
        .await
        {
            Ok(Ok(tunnel)) => tunnel,
            Ok(Err(error)) => {
                return Err(Failure::new(
                    CloseReason::ConnectFailed,
                    format!("{} could not reach {}: {error}", via.name, server.host),
                ));
            }
            Err(_) => {
                return Err(Failure::new(
                    CloseReason::ConnectFailed,
                    format!("{} did not reach {} in time", via.name, server.host),
                ));
            }
        };

        let inner = establish(
            shared,
            session,
            server,
            hop(server),
            index + 1 == chain.len(),
            (x11, &remotes),
            tunnel.into_stream(),
        )
        .await
        .map_err(|failure| failure.at(hop(server).filter(|_| index + 1 < chain.len())))?;
        outer.push(std::mem::replace(&mut handle, inner));
    }

    Ok(Link {
        profile_id: profile.id.clone(),
        route: route(chain),
        handle: Some(handle),
        outer,
        x11,
        shell: tokio::sync::OnceCell::new(),
        shell_silences: std::sync::atomic::AtomicU8::new(0),
        shares: tokio::sync::OnceCell::new(),
        forwards: (profile.forward_agent, profile.forward_x11),
        tunnels: crate::tunnels::Tunnels::new(shared, &profile.id, remotes),
        peer,
        measuring: watch::Sender::new(0),
        held: watch::Sender::new(0),
    })
}

/// `user@host`, bei abweichendem Port mit `:port` – so, wie die App einen Server nennt.
fn label(profile: &Profile) -> String {
    if profile.port == 22 {
        format!("{}@{}", profile.user, profile.host)
    } else {
        format!("{}@{}:{}", profile.user, profile.host, profile.port)
    }
}

/// Der Server für Rückfragen: der Host, bei abweichendem Port mit `:port`.
fn server_name(profile: &Profile) -> String {
    match profile.port {
        22 => profile.host.clone(),
        port if profile.host.contains(':') => format!("[{}]:{port}", profile.host),
        port => format!("{}:{port}", profile.host),
    }
}

/// Was dieser Verbindung an Verfahren angeboten wird. Die Vorgabe von russh ist die sichere Auswahl; das Profil
/// kann zweierlei daran ändern:
///
/// - **Komprimierung** stellt `zlib` nach vorn (sonst steht „keine“ vorn) – für langsame Leitungen.
/// - **Ältere Verfahren** hängt hinten an, was alte Geräte noch sprechen und heute als schwach gilt (SHA-1 im
///   Schlüsselaustausch und im MAC, CBC, 3DES, die NIST-Kurven). Hinten, damit ein Server, der Besseres kann, auch
///   Besseres bekommt. Nie von selbst: Der Schalter steht im Profil und warnt dort.
pub(crate) fn preferred(profile: &Profile) -> russh::Preferred {
    use russh::{cipher, compression, kex, mac};

    let mut preferred = russh::Preferred::DEFAULT;
    if profile.compression {
        preferred.compression = vec![compression::ZLIB_LEGACY, compression::ZLIB, compression::NONE].into();
    }

    if profile.legacy_algorithms {
        // Die Erweiterungen am Ende der Liste sind keine Verfahren, sondern Merker – sie bleiben hinten.
        let is_marker = |name: &kex::Name| {
            [
                kex::EXTENSION_SUPPORT_AS_CLIENT,
                kex::EXTENSION_SUPPORT_AS_SERVER,
                kex::EXTENSION_OPENSSH_STRICT_KEX_AS_CLIENT,
                kex::EXTENSION_OPENSSH_STRICT_KEX_AS_SERVER,
            ]
            .contains(name)
        };
        let (markers, mut exchange): (Vec<kex::Name>, Vec<kex::Name>) =
            preferred.kex.iter().copied().partition(is_marker);
        exchange.extend([
            kex::ECDH_SHA2_NISTP256,
            kex::ECDH_SHA2_NISTP384,
            kex::ECDH_SHA2_NISTP521,
            kex::DH_GEX_SHA1,
            kex::DH_G14_SHA1,
            kex::DH_G1_SHA1,
        ]);
        exchange.extend(markers);
        preferred.kex = exchange.into();

        let mut ciphers = preferred.cipher.to_vec();
        ciphers.extend([
            cipher::AES_128_GCM,
            cipher::AES_256_CBC,
            cipher::AES_192_CBC,
            cipher::AES_128_CBC,
        ]);
        preferred.cipher = ciphers.into();

        let mut macs = preferred.mac.to_vec();
        macs.extend([mac::HMAC_SHA1_ETM, mac::HMAC_SHA1]);
        preferred.mac = macs.into();
    }

    preferred
}

/// SSH-Verbindung über einen stehenden Datenstrom: Schlüsselaustausch, Host-Schlüssel prüfen, anmelden.
async fn establish<R>(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    hop: Option<String>,
    is_target: bool,
    // Was nur das Ziel bekommt: das Geheimnis für X11 und die Liste der weitergeleiteten Ports.
    granted: (Option<crate::x11::Cookie>, &crate::tunnels::Remotes),
    stream: R,
) -> Result<Handle<Client>, Failure>
where
    R: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let (x11, remotes) = granted;
    // Um ein Host-Zertifikat bittet der Kern nur, wenn für diesen Server eine Zertifizierungsstelle bekannt ist
    // (`@cert-authority` in known_hosts) – nur dann kann er es auch prüfen. Sonst bleibt alles beim bloßen Schlüssel.
    let mut preferred = preferred(profile);

    // Zuerst um die Art von Schlüssel bitten, die für diesen Server schon hinterlegt ist: Nur dann lässt sich
    // sagen, ob er sich geändert hat (`HostKeys::known_algorithms`).
    let known = shared.host_keys.known_algorithms(&profile.host, profile.port);
    if !known.is_empty() {
        let mut keys = preferred.key.to_vec();
        keys.sort_by_key(|algorithm| !known.contains(algorithm));
        preferred.key = keys.into();
    }

    if !shared.host_keys.authorities(&profile.host, profile.port).is_empty() {
        preferred.host_key_certificates = preferred.key.clone();
    }

    let (keepalive_interval, keepalive_max) = shared.keepalive(profile);
    let config = Arc::new(client::Config {
        client_id: SshId::Standard(concat!("SSH-2.0-NexusSSH_", env!("CARGO_PKG_VERSION")).into()),
        // Hält die Verbindung durch NAT und Firewalls offen und bemerkt einen stillen Abriss.
        keepalive_interval,
        keepalive_max,
        preferred,
        ..Default::default()
    });

    shared.trace(session, || match keepalive_interval {
        Some(every) => format!(
            "{}: keepalive every {} s, the connection counts as lost after {keepalive_max} unanswered",
            server_name(profile),
            every.as_secs(),
        ),
        None => format!("{}: no keepalive", server_name(profile)),
    });
    shared.trace(session, || {
        format!(
            "{}: asking for host keys of kind {}{}",
            server_name(profile),
            known
                .iter()
                .map(|algorithm| algorithm.as_str().to_owned())
                .collect::<Vec<_>>()
                .join(", "),
            if known.is_empty() {
                "any (none recorded yet)"
            } else {
                " first (recorded for this server)"
            },
        )
    });
    let rejected = Arc::new(AtomicBool::new(false));
    let handler = Client {
        shared: Arc::clone(shared),
        session,
        host: profile.host.clone(),
        port: profile.port,
        // Den Agenten bekommt nur das Ziel zu sehen, und nur, wenn sein Profil es erlaubt – nie ein Sprung-Server.
        forward_agent: profile.forward_agent && is_target,
        // Für X11 gilt dasselbe.
        x11: x11.filter(|_| is_target),
        // Weitergeleitete Ports kennt nur das Ziel; ein Sprung-Server darf hier keine Verbindungen öffnen.
        remotes: is_target.then(|| Arc::clone(remotes)),
        rejected: Arc::clone(&rejected),
    };
    let mut handle = client::connect_stream(config, stream, handler).await.map_err(|error| {
        if rejected.load(Ordering::Relaxed) {
            Failure::new(CloseReason::HostKeyRejected, "the host key was not trusted")
        } else {
            // Kein gemeinsames Verfahren: Das ist der Fall, für den es „ältere Verfahren erlauben“ im Profil gibt.
            let trouble = match error {
                russh::Error::NoCommonAlgo { .. } => "algorithms",
                _ => "other",
            };
            Failure::new(CloseReason::ConnectFailed, error.to_string()).because(trouble)
        }
    })?;

    shared.state(session, SessionState::Authenticating, hop);
    let signed_in = authenticate(shared, session, profile, &mut handle).await;
    shared.trace(session, || match &signed_in {
        Ok(()) => format!("{}: signed in as {}", server_name(profile), profile.user),
        Err(failure) => format!("{}: sign-in failed: {}", server_name(profile), failure.message),
    });
    signed_in?;
    Ok(handle)
}

struct Client {
    shared: Arc<Shared>,
    session: u64,
    host: String,
    port: u16,
    forward_agent: bool,
    /// Mit einem Geheimnis darf der Server X11-Verbindungen öffnen; ohne wird jede abgelehnt.
    x11: Option<crate::x11::Cookie>,
    /// Die Ports, um deren Weiterleitung der Server gebeten wurde; `None` bei einem Sprung-Server.
    remotes: Option<crate::tunnels::Remotes>,
    rejected: Arc<AtomicBool>,
}

impl client::Handler for Client {
    type Error = russh::Error;

    async fn kex_done(
        &mut self,
        _shared_secret: Option<&[u8]>,
        names: &russh::Names,
        session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        // Nur die Namen der Verfahren und wie sich der Server nennt – das gemeinsame Geheimnis bleibt, wo es ist.
        self.shared.trace(self.session, || {
            let software = String::from_utf8_lossy(session.remote_sshid());
            format!(
                "{}: server is {}; key exchange {}, host key {}, cipher {}, mac {} / {}, compression {:?} / {:?}",
                self.host,
                printable(software.trim()),
                names.kex.as_ref(),
                names.key.as_str(),
                names.cipher.as_ref(),
                names.client_mac.as_ref(),
                names.server_mac.as_ref(),
                names.client_compression,
                names.server_compression,
            )
        });
        Ok(())
    }

    async fn check_server_key(&mut self, server_key: &PublicKeyOrCertificate) -> Result<bool, Self::Error> {
        // Ein Zertifikat, das eine bekannte Stelle für diesen Server unterschrieben hat, genügt: keine Rückfrage,
        // nichts wird gemerkt – das Vertrauen steckt in der Stelle. Hält es nicht (abgelaufen, anderer Name, fremde
        // Stelle), gilt der Schlüssel darin wie jeder bloße Schlüssel: bekannt oder nicht, mit Rückfrage.
        // Ein widerrufener Schlüssel bleibt widerrufen, auch mit Zertifikat: Dann entscheidet unten `Verdict::Revoked`.
        let key = server_key.public_key();
        let revoked = self.shared.host_keys.check(&self.host, self.port, &key) == Verdict::Revoked;
        if let PublicKeyOrCertificate::Certificate(certificate) = server_key
            && !revoked
        {
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |since| since.as_secs());
            match self
                .shared
                .host_keys
                .check_certificate(&self.host, self.port, certificate, now)
            {
                Ok(()) => return Ok(true),
                Err(reason) => self.shared.emit(Event::Warning {
                    code: "host_certificate_invalid",
                    message: format!("{}: {reason}", self.host),
                }),
            }
        }

        let (changed, file, line, previous, foreign) = match self.shared.host_keys.check(&self.host, self.port, &key) {
            Verdict::Trusted => return Ok(true),
            // Widerrufen: keine Rückfrage, keine Verbindung.
            Verdict::Revoked => {
                self.shared.emit(Event::Warning {
                    code: "host_key_revoked",
                    message: format!("{}: the host key is revoked in known_hosts", self.host),
                });
                self.rejected.store(true, Ordering::Relaxed);
                return Ok(false);
            }
            Verdict::Unknown => (false, None, None, None, None),
            Verdict::Changed {
                file,
                line,
                previous,
                own,
            } => (
                true,
                Some(file.display().to_string()),
                Some(line),
                Some(previous),
                Some(!own),
            ),
        };

        let (host, port, session) = (self.host.clone(), self.port, self.session);
        let (algorithm, print) = (key.algorithm().to_string(), fingerprint(&key));
        let answer = self
            .shared
            .ask(session, |prompt| Event::HostKeyPrompt {
                session,
                prompt,
                host,
                port,
                algorithm,
                fingerprint: print,
                changed,
                known_hosts_file: file,
                known_hosts_line: line,
                previous_fingerprint: previous,
                known_hosts_foreign: foreign,
            })
            .await;

        if !answer.is_some_and(|answer| answer.accept) {
            self.rejected.store(true, Ordering::Relaxed);
            return Ok(false);
        }

        // Lässt sich die Entscheidung nicht speichern, gilt sie für diese Verbindung trotzdem – der Benutzer
        // wird dann beim nächsten Mal wieder gefragt.
        if let Err(error) = self.shared.host_keys.trust(&self.host, self.port, &key) {
            self.shared.emit(Event::Warning {
                code: "known_hosts_not_saved",
                message: error.to_string(),
            });
        }

        Ok(true)
    }

    // Was der Server vor der Anmeldung zeigen will (`Banner` in sshd_config). Der Text kommt von einem Server,
    // bei dem noch niemand angemeldet ist: nur druckbare Zeichen, begrenzt lang.
    async fn auth_banner(&mut self, banner: &str, _session: &mut client::Session) -> Result<(), Self::Error> {
        let text: String = banner
            .chars()
            .filter(|character| *character == '\n' || !character.is_control())
            .take(MAX_BANNER)
            .collect();
        if !text.trim().is_empty() {
            self.shared.emit(Event::Banner {
                session: self.session,
                text,
            });
        }

        Ok(())
    }

    // Der Server möchte den lokalen Agenten benutzen. Ohne ausdrückliche Erlaubnis im Profil wird abgelehnt;
    // mit ihr werden die Bytes zwischen Kanal und Agent durchgereicht.
    async fn server_channel_open_agent_forward(
        &mut self,
        channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        if !self.forward_agent {
            reply.reject(ChannelOpenFailure::AdministrativelyProhibited).await;
            return Ok(());
        }

        match connect_agent(self.shared.agent.as_deref()).await {
            Some(agent) => {
                reply.accept().await;
                tokio::spawn(async move {
                    let mut agent = agent.into_inner();
                    let mut remote = channel.into_stream();
                    let _ = tokio::io::copy_bidirectional(&mut remote, &mut agent).await;
                });
            }
            None => reply.reject(ChannelOpenFailure::ConnectFailed).await,
        }

        Ok(())
    }

    // Ein Programm auf dem Server möchte auf dem X-Server dieses Rechners zeichnen. Ohne Erlaubnis im Profil
    // wird abgelehnt; mit ihr muss die Verbindung erst das Geheimnis vorzeigen (`x11::forward`).
    async fn server_channel_open_x11(
        &mut self,
        channel: Channel<Msg>,
        _originator_address: &str,
        _originator_port: u32,
        reply: ChannelOpenHandle,
        _session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        let Some(cookie) = self.x11 else {
            reply.reject(ChannelOpenFailure::AdministrativelyProhibited).await;
            return Ok(());
        };

        reply.accept().await;
        let server = self.shared.x_server();
        tokio::spawn(async move {
            crate::x11::forward(channel.into_stream(), &server, cookie).await;
        });
        Ok(())
    }

    // Jemand hat sich am Server an einen weitergeleiteten Port gehängt (`ssh -R`). Angenommen wird nur, worum
    // der Server gebeten wurde; wohin es hier geht, steht im Tunnel – nicht in dem, was der Server nennt. Ohne
    // diese Methode nähme die Bibliothek jeden solchen Kanal an.
    async fn server_channel_open_forwarded_tcpip(
        &mut self,
        channel: Channel<Msg>,
        _connected_address: &str,
        connected_port: u32,
        _originator_address: &str,
        _originator_port: u32,
        reply: ChannelOpenHandle,
        _session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        let target = self
            .remotes
            .as_ref()
            .and_then(|remotes| crate::tunnels::target(remotes, connected_port));
        match target {
            Some((host, port)) => {
                reply.accept().await;
                crate::tunnels::forward(channel.into_stream(), host, port);
            }
            None => reply.reject(ChannelOpenFailure::AdministrativelyProhibited).await,
        }

        Ok(())
    }
}

fn allows(methods: &MethodSet, method: MethodKind) -> bool {
    methods.contains(&method)
}

fn cancelled() -> Failure {
    Failure::new(CloseReason::AuthFailed, "the sign-in was cancelled")
}

/// Meldet den Benutzer an. Reihenfolge wie bei OpenSSH: erst Schlüssel (Datei aus dem Profil, Agent,
/// Standarddateien), dann Passwort, dann Fragen des Servers.
async fn authenticate(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    handle: &mut Handle<Client>,
) -> Result<(), Failure> {
    let user = profile.user.as_str();

    // „none“ gelingt fast nie, liefert aber die Liste der Methoden, die der Server zulässt.
    let mut methods = match handle.authenticate_none(user).await? {
        russh::client::AuthResult::Success => {
            shared.trace(session, || "the server asks for no sign-in at all".to_owned());
            return Ok(());
        }
        russh::client::AuthResult::Failure { remaining_methods, .. } => remaining_methods,
    };
    // Was versucht wird und wie es ausgeht – ohne das, womit es versucht wird.
    let tried = |what: String, result: &russh::client::AuthResult| {
        shared.trace(session, || match result {
            russh::client::AuthResult::Success => format!("{what}: accepted"),
            russh::client::AuthResult::Failure {
                remaining_methods,
                partial_success,
            } => format!(
                "{what}: {}; the server still allows {}",
                if *partial_success {
                    "accepted, but more is required"
                } else {
                    "rejected"
                },
                offered(remaining_methods)
            ),
        });
    };
    shared.trace(session, || {
        format!("the server allows these ways to sign in: {}", offered(&methods))
    });
    if handle.is_closed() {
        return Err(Failure::new(
            CloseReason::ConnectionLost,
            "the server closed the connection",
        ));
    }

    let mut agent = AgentNotes::default();
    if allows(&methods, MethodKind::PublicKey) {
        let rsa_hash = handle.best_supported_rsa_hash().await.ok().flatten().flatten();

        // Nennt das Profil einen Schlüssel, den der Agent hält, signiert der Agent – zuerst, vor allen anderen.
        // So findet sich in einem Passwort-Manager mit vielen Schlüsseln der richtige (wie `IdentityFile` mit
        // einem Agenten bei OpenSSH), und nach der Passphrase der Datei fragt dann niemand.
        let named = match profile.key_path.as_deref() {
            Some(path) if profile.use_agent => named_public_key(Path::new(path)).await,
            _ => None,
        };
        let mut from_agent = false;
        if let Some((named, _)) = &named
            && let Some(remaining) =
                try_agent(shared, session, handle, user, rsa_hash, Pick::Only(named), &mut agent).await
        {
            match remaining {
                None => return Ok(()),
                Some(remaining) => (methods, from_agent) = (remaining, true),
            }
        }

        // Die Datei selbst – außer sie enthält nur den öffentlichen Teil (dann liegt der geheime allein beim
        // Agenten), oder der Server hat eben den Schlüssel abgelehnt, der in ihr steht. Kam der öffentliche Teil
        // aus der Datei daneben, kann in der Schlüsseldatei ein anderer stehen: Dann wird sie trotzdem versucht.
        let public_only = matches!(&named, Some((_, Named::PublicFile)));
        let rejected = from_agent && matches!(&named, Some((_, Named::OwnHeader)));
        if public_only && !from_agent && !agent.declined {
            shared.trace(session, || {
                "the key file holds only the public key, and the SSH agent does not hold this key".to_owned()
            });
        }

        if !rejected
            && !public_only
            && allows(&methods, MethodKind::PublicKey)
            && let Some(path) = profile.key_path.as_deref()
            && let Some(key) = load_key(shared, session, profile, Path::new(path)).await?
        {
            let what = format!("key file {path} ({})", key.algorithm().as_str());
            let key = PrivateKeyWithHashAlg::new(Arc::new(key), rsa_hash);
            let result = handle.authenticate_publickey(user, key).await?;
            tried(what, &result);
            match result {
                russh::client::AuthResult::Success => return Ok(()),
                russh::client::AuthResult::Failure { remaining_methods, .. } => methods = remaining_methods,
            }
        }

        // Mit „nur dieser Schlüssel“ im Profil (`IdentitiesOnly`) bekommt der Server keine weiteren aus dem Agenten
        // angeboten – jeder zählte bei ihm als Versuch.
        let only_named = profile.identities_only && profile.key_path.is_some();
        if only_named && profile.use_agent {
            shared.trace(session, || {
                "only the key named in the profile is offered, no other keys of the SSH agent".to_owned()
            });
        }

        // Hat der Agent eben eine Signatur verweigert, bekommt er keine weiteren Schlüssel vorgelegt: Der Benutzer
        // hat abgelehnt und soll nicht gleich wieder gefragt werden.
        if profile.use_agent
            && !only_named
            && !agent.declined
            && allows(&methods, MethodKind::PublicKey)
            && let Some(remaining) = try_agent(
                shared,
                session,
                handle,
                user,
                rsa_hash,
                Pick::Except(named.as_ref().map(|(key, _)| key)),
                &mut agent,
            )
            .await
        {
            match remaining {
                None => return Ok(()),
                Some(remaining) => methods = remaining,
            }
        }

        // Standarddateien nur, wenn das Profil keinen eigenen Schlüssel nennt.
        if profile.key_path.is_none() {
            for path in default_keys(shared.ssh_dir.as_deref()) {
                if !allows(&methods, MethodKind::PublicKey) {
                    break;
                }

                if let Some(key) = load_key(shared, session, profile, &path).await? {
                    let what = format!("key file {} ({})", path.display(), key.algorithm().as_str());
                    let key = PrivateKeyWithHashAlg::new(Arc::new(key), rsa_hash);
                    let result = handle.authenticate_publickey(user, key).await?;
                    tried(what, &result);
                    match result {
                        russh::client::AuthResult::Success => return Ok(()),
                        russh::client::AuthResult::Failure { remaining_methods, .. } => methods = remaining_methods,
                    }
                }
            }
        }
    }

    if allows(&methods, MethodKind::Password) {
        let key = password_key(&profile.id);

        // Nur für das Ziel, für das es gespeichert wurde – sonst wird es vergessen und gefragt.
        if let Some(saved) = crate::secrets::saved_password(&*shared.secrets, profile) {
            let result = handle.authenticate_password(user, saved).await?;
            tried("the saved password".to_owned(), &result);
            match result {
                russh::client::AuthResult::Success => return Ok(()),
                russh::client::AuthResult::Failure { remaining_methods, .. } => {
                    // Das gespeicherte Passwort stimmt nicht mehr: entfernen, sonst kostet es bei jeder
                    // Verbindung einen Fehlversuch.
                    crate::secrets::forget_password(&*shared.secrets, &profile.id);
                    methods = remaining_methods;
                }
            }
        }

        let mut attempt = 1;
        while allows(&methods, MethodKind::Password) && attempt <= MAX_SECRET_ATTEMPTS {
            let Some((password, save)) =
                ask_secret(shared, session, profile, SecretKind::Password, None, attempt).await
            else {
                return Err(cancelled());
            };

            let result = handle.authenticate_password(user, password.clone()).await?;
            tried(format!("a typed password (attempt {attempt})"), &result);
            match result {
                russh::client::AuthResult::Success => {
                    if save {
                        remember(shared, &key, profile, &password);
                    }

                    return Ok(());
                }
                russh::client::AuthResult::Failure { remaining_methods, .. } => methods = remaining_methods,
            }

            attempt += 1;
        }
    }

    if allows(&methods, MethodKind::KeyboardInteractive) {
        let mut response = handle.authenticate_keyboard_interactive_start(user, None).await?;
        for round in 1..=MAX_INTERACTIVE_ROUNDS {
            shared.trace(session, || match &response {
                KeyboardInteractiveAuthResponse::Success => "questions of the server: accepted".to_owned(),
                KeyboardInteractiveAuthResponse::Failure { .. } => "questions of the server: rejected".to_owned(),
                KeyboardInteractiveAuthResponse::InfoRequest { prompts, .. } => {
                    format!("the server asks {} question(s), round {round}", prompts.len())
                }
            });
            match response {
                KeyboardInteractiveAuthResponse::Success => return Ok(()),
                KeyboardInteractiveAuthResponse::Failure { .. } => break,
                KeyboardInteractiveAuthResponse::InfoRequest {
                    name,
                    instructions,
                    prompts,
                } => {
                    let answers = if prompts.is_empty() {
                        Vec::new()
                    } else {
                        let fields = prompts
                            .into_iter()
                            .map(|prompt| SecretField {
                                text: prompt.prompt,
                                echo: prompt.echo,
                            })
                            .collect();
                        let (user, host) = (profile.user.clone(), server_name(profile));
                        let answer = shared
                            .ask(session, |prompt| Event::SecretPrompt {
                                session,
                                prompt,
                                kind: SecretKind::KeyboardInteractive,
                                user,
                                host,
                                key_path: None,
                                name: Some(name).filter(|name| !name.is_empty()),
                                instructions: Some(instructions).filter(|text| !text.is_empty()),
                                fields,
                                can_save: false,
                                attempt: round,
                            })
                            .await;
                        match answer.and_then(|answer| answer.values) {
                            Some(values) => values,
                            None => return Err(cancelled()),
                        }
                    };
                    response = handle.authenticate_keyboard_interactive_respond(answers).await?;
                }
            }
        }
    }

    // Der Agent hält mehr Schlüssel, als angeboten wurden – bei einem Passwort-Manager der gewöhnliche Fall. Ohne
    // diesen Hinweis stünde nur „Anmeldung fehlgeschlagen“ da, und der richtige Schlüssel war nie an der Reihe.
    if agent.left_out > 0 {
        shared.emit(Event::Warning {
            code: "agent_more_keys",
            message: format!(
                "{}: {MAX_AGENT_KEYS} keys of the SSH agent were offered, {} more were not",
                profile.name, agent.left_out
            ),
        });
    }

    // Der Agent hat lange gebraucht und dann keinen Schlüssel genannt: So antwortet ein Passwort-Manager, der
    // gesperrt ist und nicht entsperrt wurde (Bitwarden nach einer Minute).
    if agent.silent {
        shared.emit(Event::Warning {
            code: "agent_locked",
            message: format!("{}: the SSH agent named no keys after a long wait", profile.name),
        });
    }

    // Der Server wollte einen Schlüssel des Agenten, der Agent hat nicht signiert. Auch das stünde sonst nur als
    // „Anmeldung fehlgeschlagen“ da – und ein Passwort-Manager wartet auf seine Freigabe nicht ewig.
    if agent.declined {
        shared.emit(Event::Warning {
            code: "agent_declined",
            message: format!("{}: the SSH agent did not sign", profile.name),
        });
    }

    if handle.is_closed() {
        return Err(Failure::new(
            CloseReason::AuthFailed,
            "the server closed the connection during sign-in (too many failed attempts?)",
        ));
    }

    Err(Failure::new(CloseReason::AuthFailed, "no sign-in method was accepted"))
}

/// Text des Servers für das Protokoll: nur druckbare ASCII-Zeichen, und nicht beliebig viel davon.
pub(crate) fn printable(text: &str) -> String {
    text.chars()
        .filter(|character| character.is_ascii_graphic() || *character == ' ')
        .take(120)
        .collect()
}

/// Die Verfahren, die der Server noch zulässt, als Aufzählung – für das ausführliche Protokoll.
fn offered(methods: &MethodSet) -> String {
    let names: Vec<String> = methods
        .iter()
        .map(|method| format!("{method:?}").to_lowercase())
        .collect();
    if names.is_empty() {
        "nothing".to_owned()
    } else {
        names.join(", ")
    }
}

/// Was eine Anmeldung über den Agenten festhält, über beide Runden hinweg ([`try_agent`]).
#[derive(Default)]
struct AgentNotes {
    /// Was der Agent auf die Frage nach seinen Schlüsseln genannt hat – gefragt wird einmal.
    listed: Option<Vec<keys::agent::AgentIdentity>>,
    /// Er hat lange gebraucht und dann nichts genannt ([`AGENT_SILENCE`]).
    silent: bool,
    /// So viele Schlüssel kamen nicht an die Reihe ([`MAX_AGENT_KEYS`]).
    left_out: usize,
    /// Er hat eine Signatur verweigert: abgelehnt im Passwort-Manager, oder die Freigabe kam nicht.
    declined: bool,
}

/// Welche Schlüssel des Agenten an der Reihe sind.
enum Pick<'a> {
    /// Nur dieser – der, den das Profil nennt.
    Only(&'a keys::PublicKey),
    /// Die ersten [`MAX_AGENT_KEYS`], ohne diesen (er war schon an der Reihe).
    Except(Option<&'a keys::PublicKey>),
}

/// Woher der öffentliche Schlüssel stammt, den das Profil nennt.
#[derive(PartialEq)]
enum Named {
    /// Die Datei enthält nur den öffentlichen Teil – so legt Bitwarden ihn ab.
    PublicFile,
    /// Aus dem Kopf der Schlüsseldatei selbst (Format von OpenSSH: Der öffentliche Teil steht unverschlüsselt vorn).
    OwnHeader,
    /// Aus der Datei `….pub` daneben. Die kann veraltet sein und zu einem anderen Schlüssel gehören.
    Beside,
}

/// Der öffentliche Schlüssel zu der Datei, die das Profil nennt – ohne Passphrase, nie durch Entsperren. Erst
/// die Datei selbst, dann die Datei `….pub` daneben (für Formate, deren Kopf nichts verrät, und für eine Datei,
/// die es hier gar nicht gibt).
async fn named_public_key(path: &Path) -> Option<(keys::PublicKey, Named)> {
    // Eine Schlüsseldatei ist klein; mehr wird nicht gelesen, gleich was dort liegt.
    const LIMIT: u64 = 64 * 1024;
    let read = |path: PathBuf| async move {
        use tokio::io::AsyncReadExt;

        let mut text = String::new();
        let file = tokio::fs::File::open(&path).await.ok()?;
        file.take(LIMIT + 1).read_to_string(&mut text).await.ok()?;
        (text.len() as u64 <= LIMIT).then_some(text)
    };
    if let Some(text) = read(path.to_owned()).await {
        if let Ok(key) = keys::PublicKey::from_openssh(text.trim()) {
            return Some((key, Named::PublicFile));
        }

        if let Ok(key) = keys::PrivateKey::from_openssh(text.as_bytes()) {
            return Some((key.public_key().clone(), Named::OwnHeader));
        }
    }

    let mut beside = path.as_os_str().to_owned();
    beside.push(".pub");
    let text = read(PathBuf::from(beside)).await?;
    keys::PublicKey::from_openssh(text.trim())
        .ok()
        .map(|key| (key, Named::Beside))
}

/// Probiert Schlüssel des Agenten. `None`: kein Agent erreichbar, oder er hält den verlangten Schlüssel nicht.
/// `Some(None)`: angemeldet. `Some(Some(methoden))`: abgelehnt, der Server lässt noch diese Methoden zu.
async fn try_agent(
    shared: &Arc<Shared>,
    session: u64,
    handle: &mut Handle<Client>,
    user: &str,
    rsa_hash: Option<keys::HashAlg>,
    pick: Pick<'_>,
    notes: &mut AgentNotes,
) -> Option<Option<MethodSet>> {
    let Some(mut agent) = connect_agent(shared.agent.as_deref()).await else {
        shared.trace(session, || "no SSH agent is reachable".to_owned());
        return None;
    };

    // Nach seinen Schlüsseln wird der Agent je Anmeldung einmal gefragt: Ein gesperrter Passwort-Manager lässt
    // jede Frage bis zu einer Minute offen – zweimal gefragt wäre das länger, als ein Server auf die Anmeldung
    // wartet (OpenSSH: zwei Minuten).
    let identities = match &notes.listed {
        Some(listed) => listed.clone(),
        None => {
            let asked = Instant::now();
            let identities = match timeout(AGENT_LIST_TIMEOUT, agent.request_identities()).await {
                Ok(Ok(identities)) => identities,
                Ok(Err(error)) => {
                    shared.trace(session, || format!("the SSH agent did not list its keys ({error})"));
                    Vec::new()
                }
                Err(_) => {
                    shared.trace(session, || "the SSH agent did not list its keys in time".to_owned());
                    Vec::new()
                }
            };
            // Eine Absage liest die Bibliothek als leere Liste; von „hält nichts“ unterscheidet sie nur die Zeit.
            notes.silent = identities.is_empty() && asked.elapsed() >= AGENT_SILENCE;
            shared.trace(session, || {
                if notes.silent {
                    format!(
                        "the SSH agent named no keys after {} s – is the password manager locked?",
                        asked.elapsed().as_secs()
                    )
                } else {
                    format!("the SSH agent holds {} key(s)", identities.len())
                }
            });
            notes.listed = Some(identities.clone());
            identities
        }
    };

    let same = |key: &keys::PublicKey, other: &keys::PublicKey| key.key_data() == other.key_data();
    let chosen: Vec<_> = match pick {
        Pick::Only(named) => {
            let found: Vec<_> = identities
                .into_iter()
                .filter(|identity| same(&identity.public_key(), named))
                .take(1)
                .collect();
            if found.is_empty() {
                shared.trace(session, || {
                    format!(
                        "the SSH agent does not hold the key of the profile ({})",
                        crate::hostkeys::fingerprint(named)
                    )
                });
                return None;
            }

            found
        }
        Pick::Except(tried) => {
            let mut others: Vec<_> = identities
                .into_iter()
                .filter(|identity| tried.is_none_or(|tried| !same(&identity.public_key(), tried)))
                .collect();
            notes.left_out = others.len().saturating_sub(MAX_AGENT_KEYS);
            others.truncate(MAX_AGENT_KEYS);
            others
        }
    };

    let mut remaining = None;
    for identity in chosen {
        let key = identity.public_key().into_owned();
        let what = format!(
            "agent key {} ({})",
            crate::hostkeys::fingerprint(&key),
            key.algorithm().as_str()
        );
        // Ein alter Server nimmt RSA nur mit SHA-1 – das unterschreiben manche Agenten nicht (Bitwarden).
        let old_rsa = key.algorithm().is_rsa() && rsa_hash.is_none();
        match sign_in_with_agent(handle, user, key, rsa_hash, &mut agent).await {
            Ok(result) => {
                shared.trace(session, || match &result {
                    russh::client::AuthResult::Success => format!("{what}: accepted"),
                    russh::client::AuthResult::Failure { remaining_methods, .. } => {
                        format!(
                            "{what}: rejected; the server still allows {}",
                            offered(remaining_methods)
                        )
                    }
                });
                match result {
                    russh::client::AuthResult::Success => return Some(None),
                    russh::client::AuthResult::Failure { remaining_methods, .. } => remaining = Some(remaining_methods),
                }
            }
            Err(error) => {
                shared.trace(session, || {
                    let why = if old_rsa {
                        " – this server only takes RSA signatures with SHA-1, which some agents refuse"
                    } else {
                        ""
                    };
                    format!("{what}: the agent did not sign ({error}){why}")
                });
                notes.declined = true;
                break;
            }
        }
    }

    remaining.map(Some)
}

/// Meldet mit einem Schlüssel des Agenten an: erst die Frage an den Server, ob er den Schlüssel will, dann die
/// Signatur des Agenten. `Err` nennt, warum der Agent nicht signiert hat.
///
/// Nicht einfach `authenticate_publickey_with` mit dem Agenten: Scheitert dort die Signatur, kehrt der Aufruf
/// zwar mit dem Fehler zurück, die Verbindung der Bibliothek wartet aber weiter auf die Signatur und verwirft
/// bis dahin jede andere Nachricht – der nächste Versuch (ein anderer Schlüssel, das Passwort) bliebe für
/// immer stehen (russh 0.64, gefunden 2026-10-12 mit Bitwarden: „Ablehnen“ in dessen Rückfrage). Deshalb
/// bekommt die Bibliothek bei einer Absage ihre Daten unverändert zurück: Das versteht sie als „nichts
/// schicken“ und nimmt danach wieder Anmeldungen an. Der Server hat bis dahin nur die Frage gesehen, keinen
/// Fehlversuch.
async fn sign_in_with_agent(
    handle: &mut Handle<Client>,
    user: &str,
    key: keys::PublicKey,
    rsa_hash: Option<keys::HashAlg>,
    agent: &mut DynamicAgent,
) -> Result<russh::client::AuthResult, String> {
    let refused = Arc::new(std::sync::Mutex::new(None));
    let mut signer = AskingAgent {
        agent,
        refused: refused.clone(),
    };
    let attempt = handle.authenticate_publickey_with(user, key, rsa_hash, &mut signer);
    tokio::pin!(attempt);
    let reason = loop {
        tokio::select! {
            biased;
            result = &mut attempt => return result.map_err(|error| error.to_string()),
            () = sleep(AGENT_REFUSAL_CHECK) => {
                if let Some(reason) = refused.lock().ok().and_then(|mut refused| refused.take()) {
                    break reason;
                }
            }
        }
    };

    // Nach der Absage reicht der Aufruf die unveränderten Daten noch an die Verbindung weiter und wartet dann
    // auf eine Antwort des Servers, die nie kommt. Ihm dafür einen Augenblick lassen, dann fallen lassen. Das
    // Weiterreichen geschieht im selben Zug wie die Absage, solange im Kanal zur Verbindung Platz ist – und die
    // wartet in diesem Moment auf genau diese Nachricht.
    match timeout(AGENT_REFUSAL_CHECK * 4, &mut attempt).await {
        Ok(result) => result.map_err(|error| error.to_string()),
        Err(_) => Err(reason),
    }
}

/// Der Agent als Unterzeichner, der eine Absage nicht als Fehler meldet, sondern die Daten unverändert
/// zurückgibt und sich den Grund merkt ([`sign_in_with_agent`]).
struct AskingAgent<'a> {
    agent: &'a mut DynamicAgent,
    refused: Arc<std::sync::Mutex<Option<String>>>,
}

impl russh::Signer for AskingAgent<'_> {
    type Error = russh::AgentAuthError;

    async fn auth_sign(
        &mut self,
        key: &keys::agent::AgentIdentity,
        hash_alg: Option<keys::HashAlg>,
        to_sign: Vec<u8>,
    ) -> Result<Vec<u8>, Self::Error> {
        let unsigned = to_sign.clone();
        match self.agent.auth_sign(key, hash_alg, to_sign).await {
            Ok(signed) => Ok(signed),
            Err(error) => {
                if let Ok(mut refused) = self.refused.lock() {
                    *refused = Some(error.to_string());
                }

                Ok(unsigned)
            }
        }
    }
}

type DynamicAgent = AgentClient<Box<dyn keys::agent::client::AgentStream + Send + Unpin>>;

/// Verbindet mit dem SSH-Agenten des Benutzers. `path` nennt einen bestimmten Ort (`CoreConfig::agent`), sonst
/// gilt der übliche.
async fn connect_agent(path: Option<&str>) -> Option<DynamicAgent> {
    timeout(AGENT_TIMEOUT, open_agent(path)).await.ok().flatten()
}

#[cfg(windows)]
async fn open_agent(path: Option<&str>) -> Option<DynamicAgent> {
    if let Some(pipe) = path {
        return AgentClient::connect_named_pipe(pipe)
            .await
            .ok()
            .map(AgentClient::dynamic);
    }

    // Erst der OpenSSH-Agent von Windows, dann Pageant.
    if let Ok(agent) = AgentClient::connect_named_pipe(r"\\.\pipe\openssh-ssh-agent").await {
        return Some(agent.dynamic());
    }

    AgentClient::connect_pageant().await.ok().map(AgentClient::dynamic)
}

#[cfg(unix)]
async fn open_agent(path: Option<&str>) -> Option<DynamicAgent> {
    match path {
        Some(socket) => AgentClient::connect_uds(socket).await.ok().map(AgentClient::dynamic),
        None => AgentClient::connect_env().await.ok().map(AgentClient::dynamic),
    }
}

fn default_keys(ssh_dir: Option<&Path>) -> Vec<PathBuf> {
    let Some(directory) = ssh_dir else {
        return Vec::new();
    };

    ["id_ed25519", "id_ecdsa", "id_rsa"]
        .iter()
        .map(|name| directory.join(name))
        .filter(|path| path.is_file())
        .collect()
}

/// Lädt einen privaten Schlüssel und fragt bei Bedarf nach der Passphrase.
/// `Ok(None)`: Die Datei ist nicht brauchbar, die Anmeldung geht mit anderen Methoden weiter.
async fn load_key(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    path: &Path,
) -> Result<Option<PrivateKey>, Failure> {
    match decode_key(path, None).await {
        Ok(key) => return Ok(Some(key)),
        Err(keys::Error::KeyIsEncrypted) => {}
        Err(error) => {
            shared.emit(Event::Warning {
                code: "key_unreadable",
                message: format!("{}: {error}", path.display()),
            });
            return Ok(None);
        }
    }

    // In diesem Lauf schon einmal entsperrt: Dann fragt niemand noch einmal (siehe `unlocked.rs`).
    if let Some(key) = shared.unlocked.get(path) {
        return Ok(Some(key));
    }

    let store_key = passphrase_key(&profile.id);
    if let Some(saved) = shared.secrets.get(&store_key) {
        match decode_key(path, Some(saved)).await {
            Ok(key) => {
                shared.unlocked.keep(path, &key);
                return Ok(Some(key));
            }
            Err(_) => shared.secrets.delete(&store_key),
        }
    }

    for attempt in 1..=MAX_SECRET_ATTEMPTS {
        let Some((passphrase, save)) =
            ask_secret(shared, session, profile, SecretKind::Passphrase, Some(path), attempt).await
        else {
            return Err(cancelled());
        };

        if let Ok(key) = decode_key(path, Some(passphrase.clone())).await {
            if save {
                remember(shared, &store_key, profile, &passphrase);
            }

            shared.unlocked.keep(path, &key);
            return Ok(Some(key));
        }
    }

    Ok(None)
}

// Das Entschlüsseln (bcrypt-pbkdf) rechnet spürbar; nicht auf einem Thread des Runtimes.
async fn decode_key(path: &Path, passphrase: Option<String>) -> Result<PrivateKey, keys::Error> {
    let path = path.to_owned();
    tokio::task::spawn_blocking(move || crate::keyfiles::load(&path, passphrase.as_deref()))
        .await
        .unwrap_or(Err(keys::Error::CouldNotReadKey))
}

async fn ask_secret(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    kind: SecretKind,
    key_path: Option<&Path>,
    attempt: u32,
) -> Option<(String, bool)> {
    let (user, host) = (profile.user.clone(), server_name(profile));
    let key_path = key_path.map(|path| path.display().to_string());
    let answer = shared
        .ask(session, |prompt| Event::SecretPrompt {
            session,
            prompt,
            kind,
            user,
            host,
            key_path,
            name: None,
            instructions: None,
            fields: vec![SecretField {
                text: String::new(),
                echo: false,
            }],
            can_save: true,
            attempt,
        })
        .await?;
    let value = answer.values?.into_iter().next()?;
    Some((value, answer.save))
}

fn remember(shared: &Arc<Shared>, key: &str, profile: &Profile, secret: &str) {
    let label = format!("{}@{}", profile.user, profile.host);

    // Ein Passwort wird mit seinem Ziel gespeichert; eine Passphrase gehört zur Schlüsseldatei und geht nie hinaus.
    let saved = if key == password_key(&profile.id) {
        crate::secrets::save_password(&*shared.secrets, profile, &label, secret)
    } else {
        shared.secrets.set(key, &label, secret)
    };
    if let Err(message) = saved {
        shared.emit(Event::Warning {
            code: "secret_not_saved",
            message,
        });
    }
}

#[cfg(test)]
mod preferred_tests {
    use super::*;

    #[test]
    fn the_default_offers_nothing_weak_and_no_compression_first() {
        let preferred = preferred(&Profile::default());
        assert_eq!(preferred.kex, russh::Preferred::DEFAULT.kex);
        assert_eq!(preferred.cipher, russh::Preferred::DEFAULT.cipher);
        assert_eq!(preferred.mac, russh::Preferred::DEFAULT.mac);
        assert_eq!(preferred.compression.first(), Some(&russh::compression::NONE));
    }

    #[test]
    fn compression_moves_zlib_to_the_front() {
        let preferred = preferred(&Profile {
            compression: true,
            ..Profile::default()
        });
        assert_eq!(preferred.compression.first(), Some(&russh::compression::ZLIB_LEGACY));
        assert_eq!(preferred.compression.last(), Some(&russh::compression::NONE));
    }

    #[test]
    fn older_algorithms_come_after_the_safe_ones() {
        let safe = russh::Preferred::DEFAULT;
        let preferred = preferred(&Profile {
            legacy_algorithms: true,
            ..Profile::default()
        });

        // Was sicher ist, steht weiter vorn und in derselben Reihenfolge.
        assert_eq!(preferred.cipher[..safe.cipher.len()], safe.cipher[..]);
        assert_eq!(preferred.mac[..safe.mac.len()], safe.mac[..]);
        assert_eq!(preferred.kex.first(), safe.kex.first());
        assert!(preferred.kex.contains(&russh::kex::DH_G14_SHA1));
        assert!(preferred.cipher.contains(&russh::cipher::AES_128_CBC));
        assert!(preferred.mac.contains(&russh::mac::HMAC_SHA1));

        // Die Merker für Erweiterungen bleiben am Ende, sonst gälten sie als Verfahren.
        assert_eq!(preferred.kex.last(), safe.kex.last());
        assert_eq!(preferred.kex.len(), safe.kex.len() + 6);
    }
}

#[cfg(test)]
mod probe_tests {
    use super::*;

    fn output(stdout: &str) -> Result<crate::exec::Output, crate::sftp::Failure> {
        Ok(crate::exec::Output {
            stdout: stdout.to_owned(),
            ..crate::exec::Output::default()
        })
    }

    fn failure(code: &'static str) -> Result<crate::exec::Output, crate::sftp::Failure> {
        Err(crate::sftp::Failure {
            code,
            message: String::new(),
        })
    }

    #[test]
    fn only_silence_leaves_the_question_open() {
        assert_eq!(probe_outcome(&output("nexus-posix\r\n"), "nexus-posix"), Probe::Yes);
        assert_eq!(
            probe_outcome(&output("'sh' is not recognized"), "nexus-posix"),
            Probe::No
        );
        assert_eq!(probe_outcome(&output(""), "nexus-posix"), Probe::No);
        // Abgelehnt ist ein Nein; nur eine Probe, die in ihrer Frist nichts sagt, lässt die Frage offen.
        assert_eq!(probe_outcome(&failure("no_exec"), "nexus-posix"), Probe::No);
        assert_eq!(probe_outcome(&failure("failed"), "nexus-posix"), Probe::No);
        assert_eq!(probe_outcome(&failure("timeout"), "nexus-posix"), Probe::Silent);
        assert_eq!(shell_unknown().code, "timeout");
    }
}
