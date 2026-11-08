# Features

What NexusSSH can do today – by area, not by date. As of version 0.1.1 (phase 3 including its remaining items,
packaging).

Everything is tested against the built-in test server and most of it against real OpenSSH servers: Debian as root,
plus containers with 2FA, a key with a passphrase, Alpine/BusyBox, fish and an ordinary user, as well as FreeBSD,
OpenBSD, OpenWrt (dropbear without SFTP), OPNsense and OpenSSH on Windows Server – there as Administrator and with an
ordinary account, with `cmd` and with PowerShell as the login shell. On Windows an ordinary account may not use WMI;
the server load then comes from other sources (without paging and disk throughput).

## Window

- **The window remembers its place:** position, size and "maximized" apply again at the next start. If the screen
  from back then is missing, it opens on the nearest one
- Dark interface in pure black (AMOLED), without the desktop showing through: the panes sit as rounded cards with gaps
  on the black window; plain line icons in the title bar, home page, file browser and snippets; window buttons from
  Windows, its own icon
- **Settings as a page of their own** (at the bottom of the home page sidebar or via the command palette): the
  sections on the left, the selected one on the right – "General", "Appearance", "Connections", "Notifications and
  sounds", "Terminal", "Files and transfers", "Keyboard shortcuts", "Data", "About"
- **Search the settings:** a search box at the top shows, from all sections, only what matches what you type (every
  word must appear; case and umlauts do not matter). Anything no longer at its default carries a marker on the
  right – a click on it resets exactly that setting
- **Interface size** continuously from 70 to 160 % – on top of Windows scaling, applies immediately and to everything
  in the window (menus, dialogs, terminal): in the settings, via the command palette ("Make the interface larger",
  "Make the interface smaller", "Reset the interface size"; Ctrl+Shift+Plus, +Minus, +0) or with Ctrl+mouse wheel
  outside the terminal
- **Compact layout** selectable (denser lists, smaller corner radii and gaps; takes effect after a restart); panes and
  the home page fade in softly (can be turned off)
- **Selectable accent color** (settings): sky blue (default for new installations), like Windows, or violet, blue,
  teal, green, orange, red, pink – for progress, selection and focus; takes effect after a restart
- **Title bar**: on the left "Home" and the session tabs (as pills, with a dot for the connection state; the × only on
  the active tab and the one under the pointer; the tooltip names address and state), in the middle the command
  palette, on the right the buttons **Files** and **Snippets** (pane on/off), the **Split** menu (split right or
  down, unsplit, equal size, zoom, send to all, files in two columns) and **Search**; the empty area drags the window.
  "Disconnect", "Reconnect" and "Save this connection as a server" are in the tab's menu (right-click) and in the
  command palette
- **"Reconnect" above the terminal**: when the connection has ended, a bar with the reason and the button lies over
  the terminal; the scrollback underneath stays
- **Color tag per profile or for a whole group** (red, orange, yellow, green, blue, violet): as a stripe on the tab, as
  the color of the square on the tile and as a stripe along the left edge of every terminal – in the split view too.
  A group's color (context menu of its heading) applies to everything in and below it that has none of its own –
  against mixing up production and test
- **Custom icon or letters per server and per group** (the tile's "⋯" menu or the context menu of the group heading):
  one of twelve icons or one or two letters or digits; a group's sign applies to everything in it that has none of
  its own, and appears small in the color tag before the group name. The square on the tile shows, in this order: the
  server's own sign, otherwise the **logo of its operating system** in its brand color (once it is known), otherwise
  the group's sign, otherwise, for a local terminal, its icon, otherwise the first letter of the name
- **Pin tabs** (right-click or command palette): the tab moves to the left, gets a pin instead of the × and stays
  there – also across a restart when tabs are restored. Plus "Close all other tabs" (pinned ones stay), "Disconnect
  all" and "Reconnect all"
- **Terminal in its own window**: drag a tab from the bar far into the terminal or out of the window (released just
  below the bar it stays where it was; or right-click, command palette) – its terminal then sits in a slim window of
  its own, for example on the second screen. The content including colors moves along, the session keeps running. The
  tab stays in the main window: there the file browser and server load show the session like any other, and where
  the terminal was, it says where it is. Closing the window brings the terminal back and disconnects nothing. With
  "Reopen tabs at startup" it is back in its place after a restart
- **Reachability on the tile** (setting, off by default): a dot shows whether the server's port answers; it is checked
  only while the home page is visible, at most every three minutes
- **Home page with the servers as tiles** in columns that fill the width (up to four): favorites as a section of their
  own at the top, the groups below; each tile shows a square with a sign, the system logo or the initial letter, the
  name and the address (tags in the tooltip). In the header a **search box** that, as you type, keeps only what
  carries every word in name, host, user, group or tag, the **"+ New server"** button and a "⋯" menu with "Table",
  "Select several", "Order", "Import" and "Local terminal". A click on a tile connects; its "⋯" menu (visible on hover
  and with focus, also via right-click, Menu key or Shift+F10) edits, duplicates, deletes. "Home" (the icon before the
  tabs) leads back there at any time without closing a session – the tabs keep running (also via the command palette:
  "Home"); the home page then takes the whole space between the title bar and the bottom bar
- **Bottom bar** for the whole window: on the left the server load of the server in the active tab (see "Server
  details"), on the right **Details** (opens the "Server details" page), **Transfers n** (the number of what is
  running or waiting; a click shows and hides the bottom pane with the transfers) and **Problems n** (only when there
  are any; likewise with the problems). Without an active session, and with one that cannot be measured (local
  terminal, Telnet, serial), only the buttons on the right are there
- Layout: file browser on the left, terminal in the middle, **snippets** on the right, below (closed by default)
  Output/Transfers/Problems/Search
- **Snippets on the right**: the saved commands with name and the beginning of the command; a search box filters by
  name and text, **a click runs the command in the active terminal** (optionally with Enter right away), Enter in the
  search box the first match, Esc jumps back into the terminal. Without a connected session with a shell the entries
  are grayed out, and a sentence says why. A row's pencil (on hover) and "+" open the home page's "Snippets" page for
  editing. The column exists for a local terminal too. **Folders**: commands without a folder are at the top, below
  them each folder under its heading (icon, name, count); a click on the heading collapses and expands it, remembered
  on this computer
- Panes can be shown and hidden, the gaps between the cards can be dragged (double-click = default size) and operated
  from the keyboard (arrow keys, Home); sizes are kept
- **From pane to pane with the keyboard**: Ctrl+F6 and Ctrl+Shift+F6 switch between file browser, terminal, snippets
  (into the search box) and bottom pane – also from inside the terminal, where Tab belongs to the shell. Outside
  the terminal F6 or Shift+F6 is enough
- **Windows contrast themes and large text**: with a contrast theme (light or dark) the app takes on its colors, for
  history curves and bars too, and follows a change while it is running. The enlarged text from the Windows
  accessibility settings applies throughout the interface
- German, English, French and Spanish (the last two translated, but not yet proofread by native speakers)
- Optionally the tabs that were open on exit come back at startup – including the split

## Packaging

- **Installer** (`NexusSSH-Setup-<version>.exe`): per user, without admin rights, with uninstaller; also runs
  silently, without a window (`docs/deployment.md`)
- Native AOT build: no JIT, the window is up after about a quarter of a second
- A tag `v*` in the repo builds the setup in CI and creates a release
- **Notice of new versions**: at startup (at most once a day, can be turned off in the settings) the app checks the
  GitHub releases and shows a notice with a link to the download page when a newer version is available. Also by hand
  via the command palette ("Check for updates"). Just one request to GitHub – no account, no data

## Servers

- **Home page as a table** instead of tiles ("⋯" menu in the home page header): all servers in one list with name,
  address, group and last connection; a click on a column header sorts, a second one reverses the direction. The
  filter applies there too
- **Several servers at once** (home page, "Select several"): mark them and connect together, put them into a group,
  add or remove tags, delete
- **Introduction instead of an empty page**: as long as there is no profile, the home page shows the first three
  steps – create or import a server (with the buttons for it), connect, command palette
- **Home page with a sidebar**: five entries – "Servers", "Snippets", "Keys & identities" (one page, switchable at the
  top), "Tunnels" and "More", which expands "Synchronizations", "Known servers" and the "Overview"; the settings at the
  bottom. The command palette leads straight to every page
- **Groups within groups**: `Kunde/Projekt/Prod` as a profile's group yields folders on the home page – one level per
  view, a click on the name goes deeper, breadcrumbs lead back
- **Collapse and order groups**: a click on the heading collapses it (a small arrow shows its state); "Move up"/"Move
  down", the group's color and sign in the context menu of the heading (right-click, Menu key, Shift+F10). A profile
  changes group via "Move to group" or by dragging the tile onto a heading
- Profiles with host, port, user, name, group, tags, favorite, key file; the editor is divided into three tabs
  (Server, Sign-in, Route)
- **Notes per server:** free text in the profile (purpose, contact person, maintenance window), visible on the "Server
  details" page and findable via filter and command palette
- **Identities**: create a user and key once (home page, "Keys & identities") and choose them in several profiles –
  if the key changes, you change it in one place. They travel along with an export
- **Operating system detected**: on the first connection the app remembers the server's system (Ubuntu, Debian,
  Alpine, FreeBSD …) – from `/etc/os-release` over the file connection, otherwise from the server load; so it also
  works on servers that allow no commands. Its logo appears in the square on the tile (unless the server has its own
  sign), its name in the tooltip. If a server delivers no server load, the reason is under "Output"
