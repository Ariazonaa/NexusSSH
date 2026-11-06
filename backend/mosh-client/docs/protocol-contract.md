# Initial protocol contract

> Status: Phase 0 implementation boundary
>
> Compatibility target: stock `mosh-server` 1.4.0, IPv4, one fixed UDP
> endpoint, external SSH bootstrap, and protocol version 2

This document separates verified wire facts from working hypotheses. A rule may
enter production code only when [provenance](provenance.md) records an allowed
source or a project-controlled black-box fixture.

## Evidence order

Use evidence in this order:

1. RFCs, published Mosh papers, and public command contracts;
2. project-controlled observations of released stock binaries;
3. original experiments and sanitized fixtures;
4. permissively licensed implementations as comparison material; and
5. public descriptions of GPL implementations as behavior summaries only.

Another implementation can suggest a field, state, or simpler architecture. It
cannot establish our wire contract. Stock Mosh source, comments, tests, schemas,
and file organization remain excluded.

## Verified envelope

Two loopback experiments against the Ubuntu stock 1.4.0 client and server
verified the following structure. The experiments authenticated every retained
packet in memory and retained no key, plaintext, or raw packet capture.

```text
UDP datagram
  8 bytes   direction and sequence, big endian
  N bytes   AES-128-OCB3 ciphertext
  16 bytes  OCB3 authentication tag

OCB3 nonce
  4 bytes   zero
  8 bytes   the datagram's direction and sequence bytes

authenticated plaintext
  2 bytes   timestamp, big endian
  2 bytes   timestamp reply, big endian; 0xffff means absent
  8 bytes   fragment identifier, big endian
  2 bytes   fragment number; bit 15 marks the final fragment
  N bytes   fragment payload

reassembled payload
  zlib stream
    Protocol Buffers transport instruction
```

The high bit of the 64-bit datagram header is the direction bit. Client packets
use zero; server packets use one. The remaining 63 bits hold a sequence number.
Both directions began at sequence zero in the observed sessions. OCB3 used a
12-byte nonce, a 16-byte tag, and no associated data.

The client keeps a 64-sequence authenticated replay window per direction. It
accepts each in-window sequence once and rejects duplicates and older packets.
Authentication completes before the window changes. This is a bounded local
security policy, not a claim that stock Mosh uses the same window.

The transport instruction used these top-level fields in every observed
message:

| Field | Wire type | Observed role |
| --- | --- | --- |
| 1 | varint | protocol version; value `2` |
| 2 | varint | base state number |
| 3 | varint | new state number |
| 4 | varint | acknowledged state number |
| 5 | varint | discard-before state number |
| 6 | length-delimited | compressed state's decoded difference |
| 7 | length-delimited | variable padding or chaff |

An 80-column by 24-row initial client message produced the nested field path
`6 → 1 → 3`, with width field 5 equal to 80 and height field 6 equal to 24.
Controlled stock-client fixtures now establish two client operation families:

| Path inside field 6 | Meaning |
| --- | --- |
| `1 → 2 → 4` | Append the exact input bytes; ASCII and UTF-8 retain order and encoding |
| `1 → 3 → 5/6` | Set terminal columns and rows |

The client object is an ordered operation history. A difference may contain an
earlier resize and repeated equal resize events, so the receiver applies every
operation in order and treats the last size as current.

Controlled stock-server fixtures now establish the server-side terminal shapes:

| Path inside field 6 | Meaning |
| --- | --- |
| `1 → 2 → 4` | Apply one self-contained UTF-8 VT screen patch |
| `1 → 3 → 5/6` | Set terminal columns and rows before later operations |
| `1 → 7 → 8` | Echo acknowledgement; monotonically advances the terminal state's retained client-state watermark used to confirm a tentative prediction epoch |

The terminal decoder preserves operation order, applies each difference to a
cloned SSP reference screen, and rejects invalid UTF-8, incomplete VT, invalid
sizes, and ambiguous known operations. Unknown Protocol Buffers fields retain
normal forward-compatible semantics. The first profile caps a difference at
2 MiB, 4,096 operations, 100,000 cells, and eight Unicode scalars per cell.
The cloned state also inherits the latest echo-acknowledgement watermark when a
difference contains only HostBytes or resize operations. A decreasing watermark
is invalid. Prediction compares authority with bounded candidate prefixes, so
HostBytes and acknowledgement progress need not share one difference or arrive
in one preferred order.

