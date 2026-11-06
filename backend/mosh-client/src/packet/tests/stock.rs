use super::super::*;

#[cfg(target_os = "linux")]
use crate::test_support::{
    DetachedProcessGuard, STOCK_1_4_0_KEY_BYTES, STOCK_1_4_0_KEY_TEXT, assert_stock_1_4_0,
    find_detached_pid,
};

#[cfg(target_os = "linux")]
#[test]
#[ignore = "requires a locally installed stock mosh-client 1.4.0 and util-linux script"]
fn stock_1_4_0_client_packet_opens_with_the_fixed_public_test_key() {
    use std::net::{Ipv4Addr, UdpSocket};
    use std::os::unix::process::CommandExt;
    use std::process::{Command, Stdio};
    use std::time::Duration;

    assert_stock_1_4_0("mosh-client");

    let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(3)))
        .unwrap();
    let port = socket.local_addr().unwrap().port();
    let command = format!(
        "stty rows 24 cols 80; env TERM=xterm-256color LANG=C.UTF-8 MOSH_KEY={STOCK_1_4_0_KEY_TEXT} mosh-client 127.0.0.1 {port}"
    );
    let child = Command::new("script")
        .args(["-qfec", &command, "/dev/null"])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .process_group(0)
        .spawn()
        .unwrap_or_else(|error| panic!("failed to start stock client fixture: {error}"));
    let mut child = ChildGuard::new(child);

    let mut datagram = [0_u8; MAX_DATAGRAM_BYTES];
    let (length, peer) = socket
        .recv_from(&mut datagram)
        .unwrap_or_else(|error| panic!("stock client did not send a datagram: {error}"));
    assert_eq!(peer.ip(), Ipv4Addr::LOCALHOST);

    let key = SessionKey::new(STOCK_1_4_0_KEY_BYTES);
    let mut receiver = PacketReceiver::new(&key, Direction::ClientToServer);
    let packet = receiver
        .open(&mut datagram[..length])
        .unwrap_or_else(|error| panic!("stock client packet was rejected: {error:?}"));
    assert_eq!(packet.sequence, 0);
    assert_ne!(packet.plaintext, []);

    assert!(child.terminate(), "stock client fixture did not clean up");
}

