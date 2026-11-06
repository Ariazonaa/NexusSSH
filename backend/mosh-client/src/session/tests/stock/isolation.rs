use super::*;

use std::net::SocketAddrV4;

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_public_sessions_isolate_packets_state_and_lifecycle() {
    assert_stock_1_4_0("mosh-server");
    let (bootstrap_a, mut server_a) = start_stock_server("60600:60619");
    let (bootstrap_b, mut server_b) = start_stock_server("60620:60639");
    let (bootstrap_replacement, mut server_replacement) = start_stock_server("60640:60659");

    stock_runtime().block_on(exercise_public_session_isolation(
        bootstrap_a,
        bootstrap_b,
        bootstrap_replacement,
    ));

    assert!(server_a.terminate(), "first stock server did not clean up");
    assert!(server_b.terminate(), "second stock server did not clean up");
    assert!(
        server_replacement.terminate(),
        "replacement stock server did not clean up"
    );
}

async fn exercise_public_session_isolation(
    bootstrap_a: Bootstrap,
    bootstrap_b: Bootstrap,
    bootstrap_replacement: Bootstrap,
) {
    let mut session_a = PublicStockSession::start(bootstrap_a, PredictionMode::Always).await;
    let mut session_b = PublicStockSession::start(bootstrap_b, PredictionMode::Never).await;
    assert_ne!(session_a.endpoint(), session_b.endpoint());

    session_a.send_and_wait("SESSION_A_ONLY").await;
    let old_a_datagram = session_a.last_server_datagram();
    session_b.inject(&old_a_datagram).await;
    assert!(!session_b.task_is_finished());

    session_b.send_and_wait("SESSION_B_ONLY").await;
    assert!(!session_a.contents().contains("SESSION_B_ONLY"));
    assert!(!session_b.contents().contains("SESSION_A_ONLY"));

    session_a.resize_and_report(90, 25).await;
    session_b.resize_and_report(70, 20).await;
    session_a.repaint_and_wait("SESSION_A_ONLY", "25 90").await;
    session_b.repaint_and_wait("SESSION_B_ONLY", "20 70").await;

    session_a.cancel().await;
    session_a.reject_late_packet(&old_a_datagram).await;
    session_b.prove_timer_continues().await;
    session_b.send_and_wait("SESSION_B_AFTER_A_CANCEL").await;

    let mut replacement =
        PublicStockSession::start(bootstrap_replacement, PredictionMode::Adaptive).await;
    assert_ne!(session_a.endpoint(), replacement.endpoint());
    assert_ne!(session_b.endpoint(), replacement.endpoint());
    replacement.inject(&old_a_datagram).await;
    assert!(!replacement.task_is_finished());
    replacement.send_and_wait("REPLACEMENT_SESSION_ONLY").await;
    assert!(!replacement.contents().contains("SESSION_A_ONLY"));

    session_b.close().await;
    assert!(!replacement.task_is_finished());
    replacement.send_and_wait("REPLACEMENT_AFTER_B_CLOSE").await;
    replacement.drop_owner().await;
}

struct PublicStockSession {
    session: Option<Session>,
    task: Option<JoinHandle<Result<SessionExit, SessionError>>>,
    projection: vt100::Parser,
    relay: UdpRelay,
}

impl PublicStockSession {
    async fn start(bootstrap: Bootstrap, prediction_mode: PredictionMode) -> Self {
        let relay = UdpRelay::start(bootstrap.server_addr()).await;
        let bootstrap = bootstrap.with_test_server_addr(relay.endpoint());
        let (mut session, task) =
            Session::connect_with_prediction_mode(bootstrap, 80, 24, prediction_mode)
                .await
                .expect("public Session setup failed");
        let task = tokio::spawn(task.run());
        let mut projection = vt100::Parser::new(24, 80, 0);
        wait_for_public_screen(&mut session, &mut projection, "MOSH_SESSION> ").await;
        assert_eq!(session.state(), SessionState::Active);
        Self {
            session: Some(session),
            task: Some(task),
            projection,
            relay,
        }
    }

