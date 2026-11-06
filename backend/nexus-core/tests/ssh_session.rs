//! Der Kern gegen einen echten SSH-Server im selben Prozess (`nexus-testserver`): Verbindungsaufbau,
//! Host-Schlüssel, Anmeldung, Shell – so, wie die App ihn benutzt, nur ohne die C-Hülle.

mod common;

use common::{Fixture, authorized, user_key, with_password};
use nexus_core::secrets::{SecretStore, passphrase_key, password_key};
use nexus_testserver::{Options, host_key};
use russh::keys::ssh_key::LineEnding;
use serde_json::json;
#[test]
fn a_password_login_runs_from_host_key_prompt_to_exit() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);

    // Unbekannter Server: Rückfrage mit dem Fingerabdruck des Testservers.
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["host"], "127.0.0.1");
    assert_eq!(prompt["port"], fixture.port);
    assert_eq!(prompt["algorithm"], "ssh-ed25519");
    assert_eq!(prompt["changed"], false);
    assert_eq!(
        prompt["fingerprint"],
        nexus_core::hostkeys::fingerprint(host_key(1).public_key())
    );
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture.recorder.state("authenticating");

    // Falsches Passwort: zweite Rückfrage mit erhöhtem Zähler.
    let first = fixture.recorder.event("prompt.secret");
    assert_eq!(first["kind"], "password");
    assert_eq!(first["user"], "demo");
    assert_eq!(first["attempt"], 1);
    assert_eq!(first["can_save"], true);
    fixture.answer(&first, json!({ "values": ["falsch"], "save": true }));

    let second = fixture.recorder.event("prompt.secret");
    assert_eq!(second["attempt"], 2);
    fixture.answer(&second, json!({ "values": ["geheim"], "save": true }));

    fixture.recorder.state("connected");
    fixture.recorder.output(1, "$ ");

    // Nur das richtige Passwort wurde gespeichert, und der Server ist jetzt bekannt.
    assert_eq!(fixture.secrets.get(&password_key(&profile)).as_deref(), Some("geheim"));
    assert_eq!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Trusted
    );

    // Eingabe und Ausgabe.
    assert!(fixture.core.write(1, b"echo hallo\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("\r\nhallo\r\n"));

    // Die Größe aus session.open ist beim Server angekommen, eine Änderung auch.
    assert!(fixture.core.write(1, b"size\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("80x24"));
    fixture.ok(json!({ "op": "session.resize", "session": 1, "cols": 132, "rows": 43 }));
    assert!(fixture.core.write(1, b"size\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("132x43"));

    // „exit“ beendet die Sitzung von der Serverseite.
    assert!(fixture.core.write(1, b"exit\r"));
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "remote");
    assert_eq!(closed["exit_status"], 0);
    assert!(!fixture.core.write(1, b"x"));

    // Die erfolgreiche Verbindung steht im Profil.
    assert!(fixture.recorder.saw("profiles.changed"));
    let profiles = fixture.ok(json!({ "op": "profiles.list" }));
    assert!(profiles["profiles"][0]["last_connected"].as_u64().unwrap() > 1_700_000_000);
}

#[test]
fn a_known_server_and_a_saved_password_need_no_prompts() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture
        .own_known_hosts()
        .trust("127.0.0.1", fixture.port, host_key(1).public_key())
        .unwrap();
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();

    fixture.open(1, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");

    assert!(!fixture.recorder.saw("prompt.host_key"));
    assert!(!fixture.recorder.saw("prompt.secret"));
}

#[test]
fn a_stale_saved_password_is_dropped_and_asked_again() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture
        .secrets
        .set(&password_key(&profile), "demo", "veraltet")
        .unwrap();

    fixture.open(1, &profile);
    fixture.trust_host_key();
    let prompt = fixture.recorder.event("prompt.secret");
    assert_eq!(prompt["attempt"], 1);
    assert_eq!(fixture.secrets.get(&password_key(&profile)), None);

    fixture.answer(&prompt, json!({ "values": ["geheim"] }));
    fixture.recorder.state("connected");
    assert_eq!(
        fixture.secrets.get(&password_key(&profile)),
        None,
        "ohne „speichern“ wird nichts abgelegt"
    );
}

#[test]
fn rejecting_the_host_key_ends_the_session_before_any_secret_is_asked() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);

    let prompt = fixture.recorder.event("prompt.host_key");
    fixture.answer(&prompt, json!({ "accept": false }));

    assert_eq!(fixture.recorder.state("closed")["reason"], "host_key_rejected");
    assert!(!fixture.recorder.saw("prompt.secret"));
    assert_eq!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Unknown
    );
}

