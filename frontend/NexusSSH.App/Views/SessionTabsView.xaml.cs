using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class SessionTabsView : UserControl
{
    // Der „+“-Knopf samt Abstand zum letzten Reiter.
    private const double AddButtonWidth = 56;

    public SessionTabsView()
    {
        Strip = new TabStrip(App.Shell, action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeComponent();
        Shell.PropertyChanged += OnShellPropertyChanged;
        Tabs.AddHandler(TappedEvent, new TappedEventHandler(OnTabsTapped), handledEventsToo: true);

        // Wird ein Reiter verschoben – per Befehl oder mit der Maus –, baut die TabView ihn neu ein und verliert
        // dabei die Auswahl. Sie wird erst gelöscht und dann gesetzt: Nach dem Ziehen nennt die TabView oft noch
        // den richtigen Reiter als gewählt, zeigt aber keinen so an.
        Strip.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Remove)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    Tabs.SelectedItem = null;
                    Tabs.SelectedItem = Shell.ActiveSession;
                });
            }

            // Erst nach dem Layout steht fest, wo der letzte Reiter endet.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UsedWidthChanged?.Invoke(this, EventArgs.Empty));
        };
        Tabs.SizeChanged += (_, _) => UsedWidthChanged?.Invoke(this, EventArgs.Empty);
        HomeButton.SizeChanged += (_, _) => UsedWidthChanged?.Invoke(this, EventArgs.Empty);
        ShowHomeState();

        // Der Knopf bekommt seine Farbe im Code: nach einem Wechsel des Kontrastdesigns noch einmal.
        Ui.ContrastChanged += ShowHomeState;

        // Die Reiter zählen die Sekunden bis zum nächsten selbsttätigen Versuch herunter – im Sekundentakt, und
        // ohne Sitzung, die es gerade versucht, geschieht dabei nichts.
        var retries = DispatcherQueue.CreateTimer();
        retries.Interval = TimeSpan.FromSeconds(1);
        retries.Tick += (_, _) => Shell.RefreshRetries();
        retries.Start();
        Unloaded += (_, _) => retries.Stop();
        Loaded += (_, _) => retries.Start();
    }

    /// <summary><see cref="UsedWidth"/> hat sich geändert.</summary>
    public event EventHandler? UsedWidthChanged;

    /// <summary>
    /// So breit ist, was sich hier bedienen lässt: die Reiter und der „+“-Knopf dahinter. Die TabView selbst nimmt
    /// mehr Platz ein, sobald es mehrere Reiter gibt – die freie Fläche dahinter soll aber das Fenster ziehen.
    /// </summary>
    public double UsedWidth
    {
        get
        {
            // Ohne Reiter endet das Belegte hinter „Start“.
            var end = HomeButton.ActualWidth > 0 ? HomeButton.TransformToVisual(this).TransformPoint(default).X + HomeButton.ActualWidth : 0;
            if (Strip.Tabs.Count > 0 && Tabs.ContainerFromIndex(Strip.Tabs.Count - 1) is FrameworkElement last)
            {
                end = last.TransformToVisual(this).TransformPoint(default).X + last.ActualWidth;
            }

            return Math.Min(ActualWidth, end + AddButtonWidth);
        }
    }

    public ShellViewModel Shell => App.Shell;

    /// <summary>Die Liste für die Reiterleiste: Sie darf sie mit der Maus umsortieren.</summary>
    public TabStrip Strip { get; }

    // Auswahl in beide Richtungen von Hand abgleichen: SelectedItem ist object, ActiveSession ist typisiert,
    // und beim Schließen eines Reiters meldet die TabView kurzzeitig „nichts gewählt“.
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.ActiveSession))
        {
            return;
        }

        if (!ReferenceEquals(Tabs.SelectedItem, Shell.ActiveSession))
        {
            Tabs.SelectedItem = Shell.ActiveSession;
        }

        ShowHomeState();
    }

    private void OnHomeClicked(object sender, RoutedEventArgs e) => Shell.GoHome();

    // Ein Klick auf einen Reiter schließt Einstellungen und Hilfe – auch auf den, der schon aktiv ist (dann meldet die
    // TabView keinen Wechsel). Angemeldet auch für behandelte Klicks (der Reiter wählt sich selbst); ein Knopf darin –
    // der zum Schließen – zählt nicht.
    private void OnTabsTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, Tabs); element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return;
            }

            if (element is TabViewItem item)
            {
                if (item.DataContext is SessionViewModel session && Shell.Sessions.Contains(session))
                {
                    Shell.ShowSession(session);
                }

                return;
            }
        }
    }

    // „Start“ ist eingeschaltet, solange kein Tab aktiv ist: Symbol in der Akzentfarbe auf hellerer Fläche, wie die
    // Knöpfe rechts in der Titelleiste.
    private void ShowHomeState()
    {
        var home = Shell.HasNoActiveSession;
        HomeButton.Background = (Brush)Application.Current.Resources[home ? "ToggleOnBrush" : "SubtleFillColorTransparentBrush"];
        HomeButton.Foreground = (Brush)Application.Current.Resources[home ? "DraftAccentBrush" : "TitleIconBrush"];
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Beim Umsetzen eines Reiters wählt die TabView kurz seinen Nachbarn; das ist kein Wechsel des Tabs.
        // Steht der Tab schon in der Shell, kam der Wechsel von dort (ein Tab schloss, ein Befehl); sonst wählte der
        // Benutzer – mit Maus, Tastatur oder Sprachausgabe –, und Einstellungen und Hilfe gehen zu.
        if (!Strip.IsReordering && Tabs.SelectedItem is SessionViewModel session && Shell.Sessions.Contains(session)
            && !ReferenceEquals(session, Shell.ActiveSession))
        {
            Shell.ShowSession(session);
        }
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is SessionViewModel session)
        {
            Shell.CloseSession(session);
        }
    }

    // Rechtsklick auf einen Reiter: Die Befehle gelten der Sitzung unter dem Mauszeiger, also wird sie erst aktiv.
    // Ein Reiter wurde aus der Leiste heraus losgelassen: Sein Terminal bekommt ein eigenes Fenster, dort, wo der
    // Zeiger steht. Der Reiter selbst bleibt in der Leiste. Die TabView meldet das schon, wenn der Zeiger beim
    // Umsortieren ein paar Pixel unter die Reiter rutscht – so löste sich ein Terminal oft, ohne dass es gewollt
    // war (Meldung des Users, 2026-11-06). Gelöst wird deshalb nur, was deutlich weg ist (IsFarFromStrip).
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Item is SessionViewModel session && Shell.CanDetach(session) && IsFarFromStrip())
        {
            WindowBounds? bounds = null;
            if (Interop.TerminalWindows.PointerPosition() is { } pointer)
            {
                var size = session.DetachedBounds ?? Interop.TerminalWindows.DefaultSize;
                bounds = new WindowBounds(pointer.X - 120, pointer.Y - 20, size.Width, size.Height);
            }

            Shell.Detach(session, bounds);
        }
    }

    // Wie weit über oder unter der Reiterleiste ein Reiter losgelassen werden muss, damit sein Terminal ein eigenes
    // Fenster bekommt: gut zwei Zeilen der Titelleiste, also klar im Terminal.
    private const double DetachDistance = 100;

    // Losgelassen wurde außerhalb des Fensters (anderer Bildschirm) oder weit über bzw. unter der Leiste. Daneben in
    // der Titelleiste – Palette, Knöpfe – zählt nicht. Weiß das Fenster nicht, wo der Zeiger steht, bleibt der Reiter.
    private bool IsFarFromStrip()
    {
        if (XamlRoot is not { } root || Interop.TerminalWindows.PointerIn(root) is not { } pointer)
        {
            return false;
        }

        var strip = TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
        var near = pointer.X >= 0 && pointer.X <= root.Size.Width
            && pointer.Y >= strip.Top - DetachDistance && pointer.Y <= strip.Bottom + DetachDistance;
        return !near;
    }

    private void OnTabContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionViewModel session)
        {
            return;
        }

        Shell.ActiveSession = session;
        var menu = new MenuFlyout();
        foreach (var id in (string[])["terminal.splitRight", "terminal.splitDown", "terminal.unsplit", "session.rename", "session.duplicate", "session.pin", "session.unpin", "session.detach", "session.reattach", "session.disconnect", "session.reconnect", "session.keep", "session.disconnectAll", "session.reconnectAll", "terminal.save", "terminal.log", "terminal.broadcast", "session.moveLeft", "session.moveRight", "session.close", "session.closeOthers"])
        {
            if (Shell.Commands.FirstOrDefault(command => command.Id == id) is not { IsAvailable: true } command)
            {
                continue;
            }

            if (id is "session.rename" or "session.moveLeft" or "session.close" && menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var item = new MenuFlyoutItem { Text = command.Title, KeyboardAcceleratorTextOverride = command.Shortcut?.Display ?? string.Empty };
            item.Click += (_, _) => command.Run();
            menu.Items.Add(item);
        }

        if (args.TryGetPosition(sender, out var position))
        {
            menu.ShowAt(sender, new FlyoutShowOptions { Position = position });
        }
        else
        {
            menu.ShowAt((FrameworkElement)sender);
        }

        args.Handled = true;
    }

    // „+“ zeigt die Profile nach Gruppen (Spec §8).
    private void OnAddTabClicked(TabView sender, object args)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in Shell.ProfileGroups)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            menu.Items.Add(new MenuFlyoutItem { Text = group.Name, IsEnabled = false });
            foreach (var profile in group.Profiles)
            {
                var item = new MenuFlyoutItem
                {
                    Text = profile.Name,
                    KeyboardAcceleratorTextOverride = profile.Address,
                    Icon = new FontIcon { Glyph = "" },
                };
                item.Click += (_, _) => Open(profile);
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var add = new MenuFlyoutItem { Text = L.Get("AddServer"), Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => _ = AddServerAsync();
        menu.Items.Add(add);

        menu.ShowAt(Tabs, new FlyoutShowOptions { Position = AddButtonPosition() });
    }

    private void Open(ServerProfile profile) => Shell.OpenSession(profile);

    private async Task AddServerAsync()
    {
        if (await App.Dialogs.EditProfileAsync(null, Shell.GroupNames(), Shell.Profiles) is { } profile && Shell.SaveProfile(profile) is { } saved)
        {
            Shell.OpenSession(saved);
        }
    }

    // Die TabView gibt ihren „+“-Knopf nicht heraus; das Menü erscheint deshalb hinter dem letzten Reiter.
    private Windows.Foundation.Point AddButtonPosition()
    {
        double x = 0;
        if (Shell.Sessions.Count > 0 && Tabs.ContainerFromIndex(Shell.Sessions.Count - 1) is FrameworkElement last)
        {
            var origin = last.TransformToVisual(Tabs).TransformPoint(default);
            x = origin.X + last.ActualWidth;
        }

        return new Windows.Foundation.Point(x, Tabs.ActualHeight);
    }
}
