# Stock 1.4.0 local-prediction fixture

- Date: 2026-08-30
- Oracle: unmodified Ubuntu `mosh-server` 1.4.0
- Environment: Ubuntu 26.04 WSL x86-64, IPv4 loopback
- Terminal: `TERM=xterm-256color`, `LANG=C.UTF-8`, 80 columns, 24 rows
- Remote service: none

## Scenarios

`stock_1_4_0_echo_acknowledges_the_controlled_input_operation` sends an
independently encoded initial resize as client state 1, then one printable byte
as client state 2. Authenticated stock output contains transport
acknowledgement 2 and terminal echo acknowledgement 2.

`stock_1_4_0_prediction_modes_preserve_convergence_and_adapt_to_latency`
starts separate stock servers and Sessions for `Never`, `Always`, and
`Adaptive`. A test-only UDP relay applies a fixed delay in both directions. The
fixture sends `latencyprobe` one byte at a time and measures from Session input
submission until each character is visible in an independent VT projection.

| One-way delay | Mode | Samples, ms | Median |
| ---: | --- | --- | ---: |
| 0 ms | `Never` | 28, 28, 21, 27, 27, 21, 26, 28, 22, 27, 28, 20 | 27 ms |
| 0 ms | `Always` | 29, 28, 22, 27, 28, 22, 27, 26, 24, 26, 24, 26 | 26 ms |
| 0 ms | `Adaptive` | 29, 27, 21, 26, 28, 22, 27, 27, 23, 27, 25, 24 | 27 ms |
| 40 ms | `Never` | 108, 108, 109, 108, 107, 108, 108, 107, 107, 109, 108, 108 | 108 ms |
| 40 ms | `Always` | 108, 108, 108, 0, 0, 0, 0, 0, 0, 0, 0, 0 | 0 ms |
| 40 ms | `Adaptive` | 108, 108, 107, 0, 0, 0, 0, 0, 0, 0, 0, 0 | 0 ms |
| 80 ms | `Never` | 188, 187, 251, 190, 249, 188, 250, 188, 250, 189, 250, 187 | 190 ms |
| 80 ms | `Always` | 187, 189, 252, 0, 0, 0, 0, 0, 0, 0, 0, 0 | 0 ms |
| 80 ms | `Adaptive` | 189, 188, 252, 0, 0, 0, 0, 0, 0, 0, 0, 0 | 0 ms |

After measurement, each Session sends a control input and a marker command.
The fixture waits for `LATENCY_AUTHORITY_CONVERGED`, then cancels the Session
and terminates the detached stock server.

`stock_1_4_0_public_prediction_survives_total_udp_loss_after_confirmation`
starts concurrent public `Always` and `Never` Sessions through independent
40 ms one-way relays. It uses the stock PTY's normal kernel echo and accepts an
epoch as confirmed only after public VT output makes one warmup byte visible in
less than the one-way relay delay. It then lets authority settle, pauses both
relay directions, drains packets already in flight, and submits one printable
byte to each Session.

In the recorded 2026-08-31 run, the fourth `Always` warmup byte proved the hot
epoch with 0 ms visible latency. During complete UDP loss, the next `Always`
byte was also visible in 0 ms before the relay delivered it to the server;
`Never` emitted no VT output before recovery. Each relay dropped two datagrams.
After relay recovery, both Sessions reached distinct stock-authoritative marker
screens and completed independent cancellation.

## Established behavior

- Stock echo acknowledgement identifies the controlled client state.
- The relay affects both directions; every `Never` sample is at least
  the configured round-trip delay.
- `Adaptive` remains authoritative on the low-delay link and activates on both
  delayed links. `Always` uses the same confirmed-epoch safety gate without the
  link-delay display gate.
- Conservative prediction leaves the first three delayed samples authoritative
  in this run, then removes network delay from the visible hot epoch.
- Predicted input still reaches and converges with the stock server.
- A confirmed `Always` epoch continues to produce public VT output while both
  UDP directions are completely blocked. `Never` remains authoritative-only.
- The outage assertion runs after relay in-flight traffic is drained and proves
  from relay counters that the predicted byte had not reached the stock server.
- Concurrent `Always` and `Never` Sessions retain separate prediction, packets,
  terminal state, output, convergence, and lifecycle.

## Limits

The result measures the Rust Session and VT projection on loopback. It
does not include LeanTTY, N-API, ArkTS, WebView rendering, a physical device,
real WAN jitter, loss, reordering, Unicode prediction, paste, backspace, or
interactive editor prediction. The full-loss scenario uses normal stock PTY
kernel echo; it does not validate a `stty -echo` user-space echo fixture or
prove that such a fixture established an echo-confirmed epoch. Timing samples
are evidence for the relative gate, not a universal performance guarantee.
