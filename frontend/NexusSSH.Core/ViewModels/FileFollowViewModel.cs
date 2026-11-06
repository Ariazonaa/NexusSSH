using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Zeile der mitgelesenen Datei, wie sie gezeigt wird.</summary>
public sealed record FollowLine(string Text, bool Highlighted);

/// <summary>
/// Eine Datei des Servers mitlesen (wie <c>tail -f</c>), ohne Terminal: die letzten Zeilen und alles, was
/// dazukommt, mit Filter und Hervorhebung. Der Text kommt vom Server und ist fremd – er wird Zeile für Zeile ohne
/// Steuerzeichen gezeigt und nie als etwas anderes als Text behandelt.
/// </summary>
public sealed partial class FileFollowViewModel : ObservableObject, IDisposable
{
    /// <summary>So viele Zeilen bleiben stehen; ältere fallen weg.</summary>
    public const int MaxLines = 5000;

    /// <summary>Längere Zeilen werden gekürzt – eine Zeile ohne Umbruch (ein Abbild, eine Binärdatei) soll die Ansicht nicht lähmen.</summary>
    public const int MaxLineLength = 2000;

    private readonly IUiDispatcher _dispatcher;
    private readonly List<string> _lines = [];
    private IDisposable? _following;
    private string _partial = string.Empty;

    public FileFollowViewModel(IUiDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Die Zeilen haben sich geändert: <c>true</c>, wenn nur welche dazugekommen sind (<see cref="Added"/>), sonst ist alles neu zu zeichnen.</summary>
    public event Action<bool>? LinesChanged;

    /// <summary>Die Zeilen, die seit dem letzten Mal dazugekommen sind – schon gefiltert.</summary>
    public IReadOnlyList<FollowLine> Added { get; private set; } = [];

    /// <summary>Was mitgelesen wird: Datei und Server, für die Überschrift. Leer, solange nichts läuft.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFollowing { get; private set; }

    /// <summary>Angehalten: Was dazukommt, wird gesammelt, aber die Anzeige steht – zum Lesen.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>Nur Zeilen zeigen, die diesen Text enthalten (ohne Rücksicht auf Groß- und Kleinschreibung).</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>Zeilen hervorheben, die diesen Text enthalten.</summary>
    [ObservableProperty]
    public partial string Highlight { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Alle Zeilen, die gerade gezeigt werden – gefiltert.</summary>
    public IReadOnlyList<FollowLine> Shown => [.. _lines.Where(Matches).Select(Line)];

    /// <summary>Beginnt, eine Datei mitzulesen; was davor mitgelesen wurde, endet.</summary>
    public bool Start(IRemoteFileSystem files, string path, string server)
    {
        Stop();
        _lines.Clear();
        _partial = string.Empty;
        Title = L.Get("FollowTitle", Format.Visible(RemotePath.Name(path)), server);
        Status = string.Empty;
        LinesChanged?.Invoke(false);

        // Text und Ende kommen von irgendwo; an die Liste geht nur der Thread der Oberfläche.
        // Schon jetzt „läuft“: Der erste Text kann ankommen, bevor Follow zurückkehrt.
        IsFollowing = true;
        var following = files.Follow(
            path,
            text => _dispatcher.Post(() => Take(text)),
            reason => _dispatcher.Post(() => Ended(reason)));
        if (following is null)
        {
            IsFollowing = false;
            if (Status.Length == 0)
            {
                Status = L.Get("FollowUnavailable");
            }

            return false;
        }

        // Kam das Ende schon, während Follow lief, gibt es nichts mehr zu halten.
        if (IsFollowing)
        {
            _following = following;
        }
        else
        {
            following.Dispose();
        }

        return IsFollowing;
    }

    public void Stop()
    {
        _following?.Dispose();
        _following = null;
        if (IsFollowing)
        {
            IsFollowing = false;
            Status = L.Get("FollowStopped");
        }
    }

    public void Clear()
    {
        _lines.Clear();
        LinesChanged?.Invoke(false);
    }

    public void Dispose() => Stop();

    partial void OnFilterChanged(string value) => LinesChanged?.Invoke(false);

    partial void OnHighlightChanged(string value) => LinesChanged?.Invoke(false);

    // Wer weiterlaufen lässt, sieht alles, was inzwischen dazukam.
    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            LinesChanged?.Invoke(false);
        }
    }

    private void Take(string text)
    {
        if (!IsFollowing)
        {
            return;
        }

        // Eine Zeile zählt erst, wenn ihr Umbruch da ist; der Rest wartet.
        var parts = (_partial + text).Split('\n');
        _partial = parts[^1].Length > MaxLineLength ? parts[^1][..MaxLineLength] : parts[^1];
        var added = parts[..^1].Select(Clean).ToList();
        if (added.Count == 0)
        {
            return;
        }

        _lines.AddRange(added);
        var dropped = _lines.Count > MaxLines;
        if (dropped)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        if (IsPaused)
        {
            return;
        }

        Added = [.. added.Where(Matches).Select(Line)];
        LinesChanged?.Invoke(!dropped);
    }

    private void Ended(string? reason)
    {
        _following = null;
        IsFollowing = false;
        Status = reason is null ? L.Get("FollowStopped") : L.Get("FollowEnded", reason);
    }

    private bool Matches(string line) => Filter.Length == 0 || line.Contains(Filter, StringComparison.CurrentCultureIgnoreCase);

    private FollowLine Line(string line) => new(line, Highlight.Length > 0 && line.Contains(Highlight, StringComparison.CurrentCultureIgnoreCase));

    // Ohne Wagenrücklauf und Steuerzeichen (ein Tabulator wird zu Leerraum), auf eine erträgliche Länge gekürzt.
    private static string Clean(string line)
    {
        var text = line.TrimEnd('\r');
        if (text.Length > MaxLineLength)
        {
            text = text[..MaxLineLength] + " …";
        }

        return text.Any(char.IsControl) ? string.Concat(text.Select(character => character == '\t' ? "    " : char.IsControl(character) ? string.Empty : character.ToString())) : text;
    }
}
