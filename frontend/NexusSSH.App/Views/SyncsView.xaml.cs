using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class SyncsView : UserControl
{
    public SyncsView() => InitializeComponent();

    public ShellViewModel Shell => App.Shell;

    // Die Vorschau kommt wie beim ersten Mal; die Verbindung sucht oder öffnet die Shell.
    private void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedSync sync)
        {
            _ = Shell.RunSavedSyncAsync(sync);
        }
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedSync sync)
        {
            _ = RenameAsync(sync);
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedSync sync)
        {
            _ = DeleteAsync(sync);
        }
    }

    private async Task RenameAsync(SavedSync sync)
    {
        // Der eigene Name gilt als frei: Sonst ließe sich die Schreibweise nicht ändern.
        bool IsFree(string name) => SavedSync.IsValidName(name) && (!Shell.HasSavedSync(name) || string.Equals(name.Trim(), sync.Name, StringComparison.CurrentCultureIgnoreCase));
        if (await App.Dialogs.AskTextAsync(L.Get("SyncRenameTitle"), L.Get("SyncNameLabel"), sync.Name, L.Get("Rename"), IsFree) is { } name)
        {
            Shell.RenameSync(sync, name);
        }
    }

    private async Task DeleteAsync(SavedSync sync)
    {
        if (await App.Dialogs.ConfirmAsync(L.Get("SyncDeleteTitle", sync.Name), L.Get("SyncDeleteText"), L.Get("Delete")))
        {
            Shell.DeleteSync(sync);
        }
    }
}
