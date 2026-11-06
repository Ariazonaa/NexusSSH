use super::*;

const RTO_MS: u64 = 50;

fn instruction(
    base_state: u64,
    new_state: u64,
    acknowledged_state: u64,
    discard_before_state: u64,
) -> TransportInstruction {
    TransportInstruction {
        protocol_version: 2,
        base_state,
        new_state,
        acknowledged_state,
        discard_before_state,
        state_difference: vec![u8::try_from(new_state).unwrap_or(0xff)],
        chaff: Vec::new(),
    }
}

fn apply(state: &mut SynchronizationState, instruction: &TransportInstruction) -> ReceiveCommit {
    let transition = state.begin_receive(instruction).unwrap();
    let RemoteStateDisposition::Apply(plan) = transition.remote_state else {
        panic!("expected an applicable remote state")
    };
    state.commit_receive(plan).unwrap()
}

#[test]
fn shutdown_target_never_enters_ordinary_remote_history() {
    let mut state = SynchronizationState::new();
    let mut shutdown = instruction(0, crate::instruction::SHUTDOWN_STATE, 0, 0);
    shutdown.state_difference = b"final".to_vec();

    let transition = state.begin_shutdown_receive(&shutdown).unwrap();
    assert_eq!(transition.base_state, 0);
    assert_eq!(transition.difference, b"final");
    assert_eq!(state.remote_latest(), 0);

    apply(&mut state, &instruction(0, 1, 0, 0));
    assert_eq!(state.remote_latest(), 1);
}

#[test]
fn initial_change_is_sent_from_zero_and_acknowledged() {
    let mut state = SynchronizationState::new();
    assert_eq!(state.advance_local().unwrap(), 1);
    let plan = state.plan_send(0, RTO_MS).unwrap();
    assert_eq!(plan.base_state, 0);
    assert_eq!(plan.target_state, 1);
    assert_eq!(plan.acknowledged_state, 0);
    assert_eq!(plan.throwaway_state, 0);
    let encoded = plan.instruction(vec![1], vec![0]);
    assert_eq!(encoded.protocol_version, PROTOCOL_VERSION);
    assert_eq!(encoded.base_state, 0);
    assert_eq!(encoded.new_state, 1);
    assert_eq!(encoded.acknowledged_state, 0);
    assert_eq!(encoded.discard_before_state, 0);
    state.commit_send(plan).unwrap();

    let peer_instruction = instruction(0, 1, 1, 0);
    let transition = state.begin_receive(&peer_instruction).unwrap();
    assert_eq!(
        transition.acknowledgement,
        AcknowledgementDisposition::Advanced { from: 0, to: 1 }
    );
    assert_eq!(state.known_receiver_state(), 1);
}

#[test]
fn local_changes_coalesce_to_the_latest_state() {
    let mut state = SynchronizationState::new();
    for expected in 1..=3 {
        assert_eq!(state.advance_local().unwrap(), expected);
    }

    let plan = state.plan_send(10, RTO_MS).unwrap();
    assert_eq!(plan.base_state, 0);
    assert_eq!(plan.target_state, 3);
}

#[test]
fn recent_sent_state_is_assumed_until_its_rto_expires() {
    let mut state = SynchronizationState::new();
    state.advance_local().unwrap();
    let first = state.plan_send(100, RTO_MS).unwrap();
    state.commit_send(first).unwrap();
    state.advance_local().unwrap();

    assert_eq!(state.plan_send(149, RTO_MS).unwrap().base_state, 1);
    assert_eq!(state.plan_send(150, RTO_MS).unwrap().base_state, 0);
    assert_eq!(
        state.plan_send(99, RTO_MS),
        Err(SynchronizationError::TimeMovedBackwards)
    );
}

