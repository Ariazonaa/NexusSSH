# Stock Mosh 1.4.0 Session recovery fixture

> Observation date: 2026-08-29
>
> Environment: Ubuntu 26.04 WSL x86-64, stock `mosh-server` 1.4.0,
> IPv4 loopback

## Method

The ignored recovery test placed a project-owned UDP relay between the private
Session driver and an unmodified local stock server. The relay used one fixed
client-facing endpoint and two server-facing UDP sockets with distinct source
ports. Shared test support retains only the latest server ciphertext in memory
for the Session-isolation fixture; the recovery cases do not inspect it.

After a shell marker arrived, the relay dropped both traffic directions. The
test queued another command, held the outage for 1.5 seconds, switched to the
second server-facing source port, and restored delivery. It required the
queued command to arrive, a second command to succeed, the stock server to
reply through both source sockets, and a fresh VT projection to converge from
an explicit repaint.

A second test terminated the detached stock server after the prompt appeared.
It waited 3.5 seconds, beyond the three-second heartbeat interval, and required
the Session to remain alive. The test requested a repaint, consumed ordered
output until a fresh projection converged, then cancelled the Session and
verified cleanup.

Run both cases serially with:

```bash
cargo test --lib session::tests::stock::recovery --all-features \
  -- --ignored --nocapture --test-threads=1
```

## Result

The stock server accepted a newer authenticated client packet from the second
UDP source port and returned traffic to that port. The client retransmitted
the command lost during the outage without changing its configured server
endpoint or losing terminal state. The post-recovery repaint reconstructed the
latest visible marker on a new projection.

Server disappearance did not falsely close the Session. Its local terminal
state remained repaintable, and explicit cancellation completed normally.
The output contract remained ordered: a repaint request did not require a
display revision or promise to bypass chunks already accepted by the queue.

The fixture cleared bootstrap output after parsing and retained no key,
credential, packet capture, user path, host name, or uncontrolled shell output.
A process guard cleaned up the stock server after success or failure.

## Limits

This fixture changes a loopback UDP source port, not a physical interface, IP
address, NAT mapping, or remote network. The 1.5-second outage does not prove
long-outage retention, suspend and resume, latency, power behavior, or recovery
relative to SSH. It does not prove server restart, concurrent Sessions,
HarmonyOS behavior, or LeanTTY lifecycle integration.
