# Project principles

These principles govern project scope, architecture, dependencies, public API,
testing, and integration decisions. Lower-level plans and existing code do not
override them.

## Mission

Build a focused, reliable, independent Rust Mosh client library that
interoperates with the stock `mosh-server` and is easy for other Rust projects
to embed.

The project values protocol correctness, security, bounded resource use,
predictable lifecycle behavior, and maintainability over feature count or broad
compatibility claims.

## 1. Keep the client focused and dependable

Implement the core behavior required for a dependable Mosh client, but keep
the product boundary narrow.

- Build a client library, not a server or server distribution.
- Keep SSH authentication, host verification, credentials, and remote process
  execution in the embedding application.
- Keep terminal presentation, application policy, and workspace management out
  of the protocol crate.
- Reject unsupported, malformed, ambiguous, or unsafe states explicitly.
- Bound untrusted input, memory, queues, retries, timers, and retained state.
- Add compatibility only when the project can define and verify it.
- Avoid abstractions and features for hypothetical consumers.

Simple means that each component has one clear owner. It does not mean omitting
protocol behavior, security checks, cleanup, or tests needed for reliability.

## 2. Reuse mature foundations

Prefer mature, maintained, permissively licensed dependencies for general
capabilities when they reduce the project's total complexity and risk.

The project must use established libraries for cryptographic primitives and
secure random generation. It should normally reuse suitable libraries for
secret clearing, Unicode data, asynchronous I/O, byte handling, property tests,
and fuzzing.

The project owns Mosh-specific behavior:

- the independently derived wire contract;
- packet and cryptographic integration;
- sequence, replay, fragmentation, synchronization, recovery, and roaming
  rules;
- terminal synchronization and any later evidence-backed local prediction;
- Session lifecycle, limits, errors, and output semantics; and
- interoperability evidence against the stock server.

A dependency is not simpler merely because it removes local code. Adopt it only
when its license, provenance, maintenance, security, target support, transitive
dependencies, `unsafe` code, failure behavior, and replacement cost are
acceptable. Reject a dependency that saves implementation work but increases
whole-project complexity or weakens the independent implementation boundary.

Follow [the dependency policy](dependency-policy.md) for every adoption.

## 3. Design a standard embeddable client first

Expose a small, idiomatic Rust Session API that does not require consumers to
understand Mosh packets, retransmission, synchronization, prediction, or
roaming.

A standard client in this project means an embeddable Rust library that starts
and drives a Mosh UDP Session from validated bootstrap information. It does not
mean a replacement for the complete stock `mosh` command, bundled SSH, or a
terminal user interface.

LeanTTY is the first reference consumer, not the owner of the library contract.
The crate may provide capabilities that are natural for embedded Mosh clients,
including cancellation, resize, bounded queues, state events, repaint, output
delivery, and stale-event isolation. LeanTTY-specific Pane, ArkTS, N-API,
WebView, xterm.js, HarmonyOS UI, and product policy remain in LeanTTY.

Support a LeanTTY need in this crate only when the support:

- belongs naturally to a Mosh client library;
- remains useful without LeanTTY types or platform dependencies;
- has a small public and implementation surface;
- preserves the standard client path; and
- has focused compatibility and lifecycle tests.

## 4. Implement the core path, not the reference architecture

Deliver a reliable and usable Mosh-compatible client core that interoperates
with the supported stock `mosh-server`. Do not reproduce every mechanism,
optimization, state-retention policy, prediction heuristic, or feature in the
Mosh paper or stock client unless the core path requires it.

When several wire-compatible designs are possible, choose the smallest design
that preserves:

- authenticated and replay-safe transport;
- user input without loss or duplication;
- terminal-state convergence, resize, recovery, and roaming;
- bounded resource use and predictable lifecycle behavior; and
- measured interactive usability under representative latency, loss, and
  reordering.

Add more elaborate state history, pacing, prediction, display, or recovery
machinery only when stock-server interoperability or measured user experience
shows that the simpler design is insufficient.

MoshCatty, `dart_mosh`, and `mosh-go` may be inspected as implementation,
architecture, and tradeoff references under the project's licensing and
provenance rules. Their ideas may inform the design, but project code and tests
must be independently written. These implementations are comparison evidence,
not protocol authorities, and do not replace independent wire evidence or
stock-server interoperability verification.

## Decision priority

When valid goals conflict, use this order:

1. protocol correctness and security;
2. reliability and bounded behavior;
3. a clear standard-client boundary;
4. total project simplicity and maintainability;
5. reuse of third-party libraries; and
6. convenience for LeanTTY integration.

Third-party reuse and LeanTTY convenience are means, not outcomes. They do not
justify weaker correctness, a polluted public API, or disproportionate
complexity.

## Complexity gate

Add a dependency, abstraction, public API, feature, compatibility claim, or
crate only for a current protocol requirement, reliability requirement, or
demonstrated consumer need. Record the need, alternatives, evidence, tests, and
removal or rollback path in the appropriate decision or policy document.

Prefer the smallest design that satisfies the current contract. Extend it when
evidence reveals a real state, lifecycle, test, platform, or consumer boundary.

## Applying the principles

Use these principles to make routine technical decisions without reopening the
project mission. Record a decision when it changes protocol behavior,
dependencies, public API, compatibility, security, or project scope.

Stop and revisit the project direction only when a decision would:

- change the mission, supported audience, trust boundary, or data boundary;
- add a server, CLI, SSH implementation, generic framework, or another major
  product responsibility;
- weaken the licensing or independent implementation boundary;
- make the public API or core architecture specific to LeanTTY; or
- choose between materially different outcomes when evidence does not favor one.

Tests and existing code provide evidence. They do not override the mission or
these principles.
