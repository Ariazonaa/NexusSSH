use tokio::sync::watch;

use crate::limits::{INITIAL_ATTACHMENT_TIMEOUT_MS, NO_RECENT_CONTACT_MS, NO_RECENT_REPLY_MS};

/// The latest observed ability to exchange useful state with the peer.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionReachability {
    /// No complete authenticated remote state has been accepted yet.
    AwaitingPeer,
    /// Recent remote state and acknowledgement progress have both been observed.
    Responsive,
    /// The Session remains active, but useful exchange appears interrupted.
    Interrupted {
        /// The observation that caused the interruption warning.
        reason: SessionInterruption,
    },
}

/// Why an active Session is currently considered interrupted.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionInterruption {
    /// No newly accepted latest remote state arrived within the contact window.
    NoRecentContact,
    /// The peer has not recently advanced its acknowledgement of local state.
    NoRecentReply,
}

/// An independently owned latest-value reachability observer.
pub struct SessionReachabilityWatch {
    receiver: watch::Receiver<SessionReachability>,
}

impl SessionReachabilityWatch {
    pub(crate) fn new(receiver: watch::Receiver<SessionReachability>) -> Self {
        Self { receiver }
    }

    /// Returns the latest reachability observation without waiting.
    #[must_use]
    pub fn current(&self) -> SessionReachability {
        *self.receiver.borrow()
    }

    /// Waits for and returns the next changed reachability value.
    ///
    /// Returns `None` after the Session task and all senders have stopped.
    pub async fn changed(&mut self) -> Option<SessionReachability> {
        self.receiver.changed().await.ok()?;
        Some(*self.receiver.borrow_and_update())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ReachabilityError {
    TimeMovedBackwards,
    TimerExhausted,
}

#[derive(Debug)]
pub(crate) struct ReachabilityTracker {
    current: SessionReachability,
    last_contact_ms: Option<u64>,
    last_reply_ms: u64,
}

impl ReachabilityTracker {
    pub(crate) const fn new() -> Self {
        Self {
            current: SessionReachability::AwaitingPeer,
            last_contact_ms: None,
            last_reply_ms: 0,
        }
    }

    pub(crate) const fn current(&self) -> SessionReachability {
        self.current
    }

    pub(crate) fn note_contact(&mut self, now_ms: u64) {
        self.last_contact_ms = Some(now_ms);
    }

    pub(crate) fn note_reply(&mut self, acknowledged_sent_at_ms: u64) {
        self.last_reply_ms = self.last_reply_ms.max(acknowledged_sent_at_ms);
    }

    pub(crate) fn update(
        &mut self,
        now_ms: u64,
    ) -> Result<Option<SessionReachability>, ReachabilityError> {
        let next = self.calculate(now_ms)?;
        if next == self.current {
            return Ok(None);
        }
        self.current = next;
        Ok(Some(next))
    }

    pub(crate) fn attachment_timed_out(&self, now_ms: u64) -> bool {
        self.last_contact_ms.is_none() && now_ms >= INITIAL_ATTACHMENT_TIMEOUT_MS
    }

    pub(crate) fn next_deadline_ms(&self) -> Result<Option<u64>, ReachabilityError> {
        match self.current {
            SessionReachability::AwaitingPeer => Ok(Some(INITIAL_ATTACHMENT_TIMEOUT_MS)),
            SessionReachability::Responsive => {
                let contact = self.contact_deadline_ms()?;
                let reply = self
                    .last_reply_ms
                    .checked_add(NO_RECENT_REPLY_MS)
                    .ok_or(ReachabilityError::TimerExhausted)?;
                Ok(Some(contact.min(reply)))
            }
            SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentReply,
            } => Ok(Some(self.contact_deadline_ms()?)),
            SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentContact,
            } => Ok(None),
        }
    }

    fn calculate(&self, now_ms: u64) -> Result<SessionReachability, ReachabilityError> {
        let Some(last_contact_ms) = self.last_contact_ms else {
            return Ok(SessionReachability::AwaitingPeer);
        };
        let contact_age = now_ms
            .checked_sub(last_contact_ms)
            .ok_or(ReachabilityError::TimeMovedBackwards)?;
        if contact_age >= NO_RECENT_CONTACT_MS {
            return Ok(SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentContact,
            });
        }
        let reply_age = now_ms
            .checked_sub(self.last_reply_ms)
            .ok_or(ReachabilityError::TimeMovedBackwards)?;
        if reply_age >= NO_RECENT_REPLY_MS {
            return Ok(SessionReachability::Interrupted {
                reason: SessionInterruption::NoRecentReply,
            });
        }
        Ok(SessionReachability::Responsive)
    }

    fn contact_deadline_ms(&self) -> Result<u64, ReachabilityError> {
        self.last_contact_ms
            .ok_or(ReachabilityError::TimeMovedBackwards)?
            .checked_add(NO_RECENT_CONTACT_MS)
            .ok_or(ReachabilityError::TimerExhausted)
    }
}

#[cfg(test)]
mod tests;
