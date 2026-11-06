using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Woraus gelesen wird: das Journal von systemd (<c>Path</c> ist <c>null</c>) oder eine Datei.</summary>
public sealed record LogSource(string? Path, string Name)
{
    public bool IsJournal => Path is null;

    public override string ToString() => Name;
}

/// <summary>
/// Die Protokolle eines Servers ansehen: das Journal (nach Zeitraum, Schwere und Einheit – das filtert der Server)
/// oder eine Datei unter <c>/var/log</c> (die letzten Zeilen). Der Textfilter wirkt hier, auf das Gelesene. Die
/// Befehle baut der Kern; von hier gehen nur die Auswahl und der Name einer Einheit hinüber. Alles, was gezeigt
/// wird, ist Ausgabe des Servers und steht als reiner Text da.
/// </summary>
public sealed partial class ServerLogsViewModel : ObservableObject
{
    /// <summary>Wo die Dateien gesucht werden, die die Seite zur Wahl stellt.</summary>
    public const string LogDirectory = "/var/log";

    /// <summary>So viele Dateien und Unterordner werden höchstens angeboten.</summary>
    public const int MaxFiles = 300;
    public const int MaxFolders = 20;

    // Die Schwere, die die Seite zur Wahl stellt: alles, ab Fehler, ab Warnung, ab Hinweis, ab Meldung.
    private static readonly int?[] Priorities = [null, 3, 4, 5, 6];

    private readonly SessionViewModel _session;
    private IReadOnlyList<ServerLogLine> _all = [];
    private int _load;

    public ServerLogsViewModel(SessionViewModel session)
    {
        _session = session;
        Sources = BindableList.Of([new LogSource(null, L.Get("LogsJournal"))]);
        Source = Sources[0];
    }

    /// <summary>Die Sitzung, deren Server gelesen wird.</summary>
    public SessionViewModel Session => _session;

    public string Title => L.Get("LogsTitle", _session.Title);

    /// <summary>Das Journal und die Dateien unter <see cref="LogDirectory"/>, die sich lesen lassen.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LogSource> Sources { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJournal))]
    public partial LogSource Source { get; set; }

    /// <summary>Zeitraum, Schwere und Einheit gibt es nur beim Journal.</summary>
    public bool IsJournal => Source.IsJournal;

    [ObservableProperty]
    public partial LogSince Since { get; set; } = LogSince.Day;

    /// <summary>Nummer der Auswahl „ab welcher Schwere“ (0 = alles).</summary>
    [ObservableProperty]
    public partial int PriorityChoice { get; set; }

