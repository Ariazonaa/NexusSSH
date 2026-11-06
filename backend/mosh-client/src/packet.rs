use std::net::SocketAddr;

use zeroize::Zeroize;

use crate::crypto::{SessionCrypto, SessionKey};
use crate::limits::MAX_DATAGRAM_BYTES;

const HEADER_BYTES: usize = 8;
const TAG_BYTES: usize = 16;
const OVERHEAD_BYTES: usize = HEADER_BYTES + TAG_BYTES;
const MAX_PLAINTEXT_BYTES: usize = MAX_DATAGRAM_BYTES - OVERHEAD_BYTES;
// NexusSSH (P8): Wer `MAX_DATAGRAM_BYTES` anhebt, liefe sonst in die Panik von `ocb3`
// (siehe `crypto.rs`).
const _: () = assert!(
    MAX_PLAINTEXT_BYTES < crate::crypto::OCB3_MESSAGE_LIMIT_BYTES,
    "a datagram body must stay below the ocb3 message limit"
);
const MAX_SEQUENCE: u64 = (1_u64 << 63) - 1;
const DIRECTION_MASK: u64 = 1_u64 << 63;
const REPLAY_WINDOW_BITS: u64 = 64;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Direction {
    ClientToServer,
    ServerToClient,
}

impl Direction {
    const fn header_bit(self) -> u64 {
        match self {
            Self::ClientToServer => 0,
            Self::ServerToClient => DIRECTION_MASK,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum PacketError {
    DatagramTooShort,
    DatagramTooLarge,
    PlaintextTooLarge,
    OutputTooSmall,
    InvalidSequence,
    WrongDirection,
    AuthenticationFailed,
    Replay,
    TooOld,
    SequenceExhausted,
    UnexpectedSource,
}

pub(crate) struct PacketCodec(SessionCrypto);

impl PacketCodec {
    pub(crate) fn new(key: &SessionKey) -> Self {
        Self(SessionCrypto::new(key))
    }

    pub(crate) fn seal(
        &self,
        direction: Direction,
        sequence: u64,
        plaintext: &[u8],
        output: &mut [u8],
    ) -> Result<usize, PacketError> {
        if sequence > MAX_SEQUENCE {
            return Err(PacketError::InvalidSequence);
        }
        if plaintext.len() > MAX_PLAINTEXT_BYTES {
            return Err(PacketError::PlaintextTooLarge);
        }

        let datagram_len = OVERHEAD_BYTES + plaintext.len();
        if output.len() < datagram_len {
            return Err(PacketError::OutputTooSmall);
        }

        let header = direction.header_bit() | sequence;
        let header_bytes = header.to_be_bytes();
        output[..HEADER_BYTES].copy_from_slice(&header_bytes);
        let body_end = HEADER_BYTES + plaintext.len();
        output[HEADER_BYTES..body_end].copy_from_slice(plaintext);
        let tag = self
            .0
            .seal(nonce(header_bytes), &mut output[HEADER_BYTES..body_end])
            .map_err(|()| PacketError::PlaintextTooLarge)?;
        output[body_end..datagram_len].copy_from_slice(&tag);

        Ok(datagram_len)
    }

    fn open<'a>(
        &self,
        expected_direction: Direction,
        datagram: &'a mut [u8],
    ) -> Result<OpenedPacket<'a>, PacketError> {
        if datagram.len() > MAX_DATAGRAM_BYTES {
            return Err(PacketError::DatagramTooLarge);
        }
        if datagram.len() < OVERHEAD_BYTES {
            return Err(PacketError::DatagramTooShort);
        }

        let mut header_bytes = [0_u8; HEADER_BYTES];
        header_bytes.copy_from_slice(&datagram[..HEADER_BYTES]);
        let header = u64::from_be_bytes(header_bytes);
        if header & DIRECTION_MASK != expected_direction.header_bit() {
            return Err(PacketError::WrongDirection);
        }
        let sequence = header & MAX_SEQUENCE;

        let body_end = datagram.len() - TAG_BYTES;
        let mut tag = [0_u8; TAG_BYTES];
        tag.copy_from_slice(&datagram[body_end..]);
        let plaintext = &mut datagram[HEADER_BYTES..body_end];
        if self.0.open(nonce(header_bytes), plaintext, tag).is_err() {
            plaintext.zeroize();
            return Err(PacketError::AuthenticationFailed);
        }

        Ok(OpenedPacket {
            sequence,
            plaintext,
        })
    }
}

pub(crate) struct OpenedPacket<'a> {
    pub(crate) sequence: u64,
    pub(crate) plaintext: &'a mut [u8],
}

