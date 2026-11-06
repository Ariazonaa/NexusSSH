using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NexusSSH.App.Views;

/// <summary>
/// Hält den Fokus in einer Dateiliste, wenn das ViewModel sie ersetzt (Alt+←, Rücktaste, F5, Enter auf einem Ordner,
/// ein Auffrischen nach einem Transfer).
/// </summary>
/// <remarks>
/// Mit dem neuen <c>ItemsSource</c> baut die Liste ihre Zeilen ab, noch im Setzen selbst: Die Zeile mit dem Fokus
/// verschwindet, und WinUI gibt ihn dem nächsten Halt – dem Filterfeld –, wohin dann die nächste Taste ginge
/// (gefunden 2026-10-24 im Windows-Gast). Zurückgeben lässt er sich erst, wenn es die neuen Zeilen gibt. Ein einzelner
/// Versuch mit niedrigem Vorrang kam mitunter davor dran (fragt UI Automation – eine Sprachausgabe – gerade nach,
/// läuft die Warteschlange des Threads früher weiter), fand keine Zeile und gab auf; der Fokus blieb im Filterfeld.
/// Deshalb wird nach jedem Aufbau der Liste noch einmal versucht, höchstens <see cref="RestoreMilliseconds"/> lang.
/// <para>
/// Zurückgegeben wird nur, was das Ersetzen selbst verschoben hat: Der Fokus muss die Liste im selben Zug verlassen
/// haben, in dem die neue Liste kam, und noch dort stehen, wohin WinUI ihn gesetzt hat. Hat der Benutzer ihn
/// woandershin gesetzt (Klick ins Filterfeld, Tab, Strg+L), bleibt er dort.
/// </para>
/// </remarks>
internal sealed class ListFocusKeeper
{
    private const long RestoreMilliseconds = 2_000;

    private readonly ListViewBase _list;

    // Wohin der Fokus die Liste gerade verlassen hat – nur bis zum Ende dieses Zuges der Oberfläche gesetzt.
    private bool _justLeft;
    private DependencyObject? _leftTo;

    // Ein laufender Versuch, den Fokus zurückzugeben: wo WinUI ihn hingesetzt hat, und bis wann.
    private DependencyObject? _displacedTo;
    private long _restoreUntil;

    private ListFocusKeeper(ListViewBase list)
    {
        _list = list;
        list.LosingFocus += OnLosingFocus;
        list.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, OnItemsSourceChanged);
    }

    /// <summary>Hängt den Wächter an <paramref name="list"/>; er lebt so lange wie die Liste.</summary>
    public static void Attach(ListViewBase list) => _ = new ListFocusKeeper(list);

    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (IsInList(args.NewFocusedElement))
        {
            return;
        }

        _justLeft = true;
        _leftTo = args.NewFocusedElement;

        // Ein Ersetzen der Liste ruft OnItemsSourceChanged noch im selben Zug; alles danach war etwas anderes.
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => (_justLeft, _leftTo) = (false, null));
    }

    private void OnItemsSourceChanged(DependencyObject sender, DependencyProperty property)
    {
        if (!_justLeft)
        {
            return;
        }

        _displacedTo = _leftTo;
        (_justLeft, _leftTo) = (false, null);
        if (_restoreUntil == 0)
        {
            _list.LayoutUpdated += OnLayoutUpdated;
        }

        _restoreUntil = Environment.TickCount64 + RestoreMilliseconds;

        // Gibt es die neuen Zeilen schon, genügt der nächste freie Moment; sonst versucht es OnLayoutUpdated.
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, TryRestore);
    }

    private void OnLayoutUpdated(object? sender, object e) => TryRestore();

    private void TryRestore()
    {
        if (_restoreUntil == 0)
        {
            return;
        }

        var focused = Focused();
        if (Environment.TickCount64 > _restoreUntil || IsInList(focused) || !ReferenceEquals(focused, _displacedTo))
        {
            Stop();
            return;
        }

        if (_list.Focus(FocusState.Keyboard))
        {
            Stop();
        }
    }

    private void Stop()
    {
        _list.LayoutUpdated -= OnLayoutUpdated;
        _restoreUntil = 0;
        _displacedTo = null;
    }

    private DependencyObject? Focused() =>
        _list.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;

    private bool IsInList(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _list))
            {
                return true;
            }
        }

        return false;
    }
}
