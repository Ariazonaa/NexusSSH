use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};

use crate::crypto::SessionKey;
use crate::fragment;
use crate::instruction::{PROTOCOL_VERSION, TransportInstruction};
use crate::limits::MAX_DATAGRAM_BYTES;
use crate::packet::{Direction, PacketCodec, PacketReceiver, SendSequence};
use crate::synchronization::{RemoteStateDisposition, SynchronizationState};

use super::virtual_network::{Impairment, SimTime, VirtualLink};
use super::*;

const TEST_KEY: [u8; 16] = [0x42; 16];

fn expect_send(poll: SchedulerPoll) -> WakePlan {
    let SchedulerPoll::Send(plan) = poll else {
        panic!("expected a send plan, got {poll:?}")
    };
    plan
}

fn endpoint(last_octet: u8, port: u16) -> SocketAddrV4 {
    SocketAddrV4::new(Ipv4Addr::new(192, 0, 2, last_octet), port)
}

fn seal_instruction(
    codec: &PacketCodec,
    direction: Direction,
    sequence: u64,
    instruction: &TransportInstruction,
) -> Vec<u8> {
    let compressed = instruction.encode_zlib().unwrap();
    let mut plaintext = vec![0_u8; MAX_DATAGRAM_BYTES];
    let plaintext_len = fragment::encode(
        u16::try_from(sequence).unwrap(),
        None,
        sequence,
        0,
        true,
        &compressed,
        &mut plaintext,
    )
    .unwrap();
    let mut datagram = vec![0_u8; MAX_DATAGRAM_BYTES];
    let datagram_len = codec
        .seal(
            direction,
            sequence,
            &plaintext[..plaintext_len],
            &mut datagram,
        )
        .unwrap();
    datagram.truncate(datagram_len);
    datagram
}

fn open_instruction(
    receiver: &mut PacketReceiver,
    expected_source: Option<SocketAddrV4>,
    source: SocketAddrV4,
    datagram: &mut [u8],
) -> TransportInstruction {
    let opened = match expected_source {
        Some(expected) => receiver
            .open_from(expected, SocketAddr::V4(source), datagram)
            .unwrap(),
        None => receiver.open(datagram).unwrap(),
    };
    let decoded_fragment = fragment::decode(opened.plaintext).unwrap();
    TransportInstruction::decode_zlib(decoded_fragment.body).unwrap()
}

#[test]
fn rtt_estimator_uses_tcp_updates_with_mosh_bounds() {
    let mut timing = RttEstimator::new();
    assert_eq!(timing.smoothed_rtt_ms(), None);
    assert_eq!(timing.retransmission_timeout_ms(), 1_000);
    assert_eq!(timing.frame_interval_ms(), 250);

    timing.observe_sample(100).unwrap();
    assert_eq!(timing.smoothed_rtt_ms(), Some(100));
    assert_eq!(timing.retransmission_timeout_ms(), 300);
    assert_eq!(timing.frame_interval_ms(), 50);

    timing.observe_sample(120).unwrap();
    assert_eq!(timing.smoothed_rtt_ms(), Some(103));
    assert_eq!(timing.retransmission_timeout_ms(), 273);
    assert_eq!(timing.frame_interval_ms(), 52);
}

#[test]
fn rtt_and_frame_values_are_bounded() {
    let mut timing = RttEstimator::new();
    timing.observe_sample(0).unwrap();
    assert_eq!(timing.retransmission_timeout_ms(), 50);
    assert_eq!(timing.frame_interval_ms(), 20);

    timing.observe_sample(MAX_RTT_SAMPLE_MS).unwrap();
    assert_eq!(timing.retransmission_timeout_ms(), 5_000);
    assert_eq!(timing.frame_interval_ms(), 250);
    assert_eq!(
        timing.observe_sample(MAX_RTT_SAMPLE_MS + 1),
        Err(TimingError::InvalidRttSample)
    );
}

#[test]
fn datagram_timestamps_adjust_reply_delay_and_measure_across_wrap() {
    let mut local = DatagramTiming::new(65_000);
    assert_eq!(
        local.outgoing(65_000).unwrap(),
        OutgoingTimestamps {
            timestamp: 65_000,
            timestamp_reply: None,
        }
    );

    assert_eq!(
        local
            .observe_received(0, 65_500, Some(65_020), 65_120)
            .unwrap(),
        RttSampleDisposition::Updated { sample_ms: 100 }
    );
    assert_eq!(local.estimator().smoothed_rtt_ms(), Some(100));
    assert_eq!(
        local.outgoing(65_170).unwrap(),
        OutgoingTimestamps {
            timestamp: 65_170,
            timestamp_reply: Some(14),
        }
    );

    assert_eq!(
        local.observe_received(1, 20, Some(65_500), 65_556).unwrap(),
        RttSampleDisposition::Updated { sample_ms: 56 }
    );
}

