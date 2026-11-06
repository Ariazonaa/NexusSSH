using System.Runtime.InteropServices;
using System.Text;

namespace NexusSSH.App.Interop;

/// <summary>
/// Legt Text samt HTML und RTF fest in die Zwischenablage – als Daten, nicht als Versprechen, sie später zu
/// liefern. Über die Zwischenablage von WinUI ging das RTF verloren, sobald die App endete (trotz <c>Flush</c>,
/// gemessen 2026-10-11): Kopieren, App schließen, in ein Programm einfügen, das nur RTF liest – nichts.
/// </summary>
internal static partial class TextClipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;

    private static readonly uint Html = Ole.RegisterClipboardFormat("HTML Format");
    private static readonly uint Rtf = Ole.RegisterClipboardFormat("Rich Text Format");

    /// <param name="html">Schon in der Form der Zwischenablage (<c>RichCopy.ClipboardHtml</c>), oder <c>null</c>.</param>
    /// <param name="rtf">Reines ASCII, oder <c>null</c>.</param>
    /// <returns><c>false</c>, wenn die Zwischenablage gerade einem anderen Programm gehört.</returns>
    public static bool Set(nint window, string text, byte[]? html, string? rtf)
    {
        if (!Open(window))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var done = Put(UnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
            if (html is not null)
            {
                done &= Put(Html, [.. html, 0]);
            }

            if (rtf is not null)
            {
                done &= Put(Rtf, Encoding.ASCII.GetBytes(rtf + '\0'));
            }

            return done;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Ein anderes Programm hält die Zwischenablage nur kurz offen (Verlauf von Windows, Fernwartung) – ein paar
    // Versuche, dann ist es eben nicht gegangen.
    private static bool Open(nint window)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(window))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static unsafe bool Put(uint format, byte[] bytes)
    {
        var memory = Ole.GlobalAlloc(Moveable, (nuint)bytes.Length);
        if (memory == 0)
        {
            return false;
        }

        var target = Ole.GlobalLock(memory);
        if (target == 0)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(bytes, 0, target, bytes.Length);
        Ole.GlobalUnlock(memory);

        // Nimmt die Zwischenablage den Speicher, gehört er ihr; sonst bleibt er unserer.
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            return false;
        }

        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
