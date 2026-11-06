using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

// Vorgaben der Einstellungen: was von der Vorgabe abweicht, und der Weg zurück – je Einstellung. Die Seite der
// Einstellungen kennzeichnet damit, was geändert ist. Eine Einstellung, die dort eine Zeile bekommt, gehört in
// diese Tabelle (der Schlüssel steht an der Zeile); Tastenkürzel haben ihr eigenes Zurücksetzen.
public sealed partial class ShellViewModel
{
    private static readonly AppSettings Defaults = new();

    private Dictionary<string, (Func<bool> IsDefault, Action Reset)>? _resets;

    /// <summary>Die Schlüssel aller Einstellungen, die sich zurücksetzen lassen.</summary>
    public IReadOnlyCollection<string> ResettableSettings => Resets.Keys;

    /// <summary>Ob die Einstellung so steht wie nach der Installation. Unbekannte Schlüssel gelten als unverändert.</summary>
    public bool IsDefaultSetting(string key) => !Resets.TryGetValue(key, out var entry) || entry.IsDefault();

    /// <summary>Stellt eine Einstellung auf ihre Vorgabe zurück; <c>false</c> bei einem unbekannten Schlüssel.</summary>
    public bool ResetSetting(string key)
    {
        if (!Resets.TryGetValue(key, out var entry))
        {
            return false;
        }

        entry.Reset();

        // Manche Einstellungen schreibt die Seite direkt in die Einstellungen (Sprache, Akzentfarbe, Programme):
        // Sie sieht an dieser Meldung, dass sie ihre Felder neu füllen muss.
        OnPropertyChanged(nameof(ResettableSettings));
        return true;
    }

