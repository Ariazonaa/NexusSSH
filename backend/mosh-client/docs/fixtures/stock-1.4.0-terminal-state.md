# Stock Mosh 1.4.0 terminal-state fixture

> Observation date: 2026-08-29
>
> Environment: Ubuntu 26.04 WSL, stock `mosh-server` 1.4.0, IPv4 loopback

## Method

The ignored Rust test
`stock_1_4_0_host_bytes_preserve_controlled_utf8_output` started an unmodified
stock server with `/bin/sh -c "printf 'A中Z'; sleep 3"`. The project sent its
authenticated 81×25 initial state, then decoded, reassembled, decompressed, and
applied each returned terminal difference through production code.

The test retained terminal snapshots by SSP state number. Each new state was
constructed from its named base snapshot. The test did not copy a parser across
snapshots or inspect stock source code.

## Result

The production terminal decoder accepted ordered resize and `HostBytes`
operations. The resulting state had 81 columns, 25 rows, and the exact marker
`A中Z`; `中` occupied one wide cell and one continuation cell.

The initial stock patch also contained `CSI ?1001l`, `CSI ?1004l`, and
`CSI ?1015l`. These sequences disable mouse highlight tracking, focus
reporting, and urxvt mouse encoding. The project accepts these three reset-only
policy operations, rejects their unimplemented set forms, and emits the resets
at the start of a full repaint.

## Limits

The fixture proves one controlled UTF-8 screen update, one resize, SSP base
snapshot application, and the stock initialization reset set. It does not
prove interactive input, shell prompts, colors, tmux, editors, alternate
screen, mouse or focus events, clipboard policy, sustained output, prediction,
or recovery after packet loss.
