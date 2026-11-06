//! X11-Forwarding: Der Testserver spielt ein grafisches Programm (Befehl `x11` seiner Shell), der Test den
//! X-Server dieses Rechners.

mod common;

use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::mpsc;
use std::time::Duration;

use common::{Fixture, with_password};
use serde_json::json;

fn connect(fixture: &Fixture, profile: &str) {
    fixture.open(1, profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.recorder.output(1, "$ ");
}

/// Der X-Server des Tests: nimmt Verbindungen an und meldet, was jede zuerst schickt. Wer etwas schickt, bekommt
/// eine Begrüßung zurück. (Ob dort jemand lauscht, prüft der Kern mit einer Verbindung, die nichts schickt.)
fn serve(listener: &TcpListener) -> mpsc::Receiver<Vec<u8>> {
    let listener = listener.try_clone().unwrap();
    let (seen, received) = mpsc::channel();
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { break };
            let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
            // Der Anfang (12 Bytes) und der Gruß des Programms können in zwei Stücken kommen. Erst alles lesen,
            // dann antworten: Wer mit ungelesenen Daten schließt, reißt die Verbindung ab, statt sie zu beenden.
            let mut bytes = Vec::new();
            let mut buffer = [0_u8; 256];
            while bytes.len() < 12 + b"hello from the server".len() {
                match stream.read(&mut buffer) {
                    Ok(count) if count > 0 => bytes.extend_from_slice(&buffer[..count]),
                    _ => break,
                }
            }

            if !bytes.is_empty() {
                let _ = stream.write_all(b"welcome to display 0");
                let _ = stream.shutdown(std::net::Shutdown::Write);
            }

            if seen.send(bytes).is_err() {
                break;
            }
        }
    });
    received
}

/// Die erste Verbindung, die etwas geschickt hat – oder `None`, wenn innerhalb der Frist keine kam.
fn first_with_data(received: &mpsc::Receiver<Vec<u8>>, within: Duration) -> Option<Vec<u8>> {
    loop {
        match received.recv_timeout(within) {
            Ok(bytes) if bytes.is_empty() => {}
            Ok(bytes) => return Some(bytes),
            Err(_) => return None,
        }
    }
}

