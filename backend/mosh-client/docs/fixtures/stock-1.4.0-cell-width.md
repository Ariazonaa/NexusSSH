# Stock 1.4.0 cell-width fixture

Date: 2026-09-25. Run in the default WSL distribution over IPv4 loopback
with stock `mosh-server` 1.4.0, `TERM=xterm-256color`, `LANG=C.UTF-8`,
and an 80×24 public Session. The test owns UDP ports `60700:60719` and
terminates its server.

LeanTTY [PR #263](https://github.com/wandcs/leantty/pull/263) records the
physical failure: output of `printf '\330\205-AFTER\n'` ended the library
Session with `Protocol` and returned to ltty, while the stock Mosh client
stayed connected. Local pre-fix tests reproduced `TooManyScalarsInCell` for
U+0605 at column zero. With a preceding ASCII scalar the same character was
appended to that cell instead, shifting later columns.

The fixture `stock_1_4_0_public_session_preserves_server_width_characters`
sends octal-escaped UTF-8 through the shell. It checks U+0605 at line start
and after text, then selected characters from the other observed width
disagreement classes: U+070F, U+09BE, U+17A4, U+17D8, U+302E, and U+3248.
U+2D7F is checked after an ASCII base as a zero-width scalar. The
screen must retain each marker; the Session must remain active for another
input command, then survive resize to 81×25 and a full repaint. Local
terminal-state and paint tests inspect exact cells, cursor positions,
separate host differences, incremental paint, and post-resize paint.
The U+FFFD stock fixture remains a separate regression.

The width policy was derived from an exhaustive local comparison of
`unicode-width` 0.2.2 with its `cjk` feature and the C.UTF-8 `wcwidth`
result of Ubuntu glibc 2.43. Among code points for which libc returned a
finite width, 95 differ: 80 at libc 1 / Rust 0, one each at 0/1, 1/2,
and 1/3, four at 2/0, and eight at 2/1. U+0000 is separately handled
as a control. The project-owned override covers those 95 code points and
is shared by validation and the screen. Unassigned code points with
libc width -1 were excluded. This is a versioned compatibility profile,
not a claim that every Mosh server, locale, or terminal font has the same
width table.

The stock fixture and local regressions passed on 2026-09-25. A separate
ignored stock fixture now sends the exact `\\cat /bin/ls` input on a 147×43
Session, observes a terminal update, sends another input command separately,
and verifies the Session is still active. It never clears the screen between
binary output and the follow-up. This is one bounded binary sample, not a
claim of arbitrary binary-output compatibility or physical HarmonyOS
acceptance of the repaired revision.