#[test]
fn a_changed_host_key_is_flagged_and_replaced_only_on_consent() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    // Hinterlegt ist ein anderer Schlüssel als der, den der Server vorlegt.
    fixture
        .own_known_hosts()
        .trust("127.0.0.1", fixture.port, host_key(9).public_key())
        .unwrap();

    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    assert_eq!(prompt["changed"], true);
    assert_eq!(prompt["known_hosts_line"], 1);
    assert!(prompt["known_hosts_file"].as_str().unwrap().ends_with("known_hosts"));
    // Zum Vergleich steht der bisherige Fingerabdruck dabei, und dass der Eintrag in der Datei der App steht.
    assert_eq!(
        prompt["previous_fingerprint"]
            .as_str()
            .map(|print| print.starts_with("SHA256:")),
        Some(true)
    );
    assert_ne!(prompt["previous_fingerprint"], prompt["fingerprint"]);
    assert_eq!(prompt["known_hosts_foreign"], false);
    fixture.answer(&prompt, json!({}));
    assert_eq!(fixture.recorder.state("closed")["reason"], "host_key_rejected");
    assert!(matches!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Changed { .. }
    ));

    fixture.open(2, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");
    fixture.answer(&prompt, json!({ "accept": true }));
    fixture.recorder.state("authenticating");
    assert_eq!(
        fixture
            .own_known_hosts()
            .check("127.0.0.1", fixture.port, host_key(1).public_key()),
        nexus_core::hostkeys::Verdict::Trusted
    );
}

#[test]
fn cancelling_the_password_prompt_ends_with_auth_failed() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();

    fixture.recorder.state("authenticating");
    let prompt = fixture.recorder.event("prompt.secret");
    fixture.answer(&prompt, json!({}));

    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
}

#[test]
fn three_wrong_passwords_end_with_auth_failed() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    fixture.trust_host_key();

    for attempt in 1..=3 {
        let prompt = fixture.recorder.event("prompt.secret");
        assert_eq!(prompt["attempt"], attempt);
        fixture.answer(&prompt, json!({ "values": ["falsch"], "save": true }));
    }

    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
    assert_eq!(fixture.secrets.get(&password_key(&profile)), None);
}

#[test]
fn a_default_key_file_logs_in_without_any_secret_prompt() {
    let fixture = Fixture::new(Options {
        password: None,
        authorized_keys: authorized(),
        ..Options::default()
    });
    std::fs::write(
        fixture.directory.join("ssh").join("id_ed25519"),
        user_key().to_openssh(LineEnding::LF).unwrap().as_bytes(),
    )
    .unwrap();
    let profile = fixture.profile(json!({}));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert!(!fixture.recorder.saw("prompt.secret"));
}

