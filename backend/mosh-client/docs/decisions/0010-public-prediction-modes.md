# ADR 0010: Public bounded prediction modes

- Status: Accepted
- Date: 2026-08-30

## Context

LeanTTY reproduced a public-API gap after native integration: the standard
Mosh choices `adaptive`, `always`, and `never` could not be represented when
creating a Session. The crate always used the private confirmed-epoch ASCII
predictor admitted by ADR 0005, and its test-only disable path was not a usable
or isolated consumer contract.

The public Mosh command contract defines adaptive prediction as the default,
allows prediction on low-delay links with `always`, and disables local echo
with `never`. The published paper separates tentative prediction from display:
server echo acknowledgement confirms an epoch, while network conditions decide
whether an eligible projection is shown. The project's stock 1.4.0 fixtures
already establish echo acknowledgement, convergence, and a material latency
benefit without requiring the stock client's complete overlay engine.

## Decision

Export the exact three-value `PredictionMode::{Adaptive, Always, Never}` enum.
It is exhaustive so an embedding application must make an explicit choice if a
future public mode changes the contract. Do not expose experimental or
overwrite modes.

Keep `Session::connect` and make it use the standard `Adaptive` default. Add
`Session::connect_with_prediction_mode` for an explicit per-Session choice.
Do not add an options container for one demonstrated setting, a process-wide
environment variable, runtime switching, UI types, or consumer callbacks.

The modes change display policy only:

- `Never` retains no prediction projection and displays only authenticated
  server state.
- `Always` displays every eligible projection after its epoch is confirmed.
- `Adaptive` maintains the same bounded predictor in the background but
  displays it only on a slower link or during a temporary network glitch.

Adaptive policy reuses the authenticated RTT estimator and its existing
20–250 ms frame interval. No RTT sample means no slow-link conclusion. A frame
interval above 30 ms enables display; 20 ms or below disables it after the
currently visible projection converges; 21–30 ms retains the previous choice.
An eligible pending projection that remains unconfirmed for 250 ms temporarily
enables display for that projection. A visible projection is never retracted
solely because an RTT sample changes.

All modes retain ADR 0005's confirmed epoch, authoritative terminal state,
ASCII eligibility, divergence, repaint, resize, cancellation, 32-scalar,
32-byte, ten-second, and single-projection bounds. `Always` never means
unconfirmed or unbounded prediction.

## Evidence and verification

- The public [Mosh usage contract](https://mosh.org/) defines `never`,
  `always`, and adaptive default behavior.
- The [Mosh paper](https://mosh.org/mosh-paper.pdf) supports confirmed epochs,
  server acknowledgement, and adaptive display without making timeout proof of
  correctness.
- Project-controlled stock 1.4.0 measurements independently establish echo
  acknowledgement, bounded projection convergence, and the latency benefit.
- Deterministic tests cover all modes, adaptive hysteresis and glitch timing,
  resource limits, default selection, and per-Session isolation. The stock
  latency fixture covers low and delayed links and authoritative convergence.

Public source and paper descriptions establish behavior only. Project code and
tests remain independently written and do not copy stock or third-party source,
tests, or file organization.

## Consequences

Existing callers still compile. Before this ADR, `Session::connect` behaved
approximately like `Always`; it now follows the standard `Adaptive` default.
Consumers that require the earlier display policy select `Always` explicitly.
This migration is recorded before a published version exists, while LeanTTY
still pins an auditable Git revision.

The public surface grows by one enum and one constructor. Prediction remains a
private display projection and does not enter VT output metadata, lifecycle,
reachability, SSP, or terminal authority.

## Rejected alternatives

### Keep one fixed private behavior

This leaves a demonstrated standard-client setting unrepresentable and forces
LeanTTY either to expose a false option or duplicate Mosh-specific behavior.

### Process environment variable

Environment state is process-wide, cannot isolate concurrent Sessions, and is
appropriate to the stock CLI rather than an embeddable Rust library.

### Full stock prediction overlay

Backspace, Unicode, overwrite, underline, repair counters, and the complete
stock heuristic system exceed the measured need and the accepted bounded
predictor.

### General Session options object

Only one creation setting has crossed the evidence gate. A general container
would add an abstraction for hypothetical settings without reducing current
complexity.