- Server list by groups on the home page; create, edit, **duplicate**, delete
- **Order** selectable (the home page's "⋯" menu): as created, by name, most recently connected first (favorites
  always in front)
- Through **jump hosts** the terminal names the route when connecting ("This computer → bastion → web01"); if setting
  up the connection fails, it says at which server
- **Keep the connection alive**: how often a keepalive goes to the server and after how many unanswered ones the
  connection counts as broken – in general in the settings and per server in the profile
- **Terminal per server**: which terminal the app presents itself as (`TERM`, e.g. `vt100`), which character set the
  server speaks (UTF-8, ISO-8859-1, ISO-8859-15, Windows-1252) and which environment variables it gets (`LANG` …) –
  for old systems on which umlauts or colors otherwise arrive broken
- **Import from `~/.ssh/config`**: `HostName`, `User`, `Port`, `IdentityFile`, `ProxyJump` (also several hops as a
  chain, also written out as `user@host:port`), `ForwardAgent`; also `Include` and blocks with patterns (`Host *`);
  choose what is imported; the file is only read. Plus `IdentitiesOnly`, `ServerAliveInterval`/`ServerAliveCountMax`
  (keepalives), `LocalForward`/`RemoteForward`/`DynamicForward` (as tunnels that listen only for this computer),
  `Compression`, `ConnectTimeout`, `AddressFamily`, `SetEnv` – and `Match` blocks by host, user and local user (a
  `Match exec` never applies: the app starts nothing that is written in a file). Per server the dialog names **what
  was not imported** (`ProxyCommand`, `Ciphers` …)
- **"Offer only this key"** (in the profile, like `IdentitiesOnly`): no further keys from the agent – for agents and
  password managers with many keys
