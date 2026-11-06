using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// „Zwei Spalten“: links ein Ordner dieses Rechners, rechts der Dateibrowser des Servers – in voller Breite, an der
// Stelle des Terminals (das darunter weiterläuft). Eine Ansicht zum Ein- und Ausschalten, kein eigener Tab: Sie
// gilt für die aktive Sitzung, und die schmale Dateileiste links ist währenddessen weg.
public sealed partial class ShellViewModel
{
    private LocalBrowserViewModel? _local;

    /// <summary>Der lokale Ordner der Ansicht „Zwei Spalten“ – einer für alle Sitzungen; er entsteht beim ersten Gebrauch.</summary>
    public LocalBrowserViewModel Local => _local ??= NewLocal();

    /// <summary>Ob die Ansicht „Zwei Spalten“ eingeschaltet ist. Zu sehen ist sie nur mit einer aktiven Sitzung.</summary>
    public bool IsDualFiles
    {
        get => Settings.DualFiles;
        set
        {
            if (value != Settings.DualFiles)
            {
                Settings.DualFiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowsDualFiles));
                OnPropertyChanged(nameof(ShowsTerminal));
                OnPropertyChanged(nameof(ShowsDetachedNote));
                OnPropertyChanged(nameof(ShowsFiles));
            }
        }
    }

    /// <summary>
    /// Die Ansicht „Zwei Spalten“ ist zu sehen: eingeschaltet und mit einer aktiven Sitzung – die eines lokalen
    /// Terminals hat keinen Server für die rechte Spalte, dort bleibt das Terminal.
    /// </summary>
    public bool ShowsDualFiles => IsDualFiles && HasActiveSession && !IsLocalActive;

    /// <summary>Das Terminal ist zu sehen: Es gibt eine aktive Sitzung, und die zwei Spalten liegen nicht darüber.</summary>
    public bool ShowsTerminal => HasActiveSession && !ShowsDualFiles;

    /// <summary>
    /// Lädt lokale Dateien und Ordner in das Verzeichnis, das der Dateibrowser der aktiven Sitzung zeigt. Ob dort
    /// etwas überschrieben würde, fragt die Oberfläche vorher (<see cref="SftpBrowserViewModel.ExistingNames"/>).
    /// </summary>
    public bool UploadFromLocal(IEnumerable<LocalEntry> entries, ExistingRule rule = ExistingRule.Overwrite, IReadOnlyList<string>? exclude = null)
    {
        var paths = entries.Where(entry => !entry.IsDrive).Select(entry => entry.FullPath).ToList();
        if (ActiveSession is not { Files.IsAvailable: true } session || paths.Count == 0)
        {
            return false;
        }

        session.Files.Upload(paths, rule, exclude);
        return true;
    }

    /// <summary>Lädt Einträge des Servers in den lokalen Ordner, den die linke Spalte zeigt.</summary>
    public bool DownloadToLocal(IEnumerable<RemoteEntry> entries, ExistingRule rule = ExistingRule.Overwrite, IReadOnlyList<string>? exclude = null)
    {
        var wanted = entries.Where(entry => entry.IsTransferable).ToList();
        if (ActiveSession is not { Files.IsAvailable: true } session || wanted.Count == 0 || !Local.IsFolder)
        {
            return false;
        }

        session.Files.Download(wanted, Local.CurrentPath, rule, exclude);
        return true;
    }


    /// <summary>
    /// Die Ausschlüsse als Text für die Einstellungen: ein Muster je Zeile. Was kein Muster sein kann, fällt beim
    /// Setzen weg (<see cref="ExcludePatternsRejected"/> nennt es vorher).
    /// </summary>
    public string ExcludePatternsText
    {
        get => string.Join('\n', Settings.ExcludePatterns);
        set
        {
            var patterns = TransferExcludes.Parse(value);
            if (!patterns.SequenceEqual(Settings.ExcludePatterns))
            {
                Settings.ExcludePatterns = patterns;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Die Ausschlüsse, wie sie für den nächsten Transfer gelten.</summary>
    public TransferExcludes ExcludeRules => new(Settings.ExcludePatterns);

    /// <summary>Welche Zeilen eines Textes keine Muster sind – ein Satz für unter dem Feld; leer, wenn alle gelten.</summary>
    public static string ExcludePatternsRejected(string? text)
        => TransferExcludes.Rejected(text) is { Count: > 0 } rejected ? L.Get("ExcludeInvalid", Format.Visible(string.Join(", ", rejected.Take(3)))) : string.Empty;

    /// <summary>
    /// Was die Ausschlüsse in Ordnern dieses Rechners auslassen würden – für die Rückfrage vor einem Upload. Läuft
    /// neben der Oberfläche: Ein großer Ordner braucht einen Augenblick.
    /// </summary>
    public static Task<IReadOnlyList<ExcludedItem>> FindExcludedLocallyAsync(TransferExcludes rules, IEnumerable<string> paths)
    {
        var folders = paths.Select(Path.TrimEndingDirectorySeparator).Where(Directory.Exists).ToList();
        return Task.Run<IReadOnlyList<ExcludedItem>>(() =>
        [
            .. folders.SelectMany(folder => rules.ScanLocal(folder).Select(item => item with { Path = $"{Path.GetFileName(folder)}/{item.Path}" })),
        ]);
    }

    private LocalBrowserViewModel NewLocal()
    {
        var local = new LocalBrowserViewModel(Settings.LocalFolder)
        {
            ShowHidden = ShowHiddenFiles,
            Sort = LocalColumns.Sort,
            SortDescending = LocalColumns.Descending,
        };
        local.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalBrowserViewModel.CurrentPath))
            {
                Settings.LocalFolder = local.CurrentPath;
            }
        };

        // Ein Download in den angezeigten Ordner soll dort auch erscheinen.
        Transfers.TransferCompleted += (_, _) => _dispatcher.Post(() =>
        {
            if (ShowsDualFiles)
            {
                local.Refresh();
            }
        });
        return local;
    }
}
