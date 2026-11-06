# Architecture

> Status: accepted design, incrementally implemented
>
> Governing principles: [project principles](project-principles.md)
>
> Related decisions: [0001](decisions/0001-project-scope-and-licensing.md),
> [0002](decisions/0002-runtime-and-terminal-output.md),
> [0003](decisions/0003-independent-wire-and-foundations.md),
> [0004](decisions/0004-rustcrypto-ocb3-security-exception.md),
> [0005](decisions/0005-bounded-local-prediction.md),
> [0006](decisions/0006-phase-2-core-viability-gate.md),
> [0007](decisions/0007-public-session-api.md),
> [0008](decisions/0008-authenticated-graceful-close.md), and
> [0009](decisions/0009-session-reachability.md),
> [0010](decisions/0010-public-prediction-modes.md), and
> [0011](decisions/0011-established-session-local-send-recovery.md), and
> [0012](decisions/0012-established-session-permission-recovery.md)

The [Phase 3 mechanism necessity review](necessity-review.md) records which
implemented mechanisms the stabilized library keeps and which behavior remains
deferred before code-structure work begins. Accepted behavior-preserving
changes are tracked in the [Phase 3 refactoring log](refactoring-log.md).

## Design summary

`mosh-client-rs` is one independent, unofficial Rust client library for the
stock `mosh-server`. The crate owns the Mosh session, authenticated UDP,
recovery, synchronization, and authoritative terminal state. The
embedding application owns SSH bootstrap, host trust, authentication, server
startup, its executor, and terminal presentation.

The initial design makes four choices:

- keep one crate and one small session-oriented public API;
- keep the protocol state machine deterministic behind a narrow socket, clock,
  and cancellation boundary;
- run one logical asynchronous task per Session on the embedding application's
  executor; and
- expose synthesized UTF-8 VT paint bytes as bounded, ordered output chunks.

The project does not add a server, CLI, generic transport interface, renderer
plugin system, or public terminal-cell model.

## System shape

```text
Embedding application
  ├─ SSH bootstrap, host verification and mosh-server startup
  ├─ executor
  ├─ terminal surface
  └─ one application Session owner
       │ commands: input, resize, repaint, close, cancel
       │ events: lifecycle, reachability, output bytes, close
       ▼
Mosh Session driver
  ├─ UDP socket and fixed server endpoint
  ├─ monotonic timers, retransmission and heartbeat
  ├─ bounded command and event queues
  ├─ cancellation and cleanup
  └─ deterministic protocol core
       ├─ authenticated packet and replay state
       ├─ fragmentation and synchronization state
       └─ authoritative terminal state
            └─ VT painter → bounded output bytes
```

This diagram describes ownership, not a commitment to public modules or
traits. Implementation files appear only when they create a real state,
lifecycle, test, or platform boundary.

## Ownership

| Owner | Owns | Does not own |
| --- | --- | --- |
| Embedding application | SSH, host trust, credentials, server startup, executor, UI and terminal policy | Mosh packet or recovery rules |
| Session driver | One socket, timers, command/event ordering, cancellation and cleanup | Global application state or another Session |
| Protocol core | Packet, replay, peer and synchronization transitions | OS sockets, wall-clock time or UI callbacks |
| Terminal state | The latest confirmed Mosh screen | WebView, native renderer or persistent history |
| VT painter | A deterministic display program derived from terminal state | Remote byte passthrough or system-effect policy |
| Terminal surface | Rendering, selection, search, input encoding and surface recovery | Mosh protocol authority |

One Session owns one key, endpoint, socket, terminal state, timer set, command
queue, output sequence, and cancellation path. Two Sessions must not share any
of them. Shared executor threads do not change this ownership rule.

## Runtime model

The library owns the Session future and its internal lifecycle. The embedding
application runs that future on an existing executor. The initial library does
not create a global runtime or one operating-system thread per Session.

