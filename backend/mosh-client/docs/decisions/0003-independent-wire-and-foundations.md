# ADR 0003: Derive the wire contract independently and reuse narrow foundations

- Status: accepted
- Date: 2026-08-29

## Context

Mosh has mature released binaries and published design papers, but no complete
standalone wire specification. The stock implementation is GPL-covered. Newer
permissive implementations describe simpler architectures, but most are young
and cannot establish correctness or maturity.

The project also needs cryptography, Protocol Buffers, zlib, async UDP, Unicode
width, and VT parsing. Reimplementing every general capability would violate
the project principles; importing a complete Mosh or terminal implementation
would violate the scope and provenance boundary.

## Decision

Build the wire contract from public papers, standards, command contracts, and
project-controlled black-box fixtures against released stock binaries. Treat
other implementations as hypothesis and architecture sources only. Record each
accepted rule in `docs/provenance.md`.

The project may inspect third-party implementations, including GPL-covered
implementations, to compare ideas and tradeoffs. It does not copy or translate
their code, comments, tests, file organization, or distinctive expression.
Stock Mosh source and schemas remain excluded from protocol derivation. A rule
first noticed in another implementation still requires independent evidence
and stock-server verification where applicable.

Reuse narrow maintained crates for general capabilities:

- Tokio for the Session driver;
- RustCrypto OCB3 and AES under the bounded security exception in
  [ADR 0004](0004-rustcrypto-ocb3-security-exception.md);
- Prost with original hand-declared message types and no schema generator;
- miniz_oxide's bounded zlib API;
- zeroize and strict Base64 decoding; and
- unicode-width and vte when Phase 2 fixtures justify them.

Keep Mosh packet integration, fragmentation, synchronization, recovery,
roaming, terminal state, painting, limits, lifecycle, and any later
evidence-backed prediction in this crate.

## Consequences

The project gains a reproducible legal and technical boundary. It can use the
stock server as a compatibility oracle without translating its implementation.
The selected dependencies replace general mechanisms while leaving protocol
policy visible and testable.

The approach requires more black-box fixtures before terminal work. No
independently audited, portable, permissive OCB3 crate was found. ADR 0004
accepts that narrow residual risk and defines mandatory controls before the
crypto dependency enters production code.

## Rejected alternatives

- Following stock source or schemas would violate the independent boundary.
- Treating a new clean-room implementation as the specification would replace
  one undocumented authority with another.
- Reimplementing cryptography or general codecs would increase security risk.
- Importing a complete terminal model would enlarge the public contract and
  duplicate LeanTTY's presentation layer.
