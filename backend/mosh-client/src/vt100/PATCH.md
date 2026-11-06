# Private vt100 source patch

This directory contains the nine Rust source modules from the MIT-licensed
[`vt100` 0.16.2 crate](https://crates.io/crates/vt100/0.16.2) by Jesse Luehrs.
The original `LICENSE`, `README.md`, and
`CHANGELOG.md` are retained alongside the modules. The crates.io archive had
checksum `054ff75fb8fa83e609e685106df4faeffdf3a735d3c74ebce97ec557d5d36fd9`.
The original crate-level `lib.rs` and manifest are omitted because these modules
are compiled privately within `mosh-client`, not as another package.

The imported modules have local changes in five files. In `perform.rs`, U+FFFD
passes to `Screen::text` while C1 characters retain the original unhandled
callback path. OSC 0/1/2 title fields are rejoined with semicolons before the
existing callbacks; malformed OSC 52 still follows its rejection path. In
`screen.rs` and `cell.rs`, character width comes from the root crate's shared
stock-server compatibility policy instead of direct `unicode-width` calls. The
same policy is used before screen mutation, so admission, cell storage, cursor
movement, and paint agree. `attrs.rs`, `term.rs`, `screen.rs`, and `cell.rs`
retain and paint SGR blink and hidden attributes and DEC reverse-video mode.
`mosh-client` rejects malformed UTF-8 before calling this parser.
The root crate supplies the original module names and internal reexports; its
Rustfmt skips these files to keep the imported source easy to compare.

This copy can be removed when an audited upstream release preserves valid
U+FFFD, supports the required width and terminal-control policy, and passes
the same state, paint, and stock-server fixtures. Keep the
source-package rebuild gate: [Cargo excludes nested packages](https://doc.rust-lang.org/cargo/reference/manifest.html#the-exclude-and-include-fields)
from the parent `.crate` package, and the parent manifest's registry fallback
would silently resolve to the unpatched release.
