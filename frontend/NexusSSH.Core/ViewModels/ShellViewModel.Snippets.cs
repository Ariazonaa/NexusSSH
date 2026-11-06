using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Die Spalte rechts (Entwurf A, statt der Server-Info): die gespeicherten Befehle, mit einem Klick im aktiven Terminal
// ausgeführt (RunSnippet – nichts Neues im Kern). Oben ein Suchfeld, das nach Name und Text filtert; ohne verbundene
// Sitzung mit Shell sind die Einträge ausgegraut, und ein Satz sagt, warum. Bearbeitet wird auf der Seite „Snippets“
// der Startseite – dem Editor, den es schon gibt (SnippetsEditorViewModel).
public sealed partial class ShellViewModel
{
    /// <summary>Was im Suchfeld der Spalte steht: Wörter, die im Namen oder im Befehl vorkommen müssen.</summary>
    [ObservableProperty]
    public partial string SnippetFilter { get; set; } = string.Empty;

    /// <summary>
    /// Die Befehle, die zur Suche passen und zu sehen sind (nicht in einem eingeklappten Ordner), so geordnet wie in
    /// der Spalte; wird bei jeder Änderung neu gesetzt.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<Snippet> FilteredSnippets { get; private set; } = BindableList.Of<Snippet>([]);

    /// <summary>
    /// Die Zeilen der Spalte: die Befehle ohne Ordner, dann je Ordner, in dem etwas zur Suche passt, seine
    /// Überschrift und – ausgeklappt – seine Befehle.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSnippetMatch))]
    public partial IReadOnlyList<SnippetItem> SnippetItems { get; private set; } = BindableList.Of<SnippetItem>([]);

    /// <summary>Es gibt noch keinen gespeicherten Befehl.</summary>
    public bool HasNoSnippets => Snippets.Count == 0;

    /// <summary>Es gibt Befehle, aber keiner passt zur Suche.</summary>
    public bool HasNoSnippetMatch => Snippets.Count > 0 && SnippetItems.Count == 0;

    /// <summary>Klappt einen Ordner der Spalte ein oder aus; gemerkt in den Einstellungen dieses Rechners.</summary>
    public void ToggleSnippetFolder(string folder)
    {
        if (folder.Length == 0)
        {
            return;
        }

        if (!Settings.CollapsedSnippetFolders.Remove(folder))
        {
            Settings.CollapsedSnippetFolders.Add(folder);
        }

        FilterSnippets();
    }

    /// <summary>Ob ein Klick einen Befehl ausführen kann: Die aktive Sitzung ist verbunden und hat eine Shell.</summary>
    public bool CanRunSnippets => ActiveSession is { State: ConnectionState.Connected, IsFilesOnly: false };

    /// <summary>Das Gegenteil – dann sagt ein Satz oben in der Spalte, warum die Einträge grau sind.</summary>
    public bool CannotRunSnippets => !CanRunSnippets;

    private int? _snippetToEdit;

    /// <summary>
    /// Ein Befehl soll bearbeitet werden (<see cref="TakeSnippetToEdit"/>). Die Seite „Snippets“ der Startseite wählt
    /// ihn dann – auch, wenn sie erst danach entsteht: Sie fragt beim Aufbau selbst nach.
    /// </summary>
    public event Action? SnippetEditRequested;

    /// <summary>
    /// Welcher Befehl bearbeitet werden soll: die Nummer in <see cref="Snippets"/>, −1 für einen neuen, <c>null</c>
    /// für keinen. Gilt einmal – danach ist die Bitte erledigt.
    /// </summary>
    public int? TakeSnippetToEdit()
    {
        var index = _snippetToEdit;
        _snippetToEdit = null;
        return index;
    }

    /// <summary>Enter im Suchfeld: führt den ersten Treffer aus.</summary>
    public bool RunFirstSnippet() => FilteredSnippets is [var first, ..] && RunSnippet(first);

    /// <summary>
    /// Öffnet den Editor der Befehle (die Seite „Snippets“ der Startseite) mit diesem Befehl – oder, ohne einen, mit
    /// einem neuen. Die Sitzungen laufen dabei weiter.
    /// </summary>
    public void EditSnippet(Snippet? snippet)
    {
        _snippetToEdit = snippet is null ? -1 : Settings.Snippets.IndexOf(snippet);
        ShowHome(HomePage.Snippets);
        SnippetEditRequested?.Invoke();
    }

    partial void OnSnippetFilterChanged(string value) => FilterSnippets();

    private void FilterSnippets()
    {
        var words = SnippetFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = SnippetFolders.Ordered(Settings.Snippets.Where(snippet => words.All(word =>
            snippet.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
            || snippet.Text.Contains(word, StringComparison.CurrentCultureIgnoreCase))), snippet => snippet.Folder);

        var items = new List<SnippetItem>();
        foreach (var group in matches.GroupBy(snippet => snippet.Folder))
        {
            var collapsed = group.Key.Length > 0 && Settings.CollapsedSnippetFolders.Contains(group.Key);
            if (group.Key.Length > 0)
            {
                items.Add(new SnippetItem(null, group.Key, collapsed, group.Count()));
            }

            if (!collapsed)
            {
                items.AddRange(group.Select(snippet => new SnippetItem(snippet, snippet.Folder)));
            }
        }

        FilteredSnippets = BindableList.Of(items.Where(item => item.IsCommand).Select(item => item.Snippet!));
        SnippetItems = BindableList.Of(items);
        OnPropertyChanged(nameof(HasNoSnippets));
    }

    // Ob sich Befehle ausführen lassen, hängt am Zustand der aktiven Sitzung.
    private void WatchSnippets(SessionViewModel session) => session.PropertyChanged += OnSnippetSource;

    private void OnSnippetSource(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveSession) && e.PropertyName is nameof(SessionViewModel.State) or nameof(SessionViewModel.IsFilesOnly))
        {
            NotifySnippetsRunnable();
        }
    }

    private void NotifySnippetsRunnable()
    {
        OnPropertyChanged(nameof(CanRunSnippets));
        OnPropertyChanged(nameof(CannotRunSnippets));
    }
}
