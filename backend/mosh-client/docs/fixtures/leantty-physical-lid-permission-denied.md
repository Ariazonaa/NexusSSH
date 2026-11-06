# LeanTTY physical lid-close permission-denied evidence

- Date: 2026-09-10
- Consumer baseline: LeanTTY `adf6529`
- Library release: `v0.1.0` at
  `aed5865c1d779a989a3b0cf0c84aa046313515ee`
- Device: HAD-W32 ARM64 HarmonyOS PC, OpenHarmony 6.1.1.135
- Peer: unmodified stock `mosh-server` 1.4.0 in a controlled PTY
- Network: wired LAN path to the server; USB HDC supplied control and
  observation only

## Observation

LeanTTY established a Mosh Session and verified one controlled command. At
01:26:30.479 local time, a physical lid close hid the main window while the
workspace binding remained retained. At 01:26:31.411, the current Mosh owner
reported a transport failure with stage `mosh_udp`, native category `network`,
and Rust `ErrorKind::PermissionDenied`. LeanTTY then restored its pre-Mosh page
and returned the Pane to local idle state.

After the lid reopened, the application retained the same PID and process start
time. Before recovery input, the remote shell and stock server were still
alive, and no terminal EOF had been observed. The attempted recovery text
entered LeanTTY's local buffer; Enter did not reach the server. Cleanup removed
the controlled fixture processes, HDC reverse mapping, and temporary data
without changing persistent network settings.

This proves that `v0.1.0` can terminate an established Session on a
`PermissionDenied` UDP I/O result during this lifecycle transition. It does not
prove whether `send_to` or `recv_from` failed, which raw errno produced the
category, or which HarmonyOS policy caused it.

## Artifact identity and limits

The diagnostic HAP SHA-256 was
`9f72b112fbf8db36617a7b641e54f269126e1ce7c3a948123143ad7e27dcbb86`; its
embedded native library SHA-256 was
`c1a1257359cbeefac79bb100b0abe866bbfbf0935f377b9eebd2f03c0266afbc`.
An older formal candidate used different HAP and native bytes, so it is not a
single-variable comparison and cannot establish the same error category.

The retained LeanTTY evidence is under
`build/verification/mosh-lid-diagnosis-20260910`. This project imports only the
sanitized lifecycle, error category, process, peer, and cleanup facts. It
retains no address, credential, session key, packet contents, or terminal
transcript.

## Closing gate

LeanTTY must pin the fixed revision, rebuild its ARM64 HAP, and run the same
named `operator-lid-recovery` scenario once. The run must keep the same
application process and remote PTY, observe `Interrupted`, return to
`Responsive`, execute one exact post-resume command, and complete cleanup. If
the Session still fails, retain the safe `ErrorKind` and, when available, the
private I/O direction without recording raw user data or secrets.

## Fixed-revision attempt status

On 2026-09-10 LeanTTY built an ARM64 diagnostic HAP with library revision
`ae86bfe`. Its focused policy and native gates passed, including 56 native
tests, two input-rejection tests, and one exact 34-character physical baseline
command. The HAP SHA-256 was
`1a3986dafb3595239b2e13f50cf93abc724652a1918f1b03b7ba2c7406b44c84`.

The first attempt failed during environment preparation and did not exercise
the lid. A later attempt used the same HAP and one real lid close. The named
scenario reported `passed/stable`, and cleanup passed, but HarmonyOS destroyed
the old client process at 22:03:17.386. The reopened application had a new PID
and process start time while the old remote shell and server were still alive.

That run verified LeanTTY's process-replacement workspace behavior: it restored
the workspace warning and local Mosh help, isolated old remote content, and did
not create a replacement Session automatically. It did not preserve the same
Session or remote PTY, and the continuous log captured no `PermissionDenied`.
The destroy-cause snapshot added no useful evidence.

The run therefore neither confirms nor rejects ADR 0012's established-Session
recovery. LeanTTY restored its `v0.1.0` tag dependency. The diagnostic revision
is not formally adopted, and the closing gate remains pending until a physical
run follows the same-process branch.
