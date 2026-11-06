using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class ServerDetailsView : UserControl
{
    public ServerDetailsView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;

        // Ob sich Protokolle lesen lassen, steht fest, wenn die Seite aufgeht.
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Details))
            {
                LogsLink.Visibility = Ui.VisibleIf(Shell.CanShowLogs);
            }
        };
    }

    public ShellViewModel Shell => App.Shell;

    /// <summary>Geht die Seite auf, bekommt der Knopf zum Schließen den Fokus – so führt Tab durch die Seite.</summary>
    public void FocusFirst() => DetailsCloseButton.Focus(FocusState.Programmatic);

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Shell.CloseDetails();

    private void OnLogsClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("server.logs");

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Shell.CloseDetails();
            e.Handled = true;
        }
    }

    // Ein Tunnel dieses Servers: an oder aus. Der Schalter zeigt danach wieder, was ist.
    private void OnTunnelToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            TunnelActions.Toggled(Shell, toggle);
        }
    }

    private void OnMeasureClicked(object sender, RoutedEventArgs e) => _ = Shell.Details?.MeasureLatencyAsync();

    private void OnLoadHistoryClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("server.loadHistory");

    private void OnTraceClicked(object sender, RoutedEventArgs e) => Shell.TraceRoute(Shell.Details);
}
