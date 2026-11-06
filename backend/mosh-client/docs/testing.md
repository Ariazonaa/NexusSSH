# Testing strategy

## Goal

Tests must prove that the client is safe, bounded, wire-compatible with stock
`mosh-server` 1.4.0, correct as a terminal, resilient under adverse networks,
isolated across Sessions, and viable on an ARM64 HarmonyOS PC through LeanTTY.
A successful build proves none of these properties by itself.

The strategy minimizes remote services. Deterministic local tests provide most
evidence. A locally installed released `mosh-server` supplies black-box
interoperability evidence. Only the final LeanTTY slice needs a physical PC and
a controlled reachable server.

## Evidence layers

| Layer | Environment | Proves | Does not prove |
| --- | --- | --- | --- |
| Pure unit and property tests | Rust process, no network | Parsing, state transitions, limits, Unicode helpers and cleanup rules | Stock-server compatibility |
| Deterministic network simulation | In-process virtual time and datagrams | Loss, duplication, reordering, delay, corruption, roaming and cancellation | Real socket or platform behavior |
| Fuzz and resource tests | Local host | Parser robustness, allocation bounds and transition safety | User-visible terminal correctness |
| Black-box interoperability | Loopback or local LAN with released stock server | Wire exchange and real terminal applications | HarmonyOS lifecycle or LeanTTY bridge behavior |
| Terminal-consumer contract | Local xterm.js and an independent permissive terminal model | Ordered VT paint, repaint and response behavior | Physical ArkWeb/WebGL behavior |
| LeanTTY integration | Signed build on physical ARM64 HarmonyOS PC | N-API, ArkWeb, input, rendering, lifecycle and Pane ownership | Broad device or network compatibility |

Later layers supplement earlier layers. They do not replace them.

## Test placement

Keep small, cohesive implementation tests inline. Move large private test
modules to `src/<module>/tests.rs`; `packet` and `instruction` route fast unit
tests and local stock-process fixtures through separate `unit.rs` and
`stock.rs` files. These tests remain children of their production module and
must not require broader production visibility.

Reserve the crate-level `tests/` directory for tests that exercise public
contracts and for reusable test support. `tests/session.rs` now verifies
initial validation, command validation, lifecycle state, owner shutdown,
idempotent graceful close, and idempotent cancellation through exported types
only. One stock interactive
scenario also drives the public API while remaining beside the existing shared
stock-process fixture code. Move more process fixtures only when that creates a
clear reusable test-support boundary. Choose test boundaries by ownership and
maintenance cost, not by a line-count threshold.

Phase 3E confirmed that this layout already groups tests by retained risk:
parser and protocol tests live with their owners; SSP and timing own
deterministic models; Session owns lifecycle and concurrency; terminal modules
own state, repaint, prediction, and convergence; stock-only process fixtures
stay separated from fast tests. Moving them again would weaken private
ownership without removing duplication. The
[coverage map](test-coverage.md) is the cross-module index.

## Remote-service policy

- Unit, property, simulation, fuzz, and terminal-output tests run offline.
- Interoperability uses an unmodified released `mosh-server` installed on a
  project-controlled Linux host or WSL environment.
- CI does not depend on a public Mosh host, SaaS account, telemetry endpoint,
  cloud database, or runtime download.
- Test fixtures record public behavior and sanitized bytes, not upstream source
  structure or tests.
- A controlled LAN server may support physical-PC acceptance. Its address,
  credentials, and port remain test-local and never become product defaults.
- External networks are optional exploratory evidence, not a release gate,
  unless a later compatibility decision explicitly adds them.

## Coverage by project goal

