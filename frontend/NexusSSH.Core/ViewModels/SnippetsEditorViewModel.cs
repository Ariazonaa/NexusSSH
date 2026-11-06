using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

/// <summary>Ein gespeicherter Befehl, während er bearbeitet wird.</summary>
public sealed partial class SnippetRow(string name, string text, bool run, string folder = "") : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial string Text { get; set; } = text;

    /// <summary>Gleich mit Enter abschicken statt nur eintippen.</summary>
    [ObservableProperty]
    public partial bool Run { get; set; } = run;

    /// <summary>Der Ordner; leer heißt ohne. Geändert wird er über <see cref="SnippetsEditorViewModel.SetFolder"/>.</summary>
    [ObservableProperty]
    public partial string Folder { get; private set; } = folder;

    /// <summary>Die Überschrift über dieser Zeile: der Ordner, wenn sie seine erste ist – sonst leer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeading))]
    public partial string Heading { get; private set; } = string.Empty;

    public bool HasHeading => Heading.Length > 0;

    internal void Place(string folder, string heading)
    {
        Folder = folder;
        Heading = heading;
    }

    /// <summary>Der Name – die Liste liest ihn Screenreadern als Bezeichnung der Zeile vor.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Formular für die gespeicherten Befehle (Snippets): anlegen, ändern, löschen, einem Ordner zuordnen und innerhalb
/// seines Ordners verschieben. Die Zeilen stehen immer so, wie die Spalte sie zeigt (<see cref="SnippetFolders"/>):
/// erst ohne Ordner, dann Ordner für Ordner. Übernommen wird erst mit <see cref="ToSnippets"/> – ein abgebrochener
/// Dialog ändert nichts.
/// </summary>
public sealed partial class SnippetsEditorViewModel : ObservableObject
{
    public SnippetsEditorViewModel(IEnumerable<Snippet> snippets)
    {
        Rows = [.. SnippetFolders.Ordered(snippets, snippet => snippet.Folder).Select(snippet => new SnippetRow(snippet.Name, snippet.Text, snippet.Run, snippet.Folder))];
        Selected = Rows.FirstOrDefault();
        UpdateHeadings();

        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        Rows.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<SnippetRow>() ?? [])
            {
                row.PropertyChanged -= OnRowChanged;
                row.PropertyChanged += OnRowChanged;
            }
        };
    }

    /// <summary>
    /// Etwas an den Befehlen hat sich geändert: einer kam dazu, fiel weg, wurde bearbeitet oder verschoben. Wer gleich
    /// speichert statt auf einen Knopf zu warten (die Seite der Startseite), hört hier zu.
    /// </summary>
    public event Action? Changed;

    // Die Überschrift ist Anzeige, kein Inhalt – ihretwegen wird nicht gespeichert.
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SnippetRow.Heading) or nameof(SnippetRow.HasHeading)))
        {
            Changed?.Invoke();
        }
    }

    public ObservableCollection<SnippetRow> Rows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanMoveUp), nameof(CanMoveDown))]
    public partial SnippetRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>Der gewählte Befehl hat in seinem Ordner einen Vorgänger.</summary>
    public bool CanMoveUp => Neighbor(-1) >= 0;

    /// <summary>Der gewählte Befehl hat in seinem Ordner einen Nachfolger.</summary>
    public bool CanMoveDown => Neighbor(1) >= 0;

    /// <summary>Die Ordner, die es gibt – als Vorschläge für das Feld „Ordner“; mit Text nur die, die ihn enthalten.</summary>
    public List<string> FolderSuggestions(string typed)
        => [.. Rows.Select(row => row.Folder).Where(folder => folder.Length > 0).Distinct()
            .Where(folder => folder.Contains(typed.Trim(), StringComparison.CurrentCultureIgnoreCase))];

    /// <summary>Legt einen neuen Befehl im Ordner des gewählten an (am Ende dieses Ordners) und wählt ihn.</summary>
    public SnippetRow Add()
    {
        var row = new SnippetRow(L.Get("SnippetNewName"), string.Empty, run: true, Selected?.Folder ?? string.Empty);
        Rows.Add(row);
        Regroup();
        Selected = row;
        Changed?.Invoke();
        return row;
    }

    /// <summary>Entfernt den gewählten Befehl; danach ist sein Nachbar gewählt.</summary>
    public void RemoveSelected()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var index = Rows.IndexOf(row);
        Rows.RemoveAt(index);
        UpdateHeadings();
        Selected = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        Changed?.Invoke();
    }

    /// <summary>
    /// Legt den Befehl in einen Ordner (leer: ohne). Er kommt ans Ende dieses Ordners; einen neuen Ordner gibt es
    /// danach als letzten.
    /// </summary>
    public void SetFolder(SnippetRow row, string folder)
    {
        var clean = SnippetFolders.Clean(folder);
        if (clean == row.Folder || !Rows.Contains(row))
        {
            return;
        }

        // Ans Ende der Liste, dann ordnen: So steht er im neuen Ordner als letzter.
        row.Place(clean, row.Heading);
        Rows.Move(Rows.IndexOf(row), Rows.Count - 1);
        Regroup();
        NotifyMoves();
        Changed?.Invoke();
    }

    /// <summary>Verschiebt den gewählten Befehl innerhalb seines Ordners um eine Stelle (−1 hoch, +1 runter).</summary>
    public bool MoveSelected(int step)
    {
        var target = Neighbor(step);
        if (Selected is not { } row || target < 0)
        {
            return false;
        }

        Rows.Move(Rows.IndexOf(row), target);
        UpdateHeadings();
        NotifyMoves();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Die Befehle aus den Eingaben. Zeilen ohne Namen oder ohne Text entfallen.</summary>
    public IReadOnlyList<Snippet> ToSnippets()
        => [.. Rows
            .Where(row => row.Name.Trim().Length > 0 && row.Text.Trim().Length > 0)
            .Select(row => new Snippet(row.Name.Trim(), row.Text, row.Run, row.Folder))];

    // Die Stelle des Nachbarn im selben Ordner, −1 ohne. Ordner stehen am Stück, der Nachbar ist also der nächste.
    private int Neighbor(int step)
    {
        if (Selected is not { } row || step is not (-1 or 1))
        {
            return -1;
        }

        var index = Rows.IndexOf(row) + step;
        return index >= 0 && index < Rows.Count && Rows[index].Folder == row.Folder ? index : -1;
    }

    // Bringt die Zeilen in die Ordnung der Spalte, ohne die Reihenfolge innerhalb eines Ordners zu ändern.
    private void Regroup()
    {
        var wanted = SnippetFolders.Ordered(Rows, row => row.Folder);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Rows.IndexOf(wanted[i]);
            if (at != i)
            {
                Rows.Move(at, i);
            }
        }

        UpdateHeadings();
    }

    private void UpdateHeadings()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            var folder = Rows[i].Folder;
            Rows[i].Place(folder, folder.Length > 0 && (i == 0 || Rows[i - 1].Folder != folder) ? folder : string.Empty);
        }
    }

    private void NotifyMoves()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }
}