    fn endpoint(&self) -> SocketAddrV4 {
        self.relay.endpoint()
    }

    fn contents(&self) -> String {
        self.projection.screen().contents()
    }

    fn task_is_finished(&self) -> bool {
        self.task.as_ref().is_none_or(JoinHandle::is_finished)
    }

    fn last_server_datagram(&self) -> Vec<u8> {
        self.relay.last_server_datagram()
    }

    async fn inject(&self, datagram: &[u8]) {
        self.relay.inject_to_client(datagram).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    async fn send_and_wait(&mut self, marker: &str) {
        let command = format!("printf '{marker}\\n'\n");
        let session = self.session.as_mut().expect("Session owner missing");
        session
            .send_input(command.into_bytes())
            .await
            .expect("public Session rejected input");
        wait_for_public_screen(session, &mut self.projection, marker).await;
    }

    async fn resize_and_report(&mut self, columns: u16, rows: u16) {
        let session = self.session.as_mut().expect("Session owner missing");
        session
            .resize(columns.into(), rows.into())
            .await
            .expect("public Session rejected resize");
        self.projection.screen_mut().set_size(rows, columns);
        session
            .send_input(b"stty size\n".to_vec())
            .await
            .expect("public Session rejected size query");
        let expected = format!("{rows} {columns}");
        wait_for_public_screen(session, &mut self.projection, &expected).await;
    }

    async fn repaint_and_wait(&mut self, marker: &str, size: &str) {
        let session = self.session.as_mut().expect("Session owner missing");
        session
            .request_repaint()
            .await
            .expect("public Session rejected repaint");
        let (rows, columns) = self.projection.screen().size();
        let mut replacement = vt100::Parser::new(rows, columns, 0);
        wait_for_public_screen(session, &mut replacement, marker).await;
        assert!(replacement.screen().contents().contains(size));
        self.projection = replacement;
    }

    async fn cancel(&mut self) {
        self.session
            .as_ref()
            .expect("Session owner missing")
            .cancel();
        assert_eq!(self.join_task().await, SessionExit::Cancelled);
        assert_eq!(
            self.session
                .as_ref()
                .expect("Session owner missing")
                .state(),
            SessionState::Closed
        );
    }

    async fn close(&mut self) {
        let mut session = self.session.take().expect("Session owner missing");
        session.close();
        let drain = tokio::spawn(async move {
            while session.next_output().await.is_some() {}
            session
        });
        assert_eq!(self.join_task().await, SessionExit::LocalClosed);
        self.session = Some(drain.await.expect("Session output drain panicked"));
        assert_eq!(
            self.session
                .as_ref()
                .expect("Session owner missing")
                .state(),
            SessionState::Closed
        );
    }

    async fn reject_late_packet(&mut self, datagram: &[u8]) {
        let session = self.session.as_mut().expect("Session owner missing");
        drain_public_output(session).await;
        self.relay.inject_to_client(datagram).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(session.next_output().await, None);
    }

    async fn prove_timer_continues(&self) {
        let before = self.relay.total_client_datagrams();
        tokio::time::sleep(Duration::from_millis(3_500)).await;
        assert!(self.relay.total_client_datagrams() > before);
        assert!(!self.task_is_finished());
    }

    async fn drop_owner(&mut self) {
        drop(self.session.take().expect("Session owner missing"));
        assert_eq!(self.join_task().await, SessionExit::OwnerDropped);
    }

    async fn join_task(&mut self) -> SessionExit {
        let task = self.task.take().expect("Session task missing");
        tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("public Session task did not stop")
            .expect("public Session task panicked")
            .expect("public Session task failed")
    }
}

async fn drain_public_output(session: &mut Session) {
    tokio::time::timeout(Duration::from_secs(1), async {
        while session.next_output().await.is_some() {}
    })
    .await
    .expect("closed public Session output did not finish");
}
