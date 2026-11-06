# Protocol provenance

This log protects the independent implementation boundary. Add an entry before
or with every nontrivial compatibility rule.

## Allowed evidence classes

- public protocol documentation, papers, and standards;
- public command and bootstrap contracts;
- black-box observations from unmodified released binaries;
- packet captures produced by project-controlled fixtures with secrets removed;
- original experiments and analysis written for this project.

## Hypothesis and architecture sources

Third-party implementations, including GPL-covered implementations, may be
inspected to form hypotheses and compare architectures, state ownership,
resource policies, and engineering tradeoffs. They are not protocol authorities
and cannot alone support a wire or compatibility rule.

Do not copy or translate third-party source code, comments, tests, file
organization, or other distinctive implementation expression. Write project
code and tests independently. Verify any behavior first noticed in a third-party
implementation with an allowed evidence class before accepting it here.

Stock Mosh source code and schemas remain outside protocol derivation. Use
unmodified released stock binaries only as black-box interoperability oracles.

## Entry template

```text
Date:
Behavior:
Evidence class:
Source or fixture:
Observed versions:
Implementation consequence:
Limits and uncertainty:
Author:
```

## Initial entries

### Project compatibility baseline

- Date: 2026-08-29
- Behavior: SSH starts `mosh-server`; the interactive session then uses UDP.
- Evidence class: public command contract and project-controlled environment
  experiment.
- Observed version: stock `mosh-server` 1.4.0.
- Implementation consequence: this crate receives validated bootstrap data and
  does not own SSH authentication or host verification.
- Limits: this entry does not define the packet or synchronization protocol.

### Fixed-port UDP environment feasibility

- Date: 2026-08-29
- Behavior: a physical ARM64 HarmonyOS PC reached a stock server on a fixed UDP
  port over a wired LAN; stable firewall allow, block, and restore states were
  distinguishable.
- Evidence class: project-controlled black-box environment experiment.
- Observed endpoint: `192.168.1.4:60042/UDP`; the address was test-local and is
  not a product default.
- Implementation consequence: a fixed IPv4 UDP endpoint is a viable first
  integration target.
- Limits: the experiment did not prove HarmonyOS application lifecycle recovery,
  address roaming, terminal synchronization, or product value.

### Stock bootstrap record

- Date: 2026-08-29
- Behavior: server startup output contains exactly one public connection record
  shaped as `MOSH CONNECT <port> <key>`; the key is a 22-byte Base64 encoding of
  the 128-bit AES session key.
- Evidence class: public command contract and project-controlled black-box
  observation.
