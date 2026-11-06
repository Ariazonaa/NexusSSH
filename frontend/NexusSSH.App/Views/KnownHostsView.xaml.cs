using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class KnownHostsView : UserControl
{
    public KnownHostsView()
    {
        InitializeComponent();

        // Gelesen wird die Liste, wenn die Seite aufgeht (ShellViewModel.HomePage) – hier nur gezeigt.
        Shell.KnownHosts.CollectionChanged += (_, _) => ShowKnownHosts();
        ShowKnownHosts();
    }

    public ShellViewModel Shell => App.Shell;

    private void ShowKnownHosts()
    {
        var any = Shell.KnownHosts.Count > 0;
        NoKnownHostsText.Visibility = Ui.CollapsedIf(any);
        KnownHostsList.Visibility = Ui.VisibleIf(any);
    }

    // Vertrauen für viele Server auf einmal: nur über diesen Dialog, in dem steht, was es bedeutet.
    private async void OnAddAuthorityClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Dialogs.AddAuthorityAsync(Shell.TrustAuthority);
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error);
        }
    }

    private void OnForgetHostClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is KnownHost host)
        {
            Shell.ForgetHost(host);
        }
    }
}
