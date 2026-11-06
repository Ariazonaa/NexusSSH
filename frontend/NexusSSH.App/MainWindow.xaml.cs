using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.App.Controls;
using NexusSSH.App.Interop;
using NexusSSH.Core.Backend.Native;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Eigene Titelleiste mit Reitern und Suche; die Fensterknöpfe bleiben die von Windows (Snap-Layouts
        // funktionieren). Die ganze Zeile zieht das Fenster – bis auf das, was sich darin bedienen lässt.
        ExtendsContentIntoTitleBar = true;
        AppTitleBar.Loaded += (_, _) => PlacePalette();
        AppTitleBar.SizeChanged += (_, _) => PlacePalette();
        TabsPane.UsedWidthChanged += (_, _) => PlacePalette();

        // Die Knöpfe rechts gibt es nur mit einer Sitzung: Kommen oder gehen sie, rückt die Palette, und die
        // Aussparungen der Titelleiste müssen neu gemeldet werden – sonst zögen die Knöpfe das Fenster.
        TitleButtons.SizeChanged += (_, _) => PlacePalette();
        ShowPaletteShortcut();
        RestorePlace();

        WireSplitter(SftpSplitter, () => Settings.SftpWidth, value => Settings.SftpWidth = value, AppSettings.DefaultSftpWidth, direction: 1);
        WireSplitter(SnippetsSplitter, () => Settings.InspectorWidth, value => Settings.InspectorWidth = value, AppSettings.DefaultSnippetsWidth, direction: -1);
        WireSplitter(BottomSplitter, () => Settings.BottomHeight, value => Settings.BottomHeight = value, AppSettings.DefaultBottomHeight, direction: -1);

        Shell.PropertyChanged += OnShellPropertyChanged;
        Shell.UiRequested += OnUiRequested;

        // Die Vorschau eines gemerkten Abgleichs ist dieselbe wie beim ersten Mal.
        Shell.ConfirmSync = plan => App.Dialogs.ConfirmSyncAsync(plan);
        AppWindow.Closing += OnAppWindowClosing;

        // Hinweise, wenn im Hintergrund etwas fertig wird oder die Glocke läutet (die Shell fragt nur, wenn es
        // eingeschaltet ist; hier wird entschieden, ob das Fenster gerade vorn ist).
        // Nicht mit Beispieldaten und nicht im Selbsttest: Deren Transfers laufen von selbst und würden melden.
        if (!App.IsDemo)
        {
            Interop.Attention.Register();
            Shell.AttentionRequested += (title, text) => DispatcherQueue.TryEnqueue(() =>
                Interop.Attention.Show(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), title, text));
            Closed += (_, _) => Interop.Attention.Unregister();

            // Töne zu Fehlern und Ereignissen; welche es gibt, entscheidet die Shell.
            Shell.SoundRequested += (sound, onlyInBackground) => DispatcherQueue.TryEnqueue(() =>
                Interop.Attention.Play(
                    Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id),
                    NexusSSH.Core.Settings.SoundEvents.ToneOf(sound),
                    onlyInBackground));
        }

        // Laufende tmux-/screen-Sitzungen eines Servers: Der Benutzer wählt, woran er anhängt und was künftig gilt.
        Shell.MuxOffered += (session, running) => DispatcherQueue.TryEnqueue(() => _ = Logged(OfferMuxAsync(session, running)));

        // Die Glocke des Terminals, wenn der Benutzer sie hören oder in der Taskleiste sehen will.
        Shell.BellRang += (sound, flash) => DispatcherQueue.TryEnqueue(() =>
            Interop.Attention.Ring(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), sound, flash));

        // Der Fortschritt aller Transfers im Symbol der Taskleiste (der Abgleich läuft im Thread der Oberfläche).
        var taskbar = new Interop.TaskbarProgress(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        Shell.Transfers.Synced += () => taskbar.Show(Shell.Transfers.Overall);
        Activated += (_, _) => taskbar.Show(Shell.Transfers.Overall);

        // Die Lücken zwischen den Karten hängen an der gewählten Dichte (Ui.Gap), wie die Trenner darin.
        PaneArea.Margin = BottomPane.Margin = SettingsPane.Margin = ViewerPane.Margin = LogsPane.Margin = DetailsPane.Margin = new Thickness(Ui.Gap, 0, Ui.Gap, 0);

        ApplyLayout();
        ApplyAnimations();
        AddShortcuts();
        Ui.ApplyTerminalBackground(Shell.TerminalTheme.Background);

        // Schaltet Windows ein Kontrastdesign ein oder aus, während die App läuft: Die Flächen folgen von selbst
        // (ThemeResource), was im Code eine Farbe bekam, wird hier nachgezogen. Nachgesehen wird alle paar Sekunden
        // statt auf eine Meldung zu warten – die von Windows dafür (HighContrastChanged, ColorValuesChanged) kamen
        // in der App nicht an (in der Sandbox gesehen, 2026-10-13).
        Ui.DesignChanged();
        _designTimer = DispatcherQueue.CreateTimer();
        _designTimer.Interval = TimeSpan.FromSeconds(3);
        _designTimer.Tick += (_, _) =>
        {
            if (Ui.DesignChanged())
            {
                Ui.ApplyContrast();
                Ui.ApplyTerminalBackground(Shell.TerminalTheme.Background);
            }
        };
        _designTimer.Start();
        Closed += (_, _) => _designTimer.Stop();

        // Symbol für Taskleiste und Alt+Tab. Fehlt die Datei, bleibt es beim Standardsymbol.
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "NexusSSH.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        // Der Inhalt liegt in einer eigenen Insel: Nur die hat einen Maßstab, der sich setzen lässt (UiZoom).
        _scaled = new Interop.ScaledContent(this, Root, Shell.UiZoom);
        ApplyUiZoom();
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: true);
    }

    private readonly Interop.ScaledContent _scaled;

    /// <summary>Die Wurzel der Oberfläche – das Fenster selbst hat keinen Inhalt mehr (<see cref="Interop.ScaledContent"/>).</summary>
    public XamlRoot? ContentRoot => Root.XamlRoot;

    /// <summary>
    /// Führt etwas aus, sobald die Oberfläche steht (Dialoge brauchen ihre <see cref="ContentRoot"/>). Nachgesehen
    /// wird alle 200 ms, höchstens eine halbe Minute: Auf <c>Loaded</c> der Wurzel war kein Verlass – sie liegt in
    /// einer eigenen Insel (<see cref="Interop.ScaledContent"/>), und der Rückruf kam beim ersten Start nie an.
    /// </summary>
    public void WhenLoaded(Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        var tries = 0;
        timer.Tick += (sender, _) =>
        {
            if (ContentRoot is null && ++tries < 150)
            {
                return;
            }

            sender.Stop();
            _waiting.Remove(sender);
            if (ContentRoot is not null)
            {
                action();
            }
        };
        _waiting.Add(timer);
        timer.Start();
    }

    // Die Zeitgeber müssen jemandem gehören, sonst räumt die Speicherbereinigung sie vor dem Ablauf weg.
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _waiting = [];

    /// <summary>
    /// Holt das Fenster nach vorn – nur, weil der Benutzer es eben verlangt hat (ein Link, die Sprungliste): Der
    /// zweite Start hat dafür mit <c>AllowSetForegroundWindow</c> sein Recht auf den Vordergrund weitergegeben. Sonst
    /// nimmt sich die App den Vordergrund nie selbst.
    /// </summary>
    public void BringToFront()
    {
        if (App.IsNoActivate)
        {
            return;
        }

        if (Ui.Overlapped(AppWindow) is { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    // Strg+Mausrad außerhalb des Terminals. Über dem Terminal gehört es der Seite (sie ändert dessen Schrift) –
    // das WebView reicht das Rad aber auch hierher weiter (gemessen 2026-10-09), deshalb wird es ausgenommen.
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is WebView2)
            {
                return;
            }
        }

        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) && delta != 0)
        {
            Shell.ZoomUi(Math.Sign(delta));
            e.Handled = true;
        }
    }

    private void ApplyUiZoom()
    {
        _scaled.SetZoom(Shell.UiZoom);

        // Die Fensterknöpfe gehören Windows und wachsen nicht mit: Unter 100 % wäre die Zeile niedriger als sie.
        AppWindow.TitleBar.PreferredHeightOption = Shell.UiZoom < 100 ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;
        if (Ui.Overlapped(AppWindow) is { } presenter)
        {
            var factor = Math.Max(100, Shell.UiZoom) / 100.0;
            presenter.PreferredMinimumWidth = (int)(900 * factor);
            presenter.PreferredMinimumHeight = (int)(560 * factor);
        }

        PlacePalette();
    }

    public ShellViewModel Shell => App.Shell;

    private AppSettings Settings => Shell.Settings;

    // Splitter melden nur die Zugstrecke. direction = +1, wenn Ziehen nach rechts/unten den Bereich vergrößert
    // (SFTP links), −1 für Bereiche rechts bzw. unten.
    private void WireSplitter(PaneSplitter splitter, Func<double> get, Action<double> set, double fallback, int direction)
    {
        double start = 0;
        splitter.DragStarted += (_, _) => start = get();
        splitter.DragDelta += (_, delta) =>
        {
            set(Math.Clamp(start + (direction * delta), AppSettings.MinPaneSize, AppSettings.MaxPaneSize));
            ApplyLayout();
        };
        splitter.ResetRequested += (_, _) =>
        {
            set(fallback);
            ApplyLayout();
        };
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Auch der Wechsel zur Startseite und zurück ändert, welche Bereiche zu sehen sind.
            case nameof(ShellViewModel.IsSftpVisible) or nameof(ShellViewModel.IsSnippetsVisible) or nameof(ShellViewModel.IsBottomPanelVisible) or nameof(ShellViewModel.ActiveSession) or nameof(ShellViewModel.IsDualFiles):
                ApplyLayout();
                break;

            // Nur das Terminal: alles andere weicht, und das Fenster füllt den Bildschirm.
            case nameof(ShellViewModel.IsTerminalOnly):
                ApplyTerminalOnly();
                break;

            case nameof(ShellViewModel.UiZoom):
                ApplyUiZoom();
                break;

            // Der Benutzer hat Tastenkürzel geändert.
            case nameof(ShellViewModel.Commands):
                AddShortcuts();
                ShowPaletteShortcut();
                break;

            // Die Einstellungen liegen als Seite über den Bereichen; zu geht der Fokus zurück ins Terminal.
            case nameof(ShellViewModel.IsSettingsOpen):
                SettingsPane.Visibility = Ui.VisibleIf(Shell.IsSettingsOpen);
                ApplyLayout();
                if (Shell.IsSettingsOpen)
                {
                    DispatcherQueue.TryEnqueue(SettingsPane.FocusFirst);
                }
                else if (Shell.HasActiveSession)
                {
                    Shell.FocusTerminal();
                }

                break;

            // Die Hilfe ebenso.
            case nameof(ShellViewModel.IsHelpOpen):
                HelpPane.Visibility = Ui.VisibleIf(Shell.IsHelpOpen);
                ApplyLayout();
                if (Shell.IsHelpOpen)
                {
                    DispatcherQueue.TryEnqueue(HelpPane.FocusFirst);
                }
                else if (Shell.HasActiveSession && !Shell.IsSettingsOpen)
                {
                    Shell.FocusTerminal();
                }

                break;

            // Der Betrachter liegt wie die Einstellungen über den Bereichen.
            case nameof(ShellViewModel.Viewer):
                ViewerPane.Visibility = Ui.VisibleIf(Shell.Viewer is not null);
                ApplyLayout();
                if (Shell.Viewer is not null)
                {
                    DispatcherQueue.TryEnqueue(ViewerPane.FocusFirst);
                }
                else if (Shell.HasActiveSession)
                {
                    Shell.FocusTerminal();
                }

                break;

            // Ebenso die Seiten eines Servers: seine Protokolle und seine Einzelheiten.
            case nameof(ShellViewModel.Logs):
            case nameof(ShellViewModel.Details):
                ShowServerPages(e.PropertyName);
                break;

            case nameof(ShellViewModel.Animations):
                ApplyAnimations();
                break;
        }
    }

    // Die Seiten eines Servers (Protokolle, Einzelheiten) liegen über den Bereichen. Geht eine auf, bekommt sie den
    // Fokus; geht sie zu, geht er zurück ins Terminal.
    private void ShowServerPages(string? changed)
    {
        LogsPane.Visibility = Ui.VisibleIf(Shell.Logs is not null);
        DetailsPane.Visibility = Ui.VisibleIf(Shell.Details is not null);
        ApplyLayout();
        switch (changed)
        {
            case nameof(ShellViewModel.Logs) when Shell.Logs is not null:
                DispatcherQueue.TryEnqueue(LogsPane.FocusFirst);
                break;

            case nameof(ShellViewModel.Details) when Shell.Details is not null:
                DispatcherQueue.TryEnqueue(DetailsPane.FocusFirst);
                break;

            default:
                if (!Shell.HasServerPage && Shell.HasActiveSession && !Shell.IsSettingsOpen && !Shell.IsHelpOpen)
                {
                    Shell.FocusTerminal();
                }

                break;
        }
    }

    // Was eingeblendet wird, erscheint weich statt hart – wenn es eingestellt ist.
    private void ApplyAnimations()
    {
        foreach (var pane in (UIElement[])[SftpPane, SnippetsPane, BottomPane, SettingsPane, ViewerPane, LogsPane, DetailsPane])
        {
            Ui.FadeIn(pane, Shell.Animations);
        }

        TerminalPane.ApplyAnimations(Shell.Animations);
    }

    // Ein ausgeblendeter Bereich gibt seine Spalte bzw. Zeile ganz frei, sein Splitter verschwindet mit.
    private void ApplyLayout()
    {
        SftpColumn.Width = Size(Shell.ShowsFiles, Settings.SftpWidth);
        SftpPane.Visibility = SftpSplitter.Visibility = Ui.VisibleIf(Shell.ShowsFiles);

        SnippetsColumn.Width = Size(Shell.ShowsSnippets, Settings.InspectorWidth);
        SnippetsPane.Visibility = SnippetsSplitter.Visibility = Ui.VisibleIf(Shell.ShowsSnippets);

        // Was unter einer Seite liegt (Einstellungen, Hilfe, Betrachter), ist nicht zu sehen – dann darf es auch
        // für die Tab-Taste und eine Sprachausgabe nicht da sein: Sonst wanderte der Fokus hinter die Seite, und
        // Narrator läse Knöpfe vor, die es dort gerade nicht gibt. Das Terminal bleibt dabei bestehen, wie unter
        // „Zwei Spalten“ – nur ausgeblendet.
        var covered = Shell.IsSettingsOpen || Shell.IsHelpOpen || Shell.Viewer is not null || Shell.HasServerPage;
        PaneArea.Visibility = Ui.VisibleIf(!covered);

        BottomRow.Height = Size(Shell.ShowsBottom, Settings.BottomHeight);
        BottomPane.Visibility = BottomSplitter.Visibility = Ui.VisibleIf(Shell.ShowsBottom && !covered);
    }

    // „Nur das Terminal“: Titelleiste, Hinweis auf ein Update und Statusleiste weichen (die Bereiche daneben
    // blendet ApplyLayout aus), die Karte des Terminals reicht bis an den Rand, und das Fenster nimmt den ganzen
    // Bildschirm. Zurück geht es in den Zustand von vorher – auch ein maximiertes Fenster ist danach wieder maximiert.
    private void ApplyTerminalOnly()
    {
        var only = Shell.IsTerminalOnly;
        AppTitleBar.Visibility = StatusBar.Visibility = Ui.VisibleIf(!only);
        UpdateBar.Visibility = Ui.VisibleIf(!only);
        PaneArea.Margin = only ? new Thickness(0) : new Thickness(Ui.Gap, 0, Ui.Gap, 0);
        ApplyLayout();

        // Im Selbsttest und ohne Aktivierung bleibt das Fenster, wie es ist: Ein Prüflauf soll den Bildschirm nicht belegen.
        var wanted = only && !App.IsUnattended && !App.IsNoActivate ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default;
        if (AppWindow.Presenter.Kind != wanted && (only || AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen))
        {
            AppWindow.SetPresenter(wanted);
        }

        UpdateTitleBarRegions();
    }

    private bool _maximizeAtStart;

    // Wo das Fenster zuletzt stand.
    private void RestorePlace()
    {
        var maximized = Settings.WindowMaximized;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Settings.WindowWidth, Settings.WindowHeight));
        if (Settings is { WindowX: { } x, WindowY: { } y })
        {
            var saved = new Windows.Graphics.RectInt32(x, y, Settings.WindowWidth, Settings.WindowHeight);
            var work = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest).WorkArea;
            var fitted = WindowPlacement.Fit(
                new WindowBounds(x, y, saved.Width, saved.Height),
                new WindowBounds(work.X, work.Y, work.Width, work.Height));
            var place = new Windows.Graphics.RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height);

            // Zweimal: Liegt der Platz auf einem Bildschirm mit anderer Skalierung, ändert Windows beim Umzug die Größe.
            AppWindow.MoveAndResize(place);
            AppWindow.MoveAndResize(place);
        }

        _maximizeAtStart = maximized;
    }

    /// <summary>
    /// Maximiert das Fenster, wenn es zuletzt so war. Erst nach dem ersten Zeigen: Davor setzt das Aktivieren es zurück
    /// (in der Sandbox gesehen, 2026-10-31).
    /// </summary>
    public void RestoreMaximized()
    {
        if (_maximizeAtStart && Ui.Overlapped(AppWindow) is { } presenter)
        {
            presenter.Maximize();
        }
    }

    // Beim Schließen, nicht bei jeder Änderung: Die Meldung AppWindow.Changed kam für Größe und Platz nicht an (in der
    // Sandbox gesehen; beim User stand dadurch immer die Vorgabe). Gemerkt wird das normale Fenster, damit „Wiederherstellen“
    // dorthin zurückführt. Bildschirmfüllend zählt nichts davon, minimiert bleibt „maximiert“, wie es war.
    private void RememberPlace()
    {
        if (Ui.Overlapped(AppWindow) is not { State: var state })
        {
            return;
        }

        var normal = state == OverlappedPresenterState.Restored
            ? new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height)
            : NormalPlace.Of(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        if (normal is { IsUsable: true })
        {
            (Settings.WindowX, Settings.WindowY) = (normal.X, normal.Y);
            (Settings.WindowWidth, Settings.WindowHeight) = (normal.Width, normal.Height);
        }

        if (state != OverlappedPresenterState.Minimized)
        {
            Settings.WindowMaximized = state == OverlappedPresenterState.Maximized;
        }
    }

    // So schmal wird die Palette höchstens, wenn viele Reiter offen sind oder das Fenster schmal ist.
    private const double MinPaletteWidth = 240;

    private const double PaletteWidth = 420;

    /// <summary>
    /// Stellt die Palette mittig in den Platz hinter dem letzten Reiter, wie im Bild. Werden es so viele Reiter, dass
    /// sie nicht mehr passt, wird sie schmaler – bis <see cref="MinPaletteWidth"/>, danach rollen die Reiter. Die
    /// Aussparungen der Titelleiste folgen, sobald die neue Lage steht.
    /// </summary>
    private void PlacePalette()
    {
        var room = AppTitleBar.ColumnDefinitions[0].ActualWidth;
        if (room > 0)
        {
            TabsPane.MaxWidth = Math.Max(0, room - MinPaletteWidth - 8);
            var used = Math.Min(TabsPane.UsedWidth, TabsPane.MaxWidth);
            var width = Math.Clamp(room - used - 16, MinPaletteWidth, PaletteWidth);
            PaletteBox.Width = width;
            PaletteBox.Margin = new Thickness(Math.Max(used + 8, used + ((room - used - width) / 2)), 0, 0, 0);
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarRegions);
    }

    // Das Kürzel der Palette rechts im Feld – so, wie es gerade belegt ist.
    private void ShowPaletteShortcut()
        => PaletteShortcutText.Text = Shell.Commands.FirstOrDefault(command => command.Id == "view.palette")?.Shortcut?.Display ?? string.Empty;

    /// <summary>
    /// Sagt dem Fenster, wo in der Titelleiste bedient statt gezogen wird: Reiter, Befehlspalette, Knöpfe. Alles
    /// andere in der Zeile – auch die freie Fläche hinter dem letzten Reiter – zieht das Fenster. Die Rechtecke
    /// gelten in Pixeln, die Oberfläche rechnet in DIPs.
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;

        // Platz für Minimieren, Maximieren, Schließen; vor dem ersten Anzeigen nennt Windows ihn noch nicht.
        if (AppWindow.TitleBar.RightInset > 0)
        {
            CaptionInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        }

        var regions = new List<Windows.Graphics.RectInt32>();
        foreach (var element in (FrameworkElement[])[TabsPane, PaletteBox, TitleButtons])
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            // Von den Reitern zählt nur, was belegt ist – dahinter wird gezogen.
            var width = ReferenceEquals(element, TabsPane) ? TabsPane.UsedWidth : element.ActualWidth;
            regions.Add(Region(element, width));
        }

        // Auch die Zeile selbst wird ausdrücklich genannt, statt sich auf die Vorgabe des Fensters zu verlassen:
        // Die TabView schreibt die Liste der Ziehbereiche bei jeder Änderung an ihren Reitern neu und behält nur,
        // was darin steht – ohne diesen Eintrag ließe sich das Fenster danach nicht mehr an der Titelleiste ziehen.
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Caption, [Region(AppTitleBar, AppTitleBar.ActualWidth)]);
        source.SetRegionRects(NonClientRegionKind.Passthrough, [.. regions]);

        Windows.Graphics.RectInt32 Region(FrameworkElement element, double width)
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, width, element.ActualHeight));
            return new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale));
        }
    }

    // Sieht nach, ob Windows das Kontrastdesign gewechselt hat.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _designTimer;

    // Der Hinweis auf eine neue Version: X schließt ihn, der Knopf öffnet die Release-Seite im Browser.
    private void OnUpdateDismissed(InfoBar sender, object args) => Shell.DismissUpdate();

    private void OnOpenReleaseClicked(object sender, RoutedEventArgs e)
    {
        // Die Adresse kommt aus einer Antwort aus dem Netz: geöffnet wird nur eine Seite bei GitHub, über https.
        if (Shell.AvailableUpdate?.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri is { Scheme: "https", Host: "github.com", UserInfo.Length: 0 })
        {
            _ = Launcher.LaunchUriAsync(uri);
        }

        Shell.DismissUpdate();
    }

    // --- Tastenkürzel ---

    // Die Kürzel der Befehle gelten im ganzen Fenster. Im Terminal kommen sie hier nicht an – der WebView schluckt
    // Tasten –, dort meldet die Terminal-Seite sie selbst (TerminalHost).
    private void AddShortcuts()
    {
        // Auch nach einer Änderung in den Einstellungen: erst alle weg, dann die, die jetzt gelten.
        Root.KeyboardAccelerators.Clear();
        foreach (var command in Shell.Commands)
        {
            if (command.Shortcut is not { } chord || ToVirtualKey(chord.Code) is not { } key)
            {
                continue;
            }

            var accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = chord.Shift ? VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift : VirtualKeyModifiers.Control,
            };
            accelerator.Invoked += (_, args) => args.Handled = !KeepsArrows(chord) && Shell.RunCommand(command.Id);
            Root.KeyboardAccelerators.Add(accelerator);
        }

        // F11 allein: nur das Terminal, wie im Browser und in Windows Terminal. Im Terminal meldet die Seite die Taste.
        var only = new KeyboardAccelerator { Key = VirtualKey.F11 };
        only.Invoked += (_, args) => args.Handled = Shell.RunCommand("view.terminalOnly");
        Root.KeyboardAccelerators.Add(only);

        // Außerhalb des Terminals wechselt auch F6 allein den Bereich, wie im Explorer. Im Terminal kommt die Taste
        // hier nicht an – dort gehört sie dem Programm auf dem Server.
        foreach (var (modifiers, command) in new[] { (VirtualKeyModifiers.None, "view.nextPane"), (VirtualKeyModifiers.Shift, "view.previousPane") })
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.F6, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = Shell.RunCommand(command);
            Root.KeyboardAccelerators.Add(accelerator);
        }

        // Ohne das zeigte jedes Element im Fenster alle Kürzel als Tooltip an.
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // Strg+Umschalt+Pfeil markiert in einem Textfeld Wort für Wort und in einer Liste einen Bereich: Hat so ein
    // Element den Fokus, bleibt ein Kürzel mit Pfeil bei ihm, statt im Terminal zum Nachbarn zu springen.
    private bool KeepsArrows(KeyChord chord)
    {
        if (!chord.Code.StartsWith("Arrow", StringComparison.Ordinal))
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ListViewBase or TreeView)
            {
                return true;
            }
        }

        return false;
    }

    // KeyboardEvent.code → Taste, wie XAML sie nennt.
    internal static VirtualKey? ToVirtualKey(string code) => code switch
    {
        "Tab" => VirtualKey.Tab,
        ['F', .. var number] when int.TryParse(number, out var index) && index is >= 1 and <= 12 => VirtualKey.F1 + (index - 1),
        "PageUp" => VirtualKey.PageUp,
        "PageDown" => VirtualKey.PageDown,
        "ArrowLeft" => VirtualKey.Left,
        "ArrowRight" => VirtualKey.Right,
        "ArrowUp" => VirtualKey.Up,
        "ArrowDown" => VirtualKey.Down,
        "Equal" => (VirtualKey)0xBB,
        "Minus" => (VirtualKey)0xBD,
        ['K', 'e', 'y', var letter] when letter is >= 'A' and <= 'Z' => VirtualKey.A + (letter - 'A'),
        ['D', 'i', 'g', 'i', 't', var digit] when char.IsAsciiDigit(digit) => VirtualKey.Number0 + (digit - '0'),
        _ => null,
    };

    // --- Befehlspalette ---

    private void OnUiRequested(UiRequest request)
    {
        switch (request)
        {
            case UiRequest.FocusCommandBar:
                // Die Palette steht in der Titelleiste – die gibt es „nur mit dem Terminal“ nicht.
                Shell.IsTerminalOnly = false;
                CommandBox.Focus(FocusState.Keyboard);
                break;

            case UiRequest.AddServer:
                _ = AddServerAsync();
                break;

            case UiRequest.ImportSshConfig:
                _ = ImportSshConfigAsync();
                break;

            case UiRequest.ExportData:
                _ = Logged(ExportDataAsync());
                break;

            case UiRequest.ImportSessions:
                _ = Logged(ImportSessionsAsync(ForeignSessionReader.ReadInstalled()));
                break;

            case UiRequest.ImportSessionFile:
                _ = Logged(ImportSessionFileAsync());
                break;

            case UiRequest.RenameTab:
                _ = Logged(RenameTabAsync());
                break;

            case UiRequest.CreateKey:
                _ = Logged(CreateKeyAsync());
                break;

            case UiRequest.ImportData:
                _ = Logged(ImportDataAsync());
                break;

            case UiRequest.SaveDiagnostics:
                _ = Logged(SaveDiagnosticsAsync());
                break;

            // Der Ordner des Transfer-Protokolls, im Explorer – ein fester Pfad der App, nichts vom Server.
            case UiRequest.ShowTransferLog:
                if (Shell.TransferLogFolder() is { } folder)
                {
                    _ = Launcher.LaunchFolderPathAsync(folder);
                }

                break;

            case UiRequest.ShowLoadHistory when Shell.ActiveSession is { } measured:
                _ = Logged(App.Dialogs.ShowLoadHistoryAsync(measured, AppWindow.Id));
                break;

            case UiRequest.FocusNextPane:
                MovePaneFocus(+1);
                break;

            case UiRequest.FocusPreviousPane:
                MovePaneFocus(-1);
                break;
        }
    }

    // --- Von Bereich zu Bereich ---

    // Welcher Bereich an der Reihe ist, sagt die Shell; hier wird nur nachgesehen, wo der Fokus liegt, und er
    // dorthin gesetzt.
    private void MovePaneFocus(int delta) => FocusPane(Shell.PaneAfter(PaneWithFocus(), delta));

    // Der Bereich, in dem das Element mit dem Fokus liegt; null in der Titelleiste oder einem Menü.
    private ShellPane? PaneWithFocus()
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            ShellPane? pane = element switch
            {
                _ when ReferenceEquals(element, SftpPane) => ShellPane.Files,
                _ when ReferenceEquals(element, TerminalPane) => ShellPane.Terminal,
                _ when ReferenceEquals(element, SnippetsPane) => ShellPane.Snippets,
                _ when ReferenceEquals(element, BottomPane) => ShellPane.Bottom,
                _ => null,
            };
            if (pane is not null)
            {
                return pane;
            }
        }

        return null;
    }

    private void FocusPane(ShellPane pane)
    {
        // Liegen die zwei Spalten über dem Terminal, sind sie „der Terminal-Bereich“.
        if (pane == ShellPane.Terminal && Shell.ShowsDualFiles && TerminalPane.FocusDualFiles())
        {
            return;
        }

        // Mit einer Sitzung ist „der Terminal-Bereich“ das Terminal selbst, nicht der erste Reiter darüber.
        if (pane == ShellPane.Terminal && Shell.HasActiveSession)
        {
            Shell.FocusTerminal();
            return;
        }

        if (pane == ShellPane.Files && SftpPane.FocusList())
        {
            return;
        }

        if (pane == ShellPane.Snippets && SnippetsPane.FocusSearch())
        {
            return;
        }

        UserControl view = pane switch
        {
            ShellPane.Files => SftpPane,
            ShellPane.Snippets => SnippetsPane,
            ShellPane.Bottom => BottomPane,
            _ => TerminalPane,
        };
        if (FocusManager.FindFirstFocusableElement(view) is UIElement first)
        {
            first.Focus(FocusState.Keyboard);
            return;
        }

        // Ein Bereich ohne Bedienelemente (etwa einer, der nur Text zeigt) nimmt den Fokus selbst – sonst käme man
        // mit der Tastatur nicht hindurch, und ein Bildschirmleser fände ihn nicht.
        view.IsTabStop = true;
        view.UseSystemFocusVisuals = true;
        view.Focus(FocusState.Keyboard);
    }

    private void OnCommandBoxGotFocus(object sender, RoutedEventArgs e) => ShowSuggestions();

    private void OnCommandBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Das Kürzel weicht dem Getippten.
        PaletteShortcutText.Visibility = Ui.VisibleIf(sender.Text.Length == 0);

        // Auch Text, den eine Bedienhilfe einträgt (Spracheingabe, Bildschirmleser), zählt als Eingabe – nur das
        // eigene Leeren des Felds nach einem ausgeführten Befehl nicht.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput
            || (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange && sender.Text.Length > 0))
        {
            ShowSuggestions();
        }
    }

    private void OnCommandBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter ohne Auswahl nimmt den ersten Treffer.
        var item = args.ChosenSuggestion as PaletteItem ?? Shell.SearchPalette(args.QueryText).FirstOrDefault();
        if (item is null)
        {
            return;
        }

        sender.Text = string.Empty;
        sender.IsSuggestionListOpen = false;

        // Zurück ins Terminal – und zwar **vor** dem Befehl: Holt der Befehl den Fokus selbst (Suchleiste, Dialog),
        // kommt das danach. Umgekehrt endete die App, sobald ein Befehl einen Datei-Dialog von Windows öffnete
        // (Exportieren, Importieren, Diagnosepaket): Der Fokus ging ins WebView, während der Dialog das Fenster
        // schon gesperrt hatte – ohne Meldung, Rückgabewert 0xC0000409 (gefunden 2026-10-11).
        if (Shell.HasActiveSession)
        {
            Shell.FocusTerminal();
        }

        item.Run();
    }

    /// <summary>
    /// Für den Selbsttest (<c>--self-test</c>): benutzt, was Listen aus Core an XAML gibt und erst auf Zuruf
    /// entsteht – die Vorschläge der Palette, ein zweites Terminal, das Menü der Profile. Dazu der Wechsel von
    /// Bereich zu Bereich: Ob der Fokus ankommt, zeigt sich nur an der laufenden Oberfläche.
    /// </summary>
    internal void ExerciseForSelfTest()
    {
        // Die Seiten der Startseite: Jede baut ihre Listen erst auf, wenn sie zu sehen ist – auch die Überschriften
        // der Gruppen und die Brotkrumen einer geöffneten Gruppe.
        var session = Shell.ActiveSession;

        // Die Linien-Symbole: Ihre Formen liest WinUI erst zur Laufzeit aus Text – unter AOT ein eigener Weg.
        foreach (var kind in Enum.GetValues<LineIconKind>())
        {
            if (!new LineIcon { Kind = kind }.HasShape)
            {
                throw new InvalidOperationException($"self-test: the line icon {kind} has no shape");
            }
        }

        // Das Menü „Teilen“ der Titelleiste entsteht aus den Befehlen.
        if (session is not null && TitleButtons.ExerciseSplitMenu() == 0)
        {
            throw new InvalidOperationException("self-test: the split menu is empty");
        }

        // Die Hilfe-Seite mit ihren drei Listen.
        Shell.IsHelpOpen = true;
        Root.UpdateLayout();
        Shell.IsHelpOpen = false;

        // Nur das Terminal, hin und zurück: Titelleiste und Statusleiste weichen und kommen wieder (das Fenster
        // bleibt im Selbsttest, wie es ist).
        if (Shell.CanBeTerminalOnly)
        {
            Shell.IsTerminalOnly = true;
            Root.UpdateLayout();
            if (AppTitleBar.Visibility == Visibility.Visible || StatusBar.Visibility == Visibility.Visible)
            {
                throw new InvalidOperationException("self-test: terminal-only left the title bar or the status bar");
            }

            Shell.IsTerminalOnly = false;
            Root.UpdateLayout();
            if (AppTitleBar.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("self-test: the title bar did not come back");
            }
        }

        // Das Diagnosepaket: ZIP und JSON-Schreiber gehen unter Native AOT eigene Wege – einmal wirklich schreiben.
        var diagnostics = Path.Combine(Core.AppDataLocation.Root, "self-test", "diagnose.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(diagnostics)!);
        if (!Shell.SaveDiagnostics(diagnostics, "self-test", App.IsDemo) || new FileInfo(diagnostics).Length == 0)
        {
            throw new InvalidOperationException("self-test: the diagnostics package was not written");
        }

        // Einmal größer und zurück: Der Inhalt liegt in einer eigenen Insel, deren Maßstab sich dabei ändert.
        Shell.UiZoom = AppSettings.MaxUiZoom;
        Root.UpdateLayout();
        Shell.UiZoom = AppSettings.DefaultUiZoom;
        Root.UpdateLayout();
        foreach (var page in Enum.GetValues<HomePage>())
        {
            Shell.ShowHome(page);
            Root.UpdateLayout();
        }

        Shell.ShowHome(HomePage.Servers);
        if (Shell.GroupPaths is [var group, ..])
        {
            Shell.OpenHomeFolder(group);
            Root.UpdateLayout();
            Shell.OpenHomeFolder(string.Empty);
        }

        // Die Startseite als Tabelle: eine eigene Liste aller Server, nach jeder Spalte einmal sortiert.
        var table = Shell.HomeAsTable;
        Shell.ShowHome();
        Shell.HomeAsTable = true;
        foreach (var column in Enum.GetValues<HomeTableSort>())
        {
            Shell.SortHomeTable(column);
            Root.UpdateLayout();
        }

        Shell.SortHomeTable(HomeTableSort.Name);
        Shell.HomeAsTable = table;
        Root.UpdateLayout();

        // Das Menü „⋯“ der Startseite (Tabelle, Auswahl, Reihenfolge, Importieren, lokales Terminal).
        if (TerminalPane.ExerciseHomeMenus() is { Length: > 0 } lacking)
        {
            throw new InvalidOperationException($"self-test: the home page lacks {lacking}");
        }

        Shell.ActiveSession = session;
        Root.UpdateLayout();

        // Ein lokales Terminal (mit Beispieldaten ohne Shell dahinter): sein Tab mit dem Symbol, keine Dateien, keine
        // Auslastung – und die Einstellungen mit der Liste der Shells. Die echte Shell prüft der Selbsttest mit
        // Verbindung (LiveSelfTest.LocalTerminalAsync).
        if (Shell.OpenLocalTerminal("cmd") is { } local)
        {
            Root.UpdateLayout();
            if (Shell.ShowsFiles || Shell.CanShowDetails || Shell.ShowsMonitorBar)
            {
                throw new InvalidOperationException("self-test: a local terminal shows files or server info");
            }

            Shell.CloseSession(local);
        }

        Shell.IsSettingsOpen = true;
        Root.UpdateLayout();
        Shell.IsSettingsOpen = false;
        Shell.ActiveSession = session;
        Root.UpdateLayout();

        // Die zwei Spalten: die lokale Liste samt Brotkrumen und eine zweite Dateiliste des Servers.
        var dual = Shell.IsDualFiles;
        Shell.IsDualFiles = true;
        Shell.Local.NavigateTo(LocalBrowserViewModel.Computer);
        Root.UpdateLayout();
        Shell.Local.NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Root.UpdateLayout();

        // Die Spaltenköpfe der Listen: jeden einmal drücken, eine Spalte wählen, eine Breite setzen.
        SftpPane.ExerciseColumnHeaders();
        SftpPane.ExerciseMenus();
        TerminalPane.ExerciseColumnHeaders();
        Root.UpdateLayout();
        Shell.IsDualFiles = dual;
        Root.UpdateLayout();

        // Die Zeile unten: Transfers ein und wieder aus.
        Shell.ToggleTransfers();
        Root.UpdateLayout();
        Shell.ToggleTransfers();
        Root.UpdateLayout();

        // Die Spalte der Snippets: eingeblendet, einmal gefiltert und zurück (ihre Liste wird dabei ersetzt).
        var snippets = Shell.IsSnippetsVisible;
        Shell.IsSnippetsVisible = true;
        Shell.SnippetFilter = "x";
        Root.UpdateLayout();
        Shell.SnippetFilter = string.Empty;
        Root.UpdateLayout();
        Shell.IsSnippetsVisible = snippets;

        // Die Seite „Server-Details“ mit ihren Listen (Kerne, Dateisysteme, Netz, Platten, Tunnel) – auf und zu.
        if (Shell.CanShowDetails)
        {
            Shell.ShowDetails();
            Root.UpdateLayout();
            if (DetailsPane.Visibility != Visibility.Visible || PaneArea.Visibility == Visibility.Visible)
            {
                throw new InvalidOperationException("self-test: the server details did not cover the panes");
            }

            Shell.CloseDetails();
            Root.UpdateLayout();
        }

        CommandBox.Text = ">";
        ShowSuggestions();
        CommandBox.IsSuggestionListOpen = false;
        CommandBox.Text = string.Empty;
        Shell.Split(sideBySide: true);
        Shell.IsBottomPanelVisible = true;
        Shell.Log("self-test", LogSeverity.Warning);

        (Shell.IsSftpVisible, Shell.IsSnippetsVisible) = (true, true);
        // Ins Terminal setzt die App den Fokus nur, wenn ihr Fenster vorn ist (TerminalHost.FocusWebView) – der
        // Selbsttest läuft ohne Aktivierung, und ein anderes Fenster kann vorn sein. Deshalb geht der Lauf vom
        // erwarteten Bereich aus weiter und prüft das Terminal nur, wenn das Fenster vorn ist.
        FocusPane(ShellPane.Files);
        var previous = ShellPane.Files;
        var inFront = Interop.Attention.IsInFront(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        foreach (var (delta, expected) in new[] { (+1, ShellPane.Terminal), (+1, ShellPane.Snippets), (+1, ShellPane.Bottom), (+1, ShellPane.Files), (-1, ShellPane.Bottom), (-1, ShellPane.Snippets) })
        {
            FocusPane(Shell.PaneAfter(previous, delta));
            previous = expected;
            if (expected == ShellPane.Terminal && !inFront)
            {
                continue;
            }

            if (PaneWithFocus() != expected)
            {
                throw new InvalidOperationException($"Pane focus: expected {expected}, got {PaneWithFocus()?.ToString() ?? "none"}.");
            }
        }
    }

    // Wie viele Rahmen und Trennlinien die Terminal-Seite zeigt und an wie vielen Rahmen die Marke des Selbsttests
    // noch hängt – ein Rahmen, der beim Umbauen neu entstünde, hätte keine.
    private const string CountPanes = """
        (() => {
            const shown = element => element.getClientRects().length > 0;
            const frames = [...document.querySelectorAll('.terminal-frame')];
            return [frames.filter(shown).length, [...document.querySelectorAll('.divider')].filter(shown).length,
                frames.filter(frame => frame.dataset.selfTest).length].join('/');
        })()
        """;

    // Zieht die erste Trennlinie der obersten Teilung um 40 Punkte nach rechts – Ereignisse wie von einer Maus.
    private const string DragFirstDivider = """
        (() => {
            const line = document.querySelector('#terminals > .split > .divider');
            if (!line) {
                return 'no divider';
            }

            const box = line.getBoundingClientRect();
            const x = box.left + (box.width / 2);
            const at = (left, buttons) => ({ bubbles: true, cancelable: true, composed: true, pointerId: 41, pointerType: 'mouse',
                isPrimary: true, button: 0, buttons, clientX: left, clientY: box.top + (box.height / 2) });
            line.dispatchEvent(new PointerEvent('pointerdown', at(x, 1)));
            line.dispatchEvent(new PointerEvent('pointermove', at(x + 40, 1)));
            line.dispatchEvent(new PointerEvent('pointerup', at(x + 40, 0)));
            return box.width > 0 ? 'ok' : 'no size';
        })()
        """;

    // In [a|[b/c]|[d/[e|f]]]: zieht den Kopf von b an die linke Kante von e.
    private const string DragHeaderOfB = """
        (() => {
            const parts = node => [...node.children].filter(child => !child.classList.contains('divider'));
            const top = document.querySelector('#terminals > .split');
            const b = parts(parts(top)[1])[0];
            const e = parts(parts(parts(top)[2])[1])[0];
            const bar = b.querySelector('.terminal-header');
            const head = bar.getBoundingClientRect();
            const box = e.getBoundingClientRect();
            const at = (x, y, buttons) => ({ bubbles: true, cancelable: true, composed: true, pointerId: 42, pointerType: 'mouse',
                isPrimary: true, button: 0, buttons, clientX: x, clientY: y });
            bar.dispatchEvent(new PointerEvent('pointerdown', at(head.left + (head.width / 2), head.top + (head.height / 2), 1)));
            bar.dispatchEvent(new PointerEvent('pointermove', at(box.left + 4, box.top + (box.height / 2), 1)));
            bar.dispatchEvent(new PointerEvent('pointerup', at(box.left + 4, box.top + (box.height / 2), 0)));
            return box.width > 0 && head.width > 0 ? 'ok' : 'no size';
        })()
        """;

    /// <summary>
    /// Für den Selbsttest: sechs Terminals als <c>[a|[b/c]|[d/[e|f]]]</c>, geprüft an der Terminal-Seite selbst –
    /// sechs sichtbare Rahmen und fünf Trennlinien, und keiner der Rahmen entstand beim Umbauen neu (eine Marke, die
    /// vorher an jedem hing, ist noch da); groß geschaltet steht einer allein; eine Trennlinie und ein Kopf, per
    /// Skript gezogen, kommen als neue Anteile bzw. als Umstellung in der Shell an. Nennt, woran es scheiterte.
    /// </summary>
    internal async Task<string?> ExerciseSplitsAsync()
    {
        if (TerminalHost.Current is not { } page || Shell.ActiveSession is not { } first)
        {
            return "no terminal page or no session";
        }

        // Die Seite antwortet erst, wenn sie steht.
        if (!await UntilAsync(async () => await page.EvaluateAsync("1") == "1", attempts: 200))
        {
            return "the terminal page did not load";
        }

        // Die Terminals brauchen Platz: Die zwei Spalten lägen über ihnen.
        var dual = Shell.IsDualFiles;
        Shell.IsDualFiles = false;
        foreach (var other in Shell.Layout.Panes.Where(pane => !ReferenceEquals(pane, first)).ToList())
        {
            Shell.Unsplit(other);
        }

        // Erst sechs Tabs, dann jeden Rahmen der Seite markieren, dann teilen.
        var tabs = new List<SessionViewModel> { first };
        for (var added = 1; added < 6; added++)
        {
            tabs.Add(Shell.OpenSession(first.Profile));
        }

        Shell.ActiveSession = first;
        var frames = Shell.Sessions.Count(session => !session.IsDetached).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!await UntilAsync(async () => await page.EvaluateAsync("document.querySelectorAll('.terminal-frame').length") == frames))
        {
            return "the page did not open a terminal for every tab";
        }

        await page.EvaluateAsync("document.querySelectorAll('.terminal-frame').forEach((frame, index) => frame.dataset.selfTest = String(index + 1))");

        Shell.SplitWith(tabs[1], sideBySide: true);
        Shell.ActiveSession = tabs[1];
        Shell.SplitWith(tabs[3], sideBySide: true);
        Shell.SplitWith(tabs[2], sideBySide: false);
        Shell.ActiveSession = tabs[3];
        Shell.SplitWith(tabs[4], sideBySide: false);
        Shell.ActiveSession = tabs[4];
        Shell.SplitWith(tabs[5], sideBySide: true);
        if (SplitShape(Shell.Layout.Tree, tabs) is not "[a|[b/c]|[d/[e|f]]]" and var built)
        {
            return $"the split became {built}";
        }

        if (await PageShowsAsync(page, $"6/5/{frames}") is { } six)
        {
            return $"six panes: {six}";
        }

        Shell.ToggleZoom();
        if (await PageShowsAsync(page, $"1/0/{frames}") is { } zoomed)
        {
            return $"zoomed: {zoomed}";
        }

        Shell.ToggleZoom();
        if (await PageShowsAsync(page, $"6/5/{frames}") is { } back)
        {
            return $"after the zoom: {back}";
        }

        // Die Seite meldet die Anteile beim Loslassen; die Shell nimmt sie (Summe 1) und schickt sie zurück.
        var before = ((SplitBranch<SessionViewModel>)Shell.Layout.Tree!).Shares[0];
        if (await page.EvaluateAsync(DragFirstDivider) is not "\"ok\"" and var dragged)
        {
            return $"dragging a divider: {dragged}";
        }

        if (!await UntilAsync(() => Task.FromResult(Shell.Layout.Tree is SplitBranch<SessionViewModel> { Shares: var shares }
            && shares[0] > before + 0.001 && Math.Abs(shares.Sum() - 1) < 1e-6)))
        {
            return "the shares of a dragged divider did not reach the shell";
        }

        if (await page.EvaluateAsync(DragHeaderOfB) is not "\"ok\"" and var moved)
        {
            return $"dragging a header: {moved}";
        }

        if (!await UntilAsync(() => Task.FromResult(SplitShape(Shell.Layout.Tree, tabs) == "[a|c|[d/[b|e|f]]]")))
        {
            return $"after dragging a header the split is {SplitShape(Shell.Layout.Tree, tabs)}";
        }

        if (await PageShowsAsync(page, $"6/5/{frames}") is { } rearranged)
        {
            return $"after the move: {rearranged}";
        }

        Shell.IsDualFiles = dual;
        return null;
    }

    // Die Teilung in Buchstaben: a bis f in der Reihenfolge der Tabs, | nebeneinander, / untereinander.
    private static string SplitShape(SplitNode<SessionViewModel>? node, List<SessionViewModel> tabs) => node switch
    {
        SplitLeaf<SessionViewModel> leaf => new string((char)('a' + tabs.IndexOf(leaf.Item)), 1),
        SplitBranch<SessionViewModel> branch => $"[{string.Join(branch.SideBySide ? "|" : "/", branch.Children.Select(child => SplitShape(child, tabs)))}]",
        _ => "-",
    };

    // Wartet, bis die Seite das Erwartete zeigt (höchstens fünf Sekunden); sonst, was sie zeigt.
    private static async Task<string?> PageShowsAsync(TerminalHost page, string expected)
    {
        var seen = string.Empty;
        return await UntilAsync(async () => (seen = await page.EvaluateAsync(CountPanes)) == $"\"{expected}\"")
            ? null
            : $"the page shows {seen}, expected \"{expected}\"";
    }

    private static async Task<bool> UntilAsync(Func<Task<bool>> reached, int attempts = 50)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await reached())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private void ShowSuggestions()
    {
        CommandBox.ItemsSource = Shell.SearchPalette(CommandBox.Text);
        CommandBox.IsSuggestionListOpen = true;
    }

    // Ein eigener Name für den Reiter; leer nimmt ihn wieder weg. Er gilt, solange der Tab offen ist.
    private async Task RenameTabAsync()
    {
        if (Shell.ActiveSession is { } session
            && await App.Dialogs.AskTextAsync(L.Get("RenameTabTitle"), L.Get("RenameTabLabel"), session.CustomTitle ?? string.Empty, L.Get("Rename"), _ => true) is { } name)
        {
            Shell.RenameTab(session, name);
        }
    }

    private async Task AddServerAsync()
    {
        if (await App.Dialogs.EditProfileAsync(null, Shell.GroupNames(), Shell.Profiles) is { } profile)
        {
            Shell.SaveProfile(profile);
        }
    }

    private async Task ImportSshConfigAsync()
    {
        if (await App.Dialogs.ChooseSshConfigHostsAsync(Shell.ReadSshConfig()) is { Aliases.Count: > 0 } chosen)
        {
            Shell.ImportSshConfig(chosen.Aliases, chosen.Linked);
        }
    }

    // Was hier schiefgeht, fiele sonst lautlos weg – niemand wartet auf die Aufgabe.
    private static async Task Logged(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    private async Task ExportDataAsync()
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ShellViewModel.ExportFileName(DateTimeOffset.Now),
            DefaultFileExtension = ExportFile.Extension,
        };
        picker.FileTypeChoices.Add(L.Get("ExportFiles"), [ExportFile.Extension]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            Shell.ExportData(file.Path);
        }
    }

    private bool _closeConfirmed;

    // Das X, Alt+F4 oder „Schließen“ aus der Taskleiste: Sind Sitzungen verbunden oder laufen Transfers, wird erst
    // gefragt (abstellbar). Der Selbsttest und ein Schließen aus dem Code kommen hier nicht vorbei.
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!App.IsUnattended)
        {
            RememberPlace();
        }

        if (_closeConfirmed || App.IsUnattended || Shell.CloseQuestion() is not { } question)
        {
            return;
        }

        args.Cancel = true;
        try
        {
            if (await App.Dialogs.ConfirmAsync(L.Get("CloseAskTitle"), question, L.Get("CloseAskConfirm")))
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    private async Task OfferMuxAsync(SessionViewModel session, IReadOnlyList<NexusSSH.Core.Models.MuxSession> running)
    {
        if (await App.Dialogs.ChooseMuxAsync(session, running, Shell.MuxStepOf(session.Profile), Shell.MuxControlOf(session.Profile)) is var (chosen, step, control))
        {
            Shell.ChooseMux(session, chosen, step, control);
            if (chosen is not null && ReferenceEquals(Shell.ActiveSession, session))
            {
                Shell.FocusTerminal();
            }
        }
    }

    // Das Diagnosepaket für einen Fehlerbericht: Wohin, fragt der Dialog von Windows; was hineinkommt, bestimmt Core.
    private async Task SaveDiagnosticsAsync()
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            SuggestedFileName = Core.Services.DiagnosticBundle.FileName(DateTimeOffset.Now),
            DefaultFileExtension = ".zip",
        };
        picker.FileTypeChoices.Add(L.Get("DiagnosticsFiles"), [".zip"]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            var core = NativeCore.Probe() is var (version, abi) ? $"{version} (ABI {abi}, expected {NativeCore.ExpectedAbi})" : "not loaded";
            Shell.SaveDiagnostics(file.Path, core, App.IsDemo);
        }
    }

    // Erst zeigen, was die Datei enthält und was sich ändern würde – übernommen wird nur nach Zustimmung.
    private async Task ImportSessionsAsync(IReadOnlyList<ForeignSession> sessions)
    {
        if (await App.Dialogs.ChooseForeignSessionsAsync(sessions, Shell.Profiles) is { Count: > 0 } chosen)
        {
            Shell.ImportSessions(chosen);
        }
    }

    // Dateien anderer Programme: eine exportierte Liste (MobaXterm, mRemoteNG, Royal TS, SecureCRT, eine ssh_config)
    // oder mehrere Dateien mit je einer Sitzung (SecureCRT, Remmina, Xshell). Was es ist, sagt der Inhalt.
    private async Task ImportSessionFileAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        var sessions = new List<ForeignSession>();
        string? failure = null;
        foreach (var file in files.Take(500))
        {
            if (ForeignSessionReader.ReadFile(file.Path, out var error) is { } found)
            {
                sessions.AddRange(found);
            }
            else
            {
                failure ??= error ?? string.Empty;
            }
        }

        if (sessions.Count == 0)
        {
            await App.Dialogs.ShowMessageAsync(
                L.Get("SessionsImportTitle"),
                failure is null ? L.Get("SessionFileEmpty") : L.Get("SessionFileUnreadable", failure));
            return;
        }

        await ImportSessionsAsync(sessions);
    }

    // Fragt nach Name und Passphrase; anlegen, hinterlegen und das Profil umstellen tut Core.
    private async Task CreateKeyAsync()
    {
        if (Shell.ActiveSession is { } session && await App.Dialogs.AskKeyAsync(session.Profile) is { } request)
        {
            await Shell.CreateKeyAsync(session, request);
        }
    }

    private async Task ImportDataAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(ExportFile.Extension);
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        if (Shell.PrepareImport(file.Path, out var error) is not { } plan)
        {
            await App.Dialogs.ShowMessageAsync(L.Get("DataImportFailedTitle"), error ?? string.Empty);
            return;
        }

        if (await App.Dialogs.ChooseImportAsync(plan) is { } choice)
        {
            Shell.ImportData(plan, choice);

            // Die Sprache gilt ab dem nächsten Start; die Auswahl in den Einstellungen zeigt sie schon.
            SettingsPane.ShowLanguage();
        }
    }

    private static GridLength Size(bool visible, double size) => new(visible ? size : 0);
}
