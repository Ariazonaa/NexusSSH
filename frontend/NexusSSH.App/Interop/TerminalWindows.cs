using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Interop;

/// <summary>
/// Die eigenen Fenster abgelöster Terminals: öffnet eines, sobald die Shell eine Sitzung löst
/// (<see cref="ShellViewModel.DetachedChanged"/>), und weiß, welches Fenster der App zuletzt vorn war – dort
/// erscheinen Rückfragen.
/// </summary>
internal static partial class TerminalWindows
{
    private static readonly Dictionary<string, TerminalWindow> Open = new(StringComparer.Ordinal);
    private static TerminalWindow? _active;
    private static bool _quiet;

    /// <summary>Die Größe eines neuen Fensters, wenn die Sitzung noch keines hatte.</summary>
    public static WindowBounds DefaultSize { get; } = new(0, 0, 980, 620);

    /// <summary>Alle offenen Fenster.</summary>
    public static IReadOnlyCollection<TerminalWindow> All => Open.Values;

    /// <summary>Hört auf die Shell. Einmal, bevor sie Tabs wiederherstellt.</summary>
    /// <param name="quiet">Neue Fenster nehmen den Fokus nicht (<c>--no-activate</c>, Selbsttest).</param>
    public static void Watch(ShellViewModel shell, bool quiet)
    {
        _quiet = quiet;
        shell.DetachedChanged += session =>
        {
            if (session.IsDetached)
            {
                Ensure(session);
            }
        };

        // Eine Sitzung, die schließt, nimmt ihr Fenster mit.
        shell.Sessions.CollectionChanged += (_, _) =>
        {
            foreach (var window in Open.Values.Where(window => !shell.Sessions.Contains(window.Session)).ToList())
            {
                window.CloseForExit();
            }
        };
    }

    /// <summary>Holt das Fenster der Sitzung nach vorn.</summary>
    public static void Show(SessionViewModel session)
    {
        if (session.IsDetached)
        {
            Ensure(session).Activate();
        }
    }

    /// <summary>
    /// Die Wurzel des Fensters, das zuletzt vorn war, wenn es ein abgelöstes ist – sonst <c>null</c> (dann gilt das
    /// Hauptfenster).
    /// </summary>
    public static XamlRoot? ActiveRoot => _active?.ContentRoot;

    public static void NoteActive(TerminalWindow? window) => _active = window;

    /// <summary>
    /// Ein Fenster geht gleich zu – vorher, solange seine Oberfläche noch steht. Ein Dialog darin zieht dann in ein
    /// anderes Fenster um (<see cref="Dialogs.DialogService"/>): Verschwände er mit dem Fenster, bekäme er nie eine
    /// Antwort – eine Rückfrage beim Verbinden hielte die Sitzung für immer im Aufbau, und die Warteschlange der
    /// Dialoge stünde (gefunden 2026-10-14).
    /// </summary>
    public static event Action<XamlRoot>? Closing;

    /// <summary>Meldet <see cref="Closing"/>; danach gilt das Fenster nicht mehr als zuletzt vorn.</summary>
    public static void NoteClosing(TerminalWindow window)
    {
        if (ReferenceEquals(_active, window))
        {
            _active = null;
        }

        if (window.ContentRoot is { } root)
        {
            Closing?.Invoke(root);
        }
    }

    public static void Forget(TerminalWindow window)
    {
        if (Open.TryGetValue(window.Session.Id, out var known) && ReferenceEquals(known, window))
        {
            Open.Remove(window.Session.Id);
        }

        if (ReferenceEquals(_active, window))
        {
            _active = null;
        }
    }

    /// <summary>Die App endet: alle Fenster zu, ohne dass ihre Sitzungen zurückkehren.</summary>
    public static void CloseAll()
    {
        foreach (var window in Open.Values.ToList())
        {
            window.CloseForExit();
        }
    }

    /// <summary>Ob von dem Rechteck genug auf einem Bildschirm liegt, um das Fenster dort zu greifen.</summary>
    public static bool IsOnScreen(WindowBounds bounds)
    {
        if (!bounds.IsUsable)
        {
            return false;
        }

        // Die Titelzeile muss zu fassen sein: ein Streifen oben, ein Stück vom Rand weg.
        var grip = new Windows.Graphics.RectInt32(bounds.X + 40, bounds.Y, Math.Max(bounds.Width - 80, 40), 30);
        var area = DisplayArea.GetFromRect(grip, DisplayAreaFallback.None);
        return area is not null
            && grip.X < area.WorkArea.X + area.WorkArea.Width
            && grip.X + grip.Width > area.WorkArea.X
            && grip.Y >= area.WorkArea.Y - 10
            && grip.Y < area.WorkArea.Y + area.WorkArea.Height;
    }

    /// <summary>Wo der Mauszeiger steht, in Pixeln des Bildschirms.</summary>
    public static (int X, int Y)? PointerPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    /// <summary>
    /// Wo der Mauszeiger steht, in DIPs der Oberfläche von <paramref name="root"/> (links oben ist die Ecke des
    /// Inhalts; die Insel füllt den Client-Bereich des Fensters, <see cref="ScaledContent"/>).
    /// </summary>
    public static Windows.Foundation.Point? PointerIn(XamlRoot root)
    {
        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        if (!GetCursorPos(out var point) || !ScreenToClient(window, ref point) || root.RasterizationScale <= 0)
        {
            return null;
        }

        return new Windows.Foundation.Point(point.X / root.RasterizationScale, point.Y / root.RasterizationScale);
    }

    private static TerminalWindow Ensure(SessionViewModel session)
    {
        if (Open.TryGetValue(session.Id, out var window))
        {
            return window;
        }

        Open[session.Id] = window = new TerminalWindow(session);
        if (App.IsUnattended)
        {
            // Wie das Hauptfenster im Selbsttest: weit außerhalb jedes Bildschirms und nicht in der Taskleiste.
            window.AppWindow.IsShownInSwitchers = false;
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        }

        if (_quiet)
        {
            window.AppWindow.Show(activateWindow: false);
        }
        else
        {
            window.Activate();
        }

        return window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref Point point);
}
