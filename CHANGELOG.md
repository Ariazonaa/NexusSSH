# Changelog

Newest version first. For everything NexusSSH can do, see [FEATURES.md](FEATURES.md).

## 0.7.0 – 2026-11-23

- **Plugins** (`.nxplugin`, Settings → Extensions): code as WebAssembly, run sealed off in the core without access to
  files, network or passwords. You tick which permissions a plugin gets; its commands show up in the command palette
  and their results as text and tables. With permission it can read files of the active session and suggest
  commands on its server – each command only runs after you approve it. Plugins can react when a session connects
  or disconnects and watch the output for their patterns. A plugin that misbehaves is switched off. To write one:
  a Rust SDK, an example plugin and a guide in [`docs/plugins.md`](docs/plugins.md)
- **Extension packages** (`.nxext`, Settings → Extensions): install color schemes, saved commands, highlighting rules
  and completion for more programs from one file – no code. A preview shows everything first; commands from a
  package only run after you allow them. "Save as package" passes your own on
- **Update from the app:** "Update now" in the notice about a new version downloads the setup from GitHub, checks it
  against the size and SHA-256 GitHub names, closes NexusSSH, installs the new version and starts it again – after
  asking, and only for an installed app
- **Authorized keys on the server** (command palette, on a connected SSH session): see which keys may sign in to
  the account – type, fingerprint, comment and options, with your own key files recognised and the profile's key
  marked – remove one, or add one of your own keys. Both go over SFTP, so they also work on Windows servers and on
  accounts without a shell; removing takes out exactly that one line, and if the file changed since it was shown,
  nothing is removed
- **Activity and silence on the tab:** a dot on a tab whose session printed something while you looked elsewhere;
  "Watch for silence" in the tab menu tells you – with a clock on the tab and a Windows notification – when a
  session has been quiet for a set time, e.g. when a long build is done
- **Always ask before pasting several lines** (Settings → Terminal, off by default): until now the app only asked
  when the shell would run each pasted line at once; with the switch it also asks when the shell waits for Enter
- **Clicking a Windows notification brings NexusSSH to the front.** Until now the click did nothing: the Windows
  App SDK could not register the app for it (fixed in the SDK), and Windows did not let the window come forward
- **Fixed:** starting NexusSSH while it was already running opened a second app next to the first. The two
  overwrote each other's settings when closed – a server released for MCP in one window could be gone again – and
  AI tools kept talking to the first one, so a server released in the new window never showed up there. Now only
  one NexusSSH runs per data folder: starting it again brings the running window to the front. If NexusSSH was
  just closed and is still shutting down, the new start waits for it and then opens; if the running one does not
  respond at all, the new start says so instead of opening a second one
- **Fixed:** switching a tunnel off and on again quickly could leave it in the wrong state – running although it was
  switched off, or "port already in use" because the old listener had not let go of the port yet. The last switch
  now always wins, and a tunnel that is off has freed its port
- **Fixed:** right after the app gave up listing a huge folder on the server, the next folder could fail with
  "the directory lists more entries than this app reads" (seen once in a test run). Each listing now counts only
  the answers to its own requests
- **Fixed:** on a Windows server whose SSH login shell is Windows PowerShell, a command run by an AI tool
  (`run_command`) reported exit code 1 whenever it failed, instead of its real code
- **Updated:** Windows App SDK 2.7.0, russh 0.65.3, suppaftp 13.0.0 (FTP). FileZilla Server's directory listings
  are now read by the FTP library itself

## 0.6.1 – 2026-11-20

- **Fixed:** dropping files from the server list onto a ZIP file in Explorer showed an Explorer error ("The
  specified directory … is empty"). Explorer now simply does not accept the drop there, as before 0.6.0 – it never
  took files from the app into an archive. Download first, then add to the archive

## 0.6.0 – 2026-11-20

- **Dragging into Explorer downloads directly.** Files and folders dragged from the server list into an Explorer
  window, onto a folder in it or in the navigation pane, or onto the desktop are now downloaded by the app itself
  into that folder – with progress and Cancel in its transfers and its own question before overwriting, without
  Explorer's copy dialog that sat at "Calculating time" until everything was loaded. Folders with paths over 259
  characters or more than 20,000 files no longer fail. Other programs, such as a browser, get the files as before
- **Known limits:** dropping onto a ZIP file in Explorer shows an error from Explorer and downloads nothing (fixed
  in the next version: the drop is not accepted there, as before). Copying (Ctrl+C) and pasting into Explorer still
  shows Explorer's copy dialog

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
