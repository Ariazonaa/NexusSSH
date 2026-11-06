use super::*;

#[test]
fn encodes_only_operations_after_the_selected_reference() {
    let mut history = ClientHistory::new();
    history
        .append(ClientOperation::Resize {
            columns: 80,
            rows: 24,
        })
        .unwrap();
    history.checkpoint(1);
    history
        .append(ClientOperation::Input(b"hello\n".to_vec()))
        .unwrap();
    history.checkpoint(2);

    assert_eq!(
        history.difference(1, 2).unwrap(),
        encode_client_difference(&[ClientOperation::Input(b"hello\n".to_vec())]).unwrap()
    );
    history.acknowledge(1).unwrap();
    assert_eq!(history.operation_offset, 1);
    assert_eq!(history.operations.len(), 1);
    assert!(!history.checkpoints.contains_key(&0));
}

#[test]
fn unchanged_checkpoint_encodes_an_empty_acknowledgement() {
    let mut history = ClientHistory::new();
    history.checkpoint(1);

    assert_eq!(
        history.difference(0, 1).unwrap(),
        encode_client_difference(&[]).unwrap()
    );
}

#[test]
fn acknowledgement_before_unsent_change_preserves_later_input_once() {
    let mut history = ClientHistory::new();
    history
        .append(ClientOperation::Resize {
            columns: 80,
            rows: 24,
        })
        .unwrap();
    history.checkpoint(1);
    history
        .append(ClientOperation::Input(b"pending".to_vec()))
        .unwrap();
    history.checkpoint(2);

    history.acknowledge(1).unwrap();
    assert_eq!(
        history.difference(1, 2).unwrap(),
        encode_client_difference(&[ClientOperation::Input(b"pending".to_vec())]).unwrap()
    );
    history.acknowledge(2).unwrap();
    assert!(history.operations.is_empty());
}
