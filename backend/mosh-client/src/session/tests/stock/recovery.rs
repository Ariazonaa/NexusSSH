use super::*;

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_private_session_recovers_after_outage_and_source_port_change() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60460:60479");
    let server_addr = bootstrap.server_addr();

    let runtime = stock_runtime();
    runtime.block_on(async {
        let relay = UdpRelay::start(server_addr).await;
        let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
        let (commands, mut output, task) = start_private_session(bootstrap).await;
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
        commands
            .send(SessionCommand::Input(
                b"printf 'BEFORE_RECOVERY_OUTAGE\\n'\n".to_vec(),
            ))
            .await
            .expect("Session command queue closed before outage baseline");
        wait_for_screen(&mut output, &mut projection, "BEFORE_RECOVERY_OUTAGE").await;

        relay.pause();
        commands
            .send(SessionCommand::Input(
                b"printf 'RECOVERED_AFTER_OUTAGE\\n'\n".to_vec(),
            ))
            .await
            .expect("Session command queue closed during outage");
        tokio::time::sleep(Duration::from_millis(1_500)).await;
        assert!(
            !task.is_finished(),
            "Session stopped during a network outage"
        );
        assert!(
            relay.dropped_packets() > 0,
            "relay did not observe traffic during the outage"
        );

        relay.switch_source_port();
        relay.resume();
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            "RECOVERED_AFTER_OUTAGE",
            Duration::from_secs(10),
        )
        .await;

        commands
            .send(SessionCommand::Input(
                b"printf 'AFTER_SOURCE_CHANGE_OK\\n'\n".to_vec(),
            ))
            .await
            .expect("Session command queue closed after source-port change");
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            "AFTER_SOURCE_CHANGE_OK",
            Duration::from_secs(10),
        )
        .await;

        commands
            .send(SessionCommand::Repaint)
            .await
            .expect("Session command queue closed before recovery repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), output.recv())
            .await
            .expect("recovery repaint was not delivered")
            .expect("Session output queue closed before recovery repaint");
        let mut replacement = vt100::Parser::new(24, 80, 0);
        replacement.process(&repaint);
        assert!(
            replacement
                .screen()
                .contents()
                .contains("AFTER_SOURCE_CHANGE_OK")
        );

        let traffic = relay.traffic();
        assert!(traffic.sent[0] > 0, "first relay source sent no datagrams");
        assert!(
            traffic.received[0] > 0,
            "first relay source received no datagrams"
        );
        assert!(traffic.sent[1] > 0, "second relay source sent no datagrams");
        assert!(
            traffic.received[1] > 0,
            "stock server did not reply to the changed source port"
        );

        cancel_session(&commands, task).await;
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_reachability_reports_outage_reply_loss_and_recovery() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60700:60719");
    let server_addr = bootstrap.server_addr();

    stock_runtime().block_on(async {
        let relay = UdpRelay::start(server_addr).await;
        let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let mut reachability = session.subscribe_reachability();
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        wait_for_reachability(&mut reachability, SessionReachability::Responsive).await;

        let replayed_datagram = relay.last_server_datagram();
        relay.pause();
        for _ in 0..6 {
            tokio::time::sleep(Duration::from_secs(1)).await;
            relay.inject_to_client(&replayed_datagram).await;
        }
        wait_for_reachability(
            &mut reachability,
            SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentContact,
            },
        )
        .await;
        assert!(!task.is_finished(), "outage ended an active Session");

        relay.resume();
        wait_for_reachability(&mut reachability, SessionReachability::Responsive).await;
        session
            .send_input(b"printf 'REACHABILITY_RECOVERED\n'\n".to_vec())
            .await
            .expect("Session rejected input after recovery");
        wait_for_public_screen(&mut session, &mut projection, "REACHABILITY_RECOVERED").await;

        session
            .send_input(
                b"(i=0; while [ \"$i\" -lt 20 ]; do printf 'CONTACT_TICK_%s\\n' \"$i\"; i=$((i+1)); sleep 1; done) &\n"
                    .to_vec(),
            )
            .await
            .expect("Session rejected remote contact generator");
        wait_for_public_screen(&mut session, &mut projection, "CONTACT_TICK_0").await;
        relay.pause_client_to_server();
        wait_for_reachability(
            &mut reachability,
            SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentReply,
            },
        )
        .await;
        assert!(!task.is_finished(), "reply loss ended an active Session");

        relay.resume();
        wait_for_reachability(&mut reachability, SessionReachability::Responsive).await;
        session.cancel();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(2), task)
                .await
                .expect("cancelled Session did not stop")
                .expect("Session task panicked")
                .expect("Session task failed"),
            SessionExit::Cancelled
        );
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

