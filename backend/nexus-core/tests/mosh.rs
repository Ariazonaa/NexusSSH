//! Das Terminal über Mosh: Anfang über SSH, Rückfall auf SSH, und – mit einem echten `mosh-server` – Tippen,
//! Größe, Abriss der SSH-Verbindung, Stille und Ende.
//!
//! Gegen den Testserver läuft nur, was ohne `mosh-server` geht (er spielt dessen Ausgabe nach). Ein echter Server
//! kommt aus der Umgebung (sonst laufen diese Tests leer durch): `NEXUS_REAL_MOSH_HOST` (z. B. `127.0.0.1` oder
//! `::1`), `NEXUS_REAL_MOSH_PORT`, `NEXUS_REAL_MOSH_USER` und `NEXUS_REAL_MOSH_KEY` (Schlüsseldatei ohne
//! Passphrase); für Abriss und Stille `NEXUS_REAL_MOSH_SCRIPT` (ein Skript mit `cut` und `mute`) und
//! für den Blick auf die laufenden `mosh-server` `NEXUS_REAL_MOSH_CONTAINER` (Name des Docker-Containers). Die
//! Tests mit echtem Server laufen nacheinander – `mute` träfe sonst auch die anderen.

mod common;

use std::sync::Mutex;
use std::time::{Duration, Instant};

use common::{Fixture, with_password};
use nexus_testserver::{MoshReply, MoshServer, Options};
use serde_json::{Value, json};

/// Ein Schlüssel in der Form, die `mosh-server` ausgibt.
const KEY: &str = "4NeCCgvZFe2RnPgrcU1PQw";

fn open(fixture: &Fixture, session: u64, profile: &str, trace: bool) {
    fixture.ok(json!({
        "op": "session.open", "session": session, "profile_id": profile, "cols": 80, "rows": 24, "trace": trace,
    }));
}

/// Meldet sich am Testserver an (Host-Schlüssel, Passwort) und wartet, bis die Sitzung steht.
fn sign_in(fixture: &Fixture, session: u64) {
    fixture.trust_host_key();
    let prompt = fixture.recorder.find("a question for the password", |event| {
        event["event"] == "prompt.secret" && event["session"] == session
    });
    fixture.answer(&prompt, json!({ "values": ["geheim"], "save": false }));
}

fn connected(fixture: &Fixture, session: u64, patience: Duration) -> Value {
    fixture.recorder.find_within(patience, "connected", |event| {
        event["event"] == "session.state" && event["session"] == session && event["state"] == "connected"
    })
}

fn warning(fixture: &Fixture, code: &str, patience: Duration) -> Value {
    fixture.recorder.find_within(patience, code, |event| {
        event["event"] == "core.warning" && event["code"] == code
    })
}

#[test]
fn without_mosh_server_the_terminal_runs_over_ssh_with_a_warning() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({ "mosh": true, "mosh_ports": "60001:60010" }));
    open(&fixture, 1, &profile, false);
    sign_in(&fixture, 1);

    let warned = warning(&fixture, "mosh_unavailable", common::PATIENCE);
    assert!(
        warned["message"]
            .as_str()
            .unwrap()
            .contains("mosh-server is not installed"),
        "{warned}"
    );
    connected(&fixture, 1, common::PATIENCE);
    assert!(!fixture.recorder.saw("session.mosh"));

    // Das Terminal läuft über SSH, wie ohne Mosh.
    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"echo hallo\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nhallo\r\n"));
}