Stock 1.4.0 screen initialization also sends reset-only sequences for mouse
highlight tracking, focus reporting, and urxvt mouse encoding. The terminal
model accepts only the verified `CSI ?1001l`, `CSI ?1004l`, and `CSI ?1015l`
resets among those three modes. A stock Vim 9.1 Session also carries
`CSI ?1004h`; the model accepts and suppresses that focus-reporting toggle
because the Session has no focus-event input contract. It still rejects the
unverified `?1001h` and `?1015h` sets. A full repaint emits all three resets
before the synthesized visible state. Incremental paint starts from a prior
project-generated projection and need not repeat them.

The same fixture confirms user-visible full-screen behavior without requiring
the client projection to preserve the remote alternate-buffer flag. Under
`TERM=xterm-256color`, terminfo declares the `smcup` and `rmcup` sequences;
stock Mosh sends the resulting visible Vim frame. The client must repaint that
frame and restore the shell after Vim exits. This contract does not promise
SSH-equivalent local scrollback or alternate-buffer history.

The project-generated initial exchange independently encoded that 80-by-24
shape as client state `0 → 1`. Stock 1.4.0 accepted the packet and returned a
protocol-version-2 server instruction acknowledging state one. The successful
fixture used timestamp zero, no timestamp reply, fragment identifier and number
zero, one final fragment, and one zero byte of chaff. This establishes a
smallest startup exchange only; it does not define later timestamp, fragment
identifier, chaff, or terminal-state policy.

See [the observation record](fixtures/stock-1.4.0-loopback.md) for the complete
sanitized shapes.

## Published behavioral contract

The published Mosh papers define these initial behaviors:

- each direction transports state changes and acknowledges the peer's state;
- a newer authentic client packet may update the server's peer address;
- sequence numbers are 63-bit values and exhaustion terminates the session;
- transport messages larger than a datagram are fragmented;
- the sender retains at most 32 states beyond the receiver's known state;
- the retransmission timeout follows smoothed RTT and variance with a 50 ms
  floor;
- delayed acknowledgements wait at most 100 ms;
- frame time is half smoothed RTT, bounded to 20–250 ms; and
- idle sessions send a heartbeat every three seconds.

These values define compatibility behavior. [Limits](limits.md) add local hard
bounds around them.

## Synchronization transition model

The deterministic synchronization core now implements the published SSP state
selection rules without interpreting terminal differences:

- both local and remote transport state numbering starts at zero and increases
  without reuse;
- each outgoing instruction targets the newest allocated local state;
- the source is the newest retained sent state whose RTO has not expired, or
  the known receiver state after expiry;
- an empty acknowledgement or heartbeat allocates a new transport state number
  even when the synchronized object is unchanged;
- an acknowledgement advances the known receiver state only when it names a
  retained state that this sender actually sent;
- a receiver applies a difference only when it retains the named source state;
- a target already constructed is idempotent and is not applied again;
- an out-of-order target may be retained as a future source, but only a greater
  target becomes the latest remote state; and
- after committing a new latest state, the receiver discards references below
  the authenticated throwaway number.

Difference application uses a two-step plan and commit transition. A caller
must apply the opaque difference successfully before committing the target
state. Send and receive plans carry internal generations, so a plan becomes
invalid after an intervening state change. Commit results expose capacity
evictions and the authenticated discard floor so the state owner can release
matching snapshots without mirroring retention policy.

The sender retains at most 32 unacknowledged target references. The receiver
retains its required throwaway-floor state and the newest 31 other references.
Unknown acknowledgements and missing source states do not mutate transport
authority; later instructions can recover from a retained known state. The
receiver-side count and unknown-ack behavior are local bounded policies, not
claims about stock Mosh internals.

## Timing, recovery, and roaming model

The deterministic timing core implements the published SSP deadlines without
owning a runtime or socket:

- before the first RTT sample, RTO is one second;
- authenticated timestamp replies from newer packets update SRTT and RTTVAR
  with the TCP equations and Mosh's 50 ms RTO floor;
- frame interval is half SRTT, rounded to milliseconds and clamped to 20–250
  ms; without a sample it is 250 ms;
- local changes collect for at most 15 ms and coalesce until the frame
  interval permits a send;
- an acknowledgement waits at most 100 ms and may preempt frame pacing;
- an unacknowledged state is retried one RTO after its last committed send;
- an idle direction sends a heartbeat after three seconds; and
- a long monotonic-time jump produces one send containing every expired reason,
  never one send per missed tick.

