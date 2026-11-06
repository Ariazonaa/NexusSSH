# Phase 3 refactoring log

This log records accepted Phase 3D refactors and reviews that deliberately
retain the existing design. Each batch must preserve the
[mechanism necessity review](necessity-review.md) and remain independently
reviewable and reversible.

## 3D-1: Unify private Session closure

- **Status:** implemented and verified on 2026-08-30
- **Rollback:** Phase 3C revision `bd4eca0`
- **Problem:** the driver used a private `SessionClose` enum to distinguish
  command-consumer and output-consumer loss, then immediately mapped both to
  public `SessionExit::OwnerDropped`. The wake path represented those closures
  separately, and command handling returned an optional close result even
  though no command could produce one.
- **Change:** make the driver return `SessionExit` directly; map both closed
  owner channels to one private `Wake::OwnerDropped`; make command handling
  return only success or failure; and keep multi-fragment cancellation as an
  uncommitted send followed by the driver's single cancellation decision.
- **Expected simplification:** remove one mirrored lifecycle enum, its
  conversion, two duplicate wake outcomes, and a permanently empty optional
  result. The Session driver remains the sole owner of final lifecycle choice.
- **Preserved behavior:** public API and error types; `Cancelled` versus
  `OwnerDropped`; cancellation priority; command and output ordering; closed
  lifecycle publication; and the rule that a cancelled partial instruction
  never commits SSP or timer state.
- **Characterization:** public `tests/session.rs` covers idempotent cancellation,
  owner drop, closed lifecycle, output closure, and command rejection. The
  private cancellation test covers a driver waiting without a server. The
  stock two-Session isolation fixture covers independent cancellation, owner
  drop, replacement lifecycle, and late-packet rejection.
- **Verification:** focused Session tests, the stock public-isolation fixture,
  and the complete formatting, Clippy, and test suite.
- **Rejected expansion:** replacing the explicit wake poller, moving
  cancellation into the command queue, adding subtasks, or changing remote
  close and local-I/O policy. None is needed to remove the duplicated private
  lifecycle representation.

## 3D-2: Isolate client operation history

- **Status:** implemented and verified on 2026-08-30
- **Rollback:** Phase 3D-1 revision `2711b7c`
- **Review:** keep SSP plan/commit, generations, dispositions, semantic discard
  floors, and reported evictions unchanged. They already form one deterministic
  synchronization boundary. Keep SSP-indexed terminal snapshots in the Session
  driver because it applies terminal differences and consumes synchronization
  commit results; a forwarding wrapper would not remove an invariant.
- **Problem:** `ClientHistory` owns a separate bounded operation log, monotonic
  operation indexes, SSP checkpoints, difference encoding, ACK prefix release,
  and sender-eviction cleanup, but its implementation and tests lived inside
  the Session driver module.
- **Change:** move `ClientHistory` and its focused tests to a private Session
  submodule. Add a characterization proving that an ACK for an older sent state
  cannot remove later input before that input is sent.
- **Expected simplification:** make the cumulative client object and its
  invariants reviewable without exposing it publicly or splitting the crate.
  The Session driver keeps orchestration; the submodule owns operation-history
  representation.
- **Preserved behavior:** operation order and bytes; resize ordering; state
  checkpoints; cumulative differences; ACK release; sender-capacity eviction;
  all existing limits, errors, wire encoding, and public API.
- **Characterization:** the new test passed both before and after the move.
  Existing synchronization tests cover unknown, stale, reordered, and retained
  acknowledgements. The stock sustained-I/O fixture remains the smallest
  end-to-end proof of ordered client history.
- **Verification:** focused `ClientHistory` and synchronization tests, the stock
  sustained-I/O fixture, and the complete formatting, Clippy, and test suite.
- **Rejected changes:** do not wrap the terminal-state map, merge it into SSP,
  change plan/commit, or replace screen clones without allocation evidence. Do
  not redesign prediction merely to avoid the bounded input clone; that belongs
  to later measured allocation work.

## 3D-3: Retain terminal state, repaint, and measured prediction

- **Status:** reviewed and verified on 2026-08-30
- **Baseline and rollback:** Phase 3D-2 revision `e979785`
- **Review:** SSP-indexed terminal snapshots preserve atomic application of
  reordered authenticated differences. The last-painted snapshot owns
  incremental output generation. Prediction's base and projection separately
  own stock-ACK confirmation and speculative display. The bounded input clone
  feeds client history while prediction borrows the original bytes. None is a
  duplicate owner.
