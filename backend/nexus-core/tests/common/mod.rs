//! Gemeinsames Gerüst der Integrationstests: der Kern gegen `nexus-testserver` im selben Prozess.

#![allow(dead_code)] // Jede Testdatei benutzt einen anderen Teil.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use nexus_core::core::{Core, CoreConfig, EventSink};
use nexus_core::hostkeys::HostKeys;
use nexus_core::secrets::MemorySecretStore;
use nexus_testserver::{Options, Running, host_key};
use russh::keys::ssh_key::private::Ed25519Keypair;
use russh::keys::{PrivateKey, PublicKey};
use serde_json::{Value, json};
use tokio::runtime::Runtime;

pub const PATIENCE: Duration = Duration::from_secs(30);

/// Wie lange auf ein Ereignis gewartet wird: [`PATIENCE`], gegen einen langsamen echten Server mehr – Sekunden in
/// `NEXUS_PATIENCE` (ein Windows mit PowerShell als Anmelde-Shell brauchte auf einer ausgelasteten VM länger als
/// 30 s bis zur Shell).
fn patience() -> Duration {
    std::env::var("NEXUS_PATIENCE")
        .ok()
        .and_then(|seconds| seconds.parse().ok())
        .map_or(PATIENCE, Duration::from_secs)
}

#[derive(Default)]
struct Recorded {
    events: Vec<Value>,
    seen: usize,
    output: HashMap<u64, Vec<u8>>,
}

/// Sammelt, was der Kern meldet, und lässt Tests darauf warten.
#[derive(Default)]
pub struct Recorder {
    recorded: Mutex<Recorded>,
    changed: Condvar,
}

impl EventSink for Recorder {
    fn event(&self, json: &str) {
        self.recorded
            .lock()
            .unwrap()
            .events
            .push(serde_json::from_str(json).unwrap());
        self.changed.notify_all();
    }

    fn output(&self, session: u64, data: &[u8]) {
        self.recorded
            .lock()
            .unwrap()
            .output
            .entry(session)
            .or_default()
            .extend_from_slice(data);
        self.changed.notify_all();
    }
}

impl Recorder {
    /// Wartet auf das nächste noch nicht abgeholte Ereignis mit diesem Namen; andere davor werden übersprungen.
    pub fn event(&self, name: &str) -> Value {
        let deadline = Instant::now() + patience();
        let mut recorded = self.recorded.lock().unwrap();
        loop {
            let start = recorded.seen;
            if let Some(offset) = recorded.events[start..].iter().position(|event| event["event"] == name) {
                recorded.seen = start + offset + 1;
                return recorded.events[start + offset].clone();
            }

            let left = deadline.saturating_duration_since(Instant::now());
            assert!(!left.is_zero(), "kein Ereignis {name}; gesehen: {:#?}", recorded.events);
            recorded = self.changed.wait_timeout(recorded, left).unwrap().0;
        }
    }

    /// Wartet auf ein Ereignis, das die Bedingung erfüllt – ohne die Reihenfolge der übrigen anzutasten.
    pub fn find(&self, what: &str, matches: impl Fn(&Value) -> bool) -> Value {
        self.find_within(patience(), what, matches)
    }

    /// Wie [`Recorder::find`], mit eigener Geduld – für das wenige, das länger dauern darf.
    pub fn find_within(&self, patience: Duration, what: &str, matches: impl Fn(&Value) -> bool) -> Value {
        let deadline = Instant::now() + patience;
        let mut recorded = self.recorded.lock().unwrap();
        loop {
            if let Some(event) = recorded.events.iter().find(|event| matches(event)) {
                return event.clone();
            }

            let left = deadline.saturating_duration_since(Instant::now());
            assert!(
                !left.is_zero(),
                "nichts gefunden: {what}; gesehen: {:#?}",
                recorded.events
            );
            recorded = self.changed.wait_timeout(recorded, left).unwrap().0;
        }
    }

    /// Wartet auf den nächsten Zustandswechsel und prüft, dass es der erwartete ist.
    pub fn state(&self, expected: &str) -> Value {
        let event = self.event("session.state");
        assert_eq!(event["state"], expected, "{event}");
        event
    }

