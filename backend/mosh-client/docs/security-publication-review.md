# Phase 3F security and publication review

> Review date: 2026-08-30
>
> Scope: the root crate, the separate fuzz workspace, stock 1.4.0 test support,
> and the package assembled by Cargo

## Current release review

The maintainer authorized 0.1.1 publication on GitHub on 2026-09-10, with Rust
1.88 as the minimum compiler. The [0.1.1 review](releases/0.1.1.md) records its
scope, gates, physical-evidence boundary, and completed public release. The
[0.1.0 review](releases/0.1.0.md) remains the immutable record for that version.
The dependency, secret-handling, and OCB3 risk boundaries remain in force.
Historical test counts and Rust 1.85 results below do not certify the current
revision.

## Historical decision: 2026-08-30

The implementation and security gate is closed. The crate is not ready for a
crates.io release, so `publish = false` remains.

This is a packaging and release-identity decision, not a rejection of the
client core. The original review found no public source or repository metadata.
The maintainer created
[`wandcs/mosh-client-rs`](https://github.com/wandcs/mosh-client-rs) later on
2026-08-30, and the manifest now records that source. Version `0.0.0` remains a
development placeholder, no immutable release tag exists, and publication stays
disabled.

Reconsider publication after the maintainer chooses a release version, tag, and
remaining package metadata. Add those values, review the packaged file list, and
repeat every Phase 3F gate before removing `publish = false`. Phase 4 LeanTTY
integration is separate and does not silently expand this crate's compatibility
claim.

## Dependency and supply-chain audit

The resolved root graph contains 59 registry packages; the fuzz graph contains
50. Every registry entry in both lockfiles has a checksum. `cargo metadata`
reports crates.io as the only non-local source. The production graph has no Git
or path dependency, duplicate package version, native library, runtime
download, telemetry path, or production random-number source.

`prost-derive` is the only adopted procedural macro. Several resolved crates
run small platform or compiler-detection build scripts, but the project owns no
`build.rs` and has no separate build-dependency graph. The separate fuzz
workspace contains its expected C++ libFuzzer build and NCSA license exception;
neither enters the production graph.

The final gate runs `cargo deny` and `cargo audit` independently for the root
and fuzz lockfiles. The root policy continues to allow only Apache-2.0, MIT,
and Unicode-3.0. NCSA remains confined to `fuzz/deny.toml`.

## `unsafe` audit

Project source forbids `unsafe` in both `src/lib.rs` and Cargo lint policy. A
source scan and `cargo-geiger` 0.13.0 report no project `unsafe` block.

The resolved dependencies use `unsafe` inside established implementation
boundaries:

- AES hardware intrinsics and constant-time backends;
- zeroization and fixed-size buffer primitives;
- Tokio, Mio, Socket2, and libc operating-system integration;
- Bytes, ArrayVec, Memchr, and VT parser performance paths; and
- procedural-macro implementation internals used by Prost.

These uses stay inside the dependency graph already accepted in
[the dependency assessment](dependency-assessment.md). `cargo-geiger` completed
its source report but exited nonzero because it treated unscanned README files
and optional-package metadata as warnings. The audit therefore uses its project
result as corroboration, not as a release command.

## Secret handling

The raw 16-byte Session key remains in `Zeroizing` storage from bootstrap
decode until the two crypto contexts are initialized. Phase 3F removed an
unnecessary fixed-array copy during that initialization: `KeyInit` now borrows
the existing key bytes. The raw wrapper is then dropped immediately.

`Bootstrap`, `SessionKey`, `SessionCrypto`, `OpenedPacket`, `TerminalState`, and
`ApplyPlan` redact their sensitive fields. Public errors contain stable
categories, not bootstrap text, keys, datagrams, or terminal bytes. Production
code contains no logging integration and stores no Session value outside
memory.

Stock fixtures use generated or fixed test-only credentials. Phase 3F fixed
the oldest bootstrap integration fixture so it clears captured stdout and
stderr after extracting the key, matching the other stock fixtures. A bounded
repository scan found no private-key block, common cloud access-key form,
GitHub token form, or OpenAI-style secret form. This pattern scan supplements
review; it is not proof that arbitrary text contains no secret.

AES key schedules zeroize on drop under the selected feature. OCB3 still keeps
private precomputed values without a complete zeroization guarantee. The lack
of an independent OCB3 audit and that residual key-derived state remain the
explicitly accepted risk in [ADR 0004](decisions/0004-rustcrypto-ocb3-security-exception.md).

## Final verification

The post-fix gate passed on 2026-08-30:

- stable and Rust 1.85 formatting, Clippy, rustdoc, and the complete ordinary
  suite: 102 passed, 16 environment tests ignored, and no failure on each
  toolchain;
- `untrusted_parsers` and `state_transitions`: 10,000 bounded libFuzzer runs
  each without a crash or invariant failure;
- root and fuzz `cargo deny` and `cargo audit`: all license, source, ban, and
  advisory checks passed;
- all 16 ignored stock 1.4.0 tests: passed serially, including the public
  two-Session isolation scenario;
- `aarch64-unknown-linux-ohos` library build: passed; and
- `cargo package`: assembled 86 files and rebuilt the crate from the package.
  Its missing release-metadata warning is the publication blocker recorded
  above, not a build failure.

### Post-integration lifecycle re-entry

ADR 0008 reopened one bounded API and wire-lifecycle defect after LeanTTY
integration. The 2026-08-30 post-fix gate passed on both the default toolchain
and Rust 1.85: formatting, strict Clippy, rustdoc, and the complete ordinary
suite passed with 109 tests and 21 environment tests ignored. Root `cargo deny`
and `cargo audit` remained clean. Five new close-specific stock 1.4.0 fixtures
and the updated public two-Session isolation fixture passed locally, covering
both close directions, bounded no-ACK behavior, final output, server cleanup,
and isolation. The earlier ARM64 OHOS coexistence result is not reclassified as
device lifecycle evidence; LeanTTY must rebuild and run the updated dependency
in its remaining Phase 4 acceptance.