impl core::fmt::Debug for OpenedPacket<'_> {
    fn fmt(&self, formatter: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        formatter
            .debug_struct("OpenedPacket")
            .field("sequence", &self.sequence)
            .field("plaintext", &"[REDACTED]")
            .finish()
    }
}

pub(crate) struct PacketReceiver {
    codec: PacketCodec,
    direction: Direction,
    replay: ReplayWindow,
}

impl PacketReceiver {
    pub(crate) fn new(key: &SessionKey, direction: Direction) -> Self {
        Self {
            codec: PacketCodec::new(key),
            direction,
            replay: ReplayWindow::default(),
        }
    }

    pub(crate) fn open<'a>(
        &mut self,
        datagram: &'a mut [u8],
    ) -> Result<OpenedPacket<'a>, PacketError> {
        let packet = self.codec.open(self.direction, datagram)?;
        if let Err(error) = self.replay.accept(packet.sequence) {
            packet.plaintext.zeroize();
            return Err(error);
        }
        Ok(packet)
    }

    pub(crate) fn open_from<'a>(
        &mut self,
        expected_source: impl Into<SocketAddr>,
        actual_source: SocketAddr,
        datagram: &'a mut [u8],
    ) -> Result<OpenedPacket<'a>, PacketError> {
        if !is_same_endpoint(expected_source.into(), actual_source) {
            return Err(PacketError::UnexpectedSource);
        }
        self.open(datagram)
    }
}

// NexusSSH (P5): Bei IPv6 nennt `recv_from` auch Flow-Label und Scope; verglichen werden Adresse und
// Port, der Scope nur, wenn der erwartete einen nennt (link-lokale Adressen).
pub(crate) fn is_same_endpoint(expected: SocketAddr, actual: SocketAddr) -> bool {
    match (expected, actual) {
        (SocketAddr::V4(expected), SocketAddr::V4(actual)) => expected == actual,
        (SocketAddr::V6(expected), SocketAddr::V6(actual)) => {
            expected.ip() == actual.ip()
                && expected.port() == actual.port()
                && (expected.scope_id() == 0 || expected.scope_id() == actual.scope_id())
        }
        _ => false,
    }
}

#[derive(Default)]
struct ReplayWindow {
    highest: Option<u64>,
    seen: u64,
}

impl ReplayWindow {
    fn accept(&mut self, sequence: u64) -> Result<(), PacketError> {
        let Some(highest) = self.highest else {
            self.highest = Some(sequence);
            self.seen = 1;
            return Ok(());
        };

        if sequence > highest {
            let shift = sequence - highest;
            self.seen = if shift >= REPLAY_WINDOW_BITS {
                1
            } else {
                (self.seen << shift) | 1
            };
            self.highest = Some(sequence);
            return Ok(());
        }

        let distance = highest - sequence;
        if distance >= REPLAY_WINDOW_BITS {
            return Err(PacketError::TooOld);
        }
        let bit = 1_u64 << distance;
        if self.seen & bit != 0 {
            return Err(PacketError::Replay);
        }
        self.seen |= bit;
        Ok(())
    }
}

pub(crate) struct SendSequence(Option<u64>);

impl SendSequence {
    pub(crate) const fn new() -> Self {
        Self(Some(0))
    }

    pub(crate) fn take(&mut self) -> Result<u64, PacketError> {
        let sequence = self.0.ok_or(PacketError::SequenceExhausted)?;
        self.0 = sequence.checked_add(1).filter(|next| *next <= MAX_SEQUENCE);
        Ok(sequence)
    }
}

const fn nonce(header: [u8; HEADER_BYTES]) -> [u8; 12] {
    let mut nonce = [0_u8; 12];
    let mut index = 0;
    while index < HEADER_BYTES {
        nonce[index + 4] = header[index];
        index += 1;
    }
    nonce
}

#[cfg(test)]
mod tests;
