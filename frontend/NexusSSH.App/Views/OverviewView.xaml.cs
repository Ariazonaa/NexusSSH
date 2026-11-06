using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Shell.Sessions.CollectionChanged += (_, _) => ShowSessions();
        ShowSessions();
    }

    public ShellViewModel Shell => App.Shell;

    private void ShowSessions()
    {
        var any = Shell.Sessions.Count > 0;
        EmptyText.Visibility = Ui.CollapsedIf(any);
        SessionsList.Visibility = Ui.VisibleIf(any);
    }

    private void OnSessionClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionViewModel session && Shell.Sessions.Contains(session))
        {
            Shell.ActiveSession = session;
        }
    }
}
