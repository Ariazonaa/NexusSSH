# 0011: Recover selected local UDP send errors in established Sessions

- Status: Accepted
- Date: 2026-09-02

ADR 0012 later extends established-Session recovery to the physically observed
`PermissionDenied` category and to receive-side failures. This decision still
governs failed-send commit and fragmentation behavior.

## Context

LeanTTY reproduced MCRS-003 on a physical ARM64 HarmonyOS PC. After a stock
`mosh-server` 1.4.0 Session became active, disabling the real WLAN interface
made the local UDP path return an I/O error after about 7.4 seconds. The client
task ended even though LeanTTY, the stock server, and the remote PTY remained
alive. This converted a temporary interface outage into permanent Session loss.

The driver currently propagates every `send_to` error as `SessionError::Io`.
Earlier design work deliberately deferred a recovery policy until a controlled
platform test distinguished an interface transition from ordinary packet loss.
The physical result now crosses that evidence gate.

## Decision

After a Session is active, treat only these stable `std::io::ErrorKind` values
from `send_to` as temporary local path failures:

- `NetworkDown`;
- `NetworkUnreachable`;
- `HostUnreachable`; and
- `AddrNotAvailable`.

These categories describe loss of the selected local interface, route, host
route, or usable local address. Do not classify `Other`, `Uncategorized`,
permission, descriptor, buffer, message-size, or incomplete-send failures as
recoverable. Socket creation and errors before the first authenticated remote
state retain their existing failure behavior.

A temporary failure leaves the SSP send plan and scheduler wake plan
uncommitted. A partial fragmented attempt is abandoned; a later attempt
regenerates the same logical state difference under a fresh fragment identifier
and fresh packet sequence numbers. The retry deadline is owned by the existing
send scheduler and uses the current retransmission timeout with a one-second
floor. There is no retry count or parallel cancellation state.

The Session keeps the same socket, key, synchronization state, terminal state,
and command queues. Existing reachability timing reports
`Interrupted(NoRecentContact | NoRecentReply)` and returns to `Responsive` on
authenticated progress; no local-network or restoring state is added to the
public API.

Hard cancellation remains prompt. A local graceful close keeps its existing
four-second completion bound even when temporary sends fail. All errors outside
the allowlist remain `SessionError::Io`.

## Consequences

- A real interface transition no longer destroys an established Session merely
  because one UDP send cannot be routed.
- Retry rate, retained state, and memory remain bounded while outage duration
  remains caller-controlled, as required by the Mosh recovery model.
- Embedders need no platform callback, replacement socket, reconnect loop, or
  new public state.
- If a platform reports a temporary transition only as a broad error category,
  that category remains fatal until separate evidence justifies a narrow private
  OS-code rule.

## Evidence and verification

- [LeanTTY physical evidence](../fixtures/leantty-physical-local-send-recovery.md).
- The public Mosh behavior contract states that a temporary loss of Internet
  connectivity keeps the Session alive and resumes when service returns.
- Rust's public `std::io::ErrorKind` contract defines the selected categories
  as network, host, system-network, or requested-local-address unavailability;
  all selected variants are available on Rust 1.88, the 0.1.0 compiler floor.
- Deterministic tests cover allowlist classification, scheduler pacing,
  uncommitted retry, permanent errors, cancellation, graceful close, and
  fragmented partial-send replacement.
- Existing stock 1.4.0 outage and recovery fixtures remain the interoperability
  oracle. LeanTTY pinned `94f1322` and closed the physical WLAN scenario on
  2026-09-03, as recorded in the updated physical evidence above.

## Rejected alternatives

### Recover every UDP I/O error

This could hide a bad descriptor, invalid socket use, permission failure,
oversized datagram, or persistent implementation defect and could create a
retry loop that never has a valid recovery path.

### Recreate the socket immediately

The observed Session already owns a wildcard-bound UDP socket and Mosh supports
source-address changes. Rebinding adds port and lifecycle complexity without
evidence that the socket becomes unusable after WLAN restoration.

### Reconnect from the embedding application

A new Session cannot preserve the original key, SSP state, terminal authority,
retransmission history, or remote PTY. It would be a new login, not recovery.