The production driver owns UDP reads and writes, timers, retransmission,
heartbeat, client roaming continuity, reachability, and output generation. The
public `Session` handle sends bounded commands, receives bounded VT output,
observes lifecycle and reachability, and requests graceful close or hard
cancellation. Dropping the handle or cancelling the task closes the socket,
stops timers, clears queues, and releases session secrets.

`Session::connect` returns the handle and a `SessionTask`. The caller polls
`SessionTask::run` on its own Tokio executor. One future polls cancellation and
commands, due protocol work, output capacity, and UDP input. Cancellation has
its own idempotent signal outside the command queue, so queued input or a full
output slot cannot delay it. Graceful close has a separate Session-owned
lifecycle signal. Commands linearized before close are drained in order; later
commands are rejected. The driver then retransmits a reserved, authenticated
close target for at most four seconds. That target and its ACK remain outside
ordinary synchronization retention and public lifecycle state.

The protocol core remains deterministic. Tests provide datagrams, commands,
monotonic time, and simulated network events, then inspect state transitions and
effects. This internal boundary does not become a generic transport API.

The implemented test fixture lives under `tests/support/`. It uses explicit
virtual monotonic time, stable insertion ordering, and a bounded datagram queue.
Each send names one delivery action: drop, deliver after a delay, duplicate at
two delays, or corrupt one selected byte. Reordering follows from delivery
times, silence represents timeout intervals, cancellation clears pending work,
and source addresses may change between datagrams. The client keeps the
bootstrapped server endpoint fixed. A source-address change represents the
client moving networks; a heartbeat gives the stock server a newer authentic
packet from which it can learn that address.

ArkTS, JavaScript, and terminal-render threads never drive protocol ticks. A
paused or rebuilt surface may delay painting, but it must not stop authenticated
UDP processing or make UI timer jitter part of the wire protocol.

### Synchronization commit boundary

The synchronization core owns transport state numbers and reference retention,
not terminal semantics. It first returns an immutable send or apply plan. The
driver commits a send only after the socket accepts the datagram. A state owner
commits a received target only after it applies the difference successfully.
Internal generations reject plans that outlive an intervening transition.

Commit results report the discard floor and any state removed by the bounded
retention policy. The terminal or client-state owner can therefore release the
matching snapshot without copying the synchronization core's eviction logic or
retaining an unbounded parallel history.

The client-state owner stores one bounded operation log after the known
acknowledgement. SSP state checkpoints contain operation indexes, not copied
histories. Sending encodes only the operations between the selected base and
target checkpoints. Acknowledgement advances discard the prefix, and sender
capacity eviction removes its matching checkpoint.

This two-step boundary keeps socket failure, malformed state, cancellation, and
late events from advancing protocol authority. It remains private and specific
to SSP; it is not a public state-machine or transport framework.

### Monotonic timing boundary

The timing core accepts monotonic milliseconds and returns one pending deadline
or one send plan. It never sleeps, owns a runtime, or performs I/O. State-change
collection, frame pacing, delayed acknowledgement, retransmission, and
heartbeat deadlines share one wake decision. If time jumps forward, every
expired reason is folded into that one send; missed periodic ticks are not
replayed.

Cancellation and graceful close are owned once by the Session driver. Dropping
or cancelling the driver discards timer state and any uncommitted plans;
individual timing helpers do not maintain parallel lifecycle flags. The close
deadline is a bounded terminal phase, not evidence that ordinary network
silence means disconnection.

RTT estimation is integer-only. Authenticated, in-sequence timestamp replies
feed TCP-style SRTT and RTTVAR equations with Mosh's 50 ms RTO floor. Until the
first sample, the local policy uses a one-second RTO. Wire timestamp conversion
is separate from the estimator so wrap, stale replies, and implausible samples
cannot mutate it accidentally.

