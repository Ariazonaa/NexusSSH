use core::fmt;

use aes::Aes128;
use ocb3::{
    Ocb3,
    aead::{AeadInOut, KeyInit},
    consts::{U12, U16},
};
#[cfg(test)]
use zeroize::Zeroize;
use zeroize::Zeroizing;

use crate::limits::DECODED_KEY_BYTES;

const NONCE_BYTES: usize = 12;
const TAG_BYTES: usize = 16;
const L_TABLE_SIZE: usize = 7;
// NexusSSH (P8): `ocb3` 0.2.0 verschlüsselt nur Nachrichten unter `16 << L_TABLE_SIZE` Bytes,
// entschlüsselt aber genau diese Länge noch und greift dann hinter seine Tabelle (Panik,
// Off-by-one in `decrypt`). Jede Nachricht muss deshalb kürzer sein; `packet.rs` stellt das zur
// Übersetzungszeit fest.
pub(crate) const OCB3_MESSAGE_LIMIT_BYTES: usize = 1 << (L_TABLE_SIZE + 4);

type MoshOcb3 = Ocb3<Aes128, U12, U16, L_TABLE_SIZE>;

pub(crate) struct SessionKey(Zeroizing<[u8; DECODED_KEY_BYTES]>);

impl SessionKey {
    pub(crate) fn new(bytes: [u8; DECODED_KEY_BYTES]) -> Self {
        Self(Zeroizing::new(bytes))
    }

    pub(crate) const fn from_zeroizing(bytes: Zeroizing<[u8; DECODED_KEY_BYTES]>) -> Self {
        Self(bytes)
    }

    pub(crate) fn as_bytes(&self) -> &[u8; DECODED_KEY_BYTES] {
        &self.0
    }

    #[cfg(test)]
    pub(crate) fn zeroize(&mut self) {
        self.0.zeroize();
    }
}

impl fmt::Debug for SessionKey {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("SessionKey([REDACTED])")
    }
}

pub(crate) struct SessionCrypto(MoshOcb3);

impl SessionCrypto {
    pub(crate) fn new(key: &SessionKey) -> Self {
        Self(
            MoshOcb3::new_from_slice(key.as_bytes())
                .expect("the fixed-size SessionKey matches AES-128"),
        )
    }

    pub(crate) fn seal(
        &self,
        nonce: [u8; NONCE_BYTES],
        buffer: &mut [u8],
    ) -> Result<[u8; TAG_BYTES], ()> {
        let nonce = nonce.into();
        let tag = self
            .0
            .encrypt_inout_detached(&nonce, &[], buffer.into())
            .map_err(|_| ())?;
        Ok(tag.into())
    }

    pub(crate) fn open(
        &self,
        nonce: [u8; NONCE_BYTES],
        buffer: &mut [u8],
        tag: [u8; TAG_BYTES],
    ) -> Result<(), ()> {
        let nonce = nonce.into();
        let tag = tag.into();
        self.0
            .decrypt_inout_detached(&nonce, &[], buffer.into(), &tag)
            .map_err(|_| ())
    }
}

impl fmt::Debug for SessionCrypto {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("SessionCrypto([REDACTED])")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_rfc_7253_aes_128_vector_with_empty_associated_data() {
        let key = SessionKey::new([
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d,
            0x0e, 0x0f,
        ]);
        let crypto = SessionCrypto::new(&key);
        let nonce = [
            0xbb, 0xaa, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x03,
        ];
        let mut plaintext = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        let tag = crypto.seal(nonce, &mut plaintext).unwrap();

        assert_eq!(plaintext, [0x45, 0xdd, 0x69, 0xf8, 0xf5, 0xaa, 0xe7, 0x24]);
        assert_eq!(
            tag,
            [
                0x14, 0x05, 0x4c, 0xd1, 0xf3, 0x5d, 0x82, 0x76, 0x0b, 0x2c, 0xd0, 0x0d, 0x2f, 0x99,
                0xbf, 0xa9,
            ]
        );
    }

    // NexusSSH (P8): Die längste Nachricht, die `packet.rs` zulässt, und die längste, die `ocb3`
    // sicher entschlüsselt, gehen hin und zurück; ab der Grenze verweigert schon das Verschlüsseln.
    #[test]
    fn the_largest_datagram_body_stays_below_the_ocb3_limit() {
        let crypto = SessionCrypto::new(&SessionKey::new([0x42; DECODED_KEY_BYTES]));
        let largest_body = crate::limits::MAX_DATAGRAM_BYTES - 8 - TAG_BYTES;
        assert!(largest_body < OCB3_MESSAGE_LIMIT_BYTES);
        for length in [largest_body, OCB3_MESSAGE_LIMIT_BYTES - 1] {
            let mut buffer = vec![0x5a; length];
            let tag = crypto.seal([7; NONCE_BYTES], &mut buffer).unwrap();
            crypto.open([7; NONCE_BYTES], &mut buffer, tag).unwrap();
            assert!(buffer.iter().all(|byte| *byte == 0x5a));
        }
        let mut at_limit = vec![0; OCB3_MESSAGE_LIMIT_BYTES];
        assert!(crypto.seal([7; NONCE_BYTES], &mut at_limit).is_err());
    }
}
