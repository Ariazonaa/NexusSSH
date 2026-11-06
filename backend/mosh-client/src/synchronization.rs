use core::fmt;
use std::collections::VecDeque;

use crate::instruction::{PROTOCOL_VERSION, SHUTDOWN_STATE, TransportInstruction};
use crate::limits::{
    MAX_RECEIVED_REFERENCE_STATES, MAX_RETRANSMISSION_TIMEOUT_MS, MAX_SENT_STATES_AFTER_ACK,
    MIN_RETRANSMISSION_TIMEOUT_MS,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SynchronizationError {
    StateNumberExhausted,
    InvalidAcknowledgement,
    IncompatibleVersion,
    InvalidStateTransition,
    InvalidThrowaway,
    InvalidRetransmissionTimeout,
    TimeMovedBackwards,
    NoNewState,
    StalePlan,
    GenerationExhausted,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum AcknowledgementDisposition {
    Advanced { from: u64, to: u64 },
    Current,
    Stale,
    Unknown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct SendPlan {
    generation: u64,
    pub(crate) base_state: u64,
    pub(crate) target_state: u64,
    pub(crate) acknowledged_state: u64,
    pub(crate) throwaway_state: u64,
    planned_at_ms: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct SendCommit {
    pub(crate) capacity_evicted_state: Option<u64>,
}

impl SendPlan {
    pub(crate) fn instruction(
        self,
        state_difference: Vec<u8>,
        chaff: Vec<u8>,
    ) -> TransportInstruction {
        TransportInstruction {
            protocol_version: PROTOCOL_VERSION,
            base_state: self.base_state,
            new_state: self.target_state,
            acknowledged_state: self.acknowledged_state,
            discard_before_state: self.throwaway_state,
            state_difference,
            chaff,
        }
    }
}

pub(crate) struct ApplyPlan<'a> {
    generation: u64,
    pub(crate) base_state: u64,
    pub(crate) target_state: u64,
    pub(crate) throwaway_state: u64,
    pub(crate) difference: &'a [u8],
    becomes_latest: bool,
}

impl fmt::Debug for ApplyPlan<'_> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ApplyPlan")
            .field("base_state", &self.base_state)
            .field("target_state", &self.target_state)
            .field("throwaway_state", &self.throwaway_state)
            .field("difference_bytes", &self.difference.len())
            .field("difference", &"[REDACTED]")
            .field("becomes_latest", &self.becomes_latest)
            .finish()
    }
}

#[derive(Debug)]
pub(crate) enum RemoteStateDisposition<'a> {
    Apply(ApplyPlan<'a>),
    AlreadyConstructed,
    MissingReference,
    BelowThrowawayFloor,
    BelowRetentionWindow,
}

#[derive(Debug)]
pub(crate) struct ReceiveTransition<'a> {
    pub(crate) acknowledgement: AcknowledgementDisposition,
    pub(crate) remote_state: RemoteStateDisposition<'a>,
}

#[derive(Debug)]
pub(crate) struct ShutdownTransition<'a> {
    pub(crate) base_state: u64,
    pub(crate) difference: &'a [u8],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct ReceiveCommit {
    pub(crate) discard_before_state: u64,
    pub(crate) capacity_evicted_state: Option<u64>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct SentState {
    number: u64,
    sent_at_ms: u64,
}

#[derive(Debug)]
pub(crate) struct SynchronizationState {
    local_latest: u64,
    known_receiver_state: u64,
    sent_states: VecDeque<SentState>,
    sender_generation: u64,
    remote_latest: u64,
    remote_throwaway_floor: u64,
    received_states: VecDeque<u64>,
    receiver_generation: u64,
}

impl SynchronizationState {
    pub(crate) fn new() -> Self {
        let mut received_states = VecDeque::with_capacity(MAX_RECEIVED_REFERENCE_STATES);
        received_states.push_back(0);
        Self {
            local_latest: 0,
            known_receiver_state: 0,
            sent_states: VecDeque::with_capacity(MAX_SENT_STATES_AFTER_ACK),
            sender_generation: 0,
            remote_latest: 0,
            remote_throwaway_floor: 0,
            received_states,
            receiver_generation: 0,
        }
    }

    pub(crate) const fn local_latest(&self) -> u64 {
        self.local_latest
    }

    pub(crate) const fn known_receiver_state(&self) -> u64 {
        self.known_receiver_state
    }

    pub(crate) const fn remote_latest(&self) -> u64 {
        self.remote_latest
    }

    pub(crate) fn latest_unacknowledged_sent_at_ms(&self) -> Option<u64> {
        self.sent_states.back().map(|state| state.sent_at_ms)
    }

