# 0006: Phase 2 core viability gate

- Status: Accepted
- Date: 2026-08-30

## Question

Should the project stop because terminal correctness or recovery remains
materially below SSH for the supported core workload?

This Phase 2 gate evaluates the private Rust core on local stock 1.4.0. It does
not evaluate LeanTTY, HarmonyOS lifecycle behavior, a public API, or broad
terminal compatibility. Phase 4 retains the same-device SSH/Mosh comparison.

## Evidence

| Property | Local evidence | Result |
| --- | --- | --- |
| Interactive terminal | Controlled shell prompt, input, output, and cancellation | Converged |
| Resize | Remote PTY reported 80×24, 100×30, then 60×20; replacement repaint retained 60×20 state | Converged |
| Full-screen applications | Tmux navigation, detach and reattach; Vim edit, repaint, and shell restoration | Converged |
| Input integrity | 128 ordered updates arrived once and in order | Preserved |
| Sustained output | Final state after 1,200 lines and a full one-slot output queue | Converged |
| Surface recovery | Explicit full repaint rebuilt shell and Vim projections | Converged |
| Network recovery | Queued input survived a 1.5-second bidirectional outage and UDP source-port change | Recovered |
| Server loss | Local state remained repaintable and cancellable after server disappearance | Controlled |
| Interaction latency | Confirmed prediction removed imposed RTT from the measured hot epoch | Improved |

The comparison uses SSH as the ordinary interactive-terminal baseline, not as
a requirement to copy its byte stream or connection model. No observed core
scenario loses input, corrupts the visible terminal, hangs during output
backpressure, or becomes uncontrollable after network loss.

## Decision

Continue to Phase 3. Current evidence does not show a material terminal or
recovery deficit within the declared stock 1.4.0, IPv4, fixed-port,
`xterm-256color`, UTF-8 scope.

This decision does not expand compatibility. Mosh still synchronizes visible
state rather than SSH-like local history. Mouse and focus input, physical
address changes, long suspend, xterm.js behavior, HarmonyOS lifecycle,
concurrent Sessions, and public lifecycle semantics remain later gates.

## Consequences

Phase 3 may stabilize the Session API and prove isolation, fuzzing, dependency,
and publishing requirements. A later failure in those gates may still reframe
or stop the project. Phase 4 must compare SSH and Mosh on the same LeanTTY
device before integration is accepted.
