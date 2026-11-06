using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace NexusSSH.App.Views;

public sealed partial class BottomPanelView : UserControl
{
    private readonly DispatcherQueueTimer _searchDelay;

    public BottomPanelView()
    {
        InitializeComponent();
        _searchDelay = DispatcherQueue.CreateTimer();
        _searchDelay.Interval = TimeSpan.FromMilliseconds(250);
        _searchDelay.IsRepeating = false;
        _searchDelay.Tick += (_, _) => SearchAll();
        ShowSelected();
        Shell.Output.CollectionChanged += OnOutputChanged;
        Shell.Problems.CollectionChanged += (_, _) => ShowProblems();
        Shell.History.CollectionChanged += (_, _) => ShowHistory();
        Shell.Transfers.Synced += ShowTransferButtons;
        Shell.Follow.LinesChanged += ShowFollowed;
        Shell.Follow.PropertyChanged += (_, e) =>
        {
            // Ein neues Mitlesen holt seinen Reiter nach vorn.
            if (e.PropertyName == nameof(FileFollowViewModel.Title))
            {
                Tabs.SelectedItem = FollowTab;
            }
        };

        // Klick in der Zeile unten: Der Bereich ist dann schon eingeblendet und soll die Transfers zeigen.
        Shell.UiRequested += request =>
        {
            if (request == UiRequest.ShowTransfers)
            {
                Tabs.SelectedItem = TransfersTab;
            }
            else if (request == UiRequest.ShowProblems)
            {
                Tabs.SelectedItem = ProblemsTab;
            }
        };
    }

    public ShellViewModel Shell => App.Shell;

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowSelected();

    // --- Mitlesen ---

    private Microsoft.UI.Xaml.Documents.Paragraph? _followed;

    private void OnFollowClearClicked(object sender, RoutedEventArgs e) => Shell.Follow.Clear();

    private void OnFollowStopClicked(object sender, RoutedEventArgs e) => Shell.Follow.Stop();

