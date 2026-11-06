# 0002: Runtime and terminal output ownership

- Status: Accepted
- Date: 2026-08-29

## Context

The client needs a runtime model that supports deterministic protocol tests,
one or two concurrent LeanTTY Panes, cancellation, HarmonyOS lifecycle events,
and independent library consumers. It also needs a presentation contract that
works with LeanTTY's xterm.js surface without binding the crate to a WebView or
blocking future native renderers.

Two questions must remain separate:

1. how the library represents terminal output; and
2. how an embedding application schedules and acknowledges that output.

## Decision

The library maintains the authoritative terminal state internally and emits
synthesized UTF-8 VT paint bytes as bounded, ordered output chunks. Consumers
write those chunks in order. An explicit repaint request emits a self-contained
full repaint from the latest terminal state.

The initial contract has no display revision, surface generation, or rendering
acknowledgement protocol. The Session does not need to know when a terminal
emulator finishes painting. A consumer that abandons or replaces its surface
requests a full repaint and keeps consuming the bounded stream in order. The
full repaint reestablishes the new surface from current state.

The initial public API does not expose terminal cells, a renderer trait, or a
generic output plugin. A read-only screen snapshot remains a possible additive
API after a real native-renderer consumer demonstrates the need.

Public output reconstructs the stock server's current visible screen. It is not
a raw remote PTY stream and does not preserve a remote application's
alternate-screen entry or exit boundary. The Session does not infer that
boundary from visible contents or expose an unsupported mode value.

The embedding application owns terminal initialization and restoration around
the whole Session. A consumer that must protect its pre-Mosh terminal state can
activate a temporary terminal page before it writes the first output chunk and
restore the previous page after Session output and shutdown are complete.

The library owns one asynchronous Session future, UDP socket, timer set,
protocol state, terminal state, and cancellation path per Session. The
embedding application runs that future on its executor. The library does not
create a hidden global runtime or a dedicated operating-system thread per
Session.

The deterministic core accepts explicit time and input events behind the
production driver. This is an internal verification boundary, not a public
generic transport interface.

LeanTTY runs each Mosh Session future on its existing Rust executor. One Pane
owns one Session handle and one Terminal Surface. ArkTS sends commands and
receives events; it does not drive protocol timers. Surface detach leaves the
Session running, and surface attach requests a full repaint from current Rust
state.

## Why

VT bytes match xterm.js and the common embedding contract exposed by current
Mosh libraries. Ordered bounded chunks preserve that contract without adding a
second delivery protocol between the Session and its consumer.

A caller-provided executor avoids a second runtime inside LeanTTY. A
Session-owned future keeps UDP, timers, cancellation, and cleanup inside the
crate rather than spreading protocol correctness across ArkTS or another UI
layer.

The internal deterministic core allows loss, duplication, reordering, delay,
roaming, cancellation, and time jumps to be tested without a real network or
remote service.

## Consequences

- Rust terminal state and the embedding terminal emulator both hold state, but
  Rust owns Mosh truth and the emulator owns presentation.
- A consumer receives the familiar ordered VT byte contract.
- LeanTTY requests a full repaint after WebView replacement; it does not
  acknowledge every paint operation back to the Session.
- A repaint request remains ordered behind output already accepted by the
  queue. Consumers keep applying chunks in order rather than assuming the next
  chunk is the full repaint.
- Slow rendering never makes ArkTS timers or WebView scheduling part of SSP.
- Output must be generated from authenticated terminal state. The painter may
  not pass remote or network bytes through as display bytes.
- The painter needs compatibility tests against independent terminal emulators.
- Complete scrollback remains outside the Mosh state contract.
- Application-level alternate-screen transitions are not part of public
  output. Whole-Session display isolation belongs to the consumer.
- Native cell renderers must initially parse VT output or wait for a justified
  additive snapshot API.

## Rejected alternatives

### Public cell grid or cell patches

This would freeze Unicode, width, color, cursor, hyperlink, and prediction
types before interoperability is proved. It would also force LeanTTY to bypass
xterm.js or add a cell-to-VT adapter.

### Custom structured paint operations

This would create a private terminal rendering protocol. LeanTTY would still
need to convert it to VT bytes, and every new terminal feature would expand the
cross-language contract.

### Display revisions and rendering acknowledgements

They can describe exactly which screen a surface has painted, but they create a
second state machine across Rust, N-API, ArkTS, and the terminal surface. The
core path needs only ordered output and an explicit full repaint after a surface
is replaced. Reconsider richer delivery metadata only if a measured integration
failure cannot be handled by that smaller contract.

### One operating-system thread per Session

This duplicates LeanTTY's existing asynchronous runtime, consumes more memory,
and complicates shutdown and Pane lifecycle handling.

### Library-created global runtime

This hides process-wide state, complicates tests and unloading, and creates a
second runtime inside LeanTTY.

### UI-driven protocol polling

ArkTS or JavaScript timer jitter would become part of retransmission and
heartbeat behavior. It would also enlarge the N-API surface and divide
cancellation ownership.
