# Phase 3E test coverage map

> Baseline: stock Mosh 1.4.0, IPv4, fixed server endpoint, protocol version 2
>
> Review date: 2026-09-10

This map links each retained risk to evidence that the project owns. It does
not turn unsupported behavior into a compatibility claim.

## Evidence key

| Key | Evidence |
| --- | --- |
| U | Focused unit or boundary test |
| P | Property test with bounded generated cases and shrinking |
| M | Deterministic state-machine or virtual-network scenario |
| C | Session lifecycle or concurrency scenario |
| F | `cargo-fuzz` target |
| S | Black-box stock 1.4.0 fixture |

## Retained core

| Risk boundary | Evidence | Primary location | Result and remaining boundary |
| --- | --- | --- | --- |
| Bootstrap output and secret handling | U, F, S | `tests/bootstrap.rs`, `src/bootstrap.rs`, `untrusted_parsers` | Exact, ambiguous, malformed, line, byte, key, and disclosure limits covered. Address parsing remains the caller's typed responsibility. |
| Authenticated packet and replay state | U, P, F, S | `src/packet/tests/unit.rs`, `src/packet/tests/stock.rs` | Authentication precedes replay mutation; generated payloads round-trip; size, direction, source, replay, corruption, and sequence limits covered. |
| Fragment envelope and reassembly | U, P, M, F, S | `src/fragment/tests.rs`, stock multi-fragment fixture | Reverse-order generated fragments preserve bytes; exact 1 MiB completion, one-slot retention, conflicts, expiry, and cleanup covered. |
| zlib and Protocol Buffers instruction | U, F, S | `src/instruction/tests/unit.rs`, `untrusted_parsers` | Invalid, truncated, trailing, version, compressed, decoded, operation, input, and difference bounds covered. Unknown fields keep Prost semantics. |
| SSP references and transitions | U, M, F, S | `src/synchronization/tests.rs`, `state_transitions` | Plans, stale commits, acknowledgement authority, throwaway floors, reordering, exhaustion, and 32-state bounds covered. |
| RTT, timestamps, pacing, retransmission, and heartbeat | U, M, F, S | `src/timing/tests.rs`, `state_transitions`, recovery fixtures | Published deadlines, wrap, suspension jumps, failure atomicity, roaming recovery, and scheduler-owned temporary-send deferral covered. Timers do not own cancellation. |
| Terminal decode and authoritative state | U, P, F, S | `src/terminal/mod.rs`, `src/terminal/state.rs` | Generated chunk convergence, Unicode and combining bounds, malformed VT, resize, modes, system-effect isolation, and operation limits covered. |
| Full and incremental repaint | U, S | `src/terminal/paint.rs`, stock tmux and Vim fixtures | Repaint convergence, size changes, output limits, sparse blank rows, and the visible-frame-without-application-alternate-boundary contract covered. Whole-Session xterm.js isolation belongs to the LeanTTY slice. |
| Bounded local prediction | U, P, M, C, S | `src/prediction/tests.rs`, `src/terminal/state.rs`, `tests/session.rs`, stock latency, full-loss, and isolation fixtures | Retained monotonic echo watermark, HostBytes/ACK order independence, all ordered prefixes in generated candidate sequences, confirmation, divergence, expiry, capacity, conservative input classes, standard default, all three modes, adaptive hysteresis and glitch timing, per-Session ownership, latency, total UDP loss after public confirmation, and authoritative recovery convergence covered. Broader shell prediction and user-space echo confirmation are outside scope. |
| Session commands, output, lifecycle, and isolation | U, C, S | `tests/session.rs`, `src/session/tests`, stock close and isolation fixtures | Exact command bounds, idempotent graceful close, reserved-state isolation, dropped-close retransmission, bounded no-ACK completion, authenticated local and remote outcomes, final-output drain, cancellation during receive, timer, output, and deferred-I/O waits, owner drop, backpressure, late packets, recovery, and two-Session isolation covered. A test-only hook at the concrete UDP send and receive boundaries covers the five selected local errors after `Active`, initial and unlisted fatal errors, same-socket authenticated recovery, receive pacing, partial fragmentation, close bounds, and isolation without adding a transport abstraction. |

## External defect classes