    /// Wartet, bis die Ausgabe der Sitzung `needle` enthält, und holt alles bis dahin ab.
    pub fn output(&self, session: u64, needle: &str) -> String {
        let deadline = Instant::now() + patience();
        let needle = needle.as_bytes();

        // Bei viel Ausgabe (Megabytes) nicht jedes Mal von vorn suchen: nur das, was neu dazugekommen ist.
        let mut searched = 0;
        let mut recorded = self.recorded.lock().unwrap();
        loop {
            let buffer = recorded.output.entry(session).or_default();
            if let Some(position) = buffer[searched..]
                .windows(needle.len())
                .position(|window| window == needle)
            {
                let end = searched + position + needle.len();
                let taken: Vec<u8> = buffer.drain(..end).collect();
                return String::from_utf8_lossy(&taken).into_owned();
            }

            searched = buffer.len().saturating_sub(needle.len().saturating_sub(1));
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                let tail = String::from_utf8_lossy(&buffer[buffer.len().saturating_sub(600)..]).into_owned();
                panic!(
                    "Ausgabe ohne {:?}; ihr Ende: {tail:?}; Ereignisse: {:#?}",
                    String::from_utf8_lossy(needle),
                    recorded.events
                );
            }

            recorded = self.changed.wait_timeout(recorded, left).unwrap().0;
        }
    }

    /// Wie viele Bytes Ausgabe der Sitzung noch nicht abgeholt sind.
    pub fn output_len(&self, session: u64) -> usize {
        self.recorded.lock().unwrap().output.get(&session).map_or(0, Vec::len)
    }

    /// Ob die noch nicht abgeholte Ausgabe `needle` enthält – ohne zu warten und ohne etwas abzuholen.
    pub fn output_contains(&self, session: u64, needle: &str) -> bool {
        self.recorded
            .lock()
            .unwrap()
            .output
            .get(&session)
            .is_some_and(|buffer| buffer.windows(needle.len()).any(|window| window == needle.as_bytes()))
    }

    pub fn saw(&self, name: &str) -> bool {
        self.recorded
            .lock()
            .unwrap()
            .events
            .iter()
            .any(|event| event["event"] == name)
    }

    /// Alle bisherigen Ereignisse mit diesem Namen.
    pub fn all(&self, name: &str) -> Vec<Value> {
        self.recorded
            .lock()
            .unwrap()
            .events
            .iter()
            .filter(|event| event["event"] == name)
            .cloned()
            .collect()
    }
}

pub struct Fixture {
    pub core: Core,
    pub recorder: Arc<Recorder>,
    pub secrets: Arc<MemorySecretStore>,
    pub directory: PathBuf,
    pub port: u16,
    /// Wo der Kern den SSH-Agenten sucht. Dort lauscht nur etwas, wenn der Test [`Fixture::start_agent`] ruft –
    /// der echte Agent des Rechners bleibt in jedem Fall außen vor.
    pub agent: String,
    /// Wo der Kern den X-Server dieses Rechners sucht: ein Port, an dem nur der Test antwortet – der echte
    /// X-Server des Rechners, falls einer läuft, bleibt außen vor.
    pub x_server: std::net::TcpListener,
    _server: Running,
    server_runtime: Runtime,
}

pub fn user_key() -> PrivateKey {
    PrivateKey::from(Ed25519Keypair::from_seed(&[42; 32]))
}

pub fn authorized() -> Vec<PublicKey> {
    vec![user_key().public_key().clone()]
}

/// Passwort-Anmeldung als `demo` / `geheim`, ohne SFTP.
pub fn with_password() -> Options {
    Options {
        password: Some(("demo".into(), "geheim".into())),
        ..Options::default()
    }
}

impl Fixture {
    pub fn new(options: Options) -> Self {
        Self::with(|_| options)
    }

    /// Wie [`Fixture::new`], aber die Optionen dürfen das frisch angelegte Testverzeichnis benutzen
    /// (z. B. als SFTP-Wurzel).
    pub fn with(options: impl FnOnce(&PathBuf) -> Options) -> Self {
        static COUNTER: AtomicU32 = AtomicU32::new(0);
        let unique = format!("{}-{}", std::process::id(), COUNTER.fetch_add(1, Ordering::Relaxed));
        let directory = std::env::temp_dir().join(format!("nexus-core-it-{unique}"));
        let agent = if cfg!(windows) {
            format!(r"\\.\pipe\nexus-core-it-agent-{unique}")
        } else {
            directory.join("agent.sock").display().to_string()
        };
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(directory.join("ssh")).unwrap();
        let options = options(&directory);
        let x_server = std::net::TcpListener::bind(("127.0.0.1", 0)).unwrap();

        // Der Server läuft in einem eigenen Runtime: Der Kern bringt seines mit, und ein Runtime darf nicht
        // innerhalb eines anderen fallen gelassen werden.
        let server_runtime = Runtime::new().unwrap();
        let server = server_runtime.block_on(async {
            let listener = nexus_testserver::listen(0).unwrap();
            nexus_testserver::start(listener, host_key(1), options).unwrap()
        });

        let recorder = Arc::new(Recorder::default());
        let secrets = Arc::new(MemorySecretStore::default());
        let core = Core::new(
            CoreConfig {
                data_dir: directory.join("data"),
                ssh_dir: Some(directory.join("ssh")),
                agent: Some(agent.clone()),
                x11: Some(x_server.local_addr().unwrap().to_string()),
            },
            secrets.clone(),
            recorder.clone(),
        )
        .unwrap();

        Self {
            core,
            recorder,
            secrets,
            directory,
            port: server.address.port(),
            agent,
            x_server,
            _server: server,
            server_runtime,
        }
    }

