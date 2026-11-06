# Dependency assessment

> Assessment date: 2026-08-30
>
> Status: foundation and Session runtime dependencies adopted; the OCB3 exception is accepted in
> [ADR 0004](decisions/0004-rustcrypto-ocb3-security-exception.md). The
> authenticated-packet, bounded-codec, and terminal graphs have been adopted
> and verified. Later production dependencies require the same lockfile,
> license, advisory, Linux, and ARM64 HarmonyOS checks.

The project reuses narrow foundations and owns Mosh behavior. Version numbers
below identify the assessed baseline; `Cargo.lock` will record the adopted
versions.

Phase 3F re-audited the complete resolved graph, `unsafe` boundaries, sources,
checksums, licenses, advisories, and package assembly. See the
[security and publication review](security-publication-review.md) for the
historical evidence. The [0.1.0 release review](releases/0.1.0.md) records the
2026-09-05 repeat and the maintainer-approved Rust 1.88 minimum. This release
keeps all registry versions and features unchanged; old Rust 1.85 results below
apply only to their dated development baselines.

## Production candidates

| Capability | Candidate | Decision and reason |
| --- | --- | --- |
| Async UDP, timers, channels | Tokio 1 with `net`, `rt`, `sync`, and `time` only | Selected. Mature, already used by LeanTTY, and proven on its `aarch64-unknown-linux-ohos` target. The library creates no runtime and enables neither `full` nor macros. |
| AES-128-OCB3 | `ocb3` 0.2.0 with default features disabled plus `aes` 0.9.1 with `zeroize` | Adopted under ADR 0004. The implementation uses only the high-level in-place AEAD API, a 12-byte nonce, a 16-byte tag, and no associated data. AES is pinned for Rust 1.85. |
| Protocol Buffers | `prost` 0.14.4 with only `derive` and `std` | Adopted. Original Rust message types follow verified field evidence; no upstream schema, `protoc`, or `build.rs` is used. |
| zlib | `miniz_oxide` 0.9.1 with only `with-alloc` | Adopted over `flate2`. A bounded streaming call exposes consumed input, allowing the decoder to enforce both the output cap and exactly one complete zlib stream. The crate has no native build. |
| Secret clearing | `zeroize` 1.9.0 with default features disabled and without derive | Adopted for bootstrap. Wrap the raw 16-byte session key and any owned plaintext secret. Avoid the allocation and derive features and their extra surface. |
| Bootstrap Base64 | `base64` 0.23.1 with default features disabled | Adopted. `decode_slice` needs neither `std` nor allocation. Strictly decode the 22-character unpadded standard alphabet into an existing 16-byte buffer. Disabling `simd-unsafe` keeps this dependency's path safe Rust. |
| Terminal screen and VT paint | Private MIT `vt100` 0.16.2 source | Adopted for the authoritative screen and deterministic full or incremental VT paint. The embedded copy also repairs U+FFFD, stock cell width, title semicolons, blink/hidden attributes, and whole-screen reverse video. See below. No cells enter the public API. |
| VT validation | `vte` 0.15.0 with default `std` only | Adopted directly and used by the private screen source. A separate bounded pass rejects invalid UTF-8, incomplete sequences, and cell content that the screen could otherwise truncate. Do not enable the broader ANSI helper feature. |
| Unicode width | `unicode-width` 0.2.2 | Adopted. Project code uses ordinary `width`, not `width_cjk`. The direct dependency enables `cjk` explicitly to preserve the prior resolved feature graph. The post-0.1.2 repair applies a bounded stock-server width profile before screen mutation. |

## No production dependency yet

The initial client does not need to generate a key or random nonce. The server
supplies the key, and the packet nonce comes from direction and sequence. The
smallest verified exchange uses fixed public test chaff; later chaff policy is
unresolved. Adding `rand` or `getrandom` now would create unused security
surface.

If verified behavior later requires randomness, use `rand` 0.10 with
`default-features = false` and `sys_rng`. That path uses `getrandom` 0.4 and
already builds in LeanTTY. The HarmonyOS target reports `target_os="linux"` and
`target_env="ohos"`, which selects the maintained Linux entropy backend.

## Adopted authenticated-packet graph

