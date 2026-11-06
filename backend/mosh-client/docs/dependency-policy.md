# Dependency policy

This policy applies the project's
[mature-foundation principle](project-principles.md#2-reuse-mature-foundations).

Prefer a mature dependency when it solves a defined protocol, security,
portability, or verification problem with less total complexity and risk than
small local code. A smaller local diff alone does not justify adoption.

Before adoption, record:

- exact purpose and rejected alternatives;
- SPDX license and compatibility with `MIT OR Apache-2.0`;
- maintenance and security history;
- transitive dependency count and licenses;
- `unsafe` code and native build requirements;
- support for Linux test hosts and ARM64 HarmonyOS targets;
- input limits, failure behavior, and removal plan.

Record the assessment in [dependency assessment](dependency-assessment.md).
Resolve the exact graph in `Cargo.lock`, then run license and advisory checks.
A source-level security warning from a cryptographic dependency is an explicit
gate, not a routine caveat.

Cryptographic algorithms, secure random generation, secret zeroization, and
Unicode width tables should come from mature, independently audited,
permissively licensed libraries. If a required wire algorithm has no suitable
audited portable implementation, adoption requires an accepted ADR that limits
the exception, records residual risk and compensating controls, and defines
reassessment triggers. An exception never permits a local cryptographic
implementation.

No dependency may silently introduce GPL, AGPL, proprietary code, network
services, telemetry, or runtime downloads.

The initial dependency set also follows these rules:

- disable unused default features;
- prefer pure Rust when it removes a native target boundary without weakening
  correctness;
- use a direct bounded API instead of a larger convenience layer when the
  smaller API is maintained and clearer;
- require `bytes >= 1.11.1`; and
- add no production RNG until verified protocol behavior needs one.
