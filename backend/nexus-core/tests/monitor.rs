//! Auslastung des Servers: Der Kern lässt auf einem zweiten Kanal eine Schleife laufen und meldet ihre Messungen.

mod common;

use std::time::{Duration, Instant};

use common::{Fixture, PATIENCE, with_password};
use nexus_testserver::{Monitor, Options};
use serde_json::json;

fn connected(monitor: Monitor) -> Fixture {
    let fixture = Fixture::new(Options {
        monitor,
        ..with_password()
    });
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture
}

#[test]
fn a_linux_server_reports_load_while_monitoring_is_on() {
    let fixture = connected(Monitor::Linux);
    assert!(
        !fixture.recorder.saw("monitor.stats"),
        "ohne monitor.set fragt der Kern nichts ab"
    );

    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    let stats = fixture.recorder.event("monitor.stats");
    assert_eq!(stats["session"], 1);
    assert!((20..=60).contains(&stats["cpu"].as_u64().unwrap()), "{stats}");
    assert_eq!(stats["memory_total"], 8_000_000u64 * 1024);
    assert!(stats["memory_used"].as_u64().unwrap() >= 3_000_000 * 1024);
    assert_eq!(stats["disk_total"], 100_000_000u64 * 1024);
    assert_eq!(stats["disk_used"], 28_000_000u64 * 1024);
    assert_eq!(stats["os"], "Ubuntu 24.04.1 LTS");
    assert_eq!(stats["kernel"], "Linux 6.8.0-45-generic");
    assert!(stats["uptime"].as_u64().unwrap() >= 2_764_800);
    assert_eq!(stats["load"], "0.42 0.31 0.25");

    // Was der Rechner ist, und seine Uhr (die des Testservers geht zwei Minuten vor).
    assert_eq!(
        (
            &stats["cpu_model"],
            &stats["core_count"],
            &stats["machine"],
            &stats["virtualization"]
        ),
        (
            &json!("Intel(R) Xeon(R) Gold 6140 CPU @ 2.30GHz"),
            &json!(4),
            &json!("QEMU Standard PC (Q35 + ICH9, 2009)"),
            &json!("kvm")
        ),
        "{stats}"
    );
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs();
    assert!(
        (now + 100..=now + 125).contains(&stats["time"].as_u64().unwrap()),
        "{stats}"
    );
    assert_eq!((&stats["utc_offset"], &stats["zone"]), (&json!(19_800), &json!("IST")));

    // Jeder Kern einzeln, und woraus die Auslastung besteht.
    let cpu = stats["cpu"].as_i64().unwrap();
    let cores: Vec<i64> = stats["cores"]
        .as_array()
        .unwrap()
        .iter()
        .map(|core| core.as_i64().unwrap())
        .collect();
    assert_eq!(cores, [cpu + 10, cpu - 10, cpu + 5, cpu - 5], "{stats}");
    assert_eq!(
        (&stats["cpu_system"], &stats["cpu_iowait"], &stats["cpu_steal"]),
        (&json!(cpu / 4), &json!(1), &json!(1)),
        "{stats}"
    );
    // Benutzer, System und Gestohlenes ergeben zusammen die Auslastung – bis auf das Abrunden der Anteile.
    let parts = stats["cpu_user"].as_i64().unwrap() + cpu / 4 + 1;
    assert!((cpu - 2..=cpu).contains(&parts), "{stats}");

    // Alle Dateisysteme – ohne die, die nur im Speicher liegen oder ein Abbild zeigen –, mit ihren Inodes.
    assert_eq!(
        stats["disks"],
        json!([
            { "mount": "/", "device": "/dev/sda1", "used": 28_000_000u64 * 1024, "total": 100_000_000u64 * 1024,
              "inodes_used": 250_000, "inodes_total": 6_400_000 },
            { "mount": "/srv/data", "device": "/dev/sdb1", "used": 470_000_000u64 * 1024,
              "total": 500_000_000u64 * 1024, "inodes_used": 990_000, "inodes_total": 1_000_000 },
        ])
    );
    assert_eq!(
        stats["net_rx"], 150_000,
        "Bytes je Sekunde, ohne die Schleifenschnittstelle"
    );
    assert_eq!(stats["net_tx"], 50_000);
    assert_eq!(
        stats["interfaces"],
        json!([{ "name": "eth0", "rx": 150_000, "tx": 50_000, "addresses": ["192.0.2.10", "2001:db8::10"] }]),
        "per interface, with its addresses – not lo, not the link-local one"
    );
    assert_eq!(
        (&stats["io_read"], &stats["io_write"], &stats["io_busy"]),
        (&json!(2 * 1024 * 1024), &json!(512 * 1024), &json!(25)),
        "the disk once – not its partition again, not the loop device"
    );
    assert_eq!(
        stats["io_disks"],
        json!([{ "name": "sda", "read": 2 * 1024 * 1024, "write": 512 * 1024, "busy": 25 }]),
        "and the same per disk"
    );
    assert!(stats.get("io_mixed").is_none(), "Linux names reading and writing apart");

    // Abgeschaltet kommen keine Messungen mehr; wieder eingeschaltet beginnt eine neue Reihe.
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": false }));
    std::thread::sleep(Duration::from_millis(300));
    let before = fixture.recorder.all("monitor.stats").len();
    std::thread::sleep(Duration::from_millis(1500));
    assert_eq!(fixture.recorder.all("monitor.stats").len(), before);

    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    let deadline = Instant::now() + PATIENCE;
    while fixture.recorder.all("monitor.stats").len() <= before {
        assert!(
            Instant::now() < deadline,
            "nach dem Wiedereinschalten kam keine Messung"
        );
        std::thread::sleep(Duration::from_millis(50));
    }
}