An established Session treats only the local UDP I/O categories listed in ADRs
0011 and 0012 as recoverable. Failed sends leave SSP and scheduler plans
uncommitted. Failed receives change no protocol authority and temporarily
disable socket polling. Both paths use the current RTO with a one-second floor
and five-second ceiling. Commands, output, close, cancellation, and due sends
remain pollable. A later send uses fresh packet and fragment identifiers while
preserving the same Session authority. This adds neither a retry owner nor a
cancellation flag outside the Session driver.

### Fragment reassembly boundary

The packet receiver authenticates a datagram and validates the fragment
envelope before reassembly sees it. One receiver-owned reassembler groups
fragments by identifier, retains one incomplete instruction, and emits bytes
only after every fragment from zero through the declared final number is
present. Only a complete compressed instruction may cross into zlib and
Protocol Buffers decoding. A single-slot representation enforces the one-message
policy without a second identifier map or aggregate byte counter.

Exact duplicates are idempotent. Conflicting bytes or final markers discard
that identifier rather than exposing a partially assembled instruction. A
capacity or size rejection leaves earlier valid fragments unchanged. Expiry is
measured from the first retained fragment, not extended by later arrivals. The
reassembler has no cancellation mode. The Session lifecycle owner clears or
drops it together with the rest of the packet state.

This is a private protocol boundary, not a general stream reassembler. Its
limits and conflict policy are local defenses; the stock-server fixture proves
multi-fragment ordering and loss behavior, not identical stock internals.

The sender assigns one increasing local identifier to each compressed
instruction and uses it for every fragment in that send. It commits SSP and
timer state only after the socket accepts every datagram. A failed or partial
send leaves the plan uncommitted; a later attempt uses a new identifier.

## Terminal state and output

### Two separate decisions

The design separates output representation from output delivery:

| Question | Decision |
| --- | --- |
| How is a display update represented? | Synthesized UTF-8 VT paint bytes |
| How is it delivered? | Bounded chunks written in order, plus an explicit repaint request |

The initial contract does not add display revisions, surface generations, or a
rendering acknowledgement loop. Existing terminal emulators can consume the
chunks directly. A replaced surface requests a self-contained full repaint
from the latest authoritative state.

### Public Session contract

The Phase 3 contract separates values by delivery semantics:

```text
Session
  ├─ creation policy: adaptive, always, or never prediction display
  ├─ bounded ordered commands: input, resize, repaint
  ├─ bounded ordered VT output: next_output
  ├─ latest lifecycle state: Connecting, Active, Closed
  ├─ latest reachability: AwaitingPeer, Responsive, Interrupted
  └─ prompt idempotent cancellation

SessionTask::run
  └─ LocalClosed | RemoteClosed | Cancelled | OwnerDropped | SessionError
```

Lifecycle state is a coalescing latest value, not an event log. `Active` means
the driver accepted at least one authenticated remote terminal state; it is not
a live reachability promise. VT output remains a one-slot ordered queue with
backpressure. There is deliberately no cross-stream revision or ordering
contract between lifecycle state and VT chunks. `Closed` may become visible
while one previously accepted output chunk remains available to drain.

Reachability is a second coalescing latest value. Before the first accepted
remote state it is `AwaitingPeer`. Recent authenticated remote-state and
client-acknowledgement progress make it `Responsive`. Missing recent contact or
reply makes it `Interrupted` without changing `Active` or completing the task.
Invalid or incomplete traffic does not refresh it. An independent observer can
await reachability while the Session owner consumes VT output; neither stream
backpressures the other.

The first attachment attempt fails after 15 seconds without an accepted remote
state. Once `Active`, ordinary silence has no completion timeout. Authenticated
close, explicit close or cancellation, owner drop, state exhaustion, and
unrecoverable errors retain their existing meanings.

The contract requires:

- chunk boundaries do not change terminal semantics;
- a consumer writes output chunks in order;
- a requested full repaint is regenerated from current terminal state, not
  replayed from historical output;
