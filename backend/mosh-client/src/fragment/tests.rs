use super::*;
use crate::limits::MAX_COMPRESSED_INSTRUCTION_BYTES;
use proptest::prelude::*;

fn observed_fragment(identifier: u64, number: u16, is_final: bool, body: &[u8]) -> Fragment<'_> {
    Fragment {
        timestamp: 0,
        timestamp_reply: None,
        identifier,
        number,
        is_final,
        body,
    }
}

#[test]
fn single_fragment_round_trip_uses_big_endian_fields() {
    let mut output = [0_u8; 32];
    let len = encode(
        0x0102,
        None,
        0x0304_0506_0708_090a,
        7,
        true,
        b"zlib",
        &mut output,
    )
    .unwrap();

    assert_eq!(
        &output[..HEADER_BYTES],
        &[1, 2, 0xff, 0xff, 3, 4, 5, 6, 7, 8, 9, 10, 0x80, 7]
    );
    let fragment = decode(&output[..len]).unwrap();
    assert_eq!(fragment.timestamp, 0x0102);
    assert_eq!(fragment.timestamp_reply, None);
    assert_eq!(fragment.identifier, 0x0304_0506_0708_090a);
    assert_eq!(fragment.number, 7);
    assert!(fragment.is_final);
    assert_eq!(fragment.body, b"zlib");
}

#[test]
fn rejects_every_fragment_boundary_violation() {
    assert_eq!(
        decode(&[0; HEADER_BYTES - 1]).unwrap_err(),
        FragmentError::TooShort
    );
    assert_eq!(
        decode(&vec![0; HEADER_BYTES + MAX_FRAGMENT_BODY_BYTES + 1]).unwrap_err(),
        FragmentError::BodyTooLarge
    );

    let mut output = vec![0_u8; HEADER_BYTES + MAX_FRAGMENT_BODY_BYTES];
    assert_eq!(
        encode(
            0,
            Some(1),
            0,
            MAX_FRAGMENT_NUMBER + 1,
            false,
            &[],
            &mut output,
        ),
        Err(FragmentError::FragmentNumberOutOfRange)
    );
    assert_eq!(
        encode(
            0,
            Some(1),
            0,
            0,
            false,
            &vec![0; MAX_FRAGMENT_BODY_BYTES + 1],
            &mut output,
        ),
        Err(FragmentError::BodyTooLarge)
    );
    assert_eq!(
        encode(0, Some(1), 0, 0, false, b"x", &mut [0; HEADER_BYTES]),
        Err(FragmentError::OutputTooSmall)
    );
}