    /// Ein weiterer Testserver mit eigenem Host-Schlüssel, z. B. als Ziel hinter einem Sprung-Server.
    pub fn another_server(&self, key_seed: u8, options: Options) -> Running {
        self.server_runtime.block_on(async {
            let listener = nexus_testserver::listen(0).unwrap();
            nexus_testserver::start(listener, host_key(key_seed), options).unwrap()
        })
    }

    /// Reißt alle Verbindungen ab, als wäre der Server ausgefallen: Sein Runtime endet, und mit ihm jede Verbindung.
    #[allow(dead_code)]
    pub fn cut_connections(&mut self) {
        let runtime = std::mem::replace(&mut self.server_runtime, Runtime::new().unwrap());
        runtime.shutdown_timeout(Duration::from_secs(2));
    }

    /// Beendet den Kern und startet einen neuen über demselben Datenordner – wie die App, die geschlossen und wieder
    /// geöffnet wird. Erst wenn der alte ganz zu Ende ist, beginnt der neue.
    pub fn restart_core(&mut self) {
        let config = || CoreConfig {
            data_dir: self.directory.join("data"),
            ssh_dir: Some(self.directory.join("ssh")),
            agent: Some(self.agent.clone()),
            x11: Some(self.x_server.local_addr().unwrap().to_string()),
        };

        // Solange der alte läuft, steht an seiner Stelle einer mit eigenem Datenordner.
        let placeholder = Core::new(
            CoreConfig {
                data_dir: self.directory.join("data-zwischen"),
                ..config()
            },
            self.secrets.clone(),
            Arc::new(Recorder::default()),
        )
        .unwrap();
        drop(std::mem::replace(&mut self.core, placeholder));
        self.core = Core::new(config(), self.secrets.clone(), self.recorder.clone()).unwrap();
    }

    /// Startet einen SSH-Agenten mit diesen Schlüsseln dort, wo der Kern ihn sucht.
    pub fn start_agent(&self, keys: &[PrivateKey]) {
        self.server_runtime.block_on(async {
            let listener = agent::listen(&self.agent);
            tokio::spawn(russh::keys::agent::server::serve(listener, ()));

            let mut client = agent::connect(&self.agent).await;
            for key in keys {
                client.add_identity(key, &[]).await.unwrap();
            }
        });
    }

    /// Ein Agent, der diese Schlüssel hält und zeigt, aber jede Bitte um eine Signatur ablehnt – wie ein
    /// Passwort-Manager, in dessen Rückfrage der Benutzer „Ablehnen“ drückt.
    pub fn start_declining_agent(&self, keys: &[PrivateKey]) {
        self.server_runtime.block_on(async {
            let listener = agent::listen(&self.agent);
            tokio::spawn(russh::keys::agent::server::serve(listener, agent::Declines));

            let mut client = agent::connect(&self.agent).await;
            for key in keys {
                client.add_identity(key, &[]).await.unwrap();
            }
        });
    }

    /// Ein Agent wie ein gesperrter Passwort-Manager: Er lässt die Frage nach seinen Schlüsseln eine Weile offen
    /// und lehnt sie dann ab. Zurück kommt der Zähler, wie oft danach gefragt wurde.
    pub fn start_locked_agent(&self, wait: Duration) -> Arc<std::sync::atomic::AtomicUsize> {
        let asked = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let agent = agent::Locked {
            wait,
            asked: asked.clone(),
        };
        self.server_runtime.block_on(async {
            let listener = agent::listen(&self.agent);
            tokio::spawn(russh::keys::agent::server::serve(listener, agent));
        });
        asked
    }

    pub fn request(&self, request: Value) -> Value {
        serde_json::from_str(&self.core.request(&request.to_string())).unwrap()
    }

    pub fn ok(&self, request: Value) -> Value {
        let response = self.request(request.clone());
        assert_eq!(response["ok"], true, "{request} -> {response}");
        response
    }

