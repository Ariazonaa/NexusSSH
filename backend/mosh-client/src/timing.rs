use crate::limits::{
    HEARTBEAT_INTERVAL_MS, INITIAL_RETRANSMISSION_TIMEOUT_MS, MAX_ACKNOWLEDGEMENT_DELAY_MS,
    MAX_FRAME_INTERVAL_MS, MAX_RETRANSMISSION_TIMEOUT_MS, MAX_RTT_SAMPLE_MS,
    MAX_TIMESTAMP_REPLY_AGE_MS, MIN_FRAME_INTERVAL_MS, MIN_RETRANSMISSION_TIMEOUT_MS,
    STATE_CHANGE_COLLECTION_MS,
};

const CLOCK_GRANULARITY_MS: u64 = 1;
const TIMESTAMP_MODULUS: u64 = 1_u64 << 16;
const NO_TIMESTAMP_REPLY: u16 = u16::MAX;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum TimingError {
    TimeMovedBackwards,
    TimerExhausted,
    InvalidRttSample,
    StalePlan,
    GenerationExhausted,
}

/// Integer implementation of the published TCP-style SRTT and RTTVAR update.
///
/// Values are stored in eighths of a millisecond so the required alpha=1/8
/// update does not need floating point. Wire timestamp interpretation remains
/// outside this type until its wrap behavior is independently verified.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct RttEstimator {
    smoothed_rtt_eighths: Option<u64>,
    rtt_variation_eighths: u64,
    retransmission_timeout_ms: u64,
}

impl RttEstimator {
    pub(crate) const fn new() -> Self {
        Self {
            smoothed_rtt_eighths: None,
            rtt_variation_eighths: 0,
            retransmission_timeout_ms: INITIAL_RETRANSMISSION_TIMEOUT_MS,
        }
    }

    pub(crate) const fn smoothed_rtt_ms(&self) -> Option<u64> {
        match self.smoothed_rtt_eighths {
            Some(value) => Some(value.div_ceil(8)),
            None => None,
        }
    }

    pub(crate) const fn retransmission_timeout_ms(&self) -> u64 {
        self.retransmission_timeout_ms
    }

    pub(crate) fn frame_interval_ms(&self) -> u64 {
        self.smoothed_rtt_ms()
            .map_or(MAX_FRAME_INTERVAL_MS, |smoothed| smoothed.div_ceil(2))
            .clamp(MIN_FRAME_INTERVAL_MS, MAX_FRAME_INTERVAL_MS)
    }

    pub(crate) fn observe_sample(&mut self, sample_ms: u64) -> Result<(), TimingError> {
        if sample_ms > MAX_RTT_SAMPLE_MS {
            return Err(TimingError::InvalidRttSample);
        }

        let sample_eighths = sample_ms
            .checked_mul(8)
            .ok_or(TimingError::TimerExhausted)?;
        match self.smoothed_rtt_eighths {
            None => {
                self.smoothed_rtt_eighths = Some(sample_eighths);
                self.rtt_variation_eighths = sample_ms
                    .checked_mul(4)
                    .ok_or(TimingError::TimerExhausted)?;
            }
            Some(smoothed) => {
                let deviation = smoothed.abs_diff(sample_eighths);
                self.rtt_variation_eighths = self
                    .rtt_variation_eighths
                    .checked_mul(3)
                    .and_then(|weighted| weighted.checked_add(deviation))
                    .ok_or(TimingError::TimerExhausted)?
                    / 4;
                self.smoothed_rtt_eighths = Some(
                    smoothed
                        .checked_mul(7)
                        .and_then(|weighted| weighted.checked_add(sample_eighths))
                        .ok_or(TimingError::TimerExhausted)?
                        / 8,
                );
            }
        }

        self.retransmission_timeout_ms = self.calculate_rto_ms()?;
        Ok(())
    }

