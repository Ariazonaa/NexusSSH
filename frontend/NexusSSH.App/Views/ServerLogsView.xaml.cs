using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class ServerLogsView : UserControl
{
    private ServerLogsViewModel? _logs;

    // Solange die Seite ihre Felder selbst füllt, ist eine geänderte Auswahl kein Wunsch des Benutzers.
    private bool _filling;

    public ServerLogsView()
    {
        InitializeComponent();
        foreach (var text in (string[])[L.Get("LogsSinceAll"), L.Get("LogsSince15m"), L.Get("LogsSince1h"), L.Get("LogsSince6h"), L.Get("LogsSince1d"), L.Get("LogsSince7d")])
        {
            LogsSinceBox.Items.Add(new ComboBoxItem { Content = text });
        }

        foreach (var text in (string[])[L.Get("LogsPriorityAll"), L.Get("LogsFromError"), L.Get("LogsFromWarning"), L.Get("LogsFromNotice"), L.Get("LogsFromInfo")])
        {
            LogsPriorityBox.Items.Add(new ComboBoxItem { Content = text });
        }

        foreach (var count in LogQuery.LineChoices)
        {
            LogsLinesBox.Items.Add(new ComboBoxItem { Content = count.ToString(System.Globalization.CultureInfo.CurrentCulture), Tag = count });
        }

        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Logs))
            {
                Attach(Shell.Logs);
            }
        };
        Attach(Shell.Logs);
    }

    public ShellViewModel Shell => App.Shell;

    public void FocusFirst() => LogsSourceBox.Focus(FocusState.Programmatic);

    private void Attach(ServerLogsViewModel? logs)
    {
        if (_logs is not null)
        {
            _logs.PropertyChanged -= OnLogsChanged;
        }

        _logs = logs;
        if (logs is null)
        {
            LinesList.ItemsSource = null;
            return;
        }

        logs.PropertyChanged += OnLogsChanged;
        _filling = true;
        TitleText.Text = logs.Title;
        LogsSinceBox.SelectedIndex = (int)logs.Since;
        LogsPriorityBox.SelectedIndex = logs.PriorityChoice;
        LogsLinesBox.SelectedIndex = Math.Max(0, LogQuery.LineChoices.ToList().IndexOf(logs.Lines));
        LogsUnitBox.Text = logs.Unit;
        LogsFilterBox.Text = logs.Filter;
        _filling = false;
        ShowSources();
        ShowLines();
    }

    private void OnLogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _logs))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ServerLogsViewModel.Sources):
                ShowSources();
                break;

            case nameof(ServerLogsViewModel.Shown) or nameof(ServerLogsViewModel.Error) or nameof(ServerLogsViewModel.Status)
                or nameof(ServerLogsViewModel.IsLoading) or nameof(ServerLogsViewModel.IsLimited) or nameof(ServerLogsViewModel.IsJournal):
                ShowLines();
                break;
        }
    }

    // Die Namen der Dateien kommen vom Server und stehen als Text da.
    private void ShowSources()
    {
        if (_logs is not { } logs)
        {
            return;
        }

        _filling = true;
        LogsSourceBox.Items.Clear();
        foreach (var source in logs.Sources)
        {
            LogsSourceBox.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source });
        }

        LogsSourceBox.SelectedIndex = Math.Max(0, logs.Sources.ToList().IndexOf(logs.Source));
        _filling = false;
    }

    private void ShowLines()
    {
        if (_logs is not { } logs)
        {
            return;
        }

        if (!ReferenceEquals(LinesList.ItemsSource, logs.Shown))
        {
            LinesList.ItemsSource = logs.Shown;

            // Das Neueste steht unten – dorthin, wie in einem Terminal.
            if (logs.Shown.Count > 0)
            {
                LinesList.ScrollIntoView(logs.Shown[^1]);
            }
        }

        StatusText.Text = logs.Status;
        BusyRing.IsActive = logs.IsLoading;
        ErrorText.Text = logs.Error;
        ErrorText.Visibility = Ui.VisibleIf(logs.HasError);
        LimitedText.Visibility = Ui.VisibleIf(logs.IsLimited && logs.IsJournal);
        EmptyText.Visibility = Ui.VisibleIf(logs.Shown.Count == 0 && !logs.HasError && !logs.IsLoading);
        LogsSinceBox.Visibility = LogsPriorityBox.Visibility = LogsUnitBox.Visibility = Ui.VisibleIf(logs.IsJournal);
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && _logs is { } logs && (LogsSourceBox.SelectedItem as ComboBoxItem)?.Tag is LogSource source && source != logs.Source)
        {
            logs.Source = source;
            _ = logs.LoadAsync();
        }
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _logs is not { } logs)
        {
            return;
        }

        logs.Since = (LogSince)Math.Max(0, LogsSinceBox.SelectedIndex);
        logs.PriorityChoice = Math.Max(0, LogsPriorityBox.SelectedIndex);
        logs.Lines = (LogsLinesBox.SelectedItem as ComboBoxItem)?.Tag is int lines ? lines : LogQuery.DefaultLines;
        _ = logs.LoadAsync();
    }

    private void OnUnitKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ApplyUnit();
            e.Handled = true;
        }
    }

    private void OnUnitLostFocus(object sender, RoutedEventArgs e) => ApplyUnit();

    private void ApplyUnit()
    {
        if (_logs is { } logs && logs.Unit != LogsUnitBox.Text)
        {
            logs.Unit = LogsUnitBox.Text;
            _ = logs.LoadAsync();
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling && _logs is { } logs)
        {
            logs.Filter = LogsFilterBox.Text;
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_logs is { } logs)
        {
            logs.Unit = LogsUnitBox.Text;
            _ = logs.LoadAsync();
        }
    }

    // In die Zwischenablage kommt reiner Text – die Zeilen stammen vom Server.
    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (_logs is not { Shown.Count: > 0 } logs)
        {
            return;
        }

        var chosen = LinesList.SelectedItems.OfType<ServerLogLine>().ToList();
        var text = chosen.Count > 0 ? string.Join(Environment.NewLine, logs.Shown.Where(chosen.Contains).Select(line => line.Text)) : logs.CopyText();
        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (Interop.TextClipboard.Set(window, text, html: null, rtf: null))
        {
            Shell.Log(L.Get("LogsCopied", chosen.Count > 0 ? chosen.Count : logs.Shown.Count));
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Shell.CloseLogs();

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Shell.CloseLogs();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            OnRefreshClicked(sender, e);
            e.Handled = true;
        }
    }
}
