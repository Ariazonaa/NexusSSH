using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Zeile im Reiter „Ausgabe“; Warnungen und Fehler stehen zusätzlich unter „Probleme“.</summary>
public sealed record LogEntry(DateTimeOffset Time, string Text, LogSeverity Severity = LogSeverity.Info)
{
    public bool IsError => Severity == LogSeverity.Error;

    public override string ToString() => Text;
}

/// <summary>
/// Profile einer Gruppe (Spec §8). <see cref="Name"/> ist der Anzeigename; Profile ohne Gruppe stehen unter „Ohne
/// Gruppe“. Die Gruppe ist selbst die Liste ihrer Profile: So kann eine gruppierte Ansicht sie ohne Umweg über
/// einen Eigenschaftsnamen aufzählen (der unter Native AOT nicht aufgelöst würde).
/// </summary>
public sealed class ProfileGroup(string name, IEnumerable<ServerProfile> profiles) : List<ServerProfile>(profiles)
{
    public string Name { get; } = name;

    public IReadOnlyList<ServerProfile> Profiles => this;

    /// <summary>Der ganze Pfad der Gruppe (<see cref="GroupPath"/>); leer für „Favoriten“ und „Ohne Gruppe“.</summary>
    public string Path { get; init; } = string.Empty;

    public GroupKind Kind { get; init; } = GroupKind.Group;

    /// <summary>Eingeklappt zeigt die Startseite nur die Überschrift – die Gruppe ist dann als Liste leer.</summary>
    public bool IsCollapsed { get; init; }

    /// <summary>Wie viele Profile zur Gruppe gehören, samt denen in Gruppen darunter und den gerade eingeklappten.</summary>
    public int Total { get; init; }

    /// <summary>Wie viele Gruppen direkt darunter liegen; dorthin führt ein Klick auf den Namen.</summary>
    public int Subgroups { get; init; }

    public bool HasSubgroups => Subgroups > 0;

    /// <summary>
    /// Das Zeichen, das für die Gruppe gilt (ihr eigenes oder das der nächsten darüber, <see cref="TileMarks"/>);
    /// es steht klein vor ihrem Namen. <c>null</c> = keines, und immer bei „Favoriten“ und „Ohne Gruppe“.
    /// </summary>
    public string? Mark { get; init; }

    public bool HasMark => TileMarks.Normalize(Mark) is not null;

    /// <summary>Die Kennfarbe, die für die Gruppe gilt – in ihr steht das Zeichen; <c>null</c> = keine.</summary>
    public string? Color { get; init; }

    /// <summary>Nur echte Gruppen lassen sich umsortieren; „Favoriten“ steht immer oben, „Ohne Gruppe“ unten.</summary>
    public bool CanMove => Kind == GroupKind.Group;

    /// <summary>Unter diesem Namen merkt sich die App, dass der Abschnitt eingeklappt ist.</summary>
    public string Key => Kind switch
    {
        GroupKind.Favorites => "*favorites",
        GroupKind.Ungrouped => "*ungrouped",
        _ => Path,
    };

    /// <summary>„3 Server“ oder „3 Server · 2 Untergruppen“ – steht neben dem Namen.</summary>
    public string Summary => HasSubgroups ? L.Get("GroupSummaryNested", Total, Subgroups) : L.Get("GroupSummary", Total);

    public override string ToString() => $"{Name}, {Summary}";
}

/// <summary>Was ein Abschnitt der Startseite ist.</summary>
public enum GroupKind
{
    Group,
    Favorites,
    Ungrouped,

    /// <summary>Die Profile, die direkt in der geöffneten Gruppe liegen.</summary>
    Here,
}

