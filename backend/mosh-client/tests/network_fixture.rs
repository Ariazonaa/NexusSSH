mod support;

use std::net::{Ipv4Addr, SocketAddrV4};

use support::network::{
    Datagram, Impairment, LinkError, MAX_DATAGRAM_BYTES, MAX_QUEUED_DATAGRAMS, SimTime, VirtualLink,
};

fn endpoint(last_octet: u8, port: u16) -> SocketAddrV4 {
    SocketAddrV4::new(Ipv4Addr::new(127, 0, 0, last_octet), port)
}

#[test]
fn drops_without_leaving_queued_state() {
    let mut link = VirtualLink::new();
    link.send(endpoint(1, 1), endpoint(1, 2), b"lost", Impairment::Drop)
        .unwrap();

    assert_eq!(link.queued_datagrams(), 0);
    assert_eq!(link.advance_to(SimTime::from_millis(10_000)).unwrap(), []);
}

#[test]
fn duplicates_at_independently_scheduled_times() {
    let mut link = VirtualLink::new();
    link.send(
        endpoint(1, 1),
        endpoint(1, 2),
        b"twice",
        Impairment::Duplicate {
            first_delay_ms: 5,
            second_delay_ms: 10,
        },
    )
    .unwrap();

    assert_eq!(link.advance_to(SimTime::from_millis(4)).unwrap(), vec![]);
    assert_eq!(
        link.advance_to(SimTime::from_millis(5)).unwrap()[0].bytes,
        b"twice"
    );
    assert_eq!(
        link.advance_to(SimTime::from_millis(10)).unwrap()[0].bytes,
        b"twice"
    );
}

#[test]
fn later_send_can_arrive_first() {
    let mut link = VirtualLink::new();
    let source = endpoint(1, 1);
    let destination = endpoint(1, 2);
    link.send(
        source,
        destination,
        b"first",
        Impairment::Deliver { delay_ms: 20 },
    )
    .unwrap();
    link.send(
        source,
        destination,
        b"second",
        Impairment::Deliver { delay_ms: 10 },
    )
    .unwrap();

    let delivered = link.advance_to(SimTime::from_millis(20)).unwrap();
    assert_eq!(delivered[0].bytes, b"second");
    assert_eq!(delivered[1].bytes, b"first");
}

#[test]
fn delay_supports_silence_and_timeout_boundaries() {
    let mut link = VirtualLink::new();
    link.send(
        endpoint(1, 1),
        endpoint(1, 2),
        b"delayed",
        Impairment::Deliver { delay_ms: 100 },
    )
    .unwrap();

    assert_eq!(link.advance_to(SimTime::from_millis(99)).unwrap(), []);
    assert_eq!(link.now(), SimTime::from_millis(99));
    assert_eq!(
        link.advance_to(SimTime::from_millis(100)).unwrap()[0].bytes,
        b"delayed"
    );
    assert_eq!(
        link.advance_to(SimTime::from_millis(99)),
        Err(LinkError::TimeMovedBackwards)
    );
}

#[test]
fn corruption_changes_only_the_selected_byte() {
    let mut link = VirtualLink::new();
    link.send(
        endpoint(1, 1),
        endpoint(1, 2),
        &[0x10, 0x20, 0x30],
        Impairment::Corrupt {
            delay_ms: 0,
            byte_index: 1,
            xor_mask: 0x03,
        },
    )
    .unwrap();

    assert_eq!(
        link.advance_to(SimTime::from_millis(0)).unwrap()[0].bytes,
        [0x10, 0x23, 0x30]
    );
    assert_eq!(
        link.send(
            endpoint(1, 1),
            endpoint(1, 2),
            b"x",
            Impairment::Corrupt {
                delay_ms: 0,
                byte_index: 1,
                xor_mask: 1,
            }
        ),
        Err(LinkError::InvalidCorruptionIndex)
    );
}

#[test]
fn cancellation_clears_pending_work_and_rejects_late_events() {
    let mut link = VirtualLink::new();
    link.send(
        endpoint(1, 1),
        endpoint(1, 2),
        b"pending",
        Impairment::Deliver { delay_ms: 1 },
    )
    .unwrap();

    assert_eq!(link.cancel(), 1);
    assert!(link.is_cancelled());
    assert_eq!(link.queued_datagrams(), 0);
    assert_eq!(
        link.advance_to(SimTime::from_millis(1)),
        Err(LinkError::Cancelled)
    );
    assert_eq!(
        link.send(
            endpoint(1, 1),
            endpoint(1, 2),
            b"late",
            Impairment::Deliver { delay_ms: 0 }
        ),
        Err(LinkError::Cancelled)
    );
}

#[test]
fn source_address_changes_are_observable_without_policy() {
    let mut link = VirtualLink::new();
    let old_source = endpoint(1, 60_001);
    let new_source = endpoint(2, 60_002);
    let destination = endpoint(1, 60_003);
    link.send(
        old_source,
        destination,
        b"old",
        Impairment::Deliver { delay_ms: 0 },
    )
    .unwrap();
    link.send(
        new_source,
        destination,
        b"new",
        Impairment::Deliver { delay_ms: 1 },
    )
    .unwrap();

    let old = link.advance_to(SimTime::from_millis(0)).unwrap();
    let new = link.advance_to(SimTime::from_millis(1)).unwrap();
    assert_eq!(old[0].source, old_source);
    assert_eq!(new[0].source, new_source);
    assert_eq!(old[0].destination, destination);
    assert_eq!(new[0].destination, destination);
}

#[test]
fn datagram_and_queue_limits_fail_before_mutation() {
    let source = endpoint(1, 1);
    let destination = endpoint(1, 2);
    let mut link = VirtualLink::new();
    assert_eq!(
        link.send(
            source,
            destination,
            &vec![0; MAX_DATAGRAM_BYTES + 1],
            Impairment::Deliver { delay_ms: 0 }
        ),
        Err(LinkError::DatagramTooLarge)
    );

    for _ in 0..MAX_QUEUED_DATAGRAMS {
        link.send(
            source,
            destination,
            &[],
            Impairment::Deliver { delay_ms: 1 },
        )
        .unwrap();
    }
    assert_eq!(link.queued_datagrams(), MAX_QUEUED_DATAGRAMS);
    assert_eq!(
        link.send(
            source,
            destination,
            &[],
            Impairment::Deliver { delay_ms: 1 }
        ),
        Err(LinkError::QueueFull)
    );
    assert_eq!(link.queued_datagrams(), MAX_QUEUED_DATAGRAMS);
}

fn deterministic_scenario() -> Vec<Datagram> {
    let mut link = VirtualLink::new();
    let source = endpoint(1, 1);
    let destination = endpoint(1, 2);
    link.send(
        source,
        destination,
        b"a",
        Impairment::Duplicate {
            first_delay_ms: 3,
            second_delay_ms: 1,
        },
    )
    .unwrap();
    link.send(
        source,
        destination,
        b"b",
        Impairment::Deliver { delay_ms: 1 },
    )
    .unwrap();
    link.advance_to(SimTime::from_millis(3)).unwrap()
}

#[test]
fn the_same_script_has_the_same_delivery_order() {
    assert_eq!(deterministic_scenario(), deterministic_scenario());
    let delivered = deterministic_scenario();
    assert_eq!(delivered[0].bytes, b"a");
    assert_eq!(delivered[1].bytes, b"b");
    assert_eq!(delivered[2].bytes, b"a");
}
