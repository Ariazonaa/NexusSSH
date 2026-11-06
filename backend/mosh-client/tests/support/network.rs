use std::net::SocketAddrV4;

pub(crate) const MAX_DATAGRAM_BYTES: usize = 2_048;
pub(crate) const MAX_QUEUED_DATAGRAMS: usize = 256;

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct SimTime(u64);

impl SimTime {
    pub(crate) const fn from_millis(milliseconds: u64) -> Self {
        Self(milliseconds)
    }

    pub(crate) const fn as_millis(self) -> u64 {
        self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Impairment {
    Drop,
    Deliver {
        delay_ms: u64,
    },
    Duplicate {
        first_delay_ms: u64,
        second_delay_ms: u64,
    },
    Corrupt {
        delay_ms: u64,
        byte_index: usize,
        xor_mask: u8,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct Datagram {
    pub(crate) source: SocketAddrV4,
    pub(crate) destination: SocketAddrV4,
    pub(crate) bytes: Vec<u8>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LinkError {
    Cancelled,
    DatagramTooLarge,
    QueueFull,
    TimeMovedBackwards,
    TimeOverflow,
    EventCounterExhausted,
    InvalidCorruptionIndex,
    EmptyCorruptionMask,
}

#[derive(Debug)]
struct ScheduledDatagram {
    delivery_time: SimTime,
    ordinal: u64,
    datagram: Datagram,
}

#[derive(Debug)]
pub(crate) struct VirtualLink {
    now: SimTime,
    next_ordinal: u64,
    queued: Vec<ScheduledDatagram>,
    cancelled: bool,
}

impl VirtualLink {
    pub(crate) const fn new() -> Self {
        Self {
            now: SimTime::from_millis(0),
            next_ordinal: 0,
            queued: Vec::new(),
            cancelled: false,
        }
    }

    pub(crate) const fn now(&self) -> SimTime {
        self.now
    }

    pub(crate) fn queued_datagrams(&self) -> usize {
        self.queued.len()
    }

    pub(crate) const fn is_cancelled(&self) -> bool {
        self.cancelled
    }

    pub(crate) fn send(
        &mut self,
        source: SocketAddrV4,
        destination: SocketAddrV4,
        bytes: &[u8],
        impairment: Impairment,
    ) -> Result<(), LinkError> {
        if self.cancelled {
            return Err(LinkError::Cancelled);
        }
        if bytes.len() > MAX_DATAGRAM_BYTES {
            return Err(LinkError::DatagramTooLarge);
        }

        let delayed_payloads = match impairment {
            Impairment::Drop => Vec::new(),
            Impairment::Deliver { delay_ms } => vec![(delay_ms, bytes.to_vec())],
            Impairment::Duplicate {
                first_delay_ms,
                second_delay_ms,
            } => vec![
                (first_delay_ms, bytes.to_vec()),
                (second_delay_ms, bytes.to_vec()),
            ],
            Impairment::Corrupt {
                delay_ms,
                byte_index,
                xor_mask,
            } => {
                if byte_index >= bytes.len() {
                    return Err(LinkError::InvalidCorruptionIndex);
                }
                if xor_mask == 0 {
                    return Err(LinkError::EmptyCorruptionMask);
                }
                let mut corrupted = bytes.to_vec();
                corrupted[byte_index] ^= xor_mask;
                vec![(delay_ms, corrupted)]
            }
        };

        let queued_after_send = self
            .queued
            .len()
            .checked_add(delayed_payloads.len())
            .ok_or(LinkError::QueueFull)?;
        if queued_after_send > MAX_QUEUED_DATAGRAMS {
            return Err(LinkError::QueueFull);
        }

        let ordinal_after_send = self
            .next_ordinal
            .checked_add(
                u64::try_from(delayed_payloads.len())
                    .map_err(|_| LinkError::EventCounterExhausted)?,
            )
            .ok_or(LinkError::EventCounterExhausted)?;

        let mut scheduled = Vec::with_capacity(delayed_payloads.len());
        for (offset, (delay_ms, payload)) in delayed_payloads.into_iter().enumerate() {
            let delivery_millis = self
                .now
                .as_millis()
                .checked_add(delay_ms)
                .ok_or(LinkError::TimeOverflow)?;
            let ordinal = self
                .next_ordinal
                .checked_add(u64::try_from(offset).map_err(|_| LinkError::EventCounterExhausted)?)
                .ok_or(LinkError::EventCounterExhausted)?;
            scheduled.push(ScheduledDatagram {
                delivery_time: SimTime::from_millis(delivery_millis),
                ordinal,
                datagram: Datagram {
                    source,
                    destination,
                    bytes: payload,
                },
            });
        }

        self.next_ordinal = ordinal_after_send;
        self.queued.extend(scheduled);
        Ok(())
    }

    pub(crate) fn advance_to(&mut self, time: SimTime) -> Result<Vec<Datagram>, LinkError> {
        if self.cancelled {
            return Err(LinkError::Cancelled);
        }
        if time < self.now {
            return Err(LinkError::TimeMovedBackwards);
        }
        self.now = time;

        let mut due = Vec::new();
        let mut pending = Vec::with_capacity(self.queued.len());
        for scheduled in self.queued.drain(..) {
            if scheduled.delivery_time <= self.now {
                due.push(scheduled);
            } else {
                pending.push(scheduled);
            }
        }
        self.queued = pending;
        due.sort_unstable_by_key(|scheduled| (scheduled.delivery_time, scheduled.ordinal));
        Ok(due
            .into_iter()
            .map(|scheduled| scheduled.datagram)
            .collect())
    }

    pub(crate) fn cancel(&mut self) -> usize {
        self.cancelled = true;
        let cleared = self.queued.len();
        self.queued.clear();
        cleared
    }
}
