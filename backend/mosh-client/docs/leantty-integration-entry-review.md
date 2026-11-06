# LeanTTY integration entry review

> Review date: 2026-08-30
>
> Scope: the evidence required before LeanTTY product integration begins, and
> the later native consumer result

## Current status: 2026-09-05

The original native-entry review below is historical. LeanTTY has since wired
Pane input and the Terminal Surface and recorded development tests for shell,
tmux, Vim, prediction, close, interruption, and real network switching. See
the [prediction](fixtures/leantty-arm64-prediction.md),
[WLAN recovery](fixtures/leantty-physical-local-send-recovery.md), and
[network-switch](fixtures/leantty-physical-network-switch.md) closure records.

Those results close the reported library defects. LeanTTY's formal 1.6 matrix
and its later presentation, input, and harness follow-ups remain product work.
They do not certify a release of LeanTTY or a newly built HAP. The maintainer
separately authorized the [0.1.0 library release review](releases/0.1.0.md),
which supersedes the old `0.0.0 / publish=false` publication hold below.

## Historical stage decision: 2026-08-30

The `mosh-client` core is frozen after Phase 3F and successful native consumer
integration. Further library work now requires evidence from LeanTTY's physical
Mosh Session tests or a separate release decision.

LeanTTY recorded that current SSH loses the remote working context when Wi-Fi is
disabled, then authorized a minimal Mosh vertical slice. Its native layer now
owns host verification, authentication, controlled bootstrap, the Mosh UDP
task, input, resize, repaint after output suspension, and cancellation. The
integration uses this crate's public API without adding a generic Transport
layer or a LeanTTY-specific protocol API.

This is a consumer and build milestone, not Phase 4 completion. Pane and ArkTS
ownership, the Terminal Surface event chain, the command entry, signed-HAP
sessions, and physical recovery tests remain in LeanTTY. If those tests do not
show material user value, LeanTTY removes the product path without expanding
this library.

## Governing boundary

This review applies the established ownership split:

- LeanTTY owns Host resolution, host verification, authentication, controlled
  `mosh-server` startup, Pane lifecycle, and the Terminal Surface.
- `mosh-client` owns the untrusted bootstrap parser, authenticated UDP protocol,
  bounded synchronization, instruction decoding, and public Session lifecycle.
- The first integration is one Pane-owned Mosh Session connected to the
  existing Terminal Surface. It does not add a generic Transport abstraction.
- LeanTTY-specific UI, ArkTS, N-API, Pane, and platform policy stay outside this
  crate.

## Evidence already obtained

### Library and dependency readiness

Phase 3F closed the implementation and security gate while retaining
`publish = false`; see the
[security and publication review](security-publication-review.md). The public
Session contract and its cancellation, output, resize, recovery, and secret
handling behavior are covered by the retained verification suite.

A disposable combined `cdylib` linked `mosh-client` with the dependency families
used by LeanTTY's native layer, including `napi-ohos`, Tokio, Russh, and
Russh-SFTP. Cargo resolved 219 packages and completed an
`aarch64-unknown-linux-ohos` debug build. The resulting 4,508,320-byte artifact
was an ELF64 AArch64 shared object. Its debug size is diagnostic evidence, not a
product size estimate.

The combined graph introduced no second Tokio. Duplicate versions were confined
to transitive packages already selected by the surrounding dependency graph.
The first attempt exposed a stale user-level linker path from an older checkout;
the successful run used LeanTTY's existing OHOS linker and archiver settings for
that process. No global configuration or project source was changed.

This proves source-level coexistence and target linking only. The disposable
build was not installed, launched, or exercised on a device.

### Native consumer integration

LeanTTY later integrated commit `69450f4` as a local path dependency. Its native
Mosh owner performs SSH host verification and authentication, runs a controlled
bootstrap, parses the bounded result in Rust, starts the UDP Session task, and
owns input, resize, repaint, and cancellation. Bootstrap output stays in a
`Zeroizing<Vec<u8>>`; the temporary key does not enter ArkTS, terminal output,
logs, or persistent state.

