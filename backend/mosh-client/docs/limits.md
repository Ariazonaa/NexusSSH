# Initial resource limits

> Status: Phase 0 implementation defaults

These limits protect the first compatibility profile. They are hard failures,
not preallocation targets. Increase one only with a stock-server fixture, a
resource measurement, and a recorded rollback path.

## Bootstrap and wire

| Resource | Limit | Failure |
| --- | ---: | --- |
| Bootstrap text scanned | 4 KiB, 64 lines | Reject as oversized |
| Connect records | Exactly one | Reject missing or ambiguous output |
| Encoded key | Exactly 22 unpadded standard Base64 bytes | Reject |
| Decoded key | Exactly 16 bytes | Reject and clear temporary bytes |
| UDP datagram read | 2 KiB | Drop and count oversized datagram |
| Authenticated replay window | 64 sequence numbers per direction | Reject duplicates and packets older than the window |
| Authenticated fragment body | 1,400 bytes | Reject message |
| Fragments in one instruction | 749, derived from the message and fragment bounds | Reject message |
| Reassembled compressed instruction | 1 MiB | Reject message |
| Decompressed instruction | 2 MiB | Reject message |
| Concurrent incomplete instructions | One | A new identifier displaces the incomplete instruction, as in `mosh-server` (NexusSSH P1) |
| Total incomplete-fragment storage | 1 MiB | Reject new fragment |
| Incomplete instruction lifetime | 10 seconds monotonic | Expire and count |
| Protocol nesting | Four known message levels | Reject deeper known or unknown structure |

Authenticate a datagram before parsing timestamps, fragments, zlib, or
Protocol Buffers. Check every declared length before allocation. Exact fragment
duplicates are idempotent. Conflicting bytes or final markers discard that
identifier. Other size or capacity failures preserve earlier valid fragments.
The 10-second lifetime starts with the first retained fragment and does not
slide when later fragments arrive. Reject trailing zlib members.

The reassembler stores one optional incomplete instruction. Its 1 MiB
per-instruction bound therefore also enforces the 1 MiB total-storage bound;
the implementation does not maintain a second aggregate counter.

The test-only deterministic network fixture applies the same 2 KiB datagram
limit and retains at most 256 scheduled datagrams. Queue overflow, time
overflow, backward virtual time, invalid corruption offsets, and events after
cancellation fail explicitly without partially mutating the fixture.

## Synchronization and queues

| Resource | Limit | Policy |
| --- | ---: | --- |
| Sent states beyond known receiver state | 32 | Coalesce state; never retain an unbounded history |
| Received reference states | 32 | Keep the throwaway floor and newest 31 references |
| One state difference | 2 MiB | Reject before serialization or after decode |
| Operations in one terminal difference | 4,096 | Reject before applying any operation |
| One input command | 64 KiB | Split at a verified semantic boundary or reject |
| Client operations retained after acknowledgement | 4,096 | Reject input or resize before mutation |
| Pending Session commands | 64 | Apply bounded sender backpressure |
| Pending output chunks | 1 initially | Apply backpressure to painting while protocol state continues |
| Pending reachability values | One latest value | Coalesce observations; never queue history |
| One output chunk | 2 MiB | Fail explicitly and request a bounded full repaint |

The public API preserves the Phase 2 command and output limits. Input, resize,
and repaint use the bounded command queue. Lifecycle and reachability each use
a separate latest-value channel. Reachability observers do not share VT output
backpressure. Cancellation uses a dedicated idempotent signal, so it cannot
wait behind a full command or output queue. Graceful close uses a separate
idempotent lifecycle signal: commands accepted before it remain ordered, while
later commands fail explicitly. Commands and output are never silently dropped.

Retries have no fixed count because Mosh is designed to survive long outages.
Rate, retained state, and memory remain bounded. The Session stays recoverable
until the caller closes or cancels it, a clean authenticated peer close
arrives, a sequence is exhausted, or an unrecoverable protocol or local I/O
error occurs. A graceful close reserves no ordinary SSP history entry for
`u64::MAX`.

