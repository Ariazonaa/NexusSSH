# 0009: Session reachability and initial attachment timeout

- Status: Accepted
- Date: 2026-08-30

## Context

LeanTTY physical tests reproduced two states that the monotonic public
`SessionState` cannot describe. A six-second bidirectional UDP interruption
left one authenticated Session and its remote PTY alive and recoverable. A
separate stock-server process termination produced the same client-visible
silence but could not recover. Neither case produced an authenticated close.

The published Mosh design keeps established Sessions across intermittent
connectivity, sends an idle heartbeat every three seconds, and warns when the
display may be stale. Network silence cannot distinguish a dead server from a
recoverable path failure. The warning must therefore remain separate from
lifecycle and completion.

The stock client and comparison implementations also distinguish recent server
contact from recent acknowledgement of client state. That distinction is
useful architecture evidence, not a new wire rule. The project can derive both
signals from its existing authenticated state and acknowledgement commits.

## Decision

Keep `SessionState` monotonic and add an independent latest-value
`SessionReachability` contract:

```text
AwaitingPeer
  -> Responsive
  -> Interrupted(NoRecentContact | NoRecentReply)
  -> Responsive
```

`AwaitingPeer` means no complete authenticated remote state has been accepted.
`Responsive` means the Session has recent authenticated remote-state and
client-acknowledgement progress. It does not promise that the next datagram is
reachable or that the newest input has already executed.

`NoRecentContact` begins after 6.5 monotonic seconds without a newly accepted
latest remote state. `NoRecentReply` begins after 10 monotonic seconds without
the peer advancing its acknowledgement to a locally sent state while remote
contact remains recent. `NoRecentContact` takes priority when both conditions
hold. Invalid, unauthenticated, replayed, incomplete, non-latest, or unexpected-
source traffic cannot refresh either signal.

Reachability uses a separate coalescing latest-value channel. It never shares
the ordered VT output queue. `Session` exposes the current value and an
independently owned observer so applications can await status without borrowing
the output consumer.

`Session::reachability` returns the current value.
`Session::subscribe_reachability` creates a `SessionReachabilityWatch` whose
`current` and async `changed` methods do not borrow the Session.

Do not add `Restoring`, `ServerDisappeared`, `PeerDead`, or a silence-derived
completion. A transition from `Interrupted` to `Responsive` is the recovery
signal. An embedding application may present that transition as restored or
briefly restoring. Only authenticated shutdown produces
`SessionExit::RemoteClosed`.

Before the first accepted remote state, 15 seconds of silence ends the
attachment attempt with `SessionError::ConnectionTimeout`. This deadline does
not apply after `SessionState::Active`; an established Session remains
recoverable until an existing explicit completion or failure condition occurs.

The initial thresholds are fixed. A later option requires physical evidence
that the standard values produce a material false warning or delayed warning.

## Consequences

- Embedders can distinguish an active responsive Session from one whose screen
  or input delivery may be stale without inventing a UI timer.
- Lifecycle remains monotonic and backward compatible.
- Slow or detached terminal surfaces cannot delay reachability observation.
- A server process that disappears leaves the Session active and interrupted;
  the user or embedding application still chooses when to close or cancel.
- The attachment timeout bounds a failed initial attempt without weakening
  post-attachment recovery.
- The implementation adds one small timer owner and one latest-value channel;
  it adds no transport abstraction, platform-network dependency, retry count,
  or event history.

## Evidence

- The published Mosh paper and public technical overview define intermittent
  recovery, three-second heartbeats, and stale-display warnings.
- The LeanTTY physical evidence record documents a recovered bidirectional
  interruption and an indistinguishable silent server termination.
- Stock Mosh, MoshCatty, and other implementations informed the state-shape
  comparison only. Project code and tests remain independently written.

## Rejected alternatives

### Add cyclic values to `SessionState`

This would mix permanent lifecycle with temporary network observations and
weaken the accepted `Connecting -> Active -> Closed` contract.

### Expose one `Interrupted` reason

Fresh server states do not prove that client input reaches the server. Keeping
contact and reply reasons prevents an uplink-only failure from appearing
responsive.

### Expose `Restoring`

Recovery is a transition, not a stable protocol fact. Adding a persistent value
would require UI timing or could mislabel an uplink-only failure.

### End an active Session after a silence threshold

This would turn Mosh's recovery window into a false failure and could not
distinguish server termination, routing loss, firewall drops, suspension, or
network migration.
