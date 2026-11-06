use super::super::*;
use proptest::prelude::*;

const KEY: [u8; 16] = [0x42; 16];

fn sealed(sequence: u64, plaintext: &[u8]) -> Vec<u8> {
    let key = SessionKey::new(KEY);
    let codec = PacketCodec::new(&key);
    let mut output = vec![0_u8; MAX_DATAGRAM_BYTES];
    let len = codec
        .seal(Direction::ServerToClient, sequence, plaintext, &mut output)
        .unwrap();
    output.truncate(len);
    output
}

#[test]
fn mosh_header_is_big_endian_and_forms_the_nonce() {
    let header = (DIRECTION_MASK | 0x0102_0304_0506_0708).to_be_bytes();
    assert_eq!(
        nonce(header),
        [0, 0, 0, 0, 0x81, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]
    );
}

#[test]
fn seals_and_opens_a_bounded_datagram() {
    let mut datagram = sealed(7, b"authenticated payload");
    let key = SessionKey::new(KEY);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);

    let packet = receiver.open(&mut datagram).unwrap();

    assert_eq!(packet.sequence, 7);
    assert_eq!(packet.plaintext, b"authenticated payload");
}

#[test]
fn rejects_wrong_keys_and_every_authenticated_region_change() {
    for changed_index in [HEADER_BYTES - 1, HEADER_BYTES, 24] {
        let mut datagram = sealed(3, b"payload");
        datagram[changed_index] ^= 1;
        let key = SessionKey::new(KEY);
        let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
        assert_eq!(
            receiver.open(&mut datagram).unwrap_err(),
            PacketError::AuthenticationFailed
        );
    }

    let mut datagram = sealed(3, b"payload");
    let wrong_key = SessionKey::new([0x43; 16]);
    let mut receiver = PacketReceiver::new(&wrong_key, Direction::ServerToClient);
    assert_eq!(
        receiver.open(&mut datagram).unwrap_err(),
        PacketError::AuthenticationFailed
    );
}

#[test]
fn accepts_reordering_once_and_rejects_replay_and_old_packets() {
    let key = SessionKey::new(KEY);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);

    for sequence in [65, 64, 2] {
        let mut datagram = sealed(sequence, b"payload");
        assert!(receiver.open(&mut datagram).is_ok());
    }
    let mut replay = sealed(64, b"payload");
    assert_eq!(receiver.open(&mut replay).unwrap_err(), PacketError::Replay);
    let replay_body_end = replay.len() - TAG_BYTES;
    assert!(
        replay[HEADER_BYTES..replay_body_end]
            .iter()
            .all(|byte| *byte == 0)
    );
    let mut old = sealed(1, b"payload");
    assert_eq!(receiver.open(&mut old).unwrap_err(), PacketError::TooOld);
}

#[test]
fn client_receiver_keeps_the_server_endpoint_fixed() {
    use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};

    let expected = SocketAddrV4::new(Ipv4Addr::new(192, 0, 2, 1), 60_001);
    let changed = SocketAddrV4::new(Ipv4Addr::new(192, 0, 2, 2), 60_002);
    let mut datagram = sealed(7, b"server payload");
    let key = SessionKey::new(KEY);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);

    assert_eq!(
        receiver
            .open_from(expected, SocketAddr::V4(changed), &mut datagram)
            .unwrap_err(),
        PacketError::UnexpectedSource
    );
    let packet = receiver
        .open_from(expected, SocketAddr::V4(expected), &mut datagram)
        .unwrap();
    assert_eq!(packet.sequence, 7);
    assert_eq!(packet.plaintext, b"server payload");
}

// NexusSSH (P5): Derselbe feste Endpunkt bei IPv6; Flow-Label und Scope, die `recv_from` mitliefert,
// stören nicht, eine andere Adresse, ein anderer Port oder eine andere Familie schon.
#[test]
fn client_receiver_keeps_an_ipv6_server_endpoint_fixed() {
    use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV6};

    let address = Ipv6Addr::new(0x2001, 0xdb8, 0, 0, 0, 0, 0, 1);
    let expected = SocketAddr::from((address, 60_001));
    let key = SessionKey::new(KEY);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);

    for wrong in [
        SocketAddr::from((Ipv6Addr::new(0x2001, 0xdb8, 0, 0, 0, 0, 0, 2), 60_001)),
        SocketAddr::from((address, 60_002)),
        SocketAddr::from((Ipv4Addr::new(192, 0, 2, 1), 60_001)),
    ] {
        let mut datagram = sealed(7, b"server payload");
        assert_eq!(
            receiver
                .open_from(expected, wrong, &mut datagram)
                .unwrap_err(),
            PacketError::UnexpectedSource
        );
    }
    let mut datagram = sealed(8, b"server payload");
    let from_server = SocketAddr::V6(SocketAddrV6::new(address, 60_001, 0x12345, 3));
    let packet = receiver
        .open_from(expected, from_server, &mut datagram)
        .unwrap();
    assert_eq!(packet.sequence, 8);

    let link_local = Ipv6Addr::new(0xfe80, 0, 0, 0, 0, 0, 0, 1);
    let scoped = SocketAddr::V6(SocketAddrV6::new(link_local, 60_001, 0, 3));
    assert!(is_same_endpoint(
        scoped,
        SocketAddr::V6(SocketAddrV6::new(link_local, 60_001, 7, 3))
    ));
    assert!(!is_same_endpoint(
        scoped,
        SocketAddr::V6(SocketAddrV6::new(link_local, 60_001, 0, 4))
    ));
}

