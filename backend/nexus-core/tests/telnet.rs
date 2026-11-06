//! Telnet-Sitzungen gegen einen kleinen Server im Test: Aushandlung, Ein- und Ausgabe, Größe, Ende.

mod common;

use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::mpsc;
use std::time::Duration;

use common::{Fixture, with_password};
use serde_json::json;

const IAC: u8 = 255;

/// Ein Telnet-Server für genau eine Verbindung: begrüßt, schickt weiter, was der Test ihm gibt, und meldet,
/// was er empfängt. Lässt der Test `send` fallen, schließt er die Verbindung.
struct Server {
    port: u16,
    send: Option<mpsc::Sender<Vec<u8>>>,
    received: mpsc::Receiver<Vec<u8>>,
    seen: Vec<u8>,
}

impl Server {
    fn start(greeting: &'static [u8]) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let (send, outgoing) = mpsc::channel::<Vec<u8>>();
        let (incoming, received) = mpsc::channel();
        std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream.write_all(greeting).unwrap();
            let mut reader = stream.try_clone().unwrap();
            std::thread::spawn(move || {
                let mut buffer = [0_u8; 1024];
                while let Ok(count) = reader.read(&mut buffer) {
                    if count == 0 || incoming.send(buffer[..count].to_vec()).is_err() {
                        break;
                    }
                }
            });

            // Endet der Sender, endet die Schleife – und mit ihr die Verbindung.
            for bytes in outgoing {
                if stream.write_all(&bytes).is_err() {
                    break;
                }
            }

            let _ = stream.shutdown(std::net::Shutdown::Both);
        });

        Self {
            port,
            send: Some(send),
            received,
            seen: Vec::new(),
        }
    }

    /// Wartet, bis der Server diese Bytes am Stück empfangen hat.
    fn expect(&mut self, needle: &[u8]) {
        while !self.seen.windows(needle.len()).any(|window| window == needle) {
            let more = self
                .received
                .recv_timeout(Duration::from_secs(10))
                .unwrap_or_else(|_| panic!("the server never received {needle:?}; so far: {:?}", self.seen));
            self.seen.extend(more);
        }
    }
}

#[test]
fn a_telnet_session_negotiates_types_and_ends_with_the_server() {
    // DO TERMINAL-TYPE, DO WINDOW-SIZE, WILL ECHO – und dann die Frage nach dem Namen.
    let mut server = Server::start(&[
        IAC, 253, 24, IAC, 253, 31, IAC, 251, 1, b'l', b'o', b'g', b'i', b'n', b':', b' ',
    ]);
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({ "name": "switch", "protocol": "telnet", "port": server.port, "user": "" }));

    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": profile, "cols": 100, "rows": 30 }));

    // Keine Rückfrage, keine Anmeldung: Die Verbindung steht, sobald sie steht.
    fixture.recorder.state("connecting");
    fixture.recorder.state("connected");
    fixture.recorder.output(1, "login: ");
    assert!(!fixture.recorder.saw("prompt.host_key"));
    assert!(!fixture.recorder.saw("prompt.secret"));

    // Zugesagt mit der Größe von jetzt, und das Echo dem Server überlassen.
    server.expect(&[IAC, 251, 24]);
    server.expect(&[IAC, 251, 31, IAC, 250, 31, 0, 100, 0, 30, IAC, 240]);
    server.expect(&[IAC, 253, 1]);

    // Getipptes kommt an – der Wagenrücklauf mit NUL dahinter, wie Telnet es will.
    assert!(fixture.core.write(1, b"admin\r"));
    server.expect(b"admin\r\0");

    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 120, "rows": 40 }));
    server.expect(&[IAC, 250, 31, 0, 120, 0, 40, IAC, 240]);

    // Was der Server schickt, steht im Terminal; seine Befehle nicht.
    server
        .send
        .as_ref()
        .unwrap()
        .send(vec![b'P', b'W', IAC, 241, b':'])
        .unwrap();
    fixture.recorder.output(1, "PW:");

    // Dateien gibt es über Telnet nicht, und gemessen wird auch nichts.
    let listing = fixture.request(json!({ "op": "sftp.list", "session": 1, "request": 1, "path": "/" }));
    assert_eq!(listing["ok"], false, "{listing}");
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    fixture.recorder.event("monitor.unavailable");

    // Der Server legt auf: Die Sitzung ist zu Ende, nicht abgerissen.
    server.send = None;
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "remote", "{closed}");
}

#[test]
fn a_telnet_server_that_is_not_there_is_a_failed_connection() {
    // Ein Port, an dem gerade noch jemand lauschte und jetzt niemand mehr.
    let port = TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({ "protocol": "telnet", "port": port, "user": "" }));

    fixture.open(1, &profile);

    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connect_failed", "{closed}");
}

#[test]
fn only_ssh_profiles_need_a_user_and_telnet_ones_take_no_jump_host() {
    let fixture = Fixture::new(with_password());

    let missing = fixture
        .request(json!({ "op": "profiles.save", "profile": { "name": "a", "host": "h", "port": 22, "user": "" } }));
    assert_eq!(missing["ok"], false, "{missing}");

    let jump = fixture.profile(json!({ "name": "bastion" }));
    let saved = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "switch", "host": "10.0.0.2", "port": 23, "user": "", "protocol": " Telnet ",
        "jump": jump, "key_path": "C:/keys/id", "forward_agent": true,
    } }));
    assert_eq!(saved["profile"]["protocol"], "telnet", "{saved}");
    assert_eq!(saved["profile"]["jump"], json!(null), "{saved}");
    assert_eq!(saved["profile"]["key_path"], json!(null), "{saved}");
    assert_eq!(saved["profile"]["forward_agent"], false, "{saved}");

    // Ein Protokoll, das es nicht gibt, ist SSH.
    let other = fixture.ok(json!({ "op": "profiles.save", "profile": { "name": "b", "host": "h", "port": 22, "user": "u", "protocol": "rlogin" } }));
    assert_eq!(other["profile"]["protocol"], json!(null), "{other}");
}

