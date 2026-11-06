using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace NexusSSH.App.Views;

public sealed partial class TerminalWorkspaceView : UserControl
{
    public TerminalWorkspaceView()
    {
        InitializeComponent();
        Shell.Search.PropertyChanged += OnSearchPropertyChanged;

        StartPage.LosingFocus += OnStartPageLosingFocus;
    }

    // Wer auf der Startseite einen Server öffnet, hat den Fokus auf dessen Kachel – und die Startseite ist weg,
    // sobald der Tab steht. Das Terminal ist in dem Augenblick noch eingeklappt und nimmt keinen Fokus; WinUI gab ihn
    // dem nächsten Element in der Reihe, dem Kästchen der Leiste unter dem Terminal. Das erste Leerzeichen, das der
    // Benutzer tippte, schaltete die Messung ab, und auch eine Rückfrage beim Verbinden gab ihn danach dorthin zurück
    // (gefunden 2026-10-24 im Gast). Er gehört ins Terminal – erst, wenn es zu sehen ist.
    private void OnStartPageLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (StartPage.Visibility == Visibility.Collapsed && Shell.HasActiveSession)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Shell.FocusTerminal);
        }
    }

    private void OnDetachedShowClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session)
        {
            Interop.TerminalWindows.Show(session);
        }
    }

    private void OnDetachedReattachClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session)
        {
            Shell.Reattach(session);
        }
    }

    /// <summary>Die Startseite erscheint beim Wechsel weich statt hart – wenn es eingestellt ist.</summary>
    public void ApplyAnimations(bool enabled)
    {
        Ui.FadeIn(StartPage, enabled);
    }

    public ShellViewModel Shell => App.Shell;

    /// <summary>Setzt den Fokus in die Ansicht „Zwei Spalten“, die an der Stelle des Terminals liegt.</summary>
    public bool FocusDualFiles() => DualFiles.FocusList();

    /// <summary>Für den Selbsttest: die Spaltenköpfe beider Listen der Ansicht „Zwei Spalten“ bedienen.</summary>
    internal void ExerciseColumnHeaders() => DualFiles.ExerciseColumnHeaders();

    /// <summary>Für den Selbsttest: die Menüs der Startseite; nennt, was fehlt.</summary>
    internal string ExerciseHomeMenus() => StartPage.ExerciseMenus();

    private void OnReconnectClicked(object sender, RoutedEventArgs e) => Shell.ActiveSession?.Reconnect();

    // --- Suchleiste ---

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TerminalSearchViewModel.IsOpen))
        {
            return;
        }

        if (Shell.Search.IsOpen)
        {
            // Erst nach dem Einblenden lässt sich das Feld fokussieren.
            DispatcherQueue.TryEnqueue(() =>
            {
                FindBox.Focus(FocusState.Keyboard);
                FindBox.SelectAll();
            });
        }
        else
        {
            Shell.FocusTerminal();
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
                {
                    Shell.Search.FindPrevious();
                }
                else
                {
                    Shell.Search.FindNext();
                }

                e.Handled = true;
                break;

            case VirtualKey.Escape:
                Shell.Search.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnFindPreviousClicked(object sender, RoutedEventArgs e) => Shell.Search.FindPrevious();

    private void OnFindNextClicked(object sender, RoutedEventArgs e) => Shell.Search.FindNext();

    private void OnFindCloseClicked(object sender, RoutedEventArgs e) => Shell.Search.Close();
}
