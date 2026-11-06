# Stock Mosh 1.4.0 client input and resize operations

> Fixture identifier: `stock-1.4.0-client-input-resize`
>
> Observation date: 2026-08-29
>
> Author: project team

## Environment and method

- Binary: unmodified Ubuntu `mosh-client` 1.4.0
- Platform: Ubuntu 26.04 WSL, IPv4 loopback
- Terminal: `xterm-256color`, `C.UTF-8`, initially 80 columns by 24 rows
- Key: fixed public test data `4NeCCgvZFe2RnPgrcU1PQw`
- Server: none; a project-owned local UDP socket receives client packets

Two ignored Rust unit fixtures launch the stock client in a controlled pseudo
terminal. The production packet, fragment, zlib, and top-level instruction
decoders authenticate and decode every observed packet. Test-only Prost types
then decode only the already observed nested field paths.

The input case writes the controlled UTF-8 bytes for `x中` to the pseudo
terminal. The decoded client difference contains the same bytes, in order, at
this path:

```text
repeated operation field 1
  input operation field 2
    bytes field 4 = UTF-8 bytes for x中
```

The resize case changes the pseudo terminal from 80×24 to 81×25. Both initial
and later size records use this path:

```text
repeated operation field 1
  resize operation field 3
    columns field 5
    rows field 6
```

The later difference may retain the initial size and may repeat the new size
because the controlled `stty` change and `SIGWINCH` can each produce a resize
event. The fixture therefore asserts the semantic contract: only the two
controlled sizes occur, and the final size is 81×25. It does not require stock
Mosh to coalesce duplicate resize notifications.

## Reproduction

```bash
cargo test --lib stock_1_4_0_client_input_difference_contains_exact_utf8_bytes \
  -- --ignored --nocapture

cargo test --lib stock_1_4_0_client_resize_reuses_the_terminal_size_operation \
  -- --ignored --nocapture
```

The fixtures require local `mosh-client` 1.4.0 and util-linux `script`. They
contact no server or remote service. They terminate the exact process group
they launch and retain no datagram, terminal transcript, process output, or
session secret.

## Established contract and limits

The client-to-server synchronized object is an ordered history of input and
resize operations. Input bytes are not normalized or decoded by the transport.
Resize records carry typed column and row values. Replaying or retaining more
than one resize record does not change their ordered meaning.

These fixtures do not establish server-to-client screen operations, local
prediction, malformed nested-message behavior, alternate screen, fragmentation,
or a usable shell Session.