#[cfg(target_os = "linux")]
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_server_accepts_the_project_initial_packet() {
    use std::net::{Ipv4Addr, UdpSocket};
    use std::process::Command;
    use std::time::Duration;

    use zeroize::Zeroize as _;

    use crate::bootstrap::Bootstrap;
    use crate::fragment;
    use crate::instruction::TransportInstruction;
    use crate::limits::MIN_RETRANSMISSION_TIMEOUT_MS;
    use crate::synchronization::{AcknowledgementDisposition, SynchronizationState};

    assert_stock_1_4_0("mosh-server");

    let mut output = Command::new("mosh-server")
        .args([
            "new",
            "-i",
            "127.0.0.1",
            "-p",
            "60100:60199",
            "--",
            "/bin/sh",
        ])
        .output()
        .unwrap_or_else(|error| panic!("failed to start local stock server: {error}"));
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let mut server = DetachedProcessGuard::new(detached_pid);
    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    output.stdout.zeroize();
    output.stderr.zeroize();
    let (server_address, key) = bootstrap.into_session_parts();

    let initial = TransportInstruction::initial(80, 24, vec![0]).unwrap();
    let mut synchronization = SynchronizationState::new();
    synchronization.advance_local().unwrap();
    let send_plan = synchronization
        .plan_send(0, MIN_RETRANSMISSION_TIMEOUT_MS)
        .unwrap();
    let instruction = send_plan.instruction(initial.state_difference, initial.chaff);
    let compressed = instruction.encode_zlib().unwrap();
    let mut fragment_plaintext = [0_u8; 1_414];
    let fragment_len =
        fragment::encode(0, None, 0, 0, true, &compressed, &mut fragment_plaintext).unwrap();
    let codec = PacketCodec::new(&key);
    let mut outbound = [0_u8; MAX_DATAGRAM_BYTES];
    let outbound_len = codec
        .seal(
            Direction::ClientToServer,
            0,
            &fragment_plaintext[..fragment_len],
            &mut outbound,
        )
        .unwrap();

    let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(3)))
        .unwrap();
    socket
        .send_to(&outbound[..outbound_len], server_address)
        .unwrap();
    synchronization.commit_send(send_plan).unwrap();

    let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
    let (inbound_len, peer) = socket
        .recv_from(&mut inbound)
        .unwrap_or_else(|error| panic!("stock server did not answer the initial packet: {error}"));
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let opened = receiver
        .open_from(server_address, peer, &mut inbound[..inbound_len])
        .unwrap_or_else(|error| panic!("stock server response was rejected: {error:?}"));
    assert_eq!(opened.sequence, 0);
    let response_fragment = fragment::decode(opened.plaintext)
        .unwrap_or_else(|error| panic!("stock server fragment was rejected: {error:?}"));
    assert_eq!(response_fragment.number, 0);
    assert!(response_fragment.is_final);
    let response = TransportInstruction::decode_zlib(response_fragment.body)
        .unwrap_or_else(|error| panic!("stock server instruction was rejected: {error:?}"));
    assert_eq!(response.protocol_version, 2);
    assert_eq!(response.acknowledged_state, 1);
    let transition = synchronization.begin_receive(&response).unwrap();
    assert_eq!(
        transition.acknowledgement,
        AcknowledgementDisposition::Advanced { from: 0, to: 1 }
    );
    assert_eq!(synchronization.known_receiver_state(), 1);

    inbound.zeroize();
    outbound.zeroize();
    fragment_plaintext.zeroize();
    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[cfg(target_os = "linux")]
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
#[expect(
    clippy::too_many_lines,
    reason = "one black-box scenario must keep controlled input, authenticated decoding, echo acknowledgement observation, and cleanup together"
)]
fn stock_1_4_0_echo_acknowledges_the_controlled_input_operation() {
    use std::net::{Ipv4Addr, UdpSocket};
    use std::process::Command;
    use std::time::Duration;

    use zeroize::Zeroize as _;

    use crate::bootstrap::Bootstrap;
    use crate::fragment::{self, FragmentReassembler, ReassemblyOutcome};
    use crate::instruction::{ClientOperation, TransportInstruction, encode_client_difference};
    use crate::terminal::TerminalDifference;

    assert_stock_1_4_0("mosh-server");

    let mut output = Command::new("mosh-server")
        .args([
            "new",
            "-i",
            "127.0.0.1",
            "-p",
            "60380:60399",
            "--",
            "/bin/sh",
        ])
        .output()
        .unwrap_or_else(|error| panic!("failed to start local stock server: {error}"));
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let mut server = DetachedProcessGuard::new(detached_pid);
    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    output.stdout.zeroize();
    output.stderr.zeroize();
    let (server_address, key) = bootstrap.into_session_parts();

    let codec = PacketCodec::new(&key);
    let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(5)))
        .unwrap();
    let mut outbound = [0_u8; MAX_DATAGRAM_BYTES];
    let mut fragment_plaintext = [0_u8; 1_414];

    let initial = TransportInstruction::initial(80, 24, vec![0]).unwrap();
    let compressed = initial.encode_zlib().unwrap();
    let fragment_len =
        fragment::encode(0, None, 0, 0, true, &compressed, &mut fragment_plaintext).unwrap();
    let outbound_len = codec
        .seal(
            Direction::ClientToServer,
            0,
            &fragment_plaintext[..fragment_len],
            &mut outbound,
        )
        .unwrap();
    socket
        .send_to(&outbound[..outbound_len], server_address)
        .unwrap();

    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut reassembler = FragmentReassembler::new(0);
    let mut server_state = None;
    for now_ms in 0..64_u64 {
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (inbound_len, peer) = socket.recv_from(&mut inbound).unwrap_or_else(|error| {
            panic!("stock server did not acknowledge the initial state: {error}")
        });
        let opened = receiver
            .open_from(server_address, peer, &mut inbound[..inbound_len])
            .unwrap_or_else(|error| panic!("stock server packet was rejected: {error:?}"));
        let decoded = fragment::decode(opened.plaintext)
            .unwrap_or_else(|error| panic!("stock server fragment was rejected: {error:?}"));
        let ReassemblyOutcome::Complete(compressed) = reassembler
            .push(now_ms, decoded)
            .unwrap_or_else(|error| panic!("stock fragment reassembly failed: {error:?}"))
        else {
            continue;
        };
        let instruction = TransportInstruction::decode_zlib(&compressed)
            .unwrap_or_else(|error| panic!("stock instruction was rejected: {error:?}"));
        if instruction.acknowledged_state == 1 {
            server_state = Some(instruction.new_state);
            break;
        }
    }
    let server_state = server_state.expect("stock server never acknowledged initial client state");

    let controlled_input = TransportInstruction {
        protocol_version: crate::instruction::PROTOCOL_VERSION,
        base_state: 1,
        new_state: 2,
        acknowledged_state: server_state,
        discard_before_state: 1,
        state_difference: encode_client_difference(&[ClientOperation::Input(b"x".to_vec())])
            .unwrap(),
        chaff: vec![0],
    };
    let compressed = controlled_input.encode_zlib().unwrap();
    let fragment_len =
        fragment::encode(0, None, 1, 0, true, &compressed, &mut fragment_plaintext).unwrap();
    let outbound_len = codec
        .seal(
            Direction::ClientToServer,
            1,
            &fragment_plaintext[..fragment_len],
            &mut outbound,
        )
        .unwrap();
    socket
        .send_to(&outbound[..outbound_len], server_address)
        .unwrap();

    let mut observed = Vec::new();
    for now_ms in 64..192_u64 {
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (inbound_len, peer) = socket.recv_from(&mut inbound).unwrap_or_else(|error| {
            panic!("stock server did not emit the controlled echo acknowledgement: {error}")
        });
        let opened = receiver
            .open_from(server_address, peer, &mut inbound[..inbound_len])
            .unwrap_or_else(|error| panic!("stock server packet was rejected: {error:?}"));
        let decoded = fragment::decode(opened.plaintext)
            .unwrap_or_else(|error| panic!("stock server fragment was rejected: {error:?}"));
        let ReassemblyOutcome::Complete(compressed) = reassembler
            .push(now_ms, decoded)
            .unwrap_or_else(|error| panic!("stock fragment reassembly failed: {error:?}"))
        else {
            continue;
        };
        let instruction = TransportInstruction::decode_zlib(&compressed)
            .unwrap_or_else(|error| panic!("stock instruction was rejected: {error:?}"));
        let difference = TerminalDifference::decode(&instruction.state_difference)
            .unwrap_or_else(|error| panic!("stock terminal difference was rejected: {error:?}"));
        observed.push((
            instruction.acknowledged_state,
            instruction.new_state,
            difference.latest_echo_acknowledgement(),
        ));
        if difference.latest_echo_acknowledgement().is_some() {
            break;
        }
    }

    assert!(
        observed
            .iter()
            .any(|(transport_ack, _, echo)| *transport_ack == 2 && *echo == Some(2)),
        "stock server did not tie transport and echo acknowledgement to controlled client state 2; observed {observed:?}"
    );
    outbound.zeroize();
    fragment_plaintext.zeroize();
    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[cfg(target_os = "linux")]
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0 and coreutils"]
#[expect(
    clippy::too_many_lines,
    reason = "one black-box scenario must keep server startup, authenticated collection, reorder, loss, expiry, and cleanup together"
)]
fn stock_1_4_0_multifragment_response_reorders_and_expires_after_loss() {
    use std::collections::BTreeMap;
    use std::net::{Ipv4Addr, UdpSocket};
    use std::process::Command;
    use std::time::Duration;

    use zeroize::Zeroize as _;

    use crate::bootstrap::Bootstrap;
    use crate::fragment::{self, FragmentReassembler, ReassemblyOutcome};
    use crate::instruction::TransportInstruction;

    #[derive(Debug)]
    struct OwnedFragment {
        number: u16,
        is_final: bool,
        body: Vec<u8>,
    }

    assert_stock_1_4_0("mosh-server");

    let mut output = Command::new("mosh-server")
        .args([
            "new",
            "-i",
            "127.0.0.1",
            "-p",
            "60200:60299",
            "--",
            "/bin/sh",
            "-c",
            "head -c 32768 /dev/urandom | base64; sleep 3",
        ])
        .output()
        .unwrap_or_else(|error| panic!("failed to start local stock server: {error}"));
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let mut server = DetachedProcessGuard::new(detached_pid);
    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    output.stdout.zeroize();
    output.stderr.zeroize();
    let (server_address, key) = bootstrap.into_session_parts();

    let initial = TransportInstruction::initial(200, 50, vec![0]).unwrap();
    let compressed = initial.encode_zlib().unwrap();
    let mut fragment_plaintext = [0_u8; 1_414];
    let fragment_len =
        fragment::encode(0, None, 0, 0, true, &compressed, &mut fragment_plaintext).unwrap();
    let codec = PacketCodec::new(&key);
    let mut outbound = [0_u8; MAX_DATAGRAM_BYTES];
    let outbound_len = codec
        .seal(
            Direction::ClientToServer,
            0,
            &fragment_plaintext[..fragment_len],
            &mut outbound,
        )
        .unwrap();

    let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(5)))
        .unwrap();
    socket
        .send_to(&outbound[..outbound_len], server_address)
        .unwrap();

    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut groups: BTreeMap<u64, BTreeMap<u16, OwnedFragment>> = BTreeMap::new();
    let mut selected_identifier = None;
    for _ in 0..64 {
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (inbound_len, peer) = socket.recv_from(&mut inbound).unwrap_or_else(|error| {
            panic!("stock server did not provide a complete multi-fragment response: {error}")
        });
        let opened = receiver
            .open_from(server_address, peer, &mut inbound[..inbound_len])
            .unwrap_or_else(|error| panic!("stock server packet was rejected: {error:?}"));
        let decoded = fragment::decode(opened.plaintext)
            .unwrap_or_else(|error| panic!("stock server fragment was rejected: {error:?}"));
        groups.entry(decoded.identifier).or_default().insert(
            decoded.number,
            OwnedFragment {
                number: decoded.number,
                is_final: decoded.is_final,
                body: decoded.body.to_vec(),
            },
        );
        selected_identifier = groups.iter().find_map(|(identifier, fragments)| {
            let final_number = fragments
                .values()
                .find(|fragment| fragment.is_final)?
                .number;
            (final_number > 0 && fragments.len() == usize::from(final_number) + 1)
                .then_some(*identifier)
        });
        if selected_identifier.is_some() {
            break;
        }
    }

    let identifier = selected_identifier.expect("stock response never used multiple fragments");
    let fragments = groups
        .remove(&identifier)
        .expect("selected fragment group exists");
    let mut reordered = FragmentReassembler::new(0);
    let mut complete = None;
    for (now_ms, fragment) in fragments.values().rev().enumerate() {
        let result = reordered
            .push(
                u64::try_from(now_ms).unwrap(),
                fragment::Fragment {
                    timestamp: 0,
                    timestamp_reply: None,
                    identifier,
                    number: fragment.number,
                    is_final: fragment.is_final,
                    body: &fragment.body,
                },
            )
            .unwrap();
        if let ReassemblyOutcome::Complete(bytes) = result {
            complete = Some(bytes);
        }
    }
    let instruction = TransportInstruction::decode_zlib(
        &complete.expect("reordered stock fragments did not complete"),
    )
    .unwrap_or_else(|error| panic!("reassembled stock instruction was rejected: {error:?}"));
    assert_eq!(instruction.protocol_version, 2);
    assert_ne!(instruction.state_difference, [] as [u8; 0]);

    let mut loss = FragmentReassembler::new(0);
    for fragment in fragments.values().filter(|fragment| fragment.number != 0) {
        assert!(matches!(
            loss.push(
                0,
                fragment::Fragment {
                    timestamp: 0,
                    timestamp_reply: None,
                    identifier,
                    number: fragment.number,
                    is_final: fragment.is_final,
                    body: &fragment.body,
                }
            )
            .unwrap(),
            ReassemblyOutcome::Pending | ReassemblyOutcome::Duplicate
        ));
    }
    assert_eq!(loss.incomplete_count(), 1);
    assert_eq!(loss.expire(9_999).unwrap(), 0);
    assert_eq!(loss.expire(10_000).unwrap(), 1);
    assert_eq!(loss.incomplete_count(), 0);
    assert_eq!(loss.stored_bytes(), 0);

    outbound.zeroize();
    fragment_plaintext.zeroize();
    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[cfg(target_os = "linux")]