#[test]
fn reassembles_out_of_order_fragments_and_fast_paths_one_fragment() {
    let mut reassembler = FragmentReassembler::new(0);
    assert_eq!(
        reassembler
            .push(0, observed_fragment(1, 0, true, b"single"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"single".to_vec())
    );

    assert_eq!(
        reassembler
            .push(1, observed_fragment(2, 2, true, b"c"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(
        reassembler
            .push(2, observed_fragment(2, 0, false, b"a"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(
        reassembler
            .push(3, observed_fragment(2, 1, false, b"b"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"abc".to_vec())
    );
    assert_eq!(reassembler.incomplete_count(), 0);
    assert_eq!(reassembler.stored_bytes(), 0);
}

#[test]
fn exact_duplicates_are_idempotent_and_conflicts_discard_the_instruction() {
    let mut reassembler = FragmentReassembler::new(0);
    let first = observed_fragment(7, 0, false, b"same");
    assert_eq!(
        reassembler.push(0, first).unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(
        reassembler
            .push(1, observed_fragment(7, 0, false, b"same"))
            .unwrap(),
        ReassemblyOutcome::Duplicate
    );
    assert_eq!(reassembler.stored_bytes(), 4);

    assert_eq!(
        reassembler.push(2, observed_fragment(7, 0, false, b"different")),
        Err(ReassemblyError::ConflictingFragment)
    );
    assert_eq!(reassembler.incomplete_count(), 0);
    assert_eq!(reassembler.stored_bytes(), 0);

    assert_eq!(
        reassembler
            .push(3, observed_fragment(7, 0, false, b"new"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
}

#[test]
fn contradictory_final_markers_discard_the_instruction() {
    let mut reassembler = FragmentReassembler::new(0);
    reassembler
        .push(0, observed_fragment(9, 3, false, b"d"))
        .unwrap();
    assert_eq!(
        reassembler.push(1, observed_fragment(9, 2, true, b"c")),
        Err(ReassemblyError::ConflictingFragment)
    );
    assert_eq!(reassembler.incomplete_count(), 0);

    reassembler
        .push(2, observed_fragment(10, 1, true, b"b"))
        .unwrap();
    assert_eq!(
        reassembler.push(3, observed_fragment(10, 2, true, b"c")),
        Err(ReassemblyError::ConflictingFragment)
    );
    assert_eq!(reassembler.incomplete_count(), 0);
}

#[test]
fn missing_fragments_expire_at_the_fixed_first_fragment_deadline() {
    let mut reassembler = FragmentReassembler::new(100);
    reassembler
        .push(100, observed_fragment(11, 1, true, b"b"))
        .unwrap();
    reassembler
        .push(9_999, observed_fragment(11, 0, false, b"a"))
        .unwrap();
    assert_eq!(reassembler.incomplete_count(), 0);
    assert_eq!(reassembler.stored_bytes(), 0);

    reassembler
        .push(10_000, observed_fragment(12, 0, false, b"a"))
        .unwrap();
    assert_eq!(reassembler.expire(19_999).unwrap(), 0);
    assert_eq!(reassembler.expire(20_000).unwrap(), 1);
    assert_eq!(reassembler.incomplete_count(), 0);
}

// NexusSSH (P1): Im Original lehnte der Reassembler eine zweite Kennung ab, solange eine
// unvollständige Anweisung lag, und die Sitzung endete. Wie in mosh-server verdrängt sie diese jetzt.
#[test]
fn a_new_identifier_displaces_the_incomplete_instruction_and_count_limits_fail_before_mutation() {
    let mut reassembler = FragmentReassembler::new(0);
    reassembler
        .push(0, observed_fragment(0, 0, false, b"x"))
        .unwrap();
    assert_eq!(
        reassembler
            .push(0, observed_fragment(99, 0, false, b"yz"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(reassembler.incomplete_count(), 1);
    assert_eq!(reassembler.stored_bytes(), 2);
    let stored = reassembler.stored_bytes();
    assert_eq!(
        reassembler.push(
            0,
            observed_fragment(
                99,
                u16::try_from(MAX_FRAGMENTS_PER_INSTRUCTION).unwrap(),
                false,
                b"x"
            )
        ),
        Err(ReassemblyError::FragmentNumberOutOfRange)
    );
    assert_eq!(reassembler.incomplete_count(), 1);
    assert_eq!(reassembler.stored_bytes(), stored);
    assert_eq!(
        reassembler
            .push(0, observed_fragment(99, 1, true, b"!"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"yz!".to_vec())
    );
}

// NexusSSH (P1): Auch eine neue Anweisung aus einem einzigen Fragment verdrängt die unvollständige;
// deren Rest gehört danach zu einer neuen, wieder unvollständigen Anweisung.
#[test]
fn one_complete_fragment_displaces_the_incomplete_message() {
    let mut reassembler = FragmentReassembler::new(0);
    assert_eq!(
        reassembler
            .push(0, observed_fragment(1, 0, false, b"a"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );

    assert_eq!(
        reassembler
            .push(1, observed_fragment(2, 0, true, b"complete"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"complete".to_vec())
    );
    assert_eq!(reassembler.incomplete_count(), 0);
    assert_eq!(reassembler.stored_bytes(), 0);

    assert_eq!(
        reassembler
            .push(2, observed_fragment(1, 1, true, b"b"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(reassembler.incomplete_count(), 1);
}

// NexusSSH (P1): So sieht ein Verlust auf der Leitung aus – das erste Fragment fehlt, der Server
// schickt den nächsten Stand unter neuer Kennung, und der wird vollständig.
#[test]
fn a_lost_fragment_does_not_block_the_next_instruction() {
    let mut reassembler = FragmentReassembler::new(0);
    assert_eq!(
        reassembler
            .push(0, observed_fragment(7, 1, true, b"tail"))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(
        reassembler
            .push(50, observed_fragment(8, 0, false, b"new "))
            .unwrap(),
        ReassemblyOutcome::Pending
    );
    assert_eq!(
        reassembler
            .push(60, observed_fragment(8, 1, true, b"state"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"new state".to_vec())
    );
    assert_eq!(reassembler.incomplete_count(), 0);
}

fn byte_limits_fail_without_retaining_the_rejected_fragment() {
    let body = [0_u8; MAX_FRAGMENT_BODY_BYTES];
    let mut instruction_limit = FragmentReassembler::new(0);
    for number in 0..748_u16 {
        instruction_limit
            .push(0, observed_fragment(1, number, false, &body))
            .unwrap();
    }
    let stored = instruction_limit.stored_bytes();
    assert_eq!(
        instruction_limit.push(0, observed_fragment(1, 748, false, &body)),
        Err(ReassemblyError::InstructionTooLarge)
    );
    assert_eq!(instruction_limit.stored_bytes(), stored);

    assert_eq!(MAX_COMPRESSED_INSTRUCTION_BYTES, 1024 * 1024);
}

#[test]
fn exact_instruction_byte_limit_completes_without_retained_state() {
    let full_fragments = MAX_COMPRESSED_INSTRUCTION_BYTES / MAX_FRAGMENT_BODY_BYTES;
    let remainder = MAX_COMPRESSED_INSTRUCTION_BYTES % MAX_FRAGMENT_BODY_BYTES;
    let full_body = vec![0x5a; MAX_FRAGMENT_BODY_BYTES];
    let final_body = vec![0xa5; remainder];
    let final_number = u16::try_from(full_fragments).unwrap();
    let mut reassembler = FragmentReassembler::new(0);

    for number in 0..final_number {
        assert_eq!(
            reassembler
                .push(0, observed_fragment(7, number, false, &full_body))
                .unwrap(),
            ReassemblyOutcome::Pending
        );
    }
    let completed = reassembler
        .push(0, observed_fragment(7, final_number, true, &final_body))
        .unwrap();
    let ReassemblyOutcome::Complete(bytes) = completed else {
        panic!("the exact byte limit must complete")
    };
    assert_eq!(bytes.len(), MAX_COMPRESSED_INSTRUCTION_BYTES);
    assert_eq!(reassembler.incomplete_count(), 0);
    assert_eq!(reassembler.stored_bytes(), 0);
}

#[test]
fn owner_cleanup_and_time_errors_clear_or_preserve_state_explicitly() {
    let mut reassembler = FragmentReassembler::new(10);
    reassembler
        .push(10, observed_fragment(1, 0, false, b"pending"))
        .unwrap();
    assert_eq!(
        reassembler.push(9, observed_fragment(1, 1, true, b"late")),
        Err(ReassemblyError::TimeMovedBackwards)
    );
    assert_eq!(reassembler.incomplete_count(), 1);
    assert_eq!(reassembler.clear(), 1);
    assert_eq!(reassembler.stored_bytes(), 0);
    assert_eq!(
        reassembler
            .push(12, observed_fragment(2, 0, true, b"accepted"))
            .unwrap(),
        ReassemblyOutcome::Complete(b"accepted".to_vec())
    );
    assert_eq!(reassembler.clear(), 0);
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    #[test]
    fn reverse_order_reassembly_preserves_every_generated_byte(
        bodies in prop::collection::vec(
            prop::collection::vec(any::<u8>(), 0..64),
            1..64,
        )
    ) {
        let expected = bodies.concat();
        let final_number = u16::try_from(bodies.len() - 1).unwrap();
        let mut reassembler = FragmentReassembler::new(0);
        let mut outcome = ReassemblyOutcome::Pending;

        for (number, body) in bodies.iter().enumerate().rev() {
            let number = u16::try_from(number).unwrap();
            outcome = reassembler
                .push(0, observed_fragment(1, number, number == final_number, body))
                .unwrap();
        }

        prop_assert_eq!(outcome, ReassemblyOutcome::Complete(expected));
        prop_assert_eq!(reassembler.incomplete_count(), 0);
        prop_assert_eq!(reassembler.stored_bytes(), 0);
    }
}
