# LeanTTY ARM64 prediction evidence

- Date: 2026-08-31
- Consumer: LeanTTY native Mosh owner using the public `mosh-client-rs` Session
- Library revision: `ba4b649`
- Device: physical ARM64 HarmonyOS PC
- Peer: unmodified stock `mosh-server` 1.4.0 with `/bin/sh -i`
- Terminal: normal PTY kernel echo, printable ASCII input only
- Network: independent bidirectional relay with 40 ms delay in each direction

## Corrected physical scenario

LeanTTY submitted `abcdefghijk` one byte at a time through
`Session::connect_with_prediction_mode(..., PredictionMode::Always)` and
consumed each `Session::next_output` result before submitting the next byte.
The fixture used no Enter, control input, resize, repaint, or `stty -echo`
user-space echo path.

The first ten visible warmup latencies were 107, 151, 127, 119, 157, 119, 118,
116, 145, and 142 ms. None was below the 40 ms one-way relay delay. Direct
timing of the native public Session output was 116–139 ms; the N-API and ArkTS
boundary added 0–2 ms and rendering added 1–2 ms. Removing a duplicate initial
resize and changing the test PTY and relay implementation did not change the
result.

This confines the observed delay to the library path or below it. It does not
by itself name an internal transition because the device record contains no
echo-acknowledgement trace.

LeanTTY evidence identifier:
`device-mosh-20260830T193121458Z/device-mosh.json`.

## Independently reproduced state-machine defect

Code-path analysis found that every authenticated terminal difference calls
the predictor with only the echo acknowledgement present in that difference.
The pre-fix predictor treated `None` as divergence and cleared the confirmed
epoch unconditionally. A deterministic test confirmed that a previously
confirmed idle epoch was lost after an otherwise unchanged authenticated
update without a repeated acknowledgement. A second test establishes the safe
boundary: an unchanged pending base is neutral, while a changed authoritative
screen clears the projection.

After the bounded fix, the prediction unit suite passed. The stock 1.4.0
latency fixture still produced 0 ms hot-epoch output with 40 ms and 80 ms
one-way delays, and the public full-loss fixture still emitted the confirmed
`Always` byte in 0 ms while `Never` emitted nothing before recovery.

## Historical closure condition

LeanTTY must pin the fixed revision and repeat the corrected ARM64 scenario.
At least one byte after confirmed warmup must become visible before the 40 ms
one-way relay delay, followed by authoritative convergence. Until then, the
library defect is reproduced and fixed, but it is not proved to be the sole
cause of the original physical-device symptom.

## Closure recorded on 2026-08-31

LeanTTY pinned `e1346b3dfce5c38b95ef43d78cfb3d73529f00e5` and repeated
the normal-kernel-echo, public-Session fixture on HAD-W32 with two independent
40 ms relay directions. Against a 128 ms RTT baseline, the second `Always`
warmup byte appeared in public VT output in 1 ms and rendered in 2 ms. During
complete bidirectional UDP loss, the next eligible byte appeared in 1 ms and
rendered in 3 ms; the relay dropped 13 datagrams. The `Never` comparison waited
134 ms for its first byte.

Authoritative convergence, two-Session isolation, authenticated close, a final
real-shell command, exact input, Preferences, secret checks, and cleanup all
passed. The retained test-HAP SHA-256 was
`d2bbc45eeb18dcd625ac2c032789a0b7e3ee327894dce1b6a98347d5f03dae65`.
The evidence identifier is `device-mosh-20260831T042441659Z/device-mosh.json`.

Source: LeanTTY's
[MCRS-006 record](https://github.com/wandcs/leantty/blob/ff86789caba01257e2d0865d63a152647a3125f9/docs/design/mosh-client-rs-integration-issues.md).
This imported development result closes the library's ARM64 prediction symptom;
it has `acceptanceEligible=false` and does not certify a LeanTTY release.

This record retains no session key, credential, host address, packet capture,
or terminal transcript.
