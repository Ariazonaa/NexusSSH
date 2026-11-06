using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NexusSSH.App.Interop;

/// <summary>
/// Der Inhalt des Fensters in einer eigenen Insel, deren Maßstab sich setzen lässt – so wird die Oberfläche
/// stufenlos größer oder kleiner, neben der Skalierung von Windows. Ein Fenster von WinUI kennt keinen eigenen
/// Faktor; die Insel schon (<see cref="DesktopSiteBridge.OverrideScale"/>), und mit ihr skalieren Menüs, Dialoge und
/// das WebView des Terminals. Ein <c>ScaleTransform</c> über dem Inhalt täte das nicht.
/// </summary>
internal sealed partial class ScaledContent : IDisposable
{
    private readonly DesktopWindowXamlSource _source = new();
    private readonly nint _window;
    private int _percent;
    private uint _dpi;

    public ScaledContent(Window window, UIElement content, int percent)
    {
        _window = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        _percent = percent;

        // Der Inhalt zieht um: Das Fenster selbst bleibt leer, seine Insel liegt unter dieser.
        window.Content = null;
        _source.Initialize(window.AppWindow.Id);
        _source.Content = content;
        _source.SiteBridge.ResizePolicy = ContentSizePolicy.ResizeContentToParentWindow;
        Apply();
        _source.SiteBridge.Show();

        // Der Faktor ist absolut, nicht „mal dem von Windows“: Wechselt das Fenster auf einen Bildschirm mit anderer
        // Skalierung, muss er neu gerechnet werden.
        window.AppWindow.Changed += (_, args) =>
        {
            if ((args.DidPositionChange || args.DidSizeChange) && GetDpiForWindow(_window) != _dpi)
            {
                Apply();
            }
        };

        // Die Tastatur bekäme sonst die leere Insel des Fensters.
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                Remember();
            }

            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                // Erst nach dem Fenster: Es setzt den Fokus beim Aktivieren selbst, auf seine eigene Insel.
                window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TakeFocus);
            }
        };

        // Auch gleich zu Beginn: Ein Fenster, das beim Start nicht nach vorn kommt, wird nie „aktiviert“.
        if (content is FrameworkElement element)
        {
            element.Loaded += (_, _) => window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TakeFocus);

            // Wo der Fokus liegt, wird laufend gemerkt – beim Deaktivieren ist er womöglich schon fort. Einen Moment
            // später: Das WebView reicht ihn erst dann an sein eigenes Fenster weiter.
            element.GotFocus += (_, _) => window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Remember);
        }

        // Tab über das letzte Element hinaus (oder vor das erste) beginnt am anderen Ende, statt die Insel zu verlassen.
        _source.TakeFocusRequested += (sender, args) => sender.NavigateFocus(new XamlSourceFocusNavigationRequest(args.Request.Reason));
        window.Closed += (_, _) => Dispose();
    }

    /// <summary>Der Maßstab, mit dem die Oberfläche rechnet: Pixel je DIP.</summary>
    public double Scale => _dpi / 96.0 * _percent / 100.0;

    public void SetZoom(int percent)
    {
        if (percent != _percent)
        {
            _percent = percent;
            Apply();
        }
    }

    public void Dispose() => _source.Dispose();

    private void Apply()
    {
        _dpi = GetDpiForWindow(_window);
        _source.SiteBridge.OverrideScale = (float)Scale;
    }

    private void TakeFocus()
    {
        // Nur wenn der Fokus noch außerhalb liegt: Wer schon in der Insel ist (auch im WebView darin), bleibt dort.
        // Beim Schließen wechselt die Aktivierung des Fensters noch einmal – da ist die Insel schon abgebaut.
        if (_source.SiteBridge is not { } site)
        {
            return;
        }

        // Nur, solange dieses Fenster noch vorn ist: `SetFocus` holt ein Fenster desselben Threads nach vorn. Mit einem
        // abgelösten Terminal holten sich die beiden Fenster so gegenseitig zurück, Hunderte Male in der Minute – und
        // eine Rückfrage ging im falschen Fenster auf (gefunden 2026-10-14).
        if (GetForegroundWindow() != _window)
        {
            return;
        }

        var bridge = Win32Interop.GetWindowFromWindowId(site.WindowId);
        var focus = GetFocus();
        if (focus == bridge || IsChild(bridge, focus))
        {
            _last = focus;
            return;
        }

        // Zurück dorthin, wo er zuletzt war (das WebView hat ein eigenes Fenster), sonst an die Insel selbst –
        // die gibt ihn dem Element, das ihn zuletzt hatte.
        var target = _last != 0 && IsChild(bridge, _last) ? _last : FindWindowExW(bridge, 0, "InputSiteWindowClass", null);
        SetFocus(target != 0 ? target : bridge);
    }

    // Beim Verlassen des Fensters: wo der Fokus war. Beim Zurückkommen setzt das Fenster ihn auf seine eigene Insel.
    private void Remember()
    {
        // Beim Schließen wechselt die Aktivierung des Fensters noch einmal – da ist die Insel schon abgebaut.
        if (_source.SiteBridge is not { } site)
        {
            return;
        }

        var bridge = Win32Interop.GetWindowFromWindowId(site.WindowId);
        var focus = GetFocus();
        if (focus != 0 && IsChild(bridge, focus))
        {
            _last = focus;
        }
    }

    private nint _last;

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string className, string? title);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint window);
}
