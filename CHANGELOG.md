# Changelog

Newest version first. For everything NexusSSH can do, see [FEATURES.md](FEATURES.md).

## 0.6.0 – 2026-11-20

- **Dragging into Explorer downloads directly.** Files and folders dragged from the server list into an Explorer
  window, onto a folder in it or in the navigation pane, or onto the desktop are now downloaded by the app itself
  into that folder – with progress and Cancel in its transfers and its own question before overwriting, without
  Explorer's copy dialog that sat at "Calculating time" until everything was loaded. Folders with paths over 259
  characters or more than 20,000 files no longer fail. Other programs, such as a browser, get the files as before
- **Known limits:** dropping onto a ZIP file in Explorer no longer works – Explorer reports an error, nothing is
  downloaded, and the app notes it under Problems; download first, then add to the archive. Copying (Ctrl+C) and
  pasting into Explorer still shows Explorer's copy dialog

## 0.5.0 – 2026-11-19

- **Deleting shows in the bottom bar like a transfer.** Deleting on the server – and "Delete permanently" in the
  local column – no longer seems to do nothing for minutes on a large folder: the Transfers pill shows a bin, the name
  and how many items are already gone ("node_modules · 1,234 deleted"), then "deleted" in green or "delete failed" in
  red. Each deletion has a row in the transfers with its count and a **Cancel** button that stops after the current
  item. Several selected items are one deletion, and the app stays usable meanwhile

## 0.4.0 – 2026-11-19

- **Transfers show in the bottom bar.** While an up- or download runs, the Transfers pill turns blue and shows the
  direction, the file name (or how many), the percentage and a thin progress bar; the tooltip adds server, speed and
  time left. Afterwards it says how it went: done stays green for five seconds, a failure stays red until clicked,
  paused shows amber. Screen readers hear the change of state, not every percent
- **Fixed:** closing the window could leave sessions, the core and a `mosh-server` on the server running
- **Fixed:** on servers with a low `MaxSessions`, closed tabs kept their channel taken, and right after connecting a
  command could be typed into a non-POSIX shell such as fish. Commands now wait for a free channel instead
- **Fixed:** errors from commands on Windows servers (Windows PowerShell 5.1) came back as CLIXML instead of text
- **Fixed:** AI access (MCP) answers could exceed their size limit, for example for long command output
- **Fixed:** the folder headings in the saved commands sat a few pixels too low

## 0.3.0 – 2026-11-16

- **Select files by drawing a frame**, like in Explorer: in the server list and the local one, press next to a name or
  below the last row and drag. Ctrl adds to the selection, Shift extends it, Esc cancels, and the list scrolls along at
  its edges. Dragging on an icon or name still moves the files

## 0.2.0 – 2026-11-08

- **AI access: the prompt for a command shows its time limit.** The "Decline" button counts down, a bar below the
  command shows the same, and the limit (30 seconds to 5 minutes, two by default) is set under Settings → AI access
- **AI access: "Always allow".** A command can be allowed permanently from the prompt – exactly that text on that
  server, no patterns. It then runs without asking in a channel of its own, never typed into your terminal. The
  allowed commands are listed under Settings → AI access, where they can be removed; ending a server's release removes
  them as well
- **Settings and help close on "Home" or a tab.** Before, a click there changed what lay behind the page, and the page
  stayed in front

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
