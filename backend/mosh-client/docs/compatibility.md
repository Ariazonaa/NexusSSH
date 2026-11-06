# Compatibility contract

## Baseline

Version 0.1.0 requires Rust 1.88 or newer. Release checks exercise exactly Rust
1.88.0 as well as current stable. Older Rust 1.85 validation records describe
earlier development revisions and are not the 0.1.0 compiler contract.

The initial oracle is an unmodified stock `mosh-server` 1.4.0. Interoperability
must be demonstrated through public behavior and black-box fixtures.

The first supported path is:

```text
external SSH bootstrap
  -> validated MOSH CONNECT result
  -> IPv4 fixed UDP endpoint
  -> authenticated client session
  -> interactive shell state
```

## Presentation baseline

The first presentation target is a UTF-8 VT-compatible terminal surface. The
library emits synthesized VT paint bytes as bounded chunks that consumers write
in order. A replaced surface explicitly requests a self-contained full repaint;
the initial contract has no display-revision or rendering-acknowledgement layer.

The first stock-server terminal profile is `TERM=xterm-256color`,
`LANG=C.UTF-8`, 24 rows, and 80 columns. Resize is supported within the hard
bounds in [limits](limits.md). Other locales, ambiguous-width policies, and
terminal identities need their own fixture before the claim expands.

Received terminal patches must be well-formed UTF-8 and complete VT sequences.
In ground text, U+0020–U+007E and U+00A0–U+10FFFF take the printable path;
well-formed UTF-8 cannot encode surrogate code points. The screen model retains
scalars of width one or two, including ASCII, non-ASCII letters, CJK, emoji,
private-use scalars, and U+FFFD. Zero-width scalars such as combining
marks, joiners, and variation selectors are retained with a preceding base in
the same host-byte operation; combining scalars beyond the 22-byte cell are dropped (NexusSSH P2).
Supported terminal controls (such as newline and carriage return) are parsed
as controls; unsupported C0/C1 controls and DEL (U+007F) fail explicitly. A
Unicode scalar accepted into the screen is not a guarantee that every consumer
font will draw a visible glyph or that complex grapheme shaping is identical.

The [stock-server fixture](fixtures/stock-1.4.0-replacement-character.md)
covers U+FFFD, selected Latin/CJK/emoji/combining text, and one invalid remote
byte that the server converts to U+FFFD. The client still rejects malformed
UTF-8 in a received terminal patch; this does not claim arbitrary binary-output
compatibility.

The [cell-width fixture](fixtures/stock-1.4.0-cell-width.md) covers a
versioned `C.UTF-8` width profile where stock-server libc and the client's
Unicode width data differ. Validation, authoritative cells, cursor movement,
incremental paint, and full repaint use the same width policy. A width-zero
scalar still requires a preceding base within the same host-byte operation
and remains subject to the cell limits. The fixture covers selected
differences; other locales and server libc versions require new evidence.

The [terminal-control batch fixture](fixtures/stock-1.4.0-terminal-compatibility-batch.md)
covers OSC 0/1/2 titles containing semicolons, SGR blink/hidden attributes and
their resets, and DEC whole-screen reverse video and its reset. Titles (OSC 0/2) and
the bell reach the VT output as OSC 0 and BEL (NexusSSH P9). Blink and hidden
remain distinct cell attributes in full and incremental paint; reverse video
remains a screen mode. Mouse modes 1001/1015, malformed OSC 52 payloads and other
unknown CSI or OSC sequences are passed over, and combining scalars beyond the cell
limit are dropped (NexusSSH P2, both used to end the session). The raw ST terminator and split
zero-width input boundaries require separate evidence. Actual blinking and
screen inversion depend on the consumer terminal's VT support; the library
does not promise those visual effects on xterm.js.

The first embedding oracle is LeanTTY's version-locked xterm.js surface. An
independent permissively licensed terminal model provides a second local check.
This baseline does not claim compatibility with every terminal emulator or
native cell renderer.

The public Session supports the standard `Adaptive`, `Always`, and `Never`
prediction display modes. `Adaptive` is the default. All three retain the same
confirmed-epoch ASCII eligibility and authoritative convergence; experimental,
overwrite, Unicode, paste, backspace, and control-sequence prediction remain
outside the compatibility claim. Displayed predictions are underlined while the
link is slow or the server has not confirmed for 250 ms, as stock Mosh flags
them (NexusSSH P10); confirmation is compared against the plain projection.

Mosh synchronizes visible terminal state, not complete shell history. Tests
must record local scrollback behavior, recovery effects, and limitations without
claiming SSH-equivalent or persistent history.

The output stream also does not preserve a remote application's
alternate-screen entry or exit boundary. A visible full-screen frame and an
explicit repaint remain usable, but the consumer projection need not carry an
alternate-screen flag. Consumers own any temporary terminal page used to
isolate the whole Mosh Session from pre-existing local contents.

## Required evidence

Before the compatibility claim expands, tests must cover:

- valid, malformed, ambiguous, and oversized bootstrap output;
- correct and incorrect keys;
- valid, malformed, duplicate, replayed, reordered, delayed, and lost packets;
- client source-address changes without changing the fixed server endpoint or
  losing session state;
- graceful local close, authenticated remote close, cancellation, timeout,
  server disappearance, and late packets;
- resize, Unicode, wide characters, sustained input, and sustained output;
- shell, tmux, a basic editor, alternate screen, and scrollback behavior.

Every test records the server version, platform, network conditions, expected
behavior, and cleanup result.

The current loopback matrix covers the shell, tmux attach/navigation/detach,
Vim full-screen editing and repaint, ordered sustained input, sustained output,
display backpressure, and cancellation. The interactive shell and repaint
fixture drives the Phase 3 public Session API. Stock Mosh exposes the current
visible Vim frame without requiring the local projection to retain the remote
alternate-buffer flag. The fixture asserts that the live projection and a
replacement projection rebuilt from a full repaint both remain on their local
primary buffer, then converge to the shell after Vim exits. A test-only
loopback relay proves recovery after a
1.5-second bidirectional interruption and UDP source-port change; another test
proves that server disappearance leaves the Session repaintable and
cancellable. A Session fixture verifies remote PTY resize from 80×24
to 100×30 and 60×20, followed by a replacement-surface repaint. Stock 1.4.0
black-box fixtures also cover the reserved close target in both
directions, the peer acknowledgement shape, and the bounded no-ACK wait.
Public reachability now reports recent-contact and recent-reply warnings without
treating silence as close. Established Sessions also preserve state across the
selected local UDP I/O errors in ADRs 0011 and 0012. LeanTTY's development
records now cover [physical WLAN recovery](fixtures/leantty-physical-local-send-recovery.md),
[a real address change](fixtures/leantty-physical-network-switch.md), and the
[observed lid-close permission failure](fixtures/leantty-physical-lid-permission-denied.md)
on one HAD-W32 PC. The first two fixtures retain the same Session and remote
PTY; the fixed-revision lid-close rerun remains pending. Longer outages, other
devices, and other network topologies require separate evidence; whole-Session
display restoration remains the consumer's responsibility.

The full evidence matrix, offline-first policy, runtime isolation cases, VT
output contract, and physical LeanTTY acceptance are defined in
[testing](testing.md).

## Deferred compatibility

IPv6, configurable port ranges, broader prediction, locale negotiation, broad
terminal application coverage, additional recoverable local UDP error classes,
configurable reachability thresholds, and non-LeanTTY consumers require
separate evidence. ProxyJump applies only to an embedding application's SSH
bootstrap; it does not imply UDP reachability.
