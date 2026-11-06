using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class ServerListView : UserControl
{
    private bool _cards;

    public ServerListView()
    {
        InitializeComponent();
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.HasProfiles) or nameof(ShellViewModel.HasNoHomeMatches) or nameof(ShellViewModel.IsInHomeFolder)
                or nameof(ShellViewModel.HomeAsTable) or nameof(ShellViewModel.HasNoTableMatches) or nameof(ShellViewModel.HomeTableSort) or nameof(ShellViewModel.HomeTableDescending))
            {
                ShowPresentation();
            }
        };
        ShowPresentation();
        ProfileGrid.SelectionChanged += (_, _) => ShowBulk();
        ProfileTable.SelectionChanged += (_, _) => ShowBulk();
        // Die Breite der Kacheln nach jedem Layout prüfen (gesetzt wird nur, wenn sie sich ändert): SizeChanged allein
        // kam zu früh (das Panel stand noch nicht) oder gar nicht, wenn sich das Fenster änderte, während die
        // Startseite verdeckt war – die Kacheln behielten die Breite eines kleineren Fensters (Gast, 2026-10-28).
        ProfileGrid.LayoutUpdated += (_, _) => FitTiles();

        // Erreichbarkeit: Die Kacheln lesen ihren Punkt beim Entstehen – nach einer Prüfung also neu aufbauen. Ebenso,
        // wenn ein Kontrastdesign kommt oder geht: Die Farben der Quadrate stehen im Code (Ui.TileFore).
        Shell.ReachabilityChanged += RebuildTiles;
        Ui.ContrastChanged += RebuildTiles;
        Loaded += (_, _) =>
        {
            StartProbing();
            FitTiles();
        };

        // Die Schalter im Menü „⋯“ hören auf ihren Zustand, nicht auf Click: UI Automation schaltet sie ohne Klick um.
        TableToggle.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            if (!_fillingMenu)
            {
                Shell.HomeAsTable = TableToggle.IsChecked;
            }
        });
        SelectToggle.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            if (!_fillingMenu && SelectToggle.IsChecked != _selecting)
            {
                SetSelecting(SelectToggle.IsChecked);
            }
        });
    }

    private void RebuildTiles()
    {
        if (_cards)
        {
            var source = ProfileGrid.ItemsSource;
            ProfileGrid.ItemsSource = null;
            ProfileGrid.ItemsSource = source;
            _hoveredTile = _focusedTile = null;
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _probeTimer;

    // Nur die Startseite prüft, und die Shell entscheidet selbst, ob es eingeschaltet, sichtbar und an der Zeit ist.
    private void StartProbing()
    {
        if (!_cards || _probeTimer is not null)
        {
            return;
        }

        _probeTimer = DispatcherQueue.CreateTimer();
        _probeTimer.Interval = TimeSpan.FromSeconds(30);
        _probeTimer.Tick += (_, _) => _ = Shell.ProbeReachabilityAsync();
        _probeTimer.Start();
        _ = Shell.ProbeReachabilityAsync();
    }

    /// <summary>
    /// Kacheln im Raster statt der Liste – für die Startseite, wo Platz ist. Der Aufklapper in der Titelleiste
    /// bleibt bei der Liste.
    /// </summary>
    public bool Cards
    {
        get => _cards;
        set
        {
            _cards = value;
            ShowPresentation();
        }
    }

    // Liste oder Kacheln – und keins von beiden, solange es kein Profil gibt (dann steht dort der Hinweis).
    private void ShowPresentation()
    {
        // Auf der Startseite Kacheln oder, wenn gewählt, die Tabelle; im Aufklapper der Titelleiste immer die Liste.
        var table = _cards && Shell.HomeAsTable;
        ProfileGrid.Visibility = Ui.VisibleIf(_cards && !table && Shell.HasProfiles);
        TablePanel.Visibility = Ui.VisibleIf(table && Shell.HasProfiles);
        TableToggle.Visibility = Ui.VisibleIf(_cards && Shell.HasProfiles);
        FilterPanel.Visibility = Ui.VisibleIf(_cards && Shell.HasProfiles);
        SelectToggle.Visibility = Ui.VisibleIf(_cards && Shell.HasProfiles);
        ShowBulk();
        NoMatchesText.Visibility = Ui.VisibleIf(_cards && (table ? Shell.HasNoTableMatches : Shell.HasNoHomeMatches));
        HomeCrumbBar.Visibility = Ui.VisibleIf(_cards && !table && Shell.IsInHomeFolder);
        ProfileList.Visibility = Ui.VisibleIf(!_cards && Shell.HasProfiles);
        ShowTableSort();
    }

    // Die Köpfe der Tabelle nennen ihre Spalte; an der sortierten steht ein Pfeil für die Richtung.
    private void ShowTableSort()
    {
        void Show(HyperlinkButton header, HomeTableSort column, string title)
        {
            var arrow = Shell.HomeTableSort != column ? string.Empty : Shell.HomeTableDescending ? "  ↓" : "  ↑";
            header.Content = title + arrow;
            AutomationProperties.SetName(header, title);
        }

        Show(TableNameHeader, HomeTableSort.Name, L.Get("ProfileName"));
        Show(TableAddressHeader, HomeTableSort.Address, L.Get("TableAddress"));
        Show(TableGroupHeader, HomeTableSort.Group, L.Get("Group"));
        Show(TableRecentHeader, HomeTableSort.Recent, L.Get("TableRecent"));
    }

    // Der Kopf nennt seine Spalte im Tag.
    private void OnTableHeaderClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && Enum.TryParse<HomeTableSort>(name, out var column))
        {
            Shell.SortHomeTable(column);
        }
    }

    private void OnProfileCardClicked(object sender, ItemClickEventArgs e) => Connect(e.ClickedItem as ServerProfile);

    // --- Mehrere auswählen ---

    private bool _selecting;

    // Kacheln oder Tabelle – was gerade zu sehen ist.
    private ListViewBase ShownList => Shell.HomeAsTable ? ProfileTable : ProfileGrid;

    // Jeder Server einmal: Ein Favorit steht auf der Startseite zweimal.
    private List<ServerProfile> Chosen() => [.. ShownList.SelectedItems.OfType<ServerProfile>().DistinctBy(profile => profile.Id)];

    private void SetSelecting(bool selecting)
    {
        _selecting = selecting;
        foreach (var list in (ListViewBase[])[ProfileGrid, ProfileTable])
        {
            // Solange ausgewählt wird, verbindet ein Klick nicht – er markiert.
            list.SelectionMode = _selecting ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            list.IsItemClickEnabled = !_selecting;
        }

        ShowBulk();
    }

    private void ShowBulk()
    {
        var active = _selecting && _cards && Shell.HasProfiles;
        BulkBar.Visibility = Ui.VisibleIf(active);
        if (!active)
        {
            return;
        }

        var count = Chosen().Count;
        BulkCount.Text = L.Get("BulkCount", count);
        foreach (var button in (Button[])[BulkConnectButton, BulkGroupButton, BulkTagsButton, BulkDeleteButton])
        {
            button.IsEnabled = count > 0;
        }
    }

    private void OnBulkAllClicked(object sender, RoutedEventArgs e) => ShownList.SelectAll();

    private void OnBulkConnectClicked(object sender, RoutedEventArgs e)
    {
        if (Chosen() is { Count: > 0 } chosen)
        {
            Leaving?.Invoke(this, EventArgs.Empty);
            Shell.ConnectAll(chosen);
        }
    }

    private async void OnBulkGroupClicked(object sender, RoutedEventArgs e)
    {
        if (Chosen() is { Count: > 0 } chosen && await App.Dialogs.AskGroupAsync(chosen.Count, Shell.GroupNames()) is { } group)
        {
            Shell.MoveToGroup(chosen, group);
        }
    }

    private async void OnBulkTagsClicked(object sender, RoutedEventArgs e)
    {
        if (Chosen() is { Count: > 0 } chosen && await App.Dialogs.AskTagsAsync(chosen.Count) is var (add, remove))
        {
            Shell.ChangeTags(chosen, add, remove);
        }
    }

    private async void OnBulkDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (Chosen() is { Count: > 0 } chosen && await App.Dialogs.ConfirmDeleteAsync(chosen))
        {
            Shell.DeleteProfiles(chosen);
        }
    }

    // --- Gruppen der Startseite ---

    private static ProfileGroup? GroupOf(object sender) => (sender as FrameworkElement)?.Tag as ProfileGroup;

    private void OnGroupToggleClicked(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } group)
        {
            Shell.ToggleGroup(group);
        }
    }

    private void OnGroupOpenClicked(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } group)
        {
            Shell.OpenHomeFolder(group.Path);
        }
    }

    private void OnHomeCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is HomeCrumb crumb)
        {
            Shell.OpenHomeFolder(crumb.Path);
        }
    }

    // „Nach oben“ und „Nach unten“ gibt es nur, wo es einen Nachbarn in dieser Richtung gibt.
    private void OnGroupMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout { Items: [MenuFlyoutItem up, MenuFlyoutItem down, ..] } menu && up.Tag is ProfileGroup group)
        {
            up.IsEnabled = Shell.CanMoveGroup(group, -1);
            down.IsEnabled = Shell.CanMoveGroup(group, 1);

            // Im Untermenü ist die Farbe gewählt, die an dieser Gruppe selbst eingestellt ist.
            var own = Shell.OwnGroupColor(group) ?? string.Empty;
            foreach (var color in menu.Items.OfType<MenuFlyoutSubItem>().SelectMany(sub => sub.Items).OfType<RadioMenuFlyoutItem>())
            {
                color.IsChecked = (color.Tag as string ?? string.Empty) == own;
            }

            if (menu.Items.OfType<MenuFlyoutSubItem>().LastOrDefault() is { } marks)
            {
                FillMarks(marks, Shell.OwnGroupMark(group), mark => Shell.SetGroupMark(group, mark));
            }
        }
    }

    // Der Eintrag nennt die Farbe in seinem Tag; die Gruppe steht am Untermenü darüber.
    private void OnGroupColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: var color, DataContext: ProfileGroup group })
        {
            Shell.SetGroupColor(group, color as string);
        }
    }

    private void OnGroupUpClicked(object sender, RoutedEventArgs e) => MoveGroup(sender, -1);

    private void OnGroupDownClicked(object sender, RoutedEventArgs e) => MoveGroup(sender, 1);

    private void MoveGroup(object sender, int delta)
    {
        if (GroupOf(sender) is { } group)
        {
            Shell.MoveGroup(group, delta);
        }
    }

    // „In Gruppe verschieben“: Die Gruppen stehen erst beim Öffnen des Menüs darin – es gibt sie nur als Namen an
    // den Profilen, und die ändern sich.
    private void OnProfileMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu
            || menu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault() is not { Tag: ServerProfile profile } move)
        {
            return;
        }

        var current = GroupPath.Normalize(profile.Group);
        move.Items.Clear();
        foreach (var path in Shell.GroupPaths.Prepend(string.Empty))
        {
            var item = new MenuFlyoutItem { Text = path.Length == 0 ? L.Get("Ungrouped") : path, IsEnabled = path != current };
            item.Click += (_, _) => Shell.MoveProfileToGroup(profile, path);
            move.Items.Add(item);
        }

        if (menu.Items.OfType<MenuFlyoutSubItem>().Skip(1).FirstOrDefault() is { } marks)
        {
            FillMarks(marks, Shell.OwnMark(profile), mark => Shell.SetMark(profile, mark));
        }
    }

    // Das Untermenü „Symbol“: von selbst (Betriebssystem bzw. das der Gruppe), eines der Symbole oder Buchstaben.
    // Angehakt ist, was an diesem Server oder dieser Gruppe selbst eingestellt ist.
    private void FillMarks(MenuFlyoutSubItem menu, string? own, Action<string?> set)
    {
        menu.Items.Clear();
        var none = new ToggleMenuFlyoutItem { Text = L.Get("TileMarkNone"), IsChecked = TileMarks.Normalize(own) is null };
        none.Click += (_, _) => set(null);
        menu.Items.Add(none);
        menu.Items.Add(new MenuFlyoutSeparator());

        foreach (var symbol in TileMarks.Symbols)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = symbol.Name,
                Icon = new FontIcon { Glyph = symbol.Glyph },
                IsChecked = TileMarks.Symbol(own) == symbol,
            };
            item.Click += (_, _) => set(TileMarks.OfSymbol(symbol));
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var letters = TileMarks.Letters(own);
        var text = new ToggleMenuFlyoutItem
        {
            Text = letters.Length > 0 ? L.Get("TileMarkLettersNow", letters) : L.Get("TileMarkLetters"),
            IsChecked = letters.Length > 0,
        };
        text.Click += async (_, _) =>
        {
            Leaving?.Invoke(this, EventArgs.Empty);
            if (await App.Dialogs.AskTextAsync(L.Get("TileMarkLettersTitle"), L.Get("TileMarkLettersLabel"), letters, L.Get("Save"), TileMarks.IsLetters) is { } chosen)
            {
                set(chosen);
            }
        };
        menu.Items.Add(text);
    }

    // Eine Kachel auf die Überschrift einer Gruppe ziehen legt das Profil dorthin. Die Favoriten sind keine Gruppe.
    private ServerProfile? _dragged;

    private void OnCardDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragged = e.Items.OfType<ServerProfile>().FirstOrDefault();
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        var fits = _dragged is not null && GroupOf(sender) is { Kind: not GroupKind.Favorites };
        e.AcceptedOperation = fits ? DataPackageOperation.Move : DataPackageOperation.None;
        e.Handled = true;
    }

    private void OnGroupDrop(object sender, DragEventArgs e)
    {
        if (_dragged is { } profile && GroupOf(sender) is { Kind: not GroupKind.Favorites } group)
        {
            Shell.MoveProfileToGroup(profile, group.Path);
        }

        _dragged = null;
        e.Handled = true;
    }

    /// <summary>Der Benutzer hat etwas ausgelöst, das die Liste verlässt (verbinden, Dialog) – ein Aufklapper schließt dann.</summary>
    public event EventHandler? Leaving;

    public ShellViewModel Shell => App.Shell;

    // Knöpfe und Menüeinträge einer Zeile tragen ihr Profil als Tag: Ein Menü hängt nicht im Baum der Zeile
    // und erbt deren DataContext nicht verlässlich.
    private static ServerProfile? ProfileOf(object sender) => (sender as FrameworkElement)?.Tag as ServerProfile;

    private void Connect(ServerProfile? profile)
    {
        if (profile is not null)
        {
            Leaving?.Invoke(this, EventArgs.Empty);
            Shell.OpenSession(profile);
        }
    }

    private void OnConnectClicked(object sender, RoutedEventArgs e) => Connect(ProfileOf(sender));

    private void OnProfileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Connect(ProfileList.SelectedItem as ServerProfile);

    private void OnProfileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ProfileList.SelectedItem is ServerProfile profile)
        {
            Connect(profile);
            e.Handled = true;
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => _ = EditAsync(null);

    // Eine Shell dieses Rechners: derselbe Weg wie Strg+Umschalt+T. Vorher schließt sich der Aufklapper der
    // Titelleiste; der neue Tab ist danach der aktive und zeigt das Terminal.
    private void OnLocalTerminalClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("session.newLocal");
    }

    // Aus der Einführung: die Palette öffnen, damit man sie einmal gesehen hat.
    private void OnIntroPaletteClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("view.palette");
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("server.import");
    }

    private void OnImportSessionsClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("server.importSessions");
    }

    private void OnImportSessionFileClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("server.importSessionFile");
    }

    private void OnImportDataClicked(object sender, RoutedEventArgs e)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        Shell.RunCommand("data.import");
    }

    private void OnEditClicked(object sender, RoutedEventArgs e) => _ = EditAsync(ProfileOf(sender));

    // Die Kopie ist ein neues Profil: Der Editor öffnet sich damit, gespeichert wird erst mit „Speichern“.
    private void OnDuplicateClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } profile)
        {
            _ = EditAsync(ShellViewModel.CopyOf(profile));
        }
    }

    private void OnForgetClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } profile)
        {
            _ = ForgetAsync(profile);
        }
    }

    // Das Menü „⋯“ im Kopf: Haken nach dem, was gerade gilt, und das Kürzel des lokalen Terminals, wie es belegt ist.
    // Die beiden Schalter hören auf ihren Zustand (siehe Konstruktor); hier wird nur gefüllt.
    private void OnHomeMoreMenuOpening(object? sender, object e)
    {
        _fillingMenu = true;
        TableToggle.IsChecked = Shell.HomeAsTable;
        SelectToggle.IsChecked = _selecting;
        _fillingMenu = false;

        SortSavedItem.IsChecked = Shell.ProfileSort == ProfileSort.Saved;
        SortNameItem.IsChecked = Shell.ProfileSort == ProfileSort.Name;
        SortRecentItem.IsChecked = Shell.ProfileSort == ProfileSort.Recent;
        LocalTerminalItem.KeyboardAcceleratorTextOverride = Shell.Commands.FirstOrDefault(command => command.Id == "session.newLocal")?.Shortcut?.Display ?? string.Empty;
    }

    private bool _fillingMenu;

    /// <summary>
    /// Für den Selbsttest: füllt das Menü „⋯“ und das einer Gruppe, wie beim Öffnen, und nennt, was fehlt – leer,
    /// wenn alles da ist.
    /// </summary>
    internal string ExerciseMenus()
    {
        OnHomeMoreMenuOpening(HomeMoreMenu, EventArgs.Empty);
        var missing = new List<string>();
        if (HomeMoreMenu.Items.Count < 6 || SortMenu.Items.Count != 3 || ImportMenu.Items.Count != 4)
        {
            missing.Add("the menu of the home page");
        }

        if (string.IsNullOrEmpty(LocalTerminalItem.KeyboardAcceleratorTextOverride))
        {
            missing.Add("the shortcut of the local terminal");
        }

        foreach (var kind in (Controls.LineIconKind[])[Controls.LineIconKind.ChevronRight, Controls.LineIconKind.ArrowRight])
        {
            if (!new Controls.LineIcon { Kind = kind }.HasShape)
            {
                missing.Add($"the line icon {kind}");
            }
        }

        // Die Logos der Betriebssysteme: Jeder Pfad wird unter Native AOT zu einer Geometrie, und eine Kachel mit
        // bekanntem System zeigt ihres (die Beispieldaten des Selbsttests haben solche Server).
        if (ServerSystems.Ids.FirstOrDefault(id => Ui.SystemIcon(id) is null) is { } lacking)
        {
            missing.Add($"the logo of {lacking}");
        }

        if (_cards && ProfileGrid.Visibility == Visibility.Visible && ProfileGrid.ActualWidth > 0
            && Shell.HomeGroups.SelectMany(group => group).Any(profile => TileMarks.Face(profile.Mark, profile.System, profile.GroupMark, profile.Name, profile.IsLocal).Kind == TileFace.System)
            && !ShownLogos(ProfileGrid).Any())
        {
            missing.Add("the logo of an operating system on a tile");
        }

        return string.Join(", ", missing);
    }

    // Die Logos, die auf den Kacheln zu sehen sind.
    private static IEnumerable<Microsoft.UI.Xaml.Shapes.Path> ShownLogos(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Microsoft.UI.Xaml.Shapes.Path { Tag: "system", Visibility: Visibility.Visible, Data: not null } logo)
            {
                yield return logo;
            }

            foreach (var inner in ShownLogos(child))
            {
                yield return inner;
            }
        }
    }

    // --- Kacheln: Breite, „⋯“ beim Überfahren und mit Fokus, Kontextmenü ---

    // Vier Spalten, die die Breite füllen (HomeTiles); der Platz einer Kachel schließt ihren Abstand ein.
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => FitTiles();

    // Das Panel der Kacheln, gemerkt beim Laden: Bei Gruppen lieferte ItemsPanelRoot es nicht, und die Kacheln
    // blieben so breit wie die erste (Gast, 2026-10-28).
    private ItemsWrapGrid? _tilePanel;

    private void OnTilePanelLoaded(object sender, RoutedEventArgs e)
    {
        _tilePanel = sender as ItemsWrapGrid;
        FitTiles();
    }

    private void FitTiles()
    {
        if ((_tilePanel ?? ProfileGrid.ItemsPanelRoot as ItemsWrapGrid) is { } wrap && ProfileGrid.ActualWidth > 0)
        {
            var slot = HomeTiles.Slot(ProfileGrid.ActualWidth);
            if (wrap.ItemWidth != slot)
            {
                wrap.ItemWidth = slot;
            }
        }
    }

    private Grid? _hoveredTile;
    private Grid? _focusedTile;

    private void OnTilePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var before = _hoveredTile;
        _hoveredTile = sender as Grid;
        ShowMore(before);
        ShowMore(_hoveredTile);
    }

    private void OnTilePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_hoveredTile, sender))
        {
            _hoveredTile = null;
        }

        ShowMore(sender as Grid);
    }

    // Fokus auf einer Kachel (oder ihrem „⋯“): Das Ereignis kommt vom GridViewItem herauf, die Kachel ist dessen Inhalt.
    private void OnGridFocusChanged(object sender, RoutedEventArgs e)
    {
        var before = _focusedTile;
        _focusedTile = FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && ItemOf(focused) is { ContentTemplateRoot: Grid tile } ? tile : null;
        ShowMore(before);
        ShowMore(_focusedTile);
    }

    private void ShowMore(Grid? tile)
    {
        if (tile?.Children.OfType<Button>().FirstOrDefault() is { } more)
        {
            more.Opacity = ReferenceEquals(tile, _hoveredTile) || ReferenceEquals(tile, _focusedTile) ? 1 : 0;
        }
    }

    // Rechtsklick, Menütaste oder Umschalt+F10 auf einer Kachel: dasselbe Menü wie „⋯“, an der Stelle des Klicks.
    private void OnGridContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source || ItemOf(source) is not { ContentTemplateRoot: Grid tile } item
            || tile.Children.OfType<Button>().FirstOrDefault() is not { Flyout: { } menu })
        {
            return;
        }

        args.Handled = true;
        if (args.TryGetPosition(item, out var point))
        {
            menu.ShowAt(item, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(item, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        }
    }

    private static GridViewItem? ItemOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is GridViewItem item)
            {
                return item;
            }
        }

        return null;
    }

    // Das Menü einer Gruppe hängt an ihrer Überschrift: Rechtsklick, Menütaste oder Umschalt+F10. Die Favoriten lassen
    // sich nicht ordnen und haben keines.
    private void OnGroupContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        if (sender is not Button { Tag: ProfileGroup { CanMove: true } } header || FlyoutBase.GetAttachedFlyout(header) is not { } menu)
        {
            return;
        }

        if (args.TryGetPosition(header, out var point))
        {
            menu.ShowAt(header, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(header, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        }
    }

    private void OnSortClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && Enum.TryParse<ProfileSort>(name, out var sort))
        {
            Shell.ProfileSort = sort;
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } profile)
        {
            _ = DeleteAsync(profile);
        }
    }

    private async Task EditAsync(ServerProfile? profile)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        if (await App.Dialogs.EditProfileAsync(profile, Shell.GroupNames(), Shell.Profiles) is { } edited)
        {
            Shell.SaveProfile(edited);
        }
    }

    private async Task ForgetAsync(ServerProfile profile)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        if (await App.Dialogs.ConfirmAsync(L.Get("ForgetPasswordTitle", profile.Name), L.Get("ForgetPasswordText"), L.Get("Forget")))
        {
            Shell.ForgetSecrets(profile);
        }
    }

    private async Task DeleteAsync(ServerProfile profile)
    {
        Leaving?.Invoke(this, EventArgs.Empty);
        if (await App.Dialogs.ConfirmDeleteAsync(profile))
        {
            Shell.DeleteProfile(profile);
        }
    }
}