LeanTTY's focused `policy,rust-native` gate passed on 2026-08-30. The same tree
passed 42 native tests, strict Clippy, and an ARM64 OHOS release build that
actually linked `mosh-client`. The evidence was development-only and explicitly
not release-eligible. LeanTTY had not yet connected Pane/ArkTS, its Terminal
Surface, or a user command, and had not run a physical Mosh Session.

### HarmonyOS platform capability

The official platform documentation establishes these available mechanisms:

- applications can observe
  [default-network changes](https://developer.huawei.com/consumer/en/doc/harmonyos-guides/ide_listen-default-network-change)
  and move transmission to the new network;
- the native network API includes default-network callbacks and
  [`OH_NetConn_BindSocket`](https://developer.huawei.com/consumer/en/doc/harmonyos-references-V14/_net_connection-V14);
- HarmonyOS exposes
  [continuous-task support](https://developer.huawei.com/consumer/en/doc/harmonyos-guides/long-time-task-overview)
  for long-lived work such as listening on a socket port; and
- applications can bind UDP sockets to selected networks through the documented
  [multi-network APIs](https://developer.huawei.com/consumer/en/doc/harmonyos-guides/networkboost-netmultipath-network-turbo).

The same official material warns that applications can be suspended or
terminated in the background without an appropriate task, and documents
[`TASK_KEEPING`](https://developer.huawei.com/consumer/en/doc/harmonyos-references-V5/js-apis-resourceschedule-backgroundtaskmanager-V5)
for 2-in-1 devices. Background resource guidance also describes disconnecting
[TCP and UDP when an application has no long-running task](https://developer.huawei.com/consumer/cn/doc/harmonyos-guides-V5/standard-background-hardware-V5),
but its stated device scope does not establish a HarmonyOS PC guarantee.

The documentation therefore supports a feasibility hypothesis, not the product
claim. It cannot replace a signed-HAP test on the target PC.

### Existing environment evidence

The available fixed-endpoint HSL-to-WSL UDP test establishes that the local
network can carry, block, and restore UDP traffic. It does not exercise a
LeanTTY process, application sandbox, Pane lifecycle, HarmonyOS network callback,
lock, sleep, or address change. Earlier device-side Toybox `nc` attempts failed
before product behavior was involved and remain ineligible as acceptance
evidence.

A fresh device-control preflight passed on 2026-08-30. The connected physical PC
is available for a focused probe, but no trustworthy named Mosh product scenario
or product HAP exists yet.

## Remaining product gate

LeanTTY owns and must record the following evidence before this repository marks
Phase 4 complete:

1. Connect one Pane-owned ArkTS client to the native owner and existing Terminal
   Surface, with generation isolation and a strict `mosh [user@]host|alias`
   command entry.
2. Exercise a real shell, tmux, and basic editor through a signed HAP on the
   physical PC.
3. Verify normal use, interruption, address change, lock, sleep, UDP block,
   recovery, cancellation, Pane close, and secret cleanup.
4. Compare recovery and correctness with current SSH behavior, then record the
   final integrate or remove decision in LeanTTY.

Do not infer success from buildability, SSH/SFTP lifecycle tests, a host-to-host
UDP exchange, or official API availability. A failed probe is also useful: if
ordinary third-party UDP cannot recover reliably without disproportionate
platform policy, cancel the integration rather than add workarounds.

## Re-entry conditions for this library

The first re-entry occurred on 2026-08-30 after LeanTTY reproduced local server
leakage and an unfinished remote logout. ADR 0008 and the stock 1.4.0 close
fixtures close that bounded lifecycle defect with additive graceful close while
preserving hard cancellation. This does not satisfy or weaken the separate
physical reachability and temporary-socket-error evidence gates below.

Do not extend the library from the remaining LeanTTY plan alone. Reopen core
development only when one of these conditions supplies concrete evidence:

1. physical LeanTTY testing reproduces a protocol or public API defect;
2. an observed HarmonyOS socket error needs a narrow, bounded recovery rule;
3. stock-server and physical evidence justify a reachability contract;
4. a dependency security or maintenance event requires action; or
5. the maintainer starts a publication review with a nonzero version, immutable
   tag, and complete package metadata.

Until the publication condition is met, `0.0.0` and `publish = false` remain.
The local path dependency is suitable for development evidence, not a LeanTTY
production candidate.
