# Stock 1.4.0 replacement-character and Unicode fixture

Date: 2026-09-25. Run in the default WSL distribution on IPv4 loopback with
`TERM=xterm-256color`, `LANG=C.UTF-8`, an 80×24 public Session, and an
unmodified stock `mosh-server` 1.4.0. The test starts its own shell and server
on the `60680:60699` UDP range and terminates the server on completion.

LeanTTY's `mosh-replacement-character-diagnosis-20260925.md` first isolated the
failure. The library test
`stock_1_4_0_public_session_preserves_printable_unicode` sends two shell
commands: one prints the valid UTF-8 bytes `EF BF BD` between ASCII markers;
the other prints one invalid byte `FF` and relies on the stock server's
replacement. It requires both visible `U+FFFD` markers. A third command prints
precomposed Latin `é`, CJK `中`, emoji `🙂`, and `e` plus a combining accent;
their UTF-8 bytes are supplied through shell octal escapes. The fixture then
sends another ASCII command, resizes to 81×25, requests a full repaint, and
checks that a fresh projection retains all four markers. It cancels the
Session and checks that the server was cleaned up.

The independent stock-client comparison and the pre-fix failure are recorded
in LeanTTY's diagnosis. This in-repository fixture verifies the repaired public
Session against the stock server; its projection and authority both use the
private screen code, so the unit tests separately inspect cell contents,
cursor position, and the generated UTF-8 paint bytes.

This fixture establishes those selected printable categories and U+FFFD through
the stock server. It does not claim arbitrary binary output or full Unicode
rendering fidelity.

The focused ignored fixture passed on 2026-09-25 with
`cargo test --offline stock_1_4_0_public_session_preserves_printable_unicode -- --ignored`.
The ordinary all-target suite passed separately; its stock tests remain ignored
unless selected explicitly.