- a repaint request does not bypass chunks already accepted by the queue;
- output is synthesized by the painter and never contains unauthenticated UDP,
  bootstrap text, or remote PTY bytes copied without terminal-state handling;
- drawing output does not issue DA, DSR, CPR, or other device queries; and
- the embedding application decides which terminal side effects may leave its
  terminal surface.

Command validation rejects oversized input and invalid terminal dimensions
before queueing. Public task errors expose stable I/O, protocol, resource,
exhaustion, and internal-state categories without revealing keys, bootstrap
text, datagram contents, or private parser types. [ADR 0007](decisions/0007-public-session-api.md)
records the complete ownership decision.

The initial API does not expose cells, graphemes, colors, cursor internals, or a
renderer trait. A read-only screen snapshot may be considered after a real
native-renderer consumer supplies a stable use case and compatibility tests.

The private terminal implementation stores cloneable `vt100::Screen`
snapshots, not parser objects. A verified stock `HostBytes` operation is a
self-contained framebuffer patch, so the state owner seeds a fresh parser from
the selected SSP reference screen, applies ordered operations, and publishes a
new snapshot only after validation succeeds. This preserves the
synchronization plan/commit boundary and keeps parser internals out of retained
state.

The `vt100` 0.16.2 screen modules are compiled privately into this crate with
the narrow U+FFFD correction described in the
[source patch note](../src/vt100/PATCH.md). No screen type enters the public
API. Keeping them in the same crate also makes the Git checkout and the Cargo
source package compile the same terminal behavior.

The painter derives full or incremental VT from two screen snapshots. Full
paint resets the verified external focus and mouse-policy modes, then emits the
complete visible screen and input modes. It never replays bells, titles,
clipboard requests, terminal queries, or historical remote bytes. Unsupported
visible-screen sequences fail the terminal transition instead of silently
changing the compatibility profile. One verified exception suppresses Vim's
focus-reporting toggle: the core neither forwards `CSI ?1004h` nor claims to
send focus events, and a full repaint explicitly disables the mode.

The stock server supplies the current visible framebuffer without a reliable
remote-application alternate-screen boundary. Incremental output and a full
repaint therefore reproduce the visible Vim or pager frame without asserting
that the consumer projection entered an alternate screen. The library does not
parse painted contents to guess application lifecycle, expose a synthetic mode,
or retain a second presentation history.

### Bounded local prediction

The measured Phase 2 latency gate admits one private prediction engine and ADR
0010 adds one public per-Session display policy. The engine is a display
projection, not a second terminal authority. The confirmed stock screen remains
in the SSP-indexed terminal-state map. Prediction retains at most 32 single-byte
printable ASCII records, one base screen, one projected screen, and one emitted
display snapshot. It does not retain one screen per character.

A new epoch begins tentatively. Every eligible character is recorded within the
same queue bounds, but remains hidden. The authoritative terminal state retains
the latest monotonic echo-acknowledgement watermark across later differences.
HostBytes and acknowledgement progress therefore may arrive in either order.

For every authoritative screen, prediction replays the bounded candidate queue
from its shared base and finds the longest display-equivalent prefix. A matching
base or prefix preserves the epoch while acknowledgement lags. An
acknowledgement confirms the epoch only when it covers at least one candidate
without advancing beyond that matching prefix. Confirmation removes every
candidate already reflected by authority, even when authority is ahead of the
watermark, then rebases any remaining suffix. Later eligible characters in the
confirmed epoch may be painted immediately.

No matching candidate prefix, or acknowledgement progress beyond its matching
host effect, is a divergence. It clears the projection and returns painting to
authority. Control input, resize, paste, backspace, escape sequences, capacity
exhaustion, or ten seconds of age does the same. An update without a newer echo
acknowledgement does not by itself revoke a confirmed idle epoch.