The RTT estimator does not interpret wire timestamps directly. The datagram
adapter uses the low 16 bits of monotonic milliseconds, adjusts a received
timestamp by the local reply delay, and accepts an RTT sample only from a packet
whose sequence exceeds every packet previously observed in that direction.
Replies older than one second and samples above the local 60-second ambiguity
bound do not update RTT.

Client roaming does not make the server endpoint mutable. Every outgoing
datagram retains the bootstrapped IPv4 endpoint, and every incoming datagram
must name that source before authentication or replay state changes. The
client's operating-system or NAT source address may change. The heartbeat gives
the stock server a newer authentic packet from which the server can learn that
new client address.

## Fragment reassembly policy

The authenticated receiver groups fragments by their 64-bit identifier. A
fragment number is zero-based, and the final bit names the last number in that
instruction. Reassembly completes only when every number from zero through the
final number is present. Bytes are concatenated in number order, so datagram
arrival order does not affect the compressed instruction. A final fragment
numbered zero takes the same bounded path without retaining incomplete state.

The receiver applies these local defensive rules:

- retain at most 749 fragments in one instruction;
- retain one incomplete instruction identifier and at most 1 MiB total;
- retain at most 1 MiB for one compressed instruction;
- expire an incomplete instruction 10 monotonic seconds after its first
  retained fragment;
- treat an exact repeated fragment as idempotent; and
- discard an identifier if the same number has different bytes or final status,
  or if final markers contradict the retained number range.

Limit failures reject the new fragment without partially committing it. The
reassembler does not own cancellation state; the Session owner clears or drops
it during lifecycle cleanup. Authentication, fragment-envelope validation,
complete reassembly, bounded zlib decoding, and instruction decode remain
separate ordered boundaries.

The local stock 1.4.0 fixture proves that a real multi-fragment server
instruction decodes after reverse-order delivery and that one missing fragment
cannot produce partial output before bounded expiry. The
single-incomplete-message, expiry, duplicate, and conflict choices are this
project's security and recovery policy, not claims about stock implementation
internals.

## Authenticated close exchange

Project-controlled black-box fixtures against stock 1.4.0 establish one
reserved terminal transition for clean shutdown:

- the initiator uses `new_state = u64::MAX`;
- `base_state`, `discard_before_state`, and `state_difference` retain their
  ordinary meanings, so the close can carry all changes after the receiver's
  known base;
- the receiver acknowledges shutdown with `acknowledged_state = u64::MAX` in a
  newly allocated ordinary local state; and
- an initiator retransmits while waiting and stops after an approximately
  four-second bounded acknowledgement window.

Authentication, expected peer source, replay rejection, fragment completion,
bounded decompression, and instruction decoding all precede close recognition.
The reserved target never enters ordinary synchronization numbering or
retention. A received final difference is applied from its retained ordinary
base before the Session reports clean remote completion. Ordinary network
silence is not a close signal.

## Open wire questions

The following questions remain outside the implementation contract until a
fixture resolves them:

- semantic names, cardinality, defaults, and error handling for every remaining
  host instruction and malformed client operation;
- the complete terminal operation, rendition, resize, prediction, and epoch
  field matrix;
- stock fragment identifier generation, reuse, and malformed-conflict behavior;
- stock behavior for authentic packets reordered by more than 63 sequences;
- whether empty chaff is accepted in every stock-server state;
- compression level requirements, trailing zlib data, and canonical encoding;
- stock behavior when an adjusted timestamp reply equals the `0xffff` no-reply
  marker or a timestamp-derived sample spans more than 60 seconds; and
- terminal behavior beyond the first UTF-8 `xterm-256color` profile.

An unresolved field may be carried as bounded opaque data only when no decision
depends on its contents. Unknown authenticated instructions otherwise fail
explicitly.

## Phase gates

Phase 1 may implement bootstrap parsing, the authenticated envelope,
timestamps, fragmentation, bounded zlib, the top-level instruction, and an
opaque state difference. It must verify the first authenticated exchange
against stock 1.4.0 before synchronization work begins.

That first authenticated exchange is now verified by
[`stock-1.4.0-server-initial-exchange`](fixtures/stock-1.4.0-server-exchange.md).
The top-level fields and published SSP algorithm are sufficient to implement
the terminal-agnostic synchronization state machine. Black-box fixtures now
name the initial terminal subset needed for shell, resize, Unicode, tmux, Vim,
visible full-screen transitions, repaint, and sustained I/O. Prediction is not
required for the initial core path and needs both fixtures and a measured
interaction benefit before adoption. Those tests retain sanitized structure
and visible results, never session keys or upstream implementation artifacts.