The 2026-08-29 lockfile adds `ocb3` 0.2.0, pinned `aes` 0.9.1, `aead` 0.6.1,
`cipher` 0.5.2, `crypto-common` 0.2.2, `dbl` 0.5.0, `ctutils` 0.4.2,
`cmov` 0.5.4, `hybrid-array` 0.4.14, `inout` 0.2.2, `cpubits` 0.1.1,
`cpufeatures` 0.3.1, and `typenum` 1.20.1. The target-complete lockfile also
contains `libc` 0.2.189. This released graph uses `ctutils` and `cmov`, not the
`subtle` dependency anticipated before adoption.

All packages passed the repository's license, source, ban, and RustSec policy.
Project code remains unsafe-free. `base64` forbids unsafe code under the
selected features. RustCrypto's AES path uses reviewed platform intrinsics and
portable constant-time backends; `zeroize` uses volatile writes and optimization
barriers.

The adopted graph passed RFC 7253 and stock-client 1.4.0 differential fixtures,
`cargo deny check`, `cargo audit`, stable and Rust 1.85 format/lint/test checks,
and an `aarch64-unknown-linux-ohos` library build. This verification covers the
authenticated packet envelope, not synchronization or terminal behavior.

## Adopted bounded-codec graph

The same lockfile now adds `miniz_oxide` 0.9.1 with `adler2` 2.0.1 and `prost`
0.14.4 with `bytes` 1.12.1. The derive-only build path adds `prost-derive`
0.14.4, `anyhow` 1.0.104, `itertools` 0.14.0, `either` 1.18.0,
`proc-macro2` 1.0.107, `quote` 1.0.47, `syn` 2.0.119, and `unicode-ident`
1.0.24 at build time. There is no native library, schema compiler, build
script, runtime download, telemetry, or new random source.

The decoder keeps one streaming inflate state, appends bounded 16 KiB output
chunks up to the 2 MiB hard limit, requires the zlib decoder to consume the full
input, and rejects trailing streams or bytes. It does not repeatedly restart
decompression or clear ordinary terminal payload as if it were key material.
The initial hand-declared message types match the independently observed field
shape. A project-generated packet using this graph was accepted by stock
`mosh-server` 1.4.0, and the returned instruction was decoded successfully.
License, advisory, MSRV, and ARM64 OHOS verification all passed on 2026-08-29.
This closes the codec foundation only; it does not validate later
synchronization or terminal message semantics.

## Adopted terminal graph

### 2026-09-25 U+FFFD source repair

LeanTTY and a local stock-server fixture reproduced a defect in `vt100` 0.16.2:
its parser sent valid U+FFFD to `unhandled_char` without updating the screen.
Ignoring the callback would have left the authoritative screen and cursor
wrong. No published `vt100` release contains a verified correction for this
case. The crate now compiles the audited MIT-licensed 0.16.2 screen modules
privately from [`src/vt100`](../src/vt100/PATCH.md), with one source-line change
that prints U+FFFD normally and leaves C1 behavior intact. This adds no crate,
native build, `unsafe` code, or public terminal type.

The external `vt100` dependency is removed. Its existing `itoa` 1.0.18
dependency becomes direct, while `vte` 0.15.0 and `unicode-width` 0.2.2 remain
direct. The `cjk` feature is set explicitly to preserve the prior resolved
Unicode-width behavior. The source files, upstream MIT license, and patch note
are included in the single `mosh-client` source package. A published upstream
fix can replace this copy after the same tests and package checks pass.

At the 0.1.2 release, the copied Rust files were compared against the cached
0.16.2 crate: only `perform.rs` differed, by the stated one-line condition.
The post-0.1.2 cell-width repair also changes `screen.rs` and `cell.rs` to use
the shared policy documented in [the stock cell-width fixture](fixtures/stock-1.4.0-cell-width.md).
The post-0.1.3 terminal-control repair changes `perform.rs`, `attrs.rs`,
`term.rs`, `screen.rs`, and `cell.rs` to retain OSC 0/1/2 titles containing
semicolons and paint SGR blink/hidden and DEC reverse-video state. The
[batch compatibility fixture](fixtures/stock-1.4.0-terminal-compatibility-batch.md)
checks the supported controls and retained rejection boundaries. No new crate,
public type, native build, or `unsafe` code was added.
`cargo deny check`
passed the resolved graph, the imported modules contain no `unsafe`, and Cargo
packaged the source and rebuilt its unpacked crate on 2026-09-25. Stable and
Rust 1.88 strict Clippy and all-target tests passed; the stock-server U+FFFD
fixture passed separately. The installed stable toolchain built the ARM64 OHOS
release library. Rust 1.88 lacks that installed target, so the OHOS result is
not a Rust 1.88 target build.

