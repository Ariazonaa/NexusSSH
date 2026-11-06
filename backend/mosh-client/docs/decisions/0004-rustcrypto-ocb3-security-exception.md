# ADR 0004: Accept a bounded RustCrypto OCB3 security exception

- Status: accepted
- Date: 2026-08-29

## Context

Stock Mosh requires AES-128-OCB3. RustCrypto provides the narrowest maintained
pure-Rust implementation found, but its maintainers state that `ocb3` has not
received an independent security audit and has not been thoroughly assessed for
constant-time behavior on common CPUs. Its public API also does not expose all
key-derived OCB state for explicit zeroization.

No more mature permissive pure-Rust OCB3 implementation was found. OpenSSL 3.5
provides AES-OCB, but it would add a native provider, FFI, cross-compilation,
packaging, and HarmonyOS runtime boundary that the project and LeanTTY do not
otherwise need.

## Decision

Use `ocb3` 0.2.0 with `aes` 0.9.1 for Mosh's AES-128-OCB3 wire primitive,
subject to the controls below. This decision creates one exception to the
preference for independently audited cryptographic libraries. It does not
weaken the rule for other cryptographic capabilities or permit local
cryptographic implementations.

Before production adoption, the implementation must:

- disable unused allocation and random-generation features;
- pin and review the exact released `ocb3`, `aes`, `aead`, `cipher`, `dbl`,
  `ctutils`, and `cmov` dependency graph;
- use only the in-place API with 12-byte nonces and 16-byte authentication tags;
- pass RFC 7253 vectors and stock Mosh 1.4.0 differential fixtures;
- keep the raw session key in a zeroizing wrapper and minimize the lifetime of
  all key-derived state;
- exclude secrets and key-derived state from `Debug`, errors, logs, snapshots,
  terminal output, and persistent storage;
- document the exact clearing guarantees and residual key-schedule exposure of
  the adopted versions;
- pass license and RustSec checks for the resolved lockfile; and
- pass Linux tests and an ARM64 HarmonyOS build and fixture check.

The implementation must fail closed on authentication errors, reject replay
before interpreting plaintext, and never introduce a fallback cipher.

## Residual risk

The selected OCB3 path lacks an independent audit and a complete documented
constant-time assessment. Session teardown can clear the raw key and owned
secret buffers, but it cannot prove immediate clearing of every internal
key-derived value. The project accepts this residual risk because the protocol
requires OCB3 and the native alternative increases whole-project portability,
deployment, and maintenance risk.

## Reassessment triggers

Reopen this decision when:

- an advisory or credible report affects the selected OCB3, AES, or supporting
  implementation;
- RustCrypto changes its audit, constant-time, zeroization, maintenance, or
  MSRV guarantees;
- a maintained, permissive, independently audited portable OCB3 implementation
  becomes available;
- the selected graph fails Linux or ARM64 HarmonyOS verification; or
- interoperability evidence shows that the selected implementation cannot meet
  the stock Mosh contract.

An adverse security finding blocks release until the project updates,
replaces, or removes the affected path.

## Rejected alternatives

### OpenSSL 3.5

OpenSSL has stronger project-level security governance, but its native provider,
FFI, build, packaging, and HarmonyOS runtime costs are disproportionate for one
wire primitive. Reconsider it only if the pure-Rust path becomes unacceptable
and a bounded HarmonyOS integration proves viable.

### Defer implementation indefinitely

Deferral removes the current dependency risk but prevents a Mosh client. The
accepted controls make bounded progress possible without changing the project
mission.

### Implement AES or OCB3 locally

Local cryptography would violate the project principles and increase security
and review risk.
