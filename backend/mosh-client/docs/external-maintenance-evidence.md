# External Mosh client maintenance evidence

> Survey date: 2026-08-30
>
> Window: 2024-08-30 through 2026-08-30. All three repositories were created
> in 2026, so the survey covers their complete default-branch, tracker, and
> release history.

## Purpose and evidence boundary

This survey looks for maintenance failures that should influence later review
and test design. It does not make third-party code a protocol authority.

Evidence comes from each repository's public commits, pull requests, issues,
tags, releases, and test tree. Linked consumer issues are used only for
observable failures and maintainer rationale. Third-party source and commit
descriptions can suggest hypotheses and independently designed tests. A wire or
compatibility rule still requires an allowed source or a project-controlled
stock-server fixture recorded in [provenance](provenance.md).

Repository activity is not proof of correctness. Missing issues may mean that a
project uses another tracker, and a closed issue without a fix is not evidence
that the reported behavior is correct.

## Repository inventory

| Project | Default-branch history | Change and release channel | Test signal | Maintenance interpretation |
| --- | --- | --- | --- | --- |
| [MoshCatty](https://github.com/binaricat/MoshCatty) | 51 main-branch commits from 2026-07-10 through 2026-07-17 | 7 merged PRs, no repository issues, and 9 releases from 0.1.0 through 0.1.8 | CI and release workflows, deterministic protocol tests, platform tests, and opt-in live stock-server tests | The densest evidence source, but nine releases in eight days show rapid initial hardening rather than long-term maturity |
| [`dart_mosh`](https://github.com/gwitko/dart_mosh) | 4 commits from 2026-06-05 through 2026-06-22 | No PRs, issues, tags, or GitHub releases; package version reached 0.0.4 | One test file and no repository CI workflow | A small lifecycle comparison with too little history for maintenance consensus |
| [`mosh-go`](https://github.com/unixshells/mosh-go) | 34 main-branch commits from 2026-03-13 through 2026-04-05 | 3 later issues, no PRs or GitHub releases, and 8 tags from v0.1.0 through v0.5.2 | Eight Go test files and no repository CI workflow | Useful embedded-runtime failure evidence; the latest resource fixes are newer than the last tag |

Sources: [MoshCatty commits](https://github.com/binaricat/MoshCatty/commits/main),
[PRs](https://github.com/binaricat/MoshCatty/pulls?q=is%3Apr), and
[releases](https://github.com/binaricat/MoshCatty/releases);
[`dart_mosh` commits](https://github.com/gwitko/dart_mosh/commits/master);
and [`mosh-go` commits](https://github.com/unixshells/mosh-go/commits/main),
[issues](https://github.com/unixshells/mosh-go/issues?q=is%3Aissue), and
[tags](https://github.com/unixshells/mosh-go/tags).

## Change classification

| Class | MoshCatty | `dart_mosh` | `mosh-go` | Consequence for this crate |
| --- | --- | --- | --- | --- |
| Protocol compatibility | One large hardening series changed state reconstruction, startup state, limits, and stock compatibility in [PR #5](https://github.com/binaricat/MoshCatty/pull/5) | No independently verified compatibility change | State pruning, resize after reset, action tracking, and several client/server additions | Treat every claimed wire rule as a hypothesis; prefer our paper and black-box stock evidence |
| Synchronization and recovery | High-latency reconstruction, retransmission, outage behavior, and bounded receive work changed together | Temporary UDP send errors became recoverable packet loss | State pruning changed from an arbitrary cap to the peer's throwaway number; key-repeat state creation was restricted | Preserve plan/commit separation, semantic discard floors, bounded history, and adverse-network fixtures |
| Terminal behavior | Alternate screen, wide cells, attributed blanks, edge repaint, large screens, and prediction received repeated fixes | No terminal-model maintenance after the initial release | Attribute masks, attributed spaces, resize reset, scrollback policy, and a terminal dependency rollback changed rendering | Keep terminal cases independent of one emulator and retain stock-visible repaint fixtures |
| Lifecycle and cancellation | Consumer failures included connection setup and platform input, but no public cancellation defect was recorded | Remote shutdown now completes `done`; socket send failure no longer terminates recovery | Most lifecycle work was WASM polling, reconnection, goroutine, and scheduler ownership | Investigate clean remote exit and transient send failure; do not import WASM scheduling policy |
| Performance and resource use | Receive work and large-terminal state were bounded; local prediction dominated change volume | No resource-specific change | Reused zlib and terminal objects, capped retained states, and changed scrollback after WASM OOM reports | Measure our retained and transient allocations; do not optimize from Go/WASM symptoms alone |
| Testing | Live stock-server prediction and high-latency cases were added beside deterministic suites; strict Clippy was restored separately | The shutdown change added an encrypted loopback lifecycle test; the socket-error change added no test | Existing unit files cover core modules, but the important WASM OOM and scheduling commits did not add regression tests | Use independent deterministic tests plus the smallest stock fixture; never accept a commit message as proof |
| Maintenance-only | CI, static Windows CRT, glibc floors, macOS packaging, branding, and release metadata | Public API documentation and version bumps | Demo media and dependency metadata | Exclude packaging and application policy from the protocol crate |

## Relevant historical failures

### M1: broad high-latency failure cluster

- Source: [MoshCatty PR #5](https://github.com/binaricat/MoshCatty/pull/5) and
  consumer [Netcatty #2121](https://github.com/binaricat/Netcatty/issues/2121).
- Root cause: the maintainer described multiple gaps in remote-state
  reconstruction, retry and outage behavior, terminal rendering, prediction,
  and bootstrap address handling rather than one defect.
- Affected path: Session receive/send loop, SSP state history, timing, terminal
  state, prediction, and bootstrap networking.
- Observable failure: duplicate displayed characters, SSH-like input latency,
  missing prediction marks, and intermittent connection failure on a
  high-latency path.
- Fix pattern: a large stock-alignment batch plus deterministic tests and four
  live tests against stock 1.4.0. The reporter later confirmed the packaged
  result worked.
- Regression layer: deterministic protocol and terminal tests, controlled
  delay, live stock server, and consumer confirmation.
- Applicability: high for failure classes and test scenarios; low for copying
  implementation choices. Our Phase 2 fixtures already cover several pieces,
  but not a long outage or remote clean exit.
- Confidence: medium. The report and confirmation are direct, but the fix batch
  changed 19 files and does not isolate each root cause.

### M2: local backspace prediction erases confirmed prompt cells

- Source: [MoshCatty PR #7](https://github.com/binaricat/MoshCatty/pull/7),
  consumer [Netcatty #2275](https://github.com/binaricat/Netcatty/issues/2275),
  and stock Mosh [issue #344](https://github.com/mobile-shell/mosh/issues/344).
- Root cause: a speculative row-shift treated erase as a local cursor operation
  without knowing the shell's editable boundary.
- Affected path: prediction overlay and acknowledgement gating.
- Observable failure: repeated Backspace or Delete temporarily erased the
  prompt, user, or host until authoritative state arrived.
- Fix pattern: stop predicting erase bytes, forward them unchanged, clear
  pending speculation, and wait for authoritative acknowledgement before
  resuming prediction.
- Regression layer: unit cases for both erase bytes, mixed input batches, old
  host frames, partial acknowledgements, and resumed prediction.
- Applicability: high. Our printable-ASCII-only predictor already rejects erase,
  but it lacks an explicit regression named for `0x08` and `0x7f`.
- Confidence: high. The same failure and complexity tradeoff appear in an
  independent client and the mature stock project.

### D1: remote shell exit leaves the client lifecycle unfinished

- Source: [`dart_mosh` commit 1a14a31](https://github.com/gwitko/dart_mosh/commit/1a14a310277cfbf1f928e45315605267054d7872).
- Root cause: the Session did not recognize a claimed reserved terminal state
  number as peer shutdown.
- Affected path: authenticated transport decode and Session completion.
- Observable failure: `done` waited indefinitely after the remote program
  exited unless the caller closed the Session.
- Fix pattern: recognize the candidate sentinel, stop transmit timing, complete
  the lifecycle, and leave final output drainable.
- Regression layer: decoder unit test plus an encrypted loopback Session test.
- Applicability: high as a lifecycle gap. Our public API has no remote-exit
  outcome and currently treats the candidate number as an ordinary newer state.
- Confidence: medium for the observed Dart failure, low for the wire rule. The
  public Mosh papers do not document this sentinel, so this project must first
  observe an unmodified stock 1.4.0 server exit.

### D2: transient UDP send error ends a recoverable Session

- Source: [`dart_mosh` commit 325bc91](https://github.com/gwitko/dart_mosh/commit/325bc912ecea600b2fbd9d9e0ac7b069baf66af8).
- Root cause: a local UDP send exception escaped the transmit loop even though
  an interface outage should behave like packet loss.
- Affected path: datagram send and retransmission scheduling.
- Observable failure: the Session surfaced an exception instead of recovering
  when connectivity returned.
- Fix pattern: leave the state unsent, keep scheduling, and retry later.
- Regression layer: none was added in that commit.
- Applicability: high. Our Session currently maps every `send_to` error to a
  terminal `SessionError::Io`; whether the platform exposes recoverable errors
  this way must be measured before changing policy.
- Confidence: medium. The repository changelog states the behavior directly,
  but no regression test or cross-platform evidence accompanies it.

### G1: protocol state retention pruned by capacity instead of semantics

- Source: [`mosh-go` commit 534e7da](https://github.com/unixshells/mosh-go/commit/534e7da40df84bc71509c762c5bef3ec6df636d1).
- Root cause: an arbitrary state-map limit discarded references independently
  of the peer's throwaway floor.
- Affected path: SSP receive history and WASM framebuffer tracking.
- Observable failure: state tracker desynchronization.
- Fix pattern: prune using authenticated protocol discard information.
- Regression layer: no test file changed with the fix.
- Applicability: already addressed. Our synchronizer owns a semantic discard
  floor, returns capacity evictions to terminal ownership, and tests both.
- Confidence: medium because the fix is explicit but lacks issue reproduction
  and a paired regression test.

### G2: repeated ticks allocated new states for one cumulative input

- Source: [`mosh-go` commit b71a9c5](https://github.com/unixshells/mosh-go/commit/b71a9c5824db7dc0f27395bba22cc102d8a1f89b).
- Root cause: each dirty tick reset the pending-send marker and assigned another
  state number to the same cumulative payload before acknowledgement.
- Affected path: client input history, send scheduling, and acknowledgement.
- Observable failure: repeated input and key-repeat instability.
- Fix pattern: keep at most one unacknowledged state in flight in that design.
- Regression layer: no test file changed with the fix.
- Applicability: medium. Our design can retain several sent states, but commits
  a send plan only after all datagrams are sent and the stock sustained-input
  fixture proves 128 ordered commands. An ack-before-send characterization is
  still useful.
- Confidence: medium. The commit explains its mechanism, but its one-in-flight
  solution is architecture-specific and is not a required SSP rule.

### G3: embedded runtime allocation and scheduling failures

- Sources: zlib reuse
  [d9db398](https://github.com/unixshells/mosh-go/commit/d9db3986ba8087ae2f383b515057e3e759276bcb),
  terminal reuse
  [bde81c1](https://github.com/unixshells/mosh-go/commit/bde81c13f66b4647a0a0dd7e28fd8eec560ebb26),
  state cap
  [31b4ffc](https://github.com/unixshells/mosh-go/commit/31b4ffccfc1cdb622f9601e00e8248af59b23a6b),
  and JS-driven polling
  [c582e52](https://github.com/unixshells/mosh-go/commit/c582e52c15da9a92af684641770b13b918cd072e).
- Root cause: repeated compressor or terminal allocation, unbounded retained
  projection state, and a scheduling model that could starve the Go WASM
  runtime.
- Affected path: compression, terminal projections, retained state, receive
  ownership, and WebTransport integration.
- Observable failure: WASM out-of-memory, connection loss, or stalled receive.
- Fix pattern: reuse expensive objects, cap maps, and let the embedding runtime
  drive polling.
- Regression layer: the cited commits changed production files without adding
  tests.
- Applicability: medium for measuring bounded allocation; low for the Go/WASM
  scheduling solution. Rust/Tokio and LeanTTY have different ownership.
- Confidence: medium for the reported symptoms and low for cross-runtime
  transferability.

### G4: terminal dependency and edge-state regressions

- Sources: attributed spaces
  [052945f](https://github.com/unixshells/mosh-go/commit/052945f9f83f95a1f3cbd5844fad0c4812ce2d2a), resize after
  reset [aff8e05](https://github.com/unixshells/mosh-go/commit/aff8e0572a1f14bb83297f0d86a58bc3d04611ac),
  attribute masks [96ce8ce](https://github.com/unixshells/mosh-go/commit/96ce8cef8d94a786384a6f3efbdfe3ba6fdf6b45),
  and terminal dependency rollback
  [0284bd6](https://github.com/unixshells/mosh-go/commit/0284bd652aab3dfff5b0b74945c23901d7537b61).
- Root cause: blank cells with attributes were treated as visually empty,
  reset/resize could reference unavailable state, bit layouts disagreed, and a
  dependency's scrollback semantics changed.
- Affected path: full repaint, state reconstruction, cell attributes, and
  terminal dependency integration.
- Observable failure: missing title-bar styling, rejected first post-resize
  frame, incorrect attributes, or broken rendering after dependency update.
- Fix pattern: preserve visible attributes, define reset anchors explicitly,
  verify bit mappings, and pin or roll back incompatible terminal behavior.
- Regression layer: no test file changed in the cited commits.
- Applicability: medium as independently designed terminal cases. It does not
  justify copying another emulator's cell model or version pin.
- Confidence: medium for project-local failures, low for identical behavior in
  this crate.

## Unverified reports

The three `mosh-go` issues were opened and closed on 2026-08-11 without a PR or
subsequent main-branch commit:

- [#1](https://github.com/unixshells/mosh-go/issues/1) concerns server build tags
  for WASI and is outside this client crate.
- [#2](https://github.com/unixshells/mosh-go/issues/2) proposes two pending-diff
  races. Its failure shapes are relevant, but they remain reporter-authored
  hypotheses and must be tested independently.
- [#3](https://github.com/unixshells/mosh-go/issues/3) claims a long-session
  throwaway-number failure. The reporter later commented “overzealous LLM.” It
  is not accepted evidence. A long-session stock fixture may still test the
  general bounded-retention risk without adopting the claim.

## Recurring cross-project patterns

1. Prediction is the largest complexity multiplier. MoshCatty needed two major
   PRs and many follow-up commits, while stock Mosh retained a known Backspace
   artifact rather than add more special cases. Keep our narrow confirmed ASCII
   projection and reject erase, paste, control input, and uncertain state.
2. State history must follow protocol relationships and remain bounded.
   Arbitrary eviction, duplicate state creation, or acknowledgement races can
   lose input or make reconstruction impossible. Keep synchronization as the
   sole retention owner and preserve plan/commit boundaries.
3. Network interruption has both packet and local-I/O forms. A protocol that
   survives loss may still terminate when the local socket reports a temporary
   send error. Define that boundary from measured platform behavior.
4. Terminal correctness fails at edges: wide cells, attributed blanks,
   alternate screen, resize/reset, and dependency semantic changes. Visible
   stock behavior and independent projections are stronger than matching one
   library's internal flags.
5. Embedded runtimes expose allocation, scheduler, and cleanup faults that a
   short native loopback test misses. Limits, reuse, owner-drop tests, and later
   HarmonyOS measurements remain necessary.

## Project-specific behavior to exclude

Do not bring the following into this crate without a separate current need:

- MoshCatty's ConPTY input, Ctrl+C process policy, static CRT, glibc packaging,
  macOS release assembly, or optional ECN path;
- `mosh-go` server, PTY, WebTransport, JavaScript polling, WASM component, or
  server-side scrollback policy; or
- `dart_mosh` stream and timer APIs as public Rust API precedent.

These changes may matter to their embedding applications, but they do not
belong to a small standard Rust Mosh client contract.

## Independent test candidates

| Priority | Candidate | Independent test design | Current disposition |
| --- | --- | --- | --- |
| High | Clean remote exit | Run a controlled command that exits under stock 1.4.0; observe the final authenticated traffic and public lifecycle without reading stock source | Required before recognizing any shutdown sentinel or adding an exit outcome; then update provenance |
| High | Temporary local send failure | First reproduce the actual error returned by a local route/interface interruption; prove input remains retained and recovery resumes without a production transport abstraction | Candidate for Phase 3E or physical LeanTTY acceptance; do not change all I/O errors into loss blindly |
| High | Ack before an unsent local change | In the deterministic synchronization model, register new input, receive an older acknowledgement before the send commits, then prove the next difference still contains the input exactly once | Characterization candidate; current plan/commit design appears compatible |
| High | Erase is never predicted | Exercise `0x08`, `0x7f`, mixed batches, partial acknowledgements, and later printable input; assert erase is forwarded but never projected | Small unit regression plus an optional delayed-stock prompt case |
| Medium | Long client-state run | Drive more than 1,024 acknowledged client changes through stock 1.4.0 while measuring responsiveness and bounded retained state | Tests the risk independently; does not adopt `mosh-go` issue #3 |
| Medium | Terminal edge matrix | Cover attributed blanks, wide glyphs at the right edge, reset followed by resize, and full repaint on a fresh projection | Extend only where existing terminal and stock fixtures lack the case |
| Low until measured | Allocation soak | Measure compression, terminal snapshot, repaint, and retained-state memory under sustained input/output on native and ARM64 targets | Optimize only after a repeatable bound or growth problem appears |

## Consequences for later Phase 3 work

- Do not refactor production code from this survey alone.
- Carry clean remote exit and transient send failure into the Phase 3C audit as
  explicit lifecycle questions.
- Treat semantic discard floors, bounded references, post-send commit,
  conservative prediction, and public owner-drop isolation as strengths to
  preserve unless contrary stock evidence appears.
- Use the independent test candidates when building the Phase 3E coverage map.
- No new wire rule was accepted in this phase, so this survey does not add a
  protocol-provenance entry.
