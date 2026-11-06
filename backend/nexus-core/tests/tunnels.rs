//! Portweiterleitung: lokal, dynamisch (SOCKS) und entfernt – gegen den Testserver, der Verbindungen weiterreicht
//! und auf Wunsch selbst an einem Port lauscht.

mod common;

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::time::{Duration, Instant};

use common::{Fixture, with_password};
use nexus_testserver::Options;
use serde_json::{Value, json};

fn forwarding() -> Options {
    Options {
        forwarding: true,
        ..with_password()
    }
}

/// Ein Port, der gerade frei ist.
fn free_port() -> u16 {
    TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port()
}

/// Ein Dienst „hinter dem Server“: antwortet auf alles mit `echo:` und dem Empfangenen.
fn echo_service() -> u16 {
    let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { break };
            std::thread::spawn(move || {
                let mut buffer = [0_u8; 256];
                if let Ok(count) = stream.read(&mut buffer) {
                    let _ = stream.write_all(b"echo:");
                    let _ = stream.write_all(&buffer[..count]);
                }
            });
        }
    });
    port
}

fn connect(fixture: &Fixture, session: u64, profile: &str) {
    fixture.open(session, profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture.recorder.find("verbunden", |event| {
        event["state"] == "connected" && event["session"] == session
    });
}

/// Wartet, bis die letzte Meldung zu diesem Tunnel den Zustand nennt, und liefert sie.
fn wait_state(fixture: &Fixture, tunnel: &str, state: &str) -> Value {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        let last = fixture
            .recorder
            .all("tunnel.state")
            .into_iter()
            .rfind(|event| event["tunnel"] == tunnel);
        if let Some(event) = &last
            && event["state"] == state
        {
            return event.clone();
        }

        assert!(
            Instant::now() < deadline,
            "Tunnel {tunnel} wurde nicht {state}; zuletzt: {last:?}"
        );
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// Schickt `hello` durch eine Verbindung und liest die Antwort.
fn talk(mut stream: TcpStream) -> String {
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    stream.write_all(b"hello").unwrap();
    let mut answer = vec![0_u8; 10];
    stream.read_exact(&mut answer).unwrap();
    String::from_utf8(answer).unwrap()
}

fn is_closed(port: u16) -> bool {
    TcpStream::connect_timeout(&([127, 0, 0, 1], port).into(), Duration::from_secs(2)).is_err()
}

#[test]
fn a_local_tunnel_leads_to_a_service_behind_the_server_and_ends_with_the_session() {
    let fixture = Fixture::new(forwarding());
    let (service, here) = (echo_service(), free_port());
    let profile = fixture.profile(json!({ "tunnels": [
        { "kind": "local", "name": "Datenbank", "listen_port": here, "target_host": "127.0.0.1", "target_port": service, "auto": true },
        { "kind": "local", "listen_port": free_port(), "target_host": "127.0.0.1", "target_port": service, "auto": false },
    ] }));

    // Kennungen vergibt der Kern.
    let saved = fixture.ok(json!({ "op": "profiles.list" }));
    let tunnels = saved["profiles"][0]["tunnels"].as_array().unwrap().clone();
    assert_eq!(
        (tunnels[0]["id"].as_str(), tunnels[1]["id"].as_str()),
        (Some("t1"), Some("t2"))
    );

    connect(&fixture, 1, &profile);
    let state = wait_state(&fixture, "t1", "listening");
    assert_eq!(state["profile"], profile.as_str());
    assert_eq!(talk(TcpStream::connect(("127.0.0.1", here)).unwrap()), "echo:hello");

    // Wer „localhost“ als ::1 liest, kommt auch an – wo der Rechner IPv6 kennt.
    if let Ok(six) = TcpStream::connect(("::1", here)) {
        assert_eq!(talk(six), "echo:hello");
    }

    // Der zweite Tunnel startet nicht von selbst.
    let second = u16::try_from(tunnels[1]["listen_port"].as_u64().unwrap()).unwrap();
    assert!(is_closed(second));
    assert!(
        fixture
            .recorder
            .all("tunnel.state")
            .iter()
            .all(|event| event["tunnel"] != "t2")
    );

    // Mit der Sitzung endet der Tunnel, und der Port ist wieder frei.
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    wait_state(&fixture, "t1", "stopped");
    let deadline = Instant::now() + Duration::from_secs(5);
    while !is_closed(here) {
        assert!(Instant::now() < deadline, "der Port lauscht noch");
        std::thread::sleep(Duration::from_millis(50));
    }
}

#[test]
fn a_tunnel_is_switched_on_and_off_and_says_why_it_cannot_listen() {
    let fixture = Fixture::new(forwarding());
    let (service, here) = (echo_service(), free_port());
    let taken = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let profile = fixture.profile(json!({ "tunnels": [
        { "id": "db", "kind": "local", "listen_port": here, "target_host": "127.0.0.1", "target_port": service, "auto": false },
        { "id": "busy", "kind": "local", "listen_port": taken.local_addr().unwrap().port(), "target_host": "127.0.0.1", "target_port": service, "auto": true },
        { "id": "dead", "kind": "local", "listen_port": free_port(), "target_host": "127.0.0.1", "target_port": free_port(), "auto": true },
    ] }));
    connect(&fixture, 1, &profile);

    // Ein belegter Port: Der Tunnel steht nicht, und die Meldung sagt, warum.
    let failed = wait_state(&fixture, "busy", "failed");
    assert!(
        failed["message"].as_str().unwrap().contains("already in use"),
        "{failed}"
    );

    // Ein Ziel, an dem niemand lauscht: Der Tunnel steht, die Verbindung hindurch endet gleich.
    wait_state(&fixture, "dead", "listening");

    assert!(is_closed(here));
    fixture.ok(json!({ "op": "tunnel.set", "session": 1, "tunnel": "db", "on": true }));
    wait_state(&fixture, "db", "listening");
    assert_eq!(talk(TcpStream::connect(("127.0.0.1", here)).unwrap()), "echo:hello");

    // Noch einmal „an“ ändert nichts; „aus“ gibt den Port frei, „an“ belegt ihn wieder.
    fixture.ok(json!({ "op": "tunnel.set", "session": 1, "tunnel": "db", "on": true }));
    fixture.ok(json!({ "op": "tunnel.set", "session": 1, "tunnel": "db", "on": false }));
    wait_state(&fixture, "db", "stopped");
    assert!(is_closed(here));
    fixture.ok(json!({ "op": "tunnel.set", "session": 1, "tunnel": "db", "on": true }));
    wait_state(&fixture, "db", "listening");
    assert_eq!(talk(TcpStream::connect(("127.0.0.1", here)).unwrap()), "echo:hello");

    // Eine zweite Sitzung auf derselben Verbindung startet nichts doppelt.
    let before = fixture.recorder.all("tunnel.state").len();
    fixture
        .ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24, "share": 1 }));
    fixture.recorder.find("zweite verbunden", |event| {
        event["state"] == "connected" && event["session"] == 2
    });
    std::thread::sleep(Duration::from_millis(500));
    let after = fixture.recorder.all("tunnel.state");
    assert!(
        after[before..]
            .iter()
            .all(|event| event["state"] != "failed" || event["tunnel"] == "busy"),
        "{after:?}"
    );
    assert_eq!(talk(TcpStream::connect(("127.0.0.1", here)).unwrap()), "echo:hello");

    // Was es nicht gibt, lässt sich nicht starten.
    let unknown = fixture.request(json!({ "op": "tunnel.set", "session": 1, "tunnel": "nope", "on": true }));
    assert_eq!(
        (unknown["ok"].as_bool(), unknown["error"].as_str()),
        (Some(false), Some("unknown_tunnel"))
    );
    let nowhere = fixture.request(json!({ "op": "tunnel.set", "session": 9, "tunnel": "db", "on": true }));
    assert_eq!(nowhere["error"], "not_connected");
}

