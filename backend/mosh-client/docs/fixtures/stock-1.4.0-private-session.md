# Stock Mosh 1.4.0 Session fixture

> Observation dates: 2026-08-29 and 2026-08-30
>
> Environment: Ubuntu 26.04 WSL x86-64, stock `mosh-server` 1.4.0,
> tmux 3.6, Vim 9.1.2141, IPv4 loopback

## Method

The ignored Rust test
`stock_1_4_0_public_session_drives_an_interactive_shell` started an unmodified
stock server with an interactive `/bin/sh` and a controlled prompt. The test
parsed the public bootstrap record, created a public `Session` and
`SessionTask`, started the task on a caller-created Tokio runtime, and consumed
the Session's one-slot VT output queue.

After the prompt appeared, the test sent a controlled `printf` command through
the Session input channel. It applied each output chunk to a fresh local VT
projection, waited for `SESSION_E2E_OK`, requested a full repaint, and verified
that a replacement projection recovered the same marker. It then used the
public cancellation method and waited for `SessionExit::Cancelled`.

Three additional ignored tests reused the private driver with separate loopback
port ranges. The application fixture used an isolated tmux socket and
`-f /dev/null`, created two controlled `/bin/sh` windows, navigated between
them, detached, reattached, and recovered both markers. It then started Vim
with user configuration, swap files, and viminfo disabled; inserted a marker;
requested a full repaint; and verified shell restoration after `:q!`.

The sustained-I/O fixture disabled shell echo, sent 128 distinct ordered input
fragments, and verified their exact concatenation. It then generated 1,200
controlled output lines, left the one-slot output queue full for 500 ms,
consumed the coalesced state, and verified the final marker through a full
repaint.

The resize fixture checked the initial remote PTY with `stty size`, requested
100×30 and then 60×20 through the Session command queue, and checked each size
inside the remote shell. It resized the local consumer projection in the same
order, then rebuilt a 60×20 replacement projection from a full repaint.

Run the fixture with:

```bash
cargo test session::tests::stock::stock_1_4_0_public_session_drives_an_interactive_shell \
  --all-features -- --ignored --exact --nocapture

cargo test session::tests::stock::stock_1_4_0_private_session_supports_tmux_vim_and_full_screen_repaint \
  --all-features -- --ignored --exact --nocapture

cargo test session::tests::stock::stock_1_4_0_private_session_preserves_sustained_io_under_output_backpressure \
  --all-features -- --ignored --exact --nocapture

cargo test session::tests::stock::stock_1_4_0_private_session_resizes_the_remote_pty_and_repaints \
  --all-features -- --ignored --exact --nocapture
```

## Result

Stock 1.4.0 accepted the independently generated Session traffic. The public
API completed bootstrap-to-UDP startup, prompt painting, input delivery,
terminal-state convergence, incremental output, explicit full repaint, and
cancellation.

The tmux fixture preserved both window states across navigation, detach, and
reattach. Vim's visible full-screen state survived a replacement-surface
repaint, and exit restored the shell. The `xterm-256color` terminfo entry
declares `CSI ?1049h`/`CSI ?1049l` for alternate-screen entry and exit, but the
stock Session exposed the current visible Vim frame without preserving an
alternate-screen flag in the client projection. The live projection and a
replacement projection built from an explicit full repaint both remained on
their local primary buffer. This characterizes the public output boundary; it
does not make application-level alternate-screen transitions part of the
Session contract.

Vim also produced `CSI ?1004h` through the authenticated stock terminal state.
The client suppresses this verified focus-reporting toggle because its current
contract has no focus-event input. Unknown policy modes still fail explicitly,
and every full repaint disables focus reporting before painting.

All 128 input updates arrived once and in order. The final 1,200-line output
state arrived after the display queue had remained full, and a replacement
projection recovered the final marker. This proves bounded display
backpressure does not stop authenticated UDP state convergence in the observed
loopback case.

The remote PTY reported 24×80, 30×100, and 20×60 after the matching Session
commands. A replacement 60×20 projection recovered the final size marker from
one explicit full repaint.

The test cleared captured bootstrap stdout and stderr after parsing. It kept no
session key, credential, packet capture, user path, host name, or shell output
beyond fixed public test markers. A process guard terminated the detached stock
server after success or failure.

## Limits

The fixtures cover controlled local applications between 60×20 and 100×30.
They do not prove focus-event delivery, mouse modes, complete local scrollback,
consumer-owned whole-Session isolation, concurrent Sessions, or HarmonyOS
runtime behavior. Local outage, UDP source-port change, latency, and
server-disappearance evidence is recorded separately in the
[recovery fixture](stock-1.4.0-session-recovery.md).