| Project goal | Required evidence |
| --- | --- |
| Stock server compatibility | Released 1.4.0 black-box exchange, shell, resize, tmux, editor and malformed-peer cases |
| Authenticated UDP safety | Standard primitive vectors, correct/wrong keys, corruption, replay, sequence and address-change tests |
| Recovery and roaming | Deterministic loss/delay/reorder matrix plus real interruption and address-change acceptance |
| Terminal correctness | Unicode, width, combining, cursor, color, resize, alternate screen, sustained I/O and independent renderer comparison |
| Bounded local prediction | Deterministic confirmed/diverged predictions, retained monotonic echo watermark, HostBytes/ACK order independence, generated candidate-prefix sequences, three public modes, adaptive hysteresis and glitch timing, Session isolation, control and paste rejection, limits, stock echo-ACK mapping and comparative latency |
| Small portable library | Linux host build, ARM64 HarmonyOS build, dependency audit and no remote runtime service |
| Session isolation | Two concurrent Sessions with distinct keys, sockets, timers, terminal states, events, graceful close and cancellation |
| LeanTTY value | Side-by-side SSH/Mosh scenarios under normal, interrupted, roaming, lock, sleep, UDP block and recovery conditions |

## Local deterministic tests

The first deterministic network fixture is implemented in
`tests/support/network.rs` and verified by `tests/network_fixture.rs`. It is
test-only and has no production dependency or socket. Its nine initial cases
cover loss, duplication, delivery-time reordering, delayed silence, selected
byte corruption, cancellation cleanup, source-address changes, resource
limits, and repeatable ordering. Later synchronization and Session tests reuse
the fixture to prove protocol behavior; the fixture passing by itself does not
prove recovery or roaming correctness.

### Bootstrap

Cover:

- one valid connect result with surrounding expected output;
- missing, duplicate, ambiguous, malformed, truncated and oversized results;
- invalid port, address and key lengths;
- control characters, invalid UTF-8, extra fields and hostile whitespace;
- bounded scanning and allocation; and
- errors, logs and snapshots that never contain the session key.

