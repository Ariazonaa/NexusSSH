using System.Runtime.InteropServices;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Legt Dateien und Ordner, die es hier noch nicht gibt, in die Zwischenablage – dasselbe Angebot wie beim Ziehen
/// (<see cref="VirtualFileDataObject"/>): Geladen wird erst, wenn jemand einfügt. Nur im Thread der Oberfläche benutzen.
/// </summary>
internal static class OleClipboard
{
    // Was zuletzt hineingelegt wurde – um es beim Beenden wieder herauszunehmen.
    private static nint _current;

    /// <summary>Ersetzt den Inhalt der Zwischenablage. <c>false</c>, wenn sie gerade einem anderen Programm gehört.</summary>
    public static bool Set(IVirtualFiles files)
    {
        Ole.Prepare();
        nint proxy = 0;
        try
        {
            proxy = Ole.InOtherThreads(new VirtualFileDataObject(files, ownWindow: 0), Ole.IID_IDataObject);
            if (Ole.OleSetClipboard(proxy) < 0)
            {
                return false;
            }

            // Wer das vorige Angebot gerade einfügt, hält es selbst fest; hier wird es nicht mehr gebraucht.
            (proxy, _current) = (_current, proxy);
            return true;
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
            return false;
        }
        finally
        {
            if (proxy != 0)
            {
                Marshal.Release(proxy);
            }
        }
    }

    /// <summary>
    /// Nimmt das eigene Angebot aus der Zwischenablage, wenn es noch darin liegt – beim Beenden: Einfügen ließe
    /// es sich danach nicht mehr, weil niemand mehr lädt.
    /// </summary>
    public static void Clear()
    {
        if (_current == 0)
        {
            return;
        }

        if (Ole.OleIsCurrentClipboard(_current) == Ole.S_OK)
        {
            _ = Ole.OleSetClipboard(0);
        }

        Marshal.Release(_current);
        _current = 0;
    }
}