#[test]
fn enforces_packet_and_sequence_boundaries() {
    let key = SessionKey::new(KEY);
    let codec = PacketCodec::new(&key);
    let mut output = vec![0_u8; MAX_DATAGRAM_BYTES];
    assert!(
        codec
            .seal(
                Direction::ClientToServer,
                MAX_SEQUENCE,
                &vec![0; MAX_PLAINTEXT_BYTES],
                &mut output,
            )
            .is_ok()
    );
    assert_eq!(
        codec
            .seal(
                Direction::ClientToServer,
                MAX_SEQUENCE + 1,
                &[],
                &mut output,
            )
            .unwrap_err(),
        PacketError::InvalidSequence
    );

    let mut sequence = SendSequence(Some(MAX_SEQUENCE));
    assert_eq!(sequence.take().unwrap(), MAX_SEQUENCE);
    assert_eq!(sequence.take().unwrap_err(), PacketError::SequenceExhausted);
}

#[test]
fn rejects_short_oversized_and_insufficient_buffers() {
    let key = SessionKey::new(KEY);
    let codec = PacketCodec::new(&key);
    let mut short_output = [0_u8; OVERHEAD_BYTES - 1];
    assert_eq!(
        codec
            .seal(Direction::ClientToServer, 0, &[], &mut short_output,)
            .unwrap_err(),
        PacketError::OutputTooSmall
    );

    let oversized_plaintext = vec![0_u8; MAX_PLAINTEXT_BYTES + 1];
    let mut output = vec![0_u8; MAX_DATAGRAM_BYTES + 1];
    assert_eq!(
        codec
            .seal(
                Direction::ClientToServer,
                0,
                &oversized_plaintext,
                &mut output,
            )
            .unwrap_err(),
        PacketError::PlaintextTooLarge
    );

    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    assert_eq!(
        receiver.open(&mut [0_u8; OVERHEAD_BYTES - 1]).unwrap_err(),
        PacketError::DatagramTooShort
    );
    assert_eq!(
        receiver
            .open(&mut vec![0_u8; MAX_DATAGRAM_BYTES + 1])
            .unwrap_err(),
        PacketError::DatagramTooLarge
    );
}

#[test]
fn authentication_failure_clears_candidate_plaintext_and_cannot_advance_replay() {
    let key = SessionKey::new(KEY);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut corrupted = sealed(100, b"candidate plaintext");
    let last = corrupted.len() - 1;
    let body_end = corrupted.len() - TAG_BYTES;
    corrupted[last] ^= 1;

    assert_eq!(
        receiver.open(&mut corrupted).unwrap_err(),
        PacketError::AuthenticationFailed
    );
    assert!(
        corrupted[HEADER_BYTES..body_end]
            .iter()
            .all(|byte| *byte == 0)
    );

    let mut valid = sealed(1, b"valid");
    assert!(receiver.open(&mut valid).is_ok());
}

#[test]
fn rejects_the_wrong_direction_without_decrypting() {
    let key = SessionKey::new(KEY);
    let mut datagram = sealed(0, b"payload");
    let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);

    assert_eq!(
        receiver.open(&mut datagram).unwrap_err(),
        PacketError::WrongDirection
    );
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    #[test]
    fn authenticated_round_trip_preserves_generated_payloads(
        sequence in 0_u64..=MAX_SEQUENCE,
        plaintext in prop::collection::vec(any::<u8>(), 0..512),
    ) {
        let mut datagram = sealed(sequence, &plaintext);
        let key = SessionKey::new(KEY);
        let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
        let opened = receiver.open(&mut datagram).unwrap();

        prop_assert_eq!(opened.sequence, sequence);
        prop_assert_eq!(opened.plaintext, plaintext);
    }
}
