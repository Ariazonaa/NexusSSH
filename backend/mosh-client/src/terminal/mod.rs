mod paint;
mod state;

use prost::Message;

use crate::limits::{MAX_STATE_DIFFERENCE_BYTES, MAX_TERMINAL_OPERATIONS_PER_DIFFERENCE};

#[allow(unused_imports)]
pub(crate) use paint::{PaintError, TerminalPainter};
pub(crate) use state::{TerminalState, is_valid_size};

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct TerminalDifference {
    operations: Vec<TerminalOperation>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum TerminalOperation {
    HostBytes(Vec<u8>),
    Resize { columns: u32, rows: u32 },
    EchoAcknowledgement(u64),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum TerminalError {
    DifferenceTooLarge,
    TooManyOperations,
    InvalidMessage,
    InvalidOperation,
    InvalidTerminalSize,
    InvalidUtf8,
    IncompleteSequence,
    UnsupportedCharacter,
    UnsupportedControl,
    UnsupportedEscape,
    // NexusSSH (P2): `UnsupportedCsi`, `UnsupportedOsc` und `CellEncodingTooLarge` fallen weg –
    // solche Ausgaben beenden die Sitzung nicht mehr.
    TooManyScalarsInCell,
}

#[derive(Clone, PartialEq, Message)]
struct WireHostDifference {
    #[prost(message, repeated, tag = "1")]
    operations: Vec<WireHostOperation>,
}

#[derive(Clone, PartialEq, Message)]
struct WireHostOperation {
    #[prost(message, optional, tag = "2")]
    host_bytes: Option<WireHostBytes>,
    #[prost(message, optional, tag = "3")]
    size: Option<WireHostSize>,
    #[prost(message, optional, tag = "7")]
    echo_acknowledgement: Option<WireEchoAcknowledgement>,
}

#[derive(Clone, PartialEq, Message)]
struct WireHostBytes {
    #[prost(bytes = "vec", tag = "4")]
    bytes: Vec<u8>,
}

#[derive(Clone, Copy, PartialEq, Message)]
struct WireHostSize {
    #[prost(uint32, tag = "5")]
    columns: u32,
    #[prost(uint32, tag = "6")]
    rows: u32,
}

#[derive(Clone, Copy, PartialEq, Message)]
struct WireEchoAcknowledgement {
    #[prost(uint64, tag = "8")]
    number: u64,
}

impl TerminalDifference {
    pub(crate) fn decode(bytes: &[u8]) -> Result<Self, TerminalError> {
        if bytes.len() > MAX_STATE_DIFFERENCE_BYTES {
            return Err(TerminalError::DifferenceTooLarge);
        }
        let wire = WireHostDifference::decode(bytes).map_err(|_| TerminalError::InvalidMessage)?;
        if wire.operations.len() > MAX_TERMINAL_OPERATIONS_PER_DIFFERENCE {
            return Err(TerminalError::TooManyOperations);
        }

        let mut operations = Vec::with_capacity(wire.operations.len());
        for operation in wire.operations {
            let known_fields = usize::from(operation.host_bytes.is_some())
                + usize::from(operation.size.is_some())
                + usize::from(operation.echo_acknowledgement.is_some());
            if known_fields != 1 {
                return Err(TerminalError::InvalidOperation);
            }

            if let Some(host_bytes) = operation.host_bytes {
                operations.push(TerminalOperation::HostBytes(host_bytes.bytes));
            } else if let Some(size) = operation.size {
                operations.push(TerminalOperation::Resize {
                    columns: size.columns,
                    rows: size.rows,
                });
            } else if let Some(echo) = operation.echo_acknowledgement {
                operations.push(TerminalOperation::EchoAcknowledgement(echo.number));
            }
        }
        Ok(Self { operations })
    }

    pub(crate) fn latest_echo_acknowledgement(&self) -> Option<u64> {
        self.operations.iter().rev().find_map(|operation| {
            if let TerminalOperation::EchoAcknowledgement(number) = operation {
                Some(*number)
            } else {
                None
            }
        })
    }
}

// NexusSSH (P9): ein Unterschied aus einem einzigen Stück Host-Bytes, für Tests des Treibers.
#[cfg(test)]
pub(crate) fn host_bytes_difference_for_test(bytes: &[u8]) -> Vec<u8> {
    WireHostDifference {
        operations: vec![WireHostOperation {
            host_bytes: Some(WireHostBytes {
                bytes: bytes.to_vec(),
            }),
            size: None,
            echo_acknowledgement: None,
        }],
    }
    .encode_to_vec()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn operation(operation: WireHostOperation) -> Vec<u8> {
        WireHostDifference {
            operations: vec![operation],
        }
        .encode_to_vec()
    }

    #[test]
    fn decodes_each_verified_server_operation() {
        let host_bytes = TerminalDifference::decode(&operation(WireHostOperation {
            host_bytes: Some(WireHostBytes {
                bytes: "A中Z".as_bytes().to_vec(),
            }),
            size: None,
            echo_acknowledgement: None,
        }))
        .unwrap();
        assert_eq!(
            host_bytes.operations,
            [TerminalOperation::HostBytes("A中Z".as_bytes().to_vec())]
        );

        let resize = TerminalDifference::decode(&operation(WireHostOperation {
            host_bytes: None,
            size: Some(WireHostSize {
                columns: 81,
                rows: 25,
            }),
            echo_acknowledgement: None,
        }))
        .unwrap();
        assert_eq!(
            resize.operations,
            [TerminalOperation::Resize {
                columns: 81,
                rows: 25
            }]
        );

        let echo = TerminalDifference::decode(&operation(WireHostOperation {
            host_bytes: None,
            size: None,
            echo_acknowledgement: Some(WireEchoAcknowledgement { number: 7 }),
        }))
        .unwrap();
        assert_eq!(echo.operations, [TerminalOperation::EchoAcknowledgement(7)]);
        assert_eq!(echo.latest_echo_acknowledgement(), Some(7));
        assert_eq!(host_bytes.latest_echo_acknowledgement(), None);
    }

    #[test]
    fn rejects_oversized_malformed_ambiguous_and_excessive_messages() {
        assert_eq!(
            TerminalDifference::decode(&vec![0; MAX_STATE_DIFFERENCE_BYTES + 1]),
            Err(TerminalError::DifferenceTooLarge)
        );
        assert_eq!(
            TerminalDifference::decode(&[0x0a, 0x02, 0xff]),
            Err(TerminalError::InvalidMessage)
        );
        assert_eq!(
            TerminalDifference::decode(&operation(WireHostOperation {
                host_bytes: Some(WireHostBytes { bytes: Vec::new() }),
                size: Some(WireHostSize {
                    columns: 80,
                    rows: 24,
                }),
                echo_acknowledgement: None,
            })),
            Err(TerminalError::InvalidOperation)
        );

        let at_limit = WireHostDifference {
            operations: vec![
                WireHostOperation {
                    host_bytes: Some(WireHostBytes { bytes: Vec::new() }),
                    size: None,
                    echo_acknowledgement: None,
                };
                MAX_TERMINAL_OPERATIONS_PER_DIFFERENCE
            ],
        }
        .encode_to_vec();
        assert!(TerminalDifference::decode(&at_limit).is_ok());

        let above_limit = WireHostDifference {
            operations: vec![
                WireHostOperation {
                    host_bytes: Some(WireHostBytes { bytes: Vec::new() }),
                    size: None,
                    echo_acknowledgement: None,
                };
                MAX_TERMINAL_OPERATIONS_PER_DIFFERENCE + 1
            ],
        }
        .encode_to_vec();
        assert_eq!(
            TerminalDifference::decode(&above_limit),
            Err(TerminalError::TooManyOperations)
        );
    }
}