#[test]
fn a_serial_profile_names_a_port_and_a_speed_and_a_missing_port_is_a_failed_connection() {
    let fixture = Fixture::new(with_password());

    // Kein Benutzer nötig; die Geschwindigkeit bleibt, alles, was SSH voraussetzt, fällt weg.
    let saved = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "konsole", "host": "COM250", "port": 22, "user": "", "protocol": "Serial", "baud": 115200,
        "key_path": "C:/keys/id", "forward_x11": true,
    } }));
    assert_eq!(saved["profile"]["protocol"], "serial", "{saved}");
    assert_eq!(saved["profile"]["baud"], 115200, "{saved}");
    assert_eq!(saved["profile"]["key_path"], json!(null), "{saved}");
    assert_eq!(saved["profile"]["forward_x11"], false, "{saved}");

    // Eine Geschwindigkeit, die keine ist, wird zur üblichen; zu SSH gehört gar keine.
    let odd = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "odd", "host": "COM250", "port": 22, "user": "", "protocol": "serial", "baud": 7,
    } }));
    assert_eq!(odd["profile"]["baud"], json!(null), "{odd}");
    let ssh = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "ssh", "host": "h", "port": 22, "user": "u", "baud": 9600,
    } }));
    assert_eq!(ssh["profile"]["baud"], json!(null), "{ssh}");

    // Eine Schnittstelle, die es nicht gibt.
    fixture.open(1, saved["profile"]["id"].as_str().unwrap());
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connect_failed", "{closed}");

    // Was kein COM-Anschluss ist, wird gar nicht erst geöffnet – auch kein anderes Gerät des Rechners.
    let device = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "platte", "host": "PhysicalDrive0", "port": 22, "user": "", "protocol": "serial",
    } }));
    fixture.open(2, device["profile"]["id"].as_str().unwrap());
    let refused = fixture.recorder.find("abgelehnt", |event| {
        event["event"] == "session.state" && event["session"] == 2 && event["state"] == "closed"
    });
    assert_eq!(refused["reason"], "connect_failed", "{refused}");
    assert!(
        refused["message"].as_str().unwrap().contains("is not a serial port"),
        "{refused}"
    );
}

/// Telnet an einem echten Dienst (etwa `telnetd` von BusyBox, wie es Router und Switches mitbringen): die
/// Aushandlung, die Anmeldung von Hand, die Größe des Terminals und das Ende. Läuft nur, wenn die Umgebung einen
/// nennt: `NEXUS_REAL_TELNET` (`host:port`), `NEXUS_REAL_TELNET_USER` und `NEXUS_REAL_TELNET_PASSWORD`.
#[test]
fn a_real_telnet_service_logs_in_knows_the_size_of_the_terminal_and_ends_cleanly() {
    let variable = |name: &str| std::env::var(name).ok().filter(|value| !value.is_empty());
    let (Some(address), Some(user), Some(password)) = (
        variable("NEXUS_REAL_TELNET"),
        variable("NEXUS_REAL_TELNET_USER"),
        variable("NEXUS_REAL_TELNET_PASSWORD"),
    ) else {
        return;
    };
    let (host, port) = address.rsplit_once(':').expect("host:port");
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(
        json!({ "name": "device", "protocol": "telnet", "host": host, "port": port.parse::<u16>().unwrap(), "user": "" }),
    );

    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": profile, "cols": 100, "rows": 30 }));
    fixture.recorder.state("connecting");
    fixture.recorder.state("connected");

    // Die Anmeldung gehört dem Gerät: Es fragt, getippt wird im Terminal.
    fixture.recorder.output(1, "login: ");
    assert!(fixture.core.write(1, format!("{user}\r").as_bytes()));
    fixture.recorder.output(1, "assword: ");
    assert!(fixture.core.write(1, format!("{password}\r").as_bytes()));

    // Die Rechnung steht so nicht im Echo des Befehls: Was auf „-42“ endet, ist die Antwort der Shell.
    assert!(fixture.core.write(1, b"echo size=$(stty size)-$((40+2))\r"));
    let answer = fixture.recorder.output(1, "-42");
    assert!(answer.contains("size=30 100-42"), "{answer:?}");

    // Eine neue Größe kommt beim Gerät an (NAWS).
    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 120, "rows": 40 }));
    assert!(fixture.core.write(1, b"echo size=$(stty size)-$((40+3))\r"));
    let answer = fixture.recorder.output(1, "-43");
    assert!(answer.contains("size=40 120-43"), "{answer:?}");

    // Getipptes steht genau einmal da: Das Echo macht das Gerät, nicht der Kern.
    assert!(fixture.core.write(1, b"echo once-$((40+4))\r"));
    let answer = fixture.recorder.output(1, "once-44");
    assert_eq!(answer.matches("echo once-$((40+4))").count(), 1, "{answer:?}");

    assert!(fixture.core.write(1, b"exit\r"));
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "remote", "{closed}");
}
