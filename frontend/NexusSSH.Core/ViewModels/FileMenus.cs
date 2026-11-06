using NexusSSH.Core.Models;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was sich im Dateibrowser des Servers tun lässt – je Eintrag eine Stelle in Symbolzeile oder Menü.</summary>
public enum FileAction
{
    Up,
    Refresh,
    Download,
    Upload,
    NewFolder,
    Delete,
    Back,
    Forward,
    Search,
    Bookmarks,
    UploadFolder,
    NewFile,
    ShowHidden,
    ShowPermissions,
    ShowDates,
    ShowTree,
    Sort,
    Root,
    HidePane,

    /// <summary>Ordner öffnen bzw. Datei bearbeiten – derselbe Eintrag, sein Text folgt der Auswahl.</summary>
    Open,
    Rename,
    CopyPath,
    Properties,
    Preview,

    /// <summary>In die Zwischenablage von Windows (wie im Explorer), geladen erst beim Einfügen.</summary>
    CopyFiles,
    Duplicate,
    Cut,

    /// <summary>Auf dem Server zum Kopieren vormerken (Gegenstück zu „Ausschneiden“).</summary>
    Hold,
    Paste,
    RenameMany,
    Undo,
    Pack,
    Unpack,
    Compare,
    SendToServer,
    NewLink,
    Usage,
    Follow,
    Sync,
    OpenInTerminal,

    /// <summary>Pfad als sftp-Link, scp-Ziel oder FTP-Link – je nach Profil.</summary>
    CopyPathAs,
}

/// <summary>
/// Was an der Auswahl der Dateiliste hängt, damit <see cref="FileMenus.IsEnabled"/> ohne die Oberfläche entscheiden
/// kann.
/// </summary>
public sealed record FileSelection(
    int Count,
    bool AnyTransferable,
    bool SingleFile,
    bool SingleFolder,
    bool TwoFiles,
    bool SingleArchive,
    bool CanPaste,
    bool CanUndo,
    bool HasMarked,
    bool ShellConnected)
{
    /// <summary>Die Auswahl aus den gewählten Einträgen und dem Zustand des Dateibrowsers.</summary>
    public static FileSelection Of(IReadOnlyList<RemoteEntry> selected, bool canPaste, bool canUndo, bool hasMarked, bool shellConnected)
    {
        static bool IsFile(RemoteEntry entry) => entry is { IsDirectory: false, IsSpecial: false };
        return new(
            selected.Count,
            selected.Any(entry => entry.IsTransferable),
            selected is [var one] && IsFile(one),
            selected is [{ IsDirectory: true }],
            selected is [var first, var second] && IsFile(first) && IsFile(second),
            selected is [var only] && SftpBrowserViewModel.IsArchive(only),
            canPaste,
            canUndo,
            hasMarked,
            shellConnected);
    }
}

/// <summary>
/// Wo im Dateibrowser was steht (Entwurf A, wie bei MobaXterm): oben sechs Symbole, im Menü „⋯“ die Ansicht und
/// was seltener gebraucht wird, im Kontextmenü sechs Einträge und hinter „Mehr“ der Rest. Keine Aktion fällt weg –
/// jede steht an mindestens einer dieser Stellen (Test), und die Ansicht baut ihre Menüs aus diesen Listen.
/// </summary>
public static class FileMenus
{
    /// <summary>Die Symbolzeile über der Liste.</summary>
    public static IReadOnlyList<FileAction> Toolbar { get; } =
        [FileAction.Up, FileAction.Refresh, FileAction.Download, FileAction.Upload, FileAction.NewFolder, FileAction.Delete];

    /// <summary>Das Menü „⋯“ rechts in der Symbolzeile.</summary>
    public static IReadOnlyList<FileAction> ViewMenu { get; } =
    [
        FileAction.Back, FileAction.Forward, FileAction.Search, FileAction.Bookmarks,
        FileAction.UploadFolder, FileAction.NewFile,
        FileAction.ShowHidden, FileAction.ShowPermissions, FileAction.ShowDates, FileAction.ShowTree, FileAction.Sort,
        FileAction.Root, FileAction.HidePane,
    ];

    /// <summary>Das kurze Kontextmenü der Liste.</summary>
    public static IReadOnlyList<FileAction> Context { get; } =
        [FileAction.Open, FileAction.Download, FileAction.Rename, FileAction.Delete, FileAction.CopyPath, FileAction.Properties];

    /// <summary>Was im Kontextmenü hinter „Mehr“ steht, in dieser Reihenfolge.</summary>
    public static IReadOnlyList<FileAction> More { get; } =
    [
        FileAction.Preview, FileAction.CopyFiles, FileAction.Duplicate, FileAction.Cut, FileAction.Hold, FileAction.Paste,
        FileAction.RenameMany, FileAction.Undo, FileAction.Pack, FileAction.Unpack, FileAction.Compare,
        FileAction.SendToServer, FileAction.NewLink, FileAction.Usage, FileAction.Follow, FileAction.Sync,
        FileAction.OpenInTerminal, FileAction.CopyPathAs,
    ];

    /// <summary>Vor diesen Einträgen steht im Menü „Mehr“ eine Trennlinie.</summary>
    public static bool StartsGroup(FileAction action)
        => action is FileAction.Duplicate or FileAction.Pack or FileAction.Compare or FileAction.Usage or FileAction.OpenInTerminal;

    /// <summary>
    /// Ob die Aktion mit dieser Auswahl etwas tun kann. Ohne Auswahl gilt, was dem angezeigten Ordner gilt (Pfad
    /// kopieren, im Terminal öffnen, Belegung, einfügen). Pipes, Geräte und Links ins Leere lassen sich weder
    /// öffnen noch übertragen.
    /// </summary>
    public static bool IsEnabled(FileAction action, FileSelection selection) => action switch
    {
        FileAction.Download or FileAction.CopyFiles or FileAction.SendToServer => selection.AnyTransferable,
        FileAction.Delete or FileAction.Pack or FileAction.Cut or FileAction.Hold or FileAction.Duplicate => selection.Count > 0,
        FileAction.Open => selection.SingleFile || selection.SingleFolder,
        FileAction.Rename or FileAction.Properties or FileAction.NewLink => selection.Count == 1,
        FileAction.Preview or FileAction.Follow => selection.SingleFile,
        FileAction.RenameMany => selection.Count > 1,
        FileAction.Paste => selection.CanPaste,
        FileAction.Undo => selection.CanUndo,
        FileAction.Unpack => selection.SingleArchive,
        FileAction.Compare => selection.SingleFile || selection.TwoFiles,
        FileAction.OpenInTerminal => selection.ShellConnected,
        _ => true,
    };

    /// <summary>
    /// Ob die Aktion überhaupt angeboten wird: Was Befehle am Server braucht, fehlt ohne sie (FTP, Konto nur für
    /// SFTP); an einem Windows-Server fehlt, was eine POSIX-Shell braucht (Mitlesen, „als root“). Kopieren und
    /// Duplizieren bleiben – ohne Befehle kopiert der Kern über die Dateiverbindung.
    /// </summary>
    public static bool IsShown(FileAction action, bool hasCommands, bool isWindowsServer) => action switch
    {
        FileAction.Pack or FileAction.Unpack or FileAction.Usage => hasCommands,
        FileAction.Follow or FileAction.Root => hasCommands && !isWindowsServer,
        _ => true,
    };
}