#[test]
fn the_start_line_is_fixed_and_a_second_locale_follows_a_refused_first() {
    let refused = || MoshReply {
        stdout: "@nexus-mosh\n".into(),
        stderr: "The locale requested by LANG=C.UTF-8 isn't available here.\n\
                 mosh-server needs a UTF-8 native locale to run.\n"
            .into(),
        status: 1,
    };
    let mosh = MoshServer::answering([refused(), refused()]);
    let fixture = Fixture::new(Options {
        mosh: mosh.clone(),
        ..with_password()
    });
    let profile = fixture.profile(json!({ "mosh": true, "mosh_ports": "60001:60010" }));
    open(&fixture, 1, &profile, false);
    sign_in(&fixture, 1);

    warning(&fixture, "mosh_locale", common::PATIENCE);
    connected(&fixture, 1, common::PATIENCE);
    let lines = mosh.lines();
    assert_eq!(lines.len(), 2, "{lines:#?}");
    assert!(
        lines[0].ends_with("exec mosh-server new -s -c 256 -l LANG=C.UTF-8 -p 60001:60010"),
        "{lines:#?}"
    );
    assert!(
        lines[1].ends_with("exec mosh-server new -s -c 256 -l LANG=en_US.UTF-8 -p 60001:60010"),
        "{lines:#?}"
    );
    assert!(lines[0].starts_with("PATH=\"${PATH:+$PATH:}/usr/local/bin:/opt/homebrew/bin:/usr/bin\"; "));
}

#[test]
fn two_connect_records_are_an_error_not_a_guess() {
    let fixture = Fixture::new(Options {
        mosh: MoshServer::answering([MoshReply {
            stdout: format!("@nexus-mosh\n\r\nMOSH CONNECT 60001 {KEY}\r\nMOSH CONNECT 60002 {KEY}\r\n"),
            ..MoshReply::default()
        }]),
        ..with_password()
    });
    let profile = fixture.profile(json!({ "mosh": true }));
    open(&fixture, 1, &profile, false);
    sign_in(&fixture, 1);

    let warned = warning(&fixture, "mosh_unavailable", common::PATIENCE);
    assert!(
        warned["message"].as_str().unwrap().contains("multiple connect records"),
        "{warned}"
    );
    assert!(!warned["message"].as_str().unwrap().contains(KEY));
    connected(&fixture, 1, common::PATIENCE);
}

/// Ein `mosh-server`, dessen UDP nie zurückkommt (Firewall, VPN): Nach 15 s läuft das Terminal über SSH, und die
/// App erfährt warum. Die Pakete gehen dabei an die Adresse der SSH-Verbindung – und der Schlüssel steht in keinem
/// Ereignis, auch nicht im ausführlichen Protokoll.
#[test]
fn udp_that_never_arrives_falls_back_to_ssh_with_a_warning() {
    // Ein Port, an dem jemand liest, aber nie antwortet.
    let silent = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    silent.set_read_timeout(Some(Duration::from_secs(30))).unwrap();
    let port = silent.local_addr().unwrap().port();
    let fixture = Fixture::new(Options {
        mosh: MoshServer::answering([MoshReply {
            stdout: format!("Willkommen!\n@nexus-mosh\n\r\nMOSH CONNECT {port} {KEY}\r\n"),
            stderr: "mosh-server (mosh 1.4.0) [build mosh 1.4.0]\n\n[mosh-server detached, pid = 4242]\n".into(),
            status: 0,
        }]),
        ..with_password()
    });
    let profile = fixture.profile(json!({ "mosh": true }));
    let started = Instant::now();
    open(&fixture, 1, &profile, true);
    sign_in(&fixture, 1);

    // Mosh schickt zur Adresse der SSH-Verbindung.
    let mut datagram = [0_u8; 2048];
    let (_, from) = silent.recv_from(&mut datagram).unwrap();
    assert!(from.ip().is_loopback(), "{from}");

    let warned = warning(&fixture, "mosh_blocked", Duration::from_secs(40));
    assert!(started.elapsed() >= Duration::from_secs(14), "{:?}", started.elapsed());
    assert!(warned["message"].as_str().unwrap().contains("15 seconds"), "{warned}");
    connected(&fixture, 1, common::PATIENCE);
    assert!(!fixture.recorder.saw("session.mosh"));
    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"echo hallo\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nhallo\r\n"));

    let traced: Vec<String> = fixture
        .recorder
        .all("session.trace")
        .iter()
        .map(|event| event["text"].as_str().unwrap().to_owned())
        .collect();
    assert!(
        traced
            .iter()
            .any(|line| line.contains(&format!("mosh-server waits for UDP at 127.0.0.1:{port}"))),
        "{traced:#?}"
    );
    for name in ["session.trace", "core.warning", "session.state", "session.banner"] {
        for event in fixture.recorder.all(name) {
            assert!(!event.to_string().contains(KEY), "der Schlüssel steht in {event}");
        }
    }
}

