using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Tastenkürzel eines Befehls. Strg gehört immer dazu. <see cref="Code"/> ist der Name der Taste, wie
/// <c>KeyboardEvent.code</c> ihn kennt (<c>KeyF</c>, <c>Tab</c>, <c>Equal</c>) – die Terminal-Seite vergleicht
/// damit, die App übersetzt ihn für XAML.
/// </summary>
public sealed record KeyChord(string Code, bool Shift = true)
{
    /// <summary>
    /// Die Tasten, die sich belegen lassen – nur solche, die Fenster, Terminal-Seite und diese Liste gleich nennen:
    /// Buchstaben, Ziffern, F1 bis F12 und ein paar einzelne.
    /// </summary>
    public static IReadOnlyList<string> Codes { get; } = BindableList.Of<string>(
    [
        .. Enumerable.Range('A', 26).Select(letter => $"Key{(char)letter}"),
        .. Enumerable.Range(0, 10).Select(digit => $"Digit{digit}"),
        .. Enumerable.Range(1, 12).Select(number => $"F{number}"),
        "Tab", "PageUp", "PageDown", "Equal", "Minus", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown",
    ]);

    /// <summary>
    /// Ob sich das Kürzel belegen lässt: eine bekannte Taste; ohne Umschalt kein Buchstabe und kein Pfeil – Strg+Buchstabe gehört
    /// im Terminal dem Server (Strg+C, Strg+D, Strg+R …); und nicht Strg+Umschalt+C/V, die dort kopieren und einfügen.
    /// </summary>
    public bool IsAllowed
        => Codes.Contains(Code)
            && (Shift || !(Code.StartsWith("Key", StringComparison.Ordinal) || Code.StartsWith("Arrow", StringComparison.Ordinal)))
            && !(Shift && Code is "KeyC" or "KeyV");

    /// <summary>So steht das Kürzel in den Einstellungen: <c>Shift+KeyK</c> oder <c>F5</c>. Strg gehört immer dazu.</summary>
    public string ToSetting() => Shift ? $"Shift+{Code}" : Code;

    /// <summary>Liest <see cref="ToSetting"/> zurück; <c>null</c> für alles, was kein belegbares Kürzel ist.</summary>
    public static KeyChord? Parse(string? setting)
    {
        if (string.IsNullOrEmpty(setting))
        {
            return null;
        }

        var shift = setting.StartsWith("Shift+", StringComparison.Ordinal);
        var chord = new KeyChord(shift ? setting[6..] : setting, shift);
        return chord.IsAllowed ? chord : null;
    }

    /// <summary>Nur die Taste, wie sie in einer Auswahlliste steht.</summary>
    public static string KeyName(string code) => new KeyChord(code, Shift: false).Key;

    private string Key => Code switch
    {
        "Equal" => "+",
        "Minus" => "-",
        "ArrowLeft" => "←",
        "ArrowRight" => "→",
        "ArrowUp" => "↑",
        "ArrowDown" => "↓",
        "PageUp" => Named("PgUp", "Bild↑", "Pg préc", "Re Pág"),
        "PageDown" => Named("PgDn", "Bild↓", "Pg suiv", "Av Pág"),
        _ when Code.StartsWith("Key", StringComparison.Ordinal) => Code[3..],
        _ when Code.StartsWith("Digit", StringComparison.Ordinal) => Code[5..],
        _ => Code,
    };

    /// <summary>So steht das Kürzel in der Palette, in der Sprache der Oberfläche.</summary>
    public string Display
    {
        get
        {
            var key = Key;
            var control = Named("Ctrl", "Strg", "Ctrl", "Ctrl");
            var shift = Named("Shift", "Umschalt", "Maj", "Mayús");
            return Shift ? $"{control}+{shift}+{key}" : $"{control}+{key}";
        }
    }

    // Wie die Taste auf einer Tastatur der Sprache heißt.
    private static string Named(string english, string german, string french, string spanish) => L.Language switch
    {
        AppLanguage.German => german,
        AppLanguage.French => french,
        AppLanguage.Spanish => spanish,
        _ => english,
    };
}

