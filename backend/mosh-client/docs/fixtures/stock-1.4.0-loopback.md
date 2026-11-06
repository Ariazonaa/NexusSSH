# Stock Mosh 1.4.0 loopback observations

> Observation date: 2026-08-29
>
> Environment: Ubuntu 26.04 WSL, stock `mosh-client` 1.4.0, stock
> `mosh-server` 1.4.0, IPv4 loopback

## Method

A temporary in-process UDP relay connected an unmodified stock client to an
unmodified stock server running `/bin/sh`. The client terminal was 80 columns
by 24 rows with `TERM=xterm-256color` and `LANG=C.UTF-8`.

The relay read the bootstrap key directly from the server process, used it only
in memory, and attempted AES-128-OCB3 authentication with a candidate nonce. It
recorded direction, sequence, lengths, timestamps, fragment structure, and
Protocol Buffers field shapes. It did not print or retain the key, plaintext,
terminal content, environment, or raw packets. Both processes and the socket
were closed after each run.

## Envelope result

The candidate nonce `00000000 || datagram_header_be64` authenticated all 14
packets in the first run. Client direction used bit 63 equal to zero; server
direction used bit 63 equal to one. Both directions began at sequence zero.

Every authenticated plaintext had this shape:

```text
timestamp_be16 || timestamp_reply_be16 || fragment_id_be64 ||
fragment_number_and_final_be16 || zlib_fragment
```

Every observed message fit in one fragment. Bit 15 marked that fragment final.
Reassembly followed by zlib decompression produced a valid Protocol Buffers
message.

## Top-level shapes

The first four messages established the transport state fields:

```text
C>S seq 0: 1=2, 2=0, 3=1, 4=0, 5=0, 6=bytes(8), 7=bytes
S>C seq 0: 1=2, 2=0, 3=1, 4=1, 5=0, 6=bytes(0), 7=bytes
S>C seq 1: 1=2, 2=0, 3=2, 4=1, 5=0, 6=bytes(27), 7=bytes
C>S seq 1: 1=2, 2=1, 3=2, 4=2, 5=1, 6=bytes(0), 7=bytes
```

The variable field 7 length ranged from 1 to 15 bytes in the second run. Its
contents were not retained.

## Nested shapes

The initial 80-by-24 client difference had this field structure:

```text
field 6
  field 1
    field 3
      field 5 = 80
      field 6 = 24
```

Server differences contained repeated field 1 operations. Two observed
operation families had these structural paths:

```text
1 → 7 → 8 = varint
1 → 2 → 4 = bytes
```

Controlled one-byte input also produced the `1 → 2 → 4 = bytes` path in both
directions. Only field numbers, wire types, lengths, and non-content varints
were retained. These observations do not yet assign terminal semantics.

## Limits of the observation

The runs did not exercise multi-fragment messages, loss, reordering, replay,
roaming, timestamp wrap, large terminal state, malformed input, or alternate
screen behavior. The process used an independent Python AES-OCB3 implementation
as an analysis tool; Phase 1 must reproduce the result with the selected Rust
dependency and an automated project fixture.
