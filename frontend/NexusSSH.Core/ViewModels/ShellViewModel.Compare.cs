using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Datei, die zum Vergleichen vorgemerkt ist – sie kann auf einem anderen Server liegen als die zweite.</summary>
public sealed record CompareMark(SessionViewModel Session, string Path, RemoteEntry Entry)
{
    public string Label => $"{Entry.DisplayName} ({Session.Profile.Name})";
}

// Zwei Dateien im eingestellten Vergleichsprogramm öffnen: Server gegen Server (auch zwei verschiedene) oder
// Server gegen diesen Rechner. Die Dateien des Servers werden dafür wie für die Vorschau in einen Arbeitsordner
// geholt, der mit der App verschwindet; gestartet wird das Programm mit den beiden Dateien als Argumenten – nie
// eine der Dateien selbst und nie über eine Befehlszeile.
public sealed partial class ShellViewModel
{
    private IEditorHost? _compareHost;

    /// <summary>Die Datei, die zum Vergleichen vorgemerkt ist.</summary>
    [ObservableProperty]
    public partial CompareMark? CompareMarked { get; private set; }

    public void MarkForCompare(SessionViewModel session, RemoteEntry entry)
        => CompareMarked = new CompareMark(session, session.Files.PathOf(entry), entry);

    /// <summary>Vergleicht eine Datei des Servers mit der vorgemerkten. <c>false</c>, wenn es nicht dazu kam; warum, steht im Protokoll.</summary>
    public async Task<bool> CompareWithMarkedAsync(SessionViewModel session, RemoteEntry entry)
    {
        if (CompareMarked is not { } marked)
        {
            return false;
        }

        var done = await CompareAsync(marked.Session, marked.Entry, marked.Path, session, entry, session.Files.PathOf(entry));
        if (done)
        {
            CompareMarked = null;
        }

        return done;
    }

    /// <summary>Vergleicht zwei Dateien des Servers miteinander – der einen Sitzung oder zweier verschiedener.</summary>
    public async Task<bool> CompareAsync(SessionViewModel leftSession, RemoteEntry left, string leftPath, SessionViewModel rightSession, RemoteEntry right, string rightPath)
        => await FetchForCompareAsync(leftSession, left, leftPath) is { } first
            && await FetchForCompareAsync(rightSession, right, rightPath) is { } second
            && Compare(first, second);

    /// <summary>Vergleicht eine lokale Datei (links) mit einer des Servers (rechts).</summary>
    public async Task<bool> CompareWithLocalAsync(SessionViewModel session, RemoteEntry entry, string localFile)
        => await FetchForCompareAsync(session, entry, session.Files.PathOf(entry)) is { } fetched && Compare(localFile, fetched);

    private async Task<string?> FetchForCompareAsync(SessionViewModel session, RemoteEntry entry, string path)
    {
        if (_drops is null || _compareHost is null || session.Files.FileSystem is not { } files || entry.IsDirectory || entry.IsSpecial)
        {
            return null;
        }

        if (entry.Size > RemoteEditor.MaxSize)
        {
            Log(L.Get("LogEditTooLarge", entry.DisplayName, Format.FileSize(RemoteEditor.MaxSize, isDirectory: false)), LogSeverity.Warning);
            return null;
        }

        var (local, failure) = await RemotePreview.FetchAsync(_backend.Transfers, files, path, _drops.NewFolder());
        if (local is null)
        {
            Log(L.Get("LogEditFailed", entry.DisplayName, session.Profile.Name, FailureText.OfTransfer(failure)), LogSeverity.Error);
        }

        return local;
    }

    /// <summary>Die Datei, die gerade angesehen oder bearbeitet wird – eine Seite über den Bereichen; <c>null</c>: keine.</summary>
    [ObservableProperty]
    public partial FileViewerViewModel? Viewer { get; private set; }

    /// <summary>Zeigt eine Datei des Servers im Betrachter: Text mit Farben, Hex, Bild oder PDF – und zum Bearbeiten.</summary>
    public void OpenViewer(SessionViewModel session, RemoteEntry entry)
    {
        if (session.Files.FileSystem is not { } files || entry.IsDirectory || entry.IsSpecial)
        {
            return;
        }

        // Bilder und PDF brauchen eine Kopie auf diesem Rechner – im Arbeitsordner, der mit der App verschwindet.
        Func<string, Task<(string? LocalPath, string? Failure)>>? fetch = _drops is { } drops
            ? path => RemotePreview.FetchAsync(_backend.Transfers, files, path, drops.NewFolder())
            : null;
        IsSettingsOpen = false;
        IsHelpOpen = false;
        Viewer = new FileViewerViewModel(files, _prompts, session.Profile.Name, session.Files.PathOf(entry), fetch);
        _ = Viewer.LoadAsync();
    }

    /// <summary>Schließt den Betrachter. Ist im Editor etwas ungespeichert, wird vorher gefragt.</summary>
    public async Task<bool> CloseViewerAsync()
    {
        if (Viewer is { IsDirty: true } viewer
            && !await _prompts.ConfirmAsync(L.Get("ViewerDiscardTitle", viewer.Name), L.Get("ViewerDiscardText"), L.Get("ViewerDiscard")))
        {
            return false;
        }

        Viewer = null;
        return true;
    }

    /// <summary>Verlässt den Editor und zeigt wieder die Datei des Servers – mit derselben Rückfrage.</summary>
    public async Task<bool> LeaveEditorAsync()
    {
        if (Viewer is not { } viewer
            || (viewer.IsDirty && !await _prompts.ConfirmAsync(L.Get("ViewerDiscardTitle", viewer.Name), L.Get("ViewerDiscardText"), L.Get("ViewerDiscard"))))
        {
            return false;
        }

        await viewer.LeaveEditorAsync();
        return true;
    }

    /// <summary>Das Mitlesen einer Datei im unteren Bereich – eines zur Zeit.</summary>
    public FileFollowViewModel Follow => _follow ??= new FileFollowViewModel(_dispatcher);

    private FileFollowViewModel? _follow;

    /// <summary>Liest eine Datei des Servers mit (wie <c>tail -f</c>) und zeigt den unteren Bereich.</summary>
    public void FollowFile(SessionViewModel session, RemoteEntry entry)
    {
        if (session.Files.FileSystem is not { } files || entry.IsDirectory || entry.IsSpecial)
        {
            return;
        }

        IsBottomPanelVisible = true;
        Follow.Start(files, session.Files.PathOf(entry), session.Profile.Name);
    }

    private bool Compare(string left, string right)
    {
        switch (_compareHost?.Compare(left, right))
        {
            case CompareOutcome.Started:
                Log(L.Get("LogCompareStarted", Format.Visible(Path.GetFileName(left)), Format.Visible(Path.GetFileName(right))));
                return true;

            case CompareOutcome.NoProgram:
                Log(L.Get("LogCompareNoProgram"), LogSeverity.Warning);
                return false;

            case CompareOutcome.NotAProgram:
                Log(L.Get("LogCompareNotAProgram"), LogSeverity.Warning);
                return false;

            default:
                Log(L.Get("LogCompareFailed"), LogSeverity.Error);
                return false;
        }
    }
}