/// <summary>Ein Befehl der App, erreichbar über die Befehlspalette und – wenn er eines hat – sein Tastenkürzel.</summary>
/// <param name="Listed">Ob er in der Palette steht (das Öffnen der Palette selbst steht dort nicht).</param>
public sealed record ShellCommand(string Id, string Title, Action Run, KeyChord? Shortcut = null, Func<bool>? CanRun = null, bool Listed = true)
{
    public bool IsAvailable => CanRun?.Invoke() ?? true;
}

public enum PaletteKind
{
    Server,
    Tab,
    Command,
    Snippet,
}

/// <summary>Eine Zeile der Befehlspalette (Spec §13, „Command Palette“).</summary>
public sealed record PaletteItem(PaletteKind Kind, string Title, string Detail, string Shortcut, Action Run)
{
    /// <summary>Titel und Einzelheit – Vorschlagslisten lesen das Screenreadern vor.</summary>
    public override string ToString() => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
}

public sealed partial class ShellViewModel
{
    /// <summary>Stellt eine Eingabe in der Palette auf „nur Befehle“.</summary>
    public const char CommandPrefix = '>';

    /// <summary>
    /// Alle festen Befehle, mit den Kürzeln, die gerade gelten (<see cref="SetShortcut"/>). Welche gerade
    /// ausführbar sind, sagt <see cref="ShellCommand.IsAvailable"/>.
    /// </summary>
    public IReadOnlyList<ShellCommand> Commands { get; private set; }

    /// <summary>Tastenkürzel, die das Terminal abfangen und melden soll – es schluckt sonst alle Tasten.</summary>
    public IReadOnlyList<TerminalShortcut> TerminalShortcuts
        => [.. Commands.Where(command => command.Shortcut is not null).Select(command => new TerminalShortcut(command.Shortcut!.Code, command.Shortcut.Shift, command.Id))];

    /// <summary>Führt den Befehl mit dieser Kennung aus. <c>false</c>, wenn es ihn nicht gibt oder er gerade nicht geht.</summary>
    public bool RunCommand(string id)
    {
        if (Commands.FirstOrDefault(command => command.Id == id) is not { IsAvailable: true } command)
        {
            return false;
        }

        command.Run();
        return true;
    }