    fn calculate_rto_ms(&self) -> Result<u64, TimingError> {
        let smoothed = self
            .smoothed_rtt_eighths
            .expect("RTT calculation requires an observed sample");
        let variation = self
            .rtt_variation_eighths
            .checked_mul(4)
            .ok_or(TimingError::TimerExhausted)?;
        let granularity = CLOCK_GRANULARITY_MS * 8;
        let timeout_eighths = smoothed
            .checked_add(variation.max(granularity))
            .ok_or(TimingError::TimerExhausted)?;
        Ok(timeout_eighths
            .div_ceil(8)
            .clamp(MIN_RETRANSMISSION_TIMEOUT_MS, MAX_RETRANSMISSION_TIMEOUT_MS))
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct OutgoingTimestamps {
    pub(crate) timestamp: u16,
    pub(crate) timestamp_reply: Option<u16>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RttSampleDisposition {
    Updated { sample_ms: u64 },
    NoReply,
    Reordered,
    Implausible,
}

/// Converts authenticated wire timestamps into bounded RTT observations.
///
/// The 16-bit wire clock wraps naturally. A reply value is accepted only from
/// a packet whose sequence is newer than every packet previously observed in
/// this direction, matching SSP's rule to ignore out-of-sequence RTT samples.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct DatagramTiming {
    estimator: RttEstimator,
    highest_received_sequence: Option<u64>,
    last_received_timestamp: Option<(u16, u64)>,
    last_observed_ms: u64,
}

impl DatagramTiming {
    pub(crate) const fn new(started_at_ms: u64) -> Self {
        Self {
            estimator: RttEstimator::new(),
            highest_received_sequence: None,
            last_received_timestamp: None,
            last_observed_ms: started_at_ms,
        }
    }

    pub(crate) const fn estimator(&self) -> &RttEstimator {
        &self.estimator
    }

    pub(crate) fn outgoing(&mut self, now_ms: u64) -> Result<OutgoingTimestamps, TimingError> {
        self.observe_time(now_ms)?;
        let timestamp_reply =
            self.last_received_timestamp
                .and_then(|(timestamp, received_at_ms)| {
                    let age_ms = now_ms.checked_sub(received_at_ms)?;
                    if age_ms > MAX_TIMESTAMP_REPLY_AGE_MS {
                        return None;
                    }
                    let age = u16::try_from(age_ms).ok()?;
                    let reply = timestamp.wrapping_add(age);
                    (reply != NO_TIMESTAMP_REPLY).then_some(reply)
                });
        Ok(OutgoingTimestamps {
            timestamp: wire_milliseconds(now_ms),
            timestamp_reply,
        })
    }

    pub(crate) fn observe_received(
        &mut self,
        sequence: u64,
        timestamp: u16,
        timestamp_reply: Option<u16>,
        now_ms: u64,
    ) -> Result<RttSampleDisposition, TimingError> {
        self.observe_time(now_ms)?;
        if self
            .highest_received_sequence
            .is_some_and(|highest| sequence <= highest)
        {
            return Ok(RttSampleDisposition::Reordered);
        }
        self.highest_received_sequence = Some(sequence);
        self.last_received_timestamp = Some((timestamp, now_ms));

        let Some(reply) = timestamp_reply else {
            return Ok(RttSampleDisposition::NoReply);
        };
        let sample_ms = u64::from(wire_milliseconds(now_ms).wrapping_sub(reply));
        if sample_ms > MAX_RTT_SAMPLE_MS {
            return Ok(RttSampleDisposition::Implausible);
        }
        self.estimator.observe_sample(sample_ms)?;
        Ok(RttSampleDisposition::Updated { sample_ms })
    }

    fn observe_time(&mut self, now_ms: u64) -> Result<(), TimingError> {
        if now_ms < self.last_observed_ms {
            return Err(TimingError::TimeMovedBackwards);
        }
        self.last_observed_ms = now_ms;
        Ok(())
    }
}

fn wire_milliseconds(now_ms: u64) -> u16 {
    u16::try_from(now_ms % TIMESTAMP_MODULUS).expect("timestamp remainder always fits in u16")
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct SendReasons(u8);

impl SendReasons {
    pub(crate) const STATE_CHANGE: Self = Self(1 << 0);
    pub(crate) const RETRANSMISSION: Self = Self(1 << 1);
    pub(crate) const ACKNOWLEDGEMENT: Self = Self(1 << 2);
    pub(crate) const HEARTBEAT: Self = Self(1 << 3);
    const ALL: Self = Self(
        Self::STATE_CHANGE.0 | Self::RETRANSMISSION.0 | Self::ACKNOWLEDGEMENT.0 | Self::HEARTBEAT.0,
    );

    pub(crate) const fn contains(self, reason: Self) -> bool {
        self.0 & reason.0 != 0
    }

    fn insert(&mut self, reason: Self) {
        self.0 |= reason.0;
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct WakePlan {
    generation: u64,
    planned_at_ms: u64,
    pub(crate) reasons: SendReasons,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SchedulerPoll {
    Pending { wake_at_ms: u64 },
    Send(WakePlan),
}

/// Pure monotonic-time scheduler for SSP transport sends.
///
/// It reports why one instruction is due but never runs a clock or performs
/// I/O. Any successful instruction covers the newest local state and current
/// acknowledgement, so committing a send clears both pending signals even
/// when a different timer caused the wakeup.
#[derive(Debug)]
pub(crate) struct SendScheduler {
    started_at_ms: u64,
    last_observed_ms: u64,
    last_send_ms: Option<u64>,
    local_change_at_ms: Option<u64>,
    acknowledgement_at_ms: Option<u64>,
    send_not_before_ms: Option<u64>,
    generation: u64,
}

impl SendScheduler {
    pub(crate) const fn new(started_at_ms: u64) -> Self {
        Self {
            started_at_ms,
            last_observed_ms: started_at_ms,
            last_send_ms: None,
            local_change_at_ms: None,
            acknowledgement_at_ms: None,
            send_not_before_ms: None,
            generation: 0,
        }
    }

    pub(crate) fn note_local_change(&mut self, now_ms: u64) -> Result<(), TimingError> {
        self.observe_time(now_ms)?;
        if self.local_change_at_ms.is_none() {
            let next_generation = self.next_generation()?;
            self.local_change_at_ms = Some(now_ms);
            self.generation = next_generation;
        }
        Ok(())
    }

    pub(crate) fn note_acknowledgement_needed(&mut self, now_ms: u64) -> Result<(), TimingError> {
        self.observe_time(now_ms)?;
        if self.acknowledgement_at_ms.is_none() {
            let next_generation = self.next_generation()?;
            self.acknowledgement_at_ms = Some(now_ms);
            self.generation = next_generation;
        }
        Ok(())
    }

    pub(crate) fn poll(
        &mut self,
        now_ms: u64,
        timing: &RttEstimator,
        latest_unacknowledged_sent_at_ms: Option<u64>,
    ) -> Result<SchedulerPoll, TimingError> {
        self.observe_time(now_ms)?;
        if latest_unacknowledged_sent_at_ms.is_some_and(|sent_at| sent_at > now_ms) {
            return Err(TimingError::TimeMovedBackwards);
        }

        let state_due = self.local_change_due(timing.frame_interval_ms())?;
        let acknowledgement_due = self
            .acknowledgement_at_ms
            .map(|received_at| checked_deadline(received_at, MAX_ACKNOWLEDGEMENT_DELAY_MS))
            .transpose()?;
        let retransmission_due = latest_unacknowledged_sent_at_ms
            .map(|sent_at| checked_deadline(sent_at, timing.retransmission_timeout_ms()))
            .transpose()?;
        let heartbeat_due = checked_deadline(
            self.last_send_ms.unwrap_or(self.started_at_ms),
            HEARTBEAT_INTERVAL_MS,
        )?;

        let mut reasons = SendReasons::default();
        if state_due.is_some_and(|deadline| deadline <= now_ms) {
            reasons.insert(SendReasons::STATE_CHANGE);
        }
        if retransmission_due.is_some_and(|deadline| deadline <= now_ms) {
            reasons.insert(SendReasons::RETRANSMISSION);
        }
        if acknowledgement_due.is_some_and(|deadline| deadline <= now_ms) {
            reasons.insert(SendReasons::ACKNOWLEDGEMENT);
        }
        if heartbeat_due <= now_ms {
            reasons.insert(SendReasons::HEARTBEAT);
        }
        if reasons != SendReasons::default() {
            match self.send_not_before_ms {
                Some(not_before) if now_ms < not_before => {
                    return Ok(SchedulerPoll::Pending {
                        wake_at_ms: not_before,
                    });
                }
                Some(_) | None => {}
            }
            return Ok(SchedulerPoll::Send(WakePlan {
                generation: self.generation,
                planned_at_ms: now_ms,
                reasons,
            }));
        }

        let wake_at_ms = [
            state_due,
            acknowledgement_due,
            retransmission_due,
            Some(heartbeat_due),
        ]
        .into_iter()
        .flatten()
        .min()
        .expect("heartbeat always supplies a scheduler deadline");
        Ok(SchedulerPoll::Pending { wake_at_ms })
    }

    pub(crate) fn commit_send(&mut self, plan: WakePlan) -> Result<(), TimingError> {
        if plan.generation != self.generation || plan.planned_at_ms != self.last_observed_ms {
            return Err(TimingError::StalePlan);
        }
        let next_generation = self.next_generation()?;
        self.local_change_at_ms = None;
        self.acknowledgement_at_ms = None;
        self.send_not_before_ms = None;
        self.last_send_ms = Some(plan.planned_at_ms);
        self.generation = next_generation;
        Ok(())
    }

    pub(crate) fn defer_send(&mut self, now_ms: u64, delay_ms: u64) -> Result<(), TimingError> {
        let not_before = checked_deadline(now_ms, delay_ms)?;
        self.observe_time(now_ms)?;
        self.send_not_before_ms = Some(not_before);
        Ok(())
    }

    fn local_change_due(&self, frame_interval_ms: u64) -> Result<Option<u64>, TimingError> {
        self.local_change_at_ms
            .map(|changed_at| {
                let collection_due = checked_deadline(changed_at, STATE_CHANGE_COLLECTION_MS)?;
                let frame_due = self
                    .last_send_ms
                    .map(|sent_at| checked_deadline(sent_at, frame_interval_ms))
                    .transpose()?
                    .unwrap_or(collection_due);
                Ok(collection_due.max(frame_due))
            })
            .transpose()
    }

    fn observe_time(&mut self, now_ms: u64) -> Result<(), TimingError> {
        if now_ms < self.last_observed_ms {
            return Err(TimingError::TimeMovedBackwards);
        }
        self.last_observed_ms = now_ms;
        Ok(())
    }

    fn next_generation(&self) -> Result<u64, TimingError> {
        self.generation
            .checked_add(1)
            .ok_or(TimingError::GenerationExhausted)
    }
}

fn checked_deadline(start_ms: u64, delay_ms: u64) -> Result<u64, TimingError> {
    start_ms
        .checked_add(delay_ms)
        .ok_or(TimingError::TimerExhausted)
}

#[cfg(test)]
#[path = "../tests/support/network.rs"]
mod virtual_network;

#[cfg(test)]
mod tests;
