using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class SnippetsPaneView : UserControl
{
    public SnippetsPaneView()
    {
        InitializeComponent();
    }

    public ShellViewModel Shell => App.Shell;

    /// <summary>Für den Wechsel von Bereich zu Bereich (Strg+F6): der Fokus kommt ins Suchfeld.</summary>
    public bool FocusSearch() => SnippetSearchBox.Focus(FocusState.Keyboard);

    // Ein Klick auf eine Überschrift klappt ihren Ordner ein oder aus; einer auf einen Befehl führt ihn aus – danach
    // gehört der Fokus wieder dem Terminal, in dem der Befehl steht.
    private void OnSnippetClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SnippetItem { IsHeading: true } heading)
        {
            // Die Liste wird dabei ersetzt; wer mit der Tastatur kam, bleibt auf der Überschrift.
            var keyboard = SnippetList.ContainerFromItem(heading) is ListViewItem { FocusState: FocusState.Keyboard };
            Shell.ToggleSnippetFolder(heading.Folder);
            if (keyboard)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var again = Shell.SnippetItems.FirstOrDefault(item => item.IsHeading && item.Folder == heading.Folder);
                    (SnippetList.ContainerFromItem(again) as ListViewItem)?.Focus(FocusState.Keyboard);
                });
            }
        }
        else if (e.ClickedItem is SnippetItem { Snippet: { } snippet } && Shell.RunSnippet(snippet))
        {
            Shell.FocusTerminal();
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (Shell.RunFirstSnippet())
                {
                    Shell.FocusTerminal();
                }

                e.Handled = true;
                break;

            case VirtualKey.Escape when Shell.HasActiveSession:
                Shell.FocusTerminal();
                e.Handled = true;
                break;
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => Shell.EditSnippet(null);

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SnippetItem { Snippet: { } snippet })
        {
            Shell.EditSnippet(snippet);
        }
    }

    // Der Stift zeigt sich nur beim Überfahren der Zeile, wie im Bild.
    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => ShowPen(sender, true);

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => ShowPen(sender, false);

    private static void ShowPen(object row, bool shown)
    {
        if (row is Grid { Children: [_, Button pen] })
        {
            pen.Opacity = shown ? 1 : 0;
        }
    }
}