#[test]
fn a_dynamic_tunnel_is_a_socks_proxy_through_the_server() {
    let fixture = Fixture::new(forwarding());
    let (service, here) = (echo_service(), free_port());
    let profile = fixture
        .profile(json!({ "tunnels": [ { "id": "proxy", "kind": "dynamic", "listen_port": here, "auto": true } ] }));
    connect(&fixture, 1, &profile);
    wait_state(&fixture, "proxy", "listening");

    // SOCKS 5 mit einem Namen: Den löst der Server auf, nicht dieser Rechner.
    let mut five = TcpStream::connect(("127.0.0.1", here)).unwrap();
    five.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    five.write_all(&[5, 1, 0]).unwrap();
    let mut chosen = [0_u8; 2];
    five.read_exact(&mut chosen).unwrap();
    assert_eq!(chosen, [5, 0]);
    let mut request = vec![5, 1, 0, 3, 9];
    request.extend_from_slice(b"localhost");
    request.extend_from_slice(&service.to_be_bytes());
    five.write_all(&request).unwrap();
    let mut reply = [0_u8; 10];
    five.read_exact(&mut reply).unwrap();
    assert_eq!(reply[..2], [5, 0], "{reply:?}");
    assert_eq!(talk(five), "echo:hello");

    // SOCKS 4 mit einer Adresse.
    let mut four = TcpStream::connect(("127.0.0.1", here)).unwrap();
    four.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut request = vec![4, 1];
    request.extend_from_slice(&service.to_be_bytes());
    request.extend_from_slice(&[127, 0, 0, 1, 0]);
    four.write_all(&request).unwrap();
    let mut reply = [0_u8; 8];
    four.read_exact(&mut reply).unwrap();
    assert_eq!(reply[..2], [0, 0x5A]);
    assert_eq!(talk(four), "echo:hello");

    // Ein Ziel, das der Server nicht erreicht: Der Proxy sagt es, statt nur aufzulegen.
    let mut dead = TcpStream::connect(("127.0.0.1", here)).unwrap();
    dead.set_read_timeout(Some(Duration::from_secs(20))).unwrap();
    dead.write_all(&[5, 1, 0]).unwrap();
    dead.read_exact(&mut chosen).unwrap();
    let mut request = vec![5, 1, 0, 1, 127, 0, 0, 1];
    request.extend_from_slice(&free_port().to_be_bytes());
    dead.write_all(&request).unwrap();
    let mut reply = [0_u8; 10];
    dead.read_exact(&mut reply).unwrap();
    assert_eq!(reply[..2], [5, 5]);

    // Kein SOCKS: Die Verbindung endet, der Tunnel bleibt.
    let mut junk = TcpStream::connect(("127.0.0.1", here)).unwrap();
    junk.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    junk.write_all(b"GET / HTTP/1.1\r\n\r\n").unwrap();
    let mut rest = Vec::new();
    let _ = junk.read_to_end(&mut rest);
    assert!(rest.is_empty());
    assert!(!is_closed(here));
}

