using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.App.Interop;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace NexusSSH.App.Views;

public sealed partial class SftpBrowserView : UserControl
{
    // Das Dateisystem, dessen Baum gerade zu sehen ist – sein markierter Knoten wird hier nachgezogen.
    private SftpBrowserViewModel? _watched;

    public SftpBrowserView()
    {
        InitializeComponent();
        KeyDown += OnPaneKeyDown;
        Shell.PropertyChanged += OnShellPropertyChanged;
        ColumnHeaders.Columns = Shell.RemoteColumns;

        // In der kompakten Darstellung (Ui.ApplyDensity) sind die Zeilen niedriger – wie in den übrigen Listen.
        if (Ui.Gap < 8)
        {
            var compact = new Style(typeof(ListViewItem)) { BasedOn = (Style)Resources["FileRowStyle"] };
            compact.Setters.Add(new Setter(MinHeightProperty, 24d));
            EntryList.ItemContainerStyle = compact;
        }

        BuildContextMenu();
        EntryList.ContainerContentChanging += OnRowChanging;
        ListFocusKeeper.Attach(EntryList);
        Loaded += (_, _) =>
        {
            Shell.RemoteColumns.Changed += OnColumnsChanged;
            ColumnHeaders.WidthDragged += OnColumnWidthDragged;
            Ui.ApplyColumns(EntryList, Shell.RemoteColumns);
        };
        Unloaded += (_, _) =>
        {
            Shell.RemoteColumns.Changed -= OnColumnsChanged;
            ColumnHeaders.WidthDragged -= OnColumnWidthDragged;
        };
        WireTreeSplitter();
        // Der Name am TreeView erreicht eine Sprachausgabe nicht: Der TreeView selbst steht nicht im Baum der UI
        // Automation, als „Baum“ steht dort die Liste darin – und die gibt es erst, wenn er zu sehen ist.
        DirectoryTree.SizeChanged += (_, _) =>
        {
            if (TreeList() is { } list)
            {
                AutomationProperties.SetName(list, L.Get("DirectoryTree"));
            }
        };
        ApplyTree();
        WatchFiles();
        ShowSort();
    }

    public ShellViewModel Shell => App.Shell;

    // „Bereich ausblenden“ im Menü „⋯“; zurück holt ihn der Umschalter in der Titelleiste.
    private void OnHideClicked(object sender, RoutedEventArgs e) => Shell.IsSftpVisible = false;

    private SftpBrowserViewModel? Files => Shell.ActiveSession?.Files;

    private List<RemoteEntry> Selected => [.. EntryList.SelectedItems.OfType<RemoteEntry>()];

    /// <summary>Setzt den Fokus auf die Dateiliste; <c>false</c>, wenn es gerade keine gibt.</summary>
    public bool FocusList() => Files is { IsAvailable: true } && EntryList.Focus(FocusState.Keyboard);

    /// <summary>Für den Selbsttest: die Spaltenköpfe einmal bedienen.</summary>
    internal void ExerciseColumnHeaders() => ColumnHeaders.ExerciseForSelfTest();