#[test]
fn an_encrypted_key_asks_for_its_passphrase_and_can_remember_it() {
    let fixture = Fixture::new(Options {
        password: None,
        authorized_keys: authorized(),
        ..Options::default()
    });
    let key_path = fixture.directory.join("mein_schluessel");
    let encrypted = user_key().encrypt(&mut russh::keys::key::safe_rng(), "sesam").unwrap();
    std::fs::write(&key_path, encrypted.to_openssh(LineEnding::LF).unwrap().as_bytes()).unwrap();
    let profile = fixture.profile(json!({ "key_path": key_path }));

    fixture.open(1, &profile);
    fixture.trust_host_key();

    let first = fixture.recorder.event("prompt.secret");
    assert_eq!(first["kind"], "passphrase");
    assert_eq!(first["key_path"], key_path.display().to_string());
    fixture.answer(&first, json!({ "values": ["falsch"], "save": true }));

    let second = fixture.recorder.event("prompt.secret");
    assert_eq!(second["attempt"], 2);
    fixture.answer(&second, json!({ "values": ["sesam"], "save": true }));

    fixture.recorder.state("connected");
    assert_eq!(fixture.secrets.get(&passphrase_key(&profile)).as_deref(), Some("sesam"));

    // Zweite Verbindung: die gespeicherte Passphrase genügt.
    fixture.open(2, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
}

#[test]
fn a_key_the_server_does_not_know_falls_through_to_auth_failed() {
    let fixture = Fixture::new(Options {
        password: None,
        authorized_keys: vec![host_key(77).public_key().clone()],
        ..Options::default()
    });
    std::fs::write(
        fixture.directory.join("ssh").join("id_ed25519"),
        user_key().to_openssh(LineEnding::LF).unwrap().as_bytes(),
    )
    .unwrap();
    let profile = fixture.profile(json!({}));

    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    assert_eq!(fixture.recorder.state("closed")["reason"], "auth_failed");
}

#[test]
fn closing_while_a_prompt_is_open_ends_the_session_and_voids_the_prompt() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.open(1, &profile);
    let prompt = fixture.recorder.event("prompt.host_key");

    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    assert_eq!(fixture.recorder.state("closed")["reason"], "user");

    let late = fixture.request(json!({ "op": "prompt.answer", "prompt": prompt["prompt"], "accept": true }));
    assert_eq!(late["error"], "unknown_prompt");

    // Ein zweites Schließen ist harmlos und meldet nichts mehr.
    fixture.ok(json!({ "op": "session.close", "session": 1 }));
}

#[test]
fn closing_a_connected_session_reports_the_user_as_reason() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");

    fixture.ok(json!({ "op": "session.close", "session": 1 }));
    assert_eq!(fixture.recorder.state("closed")["reason"], "user");
}

#[test]
fn two_sessions_to_the_same_server_do_not_mix() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture
        .own_known_hosts()
        .trust("127.0.0.1", fixture.port, host_key(1).public_key())
        .unwrap();

    fixture.open(1, &profile);
    fixture.open(2, &profile);
    fixture.recorder.output(1, "$ ");
    fixture.recorder.output(2, "$ ");

    assert!(fixture.core.write(1, b"echo eins\r"));
    assert!(fixture.core.write(2, b"echo zwei\r"));
    let first = fixture.recorder.output(1, "\r\n$ ");
    let second = fixture.recorder.output(2, "\r\n$ ");
    assert!(first.contains("eins") && !first.contains("zwei"), "{first:?}");
    assert!(second.contains("zwei") && !second.contains("eins"), "{second:?}");

    let duplicate =
        fixture.request(json!({ "op": "session.open", "session": 1, "profile_id": profile, "cols": 80, "rows": 24 }));
    assert_eq!(duplicate["error"], "session_exists");
}

#[test]
fn an_unreachable_server_reports_connect_failed() {
    let fixture = Fixture::new(with_password());
    // Ein Port, auf dem gerade niemand lauscht.
    let free = std::net::TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let profile = fixture.profile(json!({ "port": free }));

    fixture.open(1, &profile);
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["reason"], "connect_failed");
    assert!(closed["message"].as_str().is_some_and(|message| !message.is_empty()));
}

#[test]
fn profiles_are_validated_stored_and_deleted_with_their_secrets() {
    let fixture = Fixture::new(with_password());

    let invalid = fixture.request(json!({ "op": "profiles.save", "profile": { "host": "two words", "user": "u" } }));
    assert_eq!(invalid["error"], "invalid_profile");
    assert_eq!(invalid["message"], "host");

    let id = fixture.profile(json!({ "name": "", "tags": ["web", " "] }));
    let listed = fixture.ok(json!({ "op": "profiles.list" }));
    assert_eq!(listed["profiles"][0]["name"], "127.0.0.1");
    assert_eq!(listed["profiles"][0]["tags"], json!(["web"]));

    fixture.secrets.set(&password_key(&id), "demo", "geheim").unwrap();
    fixture.secrets.set(&passphrase_key(&id), "demo", "sesam").unwrap();
    fixture.ok(json!({ "op": "profiles.delete", "id": id }));

    assert_eq!(fixture.ok(json!({ "op": "profiles.list" }))["profiles"], json!([]));
    assert_eq!(fixture.secrets.get(&password_key(&id)), None);
    assert_eq!(fixture.secrets.get(&passphrase_key(&id)), None);

    let unknown =
        fixture.request(json!({ "op": "session.open", "session": 1, "profile_id": id, "cols": 80, "rows": 24 }));
    assert_eq!(unknown["error"], "unknown_profile");
}