The original terminal graph added `vt100` 0.16.2, `vte` 0.15.0,
`unicode-width` 0.2.2, `arrayvec` 0.7.8, `itoa` 1.0.18, and `memchr` 2.8.3.
It has no native library,
build script, runtime download, telemetry, or new `unsafe` block. `vt100` has
MSRV 1.70 and `vte` has MSRV 1.62.1, both below the 0.1.0 Rust 1.88 floor.

The project uses `vt100` as a private implementation detail, not a public cell
contract. `vte` verifies that each stock-server paint patch ends in the ground
state and enforces eight Unicode scalars per cell before `vt100` can silently
drop an overlong combining sequence. The screen has no authoritative
scrollback. Full and incremental paint output remain capped at 2 MiB.

License, source, ban, RustSec, Rust 1.85, and ARM64 OHOS checks passed on
2026-08-29. Unit tests cover resize, wide and combining characters, malformed
VT, system-effect isolation, and full and incremental repaint equivalence. A
local unmodified stock `mosh-server` 1.4.0 fixture applied a controlled 81×25
UTF-8 screen update through the production decoder and terminal state. Later
private Session fixtures cover tmux 3.6, Vim 9.1 full-screen behavior, explicit
repaint, and sustained I/O. Focus-event delivery, mouse modes, and broader
terminal identities remain open.

## Adopted Session runtime graph

The private Session driver adopts Tokio 1.53.1 with only `net`, `rt`, `sync`,
and `time`. Default features remain disabled; the crate enables neither
`macros` nor `full`. The driver uses the caller's runtime and owns one UDP
socket, timer set, bounded command receiver, and one-slot output queue. It does
not create a runtime, thread, or global Session registry.

The lockfile adds `tokio` 1.53.1, `mio` 1.2.2, `socket2` 0.6.5, and
`pin-project-lite` 0.2.17; it reuses the existing `libc` package. The graph has
no native library, runtime download, telemetry, or build-time network action.
Platform socket dependencies contain their own operating-system integration;
project code remains unsafe-free.

License, source, ban, RustSec, Rust 1.85, and ARM64 OHOS checks passed on
2026-08-29. A stock 1.4.0 loopback fixture exercised prompt, input, output,
full repaint, cancellation, tmux, Vim, and sustained I/O under output
backpressure. Public API stability, concurrent Session isolation, and broader
runtime acceptance remain Phase 3 work.

## Alternatives rejected

| Choice | Why it loses for this project |
| --- | --- |
| Copy or translate stock schemas and codecs | Violates the independent implementation and licensing boundary. |
| Hand-written AES or OCB3 | Violates the cryptographic-library principle and creates unacceptable review risk. |
| OpenSSL OCB through FFI | Has a stronger general audit history, but adds a native provider, FFI, cross-build, packaging, and HarmonyOS runtime dependency that LeanTTY does not have. |
| Hand-written Protocol Buffers parser | Saves proc-macro dependencies but makes the project own an untrusted-input codec with little product value. |
| `prost-build` and checked-in generated code | Adds a build tool and schema provenance problem without helping the small verified message set. |
| `quick-protobuf` | Smaller and borrowing-friendly, but less maintained and less widely exercised than `prost`; its generator still needs a schema workflow. |
| `flate2` | Mature and ergonomic, but its backend abstraction and streaming surface exceed the bounded one-message need. |
| System zlib or zlib-ng | Adds C toolchains, target packaging, or unsafe optimization for no measured benefit. |
| Full `alacritty_terminal`, WezTerm terminal, or a native renderer model | Large dependency and policy surfaces; they duplicate the screen model and output boundary this library must control. |
| `avt` 0.18 as the production screen | Compact and permissively licensed, but one cell stores one character and cannot preserve the required bounded combining sequence. |
| Local VT escape parser | Saves one small dependency but recreates a mature, fuzzed parser state machine. |

