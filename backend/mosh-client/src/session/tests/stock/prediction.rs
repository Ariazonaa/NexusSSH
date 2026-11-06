use super::*;

use std::time::Instant as StdInstant;

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_prediction_modes_preserve_convergence_and_adapt_to_latency() {
    const CASES: [(&str, &str, &str, u64); 3] = [
        ("60500:60509", "60510:60519", "60720:60729", 0),
        ("60520:60529", "60530:60539", "60730:60739", 40),
        ("60540:60549", "60550:60559", "60740:60749", 80),
    ];

    assert_stock_1_4_0("mosh-server");
    let runtime = stock_runtime();

    for (baseline_ports, always_ports, adaptive_ports, one_way_delay_ms) in CASES {
        let (bootstrap, mut server) = start_stock_server(baseline_ports);
        let baseline = runtime.block_on(measure_echo_latency(
            bootstrap,
            Duration::from_millis(one_way_delay_ms),
            PredictionMode::Never,
        ));
        assert!(server.terminate(), "stock server fixture did not clean up");

        let minimum = Duration::from_millis(one_way_delay_ms.saturating_mul(2));
        assert!(
            baseline.iter().all(|sample| *sample >= minimum),
            "a non-predictive echo bypassed the configured bidirectional relay delay"
        );

        let (bootstrap, mut server) = start_stock_server(always_ports);
        let always = runtime.block_on(measure_echo_latency(
            bootstrap,
            Duration::from_millis(one_way_delay_ms),
            PredictionMode::Always,
        ));
        assert!(server.terminate(), "stock server fixture did not clean up");

        let baseline_median = median_millis(&baseline);
        let always_median = median_millis(&always);
        if one_way_delay_ms > 0 {
            assert!(
                always_median.saturating_mul(2) < baseline_median,
                "always prediction did not materially reduce the median visible latency"
            );
        }

        let (bootstrap, mut server) = start_stock_server(adaptive_ports);
        let adaptive = runtime.block_on(measure_echo_latency(
            bootstrap,
            Duration::from_millis(one_way_delay_ms),
            PredictionMode::Adaptive,
        ));
        assert!(server.terminate(), "stock server fixture did not clean up");
        let adaptive_median = median_millis(&adaptive);
        if one_way_delay_ms == 0 {
            assert!(
                adaptive_median.saturating_mul(2) >= baseline_median,
                "adaptive prediction treated the low-delay link as slow"
            );
        } else {
            assert!(
                adaptive_median.saturating_mul(2) < baseline_median,
                "adaptive prediction did not activate on the delayed link"
            );
        }
        eprintln!(
            "echo latency: one-way delay={one_way_delay_ms} ms, never={:?} (median={baseline_median} ms), always={:?} (median={always_median} ms), adaptive={:?} (median={adaptive_median} ms)",
            baseline.iter().map(Duration::as_millis).collect::<Vec<_>>(),
            always.iter().map(Duration::as_millis).collect::<Vec<_>>(),
            adaptive.iter().map(Duration::as_millis).collect::<Vec<_>>(),
        );
    }
}

async fn measure_echo_latency(
    bootstrap: Bootstrap,
    one_way_delay: Duration,
    prediction_mode: PredictionMode,
) -> Vec<Duration> {
    let relay = UdpRelay::start_with_delay(bootstrap.server_addr(), one_way_delay).await;
    let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
    let (commands, mut output, task) =
        start_private_session_with_prediction_mode(bootstrap, prediction_mode).await;
    let mut projection = vt100::Parser::new(24, 80, 0);

    wait_for_screen(&mut output, &mut projection, "MOSH_SESSION> ").await;
    let mut visible = String::from("MOSH_SESSION> ");
    let mut samples = Vec::new();
    for byte in b"latencyprobe" {
        let started = StdInstant::now();
        commands
            .send(SessionCommand::Input(vec![*byte]))
            .await
            .expect("Session command queue closed during latency measurement");
        visible.push(char::from(*byte));
        wait_for_screen_with_timeout(
            &mut output,
            &mut projection,
            &visible,
            Duration::from_secs(5),
        )
        .await;
        samples.push(started.elapsed());
    }

    commands
        .send(SessionCommand::Input(
            b"\x15printf 'LATENCY_AUTHORITY_CONVERGED\\n'\n".to_vec(),
        ))
        .await
        .expect("Session command queue closed before latency convergence check");
    wait_for_screen_with_timeout(
        &mut output,
        &mut projection,
        "LATENCY_AUTHORITY_CONVERGED",
        Duration::from_secs(5),
    )
    .await;

    cancel_session(&commands, task).await;
    samples
}