#[test]
fn a_remote_tunnel_brings_connections_from_the_server_here_and_nothing_else() {
    let fixture = Fixture::new(forwarding());
    let (service, there) = (echo_service(), free_port());
    let profile = fixture.profile(json!({ "tunnels": [
        { "id": "back", "kind": "remote", "listen_port": there, "target_host": "127.0.0.1", "target_port": service, "auto": true },
    ] }));
    connect(&fixture, 1, &profile);
    fixture.recorder.output(1, "$ ");
    wait_state(&fixture, "back", "listening");

    // Der Testserver lauscht jetzt an `there` und reicht hierher weiter.
    assert_eq!(talk(TcpStream::connect(("127.0.0.1", there)).unwrap()), "echo:hello");

    // Ein Kanal für einen Port, um den niemand gebeten hat, wird abgelehnt – der Server kommt nicht an andere
    // Dienste dieses Rechners.
    assert!(fixture.core.write(1, format!("forge {service}\r").as_bytes()));
    let answer = fixture.recorder.output(1, "\r\n$ ");
    assert!(answer.contains("forge: refused"), "{answer}");

    // Angehalten nimmt der Server die Bitte zurück; ein Kanal für den Port von eben gilt dann auch nicht mehr.
    fixture.ok(json!({ "op": "tunnel.set", "session": 1, "tunnel": "back", "on": false }));
    wait_state(&fixture, "back", "stopped");
    let deadline = Instant::now() + Duration::from_secs(5);
    while !is_closed(there) {
        assert!(Instant::now() < deadline, "der Server lauscht noch");
        std::thread::sleep(Duration::from_millis(50));
    }

    // Lehnt der Server das Lauschen ab (hier: der Port ist belegt), sagt die Meldung es.
    let taken = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let busy = fixture.profile(json!({ "name": "busy", "tunnels": [
        { "id": "busy", "kind": "remote", "listen_port": taken.local_addr().unwrap().port(), "target_host": "127.0.0.1", "target_port": service, "auto": true },
    ] }));
    fixture.ok(json!({ "op": "session.open", "session": 3, "profile_id": busy, "cols": 80, "rows": 24 }));
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    let failed = wait_state(&fixture, "busy", "failed");
    assert!(
        failed["message"].as_str().unwrap().contains("does not listen"),
        "{failed}"
    );
}