#[test]
fn the_profile_list_says_whether_a_secret_is_saved_and_it_can_be_forgotten() {
    let fixture = Fixture::new(with_password());
    let with_secret = fixture.profile(json!({ "name": "mit" }));
    let without = fixture.profile(json!({ "name": "ohne" }));
    fixture
        .secrets
        .set(&password_key(&with_secret), "demo", "geheim")
        .unwrap();
    fixture
        .secrets
        .set(&passphrase_key(&with_secret), "demo", "passphrase")
        .unwrap();

    let saved = |fixture: &Fixture| -> Vec<(String, bool)> {
        fixture.ok(json!({ "op": "profiles.list" }))["profiles"]
            .as_array()
            .unwrap()
            .iter()
            .map(|profile| {
                (
                    profile["name"].as_str().unwrap().to_owned(),
                    profile["saved_secret"].as_bool().unwrap(),
                )
            })
            .collect()
    };
    assert_eq!(saved(&fixture), [("mit".to_owned(), true), ("ohne".to_owned(), false)]);

    fixture.ok(json!({ "op": "secrets.forget", "id": with_secret }));

    assert_eq!(saved(&fixture), [("mit".to_owned(), false), ("ohne".to_owned(), false)]);
    assert_eq!(fixture.secrets.get(&password_key(&with_secret)), None);
    assert_eq!(fixture.secrets.get(&passphrase_key(&with_secret)), None);

    // Das Profil selbst bleibt, und ein Profil ohne Geheimnis zu „vergessen“ ist kein Fehler.
    fixture.ok(json!({ "op": "secrets.forget", "id": without }));
    assert_eq!(
        fixture.ok(json!({ "op": "profiles.list" }))["profiles"]
            .as_array()
            .unwrap()
            .len(),
        2
    );
}

#[test]
fn a_failed_connection_says_what_was_wrong() {
    let fixture = Fixture::new(with_password());
    let free = std::net::TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port();

    // Niemand lauscht auf dem Port.
    fixture.open(1, &fixture.profile(json!({ "name": "zu", "port": free })));
    let closed = fixture.recorder.state("closed");
    assert_eq!(
        (&closed["reason"], &closed["trouble"]),
        (&json!("connect_failed"), &json!("refused"))
    );

    // Den Namen gibt es nicht (`.invalid` ist dafür reserviert).
    fixture.open(
        2,
        &fixture.profile(json!({ "name": "name", "host": "gibt-es-nicht.invalid" })),
    );
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["trouble"], "name");

    // Das Profil will nur IPv6, die Adresse ist IPv4.
    fixture.open(
        3,
        &fixture.profile(json!({ "name": "sechs", "address_family": "ipv6" })),
    );
    let closed = fixture.recorder.state("closed");
    assert_eq!(closed["trouble"], "family");
    assert_eq!(closed["message"], "127.0.0.1 has no IPv6 address");
}

#[test]
fn an_old_device_needs_the_older_algorithms_from_its_profile() {
    let fixture = Fixture::new(Options {
        old_device: true,
        ..with_password()
    });

    // Ohne den Schalter gibt es kein gemeinsames Verfahren – und die Sitzung sagt, dass es daran lag.
    fixture.open(1, &fixture.profile(json!({ "name": "neu" })));
    let closed = fixture.recorder.state("closed");
    assert_eq!(
        (&closed["reason"], &closed["trouble"]),
        (&json!("connect_failed"), &json!("algorithms"))
    );

    // Mit ihm steht die Verbindung, und die Shell arbeitet.
    let profile = fixture.profile(json!({ "name": "alt", "legacy_algorithms": true }));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.ok(json!({ "op": "session.open", "session": 2, "profile_id": profile, "cols": 80, "rows": 24 }));
    fixture.trust_host_key();
    fixture.recorder.output(2, "$ ");
    assert!(fixture.core.write(2, b"echo alt\r"));
    assert!(fixture.recorder.output(2, "\r\n$ ").contains("\r\nalt\r\n"));
}