#[test]
fn mosh_does_not_go_through_a_jump_host() {
    let fixture = Fixture::new(with_password());
    let bastion = fixture.profile(json!({ "name": "bastion" }));
    let response = fixture.request(json!({
        "op": "profiles.save",
        "profile": { "name": "x", "host": "127.0.0.1", "port": fixture.port, "user": "demo", "jump": bastion, "mosh": true },
    }));
    assert_eq!(response["ok"], false, "{response}");
    assert_eq!(response["message"], "mosh", "{response}");
}

// --- Mit einem echten mosh-server ------------------------------------------------------------------------------

/// Die Tests mit echtem Server laufen nacheinander.
static REAL: Mutex<()> = Mutex::new(());

struct Real {
    host: String,
    port: u16,
    user: String,
    key: String,
    script: Option<String>,
    container: Option<String>,
}

fn real() -> Option<Real> {
    let variable = |name: &str| std::env::var(name).ok().filter(|value| !value.is_empty());
    Some(Real {
        host: variable("NEXUS_REAL_MOSH_HOST")?,
        port: variable("NEXUS_REAL_MOSH_PORT")?.parse().ok()?,
        user: variable("NEXUS_REAL_MOSH_USER")?,
        key: variable("NEXUS_REAL_MOSH_KEY")?,
        script: variable("NEXUS_REAL_MOSH_SCRIPT"),
        container: variable("NEXUS_REAL_MOSH_CONTAINER"),
    })
}

impl Real {
    /// Ein Profil mit Mosh für den echten Server.
    fn profile(&self, fixture: &Fixture) -> String {
        fixture.profile(json!({
            "host": self.host, "port": self.port, "user": self.user, "key_path": self.key, "mosh": true,
            "mosh_ports": "60001:60010",
        }))
    }

    /// Verbindet und wartet, bis das Terminal über Mosh läuft.
    fn connect(&self, fixture: &Fixture, session: u64, profile: &str) {
        open(fixture, session, profile, true);
        fixture.trust_host_key();
        connected(fixture, session, Duration::from_secs(60));
        fixture.recorder.find("mosh running", |event| {
            event["event"] == "session.mosh" && event["session"] == session && event["state"] == "running"
        });
    }

    fn script(&self, arguments: &[&str]) -> std::process::Child {
        let script = self.script.as_deref().expect("NEXUS_REAL_MOSH_SCRIPT");
        std::process::Command::new(script).args(arguments).spawn().unwrap()
    }

    /// Ob im Container noch etwas an dieser UDP-Adresse lauscht – der `mosh-server` einer Sitzung. An der Adresse,
    /// nicht an der Zahl der `mosh-server`: Ein übrig gebliebener aus einem Rückfall endet nach 60 s von selbst,
    /// mitten in einem anderen Test, und derselbe Port kann an `127.0.0.1` und `::1` je einem gehören.
    fn listening(&self, address: &str) -> bool {
        let container = self.container.as_deref().expect("NEXUS_REAL_MOSH_CONTAINER");
        let output = std::process::Command::new("docker")
            .args(["exec", container, "ss", "-Huan", &format!("src {address}")])
            .output()
            .unwrap();
        !String::from_utf8_lossy(&output.stdout).trim().is_empty()
    }
}

/// Die UDP-Adresse, an der der `mosh-server` der Sitzung wartet, aus dem ausführlichen Protokoll.
fn mosh_address(fixture: &Fixture, session: u64) -> String {
    let traced = fixture.recorder.find("mosh address", |event| {
        event["event"] == "session.trace"
            && event["session"] == session
            && event["text"].as_str().unwrap().contains("mosh-server waits for UDP at")
    });
    let text = traced["text"].as_str().unwrap();
    text.rsplit(' ').next().unwrap().to_owned()
}

