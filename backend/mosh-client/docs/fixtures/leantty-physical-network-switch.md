# LeanTTY physical network-switch evidence

- Run date: 2026-09-04; record imported on 2026-09-05.
- Library revision: `94f13225aba535c6645a9179e0ce9f00b156629e`.
- Consumer: LeanTTY test-signed HAP on physical ARM64 HarmonyOS PC HAD-W32.
- Peer: stock `mosh-server` 1.4.0 reachable through both controlled LAN paths.
- Evidence: `device-mosh-network-switch-20260904-final/device-mosh.json`.
- HAP SHA-256: `59de09640022fa23e9d4529e7cdfbb715a4f6efc7a6ca18a013079141d978fc8`.

## Observation

The device switched to a saved alternate Wi-Fi network. Its source address and
route changed while the server endpoint remained reachable. Mosh moved from
`Interrupted(NoRecentContact)` to `Responsive`, kept the same Session and remote
PTY, and completed a new command about 53.3 seconds after switching began
without a user reconnecting.

In the comparison, SSH returned to the local prompt after about 23.3 seconds.
The device could still reach the server's TCP port; the user reconnected and
executed a command in a new SSH Session about 7.0 seconds later. This shows
working-context preservation, not a claim that Mosh restored responsiveness
faster than SSH plus manual reconnection.

Preferences, secret checks, fixture processes, temporary mappings, persistent
network settings, the original Wi-Fi network, and temporary-directory cleanup
passed.

## Source and limits

The source is LeanTTY's
[`docs/next-work.md` at `e2e3f0f`](https://github.com/wandcs/leantty/blob/e2e3f0f4d6dee15130b0b346097271a766f22abe/docs/next-work.md), which retains the 2026-09-04
development matrix and its `mosh-matrix-audit-20260904.md` evidence identifier.
No protocol source or consumer implementation is imported.

This is a recorded consumer experiment, not a new device run during the 0.1.0
review. It covers one PC and two controlled IPv4 LAN paths. It does not certify
LeanTTY's formal 1.6 candidate, arbitrary NAT or firewall transitions, IPv6,
other devices, or Session survival after application process termination.
No key, credential, address, SSID, packet capture, or terminal transcript is
retained here.