The production parser accepts an [`Ipv4Addr`](https://doc.rust-lang.org/std/net/struct.Ipv4Addr.html)
rather than address text. Invalid textual addresses are therefore rejected by
the caller or the standard library before bootstrap parsing; project tests
verify that the typed address is preserved in the resulting UDP endpoint.

### Authenticated datagrams

Use an established cryptographic crate and its published vectors. Project tests
cover integration behavior:

- valid seal/open round trips;
- wrong key, modified nonce, modified associated data and modified ciphertext;
- truncated and oversized packets;
- duplicate, replayed, stale and invalid sequence transitions;
- sequence boundary and exhaustion behavior;
- authenticate-before-decode ordering; and
- secret zeroization and non-disclosure where the selected crate supports it.

### Fragmentation and decoding

The deterministic reassembly suite covers one-fragment completion, in-order and
reverse-order completion, exact duplicates, conflicting duplicates,
contradictory final markers, missing fragments, exact expiry, backward time,
owner cleanup, fragment-count limits, the single-incomplete-identifier policy,
and retained-byte limits. No incomplete instruction may expose bytes to zlib or
Protocol Buffers.

The local stock 1.4.0 fixture generates a real multi-fragment response from
controlled high-entropy output. It authenticates and groups the fragments,
delivers one complete group in reverse order, and requires a valid
protocol-version-2 instruction. A second pass omits fragment zero and proves
that the remainder stays incomplete, survives 9,999 ms, and is fully released
at 10,000 ms. The fixture proves reordering and bounded loss handling, not
stock retransmission or full-session recovery.

### Synchronization, recovery and roaming

Use virtual monotonic time and a deterministic datagram link. Each scenario
records sent, delivered, delayed, reordered, duplicated, corrupted and dropped
packets.

The synchronization unit suite covers initial state allocation,
coalescing, optimistic reference selection before RTO, fallback after RTO,
empty acknowledgements, retained-state acknowledgements, missing references,
reordered and duplicate targets, throwaway floors, two-step stale-plan
rejection, protocol-version checks, state and generation exhaustion, timer
bounds, and 32-state sender and receiver limits.

The timing suite covers the one-second initial RTO, integer SRTT and RTTVAR
updates, 50 ms and 5 s RTO bounds, 20–250 ms frame pacing, 15 ms state
collection, 100 ms delayed acknowledgements, three-second heartbeats, 16-bit
timestamp wrap, stale or implausible timestamp replies, backward time, deadline
overflow, stale wake plans, and long-time-jump coalescing. Cancellation belongs
to the Session driver and deterministic network boundary, not to each timer
helper. One network-driven scenario drops the first authenticated state,
changes the client source address, recovers at the RTO boundary to the same
fixed server endpoint, processes the server acknowledgement, schedules the
return acknowledgement, and cancels without queued work.

The Session unit suite injects I/O outcomes only at the private concrete UDP
send and receive boundaries. It proves that an active Session defers the five
explicit ADR 0011 and 0012 categories without committing failed send plans or
busy-polling a failed receiver. Initial failures and unlisted or ambiguous
errors remain fatal. Separate cases cover authenticated receive recovery on the
same socket, prompt cancellation during a deferred receive, the existing
four-second graceful-close bound under repeated permission failures, a partial
fragmented send replaced under a fresh identifier, and retry isolation across
two Sessions. The hook is compiled only for crate tests and is not a production
transport abstraction.

A local stock 1.4.0 Session fixture adds black-box recovery evidence. A
test-only loopback relay drops both directions for 1.5 seconds, switches the
server-facing traffic to a second UDP source port, and then restores delivery.
The fixture requires the queued command to arrive, the server to reply to the
new source port, and a replacement projection to converge from a full repaint.
A separate case terminates the server, waits beyond one heartbeat interval,
then proves the Session remains locally repaintable and cancellable. These
tests do not add a production transport abstraction.

Two local stock-client fixtures establish the first nested client semantics
without a server or remote service. One verifies that controlled ASCII and
UTF-8 input bytes survive unchanged and ordered. The other verifies that
initial and later PTY sizes use the same resize operation and that the last
ordered size is current even when equal resize events repeat.

Required cases include:

- acknowledgement and retransmission timing;
- old and new state numbers;
- server silence and recovery without false Session death;
- client heartbeat and idle power behavior;
- a client source-address change while the server endpoint remains fixed;
- rejection of packets from a changed or unauthenticated server address before
  replay authority changes;
- stock-server confirmation that only a newer authentic client packet changes
  its return address;
- large monotonic-time jumps after suspension;
- cancellation during receive, send, timer and recovery waits; and
- bounded retry and state retention during a long outage.

### Terminal state

Cover:

- ASCII, UTF-8, combining characters, wide characters and continuation cells;
- invalid or incomplete UTF-8 at every chunk boundary;
- cursor movement, erasure, scrolling regions, wrapping and resize;
- colors, attributes, primary and alternate screen behavior;
- shell prompts, sustained output and control characters;
- control input, paste, backspace and resize without prediction; and
- every terminal-state, line, cell and repaint limit.

Prediction state-machine tests separately cover HostBytes arriving before its
echo acknowledgement, acknowledgement-free authoritative prefixes, authority
leading the watermark across multiple candidates, acknowledgement advancing
without its expected host effect, and monotonic watermark retention. A bounded
property generates up to 16 printable candidates and checks every ordered
authoritative prefix before a lagging acknowledgement confirms the epoch.

The `Never`, `Always`, and `Adaptive` stock Sessions run against independent
servers through the same fixed-delay relay. The fixture proves echo-ACK mapping,
relative visible latency, conservative epoch activation, adaptive low/high-delay
selection, and final authoritative convergence. Deterministic tests separately
cover the exact hysteresis and glitch boundaries. This does not replace later
LeanTTY physical-device latency evidence.

## VT output contract

Tests treat the painter as a security and compatibility boundary.

### Byte and chunk properties

- Splitting the same source updates into different input chunks yields the same
  terminal state and display result.
- Concatenating ordered output chunks preserves terminal semantics.
- A full repaint comes from current state and does not replay captured history.
- The painter never emits device queries such as DA, DSR or CPR.
- Paint output never contains a session key, endpoint secret, unauthenticated
  datagram or parser error text.
- Chunk, repaint frequency and output-byte limits fail explicitly.

### Independent terminal comparison

Feed paint bytes to LeanTTY's version-locked xterm.js and at least one
independent permissively licensed terminal model. Compare visible cells, cursor,
attributes and modes with the Rust authoritative state for the supported
profile.

Test at every possible UTF-8 and escape-sequence chunk boundary. Use the
xterm.js write callback before reading its buffer.

### Repaint and response isolation

Cover:

- first attach and explicit full repaint;
- detach with a pending output chunk;
- destroyed WebView with unread output;
- replacement surface followed by an explicit repaint request;
- repeated repaint of the same state;
- repaint during selection, search and alternate screen; and
- zero terminal `onData` responses caused by drawing or recovery output.

A repaint must not duplicate BEL, title, clipboard, URL, notification or other
system effects. The embedding application validates every allowed system effect
separately.

### Slow consumer and flow control

Use a controlled consumer that delays reads and destroys itself at selected
boundaries. Prove:

- UDP and protocol state continue while paint is delayed;
- the output queue stays within its limit;
- at most one output chunk is pending initially;
- intermediate authoritative states may be coalesced before byte generation;
- queued chunks are never dropped or reordered;
- a replaced surface explicitly requests a full repaint and consumes prior
  queued chunks in order; and
- backpressure and repaint counters remain observable.

Do not hide missing bytes, wrong cells, duplicated effects, hangs, or
irrecoverable state behind an improved throughput metric.

## Session lifecycle and concurrency

Test cancellation and cleanup at every awaited phase: startup, socket creation,
first exchange, active input, resize, retransmission, outage, repaint and close.
After completion, no socket, timer, callback, queue item or secret may remain
owned by the Session.

The first public-contract suite proves `Connecting` and `Closed`, cancellation
outside the command queue, graceful-close command rejection and four-second
bound, invalid command rejection before queueing, output closure,
`OwnerDropped`, the adaptive prediction default, all three public modes, and
per-Session mode ownership. Public stock fixtures additionally prove `Connecting` to
`Active`, input, ordered VT output, full repaint, `LocalClosed`,
`RemoteClosed`, `Cancelled`, and two-Session lifecycle isolation against stock
1.4.0.

The reachability suite separately proves `AwaitingPeer`, `Responsive`, both
interruption reasons, recovery, invalid-traffic isolation, output independence,
and the 15-second first-attachment timeout. Boundary tests use explicit
monotonic milliseconds. A stock-server relay drops both directions long enough
to produce `NoRecentContact`, then restores the same Session. An uplink-only
case keeps authenticated server states flowing while client acknowledgements
age into `NoRecentReply`. Replayed authenticated traffic during an outage does
not refresh contact. A stock server killed without a close exchange produces an
interruption but no close result.

The Phase 3A isolation fixture starts two public Sessions through distinct
loopback relays. It injects Session A ciphertext from Session B's expected relay
endpoint, interleaves unique input, uses different PTY sizes, and rebuilds both
projections through full repaint. It then cancels A, verifies that A's output is
closed and rejects a late packet, waits for B's independent heartbeat, and
proves B still accepts input. A replacement Session rejects the old A packet;
gracefully closing B produces `LocalClosed` while the replacement keeps
running, and dropping the replacement produces `OwnerDropped`. This provides
indirect public-contract evidence for distinct keys and direct evidence for
isolated endpoints, packets, input, VT output, terminal state, timers, errors,
graceful close, cancellation, cleanup, and replacement lifecycles.

The current fixtures also cover bounded command and output channels,
cancellation without a server, a stock 1.4.0 interactive shell, remote PTY
resize, tmux attach and reattach, a Vim full-screen edit and repaint, 128
ordered input commands, and 1,200 lines of output while the one-slot display
queue is full. Loopback relays cover a bounded outage, UDP source-port change,
server disappearance, late authenticated ciphertext, and one Session closing
without affecting another. A local fake peer drops close requests before a
later authenticated acknowledgement to prove bounded retransmission. Stock
client and server fixtures prove the reserved close target in both directions,
the no-ACK window, final-output drain, and server process cleanup. A graceful
owner keeps consuming output until task completion; hard cancellation remains
the escape path when presentation is abandoned. These tests do not prove a
long outage, physical address change, the precise `ErrorKind` produced by every
HarmonyOS network transition, or LeanTTY lifecycle integration. The retained
lid-close record identifies `PermissionDenied` but not its I/O direction, raw
errno, or platform policy; one fixed-revision physical rerun remains required.

## Black-box stock-server interoperability

The oracle is an unmodified released stock `mosh-server` 1.4.0 binary. Tests may
launch it locally and observe public process output, UDP behavior and terminal
results. They do not inspect or translate GPL source, comments, tests, or file
organization.

Each interoperability record includes:

- client revision and build profile;
- exact server version and platform;
- terminal profile, locale, rows and columns;
- network impairment parameters;
- commands and expected visible behavior;
- sanitized packet or state evidence when needed;
- timeout, exit status and cleanup result; and
- an explicit statement that no key or credential was retained.

The first application matrix covers:

- interactive shell prompt and command echo;
- resize before and after output;
- `tmux` attach, detach and ordinary navigation;
- one basic full-screen editor scenario;
- primary and alternate screen transitions;
- sustained input and sustained output;
- network interruption and recovery;
- authenticated client source-address change with a fixed server endpoint;
- wrong key, server disappearance and cancellation;
- visible-state and scrollback behavior without claiming complete history; and
- `Never`, `Always`, and `Adaptive` prediction under controlled bidirectional
  delay, with an authoritative convergence marker.

The stock Session fixture treats alternate-screen compatibility as visible
behavior, not preservation of a local terminal-buffer flag. Under the
`xterm-256color` profile, terminfo declares `smcup` and `rmcup`, but stock Mosh
synchronizes the resulting visible Vim frame. The client must paint the editor,
regenerate it for a replacement surface, and restore the shell on exit. It does
not promise SSH-equivalent local alternate-buffer scrollback.

### Fixture provenance

Every retained fixture is original project evidence. Store a compact manifest
with:

- fixture identifier, creation date, and author;
- stock binary name, version, package source, and platform;
- terminal size, profile, locale, and controlled input class;
- the exact property retained: field shape, length, timing, visible cells, or
  expected error;
- a SHA-256 digest for any sanitized binary artifact;
- the command that verifies or regenerates the artifact; and
- a statement that keys, credentials, plaintext shell output, user paths, host
  names, and upstream source artifacts are absent.

Prefer structural fixtures over raw packet captures. Keep ciphertext only when
a crypto interoperability test needs it, generate it with a fixed test-only key,
and label that key public test data. Never derive a fixture from a real Session
secret.

## LeanTTY physical-PC acceptance

The integration gate uses a signed LeanTTY build on a physical ARM64 HarmonyOS
PC. Compilation, installation or launch alone does not pass it.

One Pane-owned Mosh Session must prove:

- SSH bootstrap uses existing LeanTTY host resolution, host verification and
  authentication;
- the SSH bootstrap closes before the UDP session becomes authoritative;
- keyboard, paste, resize, focus, copy and search reach the correct Pane;
- terminal output uses the existing binary Bridge and xterm.js surface;
- slow rendering does not stop Mosh protocol recovery;
- WebView detach/rebuild receives a current full repaint without historical
  output replay or terminal-generated responses;
- lock, unlock, sleep, wake and application background/foreground transitions
  have defined visible status and recovery;
- UDP allow, block and restore states are distinguishable;
- a verified address change does not lose the Session;
- Pane close cancels only its Session and rejects late events; and
- a concurrent SSH or Mosh Pane does not receive another Pane's output, input,
  key, status or cleanup.

Compare the same shell, tmux and editor workload over SSH and Mosh on the same
device and controlled network. Record correctness, recovery time, interaction
latency, resource use, failure visibility and cleanup. Integrate only when the
measured recovery and interaction value exceeds the added security and
maintenance cost.

## Verification cadence

Run the smallest relevant local tests during implementation. Run formatting,
Clippy, all targets and all features before claiming a phase complete. Protocol
changes also run the smallest relevant stock-server fixture. Terminal changes
run the output-contract suite. Runtime changes run lifecycle and two-Session
isolation tests. LeanTTY changes run the named physical-device scenario.

The required Rust commands remain:

```bash
cargo fmt --all --check
cargo clippy --all-targets --all-features -- -D warnings
cargo test --all-targets --all-features
```

These commands run in the default WSL distribution at
`/mnt/c/repos/mosh-client-rs`.

For release candidates, also run the declared minimum compiler exactly. Version
0.1.1 requires Rust 1.88.0; a current-stable pass cannot replace this check:

```bash
cargo +1.88.0 fmt --all --check
cargo +1.88.0 clippy --locked --all-targets --all-features -- -D warnings
cargo +1.88.0 test --locked --all-targets --all-features
RUSTDOCFLAGS="-D warnings" cargo +1.88.0 doc --locked --no-deps --all-features
cargo +1.88.0 test --locked --doc
```

Current stable also runs strict rustdoc and doctests. Keep the root and fuzz
lockfiles unchanged during gates. Run all ignored stock fixtures serially with
`cargo test --locked --all-targets --all-features -- --ignored --test-threads=1`.
Audit both lockfiles, run the two bounded fuzz targets below, and build the
library with `cargo build --locked --release --lib --target aarch64-unknown-linux-ohos`.
Inspect `cargo package --list` and run `cargo publish --locked --dry-run` on a
clean candidate before upload. The dry run is local verification, not a release.

## Test entrypoints

Keep the common path in Cargo rather than a project-specific test runner:

```bash
# Offline deterministic suite
cargo test --all-targets --all-features

# Deterministic network fixture contract
cargo test --test network_fixture

# Named local stock-server suite; ignored during ordinary offline tests
cargo test --test interoperability -- --ignored --nocapture

# All local stock binary fixtures, including the fixed-key packet differential
cargo test --all-targets -- --ignored --nocapture

# Project-generated first packet accepted by stock server 1.4.0
cargo test --lib stock_1_4_0_server_accepts_the_project_initial_packet \
  -- --ignored --nocapture

# Real stock multi-fragment response, reverse order, and loss expiry
cargo test --lib stock_1_4_0_multifragment_response_reorders_and_expires_after_loss \
  -- --ignored --nocapture

# Dependency policy after Cargo.lock exists
cargo deny check
cargo audit
```

The interoperability test must fail with a clear prerequisite message when the
exact stock 1.4.0 binary is absent. It must never download, install, or contact a
public server. A later helper may launch the local binary and UDP impairment
proxy, but it stays test-only and accepts no credentials.

Phase 3E adds two offline fuzz entrypoints instead of one unbounded aggregate
target:

```bash
cargo +nightly fuzz check untrusted_parsers
cargo +nightly fuzz check state_transitions
cargo +nightly fuzz run untrusted_parsers -- -runs=10000 -max_len=4096
cargo +nightly fuzz run state_transitions -- -runs=10000 -max_len=4096
```

Fuzz corpora, artifacts, and coverage output stay local and require no remote
service. See the [coverage map](test-coverage.md) for target scope and retained
gaps.

## Completion and stop conditions

Stop or reframe the project if allowed evidence cannot define the protocol,
audited permissive dependencies cannot implement it safely, terminal behavior
remains materially below SSH, resource limits require disproportionate
complexity, or the physical LeanTTY slice does not provide clear recovery and
interaction value.

A passing test that no longer represents the product outcome must be revised or
removed. Tests are evidence, not the product goal.