#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
#[expect(
    clippy::too_many_lines,
    reason = "one black-box scenario must keep controlled output, authenticated decoding, reassembly, and exact process cleanup together"
)]
fn stock_1_4_0_host_bytes_preserve_controlled_utf8_output() {
    use std::net::{Ipv4Addr, UdpSocket};
    use std::process::Command;
    use std::time::Duration;

    use zeroize::Zeroize as _;

    use crate::bootstrap::Bootstrap;
    use crate::fragment::{self, FragmentReassembler, ReassemblyOutcome};
    use crate::instruction::TransportInstruction;
    use crate::terminal::{TerminalDifference, TerminalState};

    const MARKER: &[u8] = "A中Z".as_bytes();

    assert_stock_1_4_0("mosh-server");

    let mut output = Command::new("mosh-server")
        .args([
            "new",
            "-i",
            "127.0.0.1",
            "-p",
            "60300:60399",
            "--",
            "/bin/sh",
            "-c",
            "printf 'A中Z'; sleep 3",
        ])
        .output()
        .unwrap_or_else(|error| panic!("failed to start local stock server: {error}"));
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let mut server = DetachedProcessGuard::new(detached_pid);
    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    output.stdout.zeroize();
    output.stderr.zeroize();
    let (server_address, key) = bootstrap.into_session_parts();

    let initial = TransportInstruction::initial(81, 25, vec![0]).unwrap();
    let compressed = initial.encode_zlib().unwrap();
    let mut fragment_plaintext = [0_u8; 1_414];
    let fragment_len =
        fragment::encode(0, None, 0, 0, true, &compressed, &mut fragment_plaintext).unwrap();
    let codec = PacketCodec::new(&key);
    let mut outbound = [0_u8; MAX_DATAGRAM_BYTES];
    let outbound_len = codec
        .seal(
            Direction::ClientToServer,
            0,
            &fragment_plaintext[..fragment_len],
            &mut outbound,
        )
        .unwrap();

    let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(5)))
        .unwrap();
    socket
        .send_to(&outbound[..outbound_len], server_address)
        .unwrap();

    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut reassembler = FragmentReassembler::new(0);
    let mut terminal_states = std::collections::BTreeMap::new();
    terminal_states.insert(0, TerminalState::new(80, 24).unwrap());
    let mut marker_seen = false;
    let mut controlled_size_seen = false;
    for now_ms in 0..64_u64 {
        let mut inbound = [0_u8; MAX_DATAGRAM_BYTES];
        let (inbound_len, peer) = socket.recv_from(&mut inbound).unwrap_or_else(|error| {
            panic!("stock server did not send the controlled terminal output: {error}")
        });
        let opened = receiver
            .open_from(server_address, peer, &mut inbound[..inbound_len])
            .unwrap_or_else(|error| panic!("stock server packet was rejected: {error:?}"));
        let decoded = fragment::decode(opened.plaintext)
            .unwrap_or_else(|error| panic!("stock server fragment was rejected: {error:?}"));
        let ReassemblyOutcome::Complete(compressed) = reassembler
            .push(now_ms, decoded)
            .unwrap_or_else(|error| panic!("stock fragment reassembly failed: {error:?}"))
        else {
            continue;
        };
        let instruction = TransportInstruction::decode_zlib(&compressed)
            .unwrap_or_else(|error| panic!("stock instruction was rejected: {error:?}"));
        let difference = TerminalDifference::decode(&instruction.state_difference)
            .unwrap_or_else(|error| panic!("stock host difference was rejected: {error:?}"));
        let Some(base) = terminal_states.get(&instruction.base_state) else {
            continue;
        };
        let state = base
            .apply(&difference)
            .unwrap_or_else(|error| panic!("stock terminal patch was rejected: {error:?}"));
        controlled_size_seen |= state.screen().size() == (25, 81);
        marker_seen |= state
            .screen()
            .contents()
            .as_bytes()
            .windows(MARKER.len())
            .any(|window| window == MARKER);
        terminal_states.insert(instruction.new_state, state);
        if marker_seen && controlled_size_seen {
            break;
        }
    }

    assert!(
        marker_seen,
        "decoded stock HostBytes did not preserve the controlled UTF-8 marker"
    );
    assert!(
        controlled_size_seen,
        "decoded stock host difference did not carry the controlled terminal size"
    );
    outbound.zeroize();
    fragment_plaintext.zeroize();
    assert!(server.terminate(), "stock server fixture did not clean up");
}

#[cfg(target_os = "linux")]
struct ChildGuard {
    child: std::process::Child,
    terminated: bool,
}

#[cfg(target_os = "linux")]
impl ChildGuard {
    const fn new(child: std::process::Child) -> Self {
        Self {
            child,
            terminated: false,
        }
    }

    fn terminate(&mut self) -> bool {
        use std::process::{Command, Stdio};
        use std::thread;
        use std::time::Duration;

        if self.terminated {
            return true;
        }
        if self.child.try_wait().ok().flatten().is_some() {
            self.terminated = true;
            return true;
        }

        let process_group = format!("-{}", self.child.id());
        let _ = Command::new("kill")
            .args(["-TERM", "--", &process_group])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        for _ in 0..50 {
            if self.child.try_wait().ok().flatten().is_some() {
                self.terminated = true;
                return true;
            }
            thread::sleep(Duration::from_millis(20));
        }

        let _ = Command::new("kill")
            .args(["-KILL", "--", &process_group])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        self.terminated = self.child.wait().is_ok();
        self.terminated
    }
}

#[cfg(target_os = "linux")]
impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.terminate();
    }
}