The age limit bounds stale speculative display and memory; it never declares a
prediction correct. `Never` omits the projection, `Always` displays eligible
confirmed-epoch input, and the standard `Adaptive` default displays it only
when the existing authenticated RTT-derived frame interval indicates a slow
link or a pending eligible projection reaches the bounded glitch delay.
Prediction adds no client-only success timeout, runtime switch, cell patch
protocol, or rendering acknowledgement. The ordered VT output and explicit
full-repaint contracts remain unchanged. An explicit repaint clears prediction
first and regenerates the latest authoritative screen.

### Display authority and recovery

The Rust terminal state is the Mosh display authority. A terminal emulator such
as xterm.js holds a presentation projection for rendering, selection, search,
and local scrollback. The two models serve different owners.

The painter tracks the last display state emitted for the current consumer and
the latest authoritative state. The emitted snapshot may include a bounded
local projection. It may skip superseded intermediate screens before
generating bytes, but it never drops or reorders chunks already accepted by the
output queue. Replacing a surface invalidates the incremental base and requires
an explicit full repaint.

A detached or destroyed surface does not accumulate an unbounded history of
incremental paint bytes. The Session continues processing UDP and updating its
terminal state. A repaint request is ordered after chunks already accepted by
the output queue. A replacement surface consumes that bounded stream in order;
the full repaint then converges it to the latest state before incremental
output resumes.

Mosh synchronizes visible terminal state, not complete shell history. Local
scrollback accumulated by a terminal surface is useful but cannot become a
promise of SSH-equivalent or persistent history. Durable work and authoritative
history remain the responsibility of remote tools such as `tmux` or `screen`.

A consumer that must preserve the terminal contents from before Mosh starts
owns whole-Session isolation. It activates a temporary terminal page before the
first Mosh output, applies every ordered chunk there, and restores the previous
page only after final output handling and Session shutdown. This protects the
consumer's primary history; it does not create complete scrollback inside the
Mosh Session or reconstruct each remote application's screen transition.

## Output flow control

Protocol flow and display flow remain separate:

- authenticated UDP processing continues while a surface is slow;
- the output queue is bounded and starts with one pending chunk;
- the driver coalesces terminal states before generating new VT output;
- once a chunk enters the queue, the consumer owns its ordered delivery;
- a detached or replaced surface requests a full repaint and does not assume
  that the next received chunk skips older queued output; and
- output backpressure never creates an unbounded byte history.

When the output queue is full, authenticated UDP and terminal-state updates
continue, but the painter does not generate more bytes. When capacity returns,
it paints from the last emitted state to the newest permitted display state:
authority plus any active bounded projection. No render-completion
acknowledgement is part of the core Session contract.

## LeanTTY integration

LeanTTY keeps its established `App Shell → Tab → Pane → Session` ownership. One
Pane owns one Mosh Session handle and one Terminal Surface. Mosh does not create
a second workspace or session model.

```text
PaneRuntime
  ├─ SessionViewModel
  │   └─ Mosh client adapter
  │       └─ N-API → existing Rust executor → Mosh Session future
  └─ TerminalSurfaceController
      └─ TerminalBridge → xterm.js
```

The adapter sends input, resize, repaint, graceful-close, and cancellation
commands and selects the standard prediction mode when creating the Session. It maps
native events to the owning Pane with a Session identifier and lifecycle
generation. Pane close cancels the Session. Surface detach keeps the Session
alive. Surface attach requests a full repaint. Destroying the actual Session
owner cancels its task; replacing a UI Page may retain that process-owned owner
and rebind callbacks. No session key or terminal state is persisted. The
lifecycle generation rejects late Session events; it is not a display revision.

LeanTTY keeps the existing binary bridge, VT renderer, input, resize, search,
selection, and system-effect validation. Mosh-specific integration adds an
explicit repaint request, not a per-chunk completion path.

LeanTTY also owns one temporary terminal page for the lifetime of each Mosh
Session. It enters that page before delivering the first Mosh output and leaves
it after ordered output draining and Session cleanup. It does not inspect VT
paint to detect Vim, pagers, or other applications. This restores the pre-Mosh
Pane contents without claiming complete scrollback within the Mosh Session.