    /// <summary>Name einer Einheit (<c>nginx.service</c>); leer für alle.</summary>
    [ObservableProperty]
    public partial string Unit { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Lines { get; set; } = LogQuery.DefaultLines;

    /// <summary>Nur Zeilen, in denen jedes dieser Wörter vorkommt – gefiltert wird hier, nicht am Server.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>Die gezeigten Zeilen, älteste zuerst. Bei jedem Füllen eine neue Liste.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ServerLogLine> Shown { get; private set; } = BindableList.Of<ServerLogLine>([]);

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Wie viele Zeilen gelesen und wie viele davon zu sehen sind.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Warum nichts zu sehen ist; leer, wenn es gelang.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Das Konto sieht nur seine eigenen Einträge im Journal.</summary>
    [ObservableProperty]
    public partial bool IsLimited { get; private set; }

    /// <summary>Was gerade zu sehen ist, als Text – für die Zwischenablage.</summary>
    public string CopyText() => string.Join(Environment.NewLine, Shown.Select(line => line.Text));

    /// <summary>
    /// Sucht die Dateien unter <see cref="LogDirectory"/> und eine Ebene darunter (<c>nginx/</c>, <c>apt/</c>). Was
    /// sich nicht auflisten lässt, fehlt einfach – das Journal steht immer zur Wahl.
    /// </summary>
    public async Task LoadSourcesAsync()
    {
        if (_session.Files.FileSystem is not { } files)
        {
            return;
        }

        var found = new List<LogSource>();
        try
        {
            var top = await files.ListAsync(LogDirectory);
            Add(found, LogDirectory, top);
            foreach (var folder in top.Where(entry => entry is { IsDirectory: true, IsHidden: false } && IsPlainName(entry.Name)).OrderBy(entry => entry.Name, StringComparer.Ordinal).Take(MaxFolders))
            {
                try
                {
                    Add(found, $"{LogDirectory}/{folder.Name}", await files.ListAsync($"{LogDirectory}/{folder.Name}"));
                }
                catch (RemoteFileException)
                {
                    // Ein Ordner, den das Konto nicht lesen darf (`/var/log/private`): dann eben ohne ihn.
                }
            }
        }
        catch (RemoteFileException)
        {
            // Kein /var/log oder kein Zugriff: Es bleibt beim Journal.
        }

        var chosen = Source;
        // Das Journal bleibt vorn stehen.
        Sources = BindableList.Of([.. Sources.Where(source => source.Path is null), .. found.Take(MaxFiles)]);
        Source = Sources.FirstOrDefault(source => source == chosen) ?? Sources[0];
    }

    /// <summary>Liest die gewählte Quelle neu. Ein früherer Lauf, der später antwortet, gilt nicht mehr.</summary>
    public async Task LoadAsync()
    {
        var load = ++_load;
        if (_session.Inspector.Monitor is not { } monitor)
        {
            Show([], L.Get("SftpConnection"));
            return;
        }

        var unit = Unit.Trim();
        if (IsJournal && unit.Length > 0 && !LogQuery.IsUnitName(unit))
        {
            Show([], L.Get("LogsBadUnit"));
            return;
        }

        IsLoading = true;
        try
        {
            var log = await monitor.LogsAsync(new LogQuery(Source.Path, Since, Priorities[Math.Clamp(PriorityChoice, 0, Priorities.Length - 1)], unit, Lines));
            if (load == _load)
            {
                IsLimited = log.Limited;
                Show(log.Lines, string.Empty);
            }
        }
        catch (RemoteFileException e)
        {
            if (load == _load)
            {
                IsLimited = false;
                Show([], e.Code == "no_journal" ? L.Get("LogsNoJournal") : FailureText.Describe(e.Code, e.Message));
            }
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void Show(IReadOnlyList<ServerLogLine> lines, string error)
    {
        _all = lines;
        Error = error;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shown = words.Length == 0
            ? _all
            : [.. _all.Where(line => words.All(word => line.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))];
        Shown = BindableList.Of(shown);
        Status = HasError || IsLoading && _all.Count == 0
            ? string.Empty
            : words.Length == 0 ? L.Get("LogsCount", _all.Count) : L.Get("LogsCountFiltered", shown.Count, _all.Count);
    }

    // Dateien, die sich als Text lesen lassen: keine Ordner, keine Pipes, nichts Leeres – und nur Namen, die als
    // Teil eines Pfads taugen. Die Namen kommen vom Server.
    private static void Add(List<LogSource> found, string directory, IReadOnlyList<RemoteEntry> entries)
    {
        foreach (var entry in entries
            .Where(entry => entry is { IsDirectory: false, IsSpecial: false, Size: > 0 } && IsPlainName(entry.Name) && !IsBinary(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var path = $"{directory}/{entry.Name}";
            found.Add(new LogSource(path, Format.Visible(path[(LogDirectory.Length + 1)..])));
        }
    }

    private static bool IsPlainName(string name)
        => name is { Length: > 0 and <= 255 } and not ("." or "..") && !name.Contains('/') && !name.Any(char.IsControl);

    // Was unter /var/log liegt und kein Text ist: die Anmeldelisten und die Dateien des Journals.
    private static bool IsBinary(string name)
        => name is "wtmp" or "btmp" or "lastlog" or "faillog" or "tallylog"
            || name.StartsWith("wtmp.", StringComparison.Ordinal)
            || name.StartsWith("btmp.", StringComparison.Ordinal)
            || name.EndsWith(".journal", StringComparison.Ordinal)
            || name.EndsWith(".xz", StringComparison.Ordinal)
            || name.EndsWith(".bz2", StringComparison.Ordinal)
            || name.EndsWith(".zst", StringComparison.Ordinal);
}
