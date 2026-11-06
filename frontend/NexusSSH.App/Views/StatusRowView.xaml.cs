using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class StatusRowView : UserControl
{
    public StatusRowView()
    {
        InitializeComponent();
    }

    public ShellViewModel Shell => App.Shell;

    private void OnDetailsClicked(object sender, RoutedEventArgs e) => Shell.ShowDetails();

    private void OnTransfersClicked(object sender, RoutedEventArgs e) => Shell.ToggleTransfers();

    private void OnProblemsClicked(object sender, RoutedEventArgs e) => Shell.ToggleProblems();
}
