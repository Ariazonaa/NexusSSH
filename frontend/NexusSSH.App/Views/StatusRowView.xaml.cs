using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class StatusRowView : UserControl
{
    public StatusRowView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Shell.Transfers.Badge.PropertyChanged += OnBadgeChanged;
            ShowBadge(announce: false);
        };
        Unloaded += (_, _) => Shell.Transfers.Badge.PropertyChanged -= OnBadgeChanged;
    }

    public ShellViewModel Shell => App.Shell;

    private void OnDetailsClicked(object sender, RoutedEventArgs e) => Shell.ShowDetails();

    private void OnTransfersClicked(object sender, RoutedEventArgs e) => Shell.ToggleTransfers();

    private void OnProblemsClicked(object sender, RoutedEventArgs e) => Shell.ToggleProblems();

    // Farben nach dem Zustand der Pille; eine Sprachausgabe hört nur den Wechsel (Beginn, fertig, gescheitert),
    // nicht jedes Prozent.
    private void OnBadgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferBadge.Kind))
        {
            ShowBadge(announce: true);
        }
    }

    private void ShowBadge(bool announce)
    {
        VisualStateManager.GoToState(this, Shell.Transfers.Badge.Kind.ToString(), true);

        // Erst danach: Der Zustand wechselt vor dem Text, die Ansage soll den neuen lesen.
        if (announce)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FrameworkElementAutomationPeer.FromElement(TransfersPill) is { } peer)
                {
                    peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            });
        }
    }
}
