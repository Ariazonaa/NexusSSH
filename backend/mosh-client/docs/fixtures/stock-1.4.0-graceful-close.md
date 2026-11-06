# Stock 1.4.0 authenticated graceful close

- Date: 2026-08-30
- Environment: Ubuntu 26.04 WSL x86-64, IPv4 loopback
- Oracle: unmodified `mosh-client` and `mosh-server` 1.4.0
- Terminal profile: `TERM=xterm-256color`, `LANG=C.UTF-8`, 80×24

## Sanitized observations

A project-controlled UDP endpoint started stock `mosh-client` with the public
fixture key and decoded authenticated client instructions in memory. After the
initial state, `Ctrl-^ .` produced:

```text
base_state = 0
new_state = 18446744073709551615
acknowledged_state = 0
discard_before_state = 0
state_difference = the same 8-byte initial resize difference
```

The client retransmitted while no acknowledgement was returned and exited in
approximately four seconds. No raw datagrams or session secrets were retained.

In the reverse direction, the fixture sent an authenticated server instruction
with the reserved target. Stock client replied:

```text
base_state = 1
new_state = 2
acknowledged_state = 18446744073709551615
discard_before_state = 1
state_difference = empty
```

A public project Session then proved both real-server directions. Local
`Session::close()` caused the stock server process to exit and returned
`LocalClosed`. A controlled remote shell printed `REMOTE_FINAL_OK` and exited;
the Session acknowledged the peer, delivered the final screen, and returned
`RemoteClosed`.

## Boundary

The observation establishes one authenticated clean-close exchange for stock
1.4.0. It does not classify silence, route failure, local socket errors, or an
unreachable peer as clean close. Expiry of the local acknowledgement window is
bounded local completion, not proof that the server received the request.
