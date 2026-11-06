use miniz_oxide::{
    DataFormat, MZFlush, MZStatus,
    deflate::compress_to_vec_zlib,
    inflate::stream::{InflateState, inflate},
};
use prost::Message;

use crate::limits::{
    MAX_CLIENT_OPERATIONS_AFTER_ACK, MAX_COMPRESSED_INSTRUCTION_BYTES,
    MAX_DECOMPRESSED_INSTRUCTION_BYTES, MAX_INPUT_COMMAND_BYTES, MAX_STATE_DIFFERENCE_BYTES,
    MAX_TERMINAL_COLUMNS, MAX_TERMINAL_ROWS,
};

pub(crate) const PROTOCOL_VERSION: u32 = 2;
pub(crate) const SHUTDOWN_STATE: u64 = u64::MAX;
const ZLIB_LEVEL: u8 = 6;
const DECOMPRESSION_CHUNK_BYTES: usize = 16 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum InstructionError {
    InvalidTerminalSize,
    DecodedTooLarge,
    CompressedTooLarge,
    InvalidCompression,
    TrailingCompressedData,
    InvalidMessage,
    IncompatibleVersion,
    InputTooLarge,
    TooManyClientOperations,
    StateDifferenceTooLarge,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum ClientOperation {
    Input(Vec<u8>),
    Resize { columns: u32, rows: u32 },
}

#[derive(Clone, PartialEq, Message)]
pub(crate) struct TransportInstruction {
    #[prost(uint32, tag = "1")]
    pub(crate) protocol_version: u32,
    #[prost(uint64, tag = "2")]
    pub(crate) base_state: u64,
    #[prost(uint64, tag = "3")]
    pub(crate) new_state: u64,
    #[prost(uint64, tag = "4")]
    pub(crate) acknowledged_state: u64,
    #[prost(uint64, tag = "5")]
    pub(crate) discard_before_state: u64,
    #[prost(bytes = "vec", tag = "6")]
    pub(crate) state_difference: Vec<u8>,
    #[prost(bytes = "vec", tag = "7")]
    pub(crate) chaff: Vec<u8>,
}

#[derive(Clone, PartialEq, Message)]
struct TerminalDifference {
    #[prost(message, repeated, tag = "1")]
    operations: Vec<TerminalOperation>,
}

#[derive(Clone, PartialEq, Message)]
struct TerminalOperation {
    #[prost(message, optional, tag = "2")]
    bytes_operation: Option<TerminalBytes>,
    #[prost(message, optional, tag = "3")]
    initial_size: Option<TerminalSize>,
}

#[derive(Clone, PartialEq, Message)]
struct TerminalBytes {
    #[prost(bytes = "vec", tag = "4")]
    bytes: Vec<u8>,
}

#[derive(Clone, Copy, PartialEq, Message)]
struct TerminalSize {
    #[prost(uint32, tag = "5")]
    columns: u32,
    #[prost(uint32, tag = "6")]
    rows: u32,
}

impl TransportInstruction {
    pub(crate) const fn is_shutdown(&self) -> bool {
        self.new_state == SHUTDOWN_STATE
    }

    pub(crate) fn initial(
        columns: u32,
        rows: u32,
        chaff: Vec<u8>,
    ) -> Result<Self, InstructionError> {
        if columns == 0 || columns > MAX_TERMINAL_COLUMNS || rows == 0 || rows > MAX_TERMINAL_ROWS {
            return Err(InstructionError::InvalidTerminalSize);
        }

        let difference = encode_client_difference(&[ClientOperation::Resize { columns, rows }])?;

        Ok(Self {
            protocol_version: PROTOCOL_VERSION,
            base_state: 0,
            new_state: 1,
            acknowledged_state: 0,
            discard_before_state: 0,
            state_difference: difference,
            chaff,
        })
    }

    pub(crate) fn encode_zlib(&self) -> Result<Vec<u8>, InstructionError> {
        if self.encoded_len() > MAX_DECOMPRESSED_INSTRUCTION_BYTES {
            return Err(InstructionError::DecodedTooLarge);
        }
        let compressed = compress_to_vec_zlib(&self.encode_to_vec(), ZLIB_LEVEL);
        if compressed.len() > MAX_COMPRESSED_INSTRUCTION_BYTES {
            return Err(InstructionError::CompressedTooLarge);
        }
        Ok(compressed)
    }

    pub(crate) fn decode_zlib(compressed: &[u8]) -> Result<Self, InstructionError> {
        if compressed.len() > MAX_COMPRESSED_INSTRUCTION_BYTES {
            return Err(InstructionError::CompressedTooLarge);
        }
        let decoded = decompress_exact_zlib(compressed)?;
        let instruction =
            Self::decode(decoded.as_slice()).map_err(|_| InstructionError::InvalidMessage)?;
        if instruction.protocol_version != PROTOCOL_VERSION {
            return Err(InstructionError::IncompatibleVersion);
        }
        Ok(instruction)
    }
}

pub(crate) fn encode_client_difference(
    operations: &[ClientOperation],
) -> Result<Vec<u8>, InstructionError> {
    if operations.len() > MAX_CLIENT_OPERATIONS_AFTER_ACK {
        return Err(InstructionError::TooManyClientOperations);
    }

    let mut wire_operations = Vec::with_capacity(operations.len());
    for operation in operations {
        let wire = match operation {
            ClientOperation::Input(bytes) => {
                if bytes.len() > MAX_INPUT_COMMAND_BYTES {
                    return Err(InstructionError::InputTooLarge);
                }
                TerminalOperation {
                    bytes_operation: Some(TerminalBytes {
                        bytes: bytes.clone(),
                    }),
                    initial_size: None,
                }
            }
            ClientOperation::Resize { columns, rows } => {
                if *columns == 0
                    || *columns > MAX_TERMINAL_COLUMNS
                    || *rows == 0
                    || *rows > MAX_TERMINAL_ROWS
                {
                    return Err(InstructionError::InvalidTerminalSize);
                }
                TerminalOperation {
                    bytes_operation: None,
                    initial_size: Some(TerminalSize {
                        columns: *columns,
                        rows: *rows,
                    }),
                }
            }
        };
        wire_operations.push(wire);
    }

    let difference = TerminalDifference {
        operations: wire_operations,
    };
    if difference.encoded_len() > MAX_STATE_DIFFERENCE_BYTES {
        return Err(InstructionError::StateDifferenceTooLarge);
    }
    Ok(difference.encode_to_vec())
}

fn decompress_exact_zlib(compressed: &[u8]) -> Result<Vec<u8>, InstructionError> {
    let initial_capacity = compressed
        .len()
        .saturating_mul(2)
        .clamp(64, DECOMPRESSION_CHUNK_BYTES)
        .min(MAX_DECOMPRESSED_INSTRUCTION_BYTES);
    let mut output = Vec::with_capacity(initial_capacity);
    let mut chunk = vec![0_u8; DECOMPRESSION_CHUNK_BYTES];
    let mut state = InflateState::new_boxed(DataFormat::Zlib);
    let mut consumed = 0;

    loop {
        let remaining = MAX_DECOMPRESSED_INSTRUCTION_BYTES - output.len();
        let writable = remaining.clamp(1, chunk.len());
        let result = inflate(
            &mut state,
            &compressed[consumed..],
            &mut chunk[..writable],
            MZFlush::None,
        );
        consumed += result.bytes_consumed;
        if result.bytes_written > remaining {
            return Err(InstructionError::DecodedTooLarge);
        }
        output.extend_from_slice(&chunk[..result.bytes_written]);

        match result.status {
            Ok(MZStatus::StreamEnd) => {
                if consumed != compressed.len() {
                    return Err(InstructionError::TrailingCompressedData);
                }
                return Ok(output);
            }
            Ok(MZStatus::Ok) if result.bytes_consumed != 0 || result.bytes_written != 0 => {}
            Ok(MZStatus::Ok | MZStatus::NeedDict) | Err(_) => {
                return Err(InstructionError::InvalidCompression);
            }
        }
    }
}

#[cfg(test)]
mod tests;