    pub(crate) fn sent_state_sent_at_ms(&self, number: u64) -> Option<u64> {
        self.sent_states
            .iter()
            .find(|state| state.number == number)
            .map(|state| state.sent_at_ms)
    }

    #[cfg(any(fuzzing, test))] // NexusSSH (P7): auch für `fuzzing::tests`
    pub(crate) fn fuzz_retained_counts(&self) -> (usize, usize) {
        (self.sent_states.len(), self.received_states.len())
    }

    pub(crate) fn advance_local(&mut self) -> Result<u64, SynchronizationError> {
        let next_state = self
            .local_latest
            .checked_add(1)
            .ok_or(SynchronizationError::StateNumberExhausted)?;
        let next_generation = self
            .sender_generation
            .checked_add(1)
            .ok_or(SynchronizationError::GenerationExhausted)?;
        self.local_latest = next_state;
        self.sender_generation = next_generation;
        Ok(self.local_latest)
    }

    pub(crate) fn plan_send(
        &self,
        now_ms: u64,
        retransmission_timeout_ms: u64,
    ) -> Result<SendPlan, SynchronizationError> {
        if !(MIN_RETRANSMISSION_TIMEOUT_MS..=MAX_RETRANSMISSION_TIMEOUT_MS)
            .contains(&retransmission_timeout_ms)
        {
            return Err(SynchronizationError::InvalidRetransmissionTimeout);
        }

        let mut base_state = self.known_receiver_state;
        for sent in self.sent_states.iter().rev() {
            let elapsed = now_ms
                .checked_sub(sent.sent_at_ms)
                .ok_or(SynchronizationError::TimeMovedBackwards)?;
            if elapsed < retransmission_timeout_ms {
                base_state = sent.number;
                break;
            }
        }
        if self.local_latest <= base_state {
            return Err(SynchronizationError::NoNewState);
        }

        Ok(SendPlan {
            generation: self.sender_generation,
            base_state,
            target_state: self.local_latest,
            acknowledged_state: self.remote_latest,
            throwaway_state: self.known_receiver_state,
            planned_at_ms: now_ms,
        })
    }

    pub(crate) fn commit_send(
        &mut self,
        plan: SendPlan,
    ) -> Result<SendCommit, SynchronizationError> {
        if plan.generation != self.sender_generation
            || plan.target_state != self.local_latest
            || plan.acknowledged_state != self.remote_latest
            || plan.throwaway_state != self.known_receiver_state
        {
            return Err(SynchronizationError::StalePlan);
        }
        let next_generation = self
            .sender_generation
            .checked_add(1)
            .ok_or(SynchronizationError::GenerationExhausted)?;

        let mut capacity_evicted_state = None;
        if plan.target_state > self.known_receiver_state {
            if let Some(sent) = self
                .sent_states
                .iter_mut()
                .find(|sent| sent.number == plan.target_state)
            {
                sent.sent_at_ms = plan.planned_at_ms;
            } else {
                if self.sent_states.len() == MAX_SENT_STATES_AFTER_ACK {
                    capacity_evicted_state = self.sent_states.pop_front().map(|state| state.number);
                }
                self.sent_states.push_back(SentState {
                    number: plan.target_state,
                    sent_at_ms: plan.planned_at_ms,
                });
            }
        }
        self.sender_generation = next_generation;
        Ok(SendCommit {
            capacity_evicted_state,
        })
    }

