# Stock Mosh 1.4.0 Session isolation fixture

> Observation date: 2026-08-30
>
> Environment: Ubuntu 26.04 WSL x86-64, stock `mosh-server` 1.4.0,
> IPv4 loopback

## Method

The ignored test started two public `Session` instances against independent
stock servers through separate project-owned UDP relays. It captured the latest
server ciphertext for Session A and injected it into Session B from B's expected
relay endpoint. The test then interleaved unique shell markers, resized the
remote PTYs to 90 x 25 and 70 x 20, and rebuilt both VT projections through
explicit full repaint.

The test cancelled A, drained its bounded output queue, and injected the old A
packet again. It waited beyond the three-second heartbeat interval and required
B to emit traffic and accept new input. It then started a replacement Session
with a third stock server, injected the old A packet from the replacement's
expected relay endpoint, and required the replacement to remain usable. Finally,
it dropped B's public owner, required `OwnerDropped`, proved the replacement
still accepted input, and cancelled it normally.

Run the case serially with:

```bash
cargo test --lib \
  session::tests::stock::recovery::stock_1_4_0_public_sessions_isolate_packets_state_and_lifecycle \
  --all-features -- --ignored --exact --nocapture --test-threads=1
```

## Result

Wrong-key ciphertext did not stop or alter Session B or the replacement. Unique
input, output, terminal dimensions, repaint state, heartbeat traffic, task
errors, cancellation, and cleanup remained isolated. A closed Session emitted
no output after late packet injection. Cancelling A and dropping B did not stop
the other active lifecycle.

The relay retained one ciphertext datagram in test memory only. It did not log
or persist a key, credential, bootstrap secret, packet capture, host name, user
path, or uncontrolled shell output. Process guards cleaned up all three stock
servers after success or failure.

## Limits

Distinct-key behavior is inferred from authenticated ciphertext rejection
through the public API; the test does not expose or compare secret bytes. The
fixture uses loopback endpoints, not physical interfaces, NAT, roaming, suspend,
HarmonyOS, or LeanTTY. It does not cover every cancellation boundary, long
outages, resource use, or concurrent rendering in an embedding application.