#[test]
fn a_profile_takes_only_tunnels_that_can_stand() {
    let fixture = Fixture::new(with_password());
    let save = |tunnels: Value| {
        fixture.request(json!({ "op": "profiles.save", "profile": {
            "name": "t", "host": "127.0.0.1", "port": 22, "user": "demo", "tunnels": tunnels,
        } }))
    };

    for bad in [
        json!([{ "kind": "local", "listen_port": 0, "target_host": "db", "target_port": 5432 }]),
        json!([{ "kind": "local", "listen_port": 5432, "target_host": "", "target_port": 5432 }]),
        json!([{ "kind": "local", "listen_port": 5432, "target_host": "db", "target_port": 0 }]),
        json!([{ "kind": "local", "listen_port": 5432, "target_host": "db; rm -rf /", "target_port": 5432 }]),
        json!([{ "kind": "remote", "listen_port": 8080, "target_host": "a b", "target_port": 80 }]),
        json!([{ "id": "bad id", "kind": "dynamic", "listen_port": 1080 }]),
        // Zweimal derselbe Port auf diesem Rechner, zweimal dieselbe Kennung.
        json!([{ "kind": "local", "listen_port": 1080, "target_host": "db", "target_port": 1 }, { "kind": "dynamic", "listen_port": 1080 }]),
        json!([{ "id": "a", "kind": "dynamic", "listen_port": 1080 }, { "id": "a", "kind": "dynamic", "listen_port": 1081 }]),
    ] {
        let response = save(bad.clone());
        assert_eq!(response["ok"], false, "{bad} -> {response}");
        assert_eq!(response["message"], "tunnels", "{response}");
    }

    // Derselbe Port hier und am Server stört sich nicht; ein dynamischer Tunnel hat kein Ziel.
    let fine = save(json!([
        { "kind": "local", "listen_port": 8080, "target_host": " intranet.example ", "target_port": 80, "name": " Web\u{7} " },
        { "kind": "remote", "listen_port": 8080, "target_host": "::1", "target_port": 3000, "open": true },
        { "id": "proxy", "kind": "dynamic", "listen_port": 1080, "target_host": "ignored", "target_port": 9 },
    ]));
    assert_eq!(fine["ok"], true, "{fine}");
    let tunnels = fine["profile"]["tunnels"].as_array().unwrap();
    assert_eq!(
        (
            tunnels[0]["id"].as_str(),
            tunnels[0]["target_host"].as_str(),
            tunnels[0]["name"].as_str()
        ),
        (Some("t1"), Some("intranet.example"), Some("Web"))
    );
    assert_eq!(
        (tunnels[1]["id"].as_str(), tunnels[1]["open"].as_bool()),
        (Some("t2"), Some(true))
    );
    assert_eq!(
        (tunnels[2]["target_host"].as_str(), tunnels[2]["target_port"].as_u64()),
        (Some(""), Some(0))
    );

    // Telnet, FTP und serielle Profile haben keine Tunnel.
    let telnet = fixture.ok(json!({ "op": "profiles.save", "profile": {
        "name": "alt", "host": "127.0.0.1", "port": 23, "protocol": "telnet",
        "tunnels": [{ "kind": "dynamic", "listen_port": 1080 }],
    } }));
    assert!(telnet["profile"].get("tunnels").is_none(), "{telnet}");
}
