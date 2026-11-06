use super::super::driver::{
    TransportSendOutcome, is_discarded_datagram_error, is_recoverable_established_io_error,
    local_bind_addr,
};
use super::super::*;

use std::io::ErrorKind;
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4};
use std::time::Duration;

use tokio::net::UdpSocket;
use tokio::time::Instant;

use crate::Bootstrap;
use crate::crypto::SessionKey;
use crate::fragment;
use crate::instruction::{PROTOCOL_VERSION, SHUTDOWN_STATE, TransportInstruction};
use crate::limits::{INITIAL_ATTACHMENT_TIMEOUT_MS, MAX_DATAGRAM_BYTES};
use crate::packet::{Direction, PacketCodec, PacketReceiver};
use crate::prediction::PredictionMode;
use crate::test_support::STOCK_1_4_0_KEY_BYTES;

#[test]
fn cancellation_closes_the_private_driver_without_a_server() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            reachability: _reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        tokio::task::yield_now().await;
        assert!(!task.is_finished());
        cancellation.send_replace(true);
        let close = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .expect("cancelled Session did not stop")
            .unwrap()
            .unwrap();

        assert_eq!(close, SessionExit::Cancelled);
    });
}

#[test]
fn cancellation_preempts_a_blocked_output_reservation() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.output_requested = true;
        driver.output_tx.try_send(b"occupied".to_vec()).unwrap();
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            reachability: _reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        tokio::task::yield_now().await;
        assert!(!task.is_finished());
        cancellation.send_replace(true);
        let close = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .expect("output-blocked Session ignored cancellation")
            .unwrap()
            .unwrap();

        assert_eq!(close, SessionExit::Cancelled);
    });
}

#[test]
fn cancellation_preempts_a_blocked_final_output_drain() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.output_requested = true;
        driver.output_tx.try_send(b"occupied".to_vec()).unwrap();
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            reachability: _reachability,
        } = channels;
        let task = tokio::spawn(async move {
            driver
                .finish_graceful_close(SessionExit::RemoteClosed)
                .await
        });

        tokio::task::yield_now().await;
        assert!(!task.is_finished());
        cancellation.send_replace(true);
        let close = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .expect("final-output drain ignored cancellation")
            .unwrap()
            .unwrap();

        assert_eq!(close, SessionExit::Cancelled);
    });
}

#[test]
fn initial_attachment_timeout_is_reported_before_any_peer_state() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.started_at = Instant::now() - Duration::from_millis(INITIAL_ATTACHMENT_TIMEOUT_MS);
        let mut state = channels.state;

        assert_eq!(
            Box::pin(SessionTask { driver }.run()).await,
            Err(SessionError::ConnectionTimeout)
        );
        assert!(state.changed().await.is_ok());
        assert_eq!(*state.borrow_and_update(), SessionState::Closed);
    });
}

#[test]
fn active_session_retries_permission_denied_send_without_spinning() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_send_error(ErrorKind::PermissionDenied);
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            reachability: _reachability,
        } = channels;
        let task = tokio::spawn(driver.run());
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];

        assert!(
            tokio::time::timeout(Duration::from_millis(250), server.recv_from(&mut inbound))
                .await
                .is_err(),
            "temporary send failure retried without bounded pacing"
        );
        assert!(
            !task.is_finished(),
            "temporary local send failure terminated an active Session"
        );
        tokio::time::timeout(Duration::from_secs(2), server.recv_from(&mut inbound))
            .await
            .expect("active Session did not retry the temporary send failure")
            .unwrap();

        cancellation.send_replace(true);
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

#[test]
fn established_io_error_allowlist_excludes_other_permanent_and_ambiguous_failures() {
    for kind in [
        ErrorKind::NetworkDown,
        ErrorKind::NetworkUnreachable,
        ErrorKind::HostUnreachable,
        ErrorKind::AddrNotAvailable,
        ErrorKind::PermissionDenied,
    ] {
        assert!(
            is_recoverable_established_io_error(kind),
            "missing {kind:?}"
        );
    }
    for kind in [
        ErrorKind::Other,
        ErrorKind::NotConnected,
        ErrorKind::ConnectionReset,
        ErrorKind::WriteZero,
    ] {
        assert!(
            !is_recoverable_established_io_error(kind),
            "overly broad recovery for {kind:?}"
        );
    }
}