    /// <summary>
    /// Für den Selbsttest: Jede Aktion aus <see cref="FileMenus"/> steht an ihrer Stelle (Symbolzeile, Menü „⋯“,
    /// Kontextmenü samt „Mehr“), beide Menüs lassen sich füllen, und die Symbole haben ihre Form – deren Geometrie
    /// entsteht erst zur Laufzeit.
    /// </summary>
    internal void ExerciseMenus()
    {
        var toolbar = Toolbar.Children.OfType<Button>().Select(button => button.Tag as string).ToHashSet();
        var missing = FileMenus.Toolbar.Where(action => !toolbar.Contains(action.ToString())).ToList();

        OnViewMenuOpening(ViewMenu, EventArgs.Empty);
        var tagged = Tags(ViewMenu.Items).ToHashSet();
        missing.AddRange(FileMenus.ViewMenu.Where(action => !tagged.Contains(action.ToString())));

        OnContextMenuOpening(_contextMenu, EventArgs.Empty);
        missing.AddRange(FileMenus.Context.Concat(FileMenus.More).Where(action => !_contextItems.ContainsKey(action)));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"self-test: the file browser lacks {string.Join(", ", missing)}");
        }

        if (!UpIcon.HasShape)
        {
            throw new InvalidOperationException("self-test: a line icon has no shape");
        }

        static IEnumerable<string> Tags(IEnumerable<MenuFlyoutItemBase> items)
        {
            foreach (var item in items)
            {
                if (item.Tag is string tag)
                {
                    yield return tag;
                }
            }
        }
    }

    // --- Symbole der Zeilen ---

    /// <summary>Gefüllter gelber Ordner – nicht bei Links und Pipes, die ihr eigenes Zeichen haben.</summary>
    public static Visibility FolderIconVisibility(bool isDirectory, bool isLink, bool isSpecial) => Ui.VisibleIf(isDirectory && !isLink && !isSpecial);

    /// <summary>Datei als grauer Umriss.</summary>
    public static Visibility FileIconVisibility(bool isDirectory, bool isLink, bool isSpecial) => Ui.VisibleIf(!isDirectory && !isLink && !isSpecial);

    /// <summary>Links, Pipes, Geräte und Links ins Leere: das Zeichen aus <see cref="Ui.EntryGlyph"/>.</summary>
    public static Visibility GlyphVisibility(bool isLink, bool isSpecial) => Ui.VisibleIf(isLink || isSpecial);

    // --- Terminal-Ordner folgen ---

    /// <summary>Der Tooltip des Kästchens: was es tut – oder, ausgegraut, warum es nicht geht.</summary>
    public string FollowTip(bool canFollow) => canFollow ? L.Get("SftpFollowShellTip") : L.Get("SftpFollowShellOff");

    // „Wie?“ neben dem ausgegrauten Kästchen: Die Hilfe nennt die Zeile für die .bashrc.
    private void OnFollowHowClicked(object sender, RoutedEventArgs e) => Shell.IsHelpOpen = true;

    // --- Baum der Verzeichnisse ---

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.ActiveSession):
                WatchFiles();
                break;

            case nameof(ShellViewModel.FileSort):
                ShowSort();
                break;

            case nameof(ShellViewModel.ShowDirectoryTree):
                ApplyTree();
                break;
        }
    }

    // Ausgeblendet gibt der Baum seine Zeile ganz frei, sein Teiler verschwindet mit.
    private void ApplyTree()
    {
        var show = Shell.ShowDirectoryTree;
        TreeRow.Height = new GridLength(show ? Shell.Settings.TreeHeight : 0);
        DirectoryTree.Visibility = TreeSplitter.Visibility = Ui.VisibleIf(show);
    }

    private void WireTreeSplitter()
    {
        double start = 0;
        TreeSplitter.DragStarted += (_, _) => start = Shell.Settings.TreeHeight;
        TreeSplitter.DragDelta += (_, delta) =>
        {
            // Unter dem Baum bleibt Platz für ein paar Zeilen der Liste.
            var room = Math.Max(AppSettings.MinTreeHeight, ActualHeight - 260);
            Shell.Settings.TreeHeight = Math.Clamp(start + delta, AppSettings.MinTreeHeight, Math.Min(room, AppSettings.MaxTreeHeight));
            ApplyTree();
        };
        TreeSplitter.ResetRequested += (_, _) =>
        {
            Shell.Settings.TreeHeight = AppSettings.DefaultTreeHeight;
            ApplyTree();
        };
    }

    private void WatchFiles()
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnFilesPropertyChanged;
        }

        _watched = Files;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnFilesPropertyChanged;
        }

        MarkCurrentNode();
        ShowFilter();
        ShowPath();
    }

    private void OnFilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SftpBrowserViewModel.CurrentNode):
                MarkCurrentNode();
                break;

            case nameof(SftpBrowserViewModel.Filter):
                ShowFilter();
                break;

            case nameof(SftpBrowserViewModel.CurrentPath):
                ShowPath();
                break;
        }
    }

    // --- Menü „⋯“ ---

    // Lesezeichen entstehen bei jedem Öffnen neu. „Als root“ führt hinein und heraus und braucht eine POSIX-Shell am
    // Server (FileMenus.IsShown); „ausblenden“ gibt es in „Zwei Spalten“ nicht.
    private void OnViewMenuOpening(object? sender, object e)
    {
        FillBookmarks();
        ShowSort();
        RootMenuItem.Text = Files?.IsRoot == true ? L.Get("RootLeave") : L.Get("RootEnter");
        RootMenuItem.Visibility = Ui.VisibleIf(Files is not { } files || FileMenus.IsShown(FileAction.Root, files.HasCommands, files.IsWindowsServer));
        HideMenuItem.Visibility = Ui.CollapsedIf(Embedded);
    }

    // --- Liste filtern ---

    // Das Feld zeigt den Filter des aktiven Tabs: Jeder Dateibrowser hat seinen eigenen, und er endet mit dem
    // Wechsel des Verzeichnisses.
    private void ShowFilter()
    {
        var filter = Files?.Filter ?? string.Empty;
        if (FilterBox.Text != filter)
        {
            FilterBox.Text = filter;
        }
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Files is { } files && files.Filter != FilterBox.Text)
        {
            files.Filter = FilterBox.Text;
        }
    }

    // Wer in der Liste tippt, springt zum Namen (wie im Explorer); gefiltert wird mit Strg+F oder im Feld unten. Ein
    // Leerzeichen am Anfang gehört der Liste (Auswahl), Strg+Buchstabe den Befehlen.
    private readonly TypeAhead _typeAhead = new(TimeProvider.System);

    private void OnEntryCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (Files is not { IsAvailable: true } files
            || char.IsControl(e.Character)
            || (e.Character == ' ' && !_typeAhead.IsTyping)
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) != e.KeyStatus.IsMenuKeyDown)
        {
            return;
        }

        var entries = files.Entries;
        if (_typeAhead.Next(e.Character, [.. entries.Select(entry => entry.DisplayName)], EntryList.SelectedIndex) is { } index)
        {
            EntryList.SelectedIndex = index;
            Ui.FocusRow(EntryList, entries[index], index);
        }

        e.Handled = true;
    }

    // Esc leert den Filter und gibt den Fokus der Liste zurück, Pfeil ab führt in die Liste; Enter öffnet, was als
    // Einziges übrig ist.
    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                FilterBox.Text = string.Empty;
                EntryList.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;

            case VirtualKey.Enter when Files is { Entries: [var only] } files:
                files.OpenCommand.Execute(only);
                e.Handled = true;
                break;

            case VirtualKey.Down or VirtualKey.Enter:
                if (EntryList.SelectedItem is null && EntryList.Items.Count > 0)
                {
                    EntryList.SelectedIndex = 0;
                }

                EntryList.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
        }
    }

    // --- Lesezeichen ---

    // Das Untermenü entsteht bei jedem Öffnen von „⋯“ neu: oben „merken“ oder „entfernen“ für den angezeigten Ordner,
    // darunter die gemerkten Ordner dieses Servers, am Ende ein Untermenü zum Entfernen – auch für Ordner, die es
    // nicht mehr gibt und in die sich deshalb nicht mehr wechseln lässt.
    private void FillBookmarks()
    {
        BookmarksMenu.Items.Clear();
        var bookmarked = Shell.IsCurrentFolderBookmarked;
        var toggle = new MenuFlyoutItem
        {
            Text = bookmarked ? L.Get("BookmarkRemove") : L.Get("BookmarkFolder"),
            Icon = new FontIcon { Glyph = bookmarked ? "" : "" },
            IsEnabled = bookmarked || Shell.CanBookmarkCurrentFolder,
        };
        toggle.Click += (_, _) => Shell.ToggleBookmark();
        BookmarksMenu.Items.Add(toggle);
        BookmarksMenu.Items.Add(new MenuFlyoutSeparator());

        var bookmarks = Shell.ActiveBookmarks;
        if (bookmarks.Count == 0)
        {
            BookmarksMenu.Items.Add(new MenuFlyoutItem { Text = L.Get("BookmarksEmpty"), IsEnabled = false });
            return;
        }

        var remove = new MenuFlyoutSubItem { Text = L.Get("BookmarkRemoveOne"), Icon = new FontIcon { Glyph = "" } };
        foreach (var path in bookmarks)
        {
            // Der Pfad kam einmal vom Server: als reiner Text und ohne Steuerzeichen zeigen.
            var open = new MenuFlyoutItem { Text = Format.Visible(path), Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => _ = Files?.NavigateToAsync(path);
            BookmarksMenu.Items.Add(open);

            var forget = new MenuFlyoutItem { Text = Format.Visible(path) };
            forget.Click += (_, _) => Shell.RemoveBookmark(path);
            remove.Items.Add(forget);
        }

        BookmarksMenu.Items.Add(new MenuFlyoutSeparator());
        BookmarksMenu.Items.Add(remove);
    }

    // x:Bind an SelectedItem trägt nicht – die Markierung wird von Hand gesetzt. Erst nach dem Aufbau: Den Knoten
    // gibt es im Baum erst, wenn die Verzeichnisse darüber aufgeklappt sind.
    private void MarkCurrentNode()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_watched?.CurrentNode is { } node && !ReferenceEquals(DirectoryTree.SelectedItem, node))
            {
                DirectoryTree.SelectedItem = node;
            }

            // Tief unten im Baum läge die Markierung sonst außer Sicht. Der Knoten ist eben erst aufgeklappt worden –
            // erst muss das Layout stehen, sonst rollt der Baum an eine Stelle, die es noch nicht gibt.
            if (_watched?.CurrentNode is { } shown)
            {
                SeekNode(shown, 0);
            }
        });
    }

    // Rollt zum Knoten des angezeigten Verzeichnisses. Der Baum baut nur auf, was zu sehen ist – und klappt ein
    // Verzeichnis erst auf, wenn es sein Element gibt. Liegt der Knoten weiter unten, gibt es ihn im Baum deshalb
    // zunächst gar nicht: den Weg von oben abgehen, am tiefsten Verzeichnis, das es schon gibt, aufklappen und
    // dorthin rollen – dann entsteht die nächste Ebene, bis der Knoten selbst da ist.
    private void SeekNode(DirectoryNode shown, int step)
    {
        const int MostSteps = 300;
        if (!ReferenceEquals(_watched?.CurrentNode, shown))
        {
            return;
        }

        DirectoryTree.UpdateLayout();
        if (DirectoryTree.ContainerFromItem(shown) is UIElement container)
        {
            BringNodeIntoView(container);
            return;
        }

        if (step >= MostSteps)
        {
            return;
        }

        var path = _watched.TreePathTo(shown);
        foreach (var ancestor in path.Reverse())
        {
            if (DirectoryTree.ContainerFromItem(ancestor) is TreeViewItem deepest)
            {
                // Das Verzeichnis darüber ist schon aufgeklappt, der Knoten trotzdem nicht aufgebaut: Er steht unter
                // so vielen Geschwistern, dass der Baum ihn noch nicht gezeichnet hat. Dann hilft kein Rollen zum
                // Verzeichnis darüber – der Baum rollt zu der Zeile, in der der Knoten stehen wird.
                if (ReferenceEquals(ancestor, path[^1]) && deepest.IsExpanded && deepest.ActualHeight > 0
                    && _watched.TreeRowOf(shown) is { } row
                    && TreeList() is { } list && Descendant<ScrollViewer>(list) is { } viewer)
                {
                    viewer.ChangeView(null, Math.Max(0, (row * deepest.ActualHeight) - (viewer.ViewportHeight / 2)), null, disableAnimation: true);
                    break;
                }

                deepest.IsExpanded = true;
                deepest.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.0, AnimationDesired = false });
                break;
            }
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => SeekNode(shown, step + 1));
    }
    // Senkrecht in die Mitte; waagerecht so, dass der Name zu sehen ist – tief im Baum läge er sonst hinter dem
    // rechten Rand, denn jede Ebene rückt ein Stück ein.
    private void BringNodeIntoView(UIElement container)
    {
        // Jede Ebene rückt um ein festes Stück ein. Der Baum ist aber nur so breit wie sein Platz: Ab einer gewissen
        // Tiefe stünde der Name hinter dem rechten Rand. Dann bekommt die Liste im Baum die Breite, die sie für
        // diese Tiefe braucht, und lässt sich waagerecht rollen – bis zum Namen.
        const double Step = 16, Room = 160;
        var depth = _watched is { CurrentNode: { } node } ? _watched.TreePathTo(node).Count : 0;
        if (TreeList() is { ItemsPanelRoot: FrameworkElement panel } list && Descendant<ScrollViewer>(list) is { } viewer)
        {
            var needed = (depth * Step) + Room;
            var sideways = needed > viewer.ViewportWidth && viewer.ViewportWidth > 0;
            viewer.HorizontalScrollMode = sideways ? ScrollMode.Auto : ScrollMode.Disabled;
            viewer.HorizontalScrollBarVisibility = sideways ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            panel.MinWidth = sideways ? needed : 0;
            if (!sideways)
            {
                viewer.ChangeView(0, null, null, disableAnimation: true);
            }

            DirectoryTree.UpdateLayout();
        }

        // Zum Namen selbst, nicht zur ganzen Zeile: Die Zeile ist so breit wie die Liste.
        var target = Descendant<TextBlock>(container) as UIElement ?? container;
        target.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0.5,
            HorizontalAlignmentRatio = 0.6,
            AnimationDesired = false,
        });
    }
    // Die Liste, mit der der Baum seine Zeilen zeigt.
    private ListViewBase? TreeList() => Descendant<ListViewBase>(DirectoryTree);

    private static T? Descendant<T>(DependencyObject parent)
        where T : class
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is DirectoryNode node && Files is { } files)
        {
            _ = files.ExpandAsync(node);
        }
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is DirectoryNode node && Files is { } files)
        {
            _ = files.OpenNodeAsync(node);
        }
    }

    private void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelected();

    // Die Tasten wie im Explorer entscheidet FileListKeys in Core; hier wird nur ausgeführt.
    private bool RunKeyAction(FileListAction action)
    {
        switch (action)
        {
            case FileListAction.Open:
                OpenSelected();
                break;

            case FileListAction.Delete:
                _ = DeleteSelectedAsync();
                break;

            case FileListAction.Rename:
                _ = RenameSelectedAsync();
                break;

            case FileListAction.Refresh:
                Files?.RefreshCommand.Execute(null);
                break;

            case FileListAction.Up:
                Files?.GoUpCommand.Execute(null);
                break;

            case FileListAction.Back:
                Files?.GoBackCommand.Execute(null);
                break;

            case FileListAction.Forward:
                Files?.GoForwardCommand.Execute(null);
                break;

            case FileListAction.SelectAll:
                EntryList.SelectAll();
                break;

            case FileListAction.FocusPath:
                FocusPathBox();
                break;

            case FileListAction.FocusFilter:
                FilterBox.Focus(FocusState.Keyboard);
                FilterBox.SelectAll();
                break;

            case FileListAction.Properties:
                _ = ShowPropertiesAsync();
                break;

            default:
                return false;
        }

        return true;
    }

    // Strg+L, Alt+D und F4 gelten im ganzen Bereich, nur nicht in einem Textfeld (Filter, Pfad, Suche).
    private void OnPaneKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not TextBox && Ui.FileListActionFor(e, remote: true) == FileListAction.FocusPath)
        {
            FocusPathBox();
            e.Handled = true;
        }
    }

    private void FocusPathBox()
    {
        if (Files is not null)
        {
            PathBox.Focus(FocusState.Programmatic);
            PathBox.SelectAll();
        }
    }

    private void OnEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RunKeyAction(Ui.FileListActionFor(e, remote: true)))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.C when InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down):
                CopySelected();
                e.Handled = true;
                break;

            // Ordnen auf dem Server: ausschneiden, einfügen, duplizieren, zurücknehmen.
            case VirtualKey.X when InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down):
                Files?.Hold(Selected, cut: true);
                e.Handled = true;
                break;

            case VirtualKey.V when InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down):
                _ = PasteHeldAsync();
                e.Handled = true;
                break;

            case VirtualKey.D when InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down):
                _ = Files?.DuplicateAsync(Selected);
                e.Handled = true;
                break;

            case VirtualKey.Z when InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down):
                _ = Files?.UndoAsync();
                e.Handled = true;
                break;
        }
    }

    private void OpenSelected()
    {
        if (Files is { } files && Selected is [var entry])
        {
            files.OpenCommand.Execute(entry);
        }
    }

    // Was mit der Auswahl geht, entscheidet FileMenus in Core – für die Symbolzeile und das Kontextmenü gleich.
    private FileSelection Selection()
        => FileSelection.Of(
            Selected,
            Files?.CanPaste == true,
            Files?.CanUndo == true,
            Shell.CompareMarked is not null,
            Shell.ActiveSession is { State: ConnectionState.Connected, IsFilesOnly: false });

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = Selection();
        DeleteButton.IsEnabled = FileMenus.IsEnabled(FileAction.Delete, selection);
        DownloadButton.IsEnabled = FileMenus.IsEnabled(FileAction.Download, selection);
        if (Files is { } files)
        {
            files.SelectedEntry = Selected.FirstOrDefault();
        }
    }

    // Rechtsklick auf einen nicht gewählten Eintrag wählt ihn – wie im Explorer.
    private void OnEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RemoteEntry entry && !EntryList.SelectedItems.Contains(entry))
        {
            EntryList.SelectedItem = entry;
        }
    }

    // --- Kontextmenü ---

    // Kurz wie im Entwurf: sechs Einträge, dahinter „Mehr“ mit dem Rest (FileMenus.Context, FileMenus.More). Es
    // entsteht einmal; beim Öffnen zeigt jeder Eintrag, ob er mit der Auswahl etwas tun kann.
    private readonly MenuFlyout _contextMenu = new();
    private readonly Dictionary<FileAction, MenuFlyoutItemBase> _contextItems = [];
    private readonly MenuFlyoutItem _openItem = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly MenuFlyoutSubItem _sendToServerMenu = new() { Text = L.Get("SendToServer") };
    private readonly MenuFlyoutSubItem _copyPathAsMenu = new() { Text = L.Get("CopyPathAs") };
    private readonly MenuFlyoutItem _compareBothItem = new() { Text = L.Get("CompareBoth") };
    private readonly MenuFlyoutItem _compareLocalItem = new() { Text = L.Get("CompareLocal") };
    private readonly MenuFlyoutItem _compareMarkItem = new() { Text = L.Get("CompareMark") };
    private readonly MenuFlyoutItem _compareMarkedItem = new() { Text = L.Get("CompareWithMarked") };

    private void BuildContextMenu()
    {
        foreach (var action in FileMenus.Context)
        {
            // Wie im Bild: eine Linie vor „Pfad kopieren“.
            if (action == FileAction.CopyPath)
            {
                _contextMenu.Items.Add(new MenuFlyoutSeparator());
            }

            _contextMenu.Items.Add(_contextItems[action] = ContextItem(action));
        }

        var more = new MenuFlyoutSubItem { Text = L.Get("SftpMore") };
        foreach (var action in FileMenus.More)
        {
            if (FileMenus.StartsGroup(action))
            {
                more.Items.Add(new MenuFlyoutSeparator());
            }

            more.Items.Add(_contextItems[action] = ContextItem(action));
        }

        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(more);
        _contextMenu.Opening += OnContextMenuOpening;
        EntryList.ContextFlyout = _contextMenu;

        _compareBothItem.Click += OnCompareBothClicked;
        _compareLocalItem.Click += OnCompareLocalClicked;
        _compareMarkItem.Click += OnCompareMarkClicked;
        _compareMarkedItem.Click += OnCompareMarkedClicked;
    }

    // Ein Eintrag je Aktion. Was nur oben oder im Menü „⋯“ steht, gehört nicht hierher.
    private MenuFlyoutItemBase ContextItem(FileAction action)
    {
        MenuFlyoutItem Item(string text, RoutedEventHandler click, string? keys = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (keys is not null)
            {
                item.KeyboardAcceleratorTextOverride = keys;
            }

            item.Click += click;
            return item;
        }

        switch (action)
        {
            case FileAction.Open:
                _openItem.Click += OnOpenClicked;
                return _openItem;

            case FileAction.Compare:
                // Zwei Dateien im eingestellten Vergleichsprogramm: zwei gewählte, eine gegen eine lokale, oder eine
                // gegen die vorgemerkte (auch von einem anderen Server).
                var compare = new MenuFlyoutSubItem { Text = L.Get("CompareMenu") };
                foreach (var item in new[] { _compareBothItem, _compareLocalItem, _compareMarkItem, _compareMarkedItem })
                {
                    compare.Items.Add(item);
                }

                return compare;

            case FileAction.SendToServer:
                return _sendToServerMenu;

            case FileAction.CopyPathAs:
                return _copyPathAsMenu;
        }

        return action switch
        {
            FileAction.Download => Item(L.Get("Download"), OnDownloadClicked),
            FileAction.Rename => Item(L.Get("Rename"), OnRenameClicked, "F2"),
            FileAction.Delete => Item(L.Get("Delete"), OnDeleteClicked),
            FileAction.CopyPath => Item(L.Get("SftpCopyPath"), (_, _) => CopyPaths(PathLinkKind.Plain)),
            FileAction.Properties => Item(L.Get("Properties"), OnPropertiesClicked, "Alt+Enter"),
            FileAction.Preview => Item(L.Get("Preview"), OnPreviewClicked),
            FileAction.CopyFiles => Item(L.Get("CopyFiles"), OnCopyClicked, "Ctrl+C"),
            FileAction.Duplicate => Item(L.Get("ArrangeDuplicate"), OnDuplicateClicked, "Ctrl+D"),
            FileAction.Cut => Item(L.Get("ArrangeCut"), OnCutClicked, "Ctrl+X"),
            FileAction.Hold => Item(L.Get("ArrangeCopy"), OnHoldClicked),
            FileAction.Paste => Item(L.Get("ArrangePaste"), OnPasteClicked, "Ctrl+V"),
            FileAction.RenameMany => Item(L.Get("ArrangeRenameMany"), OnBatchRenameClicked),
            FileAction.Undo => Item(L.Get("ArrangeUndo"), OnUndoClicked, "Ctrl+Z"),
            FileAction.Pack => Item(L.Get("Pack"), OnPackClicked),
            FileAction.Unpack => Item(L.Get("Unpack"), OnUnpackClicked),
            FileAction.NewLink => Item(L.Get("NewLink"), OnNewLinkClicked),
            FileAction.Usage => Item(L.Get("ArrangeUsage"), OnUsageClicked),
            FileAction.Follow => Item(L.Get("FollowFile"), OnFollowClicked),
            FileAction.Sync => Item(L.Get("SyncFolder"), OnSyncFolderClicked),
            FileAction.OpenInTerminal => Item(L.Get("OpenInTerminal"), OnOpenInTerminalClicked),
            _ => throw new InvalidOperationException($"{action} has no place in the context menu"),
        };
    }

    private void OnContextMenuOpening(object? sender, object e)
    {
        var selection = Selection();
        var (commands, windows) = (Files?.HasCommands != false, Files?.IsWindowsServer == true);
        foreach (var (action, item) in _contextItems)
        {
            item.IsEnabled = FileMenus.IsEnabled(action, selection);
            item.Visibility = Ui.VisibleIf(FileMenus.IsShown(action, commands, windows));
        }

        // Ein Ordner wird geöffnet, eine Datei im Editor bearbeitet.
        _openItem.Text = Selected is [{ IsDirectory: true }] ? L.Get("SftpOpen") : L.Get("EditFile");
        _compareBothItem.IsEnabled = selection.TwoFiles;
        _compareLocalItem.IsEnabled = _compareMarkItem.IsEnabled = selection.SingleFile;
        _compareMarkedItem.IsEnabled = selection.SingleFile && selection.HasMarked;
        _compareMarkedItem.Text = Shell.CompareMarked is { } marked ? L.Get("CompareWithNamed", marked.Label) : L.Get("CompareWithMarked");
        FillSendToServerMenu(_sendToServerMenu.Items);
        FillCopyPathMenu(_copyPathAsMenu.Items);
    }

    // Öffnen bzw. Bearbeiten – wie ein Doppelklick, nur dass eine Datei gleich in den Editor geht.
    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is [{ IsDirectory: true }])
        {
            OpenSelected();
        }
        else
        {
            OnEditClicked(sender, e);
        }
    }

    // Ein Eintrag je anderer Sitzung, mit dem Ordner, den ihr Dateibrowser gerade zeigt – dorthin geht es.
    private void FillSendToServerMenu(IList<MenuFlyoutItemBase> menu)
    {
        menu.Clear();
        var targets = Shell.ActiveSession is { } from && Selected.Exists(entry => entry.IsTransferable) ? Shell.RelayTargets(from) : [];
        foreach (var target in targets)
        {
            var item = new MenuFlyoutItem { Text = L.Get("SendToServerTarget", target.Title, Format.Visible(target.Files.CurrentPath)), Tag = target };
            item.Click += OnSendToServerClicked;
            menu.Add(item);
        }

        if (targets.Count == 0)
        {
            menu.Add(new MenuFlyoutItem { Text = L.Get("SendToServerNone"), IsEnabled = false });
        }
    }

    private void OnSendToServerClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SessionViewModel target)
        {
            _ = SendToServerAsync(target);
        }
    }

    private async Task SendToServerAsync(SessionViewModel target)
    {
        var entries = Selected;
        if (Shell.ActiveSession is not { } from)
        {
            return;
        }

        // Dieselbe Rückfrage wie beim Hochladen, einmal für alles.
        var rule = ExistingRule.Overwrite;
        var existing = ShellViewModel.ExistingAt(target, entries);
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteRemoteText", string.Join(", ", existing.Select(Format.Visible))), App.Shell.Settings) is not { } chosen)
            {
                return;
            }

            rule = chosen;
        }

        // Ordner: Was nach den Ausschlüssen nicht mitginge, wird vorher gezeigt – und gefragt, was trotzdem mit soll.
        if (await App.Dialogs.AskExcludesAsync(Shell.ExcludeRules, rules => from.Files.FindExcludedAsync(rules, entries)) is not { } exclude)
        {
            return;
        }

        Shell.SendToServer(from, entries, target, rule, exclude);
    }

    // --- Suche auf dem Server ---

    private void OnSearchFilesClicked(object sender, RoutedEventArgs e)
    {
        if (Files is not { } files)
        {
            return;
        }

        if (files.IsSearchOpen)
        {
            files.CloseSearch();
            return;
        }

        files.OpenSearch();

        // Erst nach dem Einblenden lässt sich das Feld fokussieren.
        DispatcherQueue.TryEnqueue(() => SearchFilesBox.Focus(FocusState.Keyboard));
    }

    private void OnSearchFilesClosed(object sender, RoutedEventArgs e) => Files?.CloseSearch();

    // Jede Suche geht durch den Baum des Servers – nicht bei jedem Tastendruck, sondern nach einer kurzen Pause.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchDelay;

    private void OnSearchFilesTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDelay is null)
        {
            _searchDelay = DispatcherQueue.CreateTimer();
            _searchDelay.Interval = TimeSpan.FromMilliseconds(400);
            _searchDelay.IsRepeating = false;
            _searchDelay.Tick += (_, _) => _ = Files?.SearchAsync();
        }

        _searchDelay.Stop();
        if (Files is { IsSearchOpen: true })
        {
            _searchDelay.Start();
        }
    }

    private void OnSearchFilesKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                _ = Files?.SearchAsync();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                Files?.CloseSearch();
                e.Handled = true;
                break;
        }
    }

    // Ein Treffer: in seinen Ordner wechseln und ihn dort auswählen – in der Liste, in der alle Aktionen stehen.
    private async void OnHitClicked(object sender, ItemClickEventArgs e)
    {
        if (Files is { } files && e.ClickedItem is FileHit hit && await files.RevealAsync(hit) && files.SelectedEntry is { } entry)
        {
            EntryList.SelectedItem = entry;
            EntryList.ScrollIntoView(entry);
        }
    }

    // --- Sortierung und Spalten ---

    // Genau eine der drei Sortierungen ist angekreuzt; ein Klick auf die gewählte lässt sie gewählt.
    private void ShowSort()
    {
        SortNameItem.IsChecked = Shell.FileSort == FileSort.Name;
        SortSizeItem.IsChecked = Shell.FileSort == FileSort.Size;
        SortModifiedItem.IsChecked = Shell.FileSort == FileSort.Modified;
    }

    private void OnSortClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && Enum.TryParse<FileSort>(name, out var sort))
        {
            Shell.FileSort = sort;
        }

        ShowSort();
    }

    // Spalten: Breite und Sichtbarkeit setzt jede Zeile beim Entstehen und Wiederverwenden (ContainerContentChanging);
    // ändert sich etwas, ziehen die schon gebauten Zeilen an Ort und Stelle nach – die Liste selbst bleibt unberührt,
    // Auswahl und Rollstand bleiben. Beim Ziehen zeigen Kopf und Zeilen die Breite sofort (es sind nur die sichtbaren).
    private void OnColumnsChanged(object? sender, EventArgs e) => Ui.ApplyColumns(EntryList, Shell.RemoteColumns);

    private void OnColumnWidthDragged(object? sender, (FileColumn Column, double Width) drag)
        => Ui.ApplyColumnWidth(EntryList, drag.Column, drag.Width);

    private void OnRowChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        => Ui.ApplyColumnsToRow(args.ItemContainer, Shell.RemoteColumns);

    // --- Pfad kopieren, im Terminal öffnen ---

    // Der gewählte Eintrag, sonst das angezeigte Verzeichnis.
    private string? TargetPath(bool folderOnly)
    {
        if (Shell.ActiveSession?.Files is not { } files)
        {
            return null;
        }

        return Selected switch
        {
            [{ IsDirectory: true } folder] => files.PathOf(folder),
            [var entry] when !folderOnly => files.PathOf(entry),
            _ => files.CurrentPath,
        };
    }

    // Ein Eintrag je Art, die zum Profil passt (SSH: Pfad, sftp-Link, scp-Ziel; FTP: Pfad, FTP-Link).
    private void FillCopyPathMenu(IList<MenuFlyoutItemBase> menu)
    {
        menu.Clear();
        var kinds = Files?.Profile is { } profile ? PathLink.KindsFor(profile) : [PathLinkKind.Plain];
        foreach (var kind in kinds)
        {
            var text = kind switch
            {
                PathLinkKind.Sftp => L.Get("CopyPathSftp"),
                PathLinkKind.Scp => L.Get("CopyPathScp"),
                PathLinkKind.Ftp => L.Get("CopyPathFtp"),
                _ => L.Get("CopyPathPlain"),
            };
            var item = new MenuFlyoutItem { Text = text, Tag = kind };
            item.Click += OnCopyPathClicked;
            menu.Add(item);
        }
    }

    private void OnCopyPathClicked(object sender, RoutedEventArgs e)
        => CopyPaths((sender as FrameworkElement)?.Tag is PathLinkKind chosen ? chosen : PathLinkKind.Plain);

    // Mehrere gewählt: eine Zeile je Eintrag; ohne Auswahl das angezeigte Verzeichnis.
    private void CopyPaths(PathLinkKind kind)
    {
        if (Shell.ActiveSession?.Files is not { } files)
        {
            return;
        }

        var paths = Selected.Count > 1 ? Selected.ConvertAll(entry => files.PathOf(entry)) : (TargetPath(folderOnly: false) is { } one ? [one] : []);
        if (paths.Count == 0)
        {
            return;
        }

        var profile = files.Profile;
        var lines = paths.Select(path => profile is null ? PathLink.Plain(path) : PathLink.Format(profile, path, kind));
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(string.Join('\n', lines));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    // „cd“ in den gewählten Ordner – bei einer Datei oder ohne Auswahl in das angezeigte Verzeichnis.
    private void OnOpenInTerminalClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && TargetPath(folderOnly: true) is { } path)
        {
            Shell.OpenInTerminal(session, path);
        }
    }

    private void OnErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => Files?.DismissErrorCommand.Execute(null);

    private void OnUploadClicked(object sender, RoutedEventArgs e) => _ = UploadPickedAsync();

    private void OnUploadFolderClicked(object sender, RoutedEventArgs e) => _ = UploadFolderPickedAsync();

    private void OnDownloadClicked(object sender, RoutedEventArgs e) => _ = DownloadSelectedAsync();

    private void OnNewFolderClicked(object sender, RoutedEventArgs e) => _ = CreateFolderAsync();

    private void OnNewFileClicked(object sender, RoutedEventArgs e) => _ = CreateFileAsync();

    private void OnRenameClicked(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync();

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync();

    // Dasselbe wie ein Doppelklick auf die Datei: holen, im Editor öffnen, nach dem Speichern zurückschicken.
    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Selected is [{ IsDirectory: false, IsSpecial: false } entry])
        {
            Shell.EditRemoteFile(session, entry, session.Files.PathOf(entry));
        }
    }

    // Vorschau: eine Textdatei oder ein Bild ansehen, ohne einen Editor zu starten.
    private void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Selected is [{ IsDirectory: false, IsSpecial: false } entry])
        {
            Shell.OpenViewer(session, entry);
        }
    }

    private void OnPropertiesClicked(object sender, RoutedEventArgs e) => _ = ShowPropertiesAsync();

    private async Task ShowPropertiesAsync()
    {
        if (Files is { } files && Selected is [var entry]
            && await App.Dialogs.ShowPropertiesAsync(entry, files.PathOf(entry), () => files.FolderSizeTextAsync(entry), () => files.WithTargetAsync(entry), link => files.GoToTargetAsync(link)) is { } change)
        {
            await files.ApplyAsync(entry, change);
        }
    }

    // --- Pfad ---

    // Was das Feld zeigt, wenn niemand darin tippt: der Pfad des Servers, ohne Steuerzeichen (Format.Visible). Bleibt
    // er beim Abschicken unverändert, wird nicht gewechselt – so geht nie ein gesäuberter statt des echten Pfads hinaus.
    private string _shownPath = string.Empty;

    private void ShowPath()
    {
        _shownPath = Files is { } files ? Format.Visible(files.CurrentPath) : string.Empty;
        if (PathBox.FocusState == FocusState.Unfocused)
        {
            PathBox.Text = _shownPath;
        }
    }

    private void OnPathBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                GoToTypedPath();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                PathBox.Text = _shownPath;
                EntryList.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
        }
    }

    // Woanders hingeklickt: Die Eingabe verfällt, das Feld zeigt wieder, wo die Liste steht.
    private void OnPathBoxLostFocus(object sender, RoutedEventArgs e) => PathBox.Text = _shownPath;

    private void GoToTypedPath()
    {
        var typed = PathBox.Text;
        if (typed != _shownPath)
        {
            _ = Files?.GoToAsync(typed);
        }

        EntryList.Focus(FocusState.Programmatic);
    }

    private async Task UploadPickedAsync()
    {
        if (Files is not { } files)
        {
            return;
        }

        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add("*");
        var picked = await picker.PickMultipleFilesAsync();
        await UploadAsync(files, [.. picked.Select(file => file.Path)]);
    }

    // Der Dateidialog nimmt nur Dateien; ein ganzer Ordner geht über diesen Weg oder per Ziehen aus dem Explorer.
    private async Task UploadFolderPickedAsync()
    {
        if (Files is not { } files)
        {
            return;
        }

        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            await UploadAsync(files, [folder.Path]);
        }
    }

    private void OnSyncFolderClicked(object sender, RoutedEventArgs e) => _ = SyncFolderAsync();

    // Lokalen Ordner wählen, Richtung wählen, ansehen, was übertragen würde – erst dann geht etwas los.
    private async Task SyncFolderAsync()
    {
        if (Files is not { } files)
        {
            return;
        }

        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            await SyncWithAsync(folder.Path);
        }
    }

    /// <summary>
    /// Gleicht einen lokalen Ordner mit dem angezeigten ab – die Ansicht „Zwei Spalten“ nennt den ihrer linken Spalte.
    /// Wie immer: Richtung wählen, ansehen, was übertragen würde, erst dann geht etwas los.
    /// </summary>
    public async Task SyncWithAsync(string localFolder)
    {
        if (Files is not { IsAvailable: true } files
            || await App.Dialogs.AskSyncDirectionAsync(localFolder, files.CurrentPath, Shell.Settings.ExcludePatterns) is not { } chosen)
        {
            return;
        }

        // Mit einem Namen gemerkt – für den Server der Sitzung, zu der dieser Dateibrowser gehört.
        if (chosen.Name is { } name && Shell.Sessions.FirstOrDefault(session => ReferenceEquals(session.Files, files)) is { } owner)
        {
            Shell.SaveSync(name, owner.Profile, localFolder, files.CurrentPath, chosen.Direction, chosen.ByContent, chosen.Exclude);
        }

        if (await files.PlanSyncAsync(localFolder, chosen.Direction, chosen.ByContent, chosen.Exclude) is not { } plan)
        {
            return;
        }

        if (await App.Dialogs.ConfirmSyncAsync(plan) is { } deleteExtra)
        {
            await files.StartSyncAsync(plan, deleteExtra);
        }
    }

    private static async Task UploadAsync(SftpBrowserViewModel files, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // Einmal für alles: überschreiben, Gleiches oder nicht Älteres stehen lassen, oder das Neue umbenennen.
        var rule = ExistingRule.Overwrite;
        var existing = files.ExistingNames(paths.Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))));
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteRemoteText", string.Join(", ", existing)), App.Shell.Settings) is not { } chosen)
            {
                return;
            }

            rule = chosen;
        }

        if (await App.Dialogs.AskExcludesAsync(App.Shell.ExcludeRules, rules => ShellViewModel.FindExcludedLocallyAsync(rules, paths)) is not { } exclude)
        {
            return;
        }

        files.Upload(paths, rule, exclude);
    }

    private async Task DownloadSelectedAsync()
    {
        if (Files is not { } files || Selected.FindAll(entry => entry.IsTransferable) is not { Count: > 0 } entries)
        {
            return;
        }

        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };

        // Der Dialog beginnt dort, wohin zuletzt geladen wurde – solange es den Ordner noch gibt.
        if (Shell.Settings.LastDownloadFolder is { Length: > 0 } last && Directory.Exists(last))
        {
            picker.SuggestedStartFolder = last;
        }

        if (await picker.PickSingleFolderAsync() is not { } folder)
        {
            return;
        }

        Shell.Settings.LastDownloadFolder = folder.Path;

        var rule = ExistingRule.Overwrite;
        var existing = files.ExistingLocally(entries, folder.Path);
        if (existing.Count > 0)
        {
            if (await App.Dialogs.AskExistingAsync(L.Get("OverwriteLocalText", string.Join(", ", existing.Select(Format.Visible))), App.Shell.Settings) is not { } chosen)
            {
                return;
            }

            rule = chosen;
        }

        if (await App.Dialogs.AskExcludesAsync(Shell.ExcludeRules, rules => files.FindExcludedAsync(rules, entries)) is not { } exclude)
        {
            return;
        }

        files.Download(entries, folder.Path, rule, exclude);
    }

    // Eine leere Datei – zum Beispiel, um sie gleich danach im Editor zu füllen.
    private async Task CreateFileAsync()
    {
        if (Files is { } files
            && await App.Dialogs.AskTextAsync(L.Get("NewFile"), L.Get("NewFileLabel"), string.Empty, L.Get("Create"), RemotePath.IsValidName) is { } name)
        {
            await files.CreateFileAsync(name);
        }
    }

    private void OnNewLinkClicked(object sender, RoutedEventArgs e) => _ = CreateLinkAsync();

    // Ein symbolischer Link auf den gewählten Eintrag, im angezeigten Ordner.
    private async Task CreateLinkAsync()
    {
        if (Files is { } files && Selected is [var target]
            && await App.Dialogs.AskTextAsync(L.Get("NewLinkTitle", target.DisplayName), L.Get("NewLinkLabel"), string.Empty, L.Get("Create"), RemotePath.IsValidName) is { } name)
        {
            await files.CreateLinkAsync(target, name);
        }
    }

    private async Task CreateFolderAsync()
    {
        if (Files is { } files
            && await App.Dialogs.AskTextAsync(L.Get("NewFolder"), L.Get("NewFolderLabel"), string.Empty, L.Get("Create"), RemotePath.IsValidName) is { } name)
        {
            await files.CreateFolderAsync(name);
        }
    }

    // --- Ordnen auf dem Server ---

    private void OnCutClicked(object sender, RoutedEventArgs e) => Files?.Hold(Selected, cut: true);

    private void OnHoldClicked(object sender, RoutedEventArgs e) => Files?.Hold(Selected, cut: false);

    private void OnPasteClicked(object sender, RoutedEventArgs e) => _ = PasteHeldAsync();

    private void OnDuplicateClicked(object sender, RoutedEventArgs e) => _ = Files?.DuplicateAsync(Selected);

    private void OnUndoClicked(object sender, RoutedEventArgs e) => _ = Files?.UndoAsync();

    private void OnBatchRenameClicked(object sender, RoutedEventArgs e) => _ = RenameManyAsync();

    // Liegt am Ziel schon etwas mit dem Namen, entscheidet der Benutzer: ersetzen, auslassen oder gar nicht.
    private async Task PasteHeldAsync()
    {
        if (Files is not { CanPaste: true } files)
        {
            return;
        }

        var replace = false;
        if (files.PasteConflicts() is { Count: > 0 } existing)
        {
            var names = string.Join(", ", existing.Take(5).Select(Format.Visible)) + (existing.Count > 5 ? " …" : string.Empty);
            if (await App.Dialogs.ChooseAsync(L.Get("OverwriteTitle"), L.Get("OverwriteRemoteText", names), L.Get("Overwrite"), L.Get("ArrangeSkip")) is not { } answer)
            {
                return;
            }

            replace = answer;
        }

        await files.PasteAsync(replace);
    }

    private async Task RenameManyAsync()
    {
        if (Files is not { } files || Selected is not { Count: > 1 } entries)
        {
            return;
        }

        var rename = new BatchRenameViewModel([.. entries.Select(entry => entry.Name)], [.. files.Entries.Select(entry => entry.Name)]);
        if (await App.Dialogs.BatchRenameAsync(rename) is { Count: > 0 } changes)
        {
            await files.RenameManyAsync(changes);
        }
    }

    // --- Vergleichen ---

    private void OnCompareBothClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Files is { } files && Selected is [{ IsDirectory: false } left, { IsDirectory: false } right])
        {
            _ = Shell.CompareAsync(session, left, files.PathOf(left), session, right, files.PathOf(right));
        }
    }

    private void OnCompareMarkClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Selected is [{ IsDirectory: false } entry])
        {
            Shell.MarkForCompare(session, entry);
        }
    }

    private void OnCompareMarkedClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Selected is [{ IsDirectory: false } entry])
        {
            _ = Shell.CompareWithMarkedAsync(session, entry);
        }
    }

    private void OnCompareLocalClicked(object sender, RoutedEventArgs e) => _ = CompareWithLocalAsync();

    private async Task CompareWithLocalAsync()
    {
        if (Shell.ActiveSession is not { } session || Selected is not [{ IsDirectory: false } entry])
        {
            return;
        }

        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFileAsync() is { } file)
        {
            await Shell.CompareWithLocalAsync(session, entry, file.Path);
        }
    }

    // --- Als root (sudo) ---

    private void OnRootClicked(object sender, RoutedEventArgs e) => _ = ToggleRootAsync();

    // Hinein nur nach einer ausdrücklichen Bestätigung, die sagt, was das heißt; das Passwort wird nur gefragt,
    // wenn sudo es verlangt.
    private async Task ToggleRootAsync()
    {
        if (Files is not { } files || Shell.ActiveSession is not { } session)
        {
            return;
        }

        if (files.IsRoot)
        {
            await files.LeaveRootAsync();
            return;
        }

        if (!await App.Dialogs.ConfirmAsync(L.Get("RootTitle"), L.Get("RootText"), L.Get("RootConfirm")))
        {
            return;
        }

        if (await files.ElevateAsync() is null && await App.Dialogs.AskSudoPasswordAsync(session.Profile.Name) is { } password)
        {
            await files.ElevateAsync(password);
        }
    }

    // Mitlesen im unteren Bereich – ohne ein Terminal dafür zu belegen.
    private void OnFollowClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.ActiveSession is { } session && Selected is [{ IsDirectory: false, IsSpecial: false } entry])
        {
            Shell.FollowFile(session, entry);
        }
    }

    // Belegung: für den gewählten Ordner, sonst für das angezeigte Verzeichnis.
    private void OnUsageClicked(object sender, RoutedEventArgs e)
    {
        if (Files is { FileSystem: { } system } files)
        {
            var path = Selected is [{ IsDirectory: true } folder] ? files.PathOf(folder) : files.CurrentPath;
            _ = App.Dialogs.ShowUsageAsync(new FolderUsageViewModel(system, path));
        }
    }

    private void OnPackClicked(object sender, RoutedEventArgs e) => _ = PackSelectedAsync();

    private void OnUnpackClicked(object sender, RoutedEventArgs e)
    {
        if (Files is { } files && Selected is [var archive] && SftpBrowserViewModel.IsArchive(archive))
        {
            _ = files.UnpackAsync(archive);
        }
    }

    // Die gewählten Einträge auf dem Server zu einem Archiv packen; vorgeschlagen wird der Name des ersten.
    private async Task PackSelectedAsync()
    {
        var entries = Selected;
        if (Files is { } files && entries.Count > 0
            && await App.Dialogs.AskTextAsync(L.Get("PackTitle"), L.Get("PackLabel"), entries[0].Name, L.Get("Create"), RemotePath.IsValidName) is { } name)
        {
            await files.PackAsync(entries, name);
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (Files is { } files && Selected is [var entry]
            && await App.Dialogs.AskTextAsync(L.Get("Rename"), L.Get("RenameLabel"), entry.Name, L.Get("Rename"), RemotePath.IsValidName) is { } name
            && name != entry.Name)
        {
            await files.RenameAsync(entry, name);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (Files is not { } files || Selected is not { Count: > 0 } entries)
        {
            return;
        }

        var title = entries is [var single] ? L.Get("DeleteEntryTitle", single.DisplayName) : L.Get("DeleteEntriesTitle", entries.Count);
        if (await App.Dialogs.ConfirmAsync(title, L.Get("DeleteEntriesText"), L.Get("Delete")))
        {
            await files.DeleteAsync(entries);
        }
    }

    // --- In ein anderes Programm: ziehen oder kopieren ---

    // Ob gerade aus der Liste herausgezogen wird.
    private bool _draggingOut;

    private void OnCopyClicked(object sender, RoutedEventArgs e) => CopySelected();

    // Wie im Explorer: Die Einträge liegen in der Zwischenablage und lassen sich dort einfügen – dasselbe Angebot
    // wie beim Ziehen. Geladen wird erst beim Einfügen.
    private void CopySelected()
    {
        if (Shell.ActiveSession is not { } session || Shell.VirtualFilesForDrop(session, Selected) is not { } drop)
        {
            return;
        }

        // Dieselbe Auswahl lässt sich auch auf dem Server einfügen (Strg+V in einem anderen Ordner).
        Files?.Hold(Selected, cut: false);
        if (OleClipboard.Set(drop))
        {
            Shell.NoteCopied(session, drop.Count);
        }
        else
        {
            // Die Zwischenablage gehört gerade einem anderen Programm.
            drop.Completed(succeeded: false);
        }
    }

    // Die Liste erkennt nur die Geste. Gezogen wird mit dem Ziehen von Windows selbst (OleDrag), nicht mit dem von
    // WinUI: Nur so bekommt der Empfänger Ordner samt Inhalt, ohne dass sie vorher geladen sein müssen.
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Cancel = true;
        if (_draggingOut || Shell.ActiveSession is not { } session || Shell.VirtualFilesForDrop(session, e.Items.OfType<RemoteEntry>()) is not { } drop)
        {
            return;
        }

        _draggingOut = true;
        List<RemoteEntry> dragged = [.. e.Items.OfType<RemoteEntry>().Where(entry => entry.IsTransferable)];
        DraggedOut = dragged;

        // Erst nach diesem Ereignis: Das Ziehen kehrt erst zurück, wenn losgelassen ist.
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Losgelassen, und niemand hat es genommen: vielleicht über der lokalen Spalte.
                    if (!OleDrag.Run(drop, out var released) && released && dragged.Count > 0)
                    {
                        _ = Ole.GetCursorPos(out var point);
                        ReleasedUntaken?.Invoke(dragged, point);
                    }
                }
                finally
                {
                    _draggingOut = false;
                    DraggedOut = null;
                }
            }))
        {
            _draggingOut = false;
            DraggedOut = null;
            drop.Completed(succeeded: false);
        }
    }

    /// <summary>
    /// Die Einträge, die gerade aus einer Dateiliste des Servers gezogen werden; <c>null</c>, wenn nicht gezogen wird.
    /// Das Datenobjekt dieses Ziehens bietet dem eigenen Fenster nichts an – ein Ziel in der App (die lokale Spalte
    /// der Ansicht „Zwei Spalten“) erfährt hier, was unterwegs ist, und kann das Ablegen annehmen.
    /// </summary>
    internal static IReadOnlyList<RemoteEntry>? DraggedOut { get; private set; }

    /// <summary>
    /// Einträge wurden aus einer Dateiliste des Servers gezogen und losgelassen, ohne dass ein Empfänger sie nahm;
    /// dazu die Stelle auf dem Bildschirm. Dem eigenen Fenster bietet das Datenobjekt dieses Ziehens nichts an
    /// (WinUI meldet dort deshalb auch kein Ablegen) – wer hier ein Ziel sein will (die lokale Spalte der Ansicht
    /// „Zwei Spalten“), sieht selbst nach, ob die Stelle bei ihm liegt.
    /// </summary>
    internal static event Action<IReadOnlyList<RemoteEntry>, Ole.POINT>? ReleasedUntaken;

    /// <summary>Was in der Liste gewählt ist – für die lokale Spalte, die es zu sich laden kann.</summary>
    public IReadOnlyList<RemoteEntry> SelectedEntries => Selected;

    /// <summary>
    /// Eingebettet in die Ansicht „Zwei Spalten“: Dann gibt es kein „Bereich ausblenden“ – das schaltet dort die
    /// ganze Ansicht ab, nicht diese Spalte.
    /// </summary>
    public bool Embedded { get; set; }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (Files is { IsAvailable: true } files && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.Get("DropToUpload", files.CurrentPath);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Files is not { IsAvailable: true } files || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        // Das Ereignis muss offen bleiben, bis die Liste der Dateien geholt ist – aber nicht länger: Solange es
        // offen ist, wartet der Explorer, und die Rückfrage „überschreiben?“ kann dauern.
        List<string> paths = [];
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = [.. items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path))];
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
        finally
        {
            deferral.Complete();
        }

        await UploadAsync(files, paths);
    }
}
