# Phase 3 mechanism necessity review

> Status: Phase 3C accepted review
>
> Date: 2026-08-30
>
> Governing principles: [project principles](project-principles.md)

## Purpose and boundary

This review decides which implemented mechanisms belong in the supported Mosh
client core before Phase 3 changes code structure. It does not authorize a wire
change, expand the compatibility claim, or require the project to reproduce the
stock client or paper architecture.

The review uses evidence in this order:

1. the project principles and accepted decisions;
2. the independently derived [protocol contract](protocol-contract.md);
3. the [published Mosh design](https://mosh.org/mosh-paper.pdf) and applicable
   standards;
4. measured local behavior and black-box stock 1.4.0 fixtures; and
5. [external maintenance evidence](external-maintenance-evidence.md) as
   hypotheses, not protocol authority.

The classifications mean:

- **Keep**: required by the current contract or demonstrated core behavior.
- **Simplify**: preserve the behavior but remove private representation or
  ownership complexity when Phase 3D can prove equivalence.
- **Remove**: delete behavior with no current contract or demonstrated value.
- **Defer**: do not add or change behavior until its stated evidence gate passes.

## Result

The current implementation has no entire production mechanism that should be
removed. Its major mechanisms serve authenticated transport, exact input,
terminal convergence, recovery, bounded display delivery, or measured
interactive latency. Phase 3D should simplify private representation only where
the resulting patch has a smaller invariant surface and preserves public and
stock behavior.

The review does remove one unsupported claim from the written contract: remote
shell exit is a user-visible stock behavior, but this project has not yet
identified its authenticated wire signal. It remains deferred rather than being
treated as implemented Session closure.

## Session driver and wake path — keep

**User value and evidence.** One driver owns one Session's socket, key, endpoint,
protocol state, terminal state, timers, queues, and cleanup. The public
two-Session fixture proves that this ownership prevents cross-session packets,
state, cancellation, and late events. A single wake path lets cancellation,
ordered commands, due protocol work, output capacity, and datagrams make
progress without a UI-driven tick.

**Cost.** The driver is the largest private module and its explicit wake
arbitration is specialized Tokio code. The state is Session-sized rather than
process-global and all queues are bounded.

**Compatibility and maintenance risk.** Splitting ownership across tasks or
adding a generic transport abstraction would make ordering, send commit,
cancellation, and cleanup harder to prove. Replacing the wake expression can
also change priority under simultaneous readiness.

**Phase 3D boundary and rollback.** Keep one logical owner and one caller-polled
future. Phase 3D may extract a private owner with a clear invariant, or simplify
the wake expression, only with characterization tests for simultaneous
cancellation, command, timer, output, and datagram readiness. The Phase 3A
baseline is the rollback point. Do not add a runtime, operating-system thread,
generic transport trait, or UI polling contract.

## Lifecycle and cancellation — keep; simplify private duplication only

**User value and evidence.** A dedicated idempotent cancellation signal cannot
wait behind the command or output queues. Owner drop closes the driver, and
`SessionTask::run` reports the final outcome. Public unit and stock fixtures
cover cancellation before and during work, independent owner drop, cleanup,
and replacement-session isolation.

**Cost.** The driver distinguishes command-consumer and output-consumer loss
internally even though both map to `OwnerDropped`. This distinction is small
and private.

**Compatibility and maintenance risk.** Moving cancellation into the command
queue would weaken prompt shutdown. Adding cancellation flags to timers,
fragmentation, or synchronization would create competing lifecycle authority.

**Phase 3D boundary and rollback.** Keep one Session-owned cancellation signal
and the public `Cancelled` versus `OwnerDropped` distinction. Phase 3D may
collapse private close-origin variants only if no diagnostic or test needs the
origin. Revert that local refactor if it obscures ownership or failure
diagnosis.

## Output backpressure — keep

**User value and evidence.** The one-slot ordered output queue prevents
unbounded VT history while authenticated UDP processing and terminal
convergence continue. Stock sustained-output and replacement-repaint fixtures
prove that a slow surface does not stall the protocol and that the next paint
converges from the last accepted projection.

**Cost.** The driver retains one last-painted snapshot and coalesces pending
display states. The consumer must drain accepted chunks in order and explicitly
request a full repaint after replacing a surface.

**Compatibility and maintenance risk.** Backpressuring the UDP protocol on UI
painting would lose Mosh's current-state advantage. An unbounded queue would
turn sustained output or a detached surface into memory growth. A per-chunk ACK
would add the rejected cross-layer display protocol.

**Phase 3D boundary and rollback.** Preserve the queue bound, ordered accepted
chunks, state coalescing, and explicit repaint. Simplify only private flags or
clones whose removal leaves those four properties characterized. No display
revision, surface generation, or paint acknowledgement is admitted.

## `ClientHistory` and SSP checkpoints — keep

**User value and evidence.** Client input cannot be skipped like intermediate
screen frames. One bounded operation log plus state-to-index checkpoints lets
SSP build the exact difference between its selected base and target without
copying an input history per state. Acknowledgement discards the confirmed
prefix. Deterministic tests and stock sustained input prove ordered, exact
delivery through retransmission and backpressure.

The SSP plan/commit boundary is also required. Socket acceptance must precede
send authority, and a terminal difference must apply successfully before the
receiver commits its target. Generations reject stale plans. Semantic discard
floors and reported capacity evictions keep synchronization and snapshot owners
consistent without duplicating retention policy.

**Cost.** The driver stores up to 4,096 unacknowledged client operations, at
most 32 sent references, and checkpoint indexes. The synchronization core has
explicit plans, generations, dispositions, and commit results.

**Compatibility and maintenance risk.** Replacing checkpoints with only the
latest input state can lose or duplicate keystrokes after loss, ACK reordering,
or an uncommitted send. Pruning only by an arbitrary capacity can discard a
peer-named source. External projects report both failure classes, while this
project's current transition model already avoids them.

**Phase 3D boundary and rollback.** Keep the algorithms and bounds. A private
module split is acceptable if it makes operation-log ownership clearer without
creating a generic SSP framework. Characterize ACK-before-unsent-change and a
long acknowledged state run before changing index or pruning logic. Roll back
to the Phase 3A implementation on any difference in encoded operations or
retained state.

## Retained terminal state — keep; defer allocation optimization

**User value and evidence.** An authenticated server difference names an SSP
base state, which may be older than the latest screen after packet reordering.
The client must retain the corresponding terminal snapshots to apply that
difference atomically. Stock loss, reordering, outage, source-port change,
tmux, Vim, resize, and repaint fixtures prove convergence from retained state.

**Cost.** The receiver retains at most 32 cloneable visible-screen snapshots,
plus one last-painted snapshot and the bounded prediction projection. At the
maximum terminal dimensions these clones can dominate Session memory and
temporary allocation.

**Compatibility and maintenance risk.** Keeping only the latest screen would
reject otherwise constructible reordered updates and weaken recovery. Adding a
second framebuffer cache, authoritative scrollback, or one prediction snapshot
per character would duplicate state and expand memory.

**Phase 3D boundary and rollback.** Keep SSP-indexed snapshots, the
authenticated discard floor, and capacity-eviction reporting. Remove avoidable
clones only when characterization proves atomic apply and painter equivalence.
Defer alternative snapshot storage or terminal dependencies until allocation
measurement shows material value. Persistent and authoritative scrollback
remain outside the crate.

## Fragmentation — keep; simplify the one-message representation if useful

**User value and evidence.** Stock 1.4.0 emits authenticated instructions that
span multiple UDP datagrams. Reverse-order and missing-fragment fixtures prove
that bounded reassembly is part of the working core, not optional completeness.
Authentication before storage, exact-duplicate idempotence, conflict discard,
size limits, and expiry protect untrusted memory.

**Cost.** The reassembler stores at most one incomplete identifier, 749
fragments, and 1 MiB for ten seconds. Its current map-shaped representation is
more general than the one-message policy.

**Compatibility and maintenance risk.** Removing fragmentation breaks observed
stock output. Accepting overlapping identifiers without evidence increases
memory and ambiguity. Simplifying the private map to one optional incomplete
message can accidentally change duplicate, conflict, expiry, or failure
atomicity.

**Phase 3D boundary and rollback.** Keep the wire envelope and all current
limits. Phase 3D may use a single-slot private representation if focused tests
show identical outcomes and the patch materially reduces state or branches.
The existing map is the rollback. More incomplete messages, different conflict
policy, or identifier reuse needs a stock fixture and a protocol decision.

## Terminal painting and repaint — keep

**User value and evidence.** Mosh sends terminal framebuffer differences, not a
PTY byte stream that can be forwarded directly. The authoritative terminal
model validates and applies each difference to its named base. The painter then
emits safe, bounded VT that existing terminal surfaces can consume. Full repaint
rebuilds a replacement surface; incremental paint avoids a full-screen update
for every new state. Stock shell, Unicode, resize, tmux, Vim, sustained-output,
and replacement-surface fixtures cover this path.

**Cost.** The crate owns a terminal dependency, profile-specific validation,
one painted snapshot, and full versus incremental generation. Strict rejection
requires new evidence when stock applications expose unsupported visible state.

**Compatibility and maintenance risk.** Passing remote bytes through would
replay system effects and would not reconstruct an arbitrary SSP state.
Full-only painting would be simpler but increases output and surface work;
incremental-only painting cannot recover a new surface or size change. Public
cells or custom paint operations would freeze a larger renderer contract.

**Phase 3D boundary and rollback.** Keep authoritative state, full and
incremental paint, the output bound, and explicit repaint. Refactor only behind
independent repaint equivalence tests. Do not expose cells or add a renderer
trait without a demonstrated consumer gate.

## Local prediction — keep the measured subset; defer extensions

ADR 0010 later admits the demonstrated LeanTTY need for the standard
`Adaptive`, `Always`, and `Never` display policy. It supersedes only this
review's deferral of public prediction control; the measured predictor subset
and rejection of broader prediction remain unchanged.

**User value and evidence.** Controlled 40 ms and 80 ms one-way-delay stock
fixtures reduced median visible printable-key echo from 108/189 ms to 0 ms.
Prediction remains a display projection and becomes visible only after a stock
echo acknowledgement confirms the epoch. Authority wins on mismatch.

**Cost.** The layer adds an epoch, up to 32 pending single-byte records, one
base snapshot, and one projection. It also expands repaint and convergence
tests. External maintenance evidence identifies prediction as the largest
recurring complexity source.

**Compatibility and maintenance risk.** Removing the retained subset would
discard a measured Mosh usability benefit. Broadening it to backspace, paste,
control input, navigation, or client-only confirmation can erase confirmed
cells, flicker, or mislead the user. A ten-second age limit bounds stale display
state but never confirms a prediction.

**Phase 3D boundary and rollback.** Keep only printable single-byte ASCII and
the confirmed-epoch rules in ADR 0005. Add an explicit erase-not-predicted
regression before touching this code. ADR 0010 later adds a thin display gate
after a real consumer need; defer every broader heuristic until separate
measurements and stock evidence justify its maintenance cost. `Never` remains
the rollback and comparison path.

## Timers, recovery, and roaming — keep; defer local-I/O recovery policy

> Superseded on 2026-09-02 for selected established-Session UDP send errors:
> LeanTTY reproduced a fatal local I/O result while the application, stock
> server, and remote PTY remained alive during a physical WLAN interface
> outage. ADR 0011 admits a narrow allowlist and scheduler-owned retry pacing.

**User value and evidence.** RTT estimation, frame pacing, delayed ACK,
retransmission, and heartbeat provide bounded responsiveness without replaying
every missed tick. The published design establishes these roles. Deterministic
tests cover deadlines, reordering, loss, clock jumps, and stale plans. Stock
fixtures prove a bidirectional outage, queued input recovery, source-port
change, server disappearance, and continued cancellation.

**Cost.** Each Session owns one RTT estimator, datagram timestamp adapter,
scheduler, and monotonic epoch. The scheduler carries plan generations and send
reasons, but it creates one next deadline rather than independent runtime
timers.

**Compatibility and maintenance risk.** Removing pacing or retransmission
breaks recovery or allows screen floods to fill queues. Removing heartbeats
weakens NAT continuity and roaming. Treating all long silence as closure would
contradict the current Session contract. Conversely, treating every local UDP
send error as packet loss could spin, hide a dead socket, or differ across
Linux and HarmonyOS.

**Phase 3D boundary and rollback.** Keep the deterministic scheduler,
monotonic-time checks, generations, RTT bounds, retransmission, and heartbeat.
Do not add parallel timer cancellation state. Defer recoverable `send_to` error
classes until controlled platform tests identify which errors are temporary
and establish retry pacing. Long-outage, suspend/resume, physical address
change, and reachability indication remain later evidence gates.

## Public event surface — keep; defer remote clean exit

> Superseded on 2026-08-30 for clean close only: LeanTTY reproduced the missing
> lifecycle behavior, and project-controlled stock 1.4.0 fixtures established
> the authenticated reserved target, acknowledgement, and bounded wait. ADR
> 0008 admits additive graceful close and `SessionExit` outcomes. Reachability
> events and temporary UDP-send recovery remain deferred.

**User value and evidence.** The small public API separates three delivery
semantics: ordered bounded commands and VT output, coalesced latest lifecycle
state, and a final task result. `Active` means an authenticated terminal state
was accepted; it does not claim current reachability. Public fixtures exercise
this contract without exposing protocol, terminal, or LeanTTY types.

**Cost.** Consumers must poll one task, observe lifecycle separately from
output, and understand that `Closed` can precede draining one accepted output
chunk. This is smaller than a combined event protocol with cross-stream
revisions.

**Compatibility and maintenance risk.** A single event queue would let a slow
surface delay lifecycle. Adding packet, prediction, or reachability events
would expose private policy and encourage consumers to depend on unstable
internals. [Stock Mosh documents](https://mosh.org/#usage) that remote logout
normally ends a session, and an external Rust implementation reports a shutdown
marker, but neither source establishes the wire rule for this independent
crate.

**Phase 3D boundary and rollback.** Keep the exported types and current
semantics from ADR 0007. Defer a remote-close decoder, `SessionExit` variant,
and any public reachability state until a controlled stock 1.4.0 fixture
captures the authenticated behavior and the rule is recorded in provenance.
Until that evidence was collected, server silence left the Session repaintable
and cancellable. It still does: only the authenticated close exchange changes
the lifecycle outcome.

## Phase 3D constraints

Phase 3D may change private code structure, but it must preserve this review's
retained behavior. For each proposed refactor it must record:

- the ownership or invariant problem being reduced;
- why the result is smaller in total maintenance cost;
- the focused characterization or regression test;
- the smallest relevant stock fixture, when behavior could cross the wire; and
- the Phase 3A or per-batch rollback point.

The first useful review targets are the Session's private close representation,
`ClientHistory` ownership, avoidable screen clones, and the single-incomplete
fragment representation. These are inspection targets, not instructions to
change code. A refactor should be rejected when the current representation is
clearer or the reduction is only a line-count improvement.

No Phase 3D refactor may add remote-close behavior, temporary-send recovery,
reachability events, broader prediction, public cells, overlapping fragment
messages, or another runtime boundary. Those are deferred behavior decisions,
not code cleanup.
