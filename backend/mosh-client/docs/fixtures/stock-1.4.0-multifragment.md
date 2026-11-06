# Stock Mosh 1.4.0 multi-fragment response

> Fixture identifier: `stock-1.4.0-multifragment-response`
>
> Observation date: 2026-08-29
>
> Author: project team

## Environment and method

- Binary: unmodified Ubuntu `mosh-server` 1.4.0
- Platform: Ubuntu 26.04 WSL, IPv4 loopback
- Server command: `head -c 32768 /dev/urandom | base64; sleep 3`
- Terminal profile: `xterm-256color`-compatible initial state, 200 columns by
  50 rows
- Port range: test-local `60200:60299`

An ignored Rust fixture launches the released stock server and sends the
project-generated initial client packet. The controlled command produces
high-entropy, non-secret terminal output so compression cannot collapse the
response into one fragment. Project production code authenticates each server
datagram and validates its fragment envelope.

The fixture groups an actual multi-fragment server instruction by identifier,
then feeds its fragments to the production reassembler in reverse number order.
Completion must produce one zlib stream containing a protocol-version-2
instruction with a nonempty server state difference.

A separate reassembler receives the same authentic group without fragment zero.
It must retain one incomplete instruction without exposing partial bytes,
remain pending at 9,999 monotonic milliseconds, and release all retained bytes
at the 10,000-millisecond expiry boundary.

The fixture clears bootstrap output and packet buffers and terminates the exact
detached server process. It retains no key, datagram, terminal transcript,
generated random output, user path, host name, or credential.

## Reproduction

```bash
cargo test --lib stock_1_4_0_multifragment_response_reorders_and_expires_after_loss \
  -- --ignored --nocapture
```

The command requires local `mosh-server` 1.4.0 and coreutils. It performs no
download and contacts no remote host.

## Proven property and limits

This fixture proves that stock 1.4.0 emits a real instruction across multiple
fragments and that this project reconstructs it independently of arrival order.
It also proves that a missing fragment cannot yield partial decoder input and
that retained authentic bytes are released at the local expiry boundary.

It does not prove stock retransmission, stock identifier generation or reuse,
malformed conflict behavior, server-to-client terminal semantics, recovery of a
complete Session, or a maximum stock instruction size. Concurrency, byte,
fragment-count, duplicate, conflict, and expiry limits remain explicit local
security policies.
