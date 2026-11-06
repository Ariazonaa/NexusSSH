using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Eine Zeile der Vorschau im Dialog „Mehrere umbenennen“.</summary>
public sealed record RenamePreview(string From, string To, string Note, bool HasProblem)
{
    public string FromShown => Format.Visible(From);

    public string ToShown => Format.Visible(To);

    public override string ToString() => Note.Length > 0 ? $"{FromShown} → {ToShown}, {Note}" : $"{FromShown} → {ToShown}";
}

/// <summary>
/// Mehrere Einträge nach einer Regel umbenennen: Suchen/Ersetzen und ein Muster mit <c>{name}</c>, <c>{ext}</c>
/// und <c>{n}</c>. Die Vorschau rechnet bei jeder Änderung neu; umbenannt wird erst nach „Umbenennen“.
/// </summary>
public sealed partial class BatchRenameViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _names;
    private readonly IReadOnlyList<string> _others;
    private IReadOnlyList<RenameLine> _lines = [];

    /// <param name="names">Die gewählten Einträge, in der Reihenfolge der Liste – so werden sie nummeriert.</param>
    /// <param name="others">Alle Namen im Verzeichnis.</param>
    public BatchRenameViewModel(IReadOnlyList<string> names, IReadOnlyList<string> others)
    {
        (_names, _others) = (names, others);
        Rebuild();
    }

    public ObservableCollection<RenamePreview> Lines { get; } = [];

    [ObservableProperty]
    public partial string Find { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Replace { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    /// <summary>Leer: der Name bleibt, wie er nach dem Ersetzen ist.</summary>
    [ObservableProperty]
    public partial string Pattern { get; set; } = string.Empty;

    /// <summary>double, weil die NumberBox so bindet; NaN heißt „leer“ und zählt als 1.</summary>
    [ObservableProperty]
    public partial double Start { get; set; } = 1;

    [ObservableProperty]
    public partial double Digits { get; set; } = 1;

    [ObservableProperty]
    public partial bool CanApply { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Was umzubenennen ist – nur die Einträge, deren Name sich ändert.</summary>
    public IReadOnlyList<(string From, string To)> Changes => [.. _lines.Where(line => line.Changes).Select(line => (line.From, line.To))];

    partial void OnFindChanged(string value) => Rebuild();

    partial void OnReplaceChanged(string value) => Rebuild();

    partial void OnIgnoreCaseChanged(bool value) => Rebuild();

    partial void OnPatternChanged(string value) => Rebuild();

    partial void OnStartChanged(double value) => Rebuild();

    partial void OnDigitsChanged(double value) => Rebuild();

    private void Rebuild()
    {
        var rule = new RenameRule(Find, Replace, IgnoreCase, Pattern.Trim(), Whole(Start, 1), Whole(Digits, 1));
        _lines = RenamePlan.Build(_names, rule, _others);
        Lines.Clear();
        foreach (var line in _lines)
        {
            Lines.Add(new RenamePreview(line.From, line.To, Note(line), line.Problem != RenameProblem.None));
        }

        CanApply = RenamePlan.CanApply(_lines);
        var changing = _lines.Count(line => line.Changes);
        var problems = _lines.Count(line => line.Problem != RenameProblem.None);
        Summary = problems > 0 ? L.Get("BatchRenameProblems", problems) : L.Get("BatchRenameSummary", changing, _lines.Count);
    }

    private static int Whole(double value, int fallback)
        => double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, 1_000_000) : fallback;

    private static string Note(RenameLine line) => line.Problem switch
    {
        RenameProblem.Invalid => L.Get("BatchRenameInvalid"),
        RenameProblem.Twice => L.Get("BatchRenameTwice"),
        RenameProblem.Exists => L.Get("BatchRenameExists"),
        _ => line.Changes ? string.Empty : L.Get("BatchRenameSame"),
    };
}