## Terminal

| Resource | Limit | Failure |
| --- | ---: | --- |
| Rows | 200 | Reject start or resize |
| Columns | 500 | Reject start or resize |
| Visible cells | 100,000 | Reject start or resize |
| Bytes represented by one cell | 22 (vt100) | Drop further combining scalars, keep the session (NexusSSH P2) |
| Authoritative scrollback | Zero | The crate synchronizes the visible Mosh screen only |
| Pending repaint requests | One | Coalesce to the newest authoritative state |
| Pending predicted scalars | 32 printable ASCII scalars | Clear prediction and end the epoch before adding another |
| Pending predicted bytes | 32 bytes | Clear prediction and end the epoch before adding another |
| Prediction age | 10 s from the oldest pending scalar | Clear projection and repaint from authority |
| Prediction divergence | No candidate prefix matches, or ACK advances beyond its host effect | Clear projection and end the epoch immediately |
| Prediction screen snapshots | One base, one projection, one last-painted snapshot | Replace or clear; never retain one screen per scalar |
| Echo acknowledgement retained by terminal authority | One latest monotonic value | Replace on advance; reject decrease; never retain history |
| Adaptive slow-link display | Enable above 30 ms frame interval; disable at or below 20 ms | Retain the current choice in between and never retract a visible projection |
| Adaptive glitch display | 250 ms pending eligible projection | Temporarily display that projection; confirmation still comes only from the server |

The surface may keep local scrollback. It cannot make that history
authoritative or force the Session to retain old terminal states.

Prediction never mutates authoritative terminal state. Its age limit clears
stale display state; it does not classify an echo as correct or incorrect. The
adaptive thresholds are internal display policy, not additional wire timers or
publicly configurable limits. Candidate-prefix replay is bounded by the same 32
pending scalars and uses the one retained base rather than per-scalar screens.

## Timers

| Timer | Value or bound | Source |
| --- | ---: | --- |
| Initial retransmission timeout | 1 s | RFC 6298 default before the first RTT sample |
| Minimum retransmission timeout | 50 ms | Published Mosh design |
| Maximum retransmission timeout | 5 s | Local recovery-cadence bound |
| Recoverable established-Session I/O retry | Current RTO, with a 1 s floor and 5 s ceiling | ADRs 0011 and 0012 recovery policy |
| Accepted RTT sample | 0–60 s | Local 16-bit-wrap ambiguity bound; larger samples are ignored |
| Timestamp reply age | At most 1 s | Published Mosh design |
| Local state collection | At most 15 ms | Published Mosh design |
| Delayed acknowledgement | At most 100 ms | Published Mosh design |
| Frame interval | 20–250 ms | Published Mosh design |
| Idle heartbeat | 3 s | Published Mosh design |
| No recent server contact | 6.5 s | ADR 0009 public reachability policy |
| No recent peer reply | 10 s | ADR 0009 public reachability policy |
| Initial attachment timeout | 15 s | ADR 0009 stock-compatible lifecycle policy |
| Incomplete-fragment expiry | 10 s | Local memory bound |
| Graceful-close acknowledgement window | 4 s | Stock 1.4.0 black-box observation |

Use monotonic time. A long suspension advances timers once, coalesces expired
work, and never replays every missed tick. The four-second close window begins
only after an explicit local close request; expiry reports local completion but
does not prove peer receipt. The 15-second attachment timeout applies only
before the first accepted remote state. Ordinary silence in an active Session
may change reachability but never closes the Session or changes lifecycle.
An outgoing timestamp reply is omitted after one second, when no peer timestamp
exists, or when the adjusted value would collide with the wire's `0xffff`
no-reply marker.

A recoverable receive error disables receive polling until its retry deadline;
it does not block commands, due sends, output, cancellation, or graceful-close
progress. Repeated immediate errors therefore remain bounded by the same retry
cadence.