/// Tippt eine Zeile, deren Antwort nicht in ihr selbst steht, und wartet auf die Antwort.
fn echo(fixture: &Fixture, session: u64, marker: u32) {
    let line = format!("echo nexus-$(({marker}*7))\r");
    assert!(fixture.core.write(session, line.as_bytes()));
    fixture.recorder.output(session, &format!("nexus-{}", marker * 7));
}

#[test]
fn a_mosh_session_echoes_resizes_and_ends_with_the_shell() {
    let Some(real) = real() else { return };
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);

    echo(&fixture, 1, 6);
    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 100, "rows": 30 }));
    assert!(fixture.core.write(1, b"stty size\r"));
    fixture.recorder.output(1, "30 100");

    // Größer, als Mosh es kann: Der Kern begrenzt, statt dass die Sitzung scheitert.
    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 900, "rows": 400 }));
    assert!(fixture.core.write(1, b"stty size\r"));
    fixture.recorder.output(1, "200 500");

    assert!(fixture.core.write(1, b"exit\r"));
    let closed = fixture.recorder.find("closed", |event| {
        event["event"] == "session.state" && event["session"] == 1 && event["state"] == "closed"
    });
    assert_eq!(closed["reason"], "remote", "{closed}");
}

#[test]
fn closing_the_tab_ends_mosh_server_on_the_server() {
    let Some(real) = real() else { return };
    if real.container.is_none() {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);
    echo(&fixture, 1, 7);
    let address = mosh_address(&fixture, 1);
    assert!(real.listening(&address), "{address}");

    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    let closed = Instant::now();
    while real.listening(&address) {
        assert!(
            closed.elapsed() < Duration::from_secs(2),
            "mosh-server läuft noch nach {:?}",
            closed.elapsed()
        );
        std::thread::sleep(Duration::from_millis(100));
    }
}

/// Die App endet mit einem offenen Mosh-Tab: Auch dann bittet der Kern `mosh-server` um das Ende, bevor er geht –
/// sonst liefe der am Server weiter, ohne dass je wieder jemand kommt.
#[test]
fn ending_the_core_ends_mosh_server_too() {
    let Some(real) = real() else { return };
    if real.container.is_none() {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);
    echo(&fixture, 1, 15);
    let address = mosh_address(&fixture, 1);

    fixture.restart_core();
    let ended = Instant::now();
    while real.listening(&address) {
        assert!(
            ended.elapsed() < Duration::from_secs(2),
            "mosh-server läuft noch nach {:?}",
            ended.elapsed()
        );
        std::thread::sleep(Duration::from_millis(100));
    }
}