#[test]
fn an_empty_acknowledgement_allocates_a_new_transport_state() {
    let mut state = SynchronizationState::new();
    state.advance_local().unwrap();
    let first = state.plan_send(0, RTO_MS).unwrap();
    state.commit_send(first).unwrap();
    assert_eq!(
        state.plan_send(1, RTO_MS),
        Err(SynchronizationError::NoNewState)
    );

    assert_eq!(state.advance_local().unwrap(), 2);
    let acknowledgement = state.plan_send(1, RTO_MS).unwrap();
    assert_eq!(acknowledgement.base_state, 1);
    assert_eq!(acknowledgement.target_state, 2);
}

#[test]
fn acknowledgements_only_advance_to_retained_sent_states() {
    let mut state = SynchronizationState::new();
    state.advance_local().unwrap();
    let first = state.plan_send(0, RTO_MS).unwrap();
    state.commit_send(first).unwrap();
    state.advance_local().unwrap();
    let second = state.plan_send(1, RTO_MS).unwrap();
    state.commit_send(second).unwrap();

    let first_ack = instruction(0, 1, 1, 0);
    let transition = state.begin_receive(&first_ack).unwrap();
    assert_eq!(
        transition.acknowledgement,
        AcknowledgementDisposition::Advanced { from: 0, to: 1 }
    );
    let stale_ack = instruction(0, 2, 0, 0);
    let stale_transition = state.begin_receive(&stale_ack).unwrap();
    assert_eq!(
        stale_transition.acknowledgement,
        AcknowledgementDisposition::Stale
    );
    assert_eq!(state.known_receiver_state(), 1);

    let second_ack = instruction(0, 3, 2, 0);
    let second_transition = state.begin_receive(&second_ack).unwrap();
    assert_eq!(
        second_transition.acknowledgement,
        AcknowledgementDisposition::Advanced { from: 1, to: 2 }
    );

    state.advance_local().unwrap();
    let unsent_ack = instruction(0, 4, 3, 0);
    let unknown_transition = state.begin_receive(&unsent_ack).unwrap();
    assert_eq!(
        unknown_transition.acknowledgement,
        AcknowledgementDisposition::Unknown
    );
}

#[test]
fn receiver_constructs_reordered_states_once() {
    let mut state = SynchronizationState::new();
    apply(&mut state, &instruction(0, 2, 0, 0));
    assert_eq!(state.remote_latest(), 2);
    apply(&mut state, &instruction(0, 1, 0, 0));
    assert_eq!(state.remote_latest(), 2);

    let repeated = instruction(0, 1, 0, 0);
    let duplicate = state.begin_receive(&repeated).unwrap();
    assert!(matches!(
        duplicate.remote_state,
        RemoteStateDisposition::AlreadyConstructed
    ));
}

#[test]
fn receiver_waits_for_a_missing_reference_without_losing_the_ack() {
    let mut state = SynchronizationState::new();
    state.advance_local().unwrap();
    let sent = state.plan_send(0, RTO_MS).unwrap();
    state.commit_send(sent).unwrap();

    let missing_base = instruction(9, 10, 1, 0);
    let transition = state.begin_receive(&missing_base).unwrap();
    assert_eq!(
        transition.acknowledgement,
        AcknowledgementDisposition::Advanced { from: 0, to: 1 }
    );
    assert!(matches!(
        transition.remote_state,
        RemoteStateDisposition::MissingReference
    ));
}

#[test]
fn throwaway_floor_discards_old_references_after_a_new_latest_state() {
    let mut state = SynchronizationState::new();
    apply(&mut state, &instruction(0, 1, 0, 0));
    let second = apply(&mut state, &instruction(1, 2, 0, 1));
    assert_eq!(second.discard_before_state, 1);
    let third = apply(&mut state, &instruction(2, 3, 0, 2));
    assert_eq!(third.discard_before_state, 2);

    let old_reference = instruction(1, 4, 0, 1);
    let old = state.begin_receive(&old_reference).unwrap();
    assert!(matches!(
        old.remote_state,
        RemoteStateDisposition::BelowThrowawayFloor
    ));
    assert_eq!(
        state.received_states.iter().copied().collect::<Vec<_>>(),
        [2, 3]
    );
}