#[test]
fn timestamp_samples_ignore_reordering_staleness_and_ambiguity() {
    let mut timing = DatagramTiming::new(0);
    assert_eq!(
        timing.observe_received(5, 100, None, 100).unwrap(),
        RttSampleDisposition::NoReply
    );
    assert_eq!(
        timing.observe_received(4, 200, Some(100), 200).unwrap(),
        RttSampleDisposition::Reordered
    );
    assert_eq!(timing.outgoing(1_101).unwrap().timestamp_reply, None);

    assert_eq!(
        timing
            .observe_received(6, 300, Some(1_000), 61_001)
            .unwrap(),
        RttSampleDisposition::Implausible
    );
    assert_eq!(timing.estimator().smoothed_rtt_ms(), None);
    assert_eq!(
        timing.outgoing(61_000),
        Err(TimingError::TimeMovedBackwards)
    );

    let mut sentinel = DatagramTiming::new(0);
    sentinel.observe_received(0, 65_534, None, 0).unwrap();
    assert_eq!(sentinel.outgoing(1).unwrap().timestamp_reply, None);
}

#[test]
fn local_changes_collect_and_respect_the_frame_interval() {
    let mut timing = RttEstimator::new();
    timing.observe_sample(100).unwrap();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    scheduler.note_local_change(10).unwrap();
    assert_eq!(
        scheduler.poll(14, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 15 }
    );
    let first = expect_send(scheduler.poll(15, &timing, None).unwrap());
    assert_eq!(first.reasons, SendReasons::STATE_CHANGE);
    scheduler.commit_send(first).unwrap();

    scheduler.note_local_change(20).unwrap();
    assert_eq!(
        scheduler.poll(64, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 65 }
    );
    let second = expect_send(scheduler.poll(65, &timing, None).unwrap());
    assert!(second.reasons.contains(SendReasons::STATE_CHANGE));
}

#[test]
fn acknowledgement_deadline_can_preempt_frame_throttling() {
    let timing = RttEstimator::new();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    let first = expect_send(scheduler.poll(15, &timing, None).unwrap());
    scheduler.commit_send(first).unwrap();

    scheduler.note_local_change(20).unwrap();
    scheduler.note_acknowledgement_needed(25).unwrap();
    assert_eq!(
        scheduler.poll(124, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 125 }
    );
    let acknowledgement = expect_send(scheduler.poll(125, &timing, None).unwrap());
    assert!(!acknowledgement.reasons.contains(SendReasons::STATE_CHANGE));
    assert!(
        acknowledgement
            .reasons
            .contains(SendReasons::ACKNOWLEDGEMENT)
    );
    scheduler.commit_send(acknowledgement).unwrap();

    assert_eq!(
        scheduler.poll(126, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 3_125 }
    );
}

#[test]
fn retransmission_and_heartbeat_use_committed_send_time() {
    let mut timing = RttEstimator::new();
    timing.observe_sample(100).unwrap();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    let first = expect_send(scheduler.poll(15, &timing, None).unwrap());
    scheduler.commit_send(first).unwrap();

    assert_eq!(
        scheduler.poll(314, &timing, Some(15)).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 315 }
    );
    let retransmission = expect_send(scheduler.poll(315, &timing, Some(15)).unwrap());
    assert!(retransmission.reasons.contains(SendReasons::RETRANSMISSION));
    scheduler.commit_send(retransmission).unwrap();

    assert_eq!(
        scheduler.poll(3_314, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 3_315 }
    );
    let heartbeat = expect_send(scheduler.poll(3_315, &timing, None).unwrap());
    assert!(heartbeat.reasons.contains(SendReasons::HEARTBEAT));
}

