# 0007: Public Session API

- Status: Accepted
- Date: 2026-08-30

## Context

Phase 3 needs a small library contract that another Rust project can drive on
its own executor. The contract must expose lifecycle, ordered input, resize,
full repaint, bounded VT output, cancellation, and failures without exposing
SSP internals or binding the crate to LeanTTY.

Lifecycle state and terminal output have different delivery semantics. A
caller needs the latest lifecycle state, but every accepted VT output chunk
must remain ordered and subject to backpressure. Putting both into one bounded
event queue would let slow painting delay cancellation and close observation.

## Decision

The crate exports `Session`, `SessionTask`, `SessionState`, `SessionExit`,
`SessionError`, and `SessionCommandError`. ADR 0009 later adds an independent
latest-value reachability observer without changing this lifecycle state.

`Session::connect` consumes a validated `Bootstrap` and returns a `Session`
handle plus a `SessionTask`. The caller must poll `SessionTask::run` on its own
executor. The library creates no runtime or operating-system thread.

`Session` provides ordered input, resize, full-repaint requests, bounded VT
output, current lifecycle state, lifecycle-change waiting, and idempotent
cancellation. It validates command size and terminal dimensions before
queueing them.

Lifecycle transitions are monotonic:

```text
Connecting -> Active -> Closed
           \----------> Closed
```

`Active` means that at least one authenticated remote terminal state was
accepted. It does not promise network reachability at the instant it is read.
`Closed` means the task stopped or was dropped.

Lifecycle state uses a latest-value channel. VT output keeps its one-chunk
bounded, ordered queue. These streams have no shared revision or cross-stream
ordering contract. A caller applies output chunks in receive order and uses an
explicit full repaint when replacing its terminal surface.

Cancellation has a dedicated Session-owned signal outside the bounded command
queue. It is synchronous, idempotent, and checked before further protocol
work and between fragments of a multi-datagram instruction. Dropping the
Session handle or output consumer stops the task with
`SessionExit::OwnerDropped`; explicit cancellation returns
`SessionExit::Cancelled`.

Public task errors use stable categories and never contain bootstrap text,
session keys, datagram contents, or private parser types. Detailed internal
errors remain private so implementation changes do not expand the public API.

## Consequences

- Other Rust projects can embed the client without depending on private
  protocol or terminal modules.
- Slow terminal painting cannot queue lifecycle states or cancellation behind
  VT bytes.
- A caller may observe `Closed` before draining an already accepted output
  chunk; this is intentional and does not create a display acknowledgement.
- The caller owns task spawning, output consumption, and the policy for
  presenting public error categories.
- The API does not expose SSH bootstrap, a CLI, a generic transport, terminal
  cells, or LeanTTY-specific types. ADR 0010 later adds one standard
  per-Session prediction-mode input without changing lifecycle or output.
- Additive convenience methods remain possible, but Phase 3 isolation and fuzz
  work must use this contract before it is considered stable for publishing.

## Rejected alternatives

### One event enum for state and output

This gives lifecycle events the backpressure policy of terminal painting and
can delay shutdown behind a full output queue.

### Cancellation as a normal command

A full command queue could delay cancellation behind input. Cancellation is a
lifecycle boundary, not ordered terminal input.

### A hidden runtime or spawned task

This would obscure ownership, complicate shutdown, and duplicate an embedding
application's executor.

### Public protocol and terminal error enums

This would freeze private implementation choices and expose more detail than
an embedding application can safely act on.