#[test]
fn received_reference_storage_keeps_the_floor_and_newest_states_bounded() {
    let mut state = SynchronizationState::new();
    let mut first_eviction = None;
    for target in 1..=40 {
        let commit = apply(&mut state, &instruction(0, target, 0, 0));
        first_eviction = first_eviction.or(commit.capacity_evicted_state);
    }

    assert_eq!(first_eviction, Some(1));
    assert_eq!(state.received_states.len(), MAX_RECEIVED_REFERENCE_STATES);
    assert!(state.received_states.contains(&0));
    assert!(!state.received_states.contains(&1));
    assert!(state.received_states.contains(&40));

    let late_old = instruction(0, 1, 0, 0);
    let transition = state.begin_receive(&late_old).unwrap();
    assert!(matches!(
        transition.remote_state,
        RemoteStateDisposition::BelowRetentionWindow
    ));
    assert_eq!(state.received_states.len(), MAX_RECEIVED_REFERENCE_STATES);
    assert!(state.received_states.contains(&40));
}

#[test]
fn sent_state_storage_is_bounded_and_falls_back_to_the_known_reference() {
    let mut state = SynchronizationState::new();
    let mut first_eviction = None;
    for now_ms in 1..=40 {
        state.advance_local().unwrap();
        let plan = state.plan_send(now_ms, RTO_MS).unwrap();
        let commit = state.commit_send(plan).unwrap();
        first_eviction = first_eviction.or(commit.capacity_evicted_state);
    }

    assert_eq!(first_eviction, Some(1));
    assert_eq!(state.sent_states.len(), MAX_SENT_STATES_AFTER_ACK);
    assert!(!state.sent_states.iter().any(|sent| sent.number == 1));
    assert!(state.sent_states.iter().any(|sent| sent.number == 40));
    assert_eq!(state.plan_send(100, RTO_MS).unwrap().base_state, 0);
}

#[test]
fn plans_are_rejected_after_their_state_generation_changes() {
    let mut state = SynchronizationState::new();
    state.advance_local().unwrap();
    let send = state.plan_send(0, RTO_MS).unwrap();
    state.advance_local().unwrap();
    assert_eq!(
        state.commit_send(send),
        Err(SynchronizationError::StalePlan)
    );

    let first = instruction(0, 1, 0, 0);
    let transition = state.begin_receive(&first).unwrap();
    let RemoteStateDisposition::Apply(plan) = transition.remote_state else {
        panic!("expected apply plan")
    };
    apply(&mut state, &instruction(0, 2, 0, 0));
    assert_eq!(
        state.commit_receive(plan),
        Err(SynchronizationError::StalePlan)
    );
}

#[test]
fn invalid_numbers_and_timer_bounds_fail_before_mutation() {
    let mut state = SynchronizationState::new();
    assert_eq!(
        state.begin_receive(&instruction(1, 1, 0, 0)).unwrap_err(),
        SynchronizationError::InvalidStateTransition
    );
    assert_eq!(
        state.begin_receive(&instruction(1, 2, 0, 2)).unwrap_err(),
        SynchronizationError::InvalidThrowaway
    );
    assert_eq!(
        state.begin_receive(&instruction(0, 1, 1, 0)).unwrap_err(),
        SynchronizationError::InvalidAcknowledgement
    );
    let mut wrong_version = instruction(0, 1, 0, 0);
    wrong_version.protocol_version = 3;
    assert_eq!(
        state.begin_receive(&wrong_version).unwrap_err(),
        SynchronizationError::IncompatibleVersion
    );
    assert_eq!(
        state.plan_send(0, MIN_RETRANSMISSION_TIMEOUT_MS - 1),
        Err(SynchronizationError::InvalidRetransmissionTimeout)
    );
    assert_eq!(state.local_latest(), 0);
    assert_eq!(state.remote_latest(), 0);

    state.local_latest = u64::MAX;
    assert_eq!(
        state.advance_local(),
        Err(SynchronizationError::StateNumberExhausted)
    );
}
