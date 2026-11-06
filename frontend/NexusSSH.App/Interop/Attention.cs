using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NexusSSH.Core.Settings;

namespace NexusSSH.App.Interop;

/// <summary>
/// Macht auf etwas aufmerksam, das fertig wurde, während das Fenster im Hintergrund war: ein Hinweis von Windows,
/// und das Symbol in der Taskleiste blinkt, bis das Fenster wieder vorn ist. Ist das Fenster vorn, geschieht
/// nichts – dann sieht man es ohnehin.
/// </summary>
internal static partial class Attention
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    private static bool _registered;
    private static bool _usable;

    /// <summary>
    /// Meldet die App bei Windows für Hinweise an. Kennt Windows keine Hinweise für die App (ältere Version),
    /// bleibt es beim Blinken in der Taskleiste.
    /// </summary>
    /// <remarks>
    /// Zeigen und Anmelden sind zweierlei: <c>Register</c> scheitert mit dem Windows App SDK 2.5.1 in jeder
    /// App, die das SDK selbst mitbringt (0x8007007E, „Microsoft.WindowsAppRuntime.Insights.Resource.dll“ –
    /// die Datei liegt nur im Laufzeitpaket; microsoft/WindowsAppSDK#6774, gefunden 2026-10-11). Hinweise lassen
    /// sich trotzdem zeigen; nur der Klick darauf erreicht die App dann nicht. Deshalb hängt das Zeigen allein an
    /// <c>IsSupported</c> – vorher blieb mit der gescheiterten Anmeldung jeder Hinweis aus.
    /// </remarks>
    public static void Register()
    {
        try
        {
            _usable = AppNotificationManager.IsSupported();
            if (_usable)
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _registered = false;
        }
    }

    public static void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
            // Beim Beenden nicht mehr wichtig.
        }

        _registered = false;
    }

    /// <summary>
    /// Die Glocke des Terminals: der Hinweiston von Windows und – nur, wenn <paramref name="window"/> nicht vorn
    /// ist – ein Blinken in der Taskleiste.
    /// </summary>
    public static void Ring(nint window, bool sound, bool flash)
    {
        if (sound)
        {
            PlayAlias("SystemAsterisk");
        }

        if (flash && window != 0 && GetForegroundWindow() != window)
        {
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = window,
                Flags = FlashTray | FlashUntilForeground,
                Count = 3,
            };
            FlashWindowEx(ref info);
        }
    }

    /// <summary>
    /// Der Ton zu einem Ereignis, aus dem Klangschema von Windows. Mit <paramref name="onlyInBackground"/> nur,
    /// wenn <paramref name="window"/> nicht vorn ist.
    /// </summary>
    public static void Play(nint window, SoundTone tone, bool onlyInBackground)
    {
        if (onlyInBackground && window != 0 && GetForegroundWindow() == window)
        {
            return;
        }

        PlayAlias(tone switch
        {
            SoundTone.Error => "SystemHand",
            SoundTone.Warning => "SystemExclamation",
            _ => "SystemAsterisk",
        });
    }

    // Ein Ton aus dem Klangschema von Windows, über seinen Namen dort. Nicht MessageBeep: Das schweigt, sobald
    // in Windows der „Signalton“ abgestellt ist (HKCU\Control Panel\Sound, Beep = no) – auch wenn dem Ereignis
    // ein Klang zugeordnet ist (gefunden 2026-10-11 am Rechner des Users). Hat das Schema für das Ereignis
    // keinen Klang, bleibt es still (SND_NODEFAULT) – das hat der Benutzer dann so eingestellt.
    private static void PlayAlias(string alias)
        => PlaySound(alias, 0, SoundAlias | SoundAsync | SoundNoDefault);

    private const uint SoundAsync = 0x1;
    private const uint SoundNoDefault = 0x2;
    private const uint SoundAlias = 0x10000;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string name, nint module, uint flags);

    /// <summary>
    /// Eine Rückfrage ist aufgegangen: Ist <paramref name="window"/> nicht vorn, blinkt sein Knopf in der Taskleiste,
    /// bis es angesehen wird. Den Vordergrund nimmt die App sich dafür nicht – wer gerade woanders tippt, tippte
    /// sonst in die Rückfrage (in der Sandbox gesehen, 2026-10-13: Sie blieb im Hintergrund unbemerkt).
    /// </summary>
    /// <summary>Ob dieses Fenster gerade vorn ist.</summary>
    public static bool IsInFront(nint window) => window != 0 && GetForegroundWindow() == window;

    public static void Ask(nint window)
    {
        if (window == 0 || GetForegroundWindow() == window)
        {
            return;
        }

        var flash = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashTray | FlashUntilForeground,
            Count = 3,
        };
        FlashWindowEx(ref flash);
    }

    /// <summary>Hinweis und Blinken – nur, wenn <paramref name="window"/> nicht das Fenster im Vordergrund ist.</summary>
    public static void Show(nint window, string title, string text)
    {
        if (window == 0 || GetForegroundWindow() == window)
        {
            return;
        }

        var flash = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashTray | FlashUntilForeground,
            Count = 3,
        };
        FlashWindowEx(ref flash);

        if (!_usable)
        {
            return;
        }

        try
        {
            // Die Texte können vom Server stammen (Dateiname): Der Builder setzt sie als reinen Text ein.
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification());
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            // Das Blinken in der Taskleiste bleibt.
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}
