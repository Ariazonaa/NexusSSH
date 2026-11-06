//! Internal entry points for `cargo-fuzz` targets.
//!
//! This module exists only when rustc receives `--cfg fuzzing`; it is absent
//! from normal library builds and from the published API surface.

// NexusSSH (P7): Es gibt ihn auch in Test-Builds; `tests` unten treibt beide Eingänge bei jedem
// `cargo test` mit festem Zufall (wie `nexus-core/tests/fuzz.rs`). Die Ziele für `cargo fuzz` stehen
// in `backend/fuzz` (`mosh_untrusted_parsers`, `mosh_state_transitions`).

use std::net::Ipv4Addr;

use crate::Bootstrap;
use crate::crypto::SessionKey;
use crate::fragment::{Fragment, FragmentReassembler};
use crate::instruction::{PROTOCOL_VERSION, TransportInstruction};
use crate::limits::{
    MAX_COMPRESSED_INSTRUCTION_BYTES, MAX_DATAGRAM_BYTES, MAX_RECEIVED_REFERENCE_STATES,
    MAX_SENT_STATES_AFTER_ACK,
};
use crate::packet::{Direction, PacketReceiver};
use crate::synchronization::{RemoteStateDisposition, SynchronizationState};
use crate::terminal::{TerminalDifference, TerminalPainter, TerminalState};
use crate::timing::{RttEstimator, SchedulerPoll, SendScheduler};

/// Exercise every retained untrusted parser without weakening authentication
/// or bypassing production limits.
pub fn exercise_untrusted_parsers(data: &[u8]) {
    let _ = Bootstrap::parse(Ipv4Addr::LOCALHOST, data);
    let _ = TransportInstruction::decode_zlib(data);

    if let Ok(fragment) = crate::fragment::decode(data) {
        let mut reassembler = FragmentReassembler::new(0);
        let _ = reassembler.push(0, fragment);
        assert!(reassembler.incomplete_count() <= 1);
        assert!(reassembler.stored_bytes() <= MAX_COMPRESSED_INSTRUCTION_BYTES);
    }

    if let Ok(difference) = TerminalDifference::decode(data) {
        let initial = TerminalState::new(80, 24).expect("the fixed fuzz size is valid");
        if let Ok(next) = initial.apply(&difference) {
            let _ = TerminalPainter::full(&next);
            let _ = TerminalPainter::incremental(&initial, &next);
        }
    }

    let key = SessionKey::new([0x5a; 16]);
    let mut receiver = PacketReceiver::new(&key, Direction::ServerToClient);
    let mut datagram = data[..data.len().min(MAX_DATAGRAM_BYTES + 1)].to_vec();
    let _ = receiver.open(&mut datagram);
}

/// Interpret bounded input as fragment, SSP, and send-scheduler transitions.
pub fn exercise_state_transitions(data: &[u8]) {
    let mut now_ms = 0_u64;
    let mut reassembler = FragmentReassembler::new(now_ms);
    let mut synchronization = SynchronizationState::new();
    let mut scheduler = SendScheduler::new(now_ms);
    let mut timing = RttEstimator::new();

    for chunk in data.chunks(16).take(256) {
        let operation = chunk.first().copied().unwrap_or_default() % 8;
        now_ms = now_ms.saturating_add(u64::from(chunk.get(1).copied().unwrap_or_default() % 32));
        let number = u64::from_le_bytes(padded_eight(chunk));

        match operation {
            0 => {
                let fragment = Fragment {
                    timestamp: u16::try_from(now_ms & 0xffff).expect("masked timestamp fits"),
                    timestamp_reply: None,
                    identifier: number % 4,
                    number: u16::from(chunk.get(2).copied().unwrap_or_default() % 8),
                    is_final: chunk.get(3).is_some_and(|byte| byte & 1 != 0),
                    body: chunk.get(4..).unwrap_or_default(),
                };
                let _ = reassembler.push(now_ms, fragment);
            }
            1 => {
                let _ = reassembler.expire(now_ms);
            }
            2 => {
                let _ = synchronization.advance_local();
            }
            3 => {
                if let Ok(plan) = synchronization.plan_send(now_ms, 50) {
                    let _ = synchronization.commit_send(plan);
                }
            }
            4 => {
                let base_state = number % 64;
                let instruction = TransportInstruction {
                    protocol_version: if chunk.get(2).is_some_and(|byte| byte & 1 != 0) {
                        PROTOCOL_VERSION
                    } else {
                        PROTOCOL_VERSION + 1
                    },
                    base_state,
                    new_state: u64::from(chunk.get(3).copied().unwrap_or_default()) % 64,
                    acknowledged_state: u64::from(chunk.get(4).copied().unwrap_or_default()) % 64,
                    discard_before_state: u64::from(chunk.get(5).copied().unwrap_or_default()) % 64,
                    state_difference: chunk.get(6..).unwrap_or_default().to_vec(),
                    chaff: Vec::new(),
                };
                if let Ok(transition) = synchronization.begin_receive(&instruction)
                    && let RemoteStateDisposition::Apply(plan) = transition.remote_state
                {
                    let _ = synchronization.commit_receive(plan);
                }
            }
            5 => {
                let _ = scheduler.note_local_change(now_ms);
            }
            6 => {
                let _ = scheduler.note_acknowledgement_needed(now_ms);
            }
            _ => {
                let sample = number % 60_001;
                let _ = timing.observe_sample(sample);
                if let Ok(SchedulerPoll::Send(plan)) = scheduler.poll(now_ms, &timing, None) {
                    let _ = scheduler.commit_send(plan);
                }
            }
        }

        assert!(reassembler.incomplete_count() <= 1);
        assert!(reassembler.stored_bytes() <= MAX_COMPRESSED_INSTRUCTION_BYTES);
        let (sent, received) = synchronization.fuzz_retained_counts();
        assert!(sent <= MAX_SENT_STATES_AFTER_ACK);
        assert!(received <= MAX_RECEIVED_REFERENCE_STATES);
    }
}