/// <summary>Ein Glied der Brotkrumen über den Kacheln: der Name einer Gruppe und ihr ganzer Pfad.</summary>
public sealed record HomeCrumb(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>
/// Was die Terminal-Fläche zeigt: ein Terminal oder mehrere geteilt (Spec §5, „Split View“). <see cref="Tree"/> ist
/// die Anordnung (ein Blatt, wenn nichts geteilt ist; <c>null</c>, wenn es nichts zu zeigen gibt), <see cref="Panes"/>
/// ihre Blätter von oben links nach unten rechts. <see cref="Zoomed"/> ist der Bereich, der die ganze Fläche
/// bekommt, solange es einen gibt. <see cref="Fixed"/>: Die Bereiche sind die eines Fensters von tmux – dessen
/// Anordnung ändert die Oberfläche nicht.
/// </summary>
public sealed record TerminalLayout(SplitNode<SessionViewModel>? Tree, SessionViewModel? Active, SessionViewModel? Zoomed = null, bool Fixed = false)
{
    public static TerminalLayout Empty { get; } = new(null, null);

    public IReadOnlyList<SessionViewModel> Panes => Tree is null ? [] : SplitTree.Leaves(Tree);
}

/// <summary>Etwas, das nur die Oberfläche kann – ein Dialog, der Fokus, das Terminal.</summary>
public enum UiRequest
{
    AddServer,
    ImportSshConfig,
    ImportSessions,
    ImportSessionFile,
    CreateKey,
    FocusCommandBar,
    FocusTerminal,
    ClearTerminal,
    FocusDivider,
    SaveTerminal,
    RenameTab,
    ExportData,
    ImportData,
    SaveDiagnostics,
    ShowLoadHistory,
    CopyFormatted,
    CopyOneLine,
    FindMux,
    FocusNextPane,
    FocusPreviousPane,
    ShowTransfers,
    ShowProblems,
    ShowTransferLog,
    ToggleLog,
}

/// <summary>Die Bereiche des Fensters in der Reihenfolge, in der die Tastatur sie durchläuft.</summary>
public enum ShellPane
{
    Files,
    Terminal,
    Snippets,
    Bottom,
}

/// <summary>
/// Zustand des Hauptfensters. Die zentrale Regel der Oberfläche: <see cref="ActiveSession"/> – der gewählte
/// Terminal-Tab – bestimmt, welchen Server SFTP-Leiste und Inspector zeigen.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>So viele Terminals teilen sich höchstens die Fläche.</summary>
    public const int MaxPanes = SplitTree.MaxPanes;

    private const int MaxLogEntries = 500;

    private readonly INexusBackend _backend;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserPrompts _prompts;
    private readonly List<SplitGroup> _splits = [];
    private readonly RemoteEditor? _editor;
    private readonly DropFetcher? _drops;
    private readonly IUpdateService? _updates;
    private readonly SessionRetry? _retry;
    private readonly Version _currentVersion;
    private IReadOnlyList<ServerProfile> _profiles = [];

    /// <param name="editorHost">Startet den Editor für „Datei bearbeiten“; ohne ihn gibt es die Funktion nicht (Tests).</param>
    /// <param name="editDirectory">Arbeitsordner für die zum Bearbeiten geholten Dateien.</param>
    public ShellViewModel(
        INexusBackend backend,
        IUiDispatcher dispatcher,
        AppSettings settings,
        IUserPrompts prompts,
        IEditorHost? editorHost = null,
        string? editDirectory = null,
        string? dropDirectory = null,
        IUpdateService? updates = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _backend = backend;
        _dispatcher = dispatcher;
        _prompts = prompts;
        _updates = updates;
        _retry = schedule is null ? null : new SessionRetry(() => AutoReconnect, schedule);
        _currentVersion = GetType().Assembly.GetName().Version ?? new Version(0, 0, 0);
        Settings = settings;
        CheckForUpdates = settings.CheckForUpdates;
        Transfers = new TransferCenterViewModel(backend.Transfers, dispatcher);
        backend.Transfers.SetLimit(settings.TransferLimitKib * 1024L);
        if (settings.X11Display != 0)
        {
            backend.SetX11Display(settings.X11Display);
        }

        if ((settings.KeepAliveSeconds, settings.KeepAliveMissed) != (AppSettings.DefaultKeepAliveSeconds, AppSettings.DefaultKeepAliveMissed))
        {
            backend.SetKeepAlive(settings.KeepAliveSeconds, settings.KeepAliveMissed);
        }

        if (settings.MoshPredict != AppSettings.DefaultMoshPredict)
        {
            backend.SetMoshPrediction(settings.MoshPredict);
        }

        // Der Kern merkt sich entsperrte Schlüssel von selbst; gesagt wird ihm nur, wenn er es lassen soll.
        if (!settings.RememberUnlockedKeys)
        {
            backend.RememberUnlockedKeys(false);
        }
        Search = new TerminalSearchViewModel();
        IsSftpVisible = settings.ShowSftp;
        IsSnippetsVisible = settings.ShowSnippets;
        IsBottomPanelVisible = settings.ShowBottomPanel;
        ShowHiddenFiles = settings.ShowHiddenFiles;
        ShowDirectoryTree = settings.ShowDirectoryTree;
        RemoteColumns = FileColumns.Remote(settings);
        LocalColumns = FileColumns.Local(settings);
        _shownRemote = (RemoteColumns.Sort, RemoteColumns.Descending, ShowFileDates, ShowFilePermissions);
        RemoteColumns.Changed += (_, _) => OnRemoteColumnsChanged();
        LocalColumns.Changed += (_, _) => OnLocalColumnsChanged();
        ProfileSort = settings.ProfileSort;
        RestoreTabs = settings.RestoreTabs;
        ConfirmClose = settings.ConfirmClose;
        TerminalTheme = TerminalThemes.Find(settings.TerminalTheme, settings.CustomThemes);
        TerminalFontSize = Math.Clamp(settings.TerminalFontSize, TerminalThemes.MinFontSize, TerminalThemes.MaxFontSize);
        ProblemsTitle = L.Get("Problems");
        ProfileGroups = BindableList.Of<ProfileGroup>([]);
        HomeGroups = BindableList.Of<ProfileGroup>([]);
        LocalShells = BindableList.Of<LocalShell>([]);
        _defaultCommands = BuildCommands();
        Commands = _defaultCommands;
        ApplyShortcuts();
        LoadProfiles();
        LoadHistory();
        _ = LoadLocalShellsAsync();
        RefreshCounts();
        FilterSnippets();
        Sessions.CollectionChanged += (_, _) =>
        {
            RefreshTunnels();
            if (IsOverviewShown)
            {
                UpdateMonitoring();
            }
        };
        Transfers.Synced += RefreshCounts;

        // Nach einem Upload zeigt der Dateibrowser des Servers die neue Datei, ohne dass man „Aktualisieren“ drückt.
        Transfers.UploadCompleted += server =>
        {
            foreach (var session in Sessions.Where(session => session.Profile.Name == server && session.Files.IsAvailable))
            {
                _ = session.Files.RefreshCommand.ExecuteAsync(null);
            }
        };
        Transfers.TransferFailed += (file, server, detail) =>
        {
            Log(L.Get("LogTransferFailed", file, server, detail), LogSeverity.Error);
            Notify(server, L.Get("NotifyTransferFailed", file));
            Sound(SoundEvent.TransferFailed);
        };
        Transfers.TransferCompleted += (file, server) =>
        {
            Notify(server, L.Get("NotifyTransferDone", file));
            Sound(SoundEvent.TransferDone);
        };

        if (dropDirectory is not null)
        {
            _drops = new DropFetcher(dropDirectory);
            UseRelay(_drops);
        }

        _compareHost = editorHost;
        if (editorHost is not null && editDirectory is not null)
        {
            _editor = new RemoteEditor(backend.Transfers, editorHost, editDirectory);
            _editor.Opened += (file, server) => dispatcher.Post(() => Log(L.Get("LogEditOpened", file, server)));
            _editor.Uploading += (file, server) => dispatcher.Post(() => Log(L.Get("LogEditSaved", file, server)));
            _editor.Failed += (file, server, detail) =>
                dispatcher.Post(() => Log(L.Get("LogEditFailed", file, server, detail), LogSeverity.Error));
            _editor.Kept += (file, server) => dispatcher.Post(() => Log(L.Get("LogEditKept", file, server), LogSeverity.Warning));

            // Gefragt wird im Thread der Oberfläche; der Editor meldet das Speichern von irgendwo.
            _editor.OverwriteChanged = (file, server) =>
            {
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatcher.Post(async () =>
                {
                    try
                    {
                        answer.TrySetResult(await prompts.ConfirmAsync(L.Get("EditConflictTitle", file), L.Get("EditConflictText", file, server), L.Get("Overwrite")));
                    }
                    catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
                    {
                        answer.TrySetResult(false);
                    }
                });
                return answer.Task;
            };
        }

        backend.Profiles.Changed += () => dispatcher.Post(LoadProfiles);
        backend.Warning += message => dispatcher.Post(() => Log(message, LogSeverity.Warning));
    }

    /// <summary>Die Oberfläche soll etwas tun, das nur sie kann.</summary>
    public event Action<UiRequest>? UiRequested;

    public AppSettings Settings { get; }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public ObservableCollection<LogEntry> Output { get; } = [];

    /// <summary>Warnungen und Fehler aus <see cref="Output"/> (Reiter „Probleme“).</summary>
    public ObservableCollection<LogEntry> Problems { get; } = [];

    public TransferCenterViewModel Transfers { get; }

    public TerminalSearchViewModel Search { get; }

    /// <summary>Alle Profile, z. B. für die Auswahl eines Sprung-Servers.</summary>
    public IReadOnlyList<ServerProfile> Profiles => _profiles;

    /// <summary>Gespeicherte Befehle (Snippets); sie stehen in den Einstellungen.</summary>
    public IReadOnlyList<Snippet> Snippets => Settings.Snippets;

    /// <summary>Alle Profile nach Gruppen; wird bei jeder Änderung neu gesetzt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles), nameof(HasNoProfiles))]
    public partial IReadOnlyList<ProfileGroup> ProfileGroups { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSession), nameof(HasNoActiveSession), nameof(ShowsDualFiles), nameof(ShowsTerminal), nameof(ShowsFiles), nameof(ShowsSnippets), nameof(IsLocalActive))]
    public partial SessionViewModel? ActiveSession { get; set; }

    [ObservableProperty]
    public partial bool IsSftpVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSnippetsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsBottomPanelVisible { get; set; }

    /// <summary>Ob die Dateibrowser Einträge zeigen, deren Name mit einem Punkt beginnt.</summary>
    [ObservableProperty]
    public partial bool ShowHiddenFiles { get; set; }

    /// <summary>Ob die Dateibrowser über der Liste den Baum der Verzeichnisse zeigen.</summary>
    [ObservableProperty]
    public partial bool ShowDirectoryTree { get; set; }

    /// <summary>Reihenfolge der Profile in Serverliste und Palette.</summary>
    [ObservableProperty]
    public partial ProfileSort ProfileSort { get; set; }

    /// <summary>Beim Start die Tabs wieder öffnen, die beim Beenden offen waren.</summary>
    [ObservableProperty]
    public partial bool RestoreTabs { get; set; }

    /// <summary>Vor dem Beenden fragen, wenn Sitzungen verbunden sind oder Transfers laufen.</summary>
    [ObservableProperty]
    public partial bool ConfirmClose { get; set; }

    /// <summary>Beim Start nach einer neueren Version suchen (höchstens 1×/Tag).</summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    /// <summary>Eine gefundene neuere Version; <c>null</c>, solange keine bekannt ist. Treibt den Hinweis im Fenster.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateMessage))]
    public partial UpdateInfo? AvailableUpdate { get; private set; }

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>Text des Update-Hinweises.</summary>
    public string UpdateMessage => AvailableUpdate is { } update
        ? L.Get("UpdateAvailable", update.Version, _currentVersion.ToString(3))
        : string.Empty;

    /// <summary>Was die Terminal-Fläche zeigt; folgt <see cref="ActiveSession"/> und den Teilungen.</summary>
    [ObservableProperty]
    public partial TerminalLayout Layout { get; private set; } = TerminalLayout.Empty;

    [ObservableProperty]
    public partial TerminalTheme TerminalTheme { get; set; }

    [ObservableProperty]
    public partial int TerminalFontSize { get; set; }

    /// <summary>Schrift des Terminals; ein Name, den die Terminal-Seite nicht bekommen darf, wird zur Standardschrift.</summary>
    public string TerminalFont
    {
        get => Settings.TerminalFont;
        set
        {
            var font = TerminalThemes.Font(value);
            if (font != Settings.TerminalFont)
            {
                Settings.TerminalFont = font;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Wie PuTTY: Markieren kopiert, Rechtsklick fügt ein.</summary>
    public bool MouseCopyPaste
    {
        get => Settings.MouseCopyPaste;
        set
        {
            if (value != Settings.MouseCopyPaste)
            {
                Settings.MouseCopyPaste = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Regeln zum Hervorheben im Terminal, wie sie in den Einstellungen getippt werden. Die Seite bekommt nur, was
    /// <see cref="HighlightRules"/> daraus versteht.
    /// </summary>
    public string Highlights
    {
        get => Settings.Highlights;
        set
        {
            var text = TerminalHighlights.Limit(value);
            if (text != Settings.Highlights)
            {
                Settings.Highlights = text;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HighlightHint));
            }
        }
    }

    public IReadOnlyList<HighlightRule> HighlightRules => TerminalHighlights.Parse(Settings.Highlights);

    /// <summary>Sagt, wie viele Zeilen der Regeln nichts ergeben; leer, wenn alle verstanden sind.</summary>
    public string HighlightHint => TerminalHighlights.Ignored(Settings.Highlights) is > 0 and var ignored ? L.Get("HighlightsIgnored", ignored) : string.Empty;

    /// <summary>
    /// Die Spalten der Dateilisten der Server: Sortierung, welche zu sehen sind, wie breit – in allen Sitzungen gleich.
    /// Was hier geändert wird, steht in den Einstellungen (<see cref="AppSettings.RemoteColumns"/>) und gilt in jeder Liste.
    /// </summary>
    public FileColumns RemoteColumns { get; }

    /// <summary>Dasselbe für die lokale Liste der Ansicht „Zwei Spalten“, die ihre eigene Wahl hat.</summary>
    public FileColumns LocalColumns { get; }

    /// <summary>Wonach die Dateilisten der Server sortiert sind – in allen Sitzungen gleich (<see cref="RemoteColumns"/>).</summary>
    public FileSort FileSort
    {
        get => RemoteColumns.Sort;
        set => RemoteColumns.Sort = value;
    }

    /// <summary>Dateilisten in umgekehrter Reihenfolge.</summary>
    public bool FileSortDescending
    {
        get => RemoteColumns.Descending;
        set => RemoteColumns.Descending = value;
    }

    /// <summary>Die Dateilisten zeigen das Änderungsdatum als Spalte.</summary>
    public bool ShowFileDates
    {
        get => RemoteColumns.IsVisible(FileColumn.Modified);
        set
        {
            if (value != ShowFileDates)
            {
                RemoteColumns.Toggle(FileColumn.Modified);
            }
        }
    }

    /// <summary>Die Dateilisten zeigen die Zugriffsrechte als Spalte.</summary>
    public bool ShowFilePermissions
    {
        get => RemoteColumns.IsVisible(FileColumn.Permissions);
        set
        {
            if (value != ShowFilePermissions)
            {
                RemoteColumns.Toggle(FileColumn.Permissions);
            }
        }
    }

    // Was die Ansicht von den Spalten des Servers zuletzt gesehen hat: Eine Breite zu ziehen soll sie nicht dazu
    // bringen, alle Zeilen neu aufzubauen.
    private (FileSort Sort, bool Descending, bool Dates, bool Permissions) _shownRemote;

    private void OnRemoteColumnsChanged()
    {
        Settings.RemoteColumns = RemoteColumns.ToSet();
        (Settings.FileSort, Settings.FileSortDescending) = (RemoteColumns.Sort, RemoteColumns.Descending);
        ApplyFileSort();
        var now = (RemoteColumns.Sort, RemoteColumns.Descending, ShowFileDates, ShowFilePermissions);
        var before = _shownRemote;
        _shownRemote = now;
        if (now.Sort != before.Sort)
        {
            OnPropertyChanged(nameof(FileSort));
        }

        if (now.Descending != before.Descending)
        {
            OnPropertyChanged(nameof(FileSortDescending));
        }

        if (now.ShowFileDates != before.Dates)
        {
            OnPropertyChanged(nameof(ShowFileDates));
        }

        if (now.ShowFilePermissions != before.Permissions)
        {
            OnPropertyChanged(nameof(ShowFilePermissions));
        }
    }

    private void OnLocalColumnsChanged()
    {
        Settings.LocalColumns = LocalColumns.ToSet();
        (Settings.LocalSort, Settings.LocalSortDescending) = (LocalColumns.Sort, LocalColumns.Descending);

        // Die lokale Liste der Ansicht „Zwei Spalten“ ist nach ihrer eigenen Wahl sortiert, nicht nach der des Servers.
        if (_local is not null)
        {
            (_local.Sort, _local.SortDescending) = (LocalColumns.Sort, LocalColumns.Descending);
        }
    }

    private void ApplyFileSort()
    {
        foreach (var session in Sessions)
        {
            (session.Files.Sort, session.Files.SortDescending) = (FileSort, FileSortDescending);
        }
    }

    /// <summary>
    /// Wechselt in der Shell der Sitzung in ein Verzeichnis des Servers („hier im Terminal öffnen“). Der Pfad wird
    /// als ein einziges Argument geschrieben; einen Namen mit Steuerzeichen tippt die App nicht – er käme vom
    /// Server und ließe sich in keiner Shell sicher schreiben. <c>false</c>, wenn nichts getippt wurde.
    /// </summary>
    public bool OpenInTerminal(SessionViewModel session, string path)
    {
        if (session is not { State: ConnectionState.Connected, IsFilesOnly: false } || Format.ShellQuote(path) is not { } quoted)
        {
            return false;
        }

        session.Send($"cd {quoted}\r");
        ActiveSession = session;
        FocusTerminal();
        return true;
    }

    /// <summary>Reißt eine Verbindung ab, verbinden die Sitzungen von selbst neu (<see cref="SessionRetry"/>).</summary>
    public bool AutoReconnect
    {
        get => Settings.AutoReconnect;
        set
        {
            if (value != Settings.AutoReconnect)
            {
                Settings.AutoReconnect = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Was in das aktive Terminal einer geteilten Ansicht getippt wird, geht an alle Terminals dieser Ansicht – für
    /// dasselbe Kommando auf mehreren Servern. Gilt nur, solange die aktive Sitzung geteilt ist; ein Wechsel zu
    /// einem ungeteilten Tab schaltet es aus, damit später nichts überraschend mehrfach ankommt.
    /// </summary>
    [ObservableProperty]
    public partial bool BroadcastInput { get; set; }

    /// <summary>Eingabe aus dem Terminal einer Sitzung: an sie – oder, mit <see cref="BroadcastInput"/>, an ihre ganze Gruppe.</summary>
    public void Input(SessionViewModel source, ReadOnlyMemory<byte> data)
    {
        if (BroadcastInput && SplitOf(source) is { Zoomed: null } split)
        {
            foreach (var member in split.Members)
            {
                member.Terminal.Write(data);
            }

            return;
        }

        source.Terminal.Write(data);
    }

    /// <summary>
    /// Ein Schlüssel mit Passphrase wird nur einmal je Start der App entsperrt; der Kern behält ihn im Speicher.
    /// Abgeschaltet vergisst er sofort alle.
    /// </summary>
    public bool RememberUnlockedKeys
    {
        get => Settings.RememberUnlockedKeys;
        set
        {
            if (value != Settings.RememberUnlockedKeys)
            {
                Settings.RememberUnlockedKeys = value;
                _backend.RememberUnlockedKeys(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Vergisst die entsperrten Schlüssel – die nächste Verbindung fragt wieder nach der Passphrase.</summary>
    public void ForgetUnlockedKeys()
    {
        _backend.ForgetUnlockedKeys();
        Log(L.Get("UnlockedKeysForgotten"));
    }

    /// <summary>Cursor und Abstände des Terminals, wie die Terminal-Seite sie bekommt.</summary>
    public TerminalLook TerminalLook => TerminalLook.From(Settings);

    /// <summary>Form des Cursors (<see cref="TerminalLook.CursorStyles"/>).</summary>
    public string TerminalCursor
    {
        get => Settings.TerminalCursor;
        set => SetLook(Settings.TerminalCursor != TerminalLook.Cursor(value), () => Settings.TerminalCursor = TerminalLook.Cursor(value));
    }

    public bool TerminalCursorBlink
    {
        get => Settings.TerminalCursorBlink;
        set => SetLook(Settings.TerminalCursorBlink != value, () => Settings.TerminalCursorBlink = value);
    }

    public double TerminalLineHeight
    {
        get => Settings.TerminalLineHeight;
        set => SetLook(Settings.TerminalLineHeight != TerminalLook.Height(value), () => Settings.TerminalLineHeight = TerminalLook.Height(value));
    }

    public int TerminalLetterSpacing
    {
        get => Settings.TerminalLetterSpacing;
        set => SetLook(Settings.TerminalLetterSpacing != TerminalLook.Spacing(value), () => Settings.TerminalLetterSpacing = TerminalLook.Spacing(value));
    }

    public bool TerminalLigatures
    {
        get => Settings.TerminalLigatures;
        set => SetLook(Settings.TerminalLigatures != value, () => Settings.TerminalLigatures = value);
    }

    /// <summary>Eingefügten Text bereinigen, bevor er das Terminal erreicht.</summary>
    public bool PasteClean
    {
        get => Settings.PasteClean;
        set => SetLook(Settings.PasteClean != value, () => Settings.PasteClean = value);
    }

    /// <summary>Bilder zeichnen, die der Server ins Terminal schickt – von Haus aus aus.</summary>
    public bool TerminalImages
    {
        get => Settings.TerminalImages;
        set => SetLook(Settings.TerminalImages != value, () => Settings.TerminalImages = value);
    }

    /// <summary>Beim Einfügen Tabulatoren durch Leerzeichen ersetzen.</summary>
    public bool PasteTabs
    {
        get => Settings.PasteTabs;
        set => SetLook(Settings.PasteTabs != value, () => Settings.PasteTabs = value);
    }

    // Eine Einstellung des Terminals ändern: Die Seite bekommt danach alles neu (TerminalHost hört auf TerminalLook).
    private void SetLook(bool changed, Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (changed)
        {
            apply();
            OnPropertyChanged(property);
            OnPropertyChanged(nameof(TerminalLook));
        }
    }

    /// <summary>Das Terminal schlägt beim Tippen gespeicherte Befehle vor; Tab übernimmt den ersten.</summary>
    public bool SnippetSuggestions
    {
        get => Settings.SnippetSuggestions;
        set
        {
            if (value != Settings.SnippetSuggestions)
            {
                Settings.SnippetSuggestions = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die gespeicherten Befehle, die die Terminal-Seite vorschlagen darf – leer, wenn das abgeschaltet ist. Die
    /// Seite vergleicht sie mit dem, was seit dem letzten Enter getippt wurde; das Getippte verlässt sie dabei nicht.
    /// </summary>
    public IReadOnlyList<Snippet> TerminalSuggestions => SnippetSuggestions ? Snippets : [];

    /// <summary>
    /// Das Display des X-Servers dieses Rechners für X11-Forwarding (0 = das Übliche). Gilt für Verbindungen, die
    /// danach aufgebaut werden.
    /// </summary>
    public int X11Display
    {
        get => Settings.X11Display;
        set
        {
            var display = Math.Clamp(value, 0, AppSettings.MaxX11Display);
            if (display != Settings.X11Display)
            {
                Settings.X11Display = display;
                _backend.SetX11Display(display);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Alle wie viele Sekunden ein Lebenszeichen an einen Server geht (0 = keines), wenn sein Profil nichts
    /// Eigenes nennt. Gilt für Verbindungen, die danach aufgebaut werden.
    /// </summary>
    public int KeepAliveSeconds
    {
        get => Settings.KeepAliveSeconds;
        set
        {
            // Zwischen „keine“ und dem kürzesten Abstand gibt es nichts: Dort gilt der kürzeste.
            var seconds = value <= 0 ? 0 : Math.Clamp(value, 5, 3600);
            if (seconds != Settings.KeepAliveSeconds)
            {
                Settings.KeepAliveSeconds = seconds;
                _backend.SetKeepAlive(seconds, Settings.KeepAliveMissed);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die Vorhersage beim Tippen in Terminals über Mosh (<see cref="AppSettings.MoshPredictions"/>). Gilt für
    /// Sitzungen, die danach beginnen.
    /// </summary>
    public string MoshPredict
    {
        get => Settings.MoshPredict;
        set
        {
            var prediction = AppSettings.MoshPredictions.Contains(value) ? value : AppSettings.DefaultMoshPredict;
            if (prediction != Settings.MoshPredict)
            {
                Settings.MoshPredict = prediction;
                _backend.SetMoshPrediction(prediction);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Nach so vielen unbeantworteten Lebenszeichen gilt eine Verbindung als abgerissen.</summary>
    public int KeepAliveMissed
    {
        get => Settings.KeepAliveMissed;
        set
        {
            var missed = Math.Clamp(value, 1, AppSettings.MaxKeepAliveMissed);
            if (missed != Settings.KeepAliveMissed)
            {
                Settings.KeepAliveMissed = missed;
                _backend.SetKeepAlive(Settings.KeepAliveSeconds, missed);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Obergrenze für die Bandbreite aller Transfers zusammen, in KiB je Sekunde; 0 = keine. Gilt sofort, auch für
    /// laufende Transfers.
    /// </summary>
    public int TransferLimitKib
    {
        get => Settings.TransferLimitKib;
        set
        {
            var limit = Math.Clamp(value, 0, AppSettings.MaxTransferLimitKib);
            if (limit != Settings.TransferLimitKib)
            {
                Settings.TransferLimitKib = limit;
                _backend.Transfers.SetLimit(limit * 1024L);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Am Reiter steht der Titel, den ein Programm auf dem Server setzt (OSC 0/2) – solange der Benutzer dem Tab
    /// keinen eigenen Namen gegeben hat. Aus: Der Reiter heißt wie das Profil.
    /// </summary>
    public bool ServerTabTitles
    {
        get => Settings.ServerTabTitles;
        set
        {
            if (value != Settings.ServerTabTitles)
            {
                Settings.ServerTabTitles = value;
                foreach (var session in Sessions)
                {
                    session.ShowServerTitle = value;
                }

                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die Shell einer Sitzung hat ihr Arbeitsverzeichnis gemeldet: Ihr Dateibrowser folgt – wenn „Terminal-Ordner
    /// folgen“ für diese Sitzung eingeschaltet ist (<see cref="SessionViewModel.FollowsShell"/>), er zu sehen ist und
    /// er nicht schon dort steht. <c>false</c>, wenn nichts geschah.
    /// </summary>
    public bool OnTerminalDirectory(SessionViewModel session, string path)
    {
        var target = RemotePath.Normalize(path);
        session.ShellDirectory = target;
        _ = LoadPathNamesAsync(session, target);
        if (!session.FollowsShell || !IsSftpVisible || !session.Files.IsAvailable || session.Files.CurrentPath == target)
        {
            return false;
        }

        _ = session.Files.NavigateToAsync(target);
        return true;
    }

    /// <summary>
    /// Im Terminal einer Sitzung wurde ein Pfad angeklickt: Ihr Dateibrowser zeigt ihn – einen Ordner öffnet er,
    /// eine Datei wählt er in ihrem Ordner aus. Der Pfad stand in der Ausgabe des Servers; gibt es ihn nicht, steht
    /// das im Protokoll, und sonst geschieht nichts. <c>false</c>, wenn die Sitzung keine Dateien hat.
    /// </summary>
    public async Task<bool> OnTerminalPath(SessionViewModel session, string path)
    {
        if (!session.Files.IsAvailable)
        {
            return false;
        }

        IsSftpVisible = true;

        // Ein relativer Pfad gilt ab dem Verzeichnis der Shell, wenn sie es gemeldet hat (OSC 7) – sonst ab dem, das
        // der Dateibrowser zeigt; „~“ ist das Startverzeichnis.
        var from = session.ShellDirectory ?? session.Files.CurrentPath;
        var target = RemotePath.Normalize(path switch
        {
            "~" => session.Files.HomePath,
            _ when path.StartsWith("~/", StringComparison.Ordinal) => RemotePath.Combine(session.Files.HomePath, path[2..]),
            _ when path.StartsWith('/') => path,
            _ => RemotePath.Combine(from, path),
        });
        if (!await session.Files.ShowPathAsync(target))
        {
            Log(L.Get("LogPathNotFound", Format.Visible(target), session.Profile.Name), LogSeverity.Warning);
            return false;
        }

        return true;
    }

    /// <summary>Eine im Terminal angeklickte Web-Adresse wurde geöffnet – fürs Protokoll.</summary>
    public void OnTerminalLink(SessionViewModel session, Uri link)
        => Log(L.Get("LogLinkOpened", Format.Visible(link.AbsoluteUri), session.Profile.Name));

    /// <summary>Hinweise von Windows, wenn im Hintergrund etwas fertig wird oder die Glocke läutet.</summary>
    public bool Notifications
    {
        get => Settings.Notifications;
        set
        {
            if (value != Settings.Notifications)
            {
                Settings.Notifications = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Etwas, worauf ein Hinweis von Windows aufmerksam machen kann: Überschrift und Text. Ob er erscheint,
    /// entscheidet die Oberfläche (nur, wenn das Fenster nicht im Vordergrund ist).
    /// </summary>
    public event Action<string, string>? AttentionRequested;

    /// <summary>Die Glocke im Terminal einer Sitzung (BEL) – z. B. am Ende eines langen Befehls.</summary>
    public void OnTerminalBell(SessionViewModel session)
    {
        // Der Reiter: nur, wenn die Sitzung gerade nicht die ist, auf die der Benutzer sieht.
        if (BellTab && !ReferenceEquals(session, ActiveSession))
        {
            session.MarkBell();
        }

        // Ein Server kann die Glocke tausendmal läuten – gemeldet wird höchstens alle paar Sekunden.
        var now = Now();
        if (now - _lastBell >= BellPause)
        {
            _lastBell = now;
            Notify(session.Profile.Name, L.Get("NotifyBell"));
            if (BellSound || BellTaskbar)
            {
                BellRang?.Invoke(BellSound, BellTaskbar);
            }
        }
    }

    /// <summary>
    /// Ein Programm im Terminal einer Sitzung schickt eine Meldung (OSC 9, OSC 777 – etwa „Build fertig“). Es ist
    /// die Glocke mit Text: dieselbe Marke am Reiter, derselbe Weg zum Hinweis von Windows, dieselbe Pause. Der
    /// Text kommt vom Server: Der Name des Servers steht immer davor, damit der Hinweis nie wie einer der App aussieht.
    /// </summary>
    public void OnTerminalNotice(SessionViewModel session, string title, string body)
    {
        if (!TerminalNotices)
        {
            return;
        }

        if (BellTab && !ReferenceEquals(session, ActiveSession))
        {
            session.MarkBell();
        }

        var now = Now();
        if (now - _lastBell < BellPause)
        {
            return;
        }

        // Noch einmal gesäubert und gekürzt – auch wer diese Methode ohne die Seite ruft, bekommt nur das.
        title = TerminalMessages.NoticeText(title, TerminalMessages.MaxNoticeTitleLength);
        body = TerminalMessages.NoticeText(body, TerminalMessages.MaxNoticeBodyLength);
        if (title.Length == 0 && body.Length == 0)
        {
            return;
        }

        _lastBell = now;
        var server = session.Profile.Name;
        if (title.Length > 0 && body.Length > 0)
        {
            Notify($"{server}: {title}", body);
        }
        else
        {
            // Nur eines von beiden: Der Server steht oben, das andere darunter.
            Notify(server, title.Length > 0 ? title : body);
        }
    }

    /// <summary>Meldungen von Programmen im Terminal (OSC 9, OSC 777) kennzeichnen den Reiter und erscheinen als Hinweis von Windows.</summary>
    public bool TerminalNotices
    {
        get => Settings.TerminalNotices;
        set
        {
            if (value != Settings.TerminalNotices)
            {
                Settings.TerminalNotices = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Die Glocke soll zu hören oder in der Taskleiste zu sehen sein: (Ton, Blinken). Die Oberfläche tut es –
    /// höchstens alle paar Sekunden, wie der Hinweis von Windows.
    /// </summary>
    public event Action<bool, bool>? BellRang;

    /// <summary>Die Glocke spielt den Hinweiston von Windows.</summary>
    public bool BellSound
    {
        get => Settings.BellSound;
        set
        {
            if (value != Settings.BellSound)
            {
                Settings.BellSound = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Die Glocke kennzeichnet den Reiter, bis er angesehen wird.</summary>
    public bool BellTab
    {
        get => Settings.BellTab;
        set
        {
            if (value != Settings.BellTab)
            {
                Settings.BellTab = value;
                OnPropertyChanged();
                if (!value)
                {
                    foreach (var session in Sessions)
                    {
                        session.ClearBell();
                    }
                }
            }
        }
    }

    /// <summary>Die Glocke lässt die Taskleiste blinken, wenn das Fenster nicht vorn ist.</summary>
    public bool BellTaskbar
    {
        get => Settings.BellTaskbar;
        set
        {
            if (value != Settings.BellTaskbar)
            {
                Settings.BellTaskbar = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>So lange nach einem Hinweis zur Glocke kommt kein weiterer.</summary>
    public static readonly TimeSpan BellPause = TimeSpan.FromSeconds(5);

    private DateTimeOffset _lastBell = DateTimeOffset.MinValue;

    private void Notify(string title, string text)
    {
        if (Notifications)
        {
            AttentionRequested?.Invoke(title, text);
        }
    }

    /// <summary>Kompakte Darstellung; die Oberfläche übernimmt sie beim nächsten Start.</summary>
    public bool Compact
    {
        get => Settings.Compact;
        set
        {
            if (value != Settings.Compact)
            {
                Settings.Compact = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Größe der ganzen Oberfläche in Prozent (<see cref="AppSettings.UiZoom"/>); gilt sofort.</summary>
    public int UiZoom
    {
        get => Settings.UiZoom;
        set
        {
            var percent = AppSettings.ClampUiZoom(value);
            if (percent != Settings.UiZoom)
            {
                Settings.UiZoom = percent;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Macht die Oberfläche um <paramref name="steps"/> Stufen größer (oder, negativ, kleiner).</summary>
    public void ZoomUi(int steps) => UiZoom += steps * AppSettings.UiZoomStep;

    /// <summary>Bereiche und Startseite weich einblenden.</summary>
    public bool Animations
    {
        get => Settings.Animations;
        set
        {
            if (value != Settings.Animations)
            {
                Settings.Animations = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Die Seite mit den Einstellungen liegt über den Bereichen.</summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>Die Hilfe-Seite liegt über den Bereichen – nie zugleich mit den Einstellungen.</summary>
    [ObservableProperty]
    public partial bool IsHelpOpen { get; set; }

    /// <summary>
    /// Zählt hoch, wenn die Hilfe bei den Tastenkürzeln aufgehen soll – die Seite rollt dann dorthin. Ein Zähler
    /// und kein Schalter: Der Wunsch soll auch ankommen, wenn die Hilfe schon offen ist.
    /// </summary>
    [ObservableProperty]
    public partial int ShortcutsWanted { get; private set; }

    /// <summary>Öffnet die Hilfe bei der Übersicht der Tastenkürzel.</summary>
    public void ShowShortcuts()
    {
        IsHelpOpen = true;
        ShortcutsWanted++;
    }

    partial void OnIsHelpOpenChanged(bool value)
    {
        if (value)
        {
            IsSettingsOpen = false;
            Logs = null;
            Details = null;
        }

        // Unter einer Seite ist die Übersicht nicht zu sehen – dann misst auch niemand für sie.
        UpdateMonitoring();
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value)
        {
            IsHelpOpen = false;
            Logs = null;
            Details = null;
        }

        // Unter einer Seite ist die Übersicht nicht zu sehen – dann misst auch niemand für sie.
        UpdateMonitoring();
    }

    /// <summary>
    /// Merkt sich, welche Version läuft, und öffnet die Hilfe mit dem, was neu ist, wenn hier vorher eine ältere
    /// lief. Beim ersten Start bleibt sie zu. Liefert, ob sie geöffnet wurde.
    /// </summary>
    public bool NoteVersion(string current)
    {
        var update = HelpContent.IsUpdate(Settings.LastSeenVersion, current);
        Settings.LastSeenVersion = current;
        if (update)
        {
            IsHelpOpen = true;
        }

        return update;
    }

    /// <summary>Beschriftung des Reiters „Probleme“, mit der Zahl, wenn es welche gibt.</summary>
    [ObservableProperty]
    public partial string ProblemsTitle { get; private set; }

    public bool HasActiveSession => ActiveSession is not null;

    public bool HasNoActiveSession => ActiveSession is null;

    /// <summary>Was über den Kacheln der Startseite eingetippt ist: Wörter, die am Profil vorkommen müssen.</summary>
    [ObservableProperty]
    public partial string HomeFilter { get; set; } = string.Empty;

    /// <summary>
    /// Die Profile für die Kacheln der Startseite: Favoriten als eigener Abschnitt oben, dann die Gruppen – gefiltert
    /// nach <see cref="HomeFilter"/>. Wird bei jeder Änderung neu gesetzt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoHomeMatches))]
    public partial IReadOnlyList<ProfileGroup> HomeGroups { get; private set; }

    /// <summary>Es gibt Profile, aber keines passt zum Filter.</summary>
    public bool HasNoHomeMatches => HasProfiles && HomeGroups.Count == 0;

    /// <summary>
    /// Ob der Dateibrowser zu sehen ist: eingeschaltet und mit einer aktiven Sitzung. Auf der Startseite hätte er
    /// nichts zu zeigen – dort gehört die Breite den Servern.
    /// </summary>
    public bool ShowsFiles => IsSftpVisible && HasActiveSession && !IsDualFiles && !IsTerminalOnly && !IsLocalActive;

    /// <summary>Ob die Spalte mit den Snippets zu sehen ist: eingeschaltet und mit einer aktiven Sitzung – auch einem lokalen Terminal.</summary>
    public bool ShowsSnippets => IsSnippetsVisible && HasActiveSession && !IsTerminalOnly;

    public bool HasProfiles => _profiles.Count > 0;

    public bool HasNoProfiles => _profiles.Count == 0;

    /// <summary>Öffnet eine neue Sitzung in einem neuen Tab und macht sie aktiv. Verbunden wird, sobald das Terminal steht.</summary>
    public SessionViewModel OpenSession(ServerProfile profile) => AddSession(profile, Sessions.Count);

    /// <summary>Schließt die Sitzung. War sie aktiv, wird der Nachbar-Tab aktiv.</summary>
    public void CloseSession(SessionViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0 || ClosesWithTmux(session))
        {
            return;
        }

        if (ReferenceEquals(Logs?.Session, session))
        {
            Logs = null;
        }

        if (ReferenceEquals(Details, session))
        {
            Details = null;
        }

        // Schließt der aktive Bereich einer Teilung, bleibt der Fokus in ihr – nicht beim Nachbarn-Tab.
        var inSplit = ReferenceEquals(ActiveSession, session) ? NextPane(session) : null;
        RemoveFromSplit(session);
        if (ReferenceEquals(ActiveSession, session))
        {
            ActiveSession = inSplit ?? (Sessions.Count == 1 ? null : Sessions[index == Sessions.Count - 1 ? index - 1 : index + 1]);
        }

        Sessions.RemoveAt(index);
        session.ConnectionEnded -= OnConnectionEnded;
        session.Traced -= OnTraced;
        NoteEnded(session, L.Get("HistoryTabClosed"));
        session.Files.FileOpened -= OnFileOpened;
        session.Dispose();
        DropQuick(session);
        UpdateLayout();
        Log(L.Get("LogDisconnected", session.Profile.Name));
    }

    /// <summary>Wechselt zum nächsten (<c>+1</c>) oder vorigen (<c>−1</c>) Tab, am Ende wieder von vorn.</summary>
    public void ActivateRelative(int delta)
    {
        // Von der Startseite aus: vorwärts zum ersten Tab, rückwärts zum letzten.
        if (ActiveSession is not { } current)
        {
            ActiveSession = delta > 0 ? Sessions.FirstOrDefault() : Sessions.LastOrDefault();
            return;
        }

        if (Sessions.Count < 2)
        {
            return;
        }

        var index = (Sessions.IndexOf(current) + delta) % Sessions.Count;
        ActiveSession = Sessions[index < 0 ? index + Sessions.Count : index];
    }

    /// <summary>Schiebt den aktiven Tab um eine Stelle nach links (<c>−1</c>) oder rechts (<c>+1</c>).</summary>
    public bool MoveActiveTab(int delta)
        => ActiveSession is { } current && MoveTab(current, Sessions.IndexOf(current) + delta);

    /// <summary>
    /// Setzt den Tab an die Stelle <paramref name="index"/> und macht ihn aktiv – so endet auch ein Ziehen mit der
    /// Maus (<see cref="TabStrip"/>). <c>false</c>, wenn es die Stelle nicht gibt.
    /// </summary>
    public bool MoveTab(SessionViewModel session, int index)
    {
        var from = Sessions.IndexOf(session);
        if (from < 0 || index < 0 || index >= Sessions.Count)
        {
            return false;
        }

        // Angeheftete bleiben links unter sich – auch wenn die Maus einen Reiter anderswo fallen lässt.
        index = InZone(session, index);
        if (from != index)
        {
            Sessions.Move(from, index);
        }

        // Eine Reiterleiste wählt beim Umsetzen kurz den Nachbarn; aktiv ist danach die verschobene Sitzung.
        ActiveSession = session;
        return true;
    }

    /// <summary>
    /// Ob sich die Terminal-Fläche noch weiter teilen lässt: mit einer aktiven Sitzung, deren Terminal hier steht
    /// (nicht in einem eigenen Fenster, und nicht als Bereich eines Fensters von tmux – dessen Anordnung gehört
    /// tmux) und solange nicht <see cref="SplitTree.MaxPanes"/> Bereiche stehen.
    /// </summary>
    public bool CanSplit => ActiveSession is { IsDetached: false, IsVirtual: false } active && (SplitOf(active)?.Members.Count ?? 1) < MaxPanes;

    /// <summary>
    /// Teilt die Terminal-Fläche: Neben (oder unter) der aktiven Sitzung entsteht eine weitere zum selben Server,
    /// in der Richtung, die gewählt wurde – auch innerhalb einer Teilung. Sie benutzt dessen stehende Verbindung
    /// mit – ohne neue Anmeldung. Bis zu <see cref="MaxPanes"/> Terminals teilen sich die Fläche.
    /// </summary>
    public SessionViewModel? Split(bool sideBySide)
    {
        if (ActiveSession is not { } current || !CanSplit || !Fits(current, sideBySide))
        {
            return null;
        }

        var second = AddSession(current.Profile, Sessions.IndexOf(current) + 1, activate: false);
        Join(current, second, sideBySide);
        ActiveSession = second;
        return second;
    }

    /// <summary>
    /// Zeigt eine andere offene Sitzung neben (oder unter) der aktiven. Steht sie schon in derselben Teilung,
    /// wird sie dorthin umgesetzt.
    /// </summary>
    public void SplitWith(SessionViewModel other, bool sideBySide)
    {
        if (ActiveSession is not { } current || ReferenceEquals(current, other) || !Sessions.Contains(other) || other.IsVirtual || ActiveSession is { IsDetached: true } or { IsVirtual: true })
        {
            return;
        }

        if (SplitOf(current) is { } group && group.Members.Contains(other))
        {
            MovePane(other, current, sideBySide ? PaneSide.Right : PaneSide.Bottom);
            return;
        }

        if (!CanSplit)
        {
            return;
        }

        if (!Fits(current, sideBySide))
        {
            return;
        }

        // Steht ihr Terminal in einem eigenen Fenster, kommt es dafür zurück.
        if (other.IsDetached)
        {
            other.IsDetached = false;
            DetachedChanged?.Invoke(other);
        }

        RemoveFromSplit(other);
        Join(current, other, sideBySide);
        UpdateLayout();
    }

    /// <summary>Ob die Teilung des aktiven Terminals einen Bereich vergrößern lässt (und nicht die Bereiche von tmux).</summary>
    public bool CanZoom => ActiveSession is { IsDetached: false } active && SplitOf(active) is { Fixed: false };

    /// <summary>
    /// Gibt dem aktiven Bereich die ganze Fläche der Teilung – oder wieder allen ihren Platz. Die übrigen Terminals
    /// bleiben, wo sie sind, und laufen weiter.
    /// </summary>
    public void ToggleZoom()
    {
        if (!CanZoom || ActiveSession is not { } active || SplitOf(active) is not { } group)
        {
            return;
        }

        group.Zoomed = group.Zoomed is null ? active : null;
        UpdateLayout();
    }

    /// <summary>Ob der aktive Bereich Nachbarn hat, zu denen sich der Fokus mit den Tasten bewegen lässt.</summary>
    public bool CanFocusPane => ActiveSession is { IsDetached: false } active && SplitOf(active) is not null;

    /// <summary>
    /// Macht den Nachbarn des aktiven Bereichs in dieser Richtung zum aktiven. Gibt es keinen, bleibt der Fokus; ein
    /// vergrößerter Bereich wird in jedem Fall wieder einer von allen.
    /// </summary>
    public void FocusPane(PaneDirection direction)
    {
        if (ActiveSession is not { } active || SplitOf(active) is not { } group)
        {
            return;
        }

        if (group.Zoomed is not null)
        {
            group.Zoomed = null;
            UpdateLayout();
        }

        if (SplitTree.Neighbor(group.Root, active, direction) is { } next)
        {
            ActiveSession = next;
        }
    }

    /// <summary>Ob es in der Teilung des aktiven Terminals etwas gleich zu verteilen gibt.</summary>
    public bool CanEqualize => ActiveSession is { IsDetached: false } active && SplitOf(active) is { Fixed: false };

    /// <summary>Gibt allen Bereichen der Teilung des aktiven Terminals gleich viel Platz.</summary>
    public void EqualizePanes()
    {
        if (!CanEqualize || ActiveSession is not { } active || SplitOf(active) is not { } group)
        {
            return;
        }

        group.Root = SplitTree.Equalize(group.Root);
        UpdateLayout();
    }

    /// <summary>
    /// Stellt einen Bereich an die Kante eines anderen derselben Teilung (Ziehen des Kopfes in der Terminal-Seite).
    /// <c>false</c>, wenn es nichts zu tun gibt oder es nicht geht: nicht in derselben Teilung, sie dieselbe
    /// Sitzung, die Bereiche eines Fensters von tmux (ihre Anordnung gehört tmux) oder die Teilung würde zu tief.
    /// </summary>
    public bool MovePane(SessionViewModel item, SessionViewModel target, PaneSide side)
    {
        if (ReferenceEquals(item, target) || SplitOf(item) is not { Fixed: false } group || !ReferenceEquals(SplitOf(target), group)
            || ActiveSession is not { } active || !ReferenceEquals(SplitOf(active), group))
        {
            return false;
        }

        var moved = SplitTree.Move(group.Root, item, target, side);
        if (ReferenceEquals(moved, group.Root))
        {
            return false;
        }

        group.Root = moved;
        group.Zoomed = null;
        UpdateLayout();
        return true;
    }

    /// <summary>
    /// Nimmt neue Anteile für die Kinder eines Zweigs der Teilung an, in der <paramref name="member"/> steht
    /// (<paramref name="path"/>: Nummern der Kinder von der Wurzel an; leer = die Wurzel) – so meldet die Seite, dass
    /// eine Trennlinie gezogen wurde. Gilt nur für die Teilung, die gerade zu sehen ist: Eine Meldung, die zu spät
    /// kommt (andere Teilung, Bereich schon weg), ändert nichts. <c>false</c> auch bei Anteilen, die nicht
    /// passen (<see cref="SplitTree.WithShares{T}"/>). Das gilt auch für die Bereiche eines Fensters von tmux: Die
    /// Anordnung gehört tmux, die Anteile am Platz nicht – die neuen Größen der Terminals gehen danach an tmux.
    /// </summary>
    public bool SetShares(SessionViewModel member, IReadOnlyList<int> path, IReadOnlyList<double> shares)
    {
        if (SplitOf(member) is not { } group || ActiveSession is not { } active || !ReferenceEquals(SplitOf(active), group)
            || SplitTree.WithShares(group.Root, path, shares) is not { } changed)
        {
            return false;
        }

        group.Root = changed;
        UpdateLayout();
        return true;
    }

    /// <summary>Nimmt die aktive Sitzung aus der Teilung; sie bleibt als Tab offen.</summary>
    public void Unsplit()
    {
        if (ActiveSession is { } current)
        {
            RemoveFromSplit(current);
            UpdateLayout();
        }
    }

    /// <summary>
    /// Nimmt eine bestimmte Sitzung aus der Teilung (das × in der Kopfzeile ihres Terminals); sie bleibt als Tab
    /// offen. War sie die aktive, wird es eine der übrigen – zu sehen bleibt, was von der Teilung übrig ist. Die
    /// Bereiche eines Fensters von tmux ordnet tmux: Sie lassen sich so nicht herausnehmen.
    /// </summary>
    public void Unsplit(SessionViewModel session)
    {
        if (session.IsVirtual || SplitOf(session) is null)
        {
            return;
        }

        var rest = NextPane(session);
        RemoveFromSplit(session);
        if (ReferenceEquals(ActiveSession, session) && rest is not null)
        {
            ActiveSession = rest;
        }

        UpdateLayout();
    }

    /// <summary>
    /// Merkt sich in den Einstellungen, welche Tabs offen sind und wie sie geteilt sind – für den nächsten Start.
    /// Ist „Tabs wiederherstellen“ aus, wird nichts aufgehoben.
    /// </summary>
    public void RememberOpenSessions()
    {
        if (!RestoreTabs)
        {
            (Settings.OpenTabs, Settings.OpenSplits, Settings.ActiveTab) = ([], [], -1);
            Settings.DetachedTabs = [];
            return;
        }

        // Fenster von tmux sind keine eigenen Verbindungen: Sie werden nicht gemerkt, und eine Teilung mit einem
        // von ihnen auch nicht.
        var real = Sessions.Where(session => !session.IsVirtual).ToList();
        Settings.OpenTabs = [.. real.Select(session => session.Profile.Id)];
        Settings.OpenSplits = [.. _splits.Where(split => split.Members.All(member => !member.IsVirtual)).Select(split => Saved(split, real))];
        Settings.ActiveTab = ActiveSession is { IsVirtual: false } active ? real.IndexOf(active) : -1;
        Settings.PinnedTabs = [.. Enumerable.Range(0, real.Count).Where(index => real[index].IsPinned)];
        Settings.DetachedTabs = [.. Enumerable.Range(0, real.Count).Where(index => real[index].IsDetached).Select(index => new SavedWindow(index, real[index].DetachedBounds))];
    }

    /// <summary>
    /// Öffnet die Tabs wieder, die beim letzten Beenden offen waren, samt ihren Teilungen. Ein Profil, das es nicht
    /// mehr gibt, entfällt. Verbunden wird wie immer erst, wenn ein Terminal zu sehen ist – also nur für den
    /// aktiven Tab und die, die sich mit ihm die Fläche teilen.
    /// </summary>
    public void RestoreOpenSessions()
    {
        if (!RestoreTabs || Sessions.Count > 0)
        {
            return;
        }

        var restored = AddSaved(Settings.OpenTabs, Settings.OpenSplits);
        SessionViewModel? At(int index) => index >= 0 && index < restored.Count ? restored[index] : null;

        // Angeheftet ist nur, was auch wieder links steht – fehlt davor ein Profil, stimmt die Reihenfolge trotzdem.
        foreach (var pinned in Settings.PinnedTabs.Select(At).OfType<SessionViewModel>())
        {
            if (Sessions.IndexOf(pinned) == PinnedCount)
            {
                pinned.IsPinned = true;
            }
        }

        // Was in einem eigenen Fenster stand, steht wieder dort – die App baut die Fenster, sobald sie davon hört.
        foreach (var saved in Settings.DetachedTabs)
        {
            if (At(saved.Tab) is { } session && CanDetach(session) && !session.IsSplit)
            {
                session.DetachedBounds = saved.Bounds;
                session.IsDetached = true;
            }
        }

        ActiveSession = At(Settings.ActiveTab) ?? Sessions.FirstOrDefault();
        UpdateLayout();
        foreach (var session in Sessions.Where(session => session.IsDetached).ToList())
        {
            DetachedChanged?.Invoke(session);
        }
    }

    // Öffnet gespeicherte Tabs hinter den offenen und teilt sie wie gespeichert – für die zuletzt offenen Tabs.
    // Nummer aus dem Gespeicherten → Sitzung; fehlt das Profil, gibt es keine.
    private List<SessionViewModel?> AddSaved(IReadOnlyList<string> tabs, IEnumerable<SavedSplit> splits)
    {
        var added = tabs
            .Select(id => _profiles.FirstOrDefault(profile => profile.Id == id))
            .Select(profile => profile is null ? null : AddSession(profile, Sessions.Count, activate: false))
            .ToList();
        SessionViewModel? At(int index) => index >= 0 && index < added.Count ? added[index] : null;

        // Eine Sitzung gehört nur zu einer Teilung; was es nicht gibt, fällt heraus.
        bool Usable(int index) => At(index) is { IsSplit: false };

        foreach (var saved in splits)
        {
            // Der gespeicherte Baum gilt, wenn er genau die Tabs der Teilung nennt und es diese Tabs gibt; sonst (eine
            // ältere Datei, ein kaputter Baum) entsteht die Teilung in der alten Form aus der Liste der Tabs.
            var stored = saved.ToTree();
            var tree = stored is not null && SplitTree.Leaves(stored).All(index => index < added.Count)
                ? stored
                : SplitTree.FromFlat([.. saved.Tabs.Distinct().Where(Usable).Take(MaxPanes)], saved.SideBySide);
            if (tree is not null && ReferenceEquals(tree, stored))
            {
                foreach (var index in SplitTree.Leaves(tree).Where(index => !Usable(index)).ToList())
                {
                    tree = tree is null ? null : SplitTree.Remove(tree, index);
                }
            }

            if (tree is null || SplitTree.Leaves(tree).Count < 2)
            {
                continue;
            }

            var members = SplitTree.Map(tree, index => At(index)!);
            _splits.Add(new SplitGroup(members));
            foreach (var member in SplitTree.Leaves(members))
            {
                member.IsSplit = true;
            }
        }

        return added;
    }

    /// <summary>Speichert ein neues oder geändertes Profil. Bei einem Fehler steht er im Protokoll und es kommt <c>null</c> zurück.</summary>
    public ServerProfile? SaveProfile(ServerProfile profile)
    {
        try
        {
            return _backend.Profiles.Save(WithIdentity(profile));
        }
        catch (BackendException e)
        {
            Log(L.Get("LogProfileNotSaved", profile.Name, $"{e.Code}: {e.Message}"), LogSeverity.Error);
            return null;
        }
    }

    /// <summary>Entfernt das Profil samt gespeicherter Geheimnisse. Offene Sitzungen dazu bleiben bestehen.</summary>
    public void DeleteProfile(ServerProfile profile)
    {
        try
        {
            _backend.Profiles.Delete(profile.Id);
            Settings.Bookmarks.Remove(profile.Id);
        }
        catch (BackendException e)
        {
            Log(L.Get("LogProfileNotDeleted", profile.Name, $"{e.Code}: {e.Message}"), LogSeverity.Error);
        }
    }

    /// <summary>Vorlage für ein neues Profil mit denselben Angaben – noch nicht gespeichert.</summary>
    public static ServerProfile CopyOf(ServerProfile profile) => profile with
    {
        Id = string.Empty,
        Name = L.Get("ProfileCopyName", profile.Name),
        LastConnected = null,
        HasSavedSecret = false,
    };

    /// <summary>Vergisst Passwort und Passphrase, die zu dem Profil gespeichert sind.</summary>
    public void ForgetSecrets(ServerProfile profile)
    {
        _backend.Profiles.ForgetSecrets(profile.Id);
        Log(L.Get("LogSecretsForgotten", profile.Name));
    }

    /// <summary>Die Server aus <c>~/.ssh/config</c> (Spec §8), noch ohne etwas anzulegen.</summary>
    public SshConfigListing ReadSshConfig() => _backend.Profiles.ReadSshConfig();

    /// <summary>Legt Profile für die gewählten Einträge an. Liefert die Zahl der neuen Profile (samt Sprung-Servern).</summary>
    /// <param name="linked">Die Profile bleiben mit ihrem Eintrag verknüpft und folgen der Datei bei jedem Start.</param>
    public int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false)
    {
        try
        {
            // Vorher gelesen: Was die Datei für die gewählten Server nennt und die App nicht übernimmt, steht danach im
            // Protokoll – nicht nur im Dialog, der dann zu ist.
            var skipped = _backend.Profiles.ReadSshConfig().Hosts.Where(host => aliases.Contains(host.Alias) && host.Skipped is { Count: > 0 }).ToList();
            var imported = _backend.Profiles.ImportSshConfig(aliases, linked);
            Log(L.Get("LogImported", imported));
            foreach (var host in skipped)
            {
                Log(L.Get("LogImportSkipped", Format.Visible(host.Alias), string.Join(", ", host.Skipped!)), LogSeverity.Warning);
            }

            return imported;
        }
        catch (BackendException e)
        {
            Log(L.Get("LogImportFailed", $"{e.Code}: {e.Message}"), LogSeverity.Error);
            return 0;
        }
    }

    /// <summary>
    /// Legt Profile für gespeicherte Sitzungen anderer Programme an (PuTTY, WinSCP, MobaXterm). Jedes geht durch
    /// dieselbe Prüfung wie ein eingetipptes; was sie nicht besteht, steht im Protokoll. Liefert die Zahl der neuen.
    /// </summary>
    public int ImportSessions(IReadOnlyList<ForeignSession> sessions)
    {
        var imported = sessions.Count(session => SaveProfile(ForeignSessions.ToProfile(session)) is not null);
        Log(L.Get("LogSessionsImported", imported));
        return imported;
    }

    /// <summary>Profile für die Command Bar: leere Eingabe = alle (Favoriten zuerst), sonst Treffer in Name, Host, Benutzer, Gruppe oder Tags.</summary>
    public IReadOnlyList<ServerProfile> SearchProfiles(string? query)
    {
        var needle = query?.Trim() ?? string.Empty;
        var matches = needle.Length == 0
            ? _profiles
            : _profiles.Where(profile => Matches(profile, needle));
        return [.. Ordered(matches)];
    }

    /// <summary>Namen aller Gruppen, als Vorschläge im Profil-Editor.</summary>
    public IReadOnlyList<string> GroupNames()
        => BindableList.Of(GroupPaths.Order(StringComparer.CurrentCultureIgnoreCase));

    /// <summary>Ersetzt die gespeicherten Befehle.</summary>
    public void SetSnippets(IEnumerable<Snippet> snippets)
    {
        Settings.Snippets = [.. snippets.Where(snippet => snippet.Name.Trim().Length > 0 && snippet.Text.Length > 0).Select(SnippetFolders.Cleaned)];

        // Ein Ordner, den es nicht mehr gibt, bleibt nicht als eingeklappt stehen – ein neuer gleichen Namens wäre sonst zu.
        var folders = SnippetFolders.Names(Settings.Snippets);
        Settings.CollapsedSnippetFolders.RemoveAll(folder => !folders.Contains(folder));
        OnPropertyChanged(nameof(Snippets));
        FilterSnippets();
    }

    /// <summary>
    /// Tippt einen gespeicherten Befehl in das aktive Terminal; mit <see cref="Snippet.Run"/> gleich mit Enter.
    /// <c>false</c>, wenn keine Sitzung verbunden ist oder sie keine Shell hat.
    /// </summary>
    public bool RunSnippet(Snippet snippet)
    {
        if (ActiveSession is not { State: ConnectionState.Connected, IsFilesOnly: false } session)
        {
            return false;
        }

        // Ein Terminal kennt Zeilenenden nur als Wagenrücklauf – so, wie die Enter-Taste ihn schickt.
        var text = snippet.Text.ReplaceLineEndings("\r");
        session.Send(snippet.Run ? text.TrimEnd('\r') + "\r" : text);
        return true;
    }

    /// <summary>
    /// Holt eine Datei des Servers und öffnet sie im Editor; nach jedem Speichern geht sie zurück zum Server.
    /// </summary>
    public void EditRemoteFile(SessionViewModel session, RemoteEntry entry, string path)
    {
        if (_editor is null || session.Files.FileSystem is not { } files || entry.IsDirectory || entry.IsSpecial)
        {
            return;
        }

        switch (_editor.Open(files, session.Profile.Name, path, entry.Size, entry.Modified))
        {
            case EditOutcome.Started:
                Log(L.Get("LogEditFetching", entry.DisplayName, session.Profile.Name));
                break;

            case EditOutcome.TooLarge:
                Log(L.Get("LogEditTooLarge", entry.DisplayName, Format.FileSize(RemoteEditor.MaxSize, isDirectory: false)), LogSeverity.Warning);
                break;

            case EditOutcome.BadName:
                Log(L.Get("LogEditBadName", entry.DisplayName), LogSeverity.Warning);
                break;
        }
    }

    /// <summary>Vorschlag für den Namen der Datei, in die der Verlauf eines Terminals gespeichert wird.</summary>
    public static string TerminalFileName(SessionViewModel session, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. session.Profile.Name.Select(character => invalid.Contains(character) ? '_' : character)]).Trim();
        return $"{(name.Length > 0 ? name : "terminal")}-{now.ToLocalTime():yyyyMMdd-HHmmss}.txt";
    }

    /// <summary>
    /// Speichert den Verlauf eines Terminals als Textdatei. <paramref name="text"/> trennt Zeilen mit
    /// <c>\n</c>; geschrieben werden die Zeilenenden des Rechners.
    /// </summary>
    public bool SaveTerminalText(SessionViewModel session, string path, string text)
    {
        try
        {
            File.WriteAllText(path, text.ReplaceLineEndings() + Environment.NewLine);
            Log(L.Get("LogTerminalSaved", session.Title, path));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log(L.Get("LogTerminalNotSaved", session.Title, e.Message), LogSeverity.Error);
            return false;
        }
    }

    // Das ausführliche Protokoll einer Verbindung steht unter „Ausgabe“, mit dem Namen des Servers davor. Die Zeile
    // kommt englisch vom Kern und enthält Text des Servers (wie er sich nennt) – also ohne Steuerzeichen, gekürzt.
    private void OnTraced(SessionViewModel session, string text)
    {
        var clean = Format.Visible(new string([.. text.Where(character => !char.IsControl(character)).Take(400)]));
        Log($"{Format.Visible(session.Profile.Name)}: {clean}");
    }

    public void Log(string text, LogSeverity severity = LogSeverity.Info)
    {
        var entry = new LogEntry(DateTimeOffset.Now, text, severity);
        Output.Add(entry);
        while (Output.Count > MaxLogEntries)
        {
            Output.RemoveAt(0);
        }

        if (severity != LogSeverity.Info)
        {
            Problems.Add(entry);
            while (Problems.Count > MaxLogEntries)
            {
                Problems.RemoveAt(0);
            }

            UpdateProblemsTitle();
        }
    }

    public void ClearProblems()
    {
        Problems.Clear();
        UpdateProblemsTitle();
    }

    /// <summary>
    /// Sucht nach einer neueren Version. Beim Start (<paramref name="manual"/> = <c>false</c>) nur, wenn es
    /// eingeschaltet ist und die letzte Suche über einen Tag her ist. Von Hand (Palette) immer und mit Rückmeldung,
    /// auch wenn nichts da ist. Fehler sind still – der Kern meldet „kein Update".
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updates is null)
        {
            if (manual)
            {
                Log(L.Get("UpdateCheckUnavailable"));
            }

            return;
        }

        if (!manual && (!CheckForUpdates || CheckedWithinADay()))
        {
            return;
        }

        Settings.LastUpdateCheck = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var found = await _updates.CheckAsync(_currentVersion).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            if (found is not null)
            {
                AvailableUpdate = found;
                Log(L.Get("UpdateAvailable", found.Version, _currentVersion.ToString(3)));
            }
            else if (manual)
            {
                Log(L.Get("UpdateUpToDate", _currentVersion.ToString(3)));
            }
        });
    }

    /// <summary>Nimmt den Update-Hinweis wieder weg (der Benutzer hat ihn geschlossen).</summary>
    public void DismissUpdate() => AvailableUpdate = null;

    private bool CheckedWithinADay()
        => DateTimeOffset.TryParse(Settings.LastUpdateCheck, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1);

    /// <summary>
    /// Der Bereich, der nach <paramref name="current"/> an der Reihe ist (<paramref name="delta"/> +1) oder davor
    /// (−1) – nur eingeblendete zählen, am Ende geht es von vorn weiter. Liegt der Fokus in keinem Bereich
    /// (<c>null</c>: Titelleiste, ein Menü), ist es der erste bzw. der letzte.
    /// </summary>
    public ShellPane PaneAfter(ShellPane? current, int delta)
    {
        List<ShellPane> visible = [ShellPane.Terminal];
        if (ShowsFiles)
        {
            visible.Insert(0, ShellPane.Files);
        }

        if (ShowsSnippets)
        {
            visible.Add(ShellPane.Snippets);
        }

        if (ShowsBottom)
        {
            visible.Add(ShellPane.Bottom);
        }

        var index = current is { } pane ? visible.IndexOf(pane) : -1;
        if (index < 0)
        {
            return delta > 0 ? visible[0] : visible[^1];
        }

        return visible[((index + delta) % visible.Count + visible.Count) % visible.Count];
    }

    /// <summary>
    /// Zeigt die Startseite mit den Servern, ohne eine Sitzung zu schließen: Kein Tab ist dann aktiv, die Sitzungen
    /// laufen weiter, und ein Klick auf ihren Reiter holt sie zurück.
    /// </summary>
    public void ShowHome() => ActiveSession = null;

    /// <summary>Blendet den unteren Bereich ein und lässt ihn die Transfers zeigen.</summary>
    public void ShowTransfers()
    {
        IsTerminalOnly = false;
        IsBottomPanelVisible = true;
        BottomTab = BottomTab.Transfers;
        UiRequested?.Invoke(UiRequest.ShowTransfers);
    }

    /// <summary>Gibt dem Terminal den Tastaturfokus zurück, z. B. nach dem Schließen der Suchleiste.</summary>
    public void FocusTerminal() => UiRequested?.Invoke(UiRequest.FocusTerminal);

    /// <summary>Ändert die Schriftgröße des Terminals um <paramref name="delta"/> Punkt.</summary>
    public void ZoomTerminal(int delta)
        => TerminalFontSize = Math.Clamp(TerminalFontSize + delta, TerminalThemes.MinFontSize, TerminalThemes.MaxFontSize);

    public void Dispose()
    {
        _editor?.Dispose();
        _follow?.Dispose();
        _relay?.Dispose();
        _journal?.Dispose();
        _drops?.Dispose();
    }

    /// <summary>
    /// Bietet Einträge des Servers einem anderen Programm an – beim Ziehen aus der Liste oder für „Kopieren“:
    /// Dateien und ganze Ordner, die erst geladen werden, wenn der Empfänger sie abholt (<see cref="VirtualDrop"/>).
    /// <c>null</c>, wenn es nichts anzubieten gibt. Die Pfade auf dem Server stehen damit fest: Bis zum Einfügen kann
    /// der Dateibrowser längst woanders sein. Was in den Ordnern liegt, wird sofort ermittelt.
    /// </summary>
    public VirtualDrop? VirtualFilesForDrop(SessionViewModel session, IEnumerable<RemoteEntry> entries)
    {
        if (_drops is null || session.Files.DropItems(entries) is not { Count: > 0 } items)
        {
            return null;
        }

        var drop = new VirtualDrop(_drops, _backend.Transfers, () => session.Files.FileSystem, items, (notice, detail) => _dispatcher.Post(() => Note(notice, session, detail)));
        drop.Prepare();
        return drop;
    }

    /// <summary>Vermerkt im Protokoll, dass Einträge in der Zwischenablage liegen.</summary>
    public void NoteCopied(SessionViewModel session, int count) => Log(L.Get("LogCopiedFiles", count, session.Profile.Name));

    private void Note(DropNotice notice, SessionViewModel session, string? detail = null)
    {
        switch (notice)
        {
            case DropNotice.ListFailed:
                Log(L.Get("LogDropListFailed", session.Profile.Name, detail ?? string.Empty), LogSeverity.Warning);
                break;
            case DropNotice.TooMany:
                Log(L.Get("LogDropTooMany", VirtualDrop.MaxEntries), LogSeverity.Warning);
                break;
            case DropNotice.TooLong:
                Log(L.Get("LogDropTooLong", VirtualDrop.MaxPathLength), LogSeverity.Warning);
                break;
            case DropNotice.Fetching:
                Log(L.Get("LogDropFetching", session.Profile.Name));
                break;
            case DropNotice.NotConnected:
                Log(L.Get("LogDropNotConnected", session.Profile.Name), LogSeverity.Warning);
                break;
        }
    }

    // Die Sprung-Server eines Profils, der erste zuerst – nur für die Anzeige; den Weg selbst bildet der Kern.
    private List<ServerProfile> ViaOf(ServerProfile profile)
    {
        List<ServerProfile> via = [];
        var current = profile;
        while (via.Count < RouteTrace.MaxHops && current.JumpId is { } jump
            && _profiles.FirstOrDefault(other => other.Id == jump) is { } next && !via.Contains(next))
        {
            via.Insert(0, next);
            current = next;
        }

        return via;
    }

    private SessionViewModel AddSession(ServerProfile profile, int index, bool activate = true, Func<IServerConnection>? connect = null)
    {
        var session = connect is null
            ? new SessionViewModel(profile, _backend, _dispatcher, _prompts, ConnectionToShare, SessionSigningIn, _retry)
            : new SessionViewModel(profile, _backend, _dispatcher, _prompts, connect: connect);
        session.ConnectionEnded += OnConnectionEnded;
        session.Traced += OnTraced;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State))
            {
                OnSessionStateChanged(session);
                RefreshTunnels();
            }
        };

        // Erst, wenn die Verbindung ganz steht (Dateien und Auslastung hängen dann daran) – über die fragt die App
        // nach laufenden tmux-/screen-Sitzungen.
        session.Settled += () => OnConnectedForMux(session);
        session.Settled += () => _ = AskForCommandsAsync(session);
        session.Settled += () => OnConnectedForCompletion(session);
        WatchSystem(session);
        WatchAlerts(session);
        WatchSnippets(session);
        session.Files.FileOpened += OnFileOpened;
        session.Files.ShowHidden = ShowHiddenFiles;
        session.Files.ShowTree = ShowDirectoryTree;
        session.ShowServerTitle = ServerTabTitles;
        session.Via = connect is null ? ViaOf(profile) : [];
        (session.Files.Sort, session.Files.SortDescending) = (FileSort, FileSortDescending);
        // Ein neuer Reiter steht nie zwischen den angehefteten.
        Sessions.Insert(Math.Clamp(index, PinnedCount, Sessions.Count), session);
        if (activate)
        {
            ActiveSession = session;
        }

        Log(L.Get("LogSessionOpened", profile.Name, profile.Address));
        return session;
    }

    /// <summary>
    /// So lange wird gewartet, bevor die App noch einmal fragt, wie der Server Befehle annimmt – wenn er es beim
    /// ersten Mal nicht in der Frist sagen konnte (ein Windows, dessen erstes PowerShell noch startet).
    /// </summary>
    public TimeSpan CommandsRetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    // So oft wird nachgefragt; danach bleibt es bei dem, was ohne Antwort gilt (Befehle da, kein Windows).
    private const int CommandsRetries = 4;

    // Nach dem Verbinden fragen, wie der Server Befehle annimmt: Wenn gar nicht (Konto ohne Befehle), zeigt der
    // Dateibrowser nicht erst an, was welche bräuchte; unter Windows (PowerShell) nur, was es dort gibt. Über FTP
    // steht das von Anfang an fest; ein Fenster von tmux fragt nicht selbst. Kann der Kern es noch nicht sagen
    // (seine Probe blieb stumm), wird nachgefragt – sonst stünde ein langsamer Windows-Server für die ganze
    // Verbindung wie ein Linux-Server da (gesehen 2026-10-20).
    internal async Task AskForCommandsAsync(SessionViewModel session)
    {
        if (session.State != ConnectionState.Connected || session.IsVirtual || session.Profile.IsFtp || session.Profile.IsLocal || session.Inspector.Monitor is not { } monitor)
        {
            return;
        }

        var shell = await monitor.CommandShellAsync();
        for (var retry = 0; shell == CommandShell.Unknown && retry < CommandsRetries; retry++)
        {
            await Task.Delay(CommandsRetryDelay);
            if (session.State != ConnectionState.Connected || !ReferenceEquals(monitor, session.Inspector.Monitor))
            {
                return;
            }

            shell = await monitor.CommandShellAsync();
        }

        // Ohne Antwort bleibt, was galt – erst eine Antwort ändert etwas.
        if (shell == CommandShell.Unknown)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            // Unterdessen neu verbunden: Dann gilt die Antwort der neuen Verbindung.
            if (ReferenceEquals(monitor, session.Inspector.Monitor))
            {
                session.Files.HasCommands = shell != CommandShell.None;
                session.Files.IsWindowsServer = shell == CommandShell.Windows;
            }
        });
    }

    // Steht schon eine Verbindung zu diesem Server, fährt die neue Sitzung darauf mit – ohne Anmeldung.
    private IServerConnection? ConnectionToShare(SessionViewModel session)
        => session.Profile.IsLocal ? null : Sessions
            .FirstOrDefault(other => !ReferenceEquals(other, session)
                && other.Profile.Id == session.Profile.Id
                && !other.IsVirtual
                && other.State == ConnectionState.Connected
                // Ohne die SSH-Verbindung unter ihrem Mosh-Terminal ist nichts zum Mitbenutzen da.
                && !other.IsLinkLost)
            ?.Connection;

    // Meldet sich gerade eine andere Sitzung beim selben Server an, wartet die neue auf sie.
    private SessionViewModel? SessionSigningIn(SessionViewModel session)
        => session.Profile.IsLocal ? null : Sessions.FirstOrDefault(other => !ReferenceEquals(other, session)
            && other.Profile.Id == session.Profile.Id
            && other.IsSettling);

    // Ein Ende mit Fehler gehört unter „Probleme“; „geschlossen“ und „exit“ sind keine.
    private void OnConnectionEnded(SessionViewModel session, ConnectionStatus status)
    {
        var failed = status.Reason is not (CloseReason.User or CloseReason.Remote or CloseReason.None);
        var wasConnected = _connectedSince.ContainsKey(session);
        Log($"{session.Profile.Name}: {session.StatusText}", failed ? LogSeverity.Error : LogSeverity.Info);
        SoundForEnd(session, status, wasConnected);
        NoteEnded(session, session.StatusText);

        // „exit“ in der Shell (oder Strg+D): Der Benutzer will hinaus, also geht der Tab mit – wie in einem
        // Terminal-Programm. Erst danach, nicht mitten in der Meldung: Die Sitzung räumt nach ihr noch auf. Ein
        // Fenster von tmux endet mit seinem tragenden Tab (ShellViewModel.Tmux), nicht hier.
        if (status.Reason == CloseReason.Remote && wasConnected && session.WasTypedIn && !session.IsVirtual)
        {
            _dispatcher.Post(() =>
            {
                if (Sessions.Contains(session) && session.IsClosed)
                {
                    CloseSession(session);
                }
            });
        }
    }

    // Doppelklick auf eine Datei im Dateibrowser: bearbeiten. Welche Sitzung gemeint ist, sagt der Browser selbst –
    // zwei Server können gleich aussehende Einträge unter demselben Pfad haben.
    private void OnFileOpened(SftpBrowserViewModel browser, RemoteEntry entry, string path)
    {
        if (Sessions.FirstOrDefault(session => ReferenceEquals(session.Files, browser)) is { } session)
        {
            EditRemoteFile(session, entry, path);
        }
    }

    private SplitGroup? SplitOf(SessionViewModel session) => _splits.FirstOrDefault(split => split.Members.Contains(session));

    // Die Teilung für die Einstellungen: der Baum mit den Nummern der Tabs.
    private static SavedSplit Saved(SplitGroup split, List<SessionViewModel> tabs)
        => SavedSplit.Of(SplitTree.Map(split.Root, member => tabs.IndexOf(member)));

    // Welcher Bereich nach `session` den Fokus bekommt, wenn sie geht: der nächste der Teilung, beim letzten der
    // vorige. `null`, wenn sie in keiner Teilung steht.
    private SessionViewModel? NextPane(SessionViewModel session)
    {
        if (SplitOf(session) is not { } split)
        {
            return null;
        }

        var members = split.Members;
        var index = members.ToList().IndexOf(session);
        return members.Count < 2 ? null : members[index + 1 < members.Count ? index + 1 : index - 1];
    }

    // Ob neben `current` noch ein Bereich in dieser Richtung Platz hat – ohne etwas zu ändern. Gerechnet wird an
    // einem Baum aus Nummern; der Baum selbst lehnt zu viele und zu tiefe Teilungen ab, indem er sich nicht ändert.
    private bool Fits(SessionViewModel current, bool sideBySide)
    {
        if (SplitOf(current) is not { } split)
        {
            return true;
        }

        var members = split.Members;
        var probe = SplitTree.Map(split.Root, member => members.ToList().IndexOf(member));
        return SplitTree.Leaves(SplitTree.Insert(probe, members.ToList().IndexOf(current), -1, sideBySide)).Count > members.Count;
    }

    // Stellt `other` zu `current`: in dessen Teilung, in der Richtung, die verlangt ist – oder beide in eine neue.
    private void Join(SessionViewModel current, SessionViewModel other, bool sideBySide)
    {
        if (SplitOf(current) is { } split)
        {
            split.Root = SplitTree.Insert(split.Root, current, other, sideBySide);
            split.Zoomed = null;
        }
        else
        {
            _splits.Add(new SplitGroup(SplitTree.Insert(new SplitLeaf<SessionViewModel>(current), current, other, sideBySide)));
            current.IsSplit = true;
        }

        other.IsSplit = true;
    }

    private void RemoveFromSplit(SessionViewModel session)
    {
        if (SplitOf(session) is not { } split)
        {
            return;
        }

        session.IsSplit = false;
        if (ReferenceEquals(split.Zoomed, session))
        {
            split.Zoomed = null;
        }

        // Ein Terminal allein ist keine Teilung mehr.
        if (SplitTree.Remove(split.Root, session) is { } rest && SplitTree.Leaves(rest).Count >= 2)
        {
            split.Root = rest;
            return;
        }

        foreach (var last in split.Members.Where(member => !ReferenceEquals(member, session)))
        {
            last.IsSplit = false;
        }

        _splits.Remove(split);
    }

    private void UpdateLayout()
    {
        Layout = ActiveSession switch
        {
            null => TerminalLayout.Empty,

            // Ihr Terminal steht in einem eigenen Fenster; hier zeigt die Fläche nur, wo.
            { IsDetached: true } => TerminalLayout.Empty,
            var active when SplitOf(active) is { } split => new TerminalLayout(split.Root, active, split.Zoomed, split.Fixed),
            var active => new TerminalLayout(new SplitLeaf<SessionViewModel>(active), active),
        };
        OnPropertyChanged(nameof(ShowsDetachedNote));
    }

    // Gemessen wird nur für den Server, dessen Werte gerade zu sehen sind – in der Zeile unten (MonitorBar) oder auf
    // der Seite mit seinen Einzelheiten (Details).
    private void UpdateMonitoring()
    {
        foreach (var session in Sessions)
        {
            session.Inspector.SetWatched((MonitorBar || ReferenceEquals(Details, session)) && !IsTerminalOnly && ReferenceEquals(session, ActiveSession) && !session.Profile.IsLocal);

            // Mit den Hinweisen zu den Schwellen messen alle offenen Sitzungen – nur SSH kann das.
            // Ebenso, solange die Übersicht aller Sitzungen zu sehen ist – und nur so lange.
            session.Inspector.SetAlerting((ServerAlerts || IsOverviewShown) && session.CanMeasure);
        }
    }

    private void UpdateProblemsTitle()
    {
        ProblemsTitle = Problems.Count == 0 ? L.Get("Problems") : L.Get("ProblemsCount", Problems.Count);
        RefreshCounts();
    }

    partial void OnActiveSessionChanged(SessionViewModel? value)
    {
        RefreshTunnels();

        // Angesehen: Die Glocke am Reiter hat ihren Zweck erfüllt.
        value?.ClearBell();

        // Die Seite der Protokolle eines Servers liegt über den Bereichen und gehört zu seiner Sitzung:
        // Wird ein anderer Tab aktiv oder die Startseite, stünde sie sonst weiter davor.
        if (Logs is { } logs && !ReferenceEquals(logs.Session, value))
        {
            Logs = null;
        }

        if (Details is { } details && !ReferenceEquals(details, value))
        {
            Details = null;
        }

        // Wird ein anderer Bereich der Teilung aktiv, ist nicht mehr der vergrößerte der gemeinte.
        if (value is not null && SplitOf(value) is { Zoomed: { } zoomed } group && !ReferenceEquals(zoomed, value))
        {
            group.Zoomed = null;
        }

        UpdateLayout();
        UpdateMonitoring();

        // An mehrere tippen gilt der geteilten Ansicht, in der es eingeschaltet wurde.
        if (value is not { IsSplit: true })
        {
            BroadcastInput = false;
        }
    }

    partial void OnIsSftpVisibleChanged(bool value) => Settings.ShowSftp = value;

    partial void OnIsSnippetsVisibleChanged(bool value) => Settings.ShowSnippets = value;

    partial void OnIsBottomPanelVisibleChanged(bool value) => Settings.ShowBottomPanel = value;

    partial void OnShowHiddenFilesChanged(bool value)
    {
        Settings.ShowHiddenFiles = value;
        foreach (var session in Sessions)
        {
            session.Files.ShowHidden = value;
        }

        if (_local is not null)
        {
            _local.ShowHidden = value;
        }
    }

    partial void OnShowDirectoryTreeChanged(bool value)
    {
        Settings.ShowDirectoryTree = value;
        foreach (var session in Sessions)
        {
            session.Files.ShowTree = value;
        }
    }

    partial void OnRestoreTabsChanged(bool value) => Settings.RestoreTabs = value;

    partial void OnConfirmCloseChanged(bool value) => Settings.ConfirmClose = value;

    /// <summary>
    /// Was beim Beenden verloren ginge, als Text für die Rückfrage – oder <c>null</c>, wenn nichts zu fragen ist:
    /// weil der Benutzer die Frage abgestellt hat, oder weil weder eine Sitzung verbunden ist noch ein Transfer läuft.
    /// </summary>
    public string? CloseQuestion()
    {
        if (!ConfirmClose)
        {
            return null;
        }

        var connected = Sessions.Count(session => session.State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Authenticating);
        var running = Transfers.ActiveCount;
        var parts = new List<string>();
        if (connected > 0)
        {
            parts.Add(connected == 1 ? L.Get("CloseAskSessionOne") : L.Get("CloseAskSessions", connected));
        }

        if (running > 0)
        {
            parts.Add(running == 1 ? L.Get("CloseAskTransferOne") : L.Get("CloseAskTransfers", running));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    partial void OnCheckForUpdatesChanged(bool value) => Settings.CheckForUpdates = value;

    partial void OnProfileSortChanged(ProfileSort value)
    {
        Settings.ProfileSort = value;
        LoadProfiles();
    }

    partial void OnTerminalThemeChanged(TerminalTheme value)
    {
        Settings.TerminalTheme = value.Id;
        OnPropertyChanged(nameof(CanRemoveTheme));
    }

    partial void OnTerminalFontSizeChanged(int value) => Settings.TerminalFontSize = value;

    partial void OnHomeFilterChanged(string value) => UpdateHome();

    // Favoriten zuerst, dann nach der gewählten Reihenfolge. OrderBy ist stabil: „wie gespeichert“ bleibt erhalten.
    private IEnumerable<ServerProfile> Ordered(IEnumerable<ServerProfile> profiles)
    {
        var favoritesFirst = profiles.OrderByDescending(profile => profile.IsFavorite);
        return ProfileSort switch
        {
            ProfileSort.Name => favoritesFirst.ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase),
            ProfileSort.Recent => favoritesFirst.ThenByDescending(profile => profile.LastConnected ?? DateTimeOffset.MinValue),
            _ => favoritesFirst,
        };
    }

    private static bool Matches(ServerProfile profile, string needle)
        => Contains(profile.Name, needle)
            || Contains(profile.Host, needle)
            || Contains(profile.User, needle)
            || Contains(profile.Group, needle)
            || Contains(profile.Notes, needle)
            || profile.Tags.Any(tag => Contains(tag, needle));

    private static bool Contains(string text, string needle) => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Terminals, die sich die Fläche teilen: ihre Anordnung als Baum, der vergrößerte Bereich (wenn es einen gibt)
    /// und ob die Anordnung feststeht – bei den Bereichen eines Fensters von tmux (<see cref="Fixed"/>) gibt tmux sie
    /// vor (nur die Anteile am Platz gehören der App).
    /// </summary>
    private sealed class SplitGroup(SplitNode<SessionViewModel> root, bool fixedByTmux = false)
    {
        public SplitNode<SessionViewModel> Root { get; set; } = root;

        public SessionViewModel? Zoomed { get; set; }

        public bool Fixed { get; } = fixedByTmux;

        public IReadOnlyList<SessionViewModel> Members => SplitTree.Leaves(Root);
    }
}
