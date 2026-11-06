# 0012: Preserve established Sessions across observed permission failures

- Status: Accepted
- Date: 2026-09-10

## Context

LeanTTY reproduced a second MCRS-003 failure on a physical ARM64 HarmonyOS PC.
About one second after the main window became hidden during a real lid close,
an established Session ended with `ErrorKind::PermissionDenied`. The
application process, stock `mosh-server` 1.4.0, and remote PTY remained alive.
After the lid reopened, input returned to LeanTTY's local prompt.

The retained public error identifies neither `send_to` nor `recv_from` and
contains no raw operating-system error. Treating the result as a proven
`EACCES`, `EPERM`, or permanent application-permission failure would exceed the
evidence. The existing driver recovers four route and interface errors only at
the send boundary; every receive error and every permission error is fatal.

The stock client's
[`stmclient.cc`](https://github.com/mobile-shell/mosh/blob/mosh-1.4.0/src/frontend/stmclient.cc)
and
[`network.cc`](https://github.com/mobile-shell/mosh/blob/mosh-1.4.0/src/network/network.cc)
provide comparison evidence: its running event loop reports network exceptions,
waits briefly, and continues, while send failures do not advance protocol
state. This is an architecture comparison, not a wire rule or source for
project code.

## Decision

After a Session is `Active`, treat these stable `std::io::ErrorKind` values from
either UDP send or receive as recoverable local I/O failures:

- `NetworkDown`;
- `NetworkUnreachable`;
- `HostUnreachable`;
- `AddrNotAvailable`; and
- `PermissionDenied`.

The four network categories retain ADR 0011's meaning. `PermissionDenied` is
admitted only because a physical established Session observed it during a
temporary lifecycle transition. The category does not identify the raw errno
or prove why the platform refused the operation.

A failed send remains uncommitted and retries through the existing scheduler.
A failed receive changes no packet, replay, synchronization, terminal, or
reachability authority. The driver suspends receive polling until the current
retransmission timeout, with the existing one-second floor and five-second
ceiling. Commands, due sends, output, graceful close, owner drop, and hard
cancellation remain independently pollable during that wait.

Keep the same socket, endpoint, key, SSP state, terminal state, and queues. Do
not request a permission, recreate the socket, reconnect, or report successful
recovery. Only later authenticated peer progress returns reachability to
`Responsive`. Repeated permission failure leaves the established Session
`Active` and eventually `Interrupted` until the system permits I/O or the caller
closes or cancels it.

Socket creation and every I/O error before the first authenticated remote state
remain explicit failures. Other error categories remain fatal. The public API
does not change.

## Consequences

- A platform may temporarily deny an existing UDP socket without destroying
  the remote PTY or requiring a new Session.
- A permanent mid-Session policy denial is not bypassed or presented as
  success; the Session remains interrupted and caller-controlled.
- Receive retries cannot form a busy loop even if the socket repeatedly returns
  an immediate error.
- Recovery cadence and lifecycle stay Session-owned and bounded without a new
  retry counter, platform callback, or transport abstraction.

## Evidence and verification

- [LeanTTY lid-close diagnostic](../fixtures/leantty-physical-lid-permission-denied.md).
- [ADR 0011](0011-established-session-local-send-recovery.md) supplies the
  existing send commit and retry boundary.
- Rust's public `ErrorKind` contract supplies only the stable category name.
- The public Mosh contract requires established Sessions to survive temporary
  connectivity loss. Stock client source was inspected only as comparison
  evidence for continuing and pacing its event loop after network exceptions.
- Independent regressions cover send and receive, retry pacing, same-socket
  authenticated recovery, initial failure, other-error failure, cancellation,
  four-second graceful close, and two-Session isolation.

The physical observation does not establish the failing direction, raw errno,
platform policy, or behavior on another device. LeanTTY must pin the resulting
revision and repeat the one named lid-close scenario once before this closes
the consumer defect.

## Rejected alternatives

### Recover `PermissionDenied` only from send

The retained evidence cannot identify the direction. A send-only change could
leave the reproduced receive path unchanged.

### Recover every UDP error

Bad descriptors, invalid socket use, incomplete datagrams, and other permanent
defects must remain visible failures.

### Recreate the socket or Session

The process and remote PTY remained alive. Rebinding or reconnecting adds state
loss and lifecycle complexity without evidence that the socket cannot recover.

### Add raw errno or I/O direction to the public API

The embedding application cannot safely repair this private socket boundary.
Focused internal tests can distinguish directions without expanding the public
contract.