#[test]
fn the_shell_keeps_working_next_to_the_monitor() {
    let fixture = connected(Monitor::Linux);
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    fixture.recorder.event("monitor.stats");

    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"echo nebenher\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nnebenher\r\n"));
}

#[test]
fn a_server_without_proc_is_reported_once_as_unavailable() {
    let fixture = connected(Monitor::NoProc);
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));

    let event = fixture.recorder.event("monitor.unavailable");
    assert_eq!(event["session"], 1);
    assert!(event.get("retrying").is_none(), "{event}");
    assert!(!fixture.recorder.saw("monitor.stats"));

    // Der Kern versucht es nicht von selbst wieder.
    std::thread::sleep(Duration::from_millis(800));
    assert_eq!(fixture.recorder.all("monitor.unavailable").len(), 1);
}

#[test]
fn a_server_that_refuses_commands_is_unavailable_and_the_session_stays() {
    let fixture = connected(Monitor::Refused);
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    fixture.recorder.event("monitor.unavailable");

    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"echo lebt\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nlebt\r\n"));
}

/// Verstummt ein Server, der schon Werte geliefert hat (ein hängendes `df`), versucht es der Kern nach einer Pause
/// neu – und die Werte kommen wieder. Dauert gut eine Minute (20 s Stille, 30 s Pause), läuft nur mit `--ignored`.
#[test]
#[ignore = "dauert gut eine Minute"]
fn a_server_that_fell_silent_is_measured_again_after_a_pause() {
    let fixture = connected(Monitor::Linux);
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    fixture.recorder.event("monitor.stats");
    fixture.recorder.output(1, "$ ");
    assert!(fixture.core.write(1, b"hush 25\r"));

    let wait = |what: &str, done: &dyn Fn() -> bool, seconds: u64| {
        let until = Instant::now() + Duration::from_secs(seconds);
        while !done() {
            assert!(Instant::now() < until, "nicht rechtzeitig: {what}");
            std::thread::sleep(Duration::from_millis(200));
        }
    };
    wait("stumm", &|| fixture.recorder.saw("monitor.unavailable"), 40);
    let event = fixture.recorder.event("monitor.unavailable");
    assert_eq!(event["retrying"], true, "{event}");

    let before = fixture.recorder.all("monitor.stats").len();
    wait(
        "wieder Werte",
        &|| fixture.recorder.all("monitor.stats").len() > before,
        50,
    );
}

#[test]
fn a_server_without_a_free_channel_names_max_sessions() {
    // Shell und Dateien belegen alles: Die Messung bekommt keinen Kanal (so an einem Proxmox des Users, 2026-10-29).
    let fixture = Fixture::with(|directory| {
        std::fs::create_dir_all(directory.join("remote")).unwrap();
        Options {
            monitor: Monitor::Linux,
            max_sessions: Some(2),
            sftp_root: Some(directory.join("remote")),
            ..with_password()
        }
    });
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");

    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));

    let event = fixture.recorder.event("monitor.unavailable");
    let message = event["message"].as_str().unwrap();
    assert!(message.contains("MaxSessions"), "{event}");
}

#[test]
fn a_command_gets_a_channel_while_the_monitor_holds_the_last_one() {
    // MaxSessions 3 (so an einem abgesicherten Server, 2026-10-24): Shell, Dateien und Messung belegen alle drei.
    // Die Messung macht dem Befehl Platz und misst danach weiter.
    let fixture = Fixture::with(|directory| {
        std::fs::create_dir_all(directory.join("remote").join("www")).unwrap();
        Options {
            monitor: Monitor::Linux,
            max_sessions: Some(3),
            sftp_root: Some(directory.join("remote")),
            ..with_password()
        }
    });
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();
    let secret = fixture.recorder.event("prompt.secret");
    fixture.answer(&secret, json!({ "values": ["geheim"] }));
    fixture
        .recorder
        .find("verbunden", |event| event["state"] == "connected");
    fixture.ok(json!({ "op": "monitor.set", "session": 1, "enabled": true }));
    fixture.recorder.event("monitor.stats");

    fixture.ok(json!({ "op": "sftp.usage", "session": 1, "request": 7, "path": "/www" }));
    let usage = fixture.recorder.find("sftp.result", |event| {
        event["event"] == "sftp.result" && event["request"] == 7
    });
    assert_eq!(usage["ok"], true, "{usage}");

    let before = fixture.recorder.all("monitor.stats").len();
    let until = Instant::now() + PATIENCE;
    while fixture.recorder.all("monitor.stats").len() <= before {
        assert!(Instant::now() < until, "die Messung läuft danach nicht weiter");
        std::thread::sleep(Duration::from_millis(200));
    }
    assert!(!fixture.recorder.saw("monitor.unavailable"));
}

#[test]
fn monitoring_an_unknown_session_is_an_error() {
    let fixture = Fixture::new(with_password());
    let response = fixture.request(json!({ "op": "monitor.set", "session": 9, "enabled": true }));
    assert_eq!(response["error"], "unknown_session");
}