SSH and Mosh share the terminal surface but keep distinct delivery policies:

| SSH | Mosh |
| --- | --- |
| Ordered PTY bytes are irreplaceable. | Intermediate display states may be superseded. |
| Backpressure may pause transport output. | UDP and protocol state continue while paint is coalesced. |
| Recovery needs a snapshot plus undelivered bytes. | Recovery can regenerate the latest visible state. |

This is an explicit Mosh path, not a generic Transport plugin layer.

## Initial file structure

The repository remains one crate. The following layout is a target, not an
instruction to create empty files:

```text
src/
  lib.rs                 small public Session API and re-exports
  bootstrap.rs           strict validated bootstrap values
  limits.rs              packet, queue, timer and terminal limits
  error.rs               bounded public and internal errors
  packet.rs              authenticated datagram envelope
  crypto.rs              approved primitive integration and secret handling
  fragment.rs            bounded datagram fragmentation state
  fuzzing.rs             cfg(fuzzing)-only parser and state-machine entry points
  instruction.rs         bounded zlib and verified Protobuf messages
  synchronization.rs     bounded SSP sender and receiver state transitions
  timing.rs              RTT, pacing, acknowledgement, recovery and heartbeat deadlines
  terminal/
    mod.rs               terminal ownership boundary
    state.rs             authoritative screen and modes
    paint.rs             deterministic bounded VT output
  session.rs             public Session facade, lifecycle, errors and task handle
  session/
    driver.rs            single-owner socket, protocol, timing and cleanup runtime
    client_history.rs    bounded client operations and SSP checkpoints
    reachability.rs      latest-value interruption and recovery observations
  test_support.rs        Linux-only shared stock fixture process support

tests/
  support/               deterministic bounded network and safe fixtures
  fixtures/              sanitized original behavior records
  bootstrap.rs
  network_fixture.rs
  packet.rs
  wire_contract.rs
  synchronization.rs
  terminal_output.rs
  session_lifecycle.rs
  interoperability.rs

fuzz/
  fuzz_targets/          unpublished Linux cargo-fuzz parser and state targets
```

Modules stay private unless a public contract requires them. Test support does
not become production transport abstraction. The final names may change when
implementation reveals a clearer boundary, but the ownership rules remain.

Do not add a schema-generation directory, `build.rs`, vendored protocol schema,
or native crypto/compression layer. Rust message types come from the verified
wire contract. Development-only fuzz targets appear in Phase 3, when parsers
exist to fuzz.

## Security and resource invariants

- Validate lengths before allocation or decoding.
- Authenticate packets before processing payloads.
- Reject replay, invalid authentication, incompatible versions, and ambiguous
  bootstrap output explicitly.
- Bound packets, fragments, buffers, commands, events, retries, timers,
  terminal state, paint output, and repaint frequency. Bound prediction only
  if a later measured need admits it to the core.
- Use a monotonic clock for protocol time and define behavior after long
  suspension or clock jumps.
- Keep session secrets in memory and exclude them from `Debug`, errors, traces,
  snapshots, environment dumps, and terminal output.
- Preserve command ordering, cancellation, cleanup, and late-event isolation.
- Use established cryptographic crates; never implement primitives locally.

## Deferred choices

The initial design defers:

- prediction beyond the measured single-byte printable ASCII epoch;
- a public terminal-cell or screen-snapshot API;
- terminal profiles beyond the first verified UTF-8 VT target;
- complete or persistent scrollback;
- a library-created runtime;
- non-UDP transports and generic transport traits;
- more than one crate; and
- broad non-LeanTTY compatibility claims.

See [the protocol contract](protocol-contract.md), [limits](limits.md),
[dependency assessment](dependency-assessment.md), [testing](testing.md), and
[implementation survey](implementation-survey.md) for the evidence behind these
choices.