    // Neue Zeilen werden angehängt; nur wenn sich mehr geändert hat (Filter, Kürzen, Leeren), wird neu aufgebaut.
    // Jede Zeile ist ein Run aus reinem Text – was der Server schreibt, wird nie als Markup gelesen.
    private void ShowFollowed(bool appended)
    {
        // Wer am Ende steht, bleibt dort; wer hochgeblättert hat, liest ungestört weiter.
        var atEnd = FollowScroll.VerticalOffset >= FollowScroll.ScrollableHeight - 24;
        if (!appended || _followed is null)
        {
            _followed = new Microsoft.UI.Xaml.Documents.Paragraph();
            FollowText.Blocks.Clear();
            FollowText.Blocks.Add(_followed);
            Append(Shell.Follow.Shown);
        }
        else
        {
            Append(Shell.Follow.Added);
        }

        if (atEnd)
        {
            FollowScroll.UpdateLayout();
            FollowScroll.ChangeView(null, FollowScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void Append(IReadOnlyList<FollowLine> lines)
    {
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        foreach (var line in lines)
        {
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = line.Text + "\n" };
            if (line.Highlighted)
            {
                run.Foreground = accent;
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            _followed!.Inlines.Add(run);
        }
    }

    private void ShowSelected()
    {
        var selected = Tabs.SelectedItem;
        OutputPane.Visibility = Ui.VisibleIf(selected == OutputTab);
        TransfersPane.Visibility = Ui.VisibleIf(selected == TransfersTab);
        ProblemsPane.Visibility = Ui.VisibleIf(selected == ProblemsTab);
        SearchPane.Visibility = Ui.VisibleIf(selected == SearchTab);
        HistoryPane.Visibility = Ui.VisibleIf(selected == HistoryTab);
        FollowPane.Visibility = Ui.VisibleIf(selected == FollowTab);

        // Die Zeile unten schaltet danach um: „Transfers n“ blendet aus, was die Transfers schon zeigt.
        Shell.BottomTab = selected == TransfersTab ? BottomTab.Transfers
            : selected == ProblemsTab ? BottomTab.Problems
            : selected == SearchTab ? BottomTab.Search
            : selected == HistoryTab ? BottomTab.History
            : selected == FollowTab ? BottomTab.Follow
            : BottomTab.Output;
        ShowProblems();
        ShowHistory();

        // Der Knopf rechts räumt auf, was der Reiter zeigt.
        ClearButton.Visibility = Ui.VisibleIf(selected == TransfersTab || selected == ProblemsTab || selected == HistoryTab);
        var label = ClearLabel();
        AutomationProperties.SetName(ClearButton, label);
        ToolTipService.SetToolTip(ClearButton, label);
        ShowTransferButtons();
    }

    // „Alle anhalten“ und „Alle fortsetzen“ stehen nur da, wenn es dafür etwas gibt.
    private void ShowTransferButtons()
    {
        var transfers = Tabs.SelectedItem == TransfersTab;
        PauseAllButton.Visibility = Ui.VisibleIf(transfers && Shell.Transfers.CanPauseAll);
        ResumeAllButton.Visibility = Ui.VisibleIf(transfers && Shell.Transfers.CanResumeAll);
    }

    private void OnPauseAllClicked(object sender, RoutedEventArgs e) => Shell.Transfers.PauseAll();

    private void OnResumeAllClicked(object sender, RoutedEventArgs e) => Shell.Transfers.ResumeAll();

    // Je Zeile: anhalten, in der Warteschlange umstellen, abbrechen. Das Menü entsteht beim Klick aus dem, was
    // der Transfer gerade kann.
    private void OnTransferMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TransferItemViewModel transfer } anchor)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, bool enabled, Action run)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        Add(L.Get("TransferPause"), "", transfer.CanCancel, () => Shell.Transfers.Pause(transfer));
        if (transfer.IsWaiting)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(L.Get("TransferMoveFirst"), "", transfer.CanMoveUp, () => Shell.Transfers.Move(transfer, NexusSSH.Core.Services.QueueMove.First));
            Add(L.Get("TransferMoveUp"), "", transfer.CanMoveUp, () => Shell.Transfers.Move(transfer, NexusSSH.Core.Services.QueueMove.Up));
            Add(L.Get("TransferMoveDown"), "", transfer.CanMoveDown, () => Shell.Transfers.Move(transfer, NexusSSH.Core.Services.QueueMove.Down));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.Get("CancelTransfer"), "", transfer.CanCancel, () => Shell.Transfers.Cancel(transfer));
        menu.ShowAt(anchor);
    }

    private string ClearLabel()
    {
        if (Tabs.SelectedItem == ProblemsTab)
        {
            return L.Get("ClearProblems");
        }

        return Tabs.SelectedItem == HistoryTab ? L.Get("ClearHistory") : L.Get("ClearFinished");
    }

    private void ShowHistory()
    {
        var any = Shell.History.Count > 0;
        NoHistoryText.Visibility = Ui.CollapsedIf(any);
        HistoryList.Visibility = Ui.VisibleIf(any);
    }

    private void ShowProblems()
    {
        var any = Shell.Problems.Count > 0;
        NoProblemsText.Visibility = Ui.CollapsedIf(any);
        ProblemsList.Visibility = Ui.VisibleIf(any);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem == ProblemsTab)
        {
            Shell.ClearProblems();
        }
        else if (Tabs.SelectedItem == HistoryTab)
        {
            Shell.ClearHistory();
        }
        else
        {
            Shell.Transfers.ClearFinished();
        }
    }

    private void OnCancelTransferClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TransferItemViewModel transfer)
        {
            Shell.Transfers.Cancel(transfer);
        }
    }

    private void OnRetryTransferClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TransferItemViewModel transfer)
        {
            Shell.Transfers.Retry(transfer);
        }
    }

    // Jede Suche geht durch alle Zeilen aller Terminals – nicht bei jedem Tastendruck, sondern nach einer kurzen Pause.
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _searchDelay.Stop();
            SearchAll();
            e.Handled = true;
        }
    }

    private void SearchAll()
    {
        Shell.Search.ListQuery = SearchBox.Text;
        Shell.Search.SearchAll();
    }

    private void OnSearchHitClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchHit hit)
        {
            Shell.Search.Reveal(hit);
        }
    }

    // Neue Zeilen im Protokoll sollen sichtbar sein, ohne dass man scrollt.
    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && Shell.Output.Count > 0)
        {
            OutputPane.ScrollIntoView(Shell.Output[^1]);
        }
    }
}
