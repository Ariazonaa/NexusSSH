# Stock Mosh 1.4.0 bootstrap observation

> Fixture identifier: `stock-1.4.0-bootstrap`
>
> Observation date: 2026-08-29
>
> Author: project team

## Environment and method

- Binary: unmodified Ubuntu `mosh-server` 1.4.0
- Platform: Ubuntu 26.04 WSL, IPv4 loopback
- Controlled command class: start `/bin/true` on a fixed local UDP port
- Retained property: output size and line count, the number of connect records,
  literal field shape, decimal port, and encoded-key length and alphabet

The observation captured process output in memory, classified its structure,
and printed only non-secret metadata. The randomly generated key was not
printed, logged, stored in a fixture, or retained after the observation. The
detached server process was terminated and its absence verified.

## Result

The released binary exited its launcher successfully and emitted nine lines
totalling 375 bytes. Exactly one line had this structural form:

```text
MOSH CONNECT <decimal-port> <22-byte-standard-unpadded-base64-key>
```

The selected test port was preserved. The connect line had exactly four fields.
This fixture does not retain or assert surrounding human-readable lines because
they are not part of the bootstrap value contract.

## Reproduction

The ignored integration test launches the same binary on IPv4 loopback, parses
its stdout with the production parser, checks that the selected port range is
preserved, drops the opaque key, and terminates the detached server:

```bash
cargo test --test interoperability -- --ignored --nocapture
```

The test fails if the local binary is absent or is not version 1.4.0. It never
downloads software or contacts a public server.

## Limits

This observation establishes only the public bootstrap record shape for stock
1.4.0. It does not establish UDP authentication, terminal behavior, roaming,
or compatibility with other released versions.