fn median_millis(samples: &[Duration]) -> u128 {
    let mut millis = samples.iter().map(Duration::as_millis).collect::<Vec<_>>();
    millis.sort_unstable();
    millis[millis.len() / 2]
}

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_prediction_survives_total_udp_loss_after_confirmation() {
    const ONE_WAY_DELAY: Duration = Duration::from_millis(40);
    const ALWAYS_WARMUP: &str = "alwayswarmup";
    const NEVER_WARMUP: &str = "neverwarmup";

    assert_stock_1_4_0("mosh-server");
    let (always_bootstrap, mut always_server) = start_stock_server("60760:60779");
    let (never_bootstrap, mut never_server) = start_stock_server("60780:60799");

    stock_runtime().block_on(async {
        let mut always = PublicPredictionSession::start(
            always_bootstrap,
            PredictionMode::Always,
            ONE_WAY_DELAY,
        )
        .await;
        let mut never = PublicPredictionSession::start(
            never_bootstrap,
            PredictionMode::Never,
            ONE_WAY_DELAY,
        )
        .await;

        let confirmation = always
            .establish_confirmed_epoch(ALWAYS_WARMUP, ONE_WAY_DELAY)
            .await;
        let always_prefix = &ALWAYS_WARMUP[..confirmation.typed];
        let never_prefix = &NEVER_WARMUP[..confirmation.typed];
        never.type_authoritative(never_prefix).await;

        always.settle_authority().await;
        never.settle_authority().await;
        always.relay.pause();
        never.relay.pause();
        always.quiesce_after_pause(ONE_WAY_DELAY).await;
        never.quiesce_after_pause(ONE_WAY_DELAY).await;
        let always_before = always.relay_snapshot();
        let never_before = never.relay_snapshot();

        let always_started = StdInstant::now();
        always.send_byte(b'x').await;
        let predicted_marker = format!("{always_prefix}x");
        always
            .wait_for_visible(&predicted_marker, Duration::from_millis(250))
            .await;
        let predicted_after = always_started.elapsed();

        never.send_byte(b'y').await;
        assert!(
            tokio::time::timeout(Duration::from_millis(250), never.session.next_output())
                .await
                .is_err(),
            "Never emitted terminal output while all UDP traffic was blocked"
        );

        always.wait_for_dropped_packet(always_before.dropped).await;
        never.wait_for_dropped_packet(never_before.dropped).await;
        let always_during = always.relay_snapshot();
        let never_during = never.relay_snapshot();
        assert_relay_blocked("Always", always_before, always_during);
        assert_relay_blocked("Never", never_before, never_during);
        assert!(
            !never.contents().contains(&predicted_marker),
            "Always prediction leaked into the Never Session"
        );

        always.relay.resume();
        never.relay.resume();
        always
            .converge_with_marker("ALWAYS_AUTHORITY_CONVERGED")
            .await;
        never
            .converge_with_marker("NEVER_AUTHORITY_CONVERGED")
            .await;
        assert!(
            !always.contents().contains("NEVER_AUTHORITY_CONVERGED"),
            "Never authority leaked into the Always Session"
        );
        assert!(
            !never.contents().contains("ALWAYS_AUTHORITY_CONVERGED"),
            "Always authority leaked into the Never Session"
        );

        eprintln!(
            "public full-loss prediction: confirmation byte={}, confirmation latency={} ms, outage prediction latency={} ms, Always dropped={}, Never dropped={}",
            confirmation.typed,
            confirmation.latency.as_millis(),
            predicted_after.as_millis(),
            always_during.dropped - always_before.dropped,
            never_during.dropped - never_before.dropped,
        );

        always.cancel().await;
        never.cancel().await;
    });

    assert!(
        always_server.terminate(),
        "Always stock server fixture did not clean up"
    );
    assert!(
        never_server.terminate(),
        "Never stock server fixture did not clean up"
    );
}

struct PredictionConfirmation {
    typed: usize,
    latency: Duration,
}

struct PublicPredictionSession {
    session: Session,
    task: JoinHandle<Result<SessionExit, SessionError>>,
    projection: vt100::Parser,
    relay: UdpRelay,
}

impl PublicPredictionSession {
    async fn start(
        bootstrap: Bootstrap,
        prediction_mode: PredictionMode,
        one_way_delay: Duration,
    ) -> Self {
        let relay = UdpRelay::start_with_delay(bootstrap.server_addr(), one_way_delay).await;
        let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
        let (mut session, task) =
            Session::connect_with_prediction_mode(bootstrap, 80, 24, prediction_mode)
                .await
                .expect("public prediction Session setup failed");
        let task = tokio::spawn(task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);
        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        Self {
            session,
            task,
            projection,
            relay,
        }
    }

