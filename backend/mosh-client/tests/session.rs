use std::net::Ipv4Addr;
use std::time::Duration;

use mosh_client::{
    Bootstrap, PredictionMode, Session, SessionCommandError, SessionError, SessionExit,
    SessionReachability, SessionState,
};

fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
}

fn bootstrap() -> Bootstrap {
    Bootstrap::parse(
        Ipv4Addr::LOCALHOST,
        b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
    )
    .unwrap()
}

#[test]
fn public_session_validates_initial_size_without_starting_a_task() {
    runtime().block_on(async {
        for (columns, rows) in [(0, 0), (80, 0), (0, 24)] {
            let result = Session::connect(bootstrap(), columns, rows).await;
            assert!(matches!(result, Err(SessionError::InvalidTerminalSize)));
        }
    });
}

#[test]
fn public_prediction_modes_default_to_adaptive_and_are_session_local() {
    assert_eq!(PredictionMode::default(), PredictionMode::Adaptive);
    runtime().block_on(async {
        let (always, always_task) =
            Session::connect_with_prediction_mode(bootstrap(), 80, 24, PredictionMode::Always)
                .await
                .unwrap();
        let (never, never_task) =
            Session::connect_with_prediction_mode(bootstrap(), 80, 24, PredictionMode::Never)
                .await
                .unwrap();
        let always_task = tokio::spawn(always_task.run());
        let never_task = tokio::spawn(never_task.run());

        always.cancel();
        assert_eq!(always_task.await.unwrap().unwrap(), SessionExit::Cancelled);
        assert!(!never_task.is_finished());

        never.cancel();
        assert_eq!(never_task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

#[test]
fn public_session_rejects_invalid_commands_before_queueing_them() {
    runtime().block_on(async {
        let (session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();

        assert_eq!(
            session.send_input(vec![0; 64 * 1024 + 1]).await,
            Err(SessionCommandError::InputTooLarge)
        );
        assert!(session.send_input(vec![0; 64 * 1024]).await.is_ok());
        assert_eq!(
            session.resize(0, 24).await,
            Err(SessionCommandError::InvalidTerminalSize)
        );

        session.cancel();
        assert_eq!(Box::pin(task.run()).await.unwrap(), SessionExit::Cancelled);
    });
}

#[test]
fn public_cancellation_is_idempotent_and_closes_lifecycle_and_output() {
    runtime().block_on(async {
        let (mut session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();
        assert_eq!(session.state(), SessionState::Connecting);
        let task = tokio::spawn(task.run());

        session.cancel();
        session.cancel();
        assert_eq!(
            session.send_input(b"ignored".to_vec()).await,
            Err(SessionCommandError::Closed)
        );

        let exit = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .expect("cancelled public Session did not stop")
            .unwrap()
            .unwrap();
        assert_eq!(exit, SessionExit::Cancelled);
        assert_eq!(session.state_changed().await, SessionState::Closed);
        assert_eq!(session.state(), SessionState::Closed);
        assert_eq!(session.next_output().await, None);
    });
}

#[test]
fn public_reachability_observer_is_independent_from_output_and_lifecycle() {
    runtime().block_on(async {
        let (mut session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();
        let mut reachability = session.subscribe_reachability();

        assert_eq!(session.reachability(), SessionReachability::AwaitingPeer);
        assert_eq!(reachability.current(), SessionReachability::AwaitingPeer);

        let task = tokio::spawn(task.run());
        session.cancel();
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
        assert_eq!(session.state_changed().await, SessionState::Closed);
        assert_eq!(session.next_output().await, None);
        assert_eq!(reachability.changed().await, None);
    });
}

#[test]
fn public_graceful_close_is_idempotent_and_cancellation_preempts_it() {
    runtime().block_on(async {
        let (mut session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();
        let task = tokio::spawn(task.run());

        session.close();
        session.close();
        assert_eq!(
            session.send_input(b"ignored".to_vec()).await,
            Err(SessionCommandError::Closed)
        );
        assert_eq!(
            session.resize(100, 30).await,
            Err(SessionCommandError::Closed)
        );
        assert_eq!(
            session.request_repaint().await,
            Err(SessionCommandError::Closed)
        );

        session.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .expect("cancellation did not preempt graceful close")
            .unwrap()
            .unwrap();
        assert_eq!(exit, SessionExit::Cancelled);
        assert_eq!(session.state_changed().await, SessionState::Closed);
        assert_eq!(session.next_output().await, None);
    });
}

#[test]
fn public_graceful_close_has_a_bounded_wait_without_a_peer() {
    runtime().block_on(async {
        let (session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();
        let task = tokio::spawn(task.run());
        let started = tokio::time::Instant::now();

        session.close();
        let exit = tokio::time::timeout(Duration::from_secs(5), task)
            .await
            .expect("graceful close did not honor its bounded wait")
            .unwrap()
            .unwrap();
        assert_eq!(exit, SessionExit::LocalClosed);
        assert!(started.elapsed() >= Duration::from_secs(4));
        assert_eq!(session.state(), SessionState::Closed);
    });
}

#[test]
fn dropping_the_public_owner_stops_its_task() {
    runtime().block_on(async {
        let (session, task) = Session::connect(bootstrap(), 80, 24).await.unwrap();
        drop(session);

        let exit = Box::pin(tokio::time::timeout(Duration::from_secs(1), task.run()))
            .await
            .expect("ownerless public Session did not stop")
            .unwrap();
        assert_eq!(exit, SessionExit::OwnerDropped);
    });
}