- Source: [Mosh overview](https://mosh.org/), public
  [`mosh-server` manual](https://github.com/mobile-shell/mosh/blob/master/man/mosh-server.1),
  public [`mosh-client` manual](https://manpages.ubuntu.com/manpages/noble/man1/mosh-client.1.html),
  and [stock 1.4.0 bootstrap observation](fixtures/stock-1.4.0-bootstrap.md).
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0.
- Implementation consequence: scan at most 4 KiB and 64 lines of raw output;
  require exactly one ASCII record with four single-space-separated fields;
  accept a decimal port from 1 through 65535; strictly decode exactly 22
  unpadded standard-Base64 bytes into a 16-byte zeroizing buffer; never include
  input or key material in `Debug` or errors.
- Limits: surrounding process output is non-semantic and may contain arbitrary
  bytes. The initial endpoint address is caller-supplied as a typed IPv4 value.
  This rule establishes bootstrap syntax only, not packet compatibility.

### Published SSP behavior

- Date: 2026-08-29
- Behavior: SSP uses separate client-to-server and server-to-client state
  streams, a 63-bit sequence plus direction, authenticated AES-128-OCB, 16-bit
  timestamps, RTT-based retransmission, bounded sender state, delayed
  acknowledgements, adaptive frames, heartbeats, and authenticated client
  roaming.
- Evidence class: public paper and cryptographic standard.
- Source: [Mosh final paper](https://mosh.org/mosh-paper.pdf),
  [detailed draft](https://mosh.org/mosh-paper-draft.pdf), and
  [RFC 7253](https://www.rfc-editor.org/rfc/rfc7253.html). RTT equations and
  the pre-sample one-second default come from
  [RFC 6298](https://www.rfc-editor.org/rfc/rfc6298.html).
- Observed version: published protocol design; exact wire compatibility checked
  separately against stock 1.4.0.
- Implementation consequence: use the documented state and timer model, fixed
  12-byte OCB nonce, 16-byte tag, 50 ms minimum RTO, 100 ms delayed ack,
  15 ms state collection, 20–250 ms frame interval, three-second heartbeat, and
  at most 32 retained sender states. Use only RFC 6298's SRTT, RTTVAR, RTO, and
  initial-value rules; the paper does not establish TCP's congestion control or
  retransmission backoff as SSP behavior.
- Limits: the papers do not define all Protocol Buffers fields, nested terminal
  instructions, or malformed-input behavior.

### Authenticated datagram and fragment envelope

- Date: 2026-08-29
- Behavior: the UDP datagram starts with a big-endian direction/sequence word;
  OCB3 authenticates the remainder with nonce `00000000 || header`; plaintext
  contains two big-endian timestamps, a big-endian fragment identifier, and a
  big-endian fragment word whose high bit marks the final fragment.
- Evidence class: project-controlled black-box observation.
- Source: [stock 1.4.0 loopback observation](fixtures/stock-1.4.0-loopback.md)
  and [fixed-key client packet fixture](fixtures/stock-1.4.0-client-packet.md).
- Observed versions: unmodified Ubuntu `mosh-client` 1.4.0 and `mosh-server`
  1.4.0.
- Implementation consequence: implement the envelope exactly, authenticate
  before parsing or changing replay state, clear candidate plaintext after
  authentication failure, and reject duplicate or too-old authentic packets
  with a 64-sequence local window.
- Limits: the stock fixture proves one client-to-server packet with sequence
  zero. Both earlier sessions used one-fragment messages and no impaired
  network. The 64-sequence replay window is local policy; stock behavior beyond
  that distance and multi-fragment conflict rules remain unresolved.

### Compression and top-level transport instruction

- Date: 2026-08-29
- Behavior: fragment reassembly yields one zlib stream. Decompression yields a
  Protocol Buffers instruction with varint fields 1–5 and length-delimited
  fields 6–7. Field 1 was protocol version 2 in every observed message.
- Evidence class: project-controlled black-box observation, interpreted with
  the public [Protocol Buffers encoding guide](https://protobuf.dev/programming-guides/encoding/).
- Source: [stock 1.4.0 loopback observation](fixtures/stock-1.4.0-loopback.md).
- Observed versions: unmodified Ubuntu `mosh-client` 1.4.0 and `mosh-server`
  1.4.0.
- Implementation consequence: use bounded zlib and original hand-declared
  message types; do not import an upstream schema.
- Limits: the top-level state-number roles agree with the published SSP design,
  but nested terminal semantics remain partially unnamed.

### Initial terminal size shape

- Date: 2026-08-29
- Behavior: an 80-by-24 initial client message placed width 80 at nested field
  path `6 → 1 → 3 → 5` and height 24 at `6 → 1 → 3 → 6`.
- Evidence class: project-controlled black-box observation.
- Source: [stock 1.4.0 loopback observation](fixtures/stock-1.4.0-loopback.md).
- Observed versions: unmodified Ubuntu `mosh-client` 1.4.0 and `mosh-server`
  1.4.0.
- Implementation consequence: this shape may support the smallest Phase 1
  authenticated exchange and resize fixture.
- Limits: later input and resize fixtures name the initial client fields. Host
  screen operations and malformed nested-message behavior remain unresolved.

### Client input and resize operation semantics

- Date: 2026-08-29
- Behavior: the client-to-server state difference is an ordered operation
  history. Operation path `1 → 2 → 4` appends exact input bytes. Operation path
  `1 → 3 → 5/6` sets terminal columns and rows. A difference may retain an
  earlier resize or repeat the new size; the final ordered size is current.
- Evidence class: published protocol description and project-controlled
  black-box observation.
- Source: [Mosh detailed draft](https://mosh.org/mosh-paper-draft.pdf) and the
  [stock 1.4.0 client input/resize fixture](fixtures/stock-1.4.0-client-input-resize.md).
- Observed version: unmodified Ubuntu `mosh-client` 1.4.0.
- Implementation consequence: hand-declare only these verified client
  operation fields. Preserve input as bounded bytes without UTF-8 normalization
  at the transport layer. Apply resize records in order and keep the final
  validated size.
- Limits: the controlled input was `x中`; sizes were 80×24 and 81×25. The
  fixture does not define malformed operations, byte limits beyond local policy,
  host screen operations, prediction, or alternate-screen behavior.

### Project-generated initial stock-server exchange

- Date: 2026-08-29
- Behavior: stock 1.4.0 accepts the project's protocol-version-2 client state
  `0 → 1` initial packet and returns server sequence zero acknowledging client
  state one.
- Evidence class: project-controlled black-box interoperability fixture.
- Source: [stock 1.4.0 project-generated initial exchange](fixtures/stock-1.4.0-server-exchange.md).
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0.
- Implementation consequence: independently encode the verified 80-by-24
  initial nested shape, one exact zlib stream, a single final fragment, and the
  authenticated envelope. Decode the response in the reverse order and require
  protocol version 2.
- Limits: the accepted packet used timestamp zero, absent timestamp reply,
  fragment identifier and number zero, and one fixed zero chaff byte. It proves
  only the first acknowledgement, not synchronization, terminal semantics,
  retransmission, fragmentation, or roaming.

### Stock multi-fragment response and bounded reassembly

- Date: 2026-08-29
- Behavior: stock 1.4.0 emits one compressed server instruction across multiple
  numbered fragments. The project decoder reconstructs that instruction after
  reverse-order delivery. Omitting fragment zero leaves no decodable
  instruction and the retained remainder expires at the local 10-second bound.
- Evidence class: project-controlled black-box interoperability fixture plus
  original deterministic resource and conflict tests.
- Source: [stock 1.4.0 multi-fragment response](fixtures/stock-1.4.0-multifragment.md).
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0.
- Implementation consequence: group authenticated fragments by identifier;
  concatenate only a complete zero-through-final range; make exact duplicates
  idempotent; discard contradictory groups; and clear incomplete state on
  expiry or owner cleanup.
- Limits: the project retains at most 749 fragments, one incomplete identifier,
  and 1 MiB total for 10 seconds from the first fragment. These are local
  defensive policies. The fixture does not establish stock retransmission,
  identifier reuse, malformed conflict handling, terminal semantics, or
  full-session recovery.

### SSP synchronization state selection

- Date: 2026-08-29
- Behavior: the sender targets its latest state, uses a recent unexpired sent
  state as the assumed receiver reference, falls back to the known acknowledged
  state after RTO, and sends empty-difference states for acknowledgements. The
  receiver constructs targets only from saved references, keeps the greatest
  constructed state as current, and discards references below the sender's
  throwaway number.
- Evidence class: public protocol paper plus the project-controlled initial
  stock exchange.
- Source: [Mosh detailed draft](https://mosh.org/mosh-paper-draft.pdf),
  [Mosh final paper](https://mosh.org/mosh-paper.pdf), and the
  [initial exchange fixture](fixtures/stock-1.4.0-server-exchange.md).
- Observed version: published SSP design and unmodified Ubuntu
  `mosh-server` 1.4.0.
- Implementation consequence: keep sender and receiver state transitions
  deterministic; separate plan from commit; accept reordered constructible
  targets once; advance acknowledgements only to retained states actually sent;
  allocate a new state number for an empty acknowledgement; and fall back to
  the known receiver reference after RTO.
- Limits: the sender's 32-state bound follows the paper. The receiver keeps the
  throwaway floor plus the newest 31 references as a local memory bound.
  Unknown acknowledgements, unavailable references, generation exhaustion, and
  stale plans fail or wait without changing authoritative state. Terminal
  differences and broader stock behavior under impaired networks require later
  fixtures.

### SSP datagram timing and client roaming

- Date: 2026-08-29
- Behavior: an outgoing datagram carries the low 16 bits of a monotonic
  millisecond clock. A recent received timestamp is echoed after adding local
  reply delay. Only an in-sequence timestamp reply updates RTT. The client
  keeps the server address fixed; a newer authentic client packet lets the
  server learn a changed client address. An idle heartbeat supplies that packet.
- Evidence class: public protocol paper, IETF retransmission standard, and
  original deterministic simulation.
- Source: [Mosh detailed draft](https://mosh.org/mosh-paper-draft.pdf),
  [RFC 6298](https://www.rfc-editor.org/rfc/rfc6298.html), and the project-owned
  virtual network in `tests/support/network.rs`.
- Observed version: published SSP design. The recovery simulation uses only
  project-generated authenticated packets; it is not a stock-binary claim.
- Implementation consequence: keep integer SRTT and RTTVAR state; start RTO at
  one second; clamp it to the 50 ms Mosh floor and a local 5 s ceiling; pace
  frames at half SRTT within 20–250 ms; collect state for 15 ms; delay an ack no
  more than 100 ms; send a heartbeat after three idle seconds; coalesce expired
  reasons after a clock jump; and reject incoming datagrams whose source is not
  the bootstrapped server endpoint before replay state changes.
- Limits: samples above 60 seconds are ignored as ambiguous under the 16-bit
  clock. A reply that would encode as the `0xffff` no-reply marker is omitted.
  These are conservative local policies pending a focused stock fixture. The
  five-second maximum RTO is also local policy. Deterministic coverage proves a
  dropped authenticated state can be retransmitted after RTO from a changed
  client source address to the same server, acknowledged, and cancelled without
  retained work; real socket and stock-server roaming remain later evidence.

### Stock terminal difference and policy resets

- Date: 2026-08-29
- Behavior: a server terminal difference contains ordered operations. Path
  `1 → 2 → 4` carries a self-contained UTF-8 VT framebuffer patch;
  `1 → 3 → 5/6` carries columns and rows; and observed path `1 → 7 → 8` carries
  a varint echo acknowledgement. Stock initialization resets mouse highlight
  tracking, focus reporting, and urxvt mouse encoding with `CSI ?1001l`,
  `CSI ?1004l`, and `CSI ?1015l`.
- Evidence class: project-controlled black-box interoperability fixture and the
  earlier sanitized field-shape observation.
- Source: [stock terminal-state fixture](fixtures/stock-1.4.0-terminal-state.md)
  and [stock loopback observation](fixtures/stock-1.4.0-loopback.md).
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0.
- Implementation consequence: decode the three known operation shapes, apply
  them in order to a cloned reference state, and retain the latest monotonic
  echo acknowledgement with that state for private prediction confirmation.
  Accept the three verified reset-only policy sequences among otherwise
  unsupported CSI. A full repaint emits the same resets before synthesized
  terminal state. The later Vim fixture below adds one independently observed
  focus-reporting set sequence.
- Limits: the fixture controlled an 81×25 screen and the UTF-8 marker `A中Z`.
  It proved production decode, reference-state application, resize, wide-cell
  preservation, and stock initialization. It did not prove interactive shell,
  editor, alternate-screen, mouse, focus, clipboard, prediction, or sustained
  output behavior.

### Public Session interactive shell and repaint

- Date: 2026-08-29
- Behavior: the public Session API completed a stock 1.4.0 loopback
  Session, painted a controlled interactive shell prompt, delivered a command,
  painted its marker, regenerated that state for a replacement terminal, and
  cancelled without leaving the client task running.
- Evidence class: project-controlled black-box interoperability fixture.
- Source: [Session fixture](fixtures/stock-1.4.0-private-session.md) and
  `stock_1_4_0_public_session_drives_an_interactive_shell`.
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0 on Ubuntu 26.04 WSL
  x86-64.
- Implementation consequence: the Session future owns UDP reads and writes,
  authenticated packet sequencing, fragment identifiers, SSP commits,
  monotonic timers, retained client operations, terminal references, bounded
  output, repaint, and cancellation. The caller owns the runtime and bootstrap
  process.
- Limits: this fixture proves one loopback shell command and repaint. It does
  not establish latency, resize, tmux, editor, alternate-screen, sustained-I/O,
  outage, roaming, multi-Session, HarmonyOS runtime, or crate publication
  readiness.

### Stock tmux, Vim, focus policy, and sustained I/O

- Date: 2026-08-29
- Behavior: one private Session drove tmux attach, two-window navigation,
  detach and reattach, then a Vim full-screen edit, replacement-surface repaint,
  and shell restoration. A second Session preserved 128 ordered shell updates
  and converged after 1,200 output lines while its one-slot display queue
  remained full for 500 ms. Vim's authenticated terminal update contained
  `CSI ?1004h` before the visible editor state.
- Evidence class: project-controlled black-box interoperability fixture and
  local public terminfo data.
- Source: [private Session fixture](fixtures/stock-1.4.0-private-session.md),
  `stock_1_4_0_private_session_supports_tmux_vim_and_full_screen_repaint`, and
  `stock_1_4_0_private_session_preserves_sustained_io_under_output_backpressure`.
- Observed versions: unmodified Ubuntu `mosh-server` 1.4.0, tmux 3.6, and Vim
  9.1.2141 on Ubuntu 26.04 WSL x86-64. The profile was
  `TERM=xterm-256color`, `LANG=C.UTF-8`, 80 columns, and 24 rows. Tmux used an
  isolated socket and no user configuration; Vim used no user configuration,
  swap file, or viminfo.
- Implementation consequence: suppress the verified `CSI ?1004h` focus toggle
  without forwarding it or claiming focus-event support. Continue to reject
  unverified policy sets. Treat alternate-screen compatibility as visible
  full-screen convergence, repaint, and exit restoration; do not require the
  local projection to retain the remote alternate-buffer flag.
- Limits: the fixtures used IPv4 loopback without packet impairment. They do
  not prove focus-event delivery, mouse modes, local alternate-buffer history,
  Session resize, latency, outage recovery, roaming, concurrent Sessions,
  HarmonyOS runtime, or a stable public API.

### Stock Session remote PTY resize

- Date: 2026-08-30
- Behavior: the private Session started at 80×24, then requested 100×30 and
  60×20. The remote shell's `stty size` reported 24×80, 30×100, and 20×60. A
  replacement 60×20 VT projection recovered the final marker from an explicit
  full repaint.
- Evidence class: project-controlled black-box interoperability fixture.
- Source: [private Session fixture](fixtures/stock-1.4.0-private-session.md) and
  `stock_1_4_0_private_session_resizes_the_remote_pty_and_repaints`.
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0 on Ubuntu 26.04 WSL
  x86-64, IPv4 loopback, `TERM=xterm-256color`, and `LANG=C.UTF-8`.
- Implementation consequence: keep resize as an ordered Session command, end
  local prediction, synchronize the remote PTY size, resize the consumer-owned
  surface, and regenerate authority after a surface replacement.
- Limits: the fixture covers three ordinary sizes within existing bounds. It
  does not prove rapid resize storms, concurrent resize and outage, physical
  device layout, or xterm.js integration.

### Stock Session outage, source-port change, and server disappearance

- Date: 2026-08-29
- Behavior: a private Session recovered a command after a test-only relay
  dropped both traffic directions for 1.5 seconds and changed the authenticated
  client UDP source port. Stock 1.4.0 replied to the new source port. After a
  separate server process disappeared, the Session remained locally
  repaintable and cancellable beyond one heartbeat interval.
- Evidence class: project-controlled black-box interoperability fixture.
- Source: [Session recovery fixture](fixtures/stock-1.4.0-session-recovery.md),
  `stock_1_4_0_private_session_recovers_after_outage_and_source_port_change`,
  and
  `stock_1_4_0_private_session_remains_controllable_after_server_disappears`.
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0 on Ubuntu 26.04 WSL
  x86-64, `TERM=xterm-256color`, `LANG=C.UTF-8`, 80 columns, and 24 rows.
- Implementation consequence: keep the bootstrapped server endpoint fixed;
  let newer authenticated client traffic teach the server a changed return
  address. Treat silence as recoverable until explicit lifecycle policy closes
  the Session. Keep impairment transport test-only.
- Limits: the relay changed a loopback source port, not an interface, IP
  address, NAT, or remote network. The fixture does not prove long outage,
  suspend, latency, SSH comparison, server restart, concurrent Sessions,
  HarmonyOS behavior, or a stable public API.

### Stock echo acknowledgement and bounded local prediction

- Date: 2026-08-30
- Behavior: independently encoded client state 2 containing one printable byte
  produced stock transport acknowledgement 2 and terminal echo
  acknowledgement 2. With fixed 40 ms and 80 ms delay in each relay direction,
  non-predictive median visible echo was 108 ms and 189 ms. After three
  authoritative characters established a conservative epoch, predictive
  median visible echo was 0 ms in both delayed cases. A controlled marker
  command subsequently proved authoritative convergence.
- Evidence class: project-controlled black-box interoperability fixture and
  project-generated comparative timing measurement.
- Source: [local-prediction fixture](fixtures/stock-1.4.0-local-prediction.md),
  `stock_1_4_0_echo_acknowledges_the_controlled_input_operation`, and
  `stock_1_4_0_prediction_modes_preserve_convergence_and_adapt_to_latency`.
- Observed version: unmodified Ubuntu `mosh-server` 1.4.0 on Ubuntu 26.04 WSL
  x86-64, IPv4 loopback, `TERM=xterm-256color`, `LANG=C.UTF-8`, 80 columns, and
  24 rows.
- Implementation consequence: use echo acknowledgement as client-state
  evidence for a tentative epoch. Keep authority separate from a projection of
  at most 32 printable ASCII bytes for at most ten seconds. One mismatch or any
  ineligible input clears the projection. Do not infer prediction success from
  a local timeout.
- Architecture comparisons: the
  [Mosh paper](https://mosh.org/mosh-paper.pdf),
  [MoshCatty](https://github.com/binaricat/MoshCatty), and
  [mosh-go](https://github.com/unixshells/mosh-go) informed the options and
  failure analysis only. No third-party wire rule, code, test, or file layout
  was adopted.
- Limits: the measurement covers the private Rust Session and independent VT
  projection on delayed loopback. It does not prove LeanTTY rendering, a
  physical network, jitter, loss, Unicode, backspace, paste, or editor
  prediction.

### Stock authenticated graceful close

- Date: 2026-08-30
- Behavior: a stock client initiated clean shutdown with reserved target state
  `u64::MAX`, an ordinary retained base and difference, then waited for an
  acknowledgement for approximately four seconds. When sent an authenticated
  server close with that target, it replied from a newly allocated ordinary
  local state with `acknowledged_state = u64::MAX`.
- Evidence class: project-controlled black-box interoperability fixtures.
- Source: [stock graceful-close fixture](fixtures/stock-1.4.0-graceful-close.md),
  `stock_1_4_0_client_graceful_close_has_a_reserved_state_shape`,
  `stock_1_4_0_client_acknowledges_a_server_graceful_close`, and
  `stock_1_4_0_client_graceful_close_has_a_bounded_ack_wait`.
- Observed version: unmodified Ubuntu `mosh-client` and `mosh-server` 1.4.0 on
  Ubuntu 26.04 WSL x86-64, IPv4 loopback, `TERM=xterm-256color`, and
  `LANG=C.UTF-8`.
- Implementation consequence: keep the reserved close target out of ordinary
  SSP state and terminal snapshot retention; carry the current bounded client
  difference when initiating; apply a peer's final difference before
  completion; reply with a new ordinary state acknowledging `u64::MAX`; and
  bound a local graceful-close wait to four monotonic seconds.
- Limits: these observations establish the authenticated field shapes and a
  local bounded wait. They do not make silence a close signal, prove peer
  receipt after timeout, classify local UDP errors, or add public reachability
  state.

### Public reachability and silent-peer boundary

- Date: 2026-08-30
- Behavior: established Mosh Sessions survive intermittent connectivity and
  warn when recent server contact or acknowledgement progress is absent.
  Silence does not authenticate a close or prove that a server process ended.
- Evidence class: published protocol design, project-controlled stock-server
  behavior, and physical consumer experiment.
- Source: [Mosh final paper](https://mosh.org/mosh-paper.pdf), the public
  [Mosh technical overview](https://mosh.org/),
  [stock Session recovery fixture](fixtures/stock-1.4.0-session-recovery.md),
  and [LeanTTY physical reachability evidence](fixtures/leantty-physical-reachability.md).
- Observed versions: published SSP design, unmodified stock `mosh-server`
  1.4.0 on Ubuntu 26.04 WSL, and one physical ARM64 HarmonyOS LeanTTY slice.
- Implementation consequence: retain monotonic lifecycle; publish a separate
  latest-value warning after 6.5 seconds without a new accepted latest remote
  state or 10 seconds without acknowledgement progress; recover to responsive
  on fresh authenticated progress; and never complete an active Session from
  silence. Bound an unattached attempt at 15 seconds.
- Architecture comparisons: Stock Mosh, MoshCatty, swift-mosh, Spectty,
  `ssp-transport`, `dart_mosh`, `mosh-go`, and `mosh-dart` informed the public
  state-shape comparison only. No source, test, wire rule, or file structure
  was copied or translated.
- Limits: 6.5 seconds, 10 seconds, and 15 seconds are fixed initial public
  policy, not claims that every compatible implementation must use them.
  Reachability does not prove delivery of the newest input, future packet
  delivery, platform network availability, or server-process existence.

### Public bounded prediction modes

- Date: 2026-08-30
- Behavior: an embedding client may select adaptive, always, or never local
  prediction display per Session; adaptive is the standard default. Prediction
  remains tentative display state until server echo acknowledgement confirms
  its epoch and authoritative terminal state converges. A 2026-08-31 follow-up
  proved through the public Session API that a confirmed `Always` epoch still
  emits its next eligible byte during complete bidirectional UDP loss, while a
  concurrent `Never` Session emits no VT output before recovery.
- Evidence class: public command contract, published protocol design, and
  project-controlled stock-server measurement.
- Source: the public [Mosh usage contract](https://mosh.org/), the
  [Mosh final paper](https://mosh.org/mosh-paper.pdf), and the
  [local-prediction fixture](fixtures/stock-1.4.0-local-prediction.md).
- Observed version: public Mosh behavior, published design, and unmodified
  stock `mosh-server` 1.4.0 on Ubuntu 26.04 WSL x86-64.
- Implementation consequence: export the exact three-value mode, default
  `Session::connect` to adaptive, keep mode and adaptive state per Session, and
  reuse authenticated RTT-derived frame pacing as the slow-link signal. The
  project-owned adaptive policy enables above a 30 ms frame interval, disables
  at or below 20 ms after visible convergence, and temporarily enables after a
  250 ms eligible pending projection. The public full-loss fixture passed the
  existing production implementation, so it requires no predictor, terminal,
  wire, or public-API change.
- Architecture comparisons: stock Mosh, MoshCatty, and `mosh-go` informed the
  public shape and complexity comparison only. No source, test, wire rule, or
  file structure was copied or translated.
- Limits: the mode changes only whether the existing confirmed-epoch ASCII
  projection is displayed. It does not expand prediction eligibility, mutate
  terminal authority, add a wire field, or claim identical stock heuristics.
  The full-loss fixture uses normal stock PTY kernel echo and does not prove
  confirmation in a `stty -echo` user-space echo fixture or on LeanTTY's ARM64
  device path.

### Order-independent host effects and prediction acknowledgement

- Date: 2026-08-31
- Behavior: HostBytes reflecting eligible input and the terminal echo
  acknowledgement for that input are independent operations. They may appear
  in different authenticated differences. Absence of a repeated
  acknowledgement is neutral, and authority may match any ordered prefix of
  the bounded candidate queue while the watermark catches up.
- Evidence class: project-controlled physical consumer experiment, independent
  code-path analysis, deterministic regression, and stock-server
  interoperability fixtures.
- Source: [LeanTTY ARM64 prediction evidence](fixtures/leantty-arm64-prediction.md),
  `acknowledgement_free_authority_preserves_a_confirmed_idle_epoch`,
  `host_effect_before_echo_acknowledgement_preserves_the_tentative_epoch`,
  `tentative_epoch_records_input_beyond_the_first_candidate`,
  `acknowledgement_that_outpaces_host_effect_clears_candidates`,
  `ordered_host_effect_prefixes_and_lagging_acknowledgements_confirm`,
  `acknowledgement_free_authority_clears_only_a_diverged_pending_projection`,
  `stock_1_4_0_prediction_modes_preserve_convergence_and_adapt_to_latency`, and
  `stock_1_4_0_public_prediction_survives_total_udp_loss_after_confirmation`.
- Observed versions: `mosh-client-rs` at `ba4b649` and `383b10a` before the two
  successive fixes, unmodified stock `mosh-server` 1.4.0 on Ubuntu 26.04 WSL
  x86-64, and one physical ARM64 HarmonyOS LeanTTY slice.
- Implementation consequence: the authoritative terminal state retains one
  latest monotonic echo-acknowledgement watermark. Prediction replays at most
  32 candidates from one base to find the longest matching prefix. A watermark
  that covers a matching candidate confirms the epoch; authority may lead it.
  No matching prefix, or a watermark that advances beyond its host effect,
  clears the epoch. Do not add a timeout, public state, acknowledgement history,
  per-candidate screens, or consumer callback.
- Limits: deterministic regressions and generated ordered-prefix cases prove
  the state-machine contract; stock fixtures prove convergence and loss
  behavior. The physical run did not retain an internal acknowledgement trace,
  so it did not alone close the device symptom. The updated physical record
  adds LeanTTY's successful `e1346b3` repeat, with 1 ms public output during
  both delayed delivery and complete UDP loss. This is scoped development
  evidence, not a new wire rule or formal product acceptance.

### Physical local-interface outage and established-Session recovery

- Date: 2026-09-02
- Behavior: after an authenticated Session became active, disabling the real
  WLAN interface produced a local UDP I/O failure and terminated the client in
  about 7.4 seconds while the application, stock server, and remote PTY remained
  alive.
- Evidence class: project-controlled physical consumer experiment and public
  Mosh behavior contract.
- Source: [LeanTTY physical local-send recovery evidence](fixtures/leantty-physical-local-send-recovery.md),
  the public [Mosh technical overview](https://mosh.org/), and Rust's public
  [`std::io::ErrorKind`](https://doc.rust-lang.org/std/io/enum.ErrorKind.html)
  category contract.
- Observed versions: `mosh-client-rs` `e1346b3`, unmodified stock
  `mosh-server` 1.4.0, and one ARM64 HarmonyOS PC with its real WLAN interface
  disabled through system UI.
- Implementation consequence: in an established Session, classify only
  `NetworkDown`, `NetworkUnreachable`, `HostUnreachable`, and
  `AddrNotAvailable` send errors as temporary; preserve Session authority,
  leave failed plans uncommitted, and retry through the bounded existing
  scheduler. Keep other I/O errors fatal and keep reachability independent.
- Limits: the physical artifact retained the stable local-I/O category but not
  the exact `ErrorKind`. The allowlist follows the narrow standard categories
  whose meanings match interface, route, host-route, and local-address loss.
  LeanTTY's updated record closes the same physical case on `94f1322` after
  about 9.7 seconds offline, with the same Session and remote PTY. It adds no
  observed failing category; broad or platform-specific errors still require
  separate evidence.

### Physical lid-close permission failure in an established Session

- Date: 2026-09-10.
- Behavior: about one second after a physical HarmonyOS PC lid close hid the
  application window, an established Session ended on
  `ErrorKind::PermissionDenied` while the application process, stock server,
  and remote PTY remained alive.
- Evidence class: project-controlled physical consumer experiment, Rust's
  public error-category contract, and upstream architecture comparison.
- Source:
  [LeanTTY lid-close diagnostic](fixtures/leantty-physical-lid-permission-denied.md),
  Rust's [`std::io::ErrorKind`](https://doc.rust-lang.org/std/io/enum.ErrorKind.html),
  the public [Mosh behavior contract](https://mosh.org/), and the stock client's
  [`stmclient.cc`](https://github.com/mobile-shell/mosh/blob/mosh-1.4.0/src/frontend/stmclient.cc)
  and
  [`network.cc`](https://github.com/mobile-shell/mosh/blob/mosh-1.4.0/src/network/network.cc)
  as comparison evidence only.
- Observed versions: `mosh-client-rs` `v0.1.0`, unmodified stock
  `mosh-server` 1.4.0, and one HAD-W32 ARM64 HarmonyOS PC running
  OpenHarmony 6.1.1.135.
- Implementation consequence: after `Active`, recover the five explicitly
  listed local I/O categories at both send and receive boundaries. Keep send
  plans uncommitted; delay repeated receive polling through the existing RTO
  cadence; retain the same socket and protocol state; require authenticated
  progress before reporting recovery.
- Architecture comparison: the stock client keeps running after network
  exceptions and applies a short delay. Project code, tests, wording, and file
  organization remain independently written.
- Limits: the physical record does not identify send versus receive, raw errno,
  or platform policy. It does not prove every permission failure is temporary.
  Initial failures and unlisted error categories remain fatal, and a permanent
  denial remains interrupted rather than successful. A later `ae86bfe` physical
  lid run followed HarmonyOS's client-process replacement branch and captured
  no `PermissionDenied`; it verified consumer workspace restoration, not this
  library's same-Session recovery. That physical closing gate remains pending.

### Physical client address change after WLAN switching

- Date: 2026-09-04; imported on 2026-09-05.
- Behavior: a real Wi-Fi switch changed the client's IPv4 address and route;
  the public Session recovered and executed new input in the same remote PTY.
- Evidence class: project-controlled physical consumer experiment.
- Source: [LeanTTY network-switch record](fixtures/leantty-physical-network-switch.md).
- Observed versions: library `94f1322`, stock `mosh-server` 1.4.0, and one
  ARM64 HarmonyOS HAD-W32 PC with two controlled LAN paths.
- Implementation consequence: corroborates the existing fixed-server-endpoint
  and authenticated client-roaming contract; no code or wire rule changes.
- Limits: retains the scoped consumer result and cleanup status, not raw
  addresses, SSIDs, secrets, packets, or a formal LeanTTY release verdict.

### Stock terminal-control batch compatibility

- Date: 2026-09-26.
- Behavior: stock `mosh-server` 1.4.0 sends OSC 0/1/2 title text containing
  semicolons, SGR blink/hidden, and DEC whole-screen reverse video during
  ordinary shell output. The client must retain these controls in the
  authoritative screen and synthesized paint so a subsequent command remains
  usable. Stock output normalizes an OSC ST terminator to BEL in the selected
  case. Raw ST handling remains a separate input boundary.
- Evidence class: LeanTTY's recorded physical diagnosis as a lead, independent
  local raw-input scans, and project-controlled black-box stock-server public
  Session fixtures.
- Source: [batch fixture](fixtures/stock-1.4.0-terminal-compatibility-batch.md)
  and [LeanTTY PR #264](https://github.com/wandcs/leantty/pull/264).
- Observed versions: `mosh-client` 0.1.3 baseline, stock `mosh-server` 1.4.0,
  `TERM=xterm-256color`, `LANG=C.UTF-8`, and the default WSL distribution.
- Implementation consequence: rejoin OSC 0/1/2 title segments without
  changing OSC 52 validation; store blink and hidden as cell attributes and
  reverse video as a screen mode, and emit both in full and incremental paint.
- Limits: mouse 1001/1015, malformed OSC 52, more than eight scalars per cell,
  and overlarge encoded cells retain hard failure. The fixture verifies one
  server version and locale, not arbitrary binary output or physical
  HarmonyOS acceptance.

### Stock cell-width compatibility profile

- Date: 2026-09-25.
- Behavior: U+0605 is a spacing cell in the stock 1.4.0 C.UTF-8 session,
  while `unicode-width` 0.2.2 reports zero. The stock public Session also
  carries selected characters from the other finite-width disagreement
  classes without disconnecting.
- Evidence class: project-controlled black-box stock-server fixture,
  local exhaustive width-table comparison, and sanitized LeanTTY physical
  diagnosis.
- Source: [cell-width fixture](fixtures/stock-1.4.0-cell-width.md) and
  [LeanTTY PR #263](https://github.com/wandcs/leantty/pull/263).
- Observed versions: stock `mosh-server` 1.4.0, Ubuntu glibc 2.43
  `C.UTF-8`, `unicode-width` 0.2.2 with `cjk`, and local
  `mosh-client` 0.1.2 development revision.
- Implementation consequence: apply one explicit width policy in input
  validation and private screen cells. Retain the eight-scalar and
  22-byte cell bounds, malformed UTF-8 rejection, and unsupported-control
  rejection. Avoid runtime libc coupling to the consumer platform.
- Limits: 95 finite-width mismatches were observed locally; stock
  interoperability verifies representative samples, not every scalar
  in every locale or server libc. Unassigned code points were excluded.
  Physical acceptance of this revision remains with LeanTTY.

### Stock replacement-character terminal update

- Date: 2026-09-25.
- Behavior: stock `mosh-server` 1.4.0 sends a visible U+FFFD both for an
  explicit UTF-8 replacement character and after receiving one invalid remote
  byte. It also carries selected Latin, CJK, emoji, and combining text through
  the public Session. The client must retain these and remain usable.
- Evidence class: project-controlled black-box stock-server fixture and
  sanitized LeanTTY consumer diagnosis.
- Source: [replacement-character fixture](fixtures/stock-1.4.0-replacement-character.md)
  and LeanTTY's `mosh-replacement-character-diagnosis-20260925.md`.
- Observed versions: stock `mosh-server` 1.4.0, `vt100` 0.16.2, and the local
  `mosh-client` 0.1.1 development revision.
- Implementation consequence: process valid printable Unicode through the same
  authoritative screen and paint path, without a U+FFFD exception. Reject DEL
  explicitly instead of accepting and silently dropping it after text. Do not
  relax malformed UTF-8 or unsupported control handling.
- Limits: the fixture covers selected outputs, later input, resize, and repaint.
  It does not prove all arbitrary binary streams, every Unicode grapheme, or
  physical HarmonyOS behavior.