The tests below were designed from the project's contracts. They do not copy
upstream code, fixtures, or test structure.

| External evidence | Applicability and local regression |
| --- | --- |
| [Mosh 1.2.6 release notes](https://github.com/mobile-shell/mosh/releases/tag/mosh-1.2.6) report an assertion abort in Unicode fallback found by AFL | `unicode_fallback_abort_regression_is_bounded_and_failure_atomic` checks every accepted combining-scalar count, explicit rejection above the limit, and unchanged authoritative state. |
| [Mosh issue 1386](https://github.com/mobile-shell/mosh/issues/1386) reports a stock-server abort after an initial zero terminal size | The public Session contract rejects `0x0`, `80x0`, and `0x24` before socket startup or UDP output. |
| [Mosh issue 1400](https://github.com/mobile-shell/mosh/issues/1400) reports persistent display corruption from a blank-row scroll heuristic | This client has no equivalent scroll heuristic. `sparse_blank_rows_never_confuse_incremental_repaint` independently constructs three sparse frames and proves incremental convergence after every frame. |
| [Mosh issue 950](https://github.com/mobile-shell/mosh/issues/950) reports stalls after VPN MTU truncation | Packet and fragment tests reject truncated or corrupted datagrams without replay mutation; deterministic and stock recovery tests prove later valid traffic recovers. The crate does not claim PMTU discovery or tolerance for a network that silently drops all larger UDP packets. |
| [Mosh issue 1356](https://github.com/mobile-shell/mosh/issues/1356) reports a stock-server deadlock on large paste | The defect is server-side and remains open. The client enforces its 64 KiB command bound, but does not claim that stock 1.4.0 safely consumes every accepted paste as one application write. No client workaround or hazardous stock regression is added. |
| LeanTTY MCRS-003 reproduces fatal local UDP I/O while the stock server and remote PTY remain alive | The physical WLAN case closed on `94f1322`. A later lid-close record identified `PermissionDenied` but could not identify send versus receive or raw errno. ADR 0012 therefore covers that category at both concrete I/O boundaries only after `Active`, with paced receive retry, same-socket authenticated recovery, initial and unlisted fatal errors, prompt cancellation, bounded close, and two-Session isolation. One `ae86bfe` attempt did not exercise the lid; a second used one real lid close but replaced the client process and Session. Neither attempt tests ADR 0012's same-Session recovery, so that physical gate remains pending. |

## Fuzz and resource campaigns

Normal tests exercise every exact parser and retention limit. `proptest` runs
64 bounded cases per property and persists a minimal regression when a case
fails. The fuzz crate is a separate, unpublished workspace and cannot enter a
normal library build.

Run the bounded smoke gate in the default WSL distribution:

```bash
cargo +nightly fuzz check untrusted_parsers
cargo +nightly fuzz check state_transitions
cargo +nightly fuzz run untrusted_parsers -- -runs=10000 -max_len=4096
cargo +nightly fuzz run state_transitions -- -runs=10000 -max_len=4096
cargo deny --manifest-path fuzz/Cargo.toml --config fuzz/deny.toml check
cargo audit --file fuzz/Cargo.lock
```

`untrusted_parsers` covers bootstrap, authenticated packets, fragments, zlib,
Protocol Buffers, terminal differences, terminal application, and paint.
`state_transitions` interprets at most 256 operations and asserts the one-message,
1 MiB, and 32-state invariants after every step. Corpora and artifacts remain
local and ignored. A longer campaign may find more paths, but it is not a remote
service or a deterministic completion claim.

## Deliberate gaps

- Stock versions other than 1.4.0, IPv6, changing server endpoints, PMTU
  discovery, ProxyJump UDP, and broad terminal identities remain outside the
  compatibility claim.
- The stock large-paste deadlock is an upstream server limitation, not client
  behavior that this crate can safely repair.
- xterm.js, ARM64 HarmonyOS, lifecycle, physical roaming, and LeanTTY Pane
  isolation use consumer-owned development evidence; the [0.1.0 review](releases/0.1.0.md)
  records its scope. LeanTTY's formal product acceptance remains separate.
- Dependency and secret audits, final suite reruns, and the publication decision
  are recorded for the current candidate in the 0.1.0 review.