## Accepted OCB3 exception

RustCrypto states that `ocb3` has not received an independent security audit and
has not been thoroughly assessed for constant-time behavior on common CPUs.
No more mature permissive pure-Rust OCB3 crate was found. OpenSSL is the only
credible mature alternative found, and it conflicts with the project's small,
portable HarmonyOS boundary.

The project accepts this residual risk only for Mosh's required AES-128-OCB3
wire primitive. Before adding `ocb3` to production dependencies, the project
must:

- run RFC 7253 vectors and stock-1.4.0 differential vectors;
- use the in-place API with fixed 12-byte nonces and 16-byte tags;
- disable unused allocation and random-generation features;
- pin and review the exact released `ocb3`, `aes`, `aead`, `cipher`, `dbl`,
  `ctutils`, and `cmov` graph;
- run advisory and license checks on every lockfile change;
- verify ARM64 HarmonyOS output against the loopback fixture; and
- keep the raw session key in a zeroizing wrapper and minimize the lifetime of
  AES and OCB key-derived state;
- prevent keys and key-derived state from appearing in `Debug`, errors, logs,
  snapshots, or persisted data; and
- document the exact clearing behavior that the adopted versions provide on
  Session drop.

The raw key uses `Zeroizing<[u8; 16]>`, and AES 0.9.1 enables its `zeroize`
feature. `ocb3` 0.2.0 does not implement zeroization for its private precomputed
L values, so those values remain until memory is reused after drop. ADR 0004
accepts this residual exposure. Project code must not recreate OCB3 to bypass
it. Reassess the choice when RustCrypto improves clearing, a credible
vulnerability affects the graph, or a maintained audited portable alternative
becomes available.

## Development candidates

| Purpose | Candidate | Policy |
| --- | --- | --- |
| Property tests | `proptest` 1.11 | Dev-only; bound case counts and persist minimal regressions. |
| Fuzzing | `cargo-fuzz` and `libfuzzer-sys` | Dev-only in Phase 3; fuzz bootstrap, packet, fragment, protobuf, zlib, terminal, and state transitions. |
| Terminal comparison | Version-locked xterm.js and stock-server visual fixtures | Test-only independent models; disagreement becomes a named compatibility case, not an automatic majority vote. |
| License policy | `cargo-deny` | Check licenses, bans, sources, and duplicate versions after a lockfile exists. |
| Advisories | `cargo-audit` | Check RustSec before phase completion and release; CI may use a cached advisory database. |

## Adopted Phase 3E test graph

Property tests adopt `proptest` 1.11.0 with default features disabled and only
`std`. It is `MIT OR Apache-2.0`, requires Rust 1.85 (below the 0.1.0 floor), and is
compiled only for test targets. The root lockfile adds 18 dev-only packages.
The selected graph omits `fork`, `timeout`, `tempfile`, `rusty-fork`, and the
bit-set feature; generated collections and case counts are bounded in each
property.

The separate unpublished `fuzz/` workspace pins `libfuzzer-sys` 0.4.13. Its
license is `(MIT OR Apache-2.0) AND NCSA`; NCSA is allowed only by
`fuzz/deny.toml`, not by the production dependency policy. The crate brings its
C++ libFuzzer build only to nightly Linux fuzz targets. It is absent from the
production lockfile, stable builds, and ARM64 HarmonyOS artifacts.
`cargo-fuzz` 0.13.2 is a local WSL tool, not a repository dependency or runtime
service. Removing `proptest`, `fuzz/`, and the `cfg(fuzzing)` module restores
the prior test graph without production-code changes.

## Lockfile rules

- Require `bytes >= 1.11.1`; earlier 1.x releases contain
  RUSTSEC-2026-0007.
- Keep default features off unless this document names them.
- Reject git dependencies, native libraries, runtime downloads, telemetry, and
  build-time network access in the initial crate.
- Re-run the audit when a direct dependency, feature, major transitive version,
  MSRV, or HarmonyOS toolchain changes.