- **Profiles that follow `~/.ssh/config`** (checkbox on import or selection in a profile's editor): address, port,
  user, key file, jump hosts and agent forwarding then come from the file – at startup, on saving and a few seconds
  after the file (or an included one) changes, even while the app is running. In the editor these fields are locked.
  If the profile has an identity, it determines user and key, the file only the rest. Name, group, color, notes and
  everything else stay as they are in the profile. If the entry is missing from the file, the profile stays
  unchanged; the link can be removed in the editor
- **Import sessions** from PuTTY and WinSCP (the user's saved sessions) and from MobaXterm (its configuration or an
  exported `.mxtsessions` file): name, host, port, user, folder and key file (also PuTTY's `.ppk`); choose what is
  imported. Read-only, without passwords
- **Sessions from other programs' files**: an `ssh_config` from anywhere (also the one Termius's command-line tool
  writes with `termius export-ssh-config`), mRemoteNG (`confCons.xml`), Royal TS (`.rtsz`, unencrypted), SecureCRT
  (session files or XML export), Remmina (`.remmina`) and Xshell (`.xsh`) – several files at once, SSH and Telnet,
  with folders as groups and without passwords. Apart from MobaXterm and `ssh_config` built from the formats, not yet
  tested with real files from these programs
- **Generate and deploy keys**: a new Ed25519 key pair in `~/.ssh` (optionally with a passphrase), the public part is
  added to `authorized_keys` on the connected server and the profile is switched to the key – what `ssh-keygen` and
  `ssh-copy-id` otherwise do. Existing files stay untouched
- **Manage keys**: a home page page with your own key files (what is in `~/.ssh` and what a profile or an identity
  names) – type and length, fingerprint, comment, format, whether a passphrase protects it and who uses it, read
  without the passphrase. **Change the passphrase**, set or remove it; the file keeps its format. **Convert** between
  the OpenSSH and PuTTY formats (`.ppk`, version 3) as a new file next to it, with the same passphrase – the old one
  stays. The public key can be copied
- **"Forget saved password"** without deleting the profile
- **Export and import**: profiles (including groups and jump hosts), saved commands and settings as a file, to take
  them over on another computer – **without passwords**. Before the import the app shows what would be new and what
  would be replaced, and you choose what is imported; a profile that already exists is replaced instead of
  duplicated. Key paths in the user folder stay correct even with a different user name

## Connection

- **Quick connect**: type `user@host` or `user@host:2222` into the command palette and press Enter – the session
  opens without a server existing for it. The tab's menu and the command palette offer to make one from it ("Save
  this connection as a server"); otherwise it is gone again with the tab
- **Start with a target**: `NexusSSH.exe user@host[:port]` on the command line or a click on a link
  `ssh://[user@]host[:port]` opens a session – after a confirmation that names the target (and says whether it is a
  saved server). A link names only user, host and port, never a command or an option; without a user the matching
  saved server applies, otherwise, as with `ssh`, the account name. If the app is already running, it receives the
  target instead of opening a second one. Opening links with NexusSSH is a choice during installation (off by
  default)
- **Jump list**: a right-click on the icon in the taskbar shows the most recently connected servers; a click
  connects
- **Port forwarding (tunnels)**, per server and as with OpenSSH: **local** (`ssh -L` – a port on this computer leads
  to a service behind the server, e.g. `localhost:5432` to the database there), **remote** (`ssh -R` – a port on the
  server leads to this computer) and **dynamic** (`ssh -D` – a SOCKS proxy through the server). A tunnel is up as long
  as a connection to its server is up. The home page's "Tunnels" page lists all of them with state and switch; the
  "Server details" page shows those of the active session. Listening is only for this computer – for the whole
  network only on explicit request on the tunnel. Tunnels are not part of the export
- **X11 forwarding**: graphical programs on the server appear on this computer's X server (VcXsrv, Xming); to be
  allowed per profile, only for the target and never for a jump host. If the X server itself requires authentication
  (VcXsrv with `-auth`), the app signs the programs in with its secret from the user's `.Xauthority`
- **Serial** (COM port, Windows only): the console of a switch or microcontroller in the terminal – port and speed in
  the profile, fixed 8N1 without flow control
- **Telnet** for devices that cannot do SSH (switches, old routers): "Telnet instead of SSH" in the profile.
  Unencrypted – you have to sign in in the terminal yourself; files, server load and jump hosts are not available
  with it
- **Local terminal**: a shell of this computer in a tab – without a connection, but otherwise like any session
  (split, search, color scheme, rename tab). Only what the core itself has found is started: on Windows PowerShell 7,
  Windows PowerShell, Command Prompt, WSL (per distribution) and Git Bash, elsewhere the login shell and the shells
  from `/etc/shells`. It opens with Ctrl+Shift+T, via the command palette (one entry per shell found), "Local
  terminal" in the home page's "⋯" menu or as a saved profile with shell and start folder (if the folder is missing,
  the shell starts in the user folder, and a notice says so). Which shell Ctrl+Shift+T opens is set in the settings.
  A local tab has no file browser, no server load and no server details (the snippets on the right are there);
  closing it also ends everything the shell started. Local terminals stay on this computer: not in the export, and
  an import skips them
- **Mosh** per SSH profile ("Run the terminal over Mosh"): the terminal runs over UDP and survives dead spots and a
  change of network without the session dropping; the app shows what you type
  before the server has answered (as soon as it has confirmed a character in the line; on a slow line or in a dead
  spot underlined until it confirms it – "Typing prediction":
  automatic on a slow line, always or never). Sign-in works as always over SSH (key, agent, password, 2FA, host key
  check), then the app starts `mosh-server` on the server and connects to it over UDP; the UDP ports can be set in
  the profile (`60001:60010`). If there is no `mosh-server`, no UTF-8 locale or no UDP packet gets through (firewall,
  NAT), the terminal continues over SSH within 15 s, and a notice names the reason. The tab carries a Mosh sign, and
  if the terminal hears nothing from the server, it shows since when. **Files, server load and tunnels** stay on the
  SSH connection: if it breaks, the terminal keeps running, and they reconnect by themselves as soon as the server
  answers again (or immediately with "Reconnect now") – in the folder you were in. Limits: not through a jump host,
  no agent forwarding, no X11, UTF-8 only, and the terminal has no scrollback to scroll back through (Mosh sends only
  the screen). The counterpart is the ordinary `mosh-server` (tested with 1.4.0)
- SSH via `russh`: host key check, PTY, shell, resizing, keepalive
- Sign-in in this order: the profile's key file, SSH agent (Windows OpenSSH agent, Pageant), default keys from
  `~/.ssh`, password, server prompts (2FA); encrypted keys with a passphrase
- **Keys in Bitwarden / Vaultwarden:** if the agent holds the key the profile names, the agent signs – first and
  without asking for the passphrase. As the key file, the public part (`….pub`) is enough for this, the way Bitwarden
  stores it; that way the right one is found among many keys. Without one specified,
  the agent's first three are offered; if sign-in fails and it held more, a notice says
  what to do. A locked agent has a good minute to get unlocked; if it stays locked
  or the approval is declined in the password manager, sign-in continues with the next method, and a
  notice names the reason. Supported and tested with Bitwarden – also with a self-hosted
  vault (Vaultwarden); other password managers are not intended
- **Jump hosts** (like `ProxyJump`): one profile connects through another, also through several in a row; every
  server is checked individually and has its own sign-in
