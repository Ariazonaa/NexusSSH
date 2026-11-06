# LeanTTY physical reachability evidence

- Date: 2026-08-30
- Consumer: LeanTTY native Mosh owner using `mosh-client-rs`
- Device: physical ARM64 HarmonyOS PC
- Peer: unmodified stock `mosh-server` 1.4.0 in a controlled PTY
- Network: fixed IPv4 endpoint and UDP port, with local controlled impairment

## Recoverable interruption

The fixture established an interactive shell, then dropped both directions of
the Session's UDP traffic for about 6.2 seconds. The public lifecycle remained
`Active`; the Session, stock-server process, and remote terminal process stayed
alive. After traffic resumed, the same Session and terminal process executed a
new exact command. Authenticated graceful close then stopped the server.

This proves that an active Session needs a temporary interruption signal. It
also proves that silence at this duration must not close the Session.

LeanTTY evidence identifier:
`device-mosh-20260830T113251351Z/device-mosh.json`.

## Silent server termination

The fixture established a working Session, completed an exact command, and
then sent `SIGKILL` to the controlled stock-server process. The remote terminal
process exited, but the client observed only network silence. The Session did
not close or fail during the observation window. The user requested local
close; without a peer acknowledgement, the existing four-second bounded close
window completed as `SessionExit::LocalClosed`.

This proves that server termination and a recoverable network outage can have
the same client-visible signal. Silence cannot support `RemoteClosed`,
`ServerDisappeared`, or another permanent peer-death result.

LeanTTY evidence identifier:
`device-mosh-20260830T113943049Z/device-mosh.json`.

## Retained data

This record retains only the controlled scenario and result. It contains no
session key, credential, packet capture, terminal transcript, user path, or
host address. The detailed device logs remain in LeanTTY's local verification
area and are not a runtime or release dependency of this crate.
