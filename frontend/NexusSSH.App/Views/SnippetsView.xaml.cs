using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class SnippetsView : UserControl
{
    // Die Seite speichert selbst – die Meldung der Shell darüber soll das Formular nicht neu aufbauen.
    private bool _saving;

    // Solange der Editor Zeilen umstellt: Die Liste meldet dabei eine leere Auswahl, die nicht gemeint ist.
    private bool _arranging;

    // Die Zeile, deren Ordner das Feld gerade zeigt – beim Verlassen des Feldes kann schon eine andere gewählt sein.
    private SnippetRow? _folderRow;

    public SnippetsView()
    {
        Editor = NewEditor();
        InitializeComponent();
        SnippetList.SelectedItem = Editor.Selected;
        ShowFolder();

        // Ändert jemand anderes die Befehle (Import), zeigt die Seite den neuen Stand.
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Snippets) && !_saving)
            {
                Editor = NewEditor();
                Bindings.Update();
                SnippetList.SelectedItem = Editor.Selected;
                ShowFolder();
            }
        };

        // Aus der Spalte rechts (Stift oder „+“): diesen Befehl wählen bzw. einen neuen anlegen.
        Shell.SnippetEditRequested += () => EditRequested(Shell.TakeSnippetToEdit());
        EditRequested(Shell.TakeSnippetToEdit());
    }

    private void EditRequested(int? index)
    {
        if (index is not { } wanted)
        {
            return;
        }

        if (wanted < 0)
        {
            OnAddClicked(this, new RoutedEventArgs());
        }
        else if (wanted < Shell.Snippets.Count
            && Editor.Rows.FirstOrDefault(row => row.Name == Shell.Snippets[wanted].Name && row.Text == Shell.Snippets[wanted].Text) is { } row)
        {
            // Die Zeilen stehen nach Ordnern, die Liste der Shell so, wie sie gespeichert ist – gesucht wird der Befehl.
            SnippetList.SelectedItem = row;
            DispatcherQueue.TryEnqueue(() => NameBox.Focus(FocusState.Programmatic));
        }
    }

    public ShellViewModel Shell => App.Shell;

    public SnippetsEditorViewModel Editor { get; private set; }

    // Jede Änderung gilt sofort; halbfertige Zeilen (ohne Namen oder Text) lässt der Editor beim Speichern aus.
    private SnippetsEditorViewModel NewEditor()
    {
        var editor = new SnippetsEditorViewModel(Shell.Snippets);
        editor.Changed += () =>
        {
            _saving = true;
            try
            {
                Shell.SetSnippets(editor.ToSnippets());
            }
            finally
            {
                _saving = false;
            }
        };
        return editor;
    }

    // SelectedItem ist object, die Auswahl im ViewModel typisiert – deshalb von Hand, in beide Richtungen.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_arranging && !ReferenceEquals(SnippetList.SelectedItem, Editor.Selected))
        {
            Editor.Selected = SnippetList.SelectedItem as SnippetRow;
            ShowFolder();
        }
    }

    // Umstellen nimmt Zeilen aus der Liste und setzt sie wieder ein; danach ist dieselbe Zeile gewählt wie vorher.
    private void Arrange(Action change)
    {
        var keep = Editor.Selected;
        _arranging = true;
        try
        {
            change();
        }
        finally
        {
            _arranging = false;
        }

        Editor.Selected = keep;
        SnippetList.SelectedItem = keep;
        if (keep is not null)
        {
            SnippetList.ScrollIntoView(keep);
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        CommitFolder();
        SnippetRow? added = null;
        Arrange(() => added = Editor.Add());
        SnippetList.SelectedItem = added;
        ShowFolder();
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        Editor.RemoveSelected();
        SnippetList.SelectedItem = Editor.Selected;
        ShowFolder();
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e) => Arrange(() => Editor.MoveSelected(-1));

    private void OnMoveDownClicked(object sender, RoutedEventArgs e) => Arrange(() => Editor.MoveSelected(1));

    // Alt+↑/↓ in der Liste verschiebt wie die Knöpfe; die Pfeile allein wählen weiter.
    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            Arrange(() => Editor.MoveSelected(e.Key == VirtualKey.Up ? -1 : 1));
            (SnippetList.ContainerFromItem(Editor.Selected) as ListViewItem)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    // Das Feld zeigt den Ordner der gewählten Zeile.
    private void ShowFolder()
    {
        _folderRow = Editor.Selected;
        FolderBox.Text = _folderRow?.Folder ?? string.Empty;
    }

    private void CommitFolder()
    {
        if (_folderRow is { } row && FolderBox.Text.Trim() != row.Folder)
        {
            Arrange(() => Editor.SetFolder(row, FolderBox.Text));
            FolderBox.Text = row.Folder;
        }
    }

    private void OnFolderTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = Editor.FolderSuggestions(sender.Text);
        }
    }

    private void OnFolderSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            sender.Text = chosen;
        }

        CommitFolder();
    }

    // Nur wenn der Fokus das Feld wirklich verlässt – nicht, wenn er darin wandert.
    private void OnFolderLostFocus(object sender, RoutedEventArgs e)
    {
        for (var at = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; at is not null; at = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, FolderBox))
            {
                return;
            }
        }

        CommitFolder();
    }
}
