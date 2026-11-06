using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

/// <summary>Die Hilfe-Seite: Neues, Handgriffe, Tastenkürzel. Der Inhalt kommt aus <see cref="HelpContent"/>.</summary>
public sealed partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        NewsTitle.Text = L.Get("HelpNewsTitle", typeof(HelpView).Assembly.GetName().Version?.ToString(2) ?? "?");
        NewsList.ItemsSource = HelpContent.WhatsNew();
        Fill();
        Shell.PropertyChanged += OnShellPropertyChanged;
        Unloaded += (_, _) => Shell.PropertyChanged -= OnShellPropertyChanged;
    }

    public ShellViewModel Shell => App.Shell;

    public void FocusFirst() => CloseButton.Focus(FocusState.Programmatic);

    // Handgriffe und Kürzel nennen die Tasten, wie sie gelten – ändert der Benutzer eines, entsteht beides neu.
    private void Fill()
    {
        TopicList.ItemsSource = HelpContent.Topics(Shell.Commands);
        ShortcutList.ItemsSource = HelpContent.Shortcuts(Shell.Commands);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Commands))
        {
            Fill();
        }
        else if (e.PropertyName == nameof(ShellViewModel.ShortcutsWanted))
        {
            // Erst nach dem Einblenden: Eine Seite, die noch nicht zu sehen ist, rollt nicht.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShortcutsTitle.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }));
        }
    }

    // Die Kürzel als Seite zum Drucken: schlichtes HTML, wohin, fragt der Speichern-Dialog von Windows.
    private async void OnSaveShortcutsClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "NexusSSH-" + L.Get("HelpShortcutsFile"),
            DefaultFileExtension = ".html",
        };
        picker.FileTypeChoices.Add(L.Get("HelpShortcutsPage"), [".html"]);
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path, HelpContent.ShortcutsHtml(Shell.Commands, "NexusSSH – " + L.Get("HelpShortcutsTitle")));
            Shell.Log(L.Get("HelpShortcutsSaved", file.Path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Shell.Log(L.Get("HelpShortcutsNotSaved", error.Message), LogSeverity.Warning);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Shell.IsHelpOpen = false;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Shell.IsHelpOpen = false;
            e.Handled = true;
        }
    }
}
