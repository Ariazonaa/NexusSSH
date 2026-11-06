use super::super::*;

use std::fmt::Write as _;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::process::{Command, Stdio};
use std::time::Duration;

use tokio::sync::{mpsc, watch};
use tokio::task::JoinHandle;

use zeroize::Zeroize as _;

use crate::Bootstrap;
use crate::prediction::PredictionMode;
use crate::test_support::{DetachedProcessGuard, assert_stock_1_4_0, find_detached_pid};

mod isolation;
mod prediction;
mod recovery;
mod relay;

use relay::UdpRelay;

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_drives_an_interactive_shell() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60400:60419");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        assert_eq!(session.state(), SessionState::Active);
        session
            .send_input(b"printf 'SESSION_E2E_OK\\n'\n".to_vec())
            .await
            .expect("Session command queue closed before input");
        wait_for_public_screen(&mut session, &mut projection, "SESSION_E2E_OK").await;
        session
            .request_repaint()
            .await
            .expect("Session command queue closed before repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), session.next_output())
            .await
            .expect("full repaint was not delivered")
            .expect("Session output queue closed before full repaint");
        let mut replacement = vt100::Parser::new(24, 80, 0);
        replacement.process(&repaint);
        assert!(replacement.screen().contents().contains("SESSION_E2E_OK"));
        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled public stock Session did not stop")
            .unwrap()
            .expect("public stock Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

// NexusSSH (P5): Eine Sitzung über IPv6 – mosh-server lauscht nur auf `::1`, der Client bindet `[::]:0`.
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0 and IPv6 loopback"]
fn stock_1_4_0_public_session_runs_over_ipv6() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) =
        start_stock_server_on("60820:60839", IpAddr::V6(Ipv6Addr::LOCALHOST));
    assert!(bootstrap.server_addr().is_ipv6());

    stock_runtime().block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public IPv6 Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session
            .send_input(b"printf 'IPV6_%s\\n' OK\n".to_vec())
            .await
            .expect("Session command queue closed before input");
        wait_for_public_screen(&mut session, &mut projection, "IPV6_OK").await;
        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled IPv6 Session did not stop")
            .unwrap()
            .expect("IPv6 Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

// NexusSSH (P9): Glocke und Fenstertitel eines Programms kommen über mosh-server bei der VT-Ausgabe an.
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
// Die Ausgabe ist klein; ein eigenes Paket zum Zählen lohnt nicht.
#[allow(clippy::naive_bytecount)]
fn stock_1_4_0_public_session_passes_bell_and_title_on() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60840:60859");

    stock_runtime().block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session
            .send_input(b"printf '\\033]0;NX_%s\\007\\007BELL_%s\\n' TITLE DONE\n".to_vec())
            .await
            .expect("Session command queue closed before input");
        let mut seen = Vec::new();
        tokio::time::timeout(Duration::from_secs(5), async {
            loop {
                let paint = session.next_output().await.expect("output closed");
                projection.process(&paint);
                seen.extend_from_slice(&paint);
                if projection.screen().contents().contains("BELL_DONE") {
                    break;
                }
            }
        })
        .await
        .expect("the marker did not arrive");
        let contains = |needle: &[u8]| seen.windows(needle.len()).any(|w| w == needle);
        assert!(contains(b"\x1b]0;NX_TITLE\x07"), "no title in {seen:?}");
        let bells = seen.iter().filter(|byte| **byte == 0x07).count();
        assert!(bells >= 2, "no bell in {seen:?}");

        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled Session did not stop")
            .unwrap()
            .expect("Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_preserves_printable_unicode() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60680:60699");

    stock_runtime().block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session
            .send_input(b"printf 'BEFORE-\\357\\277\\275-AFTER\\n'\n".to_vec())
            .await
            .expect("Session command queue closed before valid UTF-8 output");
        wait_for_public_screen(&mut session, &mut projection, "BEFORE-\u{fffd}-AFTER").await;

        session
            .send_input(b"printf 'INVALID-\\377-AFTER\\n'\n".to_vec())
            .await
            .expect("Session command queue closed before server replacement output");
        wait_for_public_screen(&mut session, &mut projection, "INVALID-\u{fffd}-AFTER").await;

        session
            .send_input(
                b"printf 'UNICODE-\\303\\251-\\344\\270\\255-\\360\\237\\231\\202-e\\314\\201-AFTER\\n'\n"
                    .to_vec(),
            )
            .await
            .expect("Session command queue closed before multilingual output");
        wait_for_public_screen(&mut session, &mut projection, "UNICODE-é-中-🙂-e\u{301}-AFTER")
            .await;

        session
            .send_input(b"printf 'CONTINUES_OK\\n'\n".to_vec())
            .await
            .expect("Session command queue closed before follow-up input");
        wait_for_public_screen(&mut session, &mut projection, "CONTINUES_OK").await;

        session.resize(81, 25).await.expect("resize was rejected");
        session
            .request_repaint()
            .await
            .expect("Session command queue closed before repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), session.next_output())
            .await
            .expect("full repaint was not delivered")
            .expect("Session output queue closed before full repaint");
        let mut replacement = vt100::Parser::new(25, 81, 0);
        replacement.process(&repaint);
        assert!(
            replacement
                .screen()
                .contents()
                .contains("BEFORE-\u{fffd}-AFTER")
        );
        assert!(
            replacement
                .screen()
                .contents()
                .contains("INVALID-\u{fffd}-AFTER")
        );
        assert!(replacement.screen().contents().contains("CONTINUES_OK"));
        assert!(
            replacement
                .screen()
                .contents()
                .contains("UNICODE-é-中-🙂-e\u{301}-AFTER")
        );

        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled public stock Session did not stop")
            .unwrap()
            .expect("public stock Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_preserves_server_width_characters() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60700:60719");

    stock_runtime().block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        for (command, expected) in [
            (
                b"printf '\\330\\205-AFTER\\n'\n".as_slice(),
                "\u{0605}-AFTER",
            ),
            (
                b"printf 'PRE-\\330\\205-AFTER\\n'\n".as_slice(),
                "PRE-\u{0605}-AFTER",
            ),
            (
                b"printf '\\334\\217-AFTER\\n'\n".as_slice(),
                "\u{070f}-AFTER",
            ),
            (
                b"printf '\\340\\246\\276-AFTER\\n'\n".as_slice(),
                "\u{09be}-AFTER",
            ),
            (
                b"printf '\\343\\200\\256-AFTER\\n'\n".as_slice(),
                "\u{302e}-AFTER",
            ),
            (
                b"printf '\\343\\211\\210-AFTER\\n'\n".as_slice(),
                "\u{3248}-AFTER",
            ),
            (
                b"printf '\\341\\236\\244-AFTER\\n'\n".as_slice(),
                "\u{17a4}-AFTER",
            ),
            (
                b"printf '\\341\\237\\230-AFTER\\n'\n".as_slice(),
                "\u{17d8}-AFTER",
            ),
            (
                b"printf 'X\\342\\265\\277-AFTER\\n'\n".as_slice(),
                "X\u{2d7f}-AFTER",
            ),
        ] {
            session
                .send_input(command.to_vec())
                .await
                .expect("input queue closed");
            wait_for_public_screen(&mut session, &mut projection, expected).await;
            assert_eq!(session.state(), SessionState::Active);
        }

        session
            .send_input(b"printf 'WIDTH_CONTINUES_OK\\n'\n".to_vec())
            .await
            .expect("follow-up input queue closed");
        wait_for_public_screen(&mut session, &mut projection, "WIDTH_CONTINUES_OK").await;

        session.resize(81, 25).await.expect("resize was rejected");
        session
            .request_repaint()
            .await
            .expect("repaint queue closed");
        let repaint = tokio::time::timeout(Duration::from_secs(2), session.next_output())
            .await
            .expect("full repaint was not delivered")
            .expect("output queue closed before repaint");
        let mut replacement = vt100::Parser::new(25, 81, 0);
        replacement.process(&repaint);
        assert!(replacement.screen().contents().contains("\u{0605}-AFTER"));
        assert!(
            replacement
                .screen()
                .contents()
                .contains("WIDTH_CONTINUES_OK")
        );

        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled stock Session did not stop")
            .unwrap()
            .expect("stock Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_observes_binary_output_before_followup() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60720:60739");

    stock_runtime().block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 147, 43)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(43, 147, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session
            .send_input(b"\\cat /bin/ls\n".to_vec())
            .await
            .expect("binary command queue closed");
        let observed = tokio::time::timeout(Duration::from_secs(5), session.next_output())
            .await
            .expect("binary command produced no terminal update")
            .expect("public Session closed during binary command");
        projection.process(&observed);
        tokio::time::sleep(Duration::from_millis(500)).await;
        assert_eq!(session.state(), SessionState::Active);

        // Observe the binary output and issue more input before any screen reset.
        session
            .send_input(b"printf 'BINARY_%s_OK\\n' FOLLOWUP\n".to_vec())
            .await
            .expect("follow-up command queue closed");
        wait_for_public_screen(&mut session, &mut projection, "BINARY_FOLLOWUP_OK").await;
        assert_eq!(session.state(), SessionState::Active);

        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("cancelled stock Session did not stop")
            .unwrap()
            .expect("stock Session failed");
        assert_eq!(exit, SessionExit::Cancelled);
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

// These are the independent stock-server cases from LeanTTY PR #264.
const STOCK_TERMINAL_BATCH_CASES: [(&str, &str, bool); 23] = [
    (
        "osc0_semicolon_bel",
        "1b5d303b68656c6c6f3b776f726c6407",
        true,
    ),
    (
        "osc0_semicolon_st",
        "1b5d303b68656c6c6f3b776f726c641b5c",
        true,
    ),
    (
        "osc1_semicolon_bel",
        "1b5d313b68656c6c6f3b776f726c6407",
        true,
    ),
    (
        "osc2_semicolon_bel",
        "1b5d323b68656c6c6f3b776f726c6407",
        true,
    ),
    ("osc0_plain_bel", "1b5d303b68656c6c6f07", true),
    ("sgr_5", "1b5b356d58", true),
    ("sgr_8", "1b5b386d58", true),
    ("sgr_1", "1b5b316d58", true),
    ("mode_5", "1b5b3f3568", true),
    ("mode5_reset", "1b5b3f356c", true),
    ("mode_1001", "1b5b3f3130303168", true),
    ("mode_1015", "1b5b3f3130313568", true),
    ("mode_1000", "1b5b3f3130303068", true),
    ("osc52_bad", "1b5d35323b633b25252507", true),
    ("osc52", "1b5d35323b633b59574a6a07", true),
    ("combining2_8", "41cc81cc81cc81cc81cc81cc81cc81cc81", true),
    ("combining2_7", "41cc81cc81cc81cc81cc81cc81cc81", true),
    (
        "combining3_7",
        "41e1aab0e1aab0e1aab0e1aab0e1aab0e1aab0e1aab0",
        true,
    ),
    (
        "combining3_6",
        "41e1aab0e1aab0e1aab0e1aab0e1aab0e1aab0",
        true,
    ),
    // NexusSSH (P2): Diese Ausgaben (aus der Prüfung gegen mosh-server 1.4.0) und die fünf oben,
    // die im Original `false` trugen, beendeten die Sitzung.
    (
        "zalgo_10",
        "61cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81",
        true,
    ),
    (
        "zalgo_20",
        "61cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81cc81",
        true,
    ),
    (
        "combining3_20d0_7",
        "61e28390e28390e28390e28390e28390e28390e28390",
        true,
    ),
    (
        "osc52_not_base64",
        "1b5d35323b633b21216b65696e2d426173653634212107",
        true,
    ),
];

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_batch_terminal_compatibility() {
    assert_stock_1_4_0("mosh-server");
    // Each server starts fresh so a prior policy rejection cannot affect the next case.

    for (index, (id, hex, should_survive)) in STOCK_TERMINAL_BATCH_CASES.into_iter().enumerate() {
        let first_port = 60740 + 20 * index;
        let range = format!("{first_port}:{}", first_port + 19);
        let (bootstrap, mut server) = start_stock_server(&range);
        stock_runtime().block_on(async {
            let (mut session, session_task) = Session::connect(bootstrap, 147, 43)
                .await
                .unwrap_or_else(|error| panic!("{id}: public Session setup failed: {error:?}"));
            let task = tokio::spawn(session_task.run());
            let mut projection = vt100::Parser::new(43, 147, 0);
            wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;

            let bytes: Vec<u8> = hex
                .as_bytes()
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
                .collect();
            let mut octal = String::new();
            for byte in bytes {
                write!(octal, "\\{byte:03o}").unwrap();
            }
            let command = format!("printf '{octal}'; printf '\\nBATCH_%s_OK\\n' {id}\n");
            session
                .send_input(command.into_bytes())
                .await
                .unwrap_or_else(|error| panic!("{id}: input queue closed: {error:?}"));

            if should_survive {
                wait_for_public_screen(&mut session, &mut projection, &format!("BATCH_{id}_OK"))
                    .await;
                assert_eq!(session.state(), SessionState::Active, "{id}");
                session
                    .send_input(format!("printf 'AFTER_%s_OK\\n' {id}\n").into_bytes())
                    .await
                    .unwrap_or_else(|error| panic!("{id}: follow-up queue closed: {error:?}"));
                wait_for_public_screen(&mut session, &mut projection, &format!("AFTER_{id}_OK"))
                    .await;
                session.cancel();
                let exit = tokio::time::timeout(Duration::from_secs(2), task)
                    .await
                    .unwrap_or_else(|_| panic!("{id}: cancelled Session did not stop"))
                    .unwrap()
                    .unwrap_or_else(|error| panic!("{id}: Session failed: {error:?}"));
                assert_eq!(exit, SessionExit::Cancelled, "{id}");
            } else {
                let result = tokio::time::timeout(Duration::from_secs(5), task)
                    .await
                    .unwrap_or_else(|_| panic!("{id}: rejected policy did not stop Session"))
                    .unwrap();
                assert!(
                    matches!(result, Err(SessionError::Protocol)),
                    "{id}: {result:?}"
                );
            }
        });
        assert!(
            server.terminate(),
            "{id}: stock server fixture did not clean up"
        );
    }
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_gracefully_closes_the_server() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60580:60599");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session.close();
        session.close();
        assert_eq!(
            session.send_input(b"too late".to_vec()).await,
            Err(SessionCommandError::Closed)
        );
        let exit = tokio::time::timeout(Duration::from_secs(3), task)
            .await
            .expect("gracefully closed public stock Session did not stop")
            .unwrap()
            .expect("public stock Session failed");
        assert_eq!(exit, SessionExit::LocalClosed);
    });

    for _ in 0..100 {
        if server.has_exited() {
            break;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    assert!(
        server.has_exited(),
        "graceful close left stock server running"
    );
    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_reports_remote_close_and_drains_final_output() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60660:60679");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        session
            .send_input(b"printf 'REMOTE_FINAL_OK\\n'; exit\n".to_vec())
            .await
            .expect("Session command queue closed before remote exit");
        let output = tokio::spawn(async move {
            while let Some(paint) = session.next_output().await {
                projection.process(&paint);
            }
            projection.screen().contents()
        });

        let exit = tokio::time::timeout(Duration::from_secs(5), task)
            .await
            .expect("remote stock exit did not stop the public Session")
            .unwrap()
            .expect("public stock Session failed");
        assert_eq!(exit, SessionExit::RemoteClosed);
        let final_screen = output.await.unwrap();
        assert!(final_screen.contains("REMOTE_FINAL_OK"));
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

async fn wait_for_public_screen(
    session: &mut Session,
    projection: &mut vt100::Parser,
    marker: &str,
) {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    loop {
        let remaining = deadline
            .checked_duration_since(tokio::time::Instant::now())
            .unwrap_or_default();
        let output = tokio::time::timeout(remaining, session.next_output())
            .await
            .unwrap_or_else(|_| panic!("timed out waiting for {marker:?}"))
            .expect("public Session output closed before expected screen");
        projection.process(&output);
        if projection.screen().contents().contains(marker) {
            return;
        }
    }
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_private_session_resizes_the_remote_pty_and_repaints() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60560:60579");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (commands, mut output, task) = start_private_session(bootstrap).await;
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
        commands
            .send(SessionCommand::Input(b"stty size\n".to_vec()))
            .await
            .expect("Session command queue closed before baseline size check");
        wait_for_screen(&mut output, &mut projection, "24 80").await;

        commands
            .send(SessionCommand::Resize {
                columns: 100,
                rows: 30,
            })
            .await
            .expect("Session command queue closed before first resize");
        projection.screen_mut().set_size(30, 100);
        commands
            .send(SessionCommand::Input(b"stty size\n".to_vec()))
            .await
            .expect("Session command queue closed after first resize");
        wait_for_screen(&mut output, &mut projection, "30 100").await;

        commands
            .send(SessionCommand::Resize {
                columns: 60,
                rows: 20,
            })
            .await
            .expect("Session command queue closed before second resize");
        projection.screen_mut().set_size(20, 60);
        commands
            .send(SessionCommand::Input(b"stty size\n".to_vec()))
            .await
            .expect("Session command queue closed after second resize");
        wait_for_screen(&mut output, &mut projection, "20 60").await;

        commands
            .send(SessionCommand::Repaint)
            .await
            .expect("Session command queue closed before resized repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), output.recv())
            .await
            .expect("resized full repaint was not delivered")
            .expect("Session output queue closed before resized full repaint");
        let mut replacement = vt100::Parser::new(20, 60, 0);
        replacement.process(&repaint);
        assert!(replacement.screen().contents().contains("20 60"));

        cancel_session(&commands, task).await;
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires local stock mosh-server 1.4.0, tmux, and Vim"]
fn stock_1_4_0_private_session_supports_tmux_vim_and_full_screen_repaint() {
    assert_stock_1_4_0("mosh-server");
    assert_program_available("tmux", "-V");
    assert_program_available("vim", "--version");
    let (bootstrap, mut server) = start_stock_server("60420:60439");
    let tmux_socket = format!("mosh-client-rs-{}", std::process::id());
    let _tmux = TmuxServerGuard::new(tmux_socket.clone());

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (commands, mut output, task) = start_private_session(bootstrap).await;
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
        exercise_tmux(&commands, &mut output, &mut projection, &tmux_socket).await;
        exercise_vim(&commands, &mut output, &mut projection).await;

        cancel_session(&commands, task).await;
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

async fn exercise_tmux(
    commands: &TestSessionCommands,
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
    tmux_socket: &str,
) {
    for command in [
        format!(
            "tmux -L {tmux_socket} -f /dev/null new-session -d -s fixture \"/usr/bin/env PS1='MOSH_TMUX_ONE> ' /bin/sh -i\"\n"
        ),
        format!(
            "tmux -L {tmux_socket} new-window -d -t fixture \"/usr/bin/env PS1='MOSH_TMUX_TWO> ' /bin/sh -i\"\n"
        ),
        format!("tmux -L {tmux_socket} attach-session -t fixture:0\n"),
    ] {
        commands
            .send(SessionCommand::Input(command.into_bytes()))
            .await
            .expect("Session command queue closed during tmux startup");
    }
    wait_for_screen(output, projection, "MOSH_TMUX_ONE> ").await;
    commands
        .send(SessionCommand::Input(
            b"printf 'TMUX_WINDOW_ONE_OK\\n'\n".to_vec(),
        ))
        .await
        .expect("Session command queue closed before tmux input");
    wait_for_screen(output, projection, "TMUX_WINDOW_ONE_OK").await;
    commands
        .send(SessionCommand::Input(b"\x02n".to_vec()))
        .await
        .expect("Session command queue closed before tmux navigation");
    wait_for_screen(output, projection, "MOSH_TMUX_TWO> ").await;
    commands
        .send(SessionCommand::Input(
            b"printf 'TMUX_WINDOW_TWO_OK\\n'\n".to_vec(),
        ))
        .await
        .expect("Session command queue closed before second tmux window input");
    wait_for_screen(output, projection, "TMUX_WINDOW_TWO_OK").await;
    commands
        .send(SessionCommand::Input(b"\x02p".to_vec()))
        .await
        .expect("Session command queue closed before reverse tmux navigation");
    wait_for_screen(output, projection, "TMUX_WINDOW_ONE_OK").await;
    detach_tmux(commands, output, projection, "tmux detach").await;
    commands
        .send(SessionCommand::Input(
            format!("tmux -L {tmux_socket} attach-session -t fixture:1\n").into_bytes(),
        ))
        .await
        .expect("Session command queue closed before tmux reattach");
    wait_for_screen(output, projection, "TMUX_WINDOW_TWO_OK").await;
    detach_tmux(commands, output, projection, "tmux reattach").await;
}

async fn detach_tmux(
    commands: &TestSessionCommands,
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
    description: &str,
) {
    commands
        .send(SessionCommand::Input(b"\x02d".to_vec()))
        .await
        .expect("Session command queue closed before tmux detach");
    wait_for_projection(
        output,
        projection,
        description,
        Duration::from_secs(5),
        |screen| {
            !screen.contents().contains("[fixture]") && screen.contents().contains("MOSH_SESSION> ")
        },
    )
    .await;
}

async fn exercise_vim(
    commands: &TestSessionCommands,
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
) {
    commands
        .send(SessionCommand::Input(
            b"vim -Nu NONE -n -i NONE +'set noswapfile'\n".to_vec(),
        ))
        .await
        .expect("Session command queue closed before Vim startup");
    wait_for_projection(
        output,
        projection,
        "Vim full-screen state",
        Duration::from_secs(5),
        |screen| screen.contents().contains("VIM - Vi IMproved"),
    )
    .await;
    commands
        .send(SessionCommand::Input(b"iEDITOR_SESSION_OK\x1b".to_vec()))
        .await
        .expect("Session command queue closed before Vim input");
    wait_for_screen(output, projection, "EDITOR_SESSION_OK").await;
    assert!(
        !projection.screen().alternate_screen(),
        "stock visible Vim state unexpectedly preserved the remote alternate-screen boundary"
    );

    commands
        .send(SessionCommand::Repaint)
        .await
        .expect("Session command queue closed before full-screen repaint");
    let repaint = tokio::time::timeout(Duration::from_secs(2), output.recv())
        .await
        .expect("full-screen repaint was not delivered")
        .expect("Session output queue closed before full-screen repaint");
    let mut replacement = vt100::Parser::new(24, 80, 0);
    replacement.process(&repaint);
    assert!(
        replacement
            .screen()
            .contents()
            .contains("EDITOR_SESSION_OK")
    );
    assert!(
        !replacement.screen().alternate_screen(),
        "full repaint unexpectedly synthesized an application alternate-screen boundary"
    );

    commands
        .send(SessionCommand::Input(b"\x1b:q!\n".to_vec()))
        .await
        .expect("Session command queue closed before Vim exit");
    wait_for_projection(
        output,
        projection,
        "restored primary shell screen",
        Duration::from_secs(5),
        |screen| !screen.alternate_screen() && screen.contents().contains("MOSH_SESSION> "),
    )
    .await;
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_private_session_preserves_sustained_io_under_output_backpressure() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60440:60459");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (commands, mut output, task) = start_private_session(bootstrap).await;
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
        commands
            .send(SessionCommand::Input(
                b"PS1=; stty -echo; payload=\n".to_vec(),
            ))
            .await
            .expect("Session command queue closed before sustained input setup");
        let mut expected_input = String::new();
        for index in 0..128 {
            let chunk = format!("{index:03}");
            expected_input.push_str(&chunk);
            commands
                .send(SessionCommand::Input(
                    format!("payload=\"${{payload}}{chunk}\"\n").into_bytes(),
                ))
                .await
                .expect("Session command queue closed during sustained input");
        }
        commands
            .send(SessionCommand::Input(
                format!(
                    "stty echo; if [ \"$payload\" = \"{expected_input}\" ]; then printf 'SUSTAINED_INPUT_ORDER_OK\\n'; else printf 'SUSTAINED_INPUT_BAD_%s\\n' \"${{#payload}}\"; fi\n"
                )
                .into_bytes(),
            ))
            .await
            .expect("Session command queue closed after sustained input");
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            "SUSTAINED_INPUT_ORDER_OK",
            Duration::from_secs(10),
        )
        .await;

        commands
            .send(SessionCommand::Input(
                b"i=0; while [ \"$i\" -lt 1200 ]; do printf 'SUSTAINED_%04d_abcdefghijklmnopqrstuvwxyz0123456789\\n' \"$i\"; i=$((i+1)); done; printf 'SUSTAINED_DONE_1200\\n'\n".to_vec(),
            ))
            .await
            .expect("Session command queue closed before sustained output");

        tokio::time::sleep(Duration::from_millis(500)).await;
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            "SUSTAINED_DONE_1200",
            Duration::from_secs(10),
        )
        .await;
        commands
            .send(SessionCommand::Repaint)
            .await
            .expect("Session command queue closed before sustained-output repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), output.recv())
            .await
            .expect("sustained-output repaint was not delivered")
            .expect("Session output queue closed before sustained-output repaint");
        let mut replacement = vt100::Parser::new(24, 80, 0);
        replacement.process(&repaint);
        assert!(
            replacement
                .screen()
                .contents()
                .contains("SUSTAINED_DONE_1200")
        );

        cancel_session(&commands, task).await;
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

fn start_stock_server(port_range: &str) -> (Bootstrap, DetachedProcessGuard) {
    start_stock_server_on(port_range, IpAddr::V4(Ipv4Addr::LOCALHOST))
}

// NexusSSH (P5): mosh-server auch auf `::1`.
fn start_stock_server_on(port_range: &str, address: IpAddr) -> (Bootstrap, DetachedProcessGuard) {
    let bind = address.to_string();
    let mut output = Command::new("mosh-server")
        .env("TERM", "xterm-256color")
        .env("LANG", "C.UTF-8")
        .args([
            "new",
            "-i",
            &bind,
            "-p",
            port_range,
            "--",
            "/usr/bin/env",
            "PS1=MOSH_SESSION> ",
            "/bin/sh",
            "-i",
        ])
        .output()
        .unwrap_or_else(|error| panic!("failed to start local stock server: {error}"));
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let server = DetachedProcessGuard::new(detached_pid);
    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(address, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    output.stdout.zeroize();
    output.stderr.zeroize();
    (bootstrap, server)
}

fn stock_runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
}

async fn start_private_session(
    bootstrap: Bootstrap,
) -> (
    TestSessionCommands,
    mpsc::Receiver<Vec<u8>>,
    JoinHandle<Result<SessionExit, DriverError>>,
) {
    start_private_session_with_prediction_mode(bootstrap, PredictionMode::Adaptive).await
}

async fn start_private_session_with_prediction_mode(
    bootstrap: Bootstrap,
    prediction_mode: PredictionMode,
) -> (
    TestSessionCommands,
    mpsc::Receiver<Vec<u8>>,
    JoinHandle<Result<SessionExit, DriverError>>,
) {
    start_private_session_sized(bootstrap, 80, 24, prediction_mode).await
}

// NexusSSH (P1): Größere Bildschirme brauchen Anweisungen aus mehreren Fragmenten.
async fn start_private_session_sized(
    bootstrap: Bootstrap,
    columns: u32,
    rows: u32,
    prediction_mode: PredictionMode,
) -> (
    TestSessionCommands,
    mpsc::Receiver<Vec<u8>>,
    JoinHandle<Result<SessionExit, DriverError>>,
) {
    let (driver, channels) = SessionDriver::connect(bootstrap, columns, rows, prediction_mode)
        .await
        .unwrap_or_else(|error| panic!("private Session setup failed: {error:?}"));
    let SessionChannels {
        commands,
        output,
        cancellation,
        graceful_close,
        ..
    } = channels;
    let task = tokio::spawn(async move {
        let result = driver.run().await;
        if let Err(error) = &result {
            eprintln!("private Session stopped: {error:?}");
        }
        result
    });
    (
        TestSessionCommands {
            commands,
            cancellation,
            _graceful_close: graceful_close,
        },
        output,
        task,
    )
}

async fn cancel_session(
    commands: &TestSessionCommands,
    task: JoinHandle<Result<SessionExit, DriverError>>,
) {
    commands.cancel();
    let close = tokio::time::timeout(Duration::from_secs(2), task)
        .await
        .expect("cancelled stock Session did not stop")
        .unwrap()
        .unwrap_or_else(|error| panic!("private Session failed: {error:?}"));
    assert_eq!(close, SessionExit::Cancelled);
}

struct TestSessionCommands {
    commands: mpsc::Sender<SessionCommand>,
    cancellation: watch::Sender<bool>,
    _graceful_close: watch::Sender<bool>,
}

impl TestSessionCommands {
    async fn send(
        &self,
        command: SessionCommand,
    ) -> Result<(), mpsc::error::SendError<SessionCommand>> {
        self.commands.send(command).await
    }

    fn cancel(&self) {
        self.cancellation.send_replace(true);
    }
}

async fn wait_for_screen(
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
    marker: &str,
) {
    wait_for_screen_with_timeout(output, projection, marker, Duration::from_secs(5)).await;
}

async fn wait_for_screen_with_timeout(
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
    marker: &str,
    timeout: Duration,
) {
    wait_for_projection(output, projection, marker, timeout, |screen| {
        screen.contents().contains(marker)
    })
    .await;
}

async fn wait_for_projection(
    output: &mut mpsc::Receiver<Vec<u8>>,
    projection: &mut vt100::Parser,
    description: &str,
    timeout: Duration,
    mut predicate: impl FnMut(&vt100::Screen) -> bool,
) {
    tokio::time::timeout(timeout, async {
        loop {
            let paint = output
                .recv()
                .await
                .unwrap_or_else(|| panic!("Session output queue closed before {description}"));
            projection.process(&paint);
            if predicate(projection.screen()) {
                break;
            }
        }
    })
    .await
    .unwrap_or_else(|_| {
        panic!(
            "stock Session did not reach {description:?}; visible screen: {:?}",
            projection.screen().contents()
        )
    });
}

fn assert_program_available(program: &str, version_argument: &str) {
    let status = Command::new(program)
        .arg(version_argument)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .unwrap_or_else(|error| panic!("fixture requires local {program}: {error}"));
    assert!(status.success(), "fixture requires local {program}");
}

struct TmuxServerGuard {
    socket_name: String,
}

impl TmuxServerGuard {
    const fn new(socket_name: String) -> Self {
        Self { socket_name }
    }
}

impl Drop for TmuxServerGuard {
    fn drop(&mut self) {
        let _ = Command::new("tmux")
            .arg("-L")
            .arg(&self.socket_name)
            .arg("kill-server")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
}