#[test]
fn udp_that_is_dropped_falls_back_to_ssh_with_a_warning() {
    let Some(real) = real() else { return };
    if real.script.is_none() {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    let mut mute = real.script(&["mute", "25"]);
    std::thread::sleep(Duration::from_secs(1));

    open(&fixture, 1, &profile, false);
    fixture.trust_host_key();
    let started = Instant::now();
    warning(&fixture, "mosh_blocked", Duration::from_secs(40));
    assert!(started.elapsed() < Duration::from_secs(20), "{:?}", started.elapsed());
    connected(&fixture, 1, common::PATIENCE);
    assert!(!fixture.recorder.saw("session.mosh"));
    echo(&fixture, 1, 8);

    let _ = mute.kill();
    let _ = mute.wait();
    let _ = real.script(&["unmute"]).wait();
}

#[test]
fn a_lost_ssh_link_leaves_the_terminal_running_and_comes_back() {
    let Some(real) = real() else { return };
    if real.script.is_none() {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);
    let home = connected(&fixture, 1, common::PATIENCE)["home"]
        .as_str()
        .unwrap()
        .to_owned();
    echo(&fixture, 1, 9);

    // Die SSH-Verbindung kappen: Das Terminal tippt weiter, die Dateien sind weg.
    let states = fixture.recorder.all("session.state").len();
    assert!(real.script(&["cut"]).wait().unwrap().success());
    fixture.recorder.find("link lost", |event| {
        event["event"] == "session.link_lost" && event["session"] == 1
    });
    echo(&fixture, 1, 10);
    let gone = fixture.request(json!({ "op": "sftp.list", "session": 1, "request": 1, "path": home }));
    assert_eq!(gone["ok"], false, "{gone}");

    // Mosh hört den Server: Die Verbindung kommt von selbst wieder, ohne Rückfrage, samt Dateien.
    let linked = fixture.recorder.find("linked", |event| {
        event["event"] == "session.linked" && event["session"] == 1
    });
    assert_eq!(linked["home"], home.as_str());
    // Für die App war die Sitzung die ganze Zeit verbunden: kein neuer Zustand.
    assert_eq!(fixture.recorder.all("session.state").len(), states);
    fixture.ok(json!({ "op": "sftp.list", "session": 1, "request": 2, "path": home }));
    let listed = fixture.recorder.find("listed", |event| {
        event["event"] == "sftp.result" && event["request"] == 2
    });
    assert_eq!(listed["ok"], true, "{listed}");
    echo(&fixture, 1, 11);
}

#[test]
fn thirty_seconds_without_network_leave_the_terminal_standing_and_ssh_comes_back() {
    let Some(real) = real() else { return };
    if real.script.is_none() {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);
    echo(&fixture, 1, 12);

    // Das Netz ist 30 s weg: UDP wird verworfen, und die SSH-Verbindung reißt dabei.
    let mut mute = real.script(&["mute", "30"]);
    let silent = fixture
        .recorder
        .find_within(Duration::from_secs(20), "silent", |event| {
            event["event"] == "session.mosh" && event["state"] == "silent"
        });
    assert!(silent["silent_for"].as_u64().unwrap() >= 5, "{silent}");
    assert!(real.script(&["cut"]).wait().unwrap().success());
    fixture
        .recorder
        .find("link lost", |event| event["event"] == "session.link_lost");

    // Getippt wird weiter; es kommt an, sobald der Weg wieder frei ist. Solange Mosh nichts hört, versucht der
    // Kern keine neue SSH-Verbindung.
    let line = b"echo nexus-$((13*7))\r";
    assert!(fixture.core.write(1, line));
    fixture
        .recorder
        .find_within(Duration::from_secs(12), "silent for 15 s", |event| {
            event["event"] == "session.mosh" && event["silent_for"].as_u64().is_some_and(|seconds| seconds >= 15)
        });
    let still = fixture.recorder.all("session.mosh");
    assert_eq!(still.last().unwrap()["state"], "silent", "{still:#?}");
    assert!(!fixture.recorder.saw("session.linked"));

    let _ = mute.wait();
    let heard = Instant::now();
    while fixture
        .recorder
        .all("session.mosh")
        .iter()
        .filter(|event| event["state"] == "running")
        .count()
        < 2
    {
        assert!(
            heard.elapsed() < Duration::from_secs(30),
            "Mosh hört den Server nicht wieder"
        );
        std::thread::sleep(Duration::from_millis(200));
    }
    fixture.recorder.output(1, "nexus-91");

    // Und die SSH-Verbindung kommt von selbst wieder, ohne Rückfrage.
    fixture
        .recorder
        .find("linked", |event| event["event"] == "session.linked");
    assert_eq!(fixture.recorder.all("prompt.host_key").len(), 1);
    assert!(
        !fixture
            .recorder
            .all("session.state")
            .iter()
            .any(|event| event["state"] == "closed")
    );
}

#[test]
fn over_ipv6_mosh_takes_the_same_address_as_ssh() {
    let Some(real) = real() else { return };
    if !real.host.contains(':') {
        return;
    }
    let _one = REAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let fixture = Fixture::new(with_password());
    let profile = real.profile(&fixture);
    real.connect(&fixture, 1, &profile);
    let traced = fixture.recorder.find("mosh address", |event| {
        event["event"] == "session.trace" && event["text"].as_str().unwrap().contains("mosh-server waits for UDP at")
    });
    let text = traced["text"].as_str().unwrap();
    assert!(text.contains(&format!("[{}]:600", real.host)), "{text}");
    echo(&fixture, 1, 14);
}