    pub(crate) fn begin_receive<'a>(
        &mut self,
        instruction: &'a TransportInstruction,
    ) -> Result<ReceiveTransition<'a>, SynchronizationError> {
        self.validate_instruction(instruction)?;
        let acknowledgement = self.observe_acknowledgement(instruction.acknowledged_state)?;

        let remote_state = if instruction.new_state < self.remote_throwaway_floor
            || instruction.base_state < self.remote_throwaway_floor
        {
            RemoteStateDisposition::BelowThrowawayFloor
        } else if self.received_states.contains(&instruction.new_state) {
            RemoteStateDisposition::AlreadyConstructed
        } else if !self.received_states.contains(&instruction.base_state) {
            RemoteStateDisposition::MissingReference
        } else if !self.can_retain_received_state(instruction.new_state) {
            RemoteStateDisposition::BelowRetentionWindow
        } else {
            RemoteStateDisposition::Apply(ApplyPlan {
                generation: self.receiver_generation,
                base_state: instruction.base_state,
                target_state: instruction.new_state,
                throwaway_state: instruction.discard_before_state,
                difference: &instruction.state_difference,
                becomes_latest: instruction.new_state > self.remote_latest,
            })
        };

        Ok(ReceiveTransition {
            acknowledgement,
            remote_state,
        })
    }

    pub(crate) fn begin_shutdown_receive<'a>(
        &mut self,
        instruction: &'a TransportInstruction,
    ) -> Result<ShutdownTransition<'a>, SynchronizationError> {
        if instruction.protocol_version != PROTOCOL_VERSION
            || instruction.new_state != SHUTDOWN_STATE
            || instruction.acknowledged_state > self.local_latest
            || instruction.discard_before_state > instruction.base_state
            || instruction.base_state < self.remote_throwaway_floor
            || !self.received_states.contains(&instruction.base_state)
        {
            return Err(SynchronizationError::InvalidStateTransition);
        }
        self.observe_acknowledgement(instruction.acknowledged_state)?;
        Ok(ShutdownTransition {
            base_state: instruction.base_state,
            difference: &instruction.state_difference,
        })
    }

    #[expect(
        clippy::needless_pass_by_value,
        reason = "consuming the plan makes each state-application token single-use"
    )]
    pub(crate) fn commit_receive(
        &mut self,
        plan: ApplyPlan<'_>,
    ) -> Result<ReceiveCommit, SynchronizationError> {
        if plan.generation != self.receiver_generation
            || !self.received_states.contains(&plan.base_state)
            || self.received_states.contains(&plan.target_state)
        {
            return Err(SynchronizationError::StalePlan);
        }
        let next_generation = self
            .receiver_generation
            .checked_add(1)
            .ok_or(SynchronizationError::GenerationExhausted)?;

        if plan.becomes_latest {
            self.remote_latest = plan.target_state;
            self.remote_throwaway_floor = self.remote_throwaway_floor.max(plan.throwaway_state);
            self.received_states
                .retain(|number| *number >= self.remote_throwaway_floor);
        }

        let capacity_evicted_state = if self.received_states.len() == MAX_RECEIVED_REFERENCE_STATES
        {
            let protected_floor =
                self.received_states.front().copied() == Some(self.remote_throwaway_floor);
            let removal_index = usize::from(protected_floor);
            self.received_states.remove(removal_index)
        } else {
            None
        };
        let insertion_index = self
            .received_states
            .iter()
            .position(|number| *number > plan.target_state)
            .unwrap_or(self.received_states.len());
        self.received_states
            .insert(insertion_index, plan.target_state);
        self.receiver_generation = next_generation;
        Ok(ReceiveCommit {
            discard_before_state: self.remote_throwaway_floor,
            capacity_evicted_state,
        })
    }

    fn validate_instruction(
        &self,
        instruction: &TransportInstruction,
    ) -> Result<(), SynchronizationError> {
        if instruction.protocol_version != PROTOCOL_VERSION {
            return Err(SynchronizationError::IncompatibleVersion);
        }
        if instruction.acknowledged_state > self.local_latest {
            return Err(SynchronizationError::InvalidAcknowledgement);
        }
        if instruction.new_state <= instruction.base_state {
            return Err(SynchronizationError::InvalidStateTransition);
        }
        if instruction.discard_before_state > instruction.base_state {
            return Err(SynchronizationError::InvalidThrowaway);
        }
        Ok(())
    }

    fn can_retain_received_state(&self, target_state: u64) -> bool {
        if self.received_states.len() < MAX_RECEIVED_REFERENCE_STATES {
            return true;
        }
        let protected_floor =
            self.received_states.front().copied() == Some(self.remote_throwaway_floor);
        let first_evictable = usize::from(protected_floor);
        self.received_states
            .get(first_evictable)
            .is_some_and(|number| target_state > *number)
    }

    fn observe_acknowledgement(
        &mut self,
        acknowledgement: u64,
    ) -> Result<AcknowledgementDisposition, SynchronizationError> {
        if acknowledgement < self.known_receiver_state {
            return Ok(AcknowledgementDisposition::Stale);
        }
        if acknowledgement == self.known_receiver_state {
            return Ok(AcknowledgementDisposition::Current);
        }
        if !self
            .sent_states
            .iter()
            .any(|sent| sent.number == acknowledgement)
        {
            return Ok(AcknowledgementDisposition::Unknown);
        }

        let next_generation = self
            .sender_generation
            .checked_add(1)
            .ok_or(SynchronizationError::GenerationExhausted)?;
        let previous = self.known_receiver_state;
        self.known_receiver_state = acknowledgement;
        self.sent_states
            .retain(|sent| sent.number > acknowledgement);
        self.sender_generation = next_generation;
        Ok(AcknowledgementDisposition::Advanced {
            from: previous,
            to: acknowledgement,
        })
    }
}

#[cfg(test)]
mod tests;
