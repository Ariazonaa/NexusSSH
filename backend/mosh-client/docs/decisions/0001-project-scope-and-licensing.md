# 0001: Project scope and licensing

- Status: Accepted
- Date: 2026-08-29

## Decision

Create `mosh-client-rs` as an independent, unofficial Mosh client implementation
in Rust. License new project code under `MIT OR Apache-2.0`.

The first compatibility target is the stock `mosh-server` 1.4.0. The first
network target is IPv4 over a fixed UDP port. The project starts as one library
crate and remains unpublished until its compatibility and security gates pass.

## Why

A permissive Rust implementation lets Apache-2.0 applications use the client
without accepting GPL obligations or a Go runtime and FFI boundary. It also
gives the project direct control over ARM64 support, memory limits, cancellation,
cleanup, and network recovery.

This choice replaces license and cross-runtime risk with protocol, security, and
maintenance responsibility. The roadmap therefore uses interoperability and
terminal-correctness stop gates before application integration.

## Consequences

- The project owns client protocol correctness and security maintenance.
- The project does not copy GPL-covered implementation material.
- The caller owns SSH bootstrap, host trust, authentication, and presentation.
- The initial project does not implement a server or general-purpose CLI.
- LeanTTY integration starts only after the host-side client core interoperates
  with the stock server under adverse network tests.
