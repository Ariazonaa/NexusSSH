# Changelog

Newest version first. For everything NexusSSH can do, see [FEATURES.md](FEATURES.md).

## 0.1.1 – 2026-11-08

- **Fixed:** after a reconnect, a terminal kept the modes of a program that was running when the connection dropped
  (htop, vim, tmux). Mouse clicks then showed up as text like `0;64;34M` at the shell prompt. A reconnect now switches
  mouse reporting and the alternate screen off and soft-resets the terminal; its content stays

## 0.1.0 – 2026-11-06

The first public release: a Windows SSH client with a built-in SFTP file manager, a lightweight alternative to
MobaXterm. WinUI 3 front end, Rust core, shipped as a Native AOT build with a per-user installer (no admin rights).

- **Terminal:** tabs in the title bar, up to sixteen terminals split in any arrangement, search across terminals,
  eight color schemes, saved commands (snippets) in folders, a command palette (`Ctrl+Shift+P`), tmux control mode,
  terminals in their own windows, and suggestions while typing. Typing `exit` closes the tab
- **Connections:** SSH with key files, the SSH agent (Windows OpenSSH agent or Pageant, also keys held in Bitwarden
  or Vaultwarden), passwords and 2FA prompts; chained jump hosts, agent forwarding (off by default), local, remote
  and dynamic (SOCKS) tunnels, X11 forwarding, Mosh, Telnet, serial ports, FTP and FTPS, and local terminals
  (PowerShell, Command Prompt, WSL, Git Bash). Sessions to the same server share one connection and reconnect by
  themselves after a drop
- **Files:** an SFTP browser next to the terminal or in two columns, up- and download of files and whole folders,
  transfers that resume after a reconnect, an app restart or a crash, drag and drop into Explorer, edit-in-place in
  your editor, a viewer for text, hex, images and PDF, folder sync, and file access over shell commands where a
  server has no SFTP
- **Servers:** a start page with server tiles and operating system logos, `~/.ssh/config` import (also kept in sync),
  identities, known hosts with host certificate authorities, key file management (including `.ppk`), and server load
  (CPU, RAM, network, disk) for Linux, FreeBSD, OpenBSD and Windows servers under the terminal
- **AI access through MCP:** programs like Claude Code can use NexusSSH's connections, for example to debug a
  server, without ever seeing a key or password. It is off by default and set up under Settings → AI access. Only
  servers you release are visible, and every command needs your approval in a dialog that shows it in full
- **Privacy and safety:** fully local, no account, no telemetry. Passwords and passphrases live in the Windows
  Credential Manager and only on request; `~/.ssh/known_hosts` and `~/.ssh/config` are read, never written. An
  optional update check asks GitHub once a day
- **Languages:** English, German, French and Spanish