#[test]
fn other_local_send_error_still_terminates_an_active_session() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, _channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_send_error(ErrorKind::Other);

        assert_eq!(driver.run().await, Err(DriverError::Io(ErrorKind::Other)));
    });
}

#[test]
fn active_session_defers_permission_denied_receive_and_resumes_on_the_same_socket() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_receive_errors([ErrorKind::PermissionDenied]);
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            mut reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (initial, client_addr) =
            receive_instruction(&server, &mut receiver, &mut inbound).await;
        let response = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: initial.new_state,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        send_server_instruction(&server, &codec, client_addr, &response).await;

        assert!(
            tokio::time::timeout(Duration::from_millis(250), reachability.changed())
                .await
                .is_err(),
            "temporary receive error retried without bounded pacing"
        );
        tokio::time::timeout(Duration::from_secs(2), reachability.changed())
            .await
            .expect("active Session did not resume UDP receive after the retry delay")
            .unwrap();
        assert_eq!(
            *reachability.borrow_and_update(),
            SessionReachability::Responsive
        );
        assert!(!task.is_finished());

        cancellation.send_replace(true);
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

#[test]
fn recoverable_receive_errors_before_attachment_remain_explicit() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        for kind in [
            ErrorKind::NetworkDown,
            ErrorKind::NetworkUnreachable,
            ErrorKind::HostUnreachable,
            ErrorKind::AddrNotAvailable,
            ErrorKind::PermissionDenied,
        ] {
            let bootstrap = Bootstrap::parse(
                Ipv4Addr::LOCALHOST,
                b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
            )
            .unwrap();
            let (mut driver, _channels) =
                SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                    .await
                    .unwrap();
            driver.inject_receive_errors([kind]);

            assert_eq!(driver.run().await, Err(DriverError::Io(kind)));
        }
    });
}

#[test]
fn other_receive_error_still_terminates_an_active_session() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, _channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_receive_errors([ErrorKind::Other]);

        assert_eq!(driver.run().await, Err(DriverError::Io(ErrorKind::Other)));
    });
}

#[test]
fn cancellation_preempts_a_deferred_permission_denied_receive_retry() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_receive_errors([ErrorKind::PermissionDenied]);
        let cancellation = channels.cancellation;
        let task = tokio::spawn(driver.run());

        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(!task.is_finished());
        cancellation.send_replace(true);
        assert_eq!(
            tokio::time::timeout(Duration::from_millis(250), task)
                .await
                .expect("cancellation waited for the deferred receive deadline")
                .unwrap()
                .unwrap(),
            SessionExit::Cancelled
        );
    });
}

#[test]
fn recoverable_send_errors_before_attachment_remain_explicit() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        for kind in [
            ErrorKind::NetworkDown,
            ErrorKind::NetworkUnreachable,
            ErrorKind::HostUnreachable,
            ErrorKind::AddrNotAvailable,
            ErrorKind::PermissionDenied,
        ] {
            let bootstrap = Bootstrap::parse(
                Ipv4Addr::LOCALHOST,
                b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
            )
            .unwrap();
            let (mut driver, _channels) =
                SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                    .await
                    .unwrap();
            driver.inject_send_error(kind);

            assert_eq!(driver.run().await, Err(DriverError::Io(kind)));
        }
    });
}

#[test]
fn cancellation_preempts_a_deferred_temporary_send_retry() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_send_error(ErrorKind::NetworkUnreachable);
        let cancellation = channels.cancellation;
        let task = tokio::spawn(driver.run());

        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(!task.is_finished());
        cancellation.send_replace(true);
        assert_eq!(
            tokio::time::timeout(Duration::from_millis(250), task)
                .await
                .expect("cancellation waited for the deferred send deadline")
                .unwrap()
                .unwrap(),
            SessionExit::Cancelled
        );
    });
}

