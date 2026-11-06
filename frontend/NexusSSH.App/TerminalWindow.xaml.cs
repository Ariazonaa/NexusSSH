using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NexusSSH.App.Controls;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App;

/// <summary>
/// Das Terminal einer Sitzung in einem eigenen Fenster. Das Fenster zu schließen holt den Tab zurück ins
/// Hauptfenster und trennt nichts (Entscheidung des Users, 2026-10-11); geschlossen wird eine Sitzung wie immer am
/// Reiter. Was dabei mit dem Inhalt des Terminals geschieht, regelt <see cref="TerminalHost"/>.
/// </summary>
public sealed partial class TerminalWindow : Window
{
    private readonly Interop.ScaledContent _scaled;
    private readonly TerminalHost _host;
    private bool _leaving;

    public TerminalWindow(SessionViewModel session)
    {
        Session = session;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        _host = new TerminalHost(session);
        TerminalArea.Children.Add(_host);
        _host.Left += OnLeft;

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "NexusSSH.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        Place(session.DetachedBounds);
        ShowSession();
        session.PropertyChanged += OnSessionChanged;
        session.Inspector.PropertyChanged += OnSessionChanged;
        Shell.PropertyChanged += OnShellChanged;
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
        Activated += OnActivated;
        Closed += OnClosed;
        TitleRow.Loaded += (_, _) => UpdateTitleBarRegions();
        TitleRow.SizeChanged += (_, _) => UpdateTitleBarRegions();

        // Wie im Hauptfenster liegt der Inhalt in einer eigenen Insel: So gilt die Größe der Oberfläche auch hier.
        _scaled = new Interop.ScaledContent(this, Root, Shell.UiZoom);
    }

    public SessionViewModel Session { get; }

    /// <summary>Die Wurzel der Oberfläche – für Rückfragen, die zu diesem Fenster gehören.</summary>
    public XamlRoot? ContentRoot => Root.XamlRoot;

    /// <summary>Das Terminal dieses Fensters – der Selbsttest sieht dort nach, ob der Inhalt mitgekommen ist.</summary>
    internal TerminalHost Host => _host;

    /// <summary>Die App endet: Das Fenster schließt, ohne die Sitzung zurückzuholen.</summary>
    internal void CloseForExit()
    {
        _leaving = true;
        Interop.TerminalWindows.NoteClosing(this);
        Close();
    }

    private static ShellViewModel Shell => App.Shell;

    private void Place(WindowBounds? wanted)
    {
        var size = wanted ?? Interop.TerminalWindows.DefaultSize;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));

        // Nur dorthin, wo ein Bildschirm ist: Der zweite Monitor von gestern steht heute vielleicht nicht da.
        if (wanted is not null && Interop.TerminalWindows.IsOnScreen(wanted))
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(wanted.X, wanted.Y));
        }

        // Auch ein Fenster, das nie bewegt wurde, kommt beim nächsten Mal an dieselbe Stelle.
        if (!App.IsUnattended)
        {
            Session.DetachedBounds = new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
    }

    private void ShowSession()
    {
        Title = TitleText.Text = Session.Title;
        StateDot.Fill = Ui.StatusBrush(Session.Inspector.IsOnline);
        Root.Background = new SolidColorBrush(Ui.ParseColor(Shell.ThemeFor(Session.Profile).Background));
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.Title) or nameof(ServerInspectorViewModel.IsOnline))
        {
            ShowSession();
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.UiZoom):
                _scaled.SetZoom(Shell.UiZoom);
                UpdateTitleBarRegions();
                break;

            case nameof(ShellViewModel.TerminalTheme) or nameof(ShellViewModel.Themes):
                ShowSession();
                break;
        }
    }

    // Was im Terminal dieses Fensters getippt wird, gilt dieser Sitzung: Sie wird der aktive Tab, und Dateibrowser
    // und Server-Info im Hauptfenster zeigen sie.
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _leaving)
        {
            return;
        }

        Interop.TerminalWindows.NoteActive(this);
        if (Shell.Sessions.Contains(Session))
        {
            Shell.ActiveSession = Session;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, _host.FocusTerminal);
    }

    private void OnReattachClicked(object sender, RoutedEventArgs e) => Shell.Reattach(Session);

    // Schließen heißt zurückholen: Erst zieht das Terminal um (sein Inhalt wandert mit), dann geht das Fenster zu.
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_leaving || !Session.IsDetached || !Shell.Sessions.Contains(Session))
        {
            return;
        }

        args.Cancel = true;

        // Vor dem Umzug: Ein Dialog in diesem Fenster zieht zuerst in ein anderes – sonst endete er mit dem Fenster
        // ohne Antwort, und eine Rückfrage zum Host-Schlüssel stünde als „nicht vertraut“ da.
        Interop.TerminalWindows.NoteClosing(this);
        Shell.Reattach(Session);
    }

    private void OnLeft(SessionViewModel session)
    {
        _leaving = true;
        Interop.TerminalWindows.NoteClosing(this);
        Close();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Wo das Fenster steht, merkt sich die Sitzung – nicht, solange es minimiert oder maximiert ist.
        if ((args.DidPositionChange || args.DidSizeChange)
            && Ui.Overlapped(sender) is { State: OverlappedPresenterState.Restored }
            && sender.Position.X > -30000)
        {
            Session.DetachedBounds = new WindowBounds(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _leaving = true;
        Session.PropertyChanged -= OnSessionChanged;
        Session.Inspector.PropertyChanged -= OnSessionChanged;
        Shell.PropertyChanged -= OnShellChanged;
        _host.Left -= OnLeft;
        _host.Release();
        Interop.TerminalWindows.Forget(this);
        _scaled.Dispose();
    }

    // Die Zeile zieht das Fenster, der Knopf darin lässt sich bedienen (wie MainWindow.UpdateTitleBarRegions).
    private void UpdateTitleBarRegions()
    {
        if (TitleRow.XamlRoot is not { } root || TitleRow.ActualWidth <= 0)
        {
            return;
        }

        var scale = root.RasterizationScale;
        if (AppWindow.TitleBar.RightInset > 0)
        {
            CaptionInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        }

        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Caption, [Region(TitleRow)]);
        source.SetRegionRects(NonClientRegionKind.Passthrough, [Region(ReattachButton)]);

        Windows.Graphics.RectInt32 Region(FrameworkElement element)
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale));
        }
    }
}