#[test]
fn a_program_on_the_server_reaches_the_local_x_server_without_the_secret() {
    let fixture = Fixture::new(with_password());
    let display = serve(&fixture.x_server);
    let profile = fixture.profile(json!({ "forward_x11": true }));
    connect(&fixture, &profile);

    assert!(fixture.core.write(1, b"x11\r"));
    let answer = fixture.recorder.output(1, "\r\n$ ");
    assert!(
        answer.contains("x11: welcome to display 0"),
        "{answer:?} {:?}",
        fixture.recorder.all("core.warning")
    );

    // Der X-Server sieht den Anfang ohne Anmeldung – das Geheimnis galt nur der Strecke zum Server – und danach,
    // was das Programm geschickt hat.
    let bytes = first_with_data(&display, Duration::from_secs(5)).expect("the X server saw a connection");
    assert_eq!(bytes[..12], [b'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    assert_eq!(&bytes[12..], b"hello from the server");
    assert!(!fixture.recorder.saw("core.warning"));
}

#[test]
fn a_connection_with_the_wrong_secret_never_reaches_the_x_server() {
    let fixture = Fixture::new(with_password());
    let display = serve(&fixture.x_server);
    let profile = fixture.profile(json!({ "forward_x11": true }));
    connect(&fixture, &profile);

    assert!(fixture.core.write(1, b"x11 wrong\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("x11: closed"));

    assert_eq!(first_with_data(&display, Duration::from_millis(500)), None);
}

#[test]
fn without_permission_in_the_profile_the_server_cannot_open_x11_connections() {
    // Der Server versucht es ungefragt – das Profil erlaubt es nicht, also lehnt der Kern den Kanal ab.
    let fixture = Fixture::new(with_password());
    let display = serve(&fixture.x_server);
    let profile = fixture.profile(json!({}));
    connect(&fixture, &profile);

    assert!(fixture.core.write(1, b"x11\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("x11: refused"));

    assert_eq!(first_with_data(&display, Duration::from_millis(500)), None);
}

#[test]
fn forwarding_without_an_x_server_warns_and_still_connects() {
    let mut fixture = Fixture::new(with_password());

    // Dort, wo der Kern den X-Server sucht, lauscht niemand mehr.
    drop(std::mem::replace(
        &mut fixture.x_server,
        TcpListener::bind(("127.0.0.1", 0)).unwrap(),
    ));
    let profile = fixture.profile(json!({ "forward_x11": true }));
    connect(&fixture, &profile);

    let warning = fixture
        .recorder
        .find("Warnung", |event| event["event"] == "core.warning");
    assert_eq!(warning["code"], "x11_unavailable", "{warning}");
    assert!(fixture.core.write(1, b"echo still here\r"));
    fixture.recorder.output(1, "still here");
}

fn variable(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

/// X11-Forwarding mit echten Gegenstellen: ein OpenSSH-Server mit `xauth`, echte Programme dort (`xdpyinfo`,
/// `xclock`) und ein echter X-Server, zu dem der Kern ihre Verbindungen durchreicht. Läuft nur, wenn die Umgebung
/// beides nennt: `NEXUS_REAL_HOST`, `NEXUS_REAL_USER`, `NEXUS_REAL_KEY`, `NEXUS_REAL_FINGERPRINT` (wie in
/// `tests/sftp.rs`) und `NEXUS_REAL_XSERVER` – die Adresse des X-Servers (`127.0.0.1:6000` für VcXsrv oder Xming
/// mit Display 0). Er muss Verbindungen von diesem Rechner ohne Anmeldung annehmen.
#[test]
fn real_programs_draw_on_a_real_x_server_through_a_real_sshd() {
    let (Some(host), Some(user), Some(key), Some(fingerprint), Some(x_server)) = (
        variable("NEXUS_REAL_HOST"),
        variable("NEXUS_REAL_USER"),
        variable("NEXUS_REAL_KEY"),
        variable("NEXUS_REAL_FINGERPRINT"),
        variable("NEXUS_REAL_XSERVER"),
    ) else {
        return;
    };

    // Der Kern sucht den X-Server an der Adresse des Gerüsts; von dort geht jede Verbindung weiter zum echten.
    let fixture = Fixture::new(with_password());
    let listener = fixture.x_server.try_clone().unwrap();
    std::thread::spawn(move || {
        for incoming in listener.incoming() {
            let (Ok(near), Ok(far)) = (incoming, std::net::TcpStream::connect(&x_server)) else {
                continue;
            };
            let (mut near_in, mut far_out) = (near.try_clone().unwrap(), far.try_clone().unwrap());
            let (mut far_in, mut near_out) = (far, near);
            std::thread::spawn(move || {
                let _ = std::io::copy(&mut near_in, &mut far_out);
                let _ = far_out.shutdown(std::net::Shutdown::Write);
            });
            std::thread::spawn(move || {
                let _ = std::io::copy(&mut far_in, &mut near_out);
                let _ = near_out.shutdown(std::net::Shutdown::Write);
            });
        }
    });

    let profile = fixture.profile(json!({
        "host": host, "port": 22, "user": user, "key_path": key, "use_agent": false, "forward_x11": true,
    }));
    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["fingerprint"], fingerprint.as_str(), "{prompt}");
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    assert!(
        !fixture.recorder.saw("core.warning"),
        "{:?}",
        fixture.recorder.all("core.warning")
    );

    // Die Rechnung im Befehl steht so nicht in seinem Echo: Was auf „-42“ endet, ist die Antwort.
    let typed = b"echo \"display=[$DISPLAY]\"; xdpyinfo >/dev/null 2>&1 && echo X11-OK-$((40+2)) || echo X11-FAILED-$((40+2))\r";
    assert!(fixture.core.write(1, typed));
    let answer = fixture.recorder.output(1, "-42");
    assert!(answer.contains("X11-OK-42"), "{answer}");
    // sshd legt das Display hinter seinem eigenen Versatz an (X11DisplayOffset, üblich 10).
    assert!(answer.contains("display=[localhost:1"), "{answer}");

    // Ein Programm mit Fenster: Es steht danach im Baum des X-Servers – gefragt wird wieder über das Forwarding.
    let typed =
        b"xclock & sleep 2; echo WINDOWS-$(xwininfo -root -tree 2>/dev/null | grep -c xclock)-$((40+3)); kill %1\r";
    assert!(fixture.core.write(1, typed));
    let answer = fixture.recorder.output(1, "-43");
    assert!(
        !answer.contains("WINDOWS-0-43") && answer.contains("WINDOWS-"),
        "{answer}"
    );
}

#[test]
fn a_jump_host_never_gets_x11() {
    // Wie beim Agenten: Sprung-Server und Ziel sind derselbe Testserver, der ungefragt eine X11-Verbindung öffnet.
    // Erlaubt ist das Forwarding nur im Profil des Sprung-Servers – für die Verbindung dorthin gilt es trotzdem
    // nicht, weil sie hier nur als Sprung dient; das Ziel hat keine Erlaubnis.
    let fixture = Fixture::new(nexus_testserver::Options {
        authorized_keys: common::authorized(),
        forwarding: true,
        ..nexus_testserver::Options::default()
    });
    fixture.start_agent(&[common::user_key()]);
    let display = serve(&fixture.x_server);
    let bastion = fixture.profile(json!({ "name": "bastion", "use_agent": true, "forward_x11": true }));
    let target = fixture.profile(json!({ "name": "target", "use_agent": true, "jump": bastion }));

    fixture.ok(json!({ "op": "session.open", "session": 1, "profile_id": target, "cols": 80, "rows": 24 }));
    fixture.trust_host_key();
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.recorder.output(1, "$ ");

    assert!(fixture.core.write(1, b"x11\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("x11: refused"));
    assert_eq!(first_with_data(&display, Duration::from_millis(500)), None);
}

#[test]
fn the_display_of_the_local_x_server_can_be_set() {
    // Wo niemand lauscht, gibt es kein Forwarding – und die Warnung nennt die Adresse, die aus dem Display wird.
    let fixture = Fixture::new(with_password());
    fixture.ok(json!({ "op": "x11.display", "display": 57 }));
    let profile = fixture.profile(json!({ "forward_x11": true }));
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    let warning = fixture
        .recorder
        .find("Warnung", |event| event["event"] == "core.warning");
    assert_eq!(warning["code"], "x11_unavailable", "{warning}");
    assert!(
        warning["message"].as_str().unwrap().ends_with("127.0.0.1:6057"),
        "{warning}"
    );

    // Mehr als 63 Displays gibt es nicht.
    let refused = fixture.request(json!({ "op": "x11.display", "display": 64 }));
    assert_eq!(refused["ok"], false, "{refused}");
}
