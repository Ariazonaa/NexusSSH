# Stock Mosh 1.4.0 fixed-key client packet

> Fixture identifier: `stock-1.4.0-client-packet`
>
> Observation date: 2026-08-29
>
> Author: project team

## Environment and method

- Binary: unmodified Ubuntu `mosh-client` 1.4.0
- Platform: Ubuntu 26.04 WSL, IPv4 loopback
- Terminal: `xterm-256color`, `C.UTF-8`, 80 columns by 24 rows
- Key: fixed public test data `4NeCCgvZFe2RnPgrcU1PQw`

The ignored Rust unit fixture binds an ephemeral loopback UDP socket, launches
the stock client in a controlled pseudo-terminal, and receives its first
datagram. The production packet decoder authenticates and opens the datagram
with the decoded public test key. The fixture asserts client-to-server direction,
sequence zero, and non-empty authenticated plaintext.

The fixture prints and stores neither the datagram nor its plaintext. The key is
public test data, not a generated Session secret. The test terminates the exact
process group it launched and contacts no remote host.

## Reproduction

```bash
cargo test --lib stock_1_4_0_client_packet_opens_with_the_fixed_public_test_key \
  -- --ignored --nocapture
```

The fixture requires local `mosh-client` 1.4.0 and util-linux `script`. It fails
with a named prerequisite error when either is unavailable.

## Limits

This fixture proves that the decoder accepts one real stock client packet under
the fixed key. It does not prove that stock accepts this project's outbound
packets, nor does it establish fragmentation, synchronization, roaming, or
terminal semantics.