#[test]
fn graceful_close_keeps_its_bound_during_temporary_send_failures() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_send_outcomes(std::iter::repeat_n(Some(ErrorKind::NetworkUnreachable), 8));
        channels.graceful_close.send_replace(true);
        let started = Instant::now();

        assert_eq!(
            tokio::time::timeout(Duration::from_millis(4_500), driver.run())
                .await
                .expect("temporary send failures exceeded the graceful-close bound")
                .unwrap(),
            SessionExit::LocalClosed
        );
        assert!(started.elapsed() >= Duration::from_millis(3_900));
    });
}

#[test]
fn graceful_close_keeps_its_bound_during_permission_denied_receive_failures() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            b"MOSH CONNECT 65000 4NeCCgvZFe2RnPgrcU1PQw",
        )
        .unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_receive_errors(std::iter::repeat_n(ErrorKind::PermissionDenied, 8));
        channels.graceful_close.send_replace(true);
        let started = Instant::now();

        assert_eq!(
            tokio::time::timeout(Duration::from_millis(4_500), driver.run())
                .await
                .expect("receive retries exceeded the graceful-close bound")
                .unwrap(),
            SessionExit::LocalClosed
        );
        assert!(started.elapsed() >= Duration::from_millis(3_900));
    });
}

#[test]
fn fragmented_retry_restarts_with_a_fresh_identifier() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (mut driver, _channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        driver.state_tx.send_replace(SessionState::Active);
        driver.inject_send_outcomes([None, Some(ErrorKind::NetworkUnreachable)]);
        let instruction = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: 0,
            discard_before_state: 0,
            state_difference: deterministic_noise(16 * 1024),
            chaff: vec![0],
        };

        assert_eq!(
            driver
                .send_transport_instruction_for_test(&instruction, 0)
                .await
                .unwrap(),
            TransportSendOutcome::Deferred
        );
        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (length, _) =
            tokio::time::timeout(Duration::from_secs(1), server.recv_from(&mut inbound))
                .await
                .expect("first fragment was not sent before the injected failure")
                .unwrap();
        let opened = receiver.open(&mut inbound[..length]).unwrap();
        let abandoned = fragment::decode(opened.plaintext).unwrap();
        assert_eq!(abandoned.identifier, 0);
        assert_eq!(abandoned.number, 0);
        assert!(!abandoned.is_final);

        assert_eq!(
            driver
                .send_transport_instruction_for_test(&instruction, 1_000)
                .await
                .unwrap(),
            TransportSendOutcome::Sent
        );
        let mut numbers = Vec::new();
        loop {
            let (length, _) =
                tokio::time::timeout(Duration::from_secs(1), server.recv_from(&mut inbound))
                    .await
                    .expect("replacement fragmented instruction was incomplete")
                    .unwrap();
            let opened = receiver.open(&mut inbound[..length]).unwrap();
            let fragment = fragment::decode(opened.plaintext).unwrap();
            assert_eq!(fragment.identifier, 1);
            numbers.push(fragment.number);
            if fragment.is_final {
                break;
            }
        }
        assert!(numbers.len() > 1);
        assert!(
            numbers
                .iter()
                .copied()
                .eq(0..u16::try_from(numbers.len()).unwrap())
        );
    });
}