    /// <summary>
    /// Zeilen der Palette zu einer Eingabe: Server (verbinden), offene Tabs (wechseln, daneben zeigen) und Befehle.
    /// Mit <c>&gt;</c> am Anfang nur Befehle. Jedes Wort der Eingabe muss im Titel oder in der Einzelheit vorkommen.
    /// </summary>
    public IReadOnlyList<PaletteItem> SearchPalette(string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        var commandsOnly = text.StartsWith(CommandPrefix);
        var words = (commandsOnly ? text[1..] : text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var items = new List<PaletteItem>();
        if (!commandsOnly)
        {
            items.AddRange(Ordered(_profiles)
                .Select(profile => new PaletteItem(
                    PaletteKind.Server,
                    profile.Name,
                    profile.Group.Length > 0 ? $"{profile.Address}  ·  {profile.Group}" : profile.Address,
                    string.Empty,
                    () => OpenSession(profile))));

            // Wechseln lohnt sich erst als Vorschlag, wenn man danach sucht – sonst stünde jeder Server doppelt da.
            if (words.Length > 0)
            {
                foreach (var session in Sessions.Where(session => !ReferenceEquals(session, ActiveSession)))
                {
                    items.Add(new PaletteItem(PaletteKind.Tab, L.Get("CmdSwitchTo", session.Title), session.Profile.Address, string.Empty, () => ActiveSession = session));
                }
            }
        }

        items.AddRange(Commands
            .Where(command => command is { Listed: true, IsAvailable: true })
            .Select(command => new PaletteItem(PaletteKind.Command, command.Title, string.Empty, command.Shortcut?.Display ?? string.Empty, command.Run)));

        // Jede Shell, die der Kern fand, hat ihren eigenen Eintrag – sonst gäbe es nur die eine aus den Einstellungen.
        items.AddRange(LocalShells.Select(shell => new PaletteItem(
            PaletteKind.Command,
            L.Get("CmdLocalShell", shell.DisplayName),
            string.Empty,
            string.Empty,
            () => OpenLocalTerminal(shell.Id))));

        if (CanSplit && (commandsOnly || words.Length > 0))
        {
            foreach (var session in Sessions.Where(session => !ReferenceEquals(session, ActiveSession)))
            {
                items.Add(new PaletteItem(PaletteKind.Command, L.Get("CmdSplitWith", session.Title), session.Profile.Address, string.Empty, () => SplitWith(session, sideBySide: true)));
            }
        }

        // Gemerkte Abgleiche stehen wie die Server ohne Suche da – die Vorschau davor kommt wie immer.
        if (!commandsOnly)
        {
            items.AddRange(SavedSyncs.Select(sync => new PaletteItem(
                PaletteKind.Command,
                L.Get("CmdRunSync", sync.Name),
                sync.Summary,
                string.Empty,
                () => _ = RunSavedSyncAsync(sync))));
        }

        // Gespeicherte Befehle gehen an das aktive Terminal – ohne verbundene Sitzung gibt es sie nicht.
        if (ActiveSession is { State: ConnectionState.Connected })
        {
            items.AddRange(Snippets.Select(snippet => new PaletteItem(
                PaletteKind.Snippet,
                L.Get("CmdSnippet", snippet.Name),
                FirstLine(snippet.Text),
                string.Empty,
                () => RunSnippet(snippet))));
        }

        var found = items.Where(item => words.All(word => Contains(item.Title, word) || Contains(item.Detail, word)));

        // Ein Ziel wie für `ssh` (benutzer@host[:port]): verbinden, ohne dass es einen Server dazu gibt. Zuoberst –
        // Enter nimmt den ersten Treffer –, und an der Suche vorbei: Getippt ist ein Ziel, kein Suchwort.
        if (!commandsOnly && QuickTarget.Parse(text) is { } target)
        {
            found = found.Prepend(new PaletteItem(PaletteKind.Server, L.Get("QuickConnectTo", target.Display), L.Get("QuickConnectDetail"), string.Empty, () => QuickConnect(text)));
        }

        return BindableList.Of(found);
    }

    // Was von einem gespeicherten Befehl in der Palette steht: die erste Zeile, gekürzt.
    private static string FirstLine(string text)
    {
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        var more = end >= 0;
        if (more)
        {
            line = line[..end];
        }

        const int Max = 60;
        if (line.Length > Max)
        {
            line = line[..Max];
            more = true;
        }

        return more ? $"{line}…" : line.ToString();
    }

    private List<ShellCommand> BuildCommands()
    {
        bool HasSession() => ActiveSession is not null;

        // Zum Fehlersuchen: die Verbindung neu aufbauen und jeden Schritt unter „Ausgabe“ festhalten.
        void ReconnectVerbose()
        {
            if (ActiveSession is { } session)
            {
                Log(L.Get("LogVerboseStarted", Format.Visible(session.Profile.Name)));
                session.ReconnectVerbose();
                IsBottomPanelVisible = true;
            }
        }
        bool CanMove(int delta) => ActiveSession is { } active && Sessions.IndexOf(active) + delta is var to && to >= 0 && to < Sessions.Count;
        bool CanSwitchTab() => Sessions.Count > 1 || (ActiveSession is null && Sessions.Count > 0);
        void Ask(UiRequest request) => UiRequested?.Invoke(request);

        var commands = new List<ShellCommand>
        {
            new("view.palette", L.Get("CmdPalette"), () => Ask(UiRequest.FocusCommandBar), new KeyChord("KeyP"), Listed: false),
            new("server.add", L.Get("CmdAddServer"), () => Ask(UiRequest.AddServer)),
            new("server.import", L.Get("CmdImportSshConfig"), () => Ask(UiRequest.ImportSshConfig)),
            new("server.refreshLinked", L.Get("CmdRefreshLinked"), () => RefreshLinkedProfiles()),
            new("server.importSessions", L.Get("CmdImportSessions"), () => Ask(UiRequest.ImportSessions)),
            new("server.importSessionFile", L.Get("CmdImportSessionFile"), () => Ask(UiRequest.ImportSessionFile)),
            new("data.export", L.Get("CmdExportData"), () => Ask(UiRequest.ExportData)),
            new("data.import", L.Get("CmdImportData"), () => Ask(UiRequest.ImportData)),
            new("app.diagnostics", L.Get("CmdSaveDiagnostics"), () => Ask(UiRequest.SaveDiagnostics)),
            new("transfers.log", L.Get("CmdTransferLog"), () => Ask(UiRequest.ShowTransferLog)),
            new("terminal.splitRight", L.Get("CmdSplitRight"), () => Split(sideBySide: true), new KeyChord("KeyE"), () => CanSplit),
            new("terminal.splitDown", L.Get("CmdSplitDown"), () => Split(sideBySide: false), new KeyChord("KeyO"), () => CanSplit),
            new("terminal.unsplit", L.Get("CmdUnsplit"), Unsplit, CanRun: () => ActiveSession is { IsSplit: true }),
            new("terminal.broadcast", L.Get("CmdBroadcast"), () => BroadcastInput = !BroadcastInput, CanRun: () => ActiveSession is { IsSplit: true }),
            new("terminal.zoomPane", L.Get("CmdZoomPane"), ToggleZoom, new KeyChord("KeyZ"), () => CanZoom),
            new("terminal.focusLeft", L.Get("CmdFocusLeft"), () => FocusPane(PaneDirection.Left), new KeyChord("ArrowLeft"), () => CanFocusPane),
            new("terminal.focusRight", L.Get("CmdFocusRight"), () => FocusPane(PaneDirection.Right), new KeyChord("ArrowRight"), () => CanFocusPane),
            new("terminal.focusUp", L.Get("CmdFocusUp"), () => FocusPane(PaneDirection.Up), new KeyChord("ArrowUp"), () => CanFocusPane),
            new("terminal.focusDown", L.Get("CmdFocusDown"), () => FocusPane(PaneDirection.Down), new KeyChord("ArrowDown"), () => CanFocusPane),
            new("terminal.equalize", L.Get("CmdEqualizePanes"), EqualizePanes, CanRun: () => CanEqualize),

            // Aus dem Terminal führt keine Taste zu einer Trennlinie (Tab gehört der Shell) – dieser Befehl wählt die
            // neben dem aktiven Terminal, danach schieben sie die Pfeiltasten, Esc geht zurück.
            new("terminal.focusDivider", L.Get("CmdFocusDivider"), () => Ask(UiRequest.FocusDivider), CanRun: () => CanFocusPane && Layout.Panes.Count > 1),
            new("terminal.log", L.Get("CmdToggleLog"), () => Ask(UiRequest.ToggleLog), CanRun: HasSession),
            new("terminal.find", L.Get("CmdFind"), Search.Open, new KeyChord("KeyF"), HasSession),
            new("terminal.clear", L.Get("CmdClearTerminal"), () => Ask(UiRequest.ClearTerminal), CanRun: HasSession),
            new("terminal.save", L.Get("CmdSaveTerminal"), () => Ask(UiRequest.SaveTerminal), CanRun: HasSession),
            new("terminal.copyFormatted", L.Get("CmdCopyFormatted"), () => Ask(UiRequest.CopyFormatted), CanRun: HasSession),
            new("terminal.copyOneLine", L.Get("CmdCopyOneLine"), () => Ask(UiRequest.CopyOneLine), CanRun: HasSession),
            new("server.loadHistory", L.Get("CmdLoadHistory"), () => Ask(UiRequest.ShowLoadHistory), CanRun: HasSession),
            new("session.rename", L.Get("CmdRenameTab"), () => Ask(UiRequest.RenameTab), CanRun: HasSession),
            new("terminal.zoomIn", L.Get("CmdZoomIn"), () => ZoomTerminal(+1), new KeyChord("Equal", Shift: false)),
            new("terminal.zoomOut", L.Get("CmdZoomOut"), () => ZoomTerminal(-1), new KeyChord("Minus", Shift: false)),
            new("terminal.zoomReset", L.Get("CmdZoomReset"), () => TerminalFontSize = TerminalThemes.DefaultFontSize, new KeyChord("Digit0", Shift: false)),
            new("view.zoomIn", L.Get("CmdUiZoomIn"), () => ZoomUi(+1), new KeyChord("Equal")),
            new("view.zoomOut", L.Get("CmdUiZoomOut"), () => ZoomUi(-1), new KeyChord("Minus")),
            new("view.zoomReset", L.Get("CmdUiZoomReset"), () => UiZoom = AppSettings.DefaultUiZoom, new KeyChord("Digit0")),
            new("session.reconnect", L.Get("CmdReconnect"), () => ActiveSession?.Reconnect(), CanRun: () => ActiveSession is { IsClosed: true }),
            new("session.reconnectVerbose", L.Get("CmdReconnectVerbose"), ReconnectVerbose, CanRun: HasSession),
            new("session.newLocal", L.Get("CmdNewLocal"), () => OpenLocalTerminal(), new KeyChord("KeyT")),
            new("session.attachMux", L.Get("CmdAttachMux"), () => _ = FindMuxAsync(), CanRun: () => ActiveSession is { State: ConnectionState.Connected, IsFilesOnly: false, Profile.IsLocal: false }),
            new("tmux.newWindow", L.Get("CmdTmuxNewWindow"), NewTmuxWindow, CanRun: () => IsInTmux),
            new("session.disconnect", L.Get("CmdDisconnect"), () => ActiveSession?.Disconnect(), CanRun: () => ActiveSession is { State: not (ConnectionState.Idle or ConnectionState.Closed) }),
            new("session.createKey", L.Get("CmdCreateKey"), () => Ask(UiRequest.CreateKey), CanRun: () => ActiveSession is { State: ConnectionState.Connected, IsFilesOnly: false, Profile.IsSsh: true }),
            new("session.duplicate", L.Get("CmdDuplicate"), () => OpenSession(ActiveSession!.Profile), CanRun: HasSession),
            new("session.close", L.Get("CmdCloseTab"), () => CloseSession(ActiveSession!), new KeyChord("KeyW"), HasSession),
            new("session.closeOthers", L.Get("CmdCloseOtherTabs"), () => CloseOthers(ActiveSession!), CanRun: () => HasOthersToClose(ActiveSession)),
            new("session.pin", L.Get("CmdPinTab"), () => TogglePin(ActiveSession!), CanRun: () => ActiveSession is { IsPinned: false, IsVirtual: false }),
            new("session.unpin", L.Get("CmdUnpinTab"), () => TogglePin(ActiveSession!), CanRun: () => ActiveSession is { IsPinned: true }),
            new("session.keep", L.Get("CmdKeepQuick"), () => KeepQuick(ActiveSession!), CanRun: () => CanKeepQuick),
            new("session.detach", L.Get("CmdDetachTab"), () => Detach(ActiveSession!), CanRun: () => CanDetach(ActiveSession)),
            new("session.reattach", L.Get("CmdReattachTab"), () => Reattach(ActiveSession!), CanRun: () => ActiveSession is { IsDetached: true }),
            new("session.disconnectAll", L.Get("CmdDisconnectAll"), DisconnectAll, CanRun: () => HasConnected),
            new("session.reconnectAll", L.Get("CmdReconnectAll"), ReconnectAll, CanRun: () => HasClosed),
            new("session.next", L.Get("CmdNextTab"), () => ActivateRelative(+1), new KeyChord("Tab", Shift: false), CanSwitchTab),
            new("session.previous", L.Get("CmdPreviousTab"), () => ActivateRelative(-1), new KeyChord("Tab"), CanSwitchTab),
            new("session.moveLeft", L.Get("CmdMoveTabLeft"), () => MoveActiveTab(-1), new KeyChord("PageUp"), () => CanMove(-1)),
            new("session.moveRight", L.Get("CmdMoveTabRight"), () => MoveActiveTab(+1), new KeyChord("PageDown"), () => CanMove(+1)),
            new("files.refresh", L.Get("CmdRefreshFiles"), () => _ = ActiveSession!.Files.RefreshCommand.ExecuteAsync(null), CanRun: () => ActiveSession is { Files.IsAvailable: true }),
            new("files.hidden", L.Get("CmdToggleHidden"), () => ShowHiddenFiles = !ShowHiddenFiles),
            new("files.tree", L.Get("CmdToggleTree"), () => ShowDirectoryTree = !ShowDirectoryTree),
            new("snippets.manage", L.Get("CmdManageSnippets"), () => ShowHome(HomePage.Snippets)),
            new("home.syncs", L.Get("CmdManageSyncs"), () => ShowHome(HomePage.Syncs)),
            new("home.identities", L.Get("CmdManageIdentities"), () => ShowHome(HomePage.Identities)),
            new("home.keys", L.Get("CmdManageKeys"), () => ShowHome(HomePage.Keys)),
            new("home.knownHosts", L.Get("CmdShowKnownHosts"), () => ShowHome(HomePage.KnownHosts)),
            new("home.tunnels", L.Get("CmdTunnels"), () => ShowHome(HomePage.Tunnels)),
            new("home.overview", L.Get("CmdShowOverview"), () => ShowHome(HomePage.Overview)),
            new("app.checkUpdates", L.Get("CmdCheckUpdates"), () => _ = CheckForUpdatesAsync(manual: true)),
            new("keys.forget", L.Get("CmdForgetKeys"), ForgetUnlockedKeys),
            new("view.home", L.Get("CmdHome"), () => ShowHome(HomePage.Servers), CanRun: () => HasSession() || HomePage != HomePage.Servers),
            new("view.settings", L.Get("Settings"), () => IsSettingsOpen = !IsSettingsOpen),
            new("view.help", L.Get("CmdHelp"), () => IsHelpOpen = !IsHelpOpen),
            new("server.logs", L.Get("CmdServerLogs"), () => ShowLogs(), CanRun: () => CanShowLogs),
            new("server.details", L.Get("CmdServerDetails"), ShowDetails, CanRun: () => CanShowDetails),

            // Strg+F1: F1 allein gehört dem Programm im Terminal (mc, htop), und „?“ liegt je nach Tastatur woanders.
            new("view.shortcuts", L.Get("CmdShortcuts"), ShowShortcuts, new KeyChord("F1", Shift: false)),
            new("view.sftp", L.Get("ToggleSftp"), () => IsSftpVisible = !IsSftpVisible),
            new("view.dualFiles", L.Get("ToggleDualFiles"), () => IsDualFiles = !IsDualFiles),
            new("view.terminalOnly", L.Get("CmdTerminalOnly"), ToggleTerminalOnly, CanRun: () => IsTerminalOnly || CanBeTerminalOnly),
            new("view.snippets", L.Get("ToggleSnippets"), () => IsSnippetsVisible = !IsSnippetsVisible),
            new("view.monitorBar", L.Get("CmdMonitorBar"), ToggleMonitorBar),
            new("view.bottom", L.Get("ToggleBottomPanel"), () => IsBottomPanelVisible = !IsBottomPanelVisible),

            // Der Weg aus dem Terminal heraus: Tab gehört dort der Shell. F6 allein auch (mc, htop) – deshalb mit Strg.
            new("view.nextPane", L.Get("CmdNextPane"), () => Ask(UiRequest.FocusNextPane), new KeyChord("F6", Shift: false)),
            new("view.previousPane", L.Get("CmdPreviousPane"), () => Ask(UiRequest.FocusPreviousPane), new KeyChord("F6")),
        };

        commands.AddRange(Themes.Select(theme =>
            new ShellCommand($"theme.{theme.Id}", L.Get("CmdTheme", theme.Name), () => TerminalTheme = theme)));
        return commands;
    }
}
