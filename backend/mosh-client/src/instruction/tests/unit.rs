use super::super::*;

#[test]
fn initial_instruction_matches_the_observed_field_shape() {
    let instruction = TransportInstruction::initial(80, 24, vec![0]).unwrap();

    assert_eq!(instruction.protocol_version, 2);
    assert_eq!(instruction.base_state, 0);
    assert_eq!(instruction.new_state, 1);
    assert_eq!(
        instruction.state_difference,
        [0x0a, 0x06, 0x1a, 0x04, 0x28, 80, 0x30, 24]
    );
    assert_eq!(
        TransportInstruction::decode_zlib(&instruction.encode_zlib().unwrap()).unwrap(),
        instruction
    );
}

#[test]
fn only_the_reserved_target_is_a_shutdown_instruction() {
    let mut instruction = TransportInstruction::initial(80, 24, vec![0]).unwrap();
    assert!(!instruction.is_shutdown());
    instruction.new_state = SHUTDOWN_STATE;
    assert!(instruction.is_shutdown());
}

#[test]
fn client_input_and_resize_preserve_the_verified_operation_order() {
    let difference = encode_client_difference(&[
        ClientOperation::Input("x中".as_bytes().to_vec()),
        ClientOperation::Resize {
            columns: 81,
            rows: 25,
        },
    ])
    .unwrap();

    assert_eq!(
        &difference[..10],
        &[0x0a, 0x08, 0x12, 0x06, 0x22, 0x04, 0x78, 0xe4, 0xb8, 0xad]
    );
    assert_eq!(
        &difference[10..],
        &[0x0a, 0x06, 0x1a, 0x04, 0x28, 81, 0x30, 25]
    );
}

#[test]
fn client_difference_limits_fail_before_encoding() {
    assert!(
        encode_client_difference(&vec![
            ClientOperation::Input(Vec::new());
            MAX_CLIENT_OPERATIONS_AFTER_ACK
        ])
        .is_ok()
    );
    assert_eq!(
        encode_client_difference(&[ClientOperation::Input(vec![0; MAX_INPUT_COMMAND_BYTES + 1])]),
        Err(InstructionError::InputTooLarge)
    );
    assert_eq!(
        encode_client_difference(&vec![
            ClientOperation::Input(Vec::new());
            MAX_CLIENT_OPERATIONS_AFTER_ACK + 1
        ]),
        Err(InstructionError::TooManyClientOperations)
    );
}

#[test]
fn rejects_invalid_sizes_versions_compression_and_trailing_data() {
    assert_eq!(
        TransportInstruction::initial(0, 24, vec![]).unwrap_err(),
        InstructionError::InvalidTerminalSize
    );
    assert_eq!(
        TransportInstruction::decode_zlib(b"not zlib").unwrap_err(),
        InstructionError::InvalidCompression
    );

    let mut wrong_version = TransportInstruction::initial(80, 24, vec![]).unwrap();
    wrong_version.protocol_version = 3;
    assert_eq!(
        TransportInstruction::decode_zlib(&wrong_version.encode_zlib().unwrap()).unwrap_err(),
        InstructionError::IncompatibleVersion
    );

    let instruction = TransportInstruction::initial(80, 24, vec![]).unwrap();
    let mut trailing = instruction.encode_zlib().unwrap();
    trailing.extend_from_slice(b"trailing");
    assert_eq!(
        TransportInstruction::decode_zlib(&trailing).unwrap_err(),
        InstructionError::TrailingCompressedData
    );

    let mut truncated = instruction.encode_zlib().unwrap();
    truncated.pop();
    assert_eq!(
        TransportInstruction::decode_zlib(&truncated).unwrap_err(),
        InstructionError::InvalidCompression
    );
}

#[test]
fn streaming_decompression_enforces_the_decoded_limit() {
    let at_limit = vec![b'x'; MAX_DECOMPRESSED_INSTRUCTION_BYTES];
    let compressed = compress_to_vec_zlib(&at_limit, ZLIB_LEVEL);
    assert_eq!(decompress_exact_zlib(&compressed).unwrap(), at_limit);

    let above_limit = vec![b'x'; MAX_DECOMPRESSED_INSTRUCTION_BYTES + 1];
    let compressed = compress_to_vec_zlib(&above_limit, ZLIB_LEVEL);
    assert_eq!(
        decompress_exact_zlib(&compressed).unwrap_err(),
        InstructionError::DecodedTooLarge
    );
}