    /// Legt ein Profil für den Testserver an und liefert seine Kennung.
    pub fn profile(&self, extra: Value) -> String {
        let mut profile =
            json!({ "name": "test", "host": "127.0.0.1", "port": self.port, "user": "demo", "use_agent": false });
        for (key, value) in extra.as_object().unwrap() {
            profile[key] = value.clone();
        }

        self.ok(json!({ "op": "profiles.save", "profile": profile }))["profile"]["id"]
            .as_str()
            .unwrap()
            .to_owned()
    }

    pub fn open(&self, session: u64, profile: &str) {
        self.ok(json!({ "op": "session.open", "session": session, "profile_id": profile, "cols": 80, "rows": 24 }));
        self.recorder.state("connecting");
    }

    pub fn answer(&self, prompt: &Value, answer: Value) {
        let mut request = json!({ "op": "prompt.answer", "prompt": prompt["prompt"] });
        for (key, value) in answer.as_object().unwrap() {
            request[key] = value.clone();
        }

        self.ok(request);
    }

    pub fn trust_host_key(&self) {
        let prompt = self.recorder.event("prompt.host_key");
        self.answer(&prompt, json!({ "accept": true }));
    }

    pub fn own_known_hosts(&self) -> HostKeys {
        HostKeys::new(self.directory.join("data").join("known_hosts"), None)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.directory);
    }
}

/// Der Agent der Tests: unter Windows über eine Named Pipe, sonst über einen Unix-Socket – wie die echten.
mod agent {
    use futures::stream::{self, Stream};
    use russh::keys::agent::client::AgentClient;

    /// Lässt die Frage nach den Schlüsseln `wait` lang offen, lehnt sie dann ab und zählt mit.
    #[derive(Clone)]
    pub struct Locked {
        pub wait: std::time::Duration,
        pub asked: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    }

    impl russh::keys::agent::server::Agent for Locked {
        fn confirm_request(
            &self,
            message: russh::keys::agent::server::MessageType,
        ) -> impl Future<Output = bool> + Send {
            let listing = matches!(message, russh::keys::agent::server::MessageType::RequestKeys);
            let (wait, asked) = (self.wait, self.asked.clone());
            async move {
                if listing {
                    asked.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                    tokio::time::sleep(wait).await;
                }

                !listing
            }
        }
    }

    /// Zeigt seine Schlüssel und lehnt jede Bitte um eine Signatur ab.
    #[derive(Clone)]
    pub struct Declines;

    impl russh::keys::agent::server::Agent for Declines {
        fn confirm_request(
            &self,
            message: russh::keys::agent::server::MessageType,
        ) -> impl Future<Output = bool> + Send {
            let signing = matches!(message, russh::keys::agent::server::MessageType::Sign);
            async move {
                if signing {
                    // Eine Absage kommt nicht sofort: Der Benutzer liest die Rückfrage erst.
                    tokio::time::sleep(std::time::Duration::from_millis(400)).await;
                }

                !signing
            }
        }
    }

    #[cfg(windows)]
    pub type Connection = tokio::net::windows::named_pipe::NamedPipeServer;
    #[cfg(windows)]
    pub type Client = tokio::net::windows::named_pipe::NamedPipeClient;

    #[cfg(windows)]
    pub fn listen(path: &str) -> impl Stream<Item = std::io::Result<Connection>> + Unpin + use<> {
        use tokio::net::windows::named_pipe::ServerOptions;

        // Jede Verbindung braucht ihre eigene Instanz der Pipe; die nächste wird angelegt, bevor die gerade
        // verbundene herausgegeben wird, damit ein Client nie ins Leere greift.
        let first = ServerOptions::new().first_pipe_instance(true).create(path).unwrap();
        Box::pin(stream::unfold((first, path.to_owned()), |(server, path)| async move {
            let connected = server.connect().await;
            let next = ServerOptions::new().create(&path).ok()?;
            Some((connected.map(|()| server), (next, path)))
        }))
    }

    #[cfg(windows)]
    pub async fn connect(path: &str) -> AgentClient<Client> {
        AgentClient::connect_named_pipe(path).await.unwrap()
    }

    #[cfg(unix)]
    pub type Connection = tokio::net::UnixStream;
    #[cfg(unix)]
    pub type Client = tokio::net::UnixStream;

    #[cfg(unix)]
    pub fn listen(path: &str) -> impl Stream<Item = std::io::Result<Connection>> + Unpin + use<> {
        let listener = tokio::net::UnixListener::bind(path).unwrap();
        Box::pin(stream::unfold(listener, |listener| async move {
            let connection = listener.accept().await.map(|(stream, _)| stream);
            Some((connection, listener))
        }))
    }

    #[cfg(unix)]
    pub async fn connect(path: &str) -> AgentClient<Client> {
        AgentClient::connect_uds(path).await.unwrap()
    }
}
