using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace NexusSSH.App.Views;

/// <summary>
/// Zeigt, was <see cref="ShellViewModel.Viewer"/> gerade hält. Die Ansicht selbst weiß nur, wie Text, Bild und
/// PDF gezeichnet werden; was geladen, geblättert und gespeichert wird, entscheidet das ViewModel.
/// </summary>
public sealed partial class FileViewerView : UserControl
{
    private FileViewerViewModel? _viewer;
    private PdfDocument? _pdf;
    private uint _page;
    private bool _filling;

    public FileViewerView()
    {
        InitializeComponent();
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Viewer))
            {
                Attach(Shell.Viewer);
            }
        };
        Attach(Shell.Viewer);
    }

    public ShellViewModel Shell => App.Shell;

    public void FocusFirst() => CloseButton.Focus(FocusState.Programmatic);

    private void Attach(FileViewerViewModel? viewer)
    {
        if (_viewer is not null)
        {
            _viewer.PropertyChanged -= OnViewerChanged;
        }

        (_viewer, _pdf) = (viewer, null);
        if (viewer is not null)
        {
            viewer.PropertyChanged += OnViewerChanged;
        }

        Show();
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e) => Show(e.PropertyName);

    private void Show(string? changed = null)
    {
        if (_viewer is not { } viewer)
        {
            TextView.Blocks.Clear();
            ImageView.Source = null;
            Editor.Text = string.Empty;
            return;
        }

        TitleText.Text = viewer.Title;
        StatusText.Text = viewer.IsDirty ? $"{viewer.Status}  ·  {L.Get("ViewerUnsaved")}" : viewer.Status;
        BusyRing.IsActive = viewer.IsBusy;
        BackButton.Visibility = OnButton.Visibility = Ui.VisibleIf(viewer.ShowsPages && (viewer.CanGoBack || viewer.CanGoOn));
        BackButton.IsEnabled = viewer.CanGoBack && !viewer.IsBusy;
        OnButton.IsEnabled = viewer.CanGoOn && !viewer.IsBusy;
        HexButton.Visibility = Ui.VisibleIf(viewer.CanToggleHex);
        HexButton.IsChecked = viewer.IsHex;
        EditButton.Visibility = Ui.VisibleIf(viewer.IsText);
        EditButton.IsEnabled = viewer.CanEdit && !viewer.IsBusy;
        ToolTipService.SetToolTip(EditButton, viewer.EditObstacle.Length > 0 ? viewer.EditObstacle : null);
        SaveButton.Visibility = LeaveButton.Visibility = Ui.VisibleIf(viewer.IsEditing);
        SaveButton.IsEnabled = viewer.IsDirty && !viewer.IsBusy;

        TextScroll.Visibility = Ui.VisibleIf(viewer.Mode is ViewerMode.Text or ViewerMode.Hex or ViewerMode.Loading or ViewerMode.Problem);
        ImagePane.Visibility = Ui.VisibleIf(viewer.Mode is ViewerMode.Image or ViewerMode.Pdf);
        PagePanel.Visibility = Ui.VisibleIf(viewer.Mode == ViewerMode.Pdf);
        Editor.Visibility = Ui.VisibleIf(viewer.IsEditing);

        switch (changed)
        {
            case null or nameof(FileViewerViewModel.Mode):
                ShowContent(viewer);
                break;

            case nameof(FileViewerViewModel.Lines) when viewer.IsText:
            case nameof(FileViewerViewModel.HexText) when viewer.IsHex:
                ShowContent(viewer);
                break;
        }
    }

    private void ShowContent(FileViewerViewModel viewer)
    {
        switch (viewer.Mode)
        {
            case ViewerMode.Text:
                ShowLines(viewer.Lines);
                break;

            case ViewerMode.Hex:
                ShowPlain(viewer.HexText);
                break;

            case ViewerMode.Edit:
                _filling = true;
                Editor.Text = viewer.EditText;
                _filling = false;
                Editor.Focus(FocusState.Programmatic);
                break;

            case ViewerMode.Image when viewer.LocalPath is { } path:
                ShowImage(path);
                break;

            case ViewerMode.Pdf when viewer.LocalPath is { } path:
                _ = ShowPdfAsync(path);
                break;

            default:
                TextView.Blocks.Clear();
                break;
        }
    }

    // Jede Zeile besteht aus Runs mit reinem Text; die Farbe kommt aus der Einteilung des ViewModels.
    private void ShowLines(IReadOnlyList<ViewerLine> lines)
    {
        var paragraph = new Paragraph();
        foreach (var line in lines)
        {
            foreach (var span in line.Spans)
            {
                var run = new Run { Text = line.Text.Substring(span.Start, span.Length) };
                if (Brush(span.Token) is { } brush)
                {
                    run.Foreground = brush;
                }

                paragraph.Inlines.Add(run);
            }

            paragraph.Inlines.Add(new Run { Text = "\n" });
        }

        TextView.Blocks.Clear();
        TextView.Blocks.Add(paragraph);
        TextScroll.ChangeView(0, 0, null, disableAnimation: true);
    }

    private void ShowPlain(string text)
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text });
        TextView.Blocks.Clear();
        TextView.Blocks.Add(paragraph);
        TextScroll.ChangeView(0, 0, null, disableAnimation: true);
    }

    private static Brush? Brush(SyntaxToken token) => token switch
    {
        SyntaxToken.Comment => (Brush)Application.Current.Resources["SyntaxCommentBrush"],
        SyntaxToken.String => (Brush)Application.Current.Resources["SyntaxStringBrush"],
        SyntaxToken.Number => (Brush)Application.Current.Resources["SyntaxNumberBrush"],
        SyntaxToken.Key => (Brush)Application.Current.Resources["SyntaxKeyBrush"],
        SyntaxToken.Section or SyntaxToken.Tag => (Brush)Application.Current.Resources["SyntaxTagBrush"],
        _ => null,
    };

    private void ShowImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.ImageFailed += (_, _) => StatusText.Text = L.Get("PreviewNotText");
        bitmap.UriSource = new Uri(path);
        ImageView.Source = bitmap;
    }

    // Ein PDF wird Seite für Seite zu einem Bild gerendert (Windows.Data.Pdf) – es läuft nichts aus der Datei.
    private async Task ShowPdfAsync(string path)
    {
        try
        {
            _pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
            _page = 0;
            await ShowPageAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            StatusText.Text = L.Get("ViewerPdfUnreadable");
        }
    }

    private async Task ShowPageAsync()
    {
        if (_pdf is not { } pdf || pdf.PageCount == 0)
        {
            StatusText.Text = L.Get("ViewerPdfUnreadable");
            return;
        }

        _page = Math.Min(_page, pdf.PageCount - 1);
        using var page = pdf.GetPage(_page);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        ImageView.Source = bitmap;
        PageText.Text = L.Get("ViewerPage", _page + 1, pdf.PageCount);
        PreviousPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page + 1 < pdf.PageCount;
    }

    private void OnPreviousPageClicked(object sender, RoutedEventArgs e)
    {
        if (_page > 0)
        {
            _page--;
            _ = ShowPageAsync();
        }
    }

    private void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        _page++;
        _ = ShowPageAsync();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => _ = _viewer?.GoBackAsync();

    private void OnOnClicked(object sender, RoutedEventArgs e) => _ = _viewer?.GoOnAsync();

    private void OnHexClicked(object sender, RoutedEventArgs e) => _ = _viewer?.ToggleHexAsync();

    private void OnEditClicked(object sender, RoutedEventArgs e) => _ = _viewer?.EditAsync();

    private void OnSaveClicked(object sender, RoutedEventArgs e) => _ = _viewer?.SaveAsync();

    private void OnLeaveClicked(object sender, RoutedEventArgs e) => _ = Shell.LeaveEditorAsync();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => _ = Shell.CloseViewerAsync();

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling && _viewer is { IsEditing: true } viewer)
        {
            viewer.EditText = Editor.Text;
        }
    }

    // Esc schließt (mit Rückfrage, wenn etwas ungespeichert ist), Strg+S speichert.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _ = Shell.CloseViewerAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.S && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
        {
            _ = _viewer?.SaveAsync();
            e.Handled = true;
        }
    }
}