#[test]
fn temporary_send_retry_is_isolated_between_sessions() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let first_server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let second_server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let first_bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            format!(
                "MOSH CONNECT {} 4NeCCgvZFe2RnPgrcU1PQw",
                first_server.local_addr().unwrap().port()
            )
            .as_bytes(),
        )
        .unwrap();
        let second_bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            format!(
                "MOSH CONNECT {} 4NeCCgvZFe2RnPgrcU1PQw",
                second_server.local_addr().unwrap().port()
            )
            .as_bytes(),
        )
        .unwrap();
        let (mut first, first_channels) =
            SessionDriver::connect(first_bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        let (second, second_channels) =
            SessionDriver::connect(second_bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        first.state_tx.send_replace(SessionState::Active);
        second.state_tx.send_replace(SessionState::Active);
        first.inject_send_error(ErrorKind::NetworkUnreachable);
        let first_cancellation = first_channels.cancellation;
        let second_cancellation = second_channels.cancellation;
        let first_task = tokio::spawn(first.run());
        let second_task = tokio::spawn(second.run());
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];

        tokio::time::timeout(
            Duration::from_millis(250),
            second_server.recv_from(&mut inbound),
        )
        .await
        .expect("one Session's retry delay leaked into another Session")
        .unwrap();
        assert!(
            tokio::time::timeout(
                Duration::from_millis(250),
                first_server.recv_from(&mut inbound),
            )
            .await
            .is_err(),
            "failing Session retried without its own pacing"
        );
        tokio::time::timeout(Duration::from_secs(2), first_server.recv_from(&mut inbound))
            .await
            .expect("failing Session did not retry independently")
            .unwrap();

        first_cancellation.send_replace(true);
        second_cancellation.send_replace(true);
        assert_eq!(first_task.await.unwrap().unwrap(), SessionExit::Cancelled);
        assert_eq!(second_task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

#[test]
fn permission_denied_receive_retry_is_isolated_between_sessions() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let first_server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let second_server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let first_bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            format!(
                "MOSH CONNECT {} 4NeCCgvZFe2RnPgrcU1PQw",
                first_server.local_addr().unwrap().port()
            )
            .as_bytes(),
        )
        .unwrap();
        let second_bootstrap = Bootstrap::parse(
            Ipv4Addr::LOCALHOST,
            format!(
                "MOSH CONNECT {} 4NeCCgvZFe2RnPgrcU1PQw",
                second_server.local_addr().unwrap().port()
            )
            .as_bytes(),
        )
        .unwrap();
        let (mut first, first_channels) =
            SessionDriver::connect(first_bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        let (second, second_channels) =
            SessionDriver::connect(second_bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        first.state_tx.send_replace(SessionState::Active);
        second.state_tx.send_replace(SessionState::Active);
        first.inject_receive_errors([ErrorKind::PermissionDenied]);
        let first_cancellation = first_channels.cancellation;
        let second_cancellation = second_channels.cancellation;
        let mut second_reachability = second_channels.reachability;
        let first_task = tokio::spawn(first.run());
        let second_task = tokio::spawn(second.run());

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (initial, client_addr) =
            receive_instruction(&second_server, &mut receiver, &mut inbound).await;
        let response = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: initial.new_state,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        send_server_instruction(&second_server, &codec, client_addr, &response).await;

        tokio::time::timeout(Duration::from_millis(250), second_reachability.changed())
            .await
            .expect("one Session's receive delay leaked into another Session")
            .unwrap();
        assert_eq!(
            *second_reachability.borrow_and_update(),
            SessionReachability::Responsive
        );
        assert!(!first_task.is_finished());
        assert!(!second_task.is_finished());

        first_cancellation.send_replace(true);
        second_cancellation.send_replace(true);
        assert_eq!(first_task.await.unwrap().unwrap(), SessionExit::Cancelled);
        assert_eq!(second_task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

fn deterministic_noise(length: usize) -> Vec<u8> {
    let mut state = 0x1234_5678_u32;
    (0..length)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            state.to_le_bytes()[0]
        })
        .collect()
}

#[test]
fn graceful_close_retransmits_after_loss_and_accepts_a_later_acknowledgement() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (session, task) = Session::connect(bootstrap, 80, 24).await.unwrap();
        let task = tokio::spawn(task.run());
        session
            .send_input(b"ordered-before-close".to_vec())
            .await
            .unwrap();
        session.close();

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];

        let (first, client_addr) = receive_shutdown(&server, &mut receiver, &mut inbound).await;
        assert!(
            first
                .state_difference
                .windows(b"ordered-before-close".len())
                .any(|window| window == b"ordered-before-close"),
            "close difference omitted input accepted before close"
        );
        let false_shutdown = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: SHUTDOWN_STATE,
            acknowledged_state: SHUTDOWN_STATE,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        let wrong_key = SessionKey::new([0x42; 16]);
        let wrong_codec = PacketCodec::new(&wrong_key);
        send_server_instruction(&server, &wrong_codec, client_addr, &false_shutdown).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert!(
            !task.is_finished(),
            "unauthenticated shutdown stopped Session"
        );

        let (retry, retry_addr) = receive_shutdown(&server, &mut receiver, &mut inbound).await;
        assert_eq!(retry_addr, client_addr);
        assert_eq!(retry.base_state, first.base_state);
        assert_eq!(retry.new_state, SHUTDOWN_STATE);
        assert_eq!(retry.state_difference, first.state_difference);

        let acknowledgement = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: SHUTDOWN_STATE,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        send_server_instruction(&server, &codec, client_addr, &acknowledgement).await;

        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("graceful close ignored a later authenticated acknowledgement")
            .unwrap()
            .unwrap();
        assert_eq!(exit, SessionExit::LocalClosed);
    });
}

