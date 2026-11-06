using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class IdentitiesView : UserControl
{
    public IdentitiesView() => InitializeComponent();

    public ShellViewModel Shell => App.Shell;

    private void OnAddIdentityClicked(object sender, RoutedEventArgs e) => _ = EditIdentityAsync(null);

    private void OnEditIdentityClicked(object sender, RoutedEventArgs e) => _ = EditIdentityAsync((sender as FrameworkElement)?.Tag as Identity);

    private void OnDeleteIdentityClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Identity identity)
        {
            _ = DeleteIdentityAsync(identity);
        }
    }

    private async Task EditIdentityAsync(Identity? identity)
    {
        if (await App.Dialogs.EditIdentityAsync(identity) is { } edited)
        {
            Shell.SaveIdentity(edited);
        }
    }

    // Gefragt wird nur, wenn Profile daran hängen – sie behalten dann Benutzer und Schlüssel als eigene Angaben.
    private async Task DeleteIdentityAsync(Identity identity)
    {
        var used = Shell.ProfilesUsing(identity);
        if (used == 0 || await App.Dialogs.ConfirmAsync(L.Get("IdentityDeleteTitle", identity.Name), L.Get("IdentityDeleteText", used), L.Get("Delete")))
        {
            Shell.DeleteIdentity(identity);
        }
    }
}
