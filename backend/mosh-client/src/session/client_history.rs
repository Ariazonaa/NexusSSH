use std::collections::{BTreeMap, VecDeque};

use crate::instruction::{ClientOperation, InstructionError, encode_client_difference};
use crate::limits::MAX_CLIENT_OPERATIONS_AFTER_ACK;

use super::DriverError;

#[derive(Debug)]
pub(super) struct ClientHistory {
    operation_offset: u64,
    next_operation: u64,
    operations: VecDeque<ClientOperation>,
    checkpoints: BTreeMap<u64, u64>,
}

impl ClientHistory {
    pub(super) fn new() -> Self {
        let mut checkpoints = BTreeMap::new();
        checkpoints.insert(0, 0);
        Self {
            operation_offset: 0,
            next_operation: 0,
            operations: VecDeque::new(),
            checkpoints,
        }
    }

    pub(super) fn append(&mut self, operation: ClientOperation) -> Result<(), DriverError> {
        if self.operations.len() == MAX_CLIENT_OPERATIONS_AFTER_ACK {
            return Err(InstructionError::TooManyClientOperations.into());
        }
        let next_operation = self
            .next_operation
            .checked_add(1)
            .ok_or(DriverError::OperationIndexExhausted)?;
        self.operations.push_back(operation);
        if let Err(error) = encode_client_difference(self.operations.make_contiguous()) {
            self.operations.pop_back();
            return Err(error.into());
        }
        self.next_operation = next_operation;
        Ok(())
    }

    pub(super) fn checkpoint(&mut self, state: u64) {
        self.checkpoints.insert(state, self.next_operation);
    }

    pub(super) fn difference(&mut self, base: u64, target: u64) -> Result<Vec<u8>, DriverError> {
        let start = *self
            .checkpoints
            .get(&base)
            .ok_or(DriverError::MissingClientState)?;
        let end = *self
            .checkpoints
            .get(&target)
            .ok_or(DriverError::MissingClientState)?;
        let start = start
            .checked_sub(self.operation_offset)
            .and_then(|index| usize::try_from(index).ok())
            .ok_or(DriverError::MissingClientState)?;
        let end = end
            .checked_sub(self.operation_offset)
            .and_then(|index| usize::try_from(index).ok())
            .ok_or(DriverError::MissingClientState)?;
        let operations = self.operations.make_contiguous();
        let range = operations
            .get(start..end)
            .ok_or(DriverError::MissingClientState)?;
        Ok(encode_client_difference(range)?)
    }

    pub(super) fn acknowledge(&mut self, state: u64) -> Result<(), DriverError> {
        let acknowledged = *self
            .checkpoints
            .get(&state)
            .ok_or(DriverError::MissingClientState)?;
        let discard = acknowledged
            .checked_sub(self.operation_offset)
            .and_then(|count| usize::try_from(count).ok())
            .ok_or(DriverError::MissingClientState)?;
        self.operations.drain(..discard);
        self.operation_offset = acknowledged;
        self.checkpoints.retain(|number, _| *number >= state);
        Ok(())
    }

    pub(super) fn remove_checkpoint(&mut self, state: u64) {
        self.checkpoints.remove(&state);
    }
}

#[cfg(test)]
mod tests;