#[test]
fn a_compressed_connection_carries_the_shell_like_any_other() {
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({ "compression": true }));
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.trust_host_key();
    fixture.recorder.output(1, "$ ");

    // Viel Gleiches: Das ist, was Komprimierung zusammendrückt – es muss unversehrt ankommen.
    assert!(fixture.core.write(1, b"flood 300\r"));
    let output = fixture.recorder.output(1, "\r\n$ ");
    assert!(output.lines().count() >= 300, "{} lines", output.lines().count());
}

#[test]
fn a_key_unlocked_once_is_not_asked_for_again_until_the_app_says_forget() {
    let fixture = Fixture::new(Options {
        password: None,
        authorized_keys: authorized(),
        ..Options::default()
    });
    let key_path = fixture.directory.join("mein_schluessel");
    let encrypted = user_key().encrypt(&mut russh::keys::key::safe_rng(), "sesam").unwrap();
    std::fs::write(&key_path, encrypted.to_openssh(LineEnding::LF).unwrap().as_bytes()).unwrap();
    let first_profile = fixture.profile(json!({ "name": "eins", "key_path": key_path }));
    let second_profile = fixture.profile(json!({ "name": "zwei", "key_path": key_path }));

    // Die erste Verbindung fragt – gespeichert wird die Passphrase nicht.
    fixture.open(1, &first_profile);
    fixture.trust_host_key();
    let prompt = fixture.recorder.event("prompt.secret");
    fixture.answer(&prompt, json!({ "values": ["sesam"], "save": false }));
    fixture.recorder.state("connected");
    assert_eq!(fixture.secrets.get(&passphrase_key(&first_profile)), None);
    let asked = fixture.recorder.all("prompt.secret").len();

    // Ein anderes Profil mit demselben Schlüssel: keine Frage mehr.
    fixture.open(2, &second_profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    assert_eq!(fixture.recorder.all("prompt.secret").len(), asked);

    // „Vergessen“: Die nächste Verbindung fragt wieder.
    fixture.ok(json!({ "op": "keys.unlocked" }));
    fixture.open(3, &second_profile);
    let prompt = fixture.recorder.event("prompt.secret");
    assert_eq!(prompt["kind"], "passphrase");
    fixture.answer(&prompt, json!({ "values": ["sesam"], "save": false }));
    fixture.recorder.state("connected");

    // Abgestellt wird nichts mehr gemerkt: Jede Verbindung fragt.
    fixture.ok(json!({ "op": "keys.unlocked", "remember": false }));
    for session in [4, 5] {
        fixture.open(session, &first_profile);
        let prompt = fixture.recorder.event("prompt.secret");
        fixture.answer(&prompt, json!({ "values": ["sesam"], "save": false }));
        fixture.recorder.state("connected");
    }
}

#[test]
fn the_test_server_sends_a_notice_when_asked() {
    // Der Befehl `notify` des Testservers schickt nach der Wartezeit OSC 777, `notify9` OSC 9 – die Folgen, die
    // die Terminal-Seite der App als Meldung liest. Ein `;` im Text bleibt im Text.
    let fixture = Fixture::new(with_password());
    let profile = fixture.profile(json!({}));
    fixture
        .own_known_hosts()
        .trust("127.0.0.1", fixture.port, host_key(1).public_key())
        .unwrap();
    fixture.secrets.set(&password_key(&profile), "demo", "geheim").unwrap();
    fixture.open(1, &profile);
    fixture.recorder.state("authenticating");
    fixture.recorder.state("connected");
    fixture.recorder.output(1, "$ ");

    assert!(fixture.core.write(1, b"notify 0 3 Fehler; 2 Warnungen\r"));
    let output = fixture.recorder.output(1, "\x07$ ");
    assert!(
        output.ends_with("\x1b]777;notify;NexusSSH;3 Fehler; 2 Warnungen\x07$ "),
        "{output:?}"
    );

    assert!(fixture.core.write(1, b"notify9 0 Build fertig\r"));
    let output = fixture.recorder.output(1, "\x07$ ");
    assert!(output.ends_with("\x1b]9;Build fertig\x07$ "), "{output:?}");

    // Ohne Wartezeit oder Text ist es kein Befehl dieser Art.
    assert!(fixture.core.write(1, b"notify Build\r"));
    assert!(fixture.recorder.output(1, "\r\n$ ").contains("command not found"));
}
