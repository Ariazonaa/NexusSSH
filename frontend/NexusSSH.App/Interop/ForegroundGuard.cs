using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexusSSH.App.Interop;

/// <summary>
/// Hält, was <c>--no-activate</c> verspricht: Das Fenster nimmt niemandem die Tastatur. Der Start ohne Aktivierung
/// allein genügt dafür nicht – setzt später etwas den Fokus in der Oberfläche (eine Seite geht auf, die
/// Automatisierung füllt ein Textfeld), holt Windows das Fenster nach vorn, und was der Benutzer gerade in einem
/// anderen Programm tippt, landet hier (gemessen 2026-10-11: beim Setzen eines Textfelds über UI Automation war das
/// Testfenster in 54 von 64 Blicken vorn; im Filter der Dateiliste stand danach ein Komma des Users).
/// <para>
/// Deshalb: Wird ein Fenster dieser App zum Vordergrundfenster, ohne dass der Benutzer es geholt hat (Mausklick,
/// Alt+Tab, Windows-Taste), geht der Vordergrund sofort an das Fenster zurück, das ihn davor hatte. Nur in diesem
/// Modus – im gewöhnlichen Betrieb gehört es sich, dass ein Dialog der App nach vorn kommt.
/// </para>
/// </summary>
internal static unsafe partial class ForegroundGuard
{
    private const uint EventSystemForeground = 0x0003;
    private const uint OutOfContext = 0x0000;

    private static nint _previous;
    private static nint _hook;

    /// <summary>Beginnt zu wachen. Einmal, im Thread der Oberfläche (der Rückruf kommt über dessen Nachrichten).</summary>
    public static void Keep()
    {
        if (_hook != 0)
        {
            return;
        }

        var now = GetForegroundWindow();
        _previous = IsOurs(now) ? 0 : now;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &OnForeground, 0, 0, OutOfContext);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnForeground(nint hook, uint @event, nint window, int @object, int child, uint thread, uint time)
    {
        if (!IsOurs(window))
        {
            _previous = window;
            return;
        }

        // Der Benutzer hat das Fenster selbst geholt: dann bleibt es vorn.
        if (UserIsSwitching() || _previous == 0 || !IsWindow(_previous))
        {
            return;
        }

        SetForegroundWindow(_previous);
    }

    private static bool IsOurs(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    // Eine Maustaste (Klick ins Fenster oder auf die Taskleiste), Alt (Alt+Tab) oder die Windows-Taste ist gedrückt.
    private static bool UserIsSwitching()
    {
        foreach (var key in (int[])[0x01, 0x02, 0x04, 0x12, 0x5B, 0x5C])
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint first, uint last, nint module, delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