#[test]
fn temporary_send_failure_defers_without_committing_pending_work() {
    let timing = RttEstimator::new();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    let failed = expect_send(scheduler.poll(15, &timing, None).unwrap());
    scheduler.defer_send(15, 1_000).unwrap();

    assert_eq!(
        scheduler.poll(15, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 1_015 }
    );
    assert_eq!(
        scheduler.poll(1_014, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 1_015 }
    );
    let retry = expect_send(scheduler.poll(1_015, &timing, None).unwrap());
    assert_eq!(retry.reasons, failed.reasons);
    scheduler.commit_send(retry).unwrap();
    assert_eq!(
        scheduler.poll(1_016, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 4_015 }
    );
}

#[test]
fn a_large_time_jump_coalesces_all_expired_reasons_into_one_send() {
    let timing = RttEstimator::new();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    scheduler.note_acknowledgement_needed(0).unwrap();
    let plan = expect_send(scheduler.poll(10_000, &timing, Some(0)).unwrap());
    assert_eq!(plan.reasons, SendReasons::ALL);
    scheduler.commit_send(plan).unwrap();
    assert_eq!(
        scheduler.poll(10_000, &timing, None).unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 13_000 }
    );
}

#[test]
fn stale_plans_backward_time_and_overflow_are_explicit() {
    let timing = RttEstimator::new();
    let mut scheduler = SendScheduler::new(0);
    scheduler.note_local_change(0).unwrap();
    let stale = expect_send(scheduler.poll(15, &timing, None).unwrap());
    scheduler.note_acknowledgement_needed(15).unwrap();
    assert_eq!(scheduler.commit_send(stale), Err(TimingError::StalePlan));
    assert_eq!(
        scheduler.poll(14, &timing, None),
        Err(TimingError::TimeMovedBackwards)
    );

    let mut overflow = SendScheduler::new(u64::MAX - 1);
    assert_eq!(
        overflow.poll(u64::MAX - 1, &timing, None),
        Err(TimingError::TimerExhausted)
    );
}

#[test]
fn generation_exhaustion_preserves_pending_scheduler_state() {
    let timing = RttEstimator::new();
    let mut note_failure = SendScheduler::new(0);
    note_failure.generation = u64::MAX;

    assert_eq!(
        note_failure.note_local_change(0),
        Err(TimingError::GenerationExhausted)
    );
    assert_eq!(note_failure.local_change_at_ms, None);
    assert_eq!(note_failure.generation, u64::MAX);

    let mut acknowledgement_failure = SendScheduler::new(0);
    acknowledgement_failure.generation = u64::MAX;
    assert_eq!(
        acknowledgement_failure.note_acknowledgement_needed(0),
        Err(TimingError::GenerationExhausted)
    );
    assert_eq!(acknowledgement_failure.acknowledgement_at_ms, None);
    assert_eq!(acknowledgement_failure.generation, u64::MAX);

    let mut commit_failure = SendScheduler::new(0);
    commit_failure.generation = u64::MAX - 1;
    commit_failure.note_local_change(0).unwrap();
    let plan = expect_send(commit_failure.poll(15, &timing, None).unwrap());

    assert_eq!(
        commit_failure.commit_send(plan),
        Err(TimingError::GenerationExhausted)
    );
    assert_eq!(commit_failure.local_change_at_ms, Some(0));
    assert_eq!(commit_failure.acknowledgement_at_ms, None);
    assert_eq!(commit_failure.last_send_ms, None);
    assert_eq!(commit_failure.generation, u64::MAX);
}