- **Accepted change:** add the explicit erase-not-predicted regression required
  by the necessity review. It covers a partial ACK, `0x08`, `0x7f`, and a mixed
  printable-plus-erase batch without broadening prediction behavior.
- **Expected simplification:** no production refactor. Retaining the current
  boundaries avoids a new framebuffer abstraction, renderer contract, or
  cross-owner prediction API whose maintenance cost exceeds current evidence.
- **Preserved behavior:** authenticated terminal authority; atomic difference
  application; bounded full and incremental VT paint; explicit repaint;
  confirmed printable-ASCII epochs; authority on mismatch; and all public API,
  wire, state-retention, and output limits.
- **Characterization:** the new prediction test proves that partial
  acknowledgement does not confirm an epoch and that erase or mixed input
  clears pending display instead of entering the projection. Existing paint
  tests independently reconstruct full, incremental, and resized output.
- **Verification:** focused prediction and terminal tests, the stock local
  prediction fixture, and the complete formatting, Clippy, and test suite.
- **Rejected changes:** do not replace display-equivalence serialization with
  an empty `vt100` diff merely to save an unmeasured temporary allocation. Do
  not merge terminal snapshots with SSP, remove the painted snapshot, expose
  cells, add a renderer trait, broaden prediction, or redesign the bounded
  input path without allocation or compatibility evidence.

## 3D-4: Complete the retained protocol and test-support review

- **Status:** implemented and verified on 2026-08-30
- **Baseline and rollback:** Phase 3D-3 revision `b778f6b`
- **Review:** bootstrap, authenticated packet, replay, zlib, Protocol Buffers,
  RTT, timestamp, pacing, retransmission, heartbeat, and recovery boundaries
  already have one owner and explicit limits. Their remaining copies and
  allocations serve in-place cryptography, owned messages, or bounded codec
  output, so this review retains them.
- **Problems:** the reassembler represented one permitted incomplete message
  with an outer identifier map and a duplicate total-byte counter. The send
  scheduler wrote pending state before detecting generation exhaustion, so an
  error could leave an uncommitted local change or erase pending work. Linux
  stock fixtures also repeated the same version check, public test key, detached
  PID parser, and process cleanup in packet, instruction, and Session modules.
- **Changes:** store one optional incomplete instruction with its identifier
  and derive total bytes from that owner. Remove the unreachable aggregate
  storage error and duplicate constants while preserving the same one-message,
  1 MiB limits. Compute the scheduler's next generation before mutating pending
  or committed state, matching the synchronization plan/commit pattern.
  Consolidate only the identical Linux stock version, key, detached-process,
  and PID support in a test-only crate module.
- **Expected simplification:** make the one-message policy structural, remove
  one map, one mirrored byte count, repeated lookups, and one unreachable error
  branch. All scheduler failures now precede state mutation. Seven stock
  version checks and two detached-process implementations now have one owner.
- **Preserved behavior:** fragment envelope and order; one incomplete message;
  single-fragment completion beside that message; exact duplicate handling;
  conflict discard; failure atomicity; 749-fragment, 1 MiB, and ten-second
  limits; monotonic deadlines; pacing; retransmission; heartbeat; and public or
  wire contracts. Stock fixtures retain their commands, port ranges, process
  groups, timeouts, and cleanup behavior.
- **Characterization:** a new fragment test proves that a complete one-fragment
  instruction neither displaces nor joins the retained incomplete identifier.
  A red-then-green timing test proves generation exhaustion preserves both a
  newly noted change and a due send plan.
- **Verification:** focused fragment and timing suites, the stock reverse-order
  multi-fragment fixture, stock client-input fixture, stock
  outage/source-port recovery fixture, public two-Session isolation fixture,
  and the complete formatting, Clippy, and test suite.
- **Rejected changes:** do not merge parser layers, replace Prost or zlib,
  change conflict or identifier-reuse policy, accept overlapping incomplete
  messages, combine RTT and scheduler state, add timer cancellation, or change
  local UDP error and reachability policy. Do not create a generic stock-server
  harness or merge different foreground process-group, virtual-network, and
  Session-projection helpers merely because all are test code.
