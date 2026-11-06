using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Interop;

// Die Taskleiste von Windows (ITaskbarList3) – nur bis zu den beiden Methoden, die hier gebraucht werden; die
// davor stehen da, weil die Reihenfolge die der Tabelle der Schnittstelle ist.
[GeneratedComInterface]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
internal partial interface ITaskbarList3
{
    [PreserveSig]
    int HrInit();

    [PreserveSig]
    int AddTab(nint window);

    [PreserveSig]
    int DeleteTab(nint window);

    [PreserveSig]
    int ActivateTab(nint window);

    [PreserveSig]
    int SetActiveAlt(nint window);

    [PreserveSig]
    int MarkFullscreenWindow(nint window, int fullscreen);

    [PreserveSig]
    int SetProgressValue(nint window, ulong completed, ulong total);

    [PreserveSig]
    int SetProgressState(nint window, uint state);
}

/// <summary>
/// Der Fortschritt aller Transfers im Symbol der Taskleiste: ein Balken, solange etwas läuft (Windows zeichnet
/// ihn je nach Fassung grün oder in der Akzentfarbe), ein wandernder, solange die Menge nicht bekannt ist, ein
/// gelber, wenn alles angehalten ist. Geht es nicht (keine
/// Taskleiste, ältere Shell), bleibt das Symbol, wie es ist.
/// </summary>
internal sealed class TaskbarProgress
{
    private const uint NoProgress = 0;
    private const uint Indeterminate = 1;
    private const uint Normal = 2;
    private const uint Paused = 8;
    private const ulong Steps = 1000;

    private static readonly Guid TaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid TaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    private readonly nint _window;
    private ITaskbarList3? _taskbar;
    private bool _tried;
    private (uint State, ulong Value) _shown = (NoProgress, 0);

    public TaskbarProgress(nint window) => _window = window;

    /// <summary>Zeigt den Stand; ohne Änderung geschieht nichts – der Abgleich der Transfers kommt oft.</summary>
    public void Show(OverallTransfer overall)
    {
        var state = overall.State switch
        {
            OverallTransferState.Running => Normal,
            OverallTransferState.Unknown => Indeterminate,
            OverallTransferState.Paused => Paused,
            _ => NoProgress,
        };
        var value = state is Normal or Paused ? (ulong)Math.Round(Math.Clamp(overall.Fraction, 0, 1) * Steps) : 0;
        if ((state, value) == _shown || Taskbar() is not { } taskbar)
        {
            return;
        }

        try
        {
            // Gemerkt wird nur, was ankam: Vor dem ersten Anzeigen des Fensters gibt es sein Symbol noch nicht.
            var result = taskbar.SetProgressState(_window, state);
            if (result >= 0 && state is Normal or Paused)
            {
                result = taskbar.SetProgressValue(_window, value, Steps);
            }

            if (result >= 0)
            {
                _shown = (state, value);
            }
        }
        catch (COMException exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    // Erst beim ersten Gebrauch, und nur einmal versucht.
    private ITaskbarList3? Taskbar()
    {
        if (_tried)
        {
            return _taskbar;
        }

        _tried = true;
        if (Ole.CoCreateInstance(TaskbarList, 0, Ole.CLSCTX_INPROC_SERVER, TaskbarList3, out var pointer) < 0)
        {
            return null;
        }

        try
        {
            var taskbar = (ITaskbarList3)Ole.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            _taskbar = taskbar.HrInit() >= 0 ? taskbar : null;
        }
        catch (Exception exception) when (exception is InvalidCastException or COMException)
        {
            App.WriteCrashLog(exception);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        return _taskbar;
    }
}