- **One connection for several sessions:** split terminals, duplicated tabs and every further tab to the same server
  share the existing connection – without a new sign-in; if that is not possible (the server's limit), the session
  connects itself. If two tabs to the same server start at the same time, the second waits for the first one's
  sign-in
- **Accounts without a shell** (SFTP only, as with many hosting providers; shell `nologin`): the session stays open
  for files – file browser and transfers work, the terminal shows what the server said
- **Agent forwarding** per profile, off by default; a jump host never gets the agent
- Unknown host key: confirmation with fingerprint. Changed key: warning with the previous and the new fingerprint side
  by side and the location of the old entry – if it is in your own `~/.ssh/known_hosts`, the confirmation says which
  line you would have to remove there yourself. Servers from `~/.ssh/known_hosts` count as known (the file is never
  changed)
- **Host certificates**: if `known_hosts` contains a certificate authority for the server (`@cert-authority`), a
  server whose key it has signed is accepted without confirmation – as with OpenSSH. A certificate that does not hold
  (expired, other name, foreign authority) is reported, and the usual confirmation for the key applies. An authority
  can be added and removed again on the home page under "Known servers"; the connection test names a server trusted
  this way. A revoked key (`@revoked`) and a revoked authority never apply
- The server's **banner** appears in the terminal before sign-in (as plain text)
- Passwords and passphrases only in the Windows Credential Manager and only on request; never in a file
- **A key with a passphrase is unlocked only once per start** (can be turned off): it stays in memory until the app
  ends – never on disk; "Forget unlocked keys" in the command palette asks again next time
- A session outlives its connection: tab and terminal content stay, "Reconnect" sets it up again
- **`exit` closes the tab:** if the shell ends after something was typed in it (`exit`, Ctrl+D), the tab closes. If
  the server kicks you out right after connecting, the tab stays with the message
- **Automatic reconnect** after a drop: with growing pauses (2 s to 60 s), at most eight attempts; not after
  "Disconnect", `exit` or a failed sign-in. Can be turned off. The tab shows what is happening ("attempt 2 in 8 s") –
  even when another tab or the home page is in front
- **Connection adjustable per profile:** IPv4 only or IPv6 only, how long to wait for the connection to be set up,
  compression for slow lines, automatic reconnect off for this server
- **Allow older algorithms** per profile, for old devices that speak none of today's (SHA-1, CBC) – with a warning,
  never by itself and never from an export file
- **If setting up the connection fails, it says why** – name cannot be resolved, port closed, no answer, no route, no
  common algorithm – each with what you can try
- **Connection test** in the profile dialog: resolves the name, opens the port, shows how the service introduces
  itself, the host key (known, unknown, changed) and the offered sign-in methods – without signing in and without
  saving anything
- **Response time and traceroute** on the "Server details" page: how long the server takes to open the port
  (measured only on click, never by itself), and the route
  there hop by hop in the "Output" tab
- **Connection history** ("History" tab): server, start, duration and how it ended – only that, nothing from the
  session
- View **known servers** in the settings (key type, fingerprint) and forget them individually

## Terminal

- **Rename tab**; optionally the tab shows the title the server sets (vim, htop, tmux)
- xterm.js: 256 colors, 10,000 lines of scrollback, Ctrl+C copies when there is a selection (otherwise interrupts),
  Ctrl+V pastes; text that another program "types" (a password manager's auto-type, text expansion, remote control)
  also arrives correctly
- Tabs, as many as you like per server; duplicate tab; switch with Ctrl+Tab; **move tabs** by dragging with the mouse
  or with Ctrl+Shift+PgUp/PgDn – the moved tab is the active one afterwards
- **Split terminal**: up to sixteen sessions in any arrangement – "side by side" (Ctrl+Shift+E) and "stacked"
  (Ctrl+Shift+O) split the active terminal in the chosen direction, also within a split and nested any way you like
  (one wide terminal on the left, three stacked on the right). A click switches the active terminal; open tabs can
  also be added. When split, every terminal has a narrow header: a dot for the connection state, the session name, a
  sign in front for a zoomed one and a × that takes this terminal out of the split (its tab stays open)
  - **Dividers** can be dragged with the mouse (each on its own) or, once clicked, moved with the arrow keys (Shift:
    larger steps, Esc back to the terminal); without a mouse, "Move the divider with the arrow keys" in the command
    palette selects the one next to the active terminal. A double-click on it gives all parts of the branch equal
    space
  - **Rearrange**: drag a terminal's header onto one of the four edges of another – it then sits there; the rest
    rearranges itself, and no terminal loses its scrollback in the process
  - **Zoom** (Ctrl+Shift+Z or double-click on the header): one terminal temporarily gets the whole area, the others
    keep running; toggling again brings them back (not saved)
  - **To the neighbor with the keyboard**: Ctrl+Shift+Arrow (rebindable like all shortcuts) – only while more than
    one terminal is visible; otherwise the keys belong to the program in the terminal
  - "Make all terminals the same size" (command palette) distributes the space evenly
  - Arrangement and sizes are kept, also across a restart (reopening tabs); an older settings file opens in the
    arrangement it had
- **Flow control**: a great deal of output (`cat` of a large file) slows the server down instead of filling memory;
  the other sessions keep running undisturbed
- **Search in the terminal** (Ctrl+Shift+F): matches highlighted, "3 of 12", next/previous, case
  sensitivity
- **Search across all terminals** in the "Search" tab: list of matches, a click jumps to the line
- **Save scrollback**: everything in the terminal as a text file
- **Log output**: on command, per session, the server's output goes into a file as readable text until you turn it
  off again; a red dot on the tab shows it. Never by itself, never what is typed
- **Type into several terminals at once**: in a split view the input optionally goes to all of its terminals – the
  same command on several servers
- **Windows notification** (and flashing taskbar icon) when a transfer finishes in the background or the terminal
  rings the bell; can be turned off
- **Messages from the terminal**: if a program sends a message with text (OSC 9, OSC 777 – e.g. at the end of a long
  command), the app marks the tab and shows a Windows notification when the window is not in front – with the server
  name before it, at most every few seconds (together with the bell); can be turned off
- **A confirmation in the background** (a session reconnects by itself and needs the password) makes the taskbar icon
  flash; the app does not take the foreground for it
- **What the bell does** is configurable: mark the tab, play a sound, flash the taskbar icon – each on its own, or
  none of them
- **Sounds for errors and events:** server unreachable, sign-in rejected, connection dropped, transfer failed (on by
  default), optionally also "connected" and "transfer finished" – each switchable on its own, with a button to listen,
  optionally only when the window is not in front. The sounds played are those of Windows; the same sound comes at
  most every few seconds
- **Find running tmux and screen sessions** on the server and attach to them: on command via the command palette
  or, remembered per server, right after connecting (offer, or attach to the same session automatically)
- **tmux control mode** (`tmux -CC`, from tmux 2.1; tested with 2.1, 2.6 and 3.7): the windows of a tmux session
  appear as tabs of the app – new, renamed and closed windows follow by themselves. Closing one of these tabs detaches;
  the session keeps running on the server and survives a dropped connection. A window's panes appear as a split
  terminal, in any arrangement of up to sixteen panes (one wide on the left, four stacked on the right …); with more,
  the window shows its active pane. The layout belongs to tmux – rearranging and zooming are not available there –,
  the dividers, however, can be dragged, and tmux gets the app's sizes. Every window brings its scrollback along
  (up to 2000 lines). "tmux: New window" in the command palette; "Rename tab" renames the window in tmux.
  With a great deal of output the app pauses the pane and redraws it afterwards, and tmux also slows down on its own
  when the line cannot keep up (tmux 3.2 and later)
- **Saved commands** (snippets): commands you create yourself, typed into the active terminal with one click in the
  column on the right or via the command palette, optionally with Enter right away; managed on the home page's
  "Snippets" page. Every command can sit in a **folder** (one level; the "Folder" field suggests the existing ones, a
  new name creates one); ↑ ↓ or Alt+Arrow move it within its folder, a new one lands in the folder of the selected
  one. The folder travels along into the export
- **Color schemes**: Black (default: Campbell on pure black), Campbell, One Half Dark, Dracula, Nord, Gruvbox Dark,
  Solarized Dark, Tango Dark
- **Images in the terminal** (Sixel and the iTerm2 protocol; setting, **off** by default): for `img2sixel`, `chafa`,
  `imgcat` or the preview in file managers. The image data comes from the server – hence the warning and fixed limits
  for size and memory
- **Image behind the terminal** (settings, none by default – then it stays with the color scheme's black): PNG, JPEG,
  WebP or BMP, with adjustable dimming so the text stays readable
- **Import your own color schemes** (settings): from an iTerm2 file (`.itermcolors`), Windows Terminal (JSON) or
  Xresources; they then appear next to the bundled ones, in the command palette too, and can be removed
  again
- **Create and edit your own color schemes**: twenty colors with color picker and preview; they travel along in the
  export. **Per profile** a scheme can be set – production, for example, always with a dark red background
- Font size 8–32 (Ctrl +, Ctrl −, Ctrl 0, Ctrl+mouse wheel); **selectable font** (suggestions or any installed one)
- Like PuTTY and MobaXterm: **selecting copies, right-click pastes** (on by default, can be turned off in the settings)
- **Adjustable cursor and spacing:** block, bar or underline, blinking or not; line and character spacing; font
  ligatures (`=>`, `!=`) on or off
- **Pasted text is cleaned up** (can be turned off): control characters and invisible characters are dropped,
  non-breaking spaces become ordinary ones, and the line break at the end is removed – a pasted line only runs with
  Enter. Optionally tabs become spaces
- **Clickable addresses and paths**: Ctrl+click opens a web address in the browser and shows a path in the file
  browser (opens the folder, selects the file)
- **Suggestions while typing** from the saved commands: a small list at the cursor, Tab accepts; can be turned off.
  What you type stays in the terminal and is not stored anywhere
- **Completion while typing**: subcommands and options of `git`, `docker`, `systemctl`, `apt`, `journalctl` and
  `tar`, and after `cd`, `ls`, `cat`, `vim` … the files and folders from the shell's directory (names with spaces
  come in quotes). The Right Arrow key accepts, Tab stays with the shell; both can be turned off individually. The app
  reads the names over the file connection – this reliably follows a shell that reports its directory (OSC 7),
  otherwise it holds until the first `cd`. One level deeper too (`cat archiv/ne…`).
  Plus: even the first word of a line from the **server's programs**; the addresses
  of the saved servers after `ssh` and `scp`. Optionally Tab also accepts a completion
- **Highlight words** by your own rules (settings): text or regular expression, per rule one of six colors of the
  color scheme – e.g. `ERROR` red, `WARN` yellow, IP addresses cyan
- Clear terminal

## Command palette

- **Define your own keyboard shortcuts** (Settings): change, remove or reset per command; the app tells you when a
  shortcut is already taken or belongs to the server in the terminal
- **Overview of all keyboard shortcuts** (Ctrl+F1 or command palette): the help page at the shortcuts section, as they
  are currently assigned; "Save for printing…" writes them as a plain HTML page that any browser can print
- Search box in the title bar (Ctrl+Shift+P): connect to servers, switch to open tabs, run commands, send saved
  commands; a leading `>` shows commands only
- Commands: add server, import, split, unsplit, enlarge terminal, move to neighbor, make terminals equal size, search,
  clear, save history, font size, disconnect, reconnect, duplicate, close tab (Ctrl+Shift+W), next/previous tab, move
  tab, refresh file list, hidden files, manage saved commands, show/hide panels, choose color scheme
- The same commands in a tab's context menu and, for splitting, in the "Split" menu of the title bar

## Files (SFTP)

- File browser for the server of the active tab: list (folders first, names sorted naturally – `datei-2` before
  `datei-10`), navigation (up, back, forward), links are followed; the path is always shown as a field and can be
  typed in (`~` = home directory, relative to the displayed directory; Enter switches, Esc discards)
- **Layout like MobaXterm**: at the top six icons without text (up, reload, download, upload, new folder, delete) and
  the **"View and more"** menu (back, forward, search on the server, bookmarks, upload folder, new file, hidden files,
  permissions, date, tree, sorting, as root, hide panel). The **context menu is short** – open or edit, download,
  rename, delete, copy path, properties –, everything else is in it under **"More"** (preview, copy, duplicate, cut,
  mark for copying, paste, rename several, undo, pack, unpack, compare, send to another server, create link, disk
  usage, follow, sync, open in terminal, copy path as). No function was lost in the process. The list shows name and
  size under small column headers (the modified date is in the tooltip and the properties), the number of entries at
  the bottom
- **"Follow the terminal folder"** (checkbox below the path, per tab, off by default): the file browser moves to the
  folder the shell reports (OSC 7; fish by itself, bash and zsh with one line in the configuration – "How?" next to it
  leads to the help). The app never types anything into the shell for this; if the shell reports nothing, the
  checkbox is gray
- **Search on the server**: search box in the file panel, finds names below the displayed folder (even without shell
  access, at most 500 hits); clicking a hit switches to its folder and selects it
- **Filter the list**: Ctrl+F in the file list (or the field at the bottom) – what remains is whatever has the text in
  its name; typing in the list, by contrast, jumps to the name, as in Explorer; Esc clears the filter, Enter opens the
  only hit
- **Files in two columns** (can be switched on: "Split" menu in the title bar or command in the command palette): on
  the left a folder on this computer, on the right the server, both at full width in place of the terminal – which
  keeps running and is back when you switch back. Left: drives, breadcrumbs, typing a path, sorted like the server
  list; new folder, rename (F2) and delete (Del, to the Recycle Bin) via right-click or "…". In between: upload the
  selection, download the selection here, or drag from one column to the other; you are asked before anything is
  overwritten. A button syncs the two displayed folders (choose direction, preview, only then transfer). The switch
  and the local folder survive a restart
- **Bookmarks for folders**, per server: in the "View and more" menu under "Bookmarks", remember the displayed folder
  and later jump there with one click
- **Send to another server**: selected files and folders into the folder another session is currently showing –
  directly from server to server, without a detour via this computer's disk; a canceled or paused attempt continues
  at the destination instead of starting over
- **Sync folders**: compare a local folder with the displayed one and transfer to or from the server only what is
  missing, differs in size or is older – with a preview of what would happen, as one job with shared progress.
  Deletion at the destination happens only on explicit request
- **Exclusions**: patterns in the settings (`.git`, `node_modules`, `*.tmp`, also paths like `build/*.map`) that are
  skipped when transferring a folder and when syncing. If the folder contains any of them, the app asks before the
  transfer what should go along anyway – one checkbox per pattern, with count and example. For a single sync the
  patterns can be changed in the dialog
- **Sync by content**: on request, sync compares checksums instead of size and time
- **Saved syncs**: a sync can be saved under a name (local folder, folder on the server, direction, comparison,
  exclusions) and run again with one click from the home page ("Synchronizations") or the command palette – over an
  existing connection to the server or a new tab, with the usual preview first
- **Preview**: view text files and images without starting an editor
- **Speed and remaining time** per transfer, **pause all** and resume later where they were, **bandwidth limit** for
  all transfers together
- **Order the queue:** via a row's menu, let a waiting transfer run earlier, later or next, and pause a single one
  (resume with "Retry")
- **Size of a folder** in the properties, calculated by the server on click
- **Pack and unpack on the server** from the context menu (under "More"): the selection as `.tar.gz`, an archive
  (`tar`, `zip`) into its folder – without overwriting anything; needs an account that may run commands
- **Organize on the server**, without the detour via this computer: cut, copy and paste between folders
  (Ctrl+X/C/V), **duplicate** (Ctrl+D), **rename several** by pattern with preview (search/replace, `{name}`, `{ext}`,
  sequential number) and **undo** for the last rename or move (Ctrl+Z). If something already exists at the
  destination, you are asked
- **Disk usage**: what takes how much space below a folder, largest first, to drill into – "where did the space go?"
  (calculated on the server)
- **Column headers** in both lists: clicking the header sorts (clicking again reverses), right-click chooses the
  columns (size, date, permissions, **owner, group, type**), dragging the edge changes the width – columns and widths
  are kept per list. Folders always come first; the local list also sorts names like the server (naturally,
  ignoring case – an `ä` comes after the `z`). Sorting also works via the "View and more" menu ("…" in the local
  list)
- **Keyboard like in Explorer**, in the server list and the local one: typing jumps to the name, Ctrl+A,
  Shift+Arrows, F2 (rename), F5 (refresh), Del and Shift+Del, Ctrl+L, Alt+D or F4 (type a path), Backspace and
  Alt+Up (one folder up), Alt+Left/Right (back, forward), in the server list Alt+Enter (properties)
- **Copy path as…**: the path as `sftp://benutzer@host/pfad`, as a target for `scp` (`benutzer@host:/pfad`, with a
  different port including `-P`) or, for FTP, as an `ftp://` or `ftps://` link – to tell someone where the file is.
  Control characters in the path never get through; anything suspicious is put in single quotes. In the local list it
  stays the bare path
- **Links**: the list names where a symbolic link points and marks links to nowhere (also for screen readers); the
  properties show the target as well. Via commands (accounts without SFTP) and on servers whose `readlink` fails
  (Synology DSM), no target is shown – then only the icon remains. Over FTP the target is shown if the server names it
  in its listing; whether it points to nowhere cannot be told there
- **"New file"** ("View and more" menu), **"Create link"** (symbolic link), **"Copy path as"** and **"Open in
  terminal"** (`cd` into the folder; the three in the context menu under "More")
- **Directory tree** above the list (off by default – the browser is a single list): expands down to the displayed
  folder and marks it, a click switches there, subfolders are only loaded when expanded. Can be shown and hidden (also
  via the command palette), its height can be dragged; when hidden it asks the server nothing
- **Hidden files** can be shown and hidden
- **Properties**: size, time, permissions (`-rw-r--r-- (644)`), owner and group with names; **change permissions,
  owner and group** like with `chmod` and `chown` (name or number) – for a folder, on request, **for everything in
  it**; folders stay enterable, links are not followed
- New folder, rename (never overwrites), delete (folders including contents, with a confirmation prompt)
- **Edit a file** (double-click, as in MobaXterm): fetch, open in the configured editor, upload again after every
  save
- **Conflict when editing**: if the file on the server has changed since it was fetched, the app asks before
  uploading instead of overwriting it
- **Transfer rules**: if something with the same name already exists at the destination, the app asks once for
  everything – overwrite, skip what is the same size and not older, transfer only newer, or keep both ("name (2)"). In
  folders the choice applies to each file individually; it can be remembered (Settings). This applies to uploading
  (also by dragging from Explorer), downloading and "Send to another server"
- **Progress in the taskbar icon**: one bar across all running transfers together, moving while the amount is not
  yet known, yellow when everything is paused
- **Transfer log** (Settings, off by default): every finished transfer gets a line in a CSV file in the data folder –
  when, direction, server, name, from where, to where, how much, result (done, failed with reason, canceled, paused).
  The file is only appended to; "Show transfer log" (also in the command palette) opens its folder. Fields are
  separated with the computer's list separator (in Germany the semicolon), so Excel splits the file into columns
  right away when opening it
- **Copying on the server even without a shell**: if an account may only use SFTP (or it is FTP), the app copies via
  the file connection instead of with `cp`
- **Compare two files** in the configured diff tool (WinMerge, Beyond Compare, VS Code …): two files on the server,
  server against local file, or, when marked, also between two servers
- **Follow a file** (`tail -f`) in the bottom panel, without a terminal: with filter, highlighting and pause. If the
  file is rotated (`app.log` → `app.log.1`, new `app.log`), the view keeps showing the new one
- **Viewer** for files on the server as a separate page: text with syntax colors, hex view, images and PDF; large
  files are read in chunks directly from the server
- **Built-in editor** for small text files (UTF-8, up to 1 MiB): Ctrl+S saves on the server, line endings and BOM are
  kept; if the file has changed on the server in the meantime, you are asked
- **File browser as root** (`sudo`), per session and only on explicit request: view, edit and transfer with root's
  permissions; clearly marked, back with one click. A password for `sudo` is only asked for if the server requires
  it, and never stored
- **Files without SFTP**: if a server has no SFTP but allows commands (devices, BusyBox), the file browser keeps
  working via shell commands – slower, but with listing, editing and transferring
- **Servers on Windows** (Microsoft's OpenSSH): terminal with `cmd.exe` or PowerShell, files via its SFTP server. The
  core detects the server after connecting and sends its commands there as PowerShell: **server load** in the bottom
  row, on "Server details", in the overview and for threshold alerts (CPU including cores, memory, swap, drives,
  network with addresses, disks, uptime), in the file browser **disk usage, copying and moving on the server, packing
  and unpacking** (`.tar.gz` and ZIP, with Windows' `tar`) and **sync by content**. Following, "as root", logs and
  attaching to tmux exist only on Linux and the BSDs – they are hidden there. If an account without admin rights
  creates a link, the message says that Windows usually allows this only for administrators. For an account without
  commands, everything that needs them is hidden; copying and duplicating then go via the file connection
- **FTP/FTPS**: a profile can point to an FTP server instead of SSH – files only, no terminal. Encrypted with TLS,
  including the data connections; a certificate Windows does not know is confirmed by fingerprint, remembered and
  reported if it changes. Unencrypted FTP is only available on explicit request in the profile, with a warning on
  every connect. File browser, transfers including folders, resuming and editing work as over SFTP. Tested against
  vsftpd, ProFTPD, Pure-FTPd and FileZilla Server, with and without TLS; if a server does not keep the resume
  position, the app notices and takes a safe detour. A folder
  transfers two files at once over FTP (servers limit the number of connections). What needs commands on the server
  (packing, copying on the server, disk usage, following, as root) is not offered by the file browser
  there. Also **implicit FTPS** (TLS from the first byte, usually port 990) as an option in the profile. If the
  server allows no further connection, this is shown with its text under "Problems". For servers without passive
  mode there is, on request, **active mode** (the server opens the data connections to this computer).
  For old servers that do not write file names as UTF-8, the profile names the **file name encoding**
  (ISO-8859-1 or Windows-1252) – then umlauts appear correctly in the list and arrive correctly at the server
- Upload and download of files and whole folders (**"Upload folder…"** or by dragging from Explorer); the folder
  dialog starts at the destination of the last download
- **Into Explorer**: drag files and whole folders from the list into an Explorer window, or **copy** them (Ctrl+C,
  context menu) and paste them there – any size, even mixed selections. Loading only starts once dropped or pasted:
  Explorer shows its copy dialog, the app the transfer; "Cancel" on either side ends both. A browser accepts the
  files too. While dragging, Windows shows an image (file or folder icon) and below it what happens on release
  ("Copy to Downloads")
- **Many small files**: up to eight files of a folder are in flight at once – 3,000 small files take about 45 s over
  a line with 23 ms latency (`scp -r`: 300 s)
- **Many folders**: scanning a tree before the download (and before dragging into Explorer) also runs in parallel,
  16 directories at once – 421 directories are scanned over the same line in 2.7 s instead of
  40 s
- Transfer queue: two at once, progress by amount (for nothing but empty files, by files: "12 of 30 files"), cancel,
  **retry**, remove finished;
  a transfer keeps running when its tab closes, as long as another tab uses the same connection.
  "Retry" uses a connection that is open to the same server – even after "Reconnect" or from another
  tab
- If the server's file system stops responding, the file browser gets a **new channel** on the existing connection,
  without logging in again
- Downloads and uploads of new files go through a partial file – never half a file under the right name; a canceled
  upload cleans up its partial file on the server
- **A canceled upload continues where it stopped**: "Retry" sends only the rest for a single file – after a manual
  cancel as well as after a dropped connection, for an existing file as well as for a new one (whose partial file is
  then still on the server). Beforehand it checks that the file here is unchanged and that its beginning really is on
  the server; otherwise the upload starts over
- **A folder is not transferred in full again**: in both directions, "Retry" skips the files the previous attempt
  transferred completely and continues those that were in flight – in each case only if the source is the same since
  then (size and modification time) and what arrived at the destination is still there
- **A canceled download continues** – whether the connection dropped or you canceled it yourself: its partial file
  stays, and "Retry" loads only the rest, as long as the file on the server is the same. If you remove it from the
  list ("Remove finished"), the partial file goes with it
- The row in the Transfer Center says how much was already there from the previous attempt ("… – 142 MB from the
  previous attempt")
- **This survives a restart**: failed and canceled transfers are back in the list after the next start, and "Retry"
  continues as before. Whatever was still running when the app was closed is paused and afterwards shown as
  interrupted – it continues where it stood. Downloads for editing and for dragging into other programs are left out
- **Even after a crash** (of the app or of Windows) a download continues: its partial file is regularly flushed to
  disk along the way, and "Retry" resumes at the last safe point; folders skip what was already complete. If you
  remove the row, the partial file goes away. An upload continues after a crash as well – at the point the server has
  and the core recorded along the way, after the same comparison as otherwise
- If the connection drops during a transfer, it is shown as failed (with the reason), not as canceled; if you close
  the tab instead, it is canceled
- Protection against server file names that would leave the destination folder; files from the server are never run
- **Names Windows does not allow** (`backup-12:00.tar`, Maildir, `CON`) arrive under a substitute name
  (`backup-12_00.tar`, `_CON`) instead of not at all; if two names in a folder collide here (`Makefile` and
  `makefile`), the second gets a number. An edited file goes back under its real name
- Names with line breaks, tabs or reversed writing direction are shown defused (␊, ␉)
- **Pipes, devices and sockets** are recognizable as such and are never opened – opening a named pipe would stall the
  session's file browser
- If a transfer fails, the reason is shown as a sentence ("Permission denied.") instead of an error code

## Server load and server details

- **Server load in the bottom row** (like "Remote monitoring" in MobaXterm, on by default): CPU and RAM with bars,
  network, disks, users and uptime of the server in the active tab. The "Monitoring" checkbox in front of it turns
  measuring off (also in the settings and via the command palette); SSH only, not in "Terminal only"
- **"Server details" page** ("Details" in the bottom row or command palette): everything else about the server of the
  active session, as a page above the panels; it closes with Esc, the ×, or when another tab or the home page is in
  front. Plus address, group, notes, this connection's tunnels with a switch, the response time (only on click),
  traceroute, "Show history" and the way to the logs
- Measured for Linux, FreeBSD and OpenBSD servers, only while the bottom row with monitoring switched on or the
  "Server details" page is showing the active tab. If measuring goes silent (say, a hanging drive on the server), the
  app says so and retries on its own after growing pauses
- **Each core separately** as a bar (Linux, FreeBSD) and **what the load consists of**: user, system, I/O wait, stolen
- **Network per interface**: each network card with its throughput and its addresses (IPv4
  and IPv6, to select and copy); ends of virtual links without their own address are left out
- **Disk throughput** (Linux, FreeBSD, OpenBSD): bytes read and written per second
  across all disks together, how busy the busiest one is, the history as a curve – and with several
  disks, each one separately. OpenBSD reports reading and writing only combined; that is how it is shown then
- **All file systems** with mount point, usage and inodes; the bar turns yellow from 90 % and red from 95 % – also
  when only the inodes run out. In-memory file systems, snap images and Docker layers are left out
- **View logs** (command palette: "View the server's logs"): a page with the systemd journal – by time range,
  severity and unit – or the last lines of a file under `/var/log` (including rotated `.gz`). A field filters what was
  read by text; errors are red, warnings yellow; the shown or selected lines can be copied. Without a journal
  (BusyBox, BSD) the files remain
- **More about the system**: computer model, processor and number of cores, kind of virtualization, time and time zone
  of the server – and how far its clock deviates from your own
- **Overview of all open sessions** (home page → "Overview" or command palette): one row per session with CPU,
  memory, disks and load; a click opens the session. All of them are measured only while the page is
  visible
- **Threshold alerts** (on request, in the settings): reports for open sessions when a file system, memory or the
  load per core is above the configured value – once per crossing, under "Problems" and as a Windows
  notification
- **Terminal only** (F11 or command palette): title bar, file browser, snippets, bottom panel and the bottom row give
  way, the window fills the screen; the same key brings everything back
- Servers without `/proc` and without these counters, or without permitted commands: a notice instead of values

## Bottom panel

- Closed on a new installation; "Transfers n" and "Problems n" in the bottom row show and hide it with the matching
  tab, the command palette with "Toggle bottom panel"
- **Output**: the app's log
- **Transfers**: all uploads and downloads
- **Problems**: warnings and errors (failed connections and transfers, notices from the core), with a counter in the
  tab
- **Search**: hits from the history of all terminals

## AI access (MCP)

Programs like Claude Code can use the app's connections via MCP, for example to track down an error on a server. They
never get keys or passwords.

- **Off by default**: switch it on and release servers under Settings → AI access. The line for setting it up in
  Claude Code is there too. Both apply only on this computer and are not in the export
- **Released servers only**: an AI program does not see others at all, not even under "Output". If a profile later
  points to a different target, its release ends. Local terminals, Telnet and serial connections are never
  reachable
- **Read**: list of servers, the app's "Output" and "Problems", directories, text files, the journal and
  log files, the server load
- **Connect**: an AI program can connect to a released server. This happens in a tab in the
  background; prompts for a password or host key come to you as always
- **Commands only after a confirmation prompt**: every command is shown in full in the dialog, "Decline" is the
  default. Without an answer within two minutes it counts as declined
- **Visible in the terminal**: the command is typed into the session's shell, you see it along with its output, and
  `cd` and variables are kept. A partly typed line is lost in the process. If `sudo` asks for the password, you type
  it yourself; the AI program does not see it. If a program occupies the whole screen (vim, htop, less, tmux), nothing
  is typed and the command is declined. The app presses Enter only once the shell shows exactly the approved line: if
  it reads the keys differently, say in vi command mode or at a password prompt, nothing runs. The app does not
  recognize other running programs, such as `tail -f`: the text would land there, hence the confirmation prompt. If a
  command exceeds its time limit, it keeps running in the terminal
- **Separate channel where typing does not work**: with fish and csh, Windows servers (commands there are
  PowerShell), Mosh, a character set other than UTF-8, tmux control mode and commands with control characters. The
  confirmation prompt says which way it is
- **Visible**: every call is listed under "Output", and the tab carries a badge while work is going on there
- **Limits**: what an AI program reads goes to its provider. Text from servers can lead an AI astray,
  hence the confirmation prompts. Any program under your Windows account can reach the access while it is switched on

## Under the hood

- Rust core as a DLL, C# UI in WinUI 3; everything platform-independent lives outside the WinUI project, so that a
  Linux version remains possible
- C# and Rust tests (many against the test server in the same process, also via jump hosts and with an
  SSH agent); in addition, the published AOT build can be started in a self-test
- Fuzz tests of the inputs from outside (ssh-config, Telnet, the reply of `mosh-server`, server load, `ls` and FTP
  listings, import formats, messages from the terminal page) on every test run, plus targets for `cargo fuzz`
- Diagnostic package for a bug report: versions, log, output and settings as a ZIP – names of
  servers, users and groups, addresses and paths are replaced or left out
- Copy the selection with formatting (text, HTML and RTF with colors and font) or as a single line, both from
  the command palette
- Help page with the most important moves, all keyboard shortcuts and what is new in the version – after an
  update it opens once by itself
- Confirmation prompt before quitting while sessions are connected or transfers are running (can be turned off)
- Verbose log for a connection on request: what was negotiated and which login was tried –
  without secrets
- Server details: swap and memory breakdown; history of CPU and memory over the whole
  session with a selectable time range and export as CSV
- At startup the app checks whether anyone besides the user and the computer's administrators may write to the data
  folder, and names them under "Problems"
- A directory that reports more than a million entries is not read any further
- Fixed runs against the published build: self-test with a real connection including a transfer and the terminal's
  speed (`tools/self-test.ps1 -TestServer …`), startup time and memory against upper limits
  (`tools/measure-start.ps1`, `tools/budgets.json`), operation via UI Automation (`tools/ui-test.ps1`)
- `nexus-testserver`: SSH server with a simulated shell, SFTP, forwarding, banner and simulated server load
- Mosh via the checked-in library `backend/mosh-client`
  ([`mosh-client-rs`](https://github.com/wandcs/mosh-client-rs), MIT or Apache-2.0); what was changed compared to the
  original is listed with reasons and tests in `backend/mosh-client/PATCHES.md`
- `NexusSSH.exe --demo`: sample data without a network – three server tabs and six snippets, as in the screenshots of
  the UI; `--demo-full`: the more detailed sample data the test runs work with
- CI: Linux (Rust and the platform-independent C# tests) and Windows (whole solution)

## Not yet

Docker, Kubernetes, plugins, AI assistant (Phase 4); a command history in the app
(deliberately not, see `docs/architecture.md` §6 – that is what the saved commands are for);
automatic synchronization between installations (manually it works via export and import).