    private Dictionary<string, (Func<bool> IsDefault, Action Reset)> Resets => _resets ??= new(StringComparer.Ordinal)
    {
        // Allgemein
        ["Language"] = (() => Settings.Language == Defaults.Language, () => Settings.Language = Defaults.Language),
        ["RestoreTabs"] = (() => RestoreTabs == Defaults.RestoreTabs, () => RestoreTabs = Defaults.RestoreTabs),
        ["ConfirmClose"] = (() => ConfirmClose == Defaults.ConfirmClose, () => ConfirmClose = Defaults.ConfirmClose),
        ["CheckForUpdates"] = (() => CheckForUpdates == Defaults.CheckForUpdates, () => CheckForUpdates = Defaults.CheckForUpdates),

        // Aussehen
        ["Accent"] = (() => Settings.Accent == Defaults.Accent, () => Settings.Accent = Defaults.Accent),
        ["UiZoom"] = (() => UiZoom == Defaults.UiZoom, () => UiZoom = Defaults.UiZoom),
        ["Compact"] = (() => Compact == Defaults.Compact, () => Compact = Defaults.Compact),
        ["Animations"] = (() => Animations == Defaults.Animations, () => Animations = Defaults.Animations),

        // Verbindungen
        ["AutoReconnect"] = (() => AutoReconnect == Defaults.AutoReconnect, () => AutoReconnect = Defaults.AutoReconnect),
        ["RememberUnlockedKeys"] = (() => RememberUnlockedKeys == Defaults.RememberUnlockedKeys, () => RememberUnlockedKeys = Defaults.RememberUnlockedKeys),
        ["CheckReachability"] = (() => CheckReachability == Defaults.CheckReachability, () => CheckReachability = Defaults.CheckReachability),
        ["X11Display"] = (() => X11Display == Defaults.X11Display, () => X11Display = Defaults.X11Display),
        ["KeepAlive"] = (() => (KeepAliveSeconds, KeepAliveMissed) == (Defaults.KeepAliveSeconds, Defaults.KeepAliveMissed), () => (KeepAliveSeconds, KeepAliveMissed) = (Defaults.KeepAliveSeconds, Defaults.KeepAliveMissed)),
        ["MoshPredict"] = (() => MoshPredict == Defaults.MoshPredict, () => MoshPredict = Defaults.MoshPredict),

        // Hinweise und Töne
        ["Notifications"] = (() => Notifications == Defaults.Notifications, () => Notifications = Defaults.Notifications),
        ["ServerAlerts"] = (
            () => ServerAlerts == Defaults.ServerAlerts
                && Settings.AlertDiskPercent == Defaults.AlertDiskPercent
                && Settings.AlertMemoryPercent == Defaults.AlertMemoryPercent
                && Settings.AlertLoadPerCore.Equals(Defaults.AlertLoadPerCore),
            () =>
            {
                AlertDiskPercent = Defaults.AlertDiskPercent;
                AlertMemoryPercent = Defaults.AlertMemoryPercent;
                AlertLoadPerCore = Defaults.AlertLoadPerCore;
                ServerAlerts = Defaults.ServerAlerts;
            }
        ),
        ["SoundConnectFailed"] = (() => SoundConnectFailed == Defaults.SoundConnectFailed, () => SoundConnectFailed = Defaults.SoundConnectFailed),
        ["SoundAuthFailed"] = (() => SoundAuthFailed == Defaults.SoundAuthFailed, () => SoundAuthFailed = Defaults.SoundAuthFailed),
        ["SoundConnectionLost"] = (() => SoundConnectionLost == Defaults.SoundConnectionLost, () => SoundConnectionLost = Defaults.SoundConnectionLost),
        ["SoundTransferFailed"] = (() => SoundTransferFailed == Defaults.SoundTransferFailed, () => SoundTransferFailed = Defaults.SoundTransferFailed),
        ["SoundConnected"] = (() => SoundConnected == Defaults.SoundConnected, () => SoundConnected = Defaults.SoundConnected),
        ["SoundTransferDone"] = (() => SoundTransferDone == Defaults.SoundTransferDone, () => SoundTransferDone = Defaults.SoundTransferDone),
        ["SoundsOnlyInBackground"] = (() => SoundsOnlyInBackground == Defaults.SoundsOnlyInBackground, () => SoundsOnlyInBackground = Defaults.SoundsOnlyInBackground),
        ["BellTab"] = (() => BellTab == Defaults.BellTab, () => BellTab = Defaults.BellTab),
        ["BellSound"] = (() => BellSound == Defaults.BellSound, () => BellSound = Defaults.BellSound),
        ["BellTaskbar"] = (() => BellTaskbar == Defaults.BellTaskbar, () => BellTaskbar = Defaults.BellTaskbar),
        ["TerminalNotices"] = (() => TerminalNotices == Defaults.TerminalNotices, () => TerminalNotices = Defaults.TerminalNotices),

        // Terminal
        ["TerminalTheme"] = (
            () => Settings.TerminalTheme == Defaults.TerminalTheme,
            () => TerminalTheme = TerminalThemes.Find(Defaults.TerminalTheme, Settings.CustomThemes)),
        ["TerminalBackground"] = (
            () => Settings.TerminalBackgroundImage == Defaults.TerminalBackgroundImage && Settings.TerminalBackgroundDim == Defaults.TerminalBackgroundDim,
            () =>
            {
                TerminalBackgroundImage = Defaults.TerminalBackgroundImage;
                TerminalBackgroundDim = Defaults.TerminalBackgroundDim;
            }
        ),
        ["TerminalFont"] = (() => TerminalFont == Defaults.TerminalFont, () => TerminalFont = Defaults.TerminalFont),
        ["TerminalFontSize"] = (() => TerminalFontSize == Defaults.TerminalFontSize, () => TerminalFontSize = Defaults.TerminalFontSize),
        ["TerminalCursor"] = (
            () => TerminalCursor == Defaults.TerminalCursor && TerminalCursorBlink == Defaults.TerminalCursorBlink,
            () =>
            {
                TerminalCursor = Defaults.TerminalCursor;
                TerminalCursorBlink = Defaults.TerminalCursorBlink;
            }
        ),
        ["TerminalSpacing"] = (
            () => TerminalLineHeight.Equals(Defaults.TerminalLineHeight) && TerminalLetterSpacing == Defaults.TerminalLetterSpacing,
            () =>
            {
                TerminalLineHeight = Defaults.TerminalLineHeight;
                TerminalLetterSpacing = Defaults.TerminalLetterSpacing;
            }
        ),
        ["TerminalLigatures"] = (() => TerminalLigatures == Defaults.TerminalLigatures, () => TerminalLigatures = Defaults.TerminalLigatures),
        ["Highlights"] = (() => Highlights == Defaults.Highlights, () => Highlights = Defaults.Highlights),
        ["MouseCopyPaste"] = (() => MouseCopyPaste == Defaults.MouseCopyPaste, () => MouseCopyPaste = Defaults.MouseCopyPaste),
        ["McpEnabled"] = (() => McpEnabled == Defaults.McpEnabled, () => McpEnabled = Defaults.McpEnabled),
        ["PasteClean"] = (
            () => PasteClean == Defaults.PasteClean && PasteTabs == Defaults.PasteTabs,
            () =>
            {
                PasteClean = Defaults.PasteClean;
                PasteTabs = Defaults.PasteTabs;
            }
        ),
        ["SnippetSuggestions"] = (() => SnippetSuggestions == Defaults.SnippetSuggestions, () => SnippetSuggestions = Defaults.SnippetSuggestions),
        ["OptionSuggestions"] = (() => OptionSuggestions == Defaults.OptionSuggestions, () => OptionSuggestions = Defaults.OptionSuggestions),
        ["PathSuggestions"] = (() => PathSuggestions == Defaults.PathSuggestions, () => PathSuggestions = Defaults.PathSuggestions),
        ["CommandSuggestions"] = (() => CommandSuggestions == Defaults.CommandSuggestions, () => CommandSuggestions = Defaults.CommandSuggestions),
        ["KnownNameSuggestions"] = (() => KnownNameSuggestions == Defaults.KnownNameSuggestions, () => KnownNameSuggestions = Defaults.KnownNameSuggestions),
        ["TabCompletes"] = (() => TabCompletes == Defaults.TabCompletes, () => TabCompletes = Defaults.TabCompletes),
        ["ServerTabTitles"] = (() => ServerTabTitles == Defaults.ServerTabTitles, () => ServerTabTitles = Defaults.ServerTabTitles),
        ["MonitorBar"] = (() => MonitorBar == Defaults.MonitorBar, () => MonitorBar = Defaults.MonitorBar),
        ["TerminalImages"] = (() => TerminalImages == Defaults.TerminalImages, () => TerminalImages = Defaults.TerminalImages),

        // Dateien und Transfers
        ["EditorPath"] = (() => Settings.EditorPath == Defaults.EditorPath, () => Settings.EditorPath = Defaults.EditorPath),
        ["LocalShell"] = (() => Settings.LocalShell == Defaults.LocalShell, () => DefaultLocalShell = Defaults.LocalShell),
        ["ComparePath"] = (() => Settings.ComparePath == Defaults.ComparePath, () => Settings.ComparePath = Defaults.ComparePath),
        ["TransferRule"] = (() => Settings.TransferRule == Defaults.TransferRule, () => Settings.TransferRule = Defaults.TransferRule),
        ["TransferLimitKib"] = (() => TransferLimitKib == Defaults.TransferLimitKib, () => TransferLimitKib = Defaults.TransferLimitKib),
        ["ExcludePatterns"] = (() => Settings.ExcludePatterns.SequenceEqual(Defaults.ExcludePatterns), () => ExcludePatternsText = string.Join((char)10, Defaults.ExcludePatterns)),
        ["TransferLog"] = (() => KeepTransferLog == Defaults.TransferLog, () => KeepTransferLog = Defaults.TransferLog),
    };
}