#[test]
#[expect(
    clippy::too_many_lines,
    reason = "one uninterrupted scenario proves loss, RTO recovery, roaming, and delayed acknowledgement ordering"
)]
fn deterministic_loss_recovery_survives_a_client_address_change() {
    let old_client = endpoint(1, 60_001);
    let new_client = endpoint(2, 60_002);
    let server = endpoint(10, 60_010);
    let key = SessionKey::new(TEST_KEY);
    let client_codec = PacketCodec::new(&key);
    let server_codec = PacketCodec::new(&key);
    let mut client_sequence = SendSequence::new();
    let mut server_sequence = SendSequence::new();
    let mut server_receiver = PacketReceiver::new(&key, Direction::ClientToServer);
    let mut client_receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut link = VirtualLink::new();
    let mut timing = RttEstimator::new();
    timing.observe_sample(100).unwrap();
    let mut scheduler = SendScheduler::new(0);
    let mut synchronization = SynchronizationState::new();

    synchronization.advance_local().unwrap();
    scheduler.note_local_change(0).unwrap();
    let first_wake = expect_send(scheduler.poll(15, &timing, None).unwrap());
    let first_send = synchronization
        .plan_send(15, timing.retransmission_timeout_ms())
        .unwrap();
    let first_instruction = first_send.instruction(vec![1], Vec::new());
    let first_packet = seal_instruction(
        &client_codec,
        Direction::ClientToServer,
        client_sequence.take().unwrap(),
        &first_instruction,
    );
    link.send(old_client, server, &first_packet, Impairment::Drop)
        .unwrap();
    synchronization.commit_send(first_send).unwrap();
    scheduler.commit_send(first_wake).unwrap();

    assert_eq!(link.advance_to(SimTime::from_millis(314)).unwrap(), []);
    assert_eq!(
        scheduler
            .poll(
                314,
                &timing,
                synchronization.latest_unacknowledged_sent_at_ms()
            )
            .unwrap(),
        SchedulerPoll::Pending { wake_at_ms: 315 }
    );

    let retry_wake = expect_send(
        scheduler
            .poll(
                315,
                &timing,
                synchronization.latest_unacknowledged_sent_at_ms(),
            )
            .unwrap(),
    );
    assert!(retry_wake.reasons.contains(SendReasons::RETRANSMISSION));
    let retry_send = synchronization
        .plan_send(315, timing.retransmission_timeout_ms())
        .unwrap();
    assert_eq!(retry_send.base_state, 0);
    let retry_instruction = retry_send.instruction(vec![1], Vec::new());
    let retry_packet = seal_instruction(
        &client_codec,
        Direction::ClientToServer,
        client_sequence.take().unwrap(),
        &retry_instruction,
    );
    link.send(
        new_client,
        server,
        &retry_packet,
        Impairment::Deliver { delay_ms: 0 },
    )
    .unwrap();
    synchronization.commit_send(retry_send).unwrap();
    scheduler.commit_send(retry_wake).unwrap();

    let mut delivered = link.advance_to(SimTime::from_millis(315)).unwrap();
    assert_eq!(delivered.len(), 1);
    assert_eq!(delivered[0].source, new_client);
    assert_eq!(delivered[0].destination, server);
    let received_retry = open_instruction(
        &mut server_receiver,
        None,
        delivered[0].source,
        &mut delivered[0].bytes,
    );
    assert_eq!(received_retry.base_state, 0);
    assert_eq!(received_retry.new_state, 1);

    let response = TransportInstruction {
        protocol_version: PROTOCOL_VERSION,
        base_state: 0,
        new_state: 1,
        acknowledged_state: 1,
        discard_before_state: 0,
        state_difference: vec![2],
        chaff: Vec::new(),
    };
    let response_packet = seal_instruction(
        &server_codec,
        Direction::ServerToClient,
        server_sequence.take().unwrap(),
        &response,
    );
    link.send(
        server,
        new_client,
        &response_packet,
        Impairment::Deliver { delay_ms: 10 },
    )
    .unwrap();
    let mut response_datagrams = link.advance_to(SimTime::from_millis(325)).unwrap();
    let received_response = open_instruction(
        &mut client_receiver,
        Some(server),
        response_datagrams[0].source,
        &mut response_datagrams[0].bytes,
    );
    let transition = synchronization.begin_receive(&received_response).unwrap();
    let RemoteStateDisposition::Apply(apply) = transition.remote_state else {
        panic!("expected the first server state to be applicable")
    };
    synchronization.commit_receive(apply).unwrap();
    scheduler.note_acknowledgement_needed(325).unwrap();
    assert_eq!(synchronization.known_receiver_state(), 1);
    assert_eq!(synchronization.latest_unacknowledged_sent_at_ms(), None);

    let acknowledgement_wake = expect_send(
        scheduler
            .poll(
                425,
                &timing,
                synchronization.latest_unacknowledged_sent_at_ms(),
            )
            .unwrap(),
    );
    assert!(
        acknowledgement_wake
            .reasons
            .contains(SendReasons::ACKNOWLEDGEMENT)
    );
    synchronization.advance_local().unwrap();
    let acknowledgement_send = synchronization
        .plan_send(425, timing.retransmission_timeout_ms())
        .unwrap();
    assert_eq!(acknowledgement_send.base_state, 1);
    assert_eq!(acknowledgement_send.target_state, 2);
    assert_eq!(acknowledgement_send.acknowledged_state, 1);
    synchronization.commit_send(acknowledgement_send).unwrap();
    scheduler.commit_send(acknowledgement_wake).unwrap();

    assert_eq!(link.cancel(), 0);
}
