# 0008: Authenticated graceful close

- Status: Accepted
- Date: 2026-08-30

## Context

LeanTTY integration reproduced two lifecycle defects. Dropping or cancelling a
client stopped the local task but left an unmodified stock `mosh-server` 1.4.0
running, while a clean remote shell exit never completed the public Session.
Network silence cannot distinguish a recoverable outage from a dead peer, so it
is not an acceptable replacement for an authenticated close signal.

Project-controlled black-box fixtures established the stock close exchange.
The initiator sends an otherwise ordinary transport difference with the
reserved target state `u64::MAX`. The receiver acknowledges that value in a
new ordinary local state. A stock client retransmits while waiting for that
acknowledgement and stops after an approximately four-second bounded window.

## Decision

Add `Session::close()` as an idempotent graceful-close request. Keep
`Session::cancel()` as the prompt hard-stop path. `SessionTask::run` reports
`SessionExit::LocalClosed` after a locally requested exchange is acknowledged
or its four-second acknowledgement window expires, and
`SessionExit::RemoteClosed` after an authenticated peer close is acknowledged.

The reserved close target is handled by a private Session shutdown phase. It
does not enter ordinary SSP state numbering, reference retention, terminal
snapshot keys, or the public API. A close instruction still carries the
bounded difference from the peer's known ordinary base to the current ordinary
state. A peer close therefore applies its final difference before completion.

Commands accepted before `close()` remain ordered before the close difference.
After `close()` linearizes, new input, resize, and repaint requests fail with
`SessionCommandError::Closed`. Repeated close requests are no-ops. Hard
cancellation, owner drop, authentication failure, resource limits, and state
exhaustion retain their existing priority over graceful completion.

The receiver sends the close acknowledgement before waiting for the latest
authenticated display state to enter the existing bounded output queue. An
application that keeps consuming output can drain the final chunk after task
completion. Cancellation still preempts a blocked output reservation.

## Consequences

- Embedders can close the stock server without mapping application teardown to
  a hard local abort.
- Clean remote logout has a stable public completion reason.
- Packet loss during local close causes bounded retransmission, not indefinite
  shutdown; timeout means local completion and does not claim proof that the
  peer received the request.
- Silence during an ordinary active Session remains recoverable and never
  becomes `RemoteClosed`.
- ADR 0009 adds reachability after physical platform evidence. Temporary local
  UDP-send recovery remains deferred until platform evidence defines it.

## Rejected alternatives

### Replace `cancel()` with graceful close

Cancellation must remain prompt during output backpressure, malformed traffic,
executor teardown, and application destruction. A bounded wire exchange cannot
provide that guarantee.

### Treat silence or a local socket error as remote close

Neither condition is authenticated, and both can occur during a recoverable
network transition. Conflating them with logout would weaken Mosh's recovery
contract.

### Put `u64::MAX` into ordinary synchronization history

The reserved state terminates the session and has different acknowledgement
semantics. Retaining it as a normal state would contaminate monotonic
allocation, eviction, retransmission, and terminal snapshot invariants.
