# Stock Mosh 1.4.0 project-generated initial exchange

> Fixture identifier: `stock-1.4.0-server-initial-exchange`
>
> Observation date: 2026-08-29
>
> Author: project team

## Environment and method

- Binary: unmodified Ubuntu `mosh-server` 1.4.0
- Platform: Ubuntu 26.04 WSL, IPv4 loopback
- Server command: `/bin/sh`
- Terminal profile: `xterm-256color`-compatible initial state, 80 columns by
  24 rows
- Port range: test-local `60100:60199`

The ignored Rust fixture launches the released stock server and parses its
ordinary bootstrap result. The synchronization state machine plans client state
`0 → 1`; project production code places the verified initial terminal
difference in that plan, compresses it as one zlib stream, wraps it in one final
fragment, authenticates it as client sequence zero, and sends it over a
loopback UDP socket.

The stock server accepted the packet and returned server sequence zero. Project
code authenticated the response, decoded one final fragment, consumed exactly
one zlib stream, decoded a protocol-version-2 instruction acknowledging client
state one, and advanced the state machine's known receiver state to one.

The client packet used timestamp zero, no timestamp reply, fragment identifier
zero, fragment number zero, and one fixed zero byte of public test chaff. These
values are retained as structural test inputs, not as a claim about later
Session timing or chaff policy.

The fixture retains no bootstrap key, ciphertext, plaintext terminal content,
raw packet, user path, host name, or credential. It explicitly clears temporary
bootstrap output and packet buffers, then terminates the exact detached server
PID with a bounded `TERM` and `KILL` cleanup path.

## Reproduction

```bash
cargo test --lib stock_1_4_0_server_accepts_the_project_initial_packet \
  -- --ignored --nocapture
```

The command requires a locally installed `mosh-server` 1.4.0. It performs no
download and contacts no remote host.

## Proven property and limits

This fixture proves a complete smallest authenticated exchange in both
directions: the stock server accepts this project's packet, and this project
accepts the stock response. It verifies the initial size shape, zlib wrapper,
single-fragment envelope, OCB packet encoder and decoder, direction bits,
sequence zero, and initial acknowledgement.

It does not prove retransmission, multiple fragments, later state differences,
input, terminal output, roaming, malformed-message behavior, or a usable shell
Session.
