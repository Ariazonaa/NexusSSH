use super::{ReachabilityError, ReachabilityTracker, SessionInterruption, SessionReachability};
use crate::limits::{INITIAL_ATTACHMENT_TIMEOUT_MS, NO_RECENT_CONTACT_MS, NO_RECENT_REPLY_MS};

#[test]
fn awaiting_peer_has_one_bounded_attachment_deadline() {
    let mut tracker = ReachabilityTracker::new();

    assert_eq!(tracker.current(), SessionReachability::AwaitingPeer);
    assert_eq!(
        tracker.next_deadline_ms(),
        Ok(Some(INITIAL_ATTACHMENT_TIMEOUT_MS))
    );
    assert!(!tracker.attachment_timed_out(INITIAL_ATTACHMENT_TIMEOUT_MS - 1));
    assert!(tracker.attachment_timed_out(INITIAL_ATTACHMENT_TIMEOUT_MS));
    assert_eq!(tracker.update(u64::MAX), Ok(None));
}

#[test]
fn accepted_contact_becomes_responsive_then_interrupted_at_boundary() {
    let mut tracker = ReachabilityTracker::new();
    tracker.note_contact(1_000);
    tracker.note_reply(1_000);

    assert_eq!(
        tracker.update(1_000),
        Ok(Some(SessionReachability::Responsive))
    );
    assert_eq!(tracker.update(1_000 + NO_RECENT_CONTACT_MS - 1), Ok(None));
    assert_eq!(
        tracker.update(1_000 + NO_RECENT_CONTACT_MS),
        Ok(Some(SessionReachability::Interrupted {
            reason: SessionInterruption::NoRecentContact,
        }))
    );
    assert_eq!(tracker.next_deadline_ms(), Ok(None));
}

#[test]
fn fresh_contact_exposes_stale_reply_without_hiding_contact_loss() {
    let mut tracker = ReachabilityTracker::new();
    tracker.note_contact(0);
    tracker.note_reply(0);
    assert_eq!(tracker.update(0), Ok(Some(SessionReachability::Responsive)));

    tracker.note_contact(NO_RECENT_REPLY_MS - 1);
    assert_eq!(tracker.update(NO_RECENT_REPLY_MS - 1), Ok(None));
    tracker.note_contact(NO_RECENT_REPLY_MS);
    assert_eq!(
        tracker.update(NO_RECENT_REPLY_MS),
        Ok(Some(SessionReachability::Interrupted {
            reason: SessionInterruption::NoRecentReply,
        }))
    );

    assert_eq!(
        tracker.update(NO_RECENT_REPLY_MS + NO_RECENT_CONTACT_MS),
        Ok(Some(SessionReachability::Interrupted {
            reason: SessionInterruption::NoRecentContact,
        }))
    );
}

#[test]
fn contact_and_reply_progress_restore_responsive_state() {
    let mut tracker = ReachabilityTracker::new();
    tracker.note_contact(0);
    tracker.note_reply(0);
    assert_eq!(
        tracker.update(NO_RECENT_CONTACT_MS),
        Ok(Some(SessionReachability::Interrupted {
            reason: SessionInterruption::NoRecentContact,
        }))
    );

    tracker.note_contact(NO_RECENT_CONTACT_MS);
    assert_eq!(
        tracker.update(NO_RECENT_CONTACT_MS),
        Ok(Some(SessionReachability::Responsive))
    );
    tracker.note_contact(NO_RECENT_REPLY_MS);
    assert_eq!(
        tracker.update(NO_RECENT_REPLY_MS),
        Ok(Some(SessionReachability::Interrupted {
            reason: SessionInterruption::NoRecentReply,
        }))
    );
    tracker.note_reply(NO_RECENT_REPLY_MS);
    assert_eq!(
        tracker.update(NO_RECENT_REPLY_MS),
        Ok(Some(SessionReachability::Responsive))
    );
}

#[test]
fn backwards_time_and_deadline_overflow_are_explicit() {
    let mut tracker = ReachabilityTracker::new();
    tracker.note_contact(2);
    tracker.note_reply(2);
    assert_eq!(
        tracker.update(1),
        Err(ReachabilityError::TimeMovedBackwards)
    );

    tracker.note_contact(u64::MAX);
    tracker.note_reply(u64::MAX);
    assert_eq!(
        tracker.update(u64::MAX),
        Ok(Some(SessionReachability::Responsive))
    );
    assert_eq!(
        tracker.next_deadline_ms(),
        Err(ReachabilityError::TimerExhausted)
    );
}