#[test]
fn simultaneous_authenticated_shutdown_is_acknowledged_and_reports_local_close() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (session, task) = Session::connect(bootstrap, 80, 24).await.unwrap();
        let task = tokio::spawn(task.run());
        session.close();

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (_local_shutdown, client_addr) =
            receive_shutdown(&server, &mut receiver, &mut inbound).await;
        let simultaneous = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: SHUTDOWN_STATE,
            acknowledged_state: SHUTDOWN_STATE,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        send_server_instruction(&server, &codec, client_addr, &simultaneous).await;

        let acknowledgement = loop {
            let (instruction, _) = receive_instruction(&server, &mut receiver, &mut inbound).await;
            if !instruction.is_shutdown() && instruction.acknowledged_state == SHUTDOWN_STATE {
                break instruction;
            }
        };
        assert_ne!(acknowledgement.new_state, SHUTDOWN_STATE);
        let exit = tokio::time::timeout(Duration::from_secs(2), task)
            .await
            .expect("simultaneous graceful close did not stop")
            .unwrap()
            .unwrap();
        assert_eq!(exit, SessionExit::LocalClosed);
    });
}

async fn receive_shutdown(
    socket: &UdpSocket,
    receiver: &mut PacketReceiver,
    inbound: &mut [u8],
) -> (TransportInstruction, SocketAddr) {
    loop {
        let (instruction, source) = receive_instruction(socket, receiver, inbound).await;
        if instruction.is_shutdown() {
            return (instruction, source);
        }
    }
}

async fn receive_instruction(
    socket: &UdpSocket,
    receiver: &mut PacketReceiver,
    inbound: &mut [u8],
) -> (TransportInstruction, SocketAddr) {
    let (length, source) = tokio::time::timeout(Duration::from_secs(2), socket.recv_from(inbound))
        .await
        .expect("client did not send the expected graceful-close instruction")
        .unwrap();
    let opened = receiver.open(&mut inbound[..length]).unwrap();
    let decoded = fragment::decode(opened.plaintext).unwrap();
    let instruction = TransportInstruction::decode_zlib(decoded.body).unwrap();
    (instruction, source)
}

async fn send_server_instruction(
    socket: &UdpSocket,
    codec: &PacketCodec,
    client_addr: SocketAddr,
    instruction: &TransportInstruction,
) {
    send_numbered_server_instruction(socket, codec, client_addr, 0, instruction).await;
}

