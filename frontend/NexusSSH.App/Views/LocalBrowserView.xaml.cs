using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusSSH.App.Interop;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class LocalBrowserView : UserControl
{
    public LocalBrowserView()
    {
        InitializeComponent();
        KeyDown += OnPaneKeyDown;
        Local.Recycle = RecycleBin.Send;
        ColumnHeaders.Columns = Shell.LocalColumns;
        EntryList.ContainerContentChanging += OnRowChanging;
        ListFocusKeeper.Attach(EntryList);
        Loaded += (_, _) =>
        {
            SftpBrowserView.ReleasedUntaken += OnReleasedUntaken;
            Shell.LocalColumns.Changed += OnColumnsChanged;
            ColumnHeaders.WidthDragged += OnColumnWidthDragged;
            Ui.ApplyColumns(EntryList, Shell.LocalColumns);
        };
        Unloaded += (_, _) =>
        {
            SftpBrowserView.ReleasedUntaken -= OnReleasedUntaken;
            Shell.LocalColumns.Changed -= OnColumnsChanged;
            ColumnHeaders.WidthDragged -= OnColumnWidthDragged;
        };
    }

    // Spalten: wie in SftpBrowserView – die Zeilen ziehen an Ort und Stelle nach, die Liste bleibt unberührt.
    private void OnColumnsChanged(object? sender, EventArgs e) => Ui.ApplyColumns(EntryList, Shell.LocalColumns);

    private void OnColumnWidthDragged(object? sender, (FileColumn Column, double Width) drag)
        => Ui.ApplyColumnWidth(EntryList, drag.Column, drag.Width);

    private void OnRowChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        => Ui.ApplyColumnsToRow(args.ItemContainer, Shell.LocalColumns);

    /// <summary>„Diesen Ordner mit dem des Servers abgleichen“ – das kann die rechte Spalte; sie bekommt den Ordner.</summary>
    public Func<string, Task>? SyncRequested { get; set; }

    public ShellViewModel Shell => App.Shell;

    public LocalBrowserViewModel Local => Shell.Local;

    /// <summary>Was in der rechten Spalte gerade gewählt ist – „hierher laden“ meint diese Einträge.</summary>
    public Func<IReadOnlyList<RemoteEntry>>? RemoteSelection { get; set; }

    private List<LocalEntry> Selected => [.. EntryList.SelectedItems.OfType<LocalEntry>()];

    /// <summary>Setzt den Fokus auf die Liste – für den Wechsel der Bereiche mit der Tastatur.</summary>
    public bool FocusList() => EntryList.Focus(FocusState.Keyboard);

    /// <summary>Für den Selbsttest: die Spaltenköpfe einmal bedienen.</summary>
    internal void ExerciseColumnHeaders() => ColumnHeaders.ExerciseForSelfTest();

    private void OnUpClicked(object sender, RoutedEventArgs e) => Local.GoUp();

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Local.Refresh();

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is PathSegment segment)
        {
            Local.NavigateTo(segment.FullPath);
        }
    }

    // Der Knopf öffnet das Feld – und wechselt in den eingetippten Ordner, wenn es schon offen ist.
    private void OnEditPathClicked(object sender, RoutedEventArgs e)
    {
        if (LocalPathBox.Visibility == Visibility.Visible)
        {
            GoToTyped();
            return;
        }

        LocalPathBox.Text = Local.CurrentPath;
        (LocalPathBox.Visibility, Breadcrumbs.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        LocalPathBox.Focus(FocusState.Keyboard);
        LocalPathBox.SelectAll();
    }

    private void OnPathBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            GoToTyped();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ClosePathBox();
            e.Handled = true;
        }
    }

    // Anführungszeichen drumherum (aus „Als Pfad kopieren“) gehören nicht zum Pfad.
    private void GoToTyped()
    {
        var typed = LocalPathBox.Text.Trim().Trim('"');
        if (typed.Length > 0)
        {
            Local.NavigateTo(typed);
        }

        ClosePathBox();
    }

    private void ClosePathBox()
    {
        (LocalPathBox.Visibility, Breadcrumbs.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        EntryList.Focus(FocusState.Programmatic);
    }

    private void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelected();

    // Die Tasten wie im Explorer entscheidet FileListKeys in Core; hier wird nur ausgeführt.
    private void OnEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RunKeyAction(Ui.FileListActionFor(e, remote: false)))
        {
            e.Handled = true;
        }
    }

    private bool RunKeyAction(FileListAction action)
    {
        switch (action)
        {
            case FileListAction.Open:
                OpenSelected();
                break;

            case FileListAction.Up:
                Local.GoUp();
                break;

            case FileListAction.Back:
                Local.GoBack();
                break;

            case FileListAction.Forward:
                Local.GoForward();
                break;

            case FileListAction.Refresh:
                Local.Refresh();
                break;

            case FileListAction.Rename:
                _ = RenameSelectedAsync();
                break;

            case FileListAction.Delete:
                _ = DeleteSelectedAsync(forever: false);
                break;

            case FileListAction.DeleteForever:
                _ = DeleteSelectedAsync(forever: true);
                break;

            case FileListAction.SelectAll:
                EntryList.SelectAll();
                break;

            case FileListAction.FocusPath:
                FocusPathBox();
                break;

            default:
                return false;
        }

        return true;
    }

    // Strg+L, Alt+D und F4 gelten im ganzen Bereich, nur nicht in einem Textfeld.
    private void OnPaneKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not TextBox && Ui.FileListActionFor(e, remote: false) == FileListAction.FocusPath)
        {
            FocusPathBox();
            e.Handled = true;
        }
    }

    private void FocusPathBox()
    {
        if (LocalPathBox.Visibility != Visibility.Visible)
        {
            LocalPathBox.Text = Local.CurrentPath;
            (LocalPathBox.Visibility, Breadcrumbs.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        }

        LocalPathBox.Focus(FocusState.Keyboard);
        LocalPathBox.SelectAll();
    }

    // Tippen springt zum Namen wie im Explorer; ein Leerzeichen am Anfang gehört der Liste (Auswahl).
    private readonly TypeAhead _typeAhead = new(TimeProvider.System);

    private void OnEntryCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (char.IsControl(e.Character)
            || (e.Character == ' ' && !_typeAhead.IsTyping)
            || Ui.IsDown(VirtualKey.Control) != e.KeyStatus.IsMenuKeyDown)
        {
            return;
        }

        var entries = Local.Entries;
        if (_typeAhead.Next(e.Character, [.. entries.Select(entry => entry.DisplayName)], EntryList.SelectedIndex) is { } index)
        {
            EntryList.SelectedIndex = index;
            Ui.FocusRow(EntryList, entries[index], index);
        }

        e.Handled = true;
    }

    private void OpenSelected()
    {
        if (Selected is [{ IsDirectory: true } folder])
        {
            Local.Open(folder);
        }
    }

    // Hochladen lässt sich, was gewählt ist – nur keine Laufwerke.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UploadButton.IsEnabled = Selected.Exists(entry => !entry.IsDrive);

    private void OnUploadClicked(object sender, RoutedEventArgs e) => _ = UploadAsync(Selected);

    // Erst fragen, ob am Server etwas überschrieben würde – der Kern überschreibt.
    private async Task UploadAsync(IReadOnlyList<LocalEntry> entries)
    {
        var wanted = entries.Where(entry => !entry.IsDrive).ToList();
        if (Shell.ActiveSession is not { Files.IsAvailable: true } session || wanted.Count == 0)
        {
            return;
        }

        var rule = ExistingRule.Overwrite;
        var existing = session.Files.ExistingNames(wanted.Select(entry => entry.Name));
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteRemoteText", string.Join(", ", existing)), App.Shell.Settings) is not { } chosen)
            {
                return;
            }

            rule = chosen;
        }

        // Ordner: Was nach den Ausschlüssen nicht mitginge, wird vorher gezeigt – und gefragt, was trotzdem mit soll.
        if (await App.Dialogs.AskExcludesAsync(Shell.ExcludeRules, rules => ShellViewModel.FindExcludedLocallyAsync(rules, wanted.Select(entry => entry.FullPath))) is not { } exclude)
        {
            return;
        }

        Shell.UploadFromLocal(wanted, rule, exclude);
    }

    private void OnDownloadHereClicked(object sender, RoutedEventArgs e) => _ = DownloadHereAsync(RemoteSelection?.Invoke() ?? []);

    // Was vom Server hierher soll: in den angezeigten Ordner, nach der Frage, ob hier etwas überschrieben würde.
    private async Task DownloadHereAsync(IReadOnlyList<RemoteEntry> entries)
    {
        var wanted = entries.Where(entry => entry.IsTransferable).ToList();
        if (Shell.ActiveSession is not { Files.IsAvailable: true } session || wanted.Count == 0)
        {
            return;
        }

        if (!Local.IsFolder)
        {
            await App.Dialogs.ShowMessageAsync(L.Get("LocalComputer"), L.Get("LocalNoFolder"));
            return;
        }

        var rule = ExistingRule.Overwrite;
        var existing = session.Files.ExistingLocally(wanted, Local.CurrentPath);
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteLocalText", string.Join(", ", existing.Select(Format.Visible))), App.Shell.Settings) is not { } chosen)
            {
                return;
            }

            rule = chosen;
        }

        if (await App.Dialogs.AskExcludesAsync(Shell.ExcludeRules, rules => session.Files.FindExcludedAsync(rules, wanted)) is not { } exclude)
        {
            return;
        }

        Shell.DownloadToLocal(wanted, rule, exclude);
    }

    // --- Ziehen ---

    // Von hier zum Server (oder in ein anderes Programm): Die gewählten Dateien und Ordner gehen als gewöhnliche
    // Dateien auf die Reise – die Liste des Servers nimmt sie an wie aus dem Explorer.
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<LocalEntry>().Where(entry => !entry.IsDrive).Select(entry => (entry.FullPath, entry.IsDirectory)).ToList();
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var (path, isDirectory) in paths)
                {
                    items.Add(isDirectory ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                }

                request.SetData(items);
            }
            catch (Exception exception)
            {
                App.WriteCrashLog(exception);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    // Vom Server hierher. Das Datenobjekt dieses Ziehens bietet dem eigenen Fenster mit Absicht nichts an; was
    // unterwegs ist, nennt die Serverliste selbst (SftpBrowserView.DraggedOut). Nimmt diese Spalte das Ablegen an,
    // zeigt das Ziehbild „Nach … laden“ statt des Verbotszeichens.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (SftpBrowserView.DraggedOut is { Count: > 0 } && Local.IsFolder)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.Get("DropToDownload", Local.Segments[^1].Name);
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (SftpBrowserView.DraggedOut is { Count: > 0 } entries && Local.IsFolder)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;

            // Erst nach dem Loslassen: Die Rückfrage „überschreiben?“ darf das Ziehen nicht aufhalten.
            List<RemoteEntry> wanted = [.. entries];
            DispatcherQueue.TryEnqueue(() => _ = DownloadHereAsync(wanted));
        }
    }

    // Meldet WinUI das Ablegen nicht, sagt die Serverliste, wo losgelassen wurde, ohne dass jemand die Einträge
    // nahm – liegt die Stelle in dieser Spalte, kommen sie ebenfalls in den angezeigten Ordner.
    private void OnReleasedUntaken(IReadOnlyList<RemoteEntry> entries, Ole.POINT screen)
    {
        if (IsOver(screen))
        {
            _ = DownloadHereAsync(entries);
        }
    }

    // Ob eine Stelle auf dem Bildschirm in dieser Spalte liegt – und die Spalte gerade zu sehen ist und ein Ordner
    // darin steht, in den sich laden lässt. Danach richtet sich auch der Zeiger während des Ziehens.
    private bool IsOver(Ole.POINT screen)
    {
        if (!Shell.ShowsDualFiles || !Local.IsFolder || XamlRoot is not { } root || ActualWidth <= 0)
        {
            return false;
        }

        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        if (Ole.TopWindowUnderCursor() != window || Ole.ScreenToClient(window, ref screen) == 0)
        {
            return false;
        }

        var origin = TransformToVisual(null).TransformPoint(default);
        var (x, y) = (screen.X / root.RasterizationScale - origin.X, screen.Y / root.RasterizationScale - origin.Y);
        return x >= 0 && y >= 0 && x < ActualWidth && y < ActualHeight;
    }

    // --- Aufräumen: neuer Ordner, umbenennen, löschen ---

    private void OnEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LocalEntry entry && !EntryList.SelectedItems.Contains(entry))
        {
            EntryList.SelectedItem = entry;
        }
    }

    private void OnContextMenuOpening(object? sender, object e)
    {
        var entries = Selected.FindAll(entry => !entry.IsDrive);
        UploadMenuItem.IsEnabled = DeleteMenuItem.IsEnabled = entries.Count > 0;
        CopyPathMenuItem.IsEnabled = entries.Count > 0;
        RenameMenuItem.IsEnabled = entries.Count == 1 && Selected.Count == 1;
        NewFolderMenuItem.IsEnabled = SyncMenuItem.IsEnabled = Local.IsFolder;
    }

    // Mehrere gewählt: eine Zeile je Eintrag.
    private void OnCopyPathClicked(object sender, RoutedEventArgs e)
    {
        if (Selected.FindAll(entry => !entry.IsDrive) is { Count: > 0 } entries)
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(string.Join('\n', entries.Select(entry => PathLink.Plain(entry.FullPath))));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
    }

    private void OnNewFolderClicked(object sender, RoutedEventArgs e) => _ = CreateFolderAsync();

    private void OnRenameClicked(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync();

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync();

    private void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (Local.IsFolder)
        {
            _ = SyncRequested?.Invoke(Local.CurrentPath);
        }
    }

    private async Task CreateFolderAsync()
    {
        if (Local.IsFolder
            && await App.Dialogs.AskTextAsync(L.Get("NewFolder"), L.Get("NewFolderLabel"), string.Empty, L.Get("Create"), LocalBrowserViewModel.IsValidName) is { } name)
        {
            Local.CreateFolder(name);
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (Selected is [{ IsDrive: false } entry]
            && await App.Dialogs.AskTextAsync(L.Get("Rename"), L.Get("RenameLabel"), entry.Name, L.Get("Rename"), LocalBrowserViewModel.IsValidName) is { } name
            && name.Trim() != entry.Name)
        {
            Local.Rename(entry, name);
        }
    }

    // Erst fragen. Gelöscht wird in den Papierkorb, wo das Laufwerk einen hat.
    private async Task DeleteSelectedAsync(bool forever = false)
    {
        if (Selected.FindAll(entry => !entry.IsDrive) is not { Count: > 0 } entries)
        {
            return;
        }

        var title = entries is [var single] ? L.Get("DeleteEntryTitle", single.DisplayName) : L.Get("DeleteEntriesTitle", entries.Count);
        if (await App.Dialogs.ConfirmAsync(title, L.Get(forever ? "LocalDeleteForeverText" : "LocalDeleteText"), L.Get("Delete")))
        {
            Local.Delete(entries, forever);
        }
    }
}
