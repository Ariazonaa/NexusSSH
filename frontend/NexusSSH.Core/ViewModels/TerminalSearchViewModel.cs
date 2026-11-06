using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;

namespace NexusSSH.Core.ViewModels;

/// <summary>Auftrag an das Terminal: nach <see cref="Query"/> suchen und die Fundstelle zeigen.</summary>
/// <param name="Incremental">Beim Tippen: an der aktuellen Fundstelle bleiben, solange sie noch passt.</param>
public sealed record FindRequest(string Query, bool MatchCase, bool Backwards, bool Incremental);

/// <summary>Fundstelle der Suche über alle Sitzungen (Reiter „Suche“).</summary>
public sealed record SearchHit(string SessionId, string SessionTitle, int Row, string Text)
{
    public override string ToString() => $"{SessionTitle}: {Text}";
}

/// <summary>
/// Suche im Terminal (Spec §5): die Suchleiste über dem aktiven Terminal und die Trefferliste über alle Sitzungen
/// im unteren Bereich (Spec §7). Gesucht wird in der Terminal-Seite, denn dort liegt der Verlauf; hier steht nur
/// der Zustand. Die Ansicht reicht die Ereignisse an die Seite weiter und meldet deren Antworten zurück.
/// </summary>
public sealed partial class TerminalSearchViewModel : ObservableObject
{
    /// <summary>Die Suchleiste über dem Terminal ist offen.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    /// <summary>„3 von 12“ oder „Keine Treffer“; leer, solange nichts gesucht ist.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Suchtext der Trefferliste im unteren Bereich.</summary>
    [ObservableProperty]
    public partial string ListQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ListSummary { get; private set; } = string.Empty;

    public ObservableCollection<SearchHit> Hits { get; } = [];

    /// <summary>Im aktiven Terminal suchen.</summary>
    public event Action<FindRequest>? FindRequested;

    /// <summary>Die Hervorhebungen im aktiven Terminal zurücknehmen.</summary>
    public event Action? ClearRequested;

    /// <summary>In allen Terminals suchen: Suchtext, Groß-/Kleinschreibung beachten.</summary>
    public event Action<string, bool>? ListRequested;

    /// <summary>Zu einer Fundstelle springen: Fundstelle, Suchtext, Groß-/Kleinschreibung beachten.</summary>
    public event Action<SearchHit, string, bool>? RevealRequested;

    public void Open() => IsOpen = true;

    public void Close()
    {
        IsOpen = false;
        Summary = string.Empty;
        ClearRequested?.Invoke();
    }

    public void FindNext() => Find(backwards: false, incremental: false);

    public void FindPrevious() => Find(backwards: true, incremental: false);

    /// <summary>Die Seite meldet den Stand: Nummer der gezeigten Fundstelle (ab 0, −1 = unbekannt) und ihre Zahl.</summary>
    public void ReportFound(int index, int count)
    {
        if (Query.Length == 0)
        {
            Summary = string.Empty;
        }
        else if (count == 0)
        {
            Summary = L.Get("SearchNoMatches");
        }
        else
        {
            Summary = index >= 0 ? L.Get("SearchPosition", index + 1, count) : L.Get("SearchCount", count);
        }
    }

    /// <summary>Startet die Suche über alle Sitzungen. Ein leerer Suchtext leert die Liste.</summary>
    public void SearchAll()
    {
        var query = ListQuery.Trim();
        if (query.Length == 0)
        {
            Hits.Clear();
            ListSummary = string.Empty;
            return;
        }

        ListRequested?.Invoke(query, MatchCase);
    }

    /// <summary>Die Seite liefert die Fundstellen zu <paramref name="query"/>. Antworten auf eine überholte Suche fallen weg.</summary>
    public void ReportHits(string query, IReadOnlyList<SearchHit> hits, bool more)
    {
        if (query != ListQuery.Trim())
        {
            return;
        }

        Hits.Clear();
        foreach (var hit in hits)
        {
            Hits.Add(hit);
        }

        ListSummary = hits.Count == 0
            ? L.Get("SearchNoMatches")
            : more
                ? L.Get("SearchHitsMore", hits.Count)
                : L.Get("SearchHits", hits.Count, hits.Select(hit => hit.SessionId).Distinct().Count());
    }

    public void Reveal(SearchHit hit) => RevealRequested?.Invoke(hit, ListQuery.Trim(), MatchCase);

    partial void OnQueryChanged(string value) => Find(backwards: false, incremental: true);

    // Die Einstellung gilt für beide Suchen: die Leiste über dem Terminal und die Liste über alle Sitzungen.
    partial void OnMatchCaseChanged(bool value)
    {
        Find(backwards: false, incremental: true);
        if (ListQuery.Trim().Length > 0)
        {
            SearchAll();
        }
    }

    private void Find(bool backwards, bool incremental)
    {
        if (Query.Length == 0)
        {
            Summary = string.Empty;
            ClearRequested?.Invoke();
            return;
        }

        FindRequested?.Invoke(new FindRequest(Query, MatchCase, backwards, incremental));
    }
}
