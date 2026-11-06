# Implementation survey

> Survey date: 2026-08-30

## Purpose and evidence boundary

This survey compares public architecture, API, README, and license information
from representative Mosh clients and terminal libraries. It informs project
boundaries and embedding choices; it does not define wire behavior.

The later [external maintenance evidence](external-maintenance-evidence.md)
audits the complete default-branch, tracker, and release history of MoshCatty,
`dart_mosh`, and `mosh-go`. Keep architectural comparisons here and historical
failure evidence there.

The project may inspect third-party implementations, including GPL-covered
implementations, to compare architecture and tradeoffs. It does not copy or
translate their source, comments, tests, file organization, or distinctive
expression. Third-party implementations may suggest hypotheses, but every wire
or compatibility rule still needs independent evidence recorded in
[provenance](provenance.md). Stock Mosh source remains excluded from protocol
derivation; released stock binaries serve as black-box interoperability oracles.

The projects below differ greatly in maturity and scope. Their inclusion does
not endorse their security, correctness, or suitability as dependencies.

## Mosh implementations

Only stock Mosh qualifies as a mature Mosh implementation in this survey. The
other projects provide useful independent comparisons, but their age, adoption,
release history, or interoperability evidence is too limited to make them
protocol authorities or production dependencies.

| Project | Language and license | Maturity signal | Public output/runtime shape | Project consequence |
| --- | --- | --- | --- | --- |
| [Stock Mosh](https://mosh.org/) | C++, GPL-3.0+ with documented exceptions | Mature, widely deployed, released 1.4.0 baseline | Standalone client and server synchronize the latest visible screen; intermediate frames may be skipped | Use released `mosh-server` 1.4.0 only as a black-box oracle; do not use source as implementation input |
| [swift-mosh](https://github.com/wiedymi/swift-mosh) | Swift, MIT | Emerging 0.1 project with limited adoption | `MoshClientSession` exposes async `start`, `enqueue`, `hostOpStream`, and `stop`; display consumers receive `MoshHostOp.hostBytes` | Shows that a small session loop and byte output are possible; verify every behavior independently |
| [mosh-go](https://github.com/unixshells/mosh-go) | Go, MIT | New project with claimed stock interoperability but limited history | Main client uses `Dial`, `Send`, `Recv`, and internal loops; manual/raw modes let WASM callers drive ticks and receive lower-level data | Supports byte output and an internal deterministic core; not a maturity baseline |
| [`dart_mosh`](https://github.com/gwitko/dart_mosh) | Dart, Apache-2.0 | Emerging library used by Conduit, with little standalone history | Exposes a compact UDP Session, cumulative pending client actions, resize, rehoming, and host-byte output | Useful minimal-client comparison; verify protocol behavior independently |
| [mosh-dart](https://github.com/unixshells/mosh-dart) | Dart, MIT | Experimental, few commits and no established releases | Separates protocol, OCB, fragmentation, serialization, and transport concerns without prescribing one terminal UI | Architectural comparison only |
| [ssp-transport](https://github.com/GlassOnTin/ssp-transport) | Kotlin, GPL-3.0 | Limited public adoption | Public embedding shape sends terminal output to a caller callback and runs transport work in coroutines | Architecture and tradeoff reference only; do not copy implementation expression |
| [MoshCatty](https://github.com/binaricat/MoshCatty) | Rust, GPL-3.0+ | Released binaries and claimed stock interoperability; limited independent evidence | Public documentation describes HostBytes paint, a self-contained UDP client loop, and terminal output for a host application | Inspect ideas and tradeoffs; write code and tests independently and verify behavior against stock |
| [Spectty](https://github.com/ocnc/spectty) | Swift, MIT | Emerging application, not a standalone mature protocol library | Mosh transport feeds a terminal state machine and cell model rendered by a native Metal UI; concurrency uses Swift async/await | Shows that cells fit an application-owned native renderer; it does not justify a public cell model here |

The published [`mosh-rs`](https://docs.rs/mosh-rs/latest/mosh_rs/)
documentation describes a source-derived port. It is excluded from project
design and provenance because its implementation origin conflicts with this
project's independent implementation boundary.

The clean-room [Shellby analysis](https://mahui.me/en/blog/shellby-mosh-clean-room/)
describes a pure `(state, input, now) → effects` core with a separate I/O shell.
It also proposes exact wire details. This project uses the architecture as a
comparison and the wire details as experiment hypotheses only. The local stock
1.4.0 fixture has independently confirmed the outer envelope; unverified nested
semantics remain excluded.

## Terminal and runtime libraries

| Project | Public boundary | Relevant lesson |
| --- | --- | --- |
| [xterm.js](https://xtermjs.org/docs/api/terminal/classes/terminal/) | `Terminal.write` accepts a string or `Uint8Array`; parsing is asynchronous and reports completion through a callback | VT bytes are the natural LeanTTY boundary; `writeAck` can represent completed parsing |
| [wezterm-term](https://github.com/wezterm/wezterm/blob/main/term/README.md) | Bytes enter a terminal model through `advance_bytes`; the application reads screen cells and renders them | Cell access fits a terminal library whose consumer owns the renderer; it need not be the first public Mosh API |
| [rustls unbuffered API](https://docs.rs/rustls/latest/rustls/unbuffered/) | Caller supplies buffers and I/O and advances a state machine | A sans-I/O core is excellent for deterministic tests but pushes substantial scheduling responsibility to the caller |

## Options considered

### Terminal output representation

| Option | Strengths | Costs | Decision |
| --- | --- | --- | --- |
| Full cells or cell patches | Exact state, direct native rendering, easy snapshot inspection | Large and unstable public model; poor xterm.js fit; duplicates rendering features | Deferred until a real native-renderer consumer exists |
| Structured paint commands | Typed, testable, smaller than full cells | Creates a private terminal protocol and still needs conversion for xterm.js | Rejected |
| VT paint bytes | Common terminal boundary, compact N-API payload, broad emulator support | Requires a second parser in the display, careful recovery, and terminal-profile tests | Selected |
| VT bytes with revision and acknowledgement metadata | Can describe exactly which screen a surface painted | Adds a second cross-layer state machine before a consumer proves the need | Deferred; start with ordered bytes and explicit repaint |

### Runtime ownership

| Option | Strengths | Costs | Decision |
| --- | --- | --- | --- |
| Caller drives every tick and datagram | Fully deterministic and runtime-neutral | Exposes protocol scheduling to LeanTTY and ArkTS | Keep only as an internal test shape |
| One OS thread per Session | Simple blocking ownership | Extra memory, shutdown work, and duplicate runtime | Rejected |
| Library-created global runtime | Simple surface API | Hidden global state and conflict with embedding runtimes | Rejected |
| Caller executor, library-owned Session future | Clear lifecycle, efficient concurrent Sessions, deterministic core remains testable | Requires an async executor dependency or boundary | Selected, subject to dependency audit |

### Local prediction

| Design | Strengths | Costs | Decision |
| --- | --- | --- | --- |
| No prediction | Smallest state model and no speculative display | Measured visible latency grows by approximately the imposed RTT | Retained as a test baseline, rejected as the completed Phase 2 experience |
| Immediate printable overlay with client timeout | Very small and responsive | Jitter can remove a correct prediction before server evidence; weak epoch confidence | Rejected |
| Full stock-style predictor | Broad editing and terminal heuristics | Disproportionate state and compatibility surface for the initial library | Rejected |
| Confirmed epoch with narrow ASCII projection | Server-backed confidence, bounded divergence, no public API expansion | First characters pay RTT; no Unicode, paste or backspace prediction | Selected |

The [Mosh paper](https://mosh.org/mosh-paper.pdf) establishes confirmed epochs
and server echo acknowledgements as the robust design reference. MoshCatty's
public documentation describes an authoritative framebuffer plus overlay and
explicitly avoids speculative backspace. The small MIT-licensed
[`mosh-go` predictor](https://github.com/unixshells/mosh-go/blob/main/predict.go)
shows that a compact overlay is possible, but its client-side expiry is not
used as confirmation here. These are architecture comparisons only; the stock
1.4.0 black-box fixture independently establishes this project's echo mapping.

## Conclusions

The emerging independent implementations converge on a small asynchronous
Session and byte-oriented host output. That convergence is useful design
evidence, not a maturity consensus. Applications that own native renderers tend
to keep their own terminal model; this remains a separate layer choice.

`mosh-client-rs` therefore starts with ordered VT bytes and an explicit full
repaint request. It coalesces authoritative terminal states before generating
bytes, but it does not add display revisions or rendering acknowledgements
without evidence that the smaller contract fails.

The compared clients keep networking and timers inside a Session loop. The
chosen Rust design follows that smaller shape while letting the embedding
application provide the executor. Stock behavior remains the compatibility
oracle; newer libraries mainly show where the official implementation's broader
program structure is unnecessary for an embeddable client.
