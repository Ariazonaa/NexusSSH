use core::fmt;
use std::net::{IpAddr, SocketAddr};

use crate::crypto::SessionKey;
use crate::error::BootstrapError;
use crate::limits::{
    DECODED_KEY_BYTES, ENCODED_KEY_BYTES, MAX_BOOTSTRAP_BYTES, MAX_BOOTSTRAP_LINES,
};
use base64::{Engine as _, engine::general_purpose::STANDARD_NO_PAD};

const CONNECT_WORDS: [&[u8]; 2] = [b"MOSH", b"CONNECT"];

/// Validated values from a stock `mosh-server` bootstrap exchange.
///
/// The server address is supplied as an [`IpAddr`] so address text is parsed
/// by the caller or by the standard library, not by the protocol parser. The
/// session key is intentionally opaque and is cleared when this value is
/// dropped.
pub struct Bootstrap {
    // NexusSSH (P5): IPv4 oder IPv6 – das Original nahm nur `SocketAddrV4`.
    server_addr: SocketAddr,
    session_key: SessionKey,
}

impl Bootstrap {
    /// Parse bounded, untrusted server output and bind it to a verified IPv4
    /// or IPv6 server address.
    ///
    /// Unrelated lines are ignored as raw bytes. A possible connect record is
    /// accepted only as the exact ASCII form `MOSH CONNECT <port> <key>` with
    /// single spaces and no leading or trailing whitespace.
    ///
    /// # Errors
    ///
    /// Returns [`BootstrapError`] for oversized input, missing or ambiguous
    /// records, a non-canonical record, an invalid port, or an invalid key.
    pub fn parse(server_address: impl Into<IpAddr>, output: &[u8]) -> Result<Self, BootstrapError> {
        if output.len() > MAX_BOOTSTRAP_BYTES {
            return Err(BootstrapError::OutputTooLarge);
        }
        if physical_line_count(output) > MAX_BOOTSTRAP_LINES {
            return Err(BootstrapError::TooManyLines);
        }

        let mut candidate = None;
        for raw_line in output.split_inclusive(|byte| *byte == b'\n') {
            let (line, terminated) = raw_line
                .strip_suffix(b"\n")
                .map_or((raw_line, false), |line| (line, true));
            let line = if terminated {
                line.strip_suffix(b"\r").unwrap_or(line)
            } else {
                line
            };

            if looks_like_connect_record(line) && candidate.replace(line).is_some() {
                return Err(BootstrapError::AmbiguousConnectRecord);
            }
        }

        let record = candidate.ok_or(BootstrapError::MissingConnectRecord)?;
        let (port, session_key) = parse_connect_record(record)?;

        Ok(Self {
            server_addr: SocketAddr::new(server_address.into(), port),
            session_key,
        })
    }

    /// Return the validated fixed UDP endpoint.
    #[must_use]
    pub const fn server_addr(&self) -> SocketAddr {
        self.server_addr
    }

    pub(crate) fn into_session_parts(self) -> (SocketAddr, SessionKey) {
        (self.server_addr, self.session_key)
    }

    // NexusSSH (P4): nur die Stock-Tests unter Linux benutzen das.
    #[cfg(all(test, target_os = "linux"))]
    pub(crate) fn with_test_server_addr(mut self, server_addr: impl Into<SocketAddr>) -> Self {
        self.server_addr = server_addr.into();
        self
    }

    #[cfg(test)]
    fn session_key(&self) -> &[u8; DECODED_KEY_BYTES] {
        self.session_key.as_bytes()
    }
}

impl fmt::Debug for Bootstrap {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let session_key_bytes = self.session_key.as_bytes().len();
        formatter
            .debug_struct("Bootstrap")
            .field("server_addr", &self.server_addr)
            .field("session_key_bytes", &session_key_bytes)
            .field("session_key", &"[REDACTED]")
            .finish()
    }
}

fn physical_line_count(output: &[u8]) -> usize {
    if output.is_empty() {
        return 0;
    }

    // Input is capped at 4 KiB, so a scalar scan is simpler than another dependency.
    #[allow(clippy::naive_bytecount)]
    let newline_count = output.iter().filter(|byte| **byte == b'\n').count();
    newline_count + usize::from(!output.ends_with(b"\n"))
}

fn looks_like_connect_record(line: &[u8]) -> bool {
    let mut words = line
        .split(u8::is_ascii_whitespace)
        .filter(|word| !word.is_empty());

    words.next() == Some(CONNECT_WORDS[0]) && words.next() == Some(CONNECT_WORDS[1])
}

fn parse_connect_record(record: &[u8]) -> Result<(u16, SessionKey), BootstrapError> {
    if record
        .iter()
        .any(|byte| byte.is_ascii_whitespace() && *byte != b' ')
    {
        return Err(BootstrapError::MalformedConnectRecord);
    }

    let mut fields = record.split(|byte| *byte == b' ');
    if fields.next() != Some(CONNECT_WORDS[0]) || fields.next() != Some(CONNECT_WORDS[1]) {
        return Err(BootstrapError::MalformedConnectRecord);
    }

    let port = fields
        .next()
        .ok_or(BootstrapError::MalformedConnectRecord)?;
    let encoded_key = fields
        .next()
        .ok_or(BootstrapError::MalformedConnectRecord)?;
    if fields.next().is_some() || port.is_empty() || encoded_key.is_empty() {
        return Err(BootstrapError::MalformedConnectRecord);
    }

    let port = parse_port(port)?;
    if encoded_key.len() != ENCODED_KEY_BYTES {
        return Err(BootstrapError::InvalidKey);
    }

    let mut decoded_key = zeroize::Zeroizing::new([0_u8; DECODED_KEY_BYTES]);
    let decoded = STANDARD_NO_PAD
        .decode_slice(encoded_key, decoded_key.as_mut())
        .map_err(|_| BootstrapError::InvalidKey)?;
    if decoded != DECODED_KEY_BYTES {
        return Err(BootstrapError::InvalidKey);
    }

    Ok((port, SessionKey::from_zeroizing(decoded_key)))
}

fn parse_port(bytes: &[u8]) -> Result<u16, BootstrapError> {
    if bytes.len() > 5 || !bytes.iter().all(u8::is_ascii_digit) {
        return Err(BootstrapError::InvalidPort);
    }

    let mut port = 0_u32;
    for byte in bytes {
        port = port * 10 + u32::from(*byte - b'0');
    }

    u16::try_from(port)
        .ok()
        .filter(|port| *port != 0)
        .ok_or(BootstrapError::InvalidPort)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::Ipv4Addr;

    const TEST_RECORD: &[u8] = b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw";

    #[test]
    fn decodes_the_exact_key_bytes() {
        let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, TEST_RECORD).unwrap();

        assert_eq!(
            bootstrap.session_key(),
            &[
                0xe0, 0xd7, 0x82, 0x0a, 0x0b, 0xd9, 0x15, 0xed, 0x91, 0x9c, 0xf8, 0x2b, 0x71, 0x4d,
                0x4f, 0x43,
            ]
        );
    }

    #[test]
    fn key_storage_supports_explicit_zeroization() {
        let mut bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, TEST_RECORD).unwrap();

        bootstrap.session_key.zeroize();

        assert_eq!(bootstrap.session_key(), &[0; DECODED_KEY_BYTES]);
    }
}