// NexusSSH (P9): mehrere Antworten des Servers brauchen steigende Nummern (sonst Wiederholung).
async fn send_numbered_server_instruction(
    socket: &UdpSocket,
    codec: &PacketCodec,
    client_addr: SocketAddr,
    sequence: u64,
    instruction: &TransportInstruction,
) {
    let compressed = instruction.encode_zlib().unwrap();
    let mut plaintext = [0_u8; MAX_DATAGRAM_BYTES];
    let plaintext_len = fragment::encode(0, None, 0, 0, true, &compressed, &mut plaintext).unwrap();
    let mut datagram = [0_u8; MAX_DATAGRAM_BYTES];
    let datagram_len = codec
        .seal(
            Direction::ServerToClient,
            sequence,
            &plaintext[..plaintext_len],
            &mut datagram,
        )
        .unwrap();
    socket
        .send_to(&datagram[..datagram_len], client_addr)
        .await
        .unwrap();
}

// NexusSSH (P3): Ein ICMP „Port unreachable“ (Windows: ConnectionReset beim Empfangen) oder ein zu
// großes Datagramm (WSAEMSGSIZE) beendet die Sitzung in keinem Zustand.
#[test]
fn reset_and_oversized_receive_errors_drop_only_that_datagram() {
    assert!(is_discarded_datagram_error(&std::io::Error::from(
        ErrorKind::ConnectionReset
    )));
    assert!(!is_discarded_datagram_error(&std::io::Error::from(
        ErrorKind::Other
    )));
    assert_eq!(
        is_discarded_datagram_error(&std::io::Error::from_raw_os_error(10040)),
        cfg!(windows)
    );
    assert!(!is_recoverable_established_io_error(
        ErrorKind::ConnectionReset
    ));

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (mut driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        // Noch vor der ersten Antwort des Servers (Zustand Connecting).
        driver.inject_receive_errors([ErrorKind::ConnectionReset, ErrorKind::ConnectionReset]);
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            mut reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (initial, client_addr) =
            receive_instruction(&server, &mut receiver, &mut inbound).await;
        assert!(!task.is_finished());
        let response = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: initial.new_state,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        send_server_instruction(&server, &codec, client_addr, &response).await;

        tokio::time::timeout(Duration::from_secs(2), reachability.changed())
            .await
            .expect("the Session did not receive after the dropped datagrams")
            .unwrap();
        assert_eq!(
            *reachability.borrow_and_update(),
            SessionReachability::Responsive
        );
        assert!(!task.is_finished());

        cancellation.send_replace(true);
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

// NexusSSH (P5): Ein Server, der nur über IPv6 erreichbar ist. Der Socket der Sitzung gehört zur
// Familie des Servers (`[::]:0`) und nimmt nur Datagramme von dessen Adresse an.
#[test]
fn a_session_runs_against_an_ipv6_server() {
    assert_eq!(
        local_bind_addr(SocketAddr::from((Ipv6Addr::LOCALHOST, 60_001))),
        SocketAddr::from((Ipv6Addr::UNSPECIFIED, 0))
    );
    assert_eq!(
        local_bind_addr(SocketAddr::from((Ipv4Addr::LOCALHOST, 60_001))),
        SocketAddr::from((Ipv4Addr::UNSPECIFIED, 0))
    );

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let Ok(server) = UdpSocket::bind((Ipv6Addr::LOCALHOST, 0)).await else {
            eprintln!("skipped: this machine has no IPv6 loopback");
            return;
        };
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv6Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (driver, channels) =
            SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Adaptive)
                .await
                .unwrap();
        let SessionChannels {
            commands: _commands,
            output: _output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            mut reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (initial, client_addr) =
            receive_instruction(&server, &mut receiver, &mut inbound).await;
        assert!(client_addr.is_ipv6());
        let response = TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: initial.new_state,
            discard_before_state: 0,
            state_difference: Vec::new(),
            chaff: vec![0],
        };
        // Von einem anderen Port derselben Adresse: verworfen.
        let stranger = UdpSocket::bind((Ipv6Addr::LOCALHOST, 0)).await.unwrap();
        send_server_instruction(&stranger, &codec, client_addr, &response).await;
        assert!(
            tokio::time::timeout(Duration::from_millis(300), reachability.changed())
                .await
                .is_err(),
            "a datagram from another port was taken"
        );
        send_server_instruction(&server, &codec, client_addr, &response).await;

        tokio::time::timeout(Duration::from_secs(2), reachability.changed())
            .await
            .expect("the IPv6 Session did not take the server's answer")
            .unwrap();
        assert_eq!(
            *reachability.borrow_and_update(),
            SessionReachability::Responsive
        );

        cancellation.send_replace(true);
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}

async fn next_paint(output: &mut tokio::sync::mpsc::Receiver<Vec<u8>>) -> Vec<u8> {
    tokio::time::timeout(Duration::from_secs(2), output.recv())
        .await
        .expect("no VT output")
        .expect("output closed")
}

// NexusSSH (P9): Glocke und Fenstertitel aus dem Zustand des Servers erreichen die VT-Ausgabe – der
// Titel als OSC 0 (bei jedem ganzen Bild und wenn er sich ändert), die Glocke als `BEL`, einmal je
// neuem Klingeln.
#[test]
// Die Ausgabe ist klein; ein eigenes Paket zum Zählen lohnt nicht.
#[allow(clippy::naive_bytecount)]
fn bell_and_title_reach_the_vt_output() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        let server = UdpSocket::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let port = server.local_addr().unwrap().port();
        let bootstrap_text = format!("MOSH CONNECT {port} 4NeCCgvZFe2RnPgrcU1PQw");
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, bootstrap_text.as_bytes()).unwrap();
        let (driver, channels) = SessionDriver::connect(bootstrap, 80, 24, PredictionMode::Never)
            .await
            .unwrap();
        let SessionChannels {
            commands,
            mut output,
            cancellation,
            graceful_close: _graceful_close,
            state: _state,
            reachability: _reachability,
        } = channels;
        let task = tokio::spawn(driver.run());

        let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
        let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
        let codec = PacketCodec::new(&key);
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (initial, client_addr) =
            receive_instruction(&server, &mut receiver, &mut inbound).await;
        let state = |base_state, new_state, bytes: &[u8]| TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state,
            new_state,
            acknowledged_state: initial.new_state,
            discard_before_state: 0,
            state_difference: crate::terminal::host_bytes_difference_for_test(bytes),
            chaff: vec![0],
        };
        let contains =
            |haystack: &[u8], needle: &[u8]| haystack.windows(needle.len()).any(|w| w == needle);

        // Ein Titel mit Steuerzeichen (U+009C beendete sonst die OSC-Folge) und eine Glocke.
        send_numbered_server_instruction(
            &server,
            &codec,
            client_addr,
            1,
            &state(0, 1, "\x07\x1b]0;one;\u{9c}two\x07A".as_bytes()),
        )
        .await;
        let first = next_paint(&mut output).await;
        assert!(contains(&first, b"\x1b]0;one;two\x07"), "{first:?}");
        assert!(first.ends_with(b"\x07\x07"), "{first:?}");

        // Nur die Glocke: kein Titel, ein BEL.
        send_numbered_server_instruction(&server, &codec, client_addr, 2, &state(1, 2, b"\x07B"))
            .await;
        let second = next_paint(&mut output).await;
        assert!(!contains(&second, b"\x1b]"), "{second:?}");
        assert_eq!(second.iter().filter(|byte| **byte == 0x07).count(), 1);

        // Weder Glocke noch neuer Titel: nichts davon.
        send_numbered_server_instruction(&server, &codec, client_addr, 3, &state(2, 3, b"C")).await;
        let third = next_paint(&mut output).await;
        assert!(!third.contains(&0x07), "{third:?}");

        // Ein ganzes Bild nennt den Titel wieder, ohne zu klingeln.
        commands.send(SessionCommand::Repaint).await.unwrap();
        let repaint = next_paint(&mut output).await;
        assert!(repaint.ends_with(b"\x1b]0;one;two\x07"), "{repaint:?}");

        cancellation.send_replace(true);
        assert_eq!(task.await.unwrap().unwrap(), SessionExit::Cancelled);
    });
}