fn padded_eight(bytes: &[u8]) -> [u8; 8] {
    let mut padded = [0_u8; 8];
    let copied = bytes.len().min(padded.len());
    padded[..copied].copy_from_slice(&bytes[..copied]);
    padded
}

// NexusSSH (P7): Zufallstests bei jedem `cargo test`, auch unter Windows. Der Zufall ist fest;
// `NEXUS_FUZZ_ROUNDS` setzt die Zahl der Runden je Eingang herauf.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::terminal::host_bytes_difference_for_test;

    /// xorshift64*: klein, schnell, und ohne weitere Abhängigkeit.
    struct Random(u64);

    impl Random {
        fn next(&mut self) -> u64 {
            self.0 ^= self.0 >> 12;
            self.0 ^= self.0 << 25;
            self.0 ^= self.0 >> 27;
            self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
        }

        fn byte(&mut self) -> u8 {
            self.next().to_le_bytes()[0]
        }

        fn below(&mut self, limit: usize) -> usize {
            usize::try_from(self.next() % u64::try_from(limit.max(1)).unwrap()).unwrap()
        }
    }

    fn rounds() -> usize {
        std::env::var("NEXUS_FUZZ_ROUNDS")
            .ok()
            .and_then(|rounds| rounds.parse().ok())
            .unwrap_or(5_000)
    }

    /// Echte Eingaben als Ausgangspunkt: eine Zeile von `mosh-server`, Unterschiede des Bildschirms
    /// mit den Folgen, die früher die Sitzung beendeten, Glocke und Titel.
    fn seeds() -> Vec<Vec<u8>> {
        let mut seeds = vec![
            b"MOSH CONNECT 60001 4NeCCgvZFe2RnPgrcU1PQw\r\n".to_vec(),
            b"warning: locale\nMOSH CONNECT 65535 4NeCCgvZFe2RnPgrcU1PQw".to_vec(),
        ];
        for bytes in [
            "A\u{301}\u{301}\u{301}\u{301}\u{301}\u{301}\u{301}\u{301}\u{301}\u{301}B".as_bytes(),
            b"\x1b[?1001h\x1b[?1015h\x1b]52;c;!!\x07",
            b"\x07\x1b]0;title;with;semicolons\x07\x1b]2;x\x07",
            b"\x1b[2J\x1b[1;1H\x1b[31mred\x1b[0m\r\n\x1b[L\x1b[M",
        ] {
            seeds.push(host_bytes_difference_for_test(bytes));
        }
        seeds
    }

    fn mutated(random: &mut Random, seeds: &[Vec<u8>]) -> Vec<u8> {
        let mut data = if random.below(4) == 0 {
            (0..random.below(2_048)).map(|_| random.byte()).collect()
        } else {
            seeds[random.below(seeds.len())].clone()
        };
        for _ in 0..random.below(8) {
            let at = random.below(data.len() + 1);
            match random.below(4) {
                0 if at < data.len() => data[at] ^= 1 << random.below(8),
                1 => data.insert(at, random.byte()),
                2 if at < data.len() => {
                    data.remove(at);
                }
                _ => data.insert(
                    at,
                    [0x00, 0x07, 0x1b, 0x7f, 0x80, 0x9c, 0xff][random.below(7)],
                ),
            }
        }
        data
    }

    #[test]
    fn untrusted_parsers_survive_mutated_input() {
        let seeds = seeds();
        let mut random = Random(0x6d6f_7368_2d70_6172);
        for _ in 0..rounds() {
            exercise_untrusted_parsers(&mutated(&mut random, &seeds));
        }
    }

    #[test]
    fn state_transitions_survive_random_input() {
        let mut random = Random(0x6d6f_7368_2d73_7370);
        for _ in 0..rounds() {
            let data: Vec<u8> = (0..random.below(4_096)).map(|_| random.byte()).collect();
            exercise_state_transitions(&data);
        }
    }
}