    fn contents(&self) -> String {
        self.projection.screen().contents()
    }

    async fn send_byte(&self, byte: u8) {
        self.session
            .send_input(vec![byte])
            .await
            .expect("public prediction Session rejected input");
    }

    async fn wait_for_visible(&mut self, marker: &str, timeout: Duration) {
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            let remaining = deadline
                .checked_duration_since(tokio::time::Instant::now())
                .unwrap_or_default();
            let output = tokio::time::timeout(remaining, self.session.next_output())
                .await
                .unwrap_or_else(|_| panic!("timed out waiting for public prediction {marker:?}"))
                .expect("public prediction Session output closed");
            self.projection.process(&output);
            if self.contents().contains(marker) {
                return;
            }
        }
    }

    async fn establish_confirmed_epoch(
        &mut self,
        input: &str,
        prediction_threshold: Duration,
    ) -> PredictionConfirmation {
        let mut visible = String::from("MOSH_SESSION> ");
        for (index, byte) in input.bytes().enumerate() {
            let started = StdInstant::now();
            self.send_byte(byte).await;
            visible.push(char::from(byte));
            self.wait_for_visible(&visible, Duration::from_secs(5))
                .await;
            let latency = started.elapsed();
            if latency < prediction_threshold {
                return PredictionConfirmation {
                    typed: index + 1,
                    latency,
                };
            }
        }
        panic!("Always never exposed a confirmed prediction through public Session output");
    }

    async fn type_authoritative(&mut self, input: &str) {
        let mut visible = String::from("MOSH_SESSION> ");
        for byte in input.bytes() {
            self.send_byte(byte).await;
            visible.push(char::from(byte));
            self.wait_for_visible(&visible, Duration::from_secs(5))
                .await;
        }
    }

    async fn settle_authority(&mut self) {
        tokio::time::sleep(Duration::from_millis(300)).await;
        while let Ok(Some(output)) =
            tokio::time::timeout(Duration::from_millis(100), self.session.next_output()).await
        {
            self.projection.process(&output);
        }
    }

    async fn quiesce_after_pause(&mut self, one_way_delay: Duration) {
        tokio::time::sleep(one_way_delay + one_way_delay + Duration::from_millis(100)).await;
        while let Ok(Some(output)) =
            tokio::time::timeout(Duration::from_millis(50), self.session.next_output()).await
        {
            self.projection.process(&output);
        }
    }

    fn relay_snapshot(&self) -> RelaySnapshot {
        let traffic = self.relay.traffic();
        RelaySnapshot {
            sent: traffic.sent.iter().sum(),
            received: traffic.received.iter().sum(),
            dropped: self.relay.dropped_packets(),
        }
    }

    async fn wait_for_dropped_packet(&self, before: usize) {
        tokio::time::timeout(Duration::from_secs(2), async {
            while self.relay.dropped_packets() == before {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .expect("relay did not drop outage traffic");
    }

    async fn converge_with_marker(&mut self, marker: &str) {
        let command = format!("\x15printf '{marker}\\n'\n");
        self.session
            .send_input(command.into_bytes())
            .await
            .expect("public prediction Session rejected convergence input");
        self.wait_for_visible(marker, Duration::from_secs(10)).await;
        self.session
            .request_repaint()
            .await
            .expect("public prediction Session rejected convergence repaint");
        let repaint = tokio::time::timeout(Duration::from_secs(2), self.session.next_output())
            .await
            .expect("public prediction convergence repaint timed out")
            .expect("public prediction output closed before convergence repaint");
        let mut authoritative = vt100::Parser::new(24, 80, 0);
        authoritative.process(&repaint);
        assert!(authoritative.screen().contents().contains(marker));
        self.projection = authoritative;
    }

    async fn cancel(self) {
        self.session.cancel();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(2), self.task)
                .await
                .expect("cancelled public prediction Session did not stop")
                .expect("public prediction Session task panicked")
                .expect("public prediction Session failed"),
            SessionExit::Cancelled
        );
    }
}

#[derive(Clone, Copy)]
struct RelaySnapshot {
    sent: usize,
    received: usize,
    dropped: usize,
}

fn assert_relay_blocked(mode: &str, before: RelaySnapshot, during: RelaySnapshot) {
    assert_eq!(
        during.sent, before.sent,
        "relay delivered the {mode} outage input to the stock server"
    );
    assert_eq!(
        during.received, before.received,
        "relay delivered stock output to the {mode} Session during the outage"
    );
}
