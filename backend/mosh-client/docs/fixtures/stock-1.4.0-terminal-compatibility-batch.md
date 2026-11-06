# Stock 1.4.0 terminal-control batch fixture

Date: 2026-09-26. Run in the default WSL distribution over IPv4 loopback
with stock `mosh-server` 1.4.0, `TERM=xterm-256color`, `LANG=C.UTF-8`,
and a 147×43 public Session. The test owns UDP ranges `60740:60759` through
`61100:61119` and terminates each detached server. LeanTTY
[PR #264](https://github.com/wandcs/leantty/pull/264) contains the independent
physical diagnosis and the 19-case / 277-input replay package.

The 0.1.3 baseline replay reproduced all 19 recorded outcomes. The stock
baseline ended 12 Sessions with `Protocol` and accepted seven. The raw-input
baseline rejected OSC 0/1/2 titles containing semicolons, SGR 5/8, and DEC
?5h/?5l. The repair keeps title bytes together, stores blink and hidden text
attributes, and stores reverse video as a screen mode. State and paint tests
cover the matching SGR 25/28 and DEC ?5l resets, full repaint, and incremental
paint. Title callbacks receive the complete semicolon-separated payload;
the public Session still applies no system title effect.

The control meanings follow [xterm's control-sequence reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html).
The crate preserves and emits the reverse-video and blink controls, while
rendering remains the consumer's responsibility. The current
[xterm.js feature table](https://xtermjs.org/docs/api/vtfeatures/) lists
SGR 5 as unsupported and does not list DEC private mode 5. This fixture
establishes Session survival and VT output, not visible blinking or screen
inversion on LeanTTY's renderer.

Run the original package without changing its baseline file:

```sh
python3 /mnt/c/repos/leantty/tools/diagnostics/mosh-disconnect/run.py \
  --source /mnt/c/repos/mosh-client-rs --work /tmp/mosh-disconnect-check --scan
```

After the repair, the 19-case replay changed exactly seven raw outcomes:
OSC 0/1/2 title cases using BEL, SGR 5/8, and DEC ?5h/?5l. In the full
277-input scan, 21 changed outcomes are confined to title, SGR, and reverse
video variants; the other 256 remain at baseline. Raw OSC ST termination
still reports `UnsupportedEscape`; stock 1.4.0 normalizes the selected ST
input into a BEL-terminated terminal patch. ST and split zero-width input
boundaries require their separately recorded investigation.

The ignored test
`stock_1_4_0_public_session_batch_terminal_compatibility` starts one fresh
stock server for each of the 19 cases. It sends the case bytes through the
shell as octal `printf` input, verifies accepted cases reach a marker and
execute a separately sent follow-up command, and verifies rejected cases
still end with `SessionError::Protocol`. All 19 passed after the repair:

| Group | Accepted stock cases | Rejected stock cases | Policy |
| --- | ---: | ---: | --- |
| OSC 0/1/2 title and control | 5 | 0 | Preserve title text with semicolons |
| SGR blink/hidden and control | 3 | 0 | Retain cell attributes |
| DEC reverse video | 2 | 0 | Retain screen mode |
| Mouse 1001/1015 and 1000 control | 1 | 2 | Keep 1001/1015 outside profile |
| OSC 52 valid/invalid payload | 1 | 1 | Reject malformed Base64 |
| Eight-scalar cell bound | 1 | 1 | Reject overlong cell |
| Encoded cell-byte bound | 1 | 1 | Reject oversized cell |

The accepted total is 14 and the rejected total is five. Rejected stock
cases are intentional compatibility boundaries. This repair does not remove
their checks, hide their errors, or claim support for them.

`stock_1_4_0_public_session_observes_binary_output_before_followup` sends
the exact `\cat /bin/ls` command in a 147×43 Session, observes a terminal
update, and then sends a distinct follow-up command. The Session remains
active and paints the follow-up marker without a clear or terminal reset.
This is one bounded binary-output sample; it is not a claim for arbitrary
binary streams or physical HarmonyOS acceptance.