async fn wait_for_reachability(
    reachability: &mut SessionReachabilityWatch,
    expected: SessionReachability,
) {
    tokio::time::timeout(Duration::from_secs(12), async {
        loop {
            if reachability.current() == expected {
                return;
            }
            reachability
                .changed()
                .await
                .expect("reachability observer closed before expected state");
        }
    })
    .await
    .unwrap_or_else(|_| panic!("timed out waiting for reachability {expected:?}"));
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_session_reports_interruption_after_server_disappears() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60480:60499");

    let runtime = stock_runtime();
    runtime.block_on(async {
        let (mut session, session_task) = Session::connect(bootstrap, 80, 24)
            .await
            .expect("public Session setup failed");
        let mut reachability = session.subscribe_reachability();
        let task = tokio::spawn(session_task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);

        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        wait_for_reachability(&mut reachability, SessionReachability::Responsive).await;
        assert!(server.signal_kill(), "failed to kill stock server");
        tokio::time::timeout(Duration::from_secs(2), async {
            while !server.has_exited() {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
        })
        .await
        .expect("stock server did not exit after termination signal");
        wait_for_reachability(
            &mut reachability,
            SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentContact,
            },
        )
        .await;
        assert!(
            !task.is_finished(),
            "Session stopped solely because the server became silent"
        );
        assert_eq!(session.state(), SessionState::Active);

        session
            .request_repaint()
            .await
            .expect("Session command queue closed after server disappearance");
        let mut replacement = vt100::Parser::new(24, 80, 0);
        let repaint = tokio::time::timeout(Duration::from_secs(2), session.next_output())
            .await
            .expect("repaint timed out after server disappearance")
            .expect("output closed after server disappearance");
        replacement.process(&repaint);
        assert!(replacement.screen().contents().contains("MOSH_SESSION> "));

        session.cancel();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(2), task)
                .await
                .expect("cancelled Session did not stop")
                .expect("Session task panicked")
                .expect("Session task failed"),
            SessionExit::Cancelled
        );
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}

// NexusSSH (P1): Geht ein Fragment einer Anweisung verloren, schickt mosh-server den nächsten Stand
// unter neuer Kennung. Im Original endete die Sitzung beim ersten solchen Verlust mit
// `ResourceLimit`; jetzt verdrängt die neue Kennung die unvollständige Anweisung.
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_private_session_survives_lost_fragments() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap, mut server) = start_stock_server("60800:60819");
    let server_addr = bootstrap.server_addr();

    stock_runtime().block_on(async {
        let relay = UdpRelay::start(server_addr).await;
        let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
        let (commands, mut output, task) =
            start_private_session_sized(bootstrap, 120, 40, PredictionMode::Never).await;
        let mut projection = vt100::Parser::new(40, 120, 0);

        wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
        relay.drop_every_nth_large_server_datagram(5);
        // Zufallsbytes als Hex lassen sich kaum packen: Auf 120 × 40 braucht ein neuer Stand des
        // Bildschirms mehrere Fragmente. Die Marke steht nicht im Echo der Zeile (`%s`).
        commands
            .send(SessionCommand::Input(
                b"i=0; while [ $i -lt 300 ]; do head -c 900 /dev/urandom | od -An -tx1; i=$((i+1)); done; printf 'LOSSY_%s\\n' DONE\n"
                    .to_vec(),
            ))
            .await
            .expect("Session command queue closed before lossy output");
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            "LOSSY_DONE",
            Duration::from_secs(60),
        )
        .await;
        assert!(
            relay.dropped_packets() > 0,
            "relay dropped no server datagram"
        );
        assert!(!task.is_finished(), "lost fragments ended the Session");

        cancel_session(&commands, task).await;
    });

    assert!(server.terminate(), "stock server fixture did not clean up");
}
