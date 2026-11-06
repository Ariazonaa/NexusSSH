using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Zieht Dateien und Ordner, die es hier noch nicht gibt, in ein anderes Programm – mit dem Ziehen von Windows
/// selbst (<c>DoDragDrop</c>) statt dem von WinUI: Nur so lässt sich eine Liste mit Unterordnern anbieten, deren
/// Inhalt erst beim Abholen entsteht (<see cref="VirtualFileDataObject"/>).
/// </summary>
internal static partial class OleDrag
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONUP = 0x0202;
    private const int VK_LBUTTON = 1;

    /// <summary>
    /// Zieht, bis die Maus losgelassen oder Esc gedrückt wird; kehrt erst dann zurück. Im Thread der Oberfläche
    /// rufen, solange die linke Maustaste gedrückt ist. Liefert, ob ein Empfänger die Einträge genommen hat.
    /// </summary>
    public static bool Run(IVirtualFiles files) => Run(files, out _);

    /// <summary>
    /// Wie <see cref="Run(IVirtualFiles)"/>; <paramref name="released"/> sagt dazu, ob die Maus losgelassen wurde
    /// (und nicht Esc gedrückt) – auch wenn dort niemand die Einträge genommen hat.
    /// </summary>
    public static bool Run(IVirtualFiles files, out bool released)
    {
        released = false;
        Ole.Prepare();
        // Das Ziehen beginnt über dem eigenen Fenster.
        var data = new VirtualFileDataObject(files, Ole.TopWindowUnderCursor()) { IsDragging = true };
        nint proxy = 0;
        nint source = 0;
        var taken = false;
        using var done = new CancellationTokenSource();
        try
        {
            proxy = Ole.InOtherThreads(data, Ole.IID_IDataObject);
            ShowImage(proxy, files is VirtualDrop { HasFolders: true });
            var dropSource = new DropSource(data);
            source = Ole.InterfaceOf(dropSource, Ole.IID_IDropSource);
            _ = FeedMouseAsync(Ole.GetCurrentThreadId(), done.Token);
            var result = Ole.DoDragDrop(proxy, source, Ole.DROPEFFECT_COPY, out var effect);

            // Nicht am Ergebnis abzulesen: Nimmt das Ziel nichts an, nennt Windows auch das Loslassen „abgebrochen“.
            released = dropSource.Released;
            taken = result == Ole.DRAGDROP_S_DROP && effect != 0;
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
        finally
        {
            data.IsDragging = false;
            done.Cancel();
            if (source != 0)
            {
                Marshal.Release(source);
            }

            if (proxy != 0)
            {
                Marshal.Release(proxy);
            }
        }

        // Holt der Empfänger im Hintergrund ab, meldet er sich selbst, wenn er fertig ist (EndOperation). Sonst
        // ist mit dem Loslassen alles geschehen – oder nichts.
        if (!taken || !data.IsInOperation)
        {
            data.Complete(taken);
        }

        return taken;
    }

    // Ein Bild unter dem Mauszeiger (DragImage) und daneben der Hinweis des Empfängers („Nach Downloads kopieren“).
    // Beides zeichnet der Bild-Helfer von Windows; er legt es im Datenobjekt ab und liest es von dort – deshalb
    // bewahrt das Datenobjekt auf, was hineingeschrieben wird. Geht etwas schief, wird eben ohne Bild gezogen.
    private static void ShowImage(nint dataObject, bool folder)
    {
        if (Ole.CoCreateInstance(Ole.CLSID_DragDropHelper, 0, Ole.CLSCTX_INPROC_SERVER, Ole.IID_IDragSourceHelper2, out var pointer) < 0)
        {
            return;
        }

        try
        {
            var helper = (IDragSourceHelper2)Ole.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            _ = helper.SetFlags(Ole.DSH_ALLOWDROPDESCRIPTIONTEXT);
            _ = DragImage.Give(helper, dataObject, folder);
        }
        catch (Exception exception) when (exception is InvalidCastException or COMException)
        {
            App.WriteCrashLog(exception);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    // DoDragDrop erfährt Mausbewegung und Loslassen aus Mausnachrichten an sein eigenes, unsichtbares Fenster. In
    // einer WinUI-App kommt die Maus aber als Zeiger an (EnableMouseInPointer), und diese Nachrichten gehen an das
    // Fenster, in dem die Taste gedrückt wurde – das Ziehen endete nie. Deshalb reicht dieser Beobachter den
    // Stand der Maus nach, als Nachrichten an das Fenster, das im Thread der Oberfläche gerade die Maus hält.
    private static async Task FeedMouseAsync(uint thread, CancellationToken done)
    {
        try
        {
            while (!done.IsCancellationRequested)
            {
                await Task.Delay(15, done).ConfigureAwait(false);
                var info = new Ole.GUITHREADINFO { Size = (uint)Marshal.SizeOf<Ole.GUITHREADINFO>() };
                if (Ole.GetGUIThreadInfo(thread, ref info) == 0 || info.Capture == 0)
                {
                    continue;
                }

                _ = Ole.GetCursorPos(out var point);
                _ = Ole.ScreenToClient(info.Capture, ref point);
                var position = (nint)(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
                if (Ole.GetAsyncKeyState(VK_LBUTTON) < 0)
                {
                    _ = Ole.PostMessage(info.Capture, WM_MOUSEMOVE, Ole.MK_LBUTTON, position);
                }
                else
                {
                    // Losgelassen. Nicht im Takt wiederholen: Das Ziel braucht einen Augenblick, um zu übernehmen.
                    _ = Ole.PostMessage(info.Capture, WM_LBUTTONUP, 0, position);
                    await Task.Delay(300, done).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    private const int IDC_ARROW = 32512;

    [GeneratedComClass]
    private sealed partial class DropSource(VirtualFileDataObject data) : IOleDropSource
    {
        /// <summary>Die Maus wurde losgelassen (nicht Esc gedrückt) – gleich, ob dort jemand etwas nahm.</summary>
        public bool Released { get; private set; }

        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            if (escapePressed != 0)
            {
                return Ole.DRAGDROP_S_CANCEL;
            }

            if ((keyState & Ole.MK_LBUTTON) == 0)
            {
                Released = true;
                return Ole.DRAGDROP_S_DROP;
            }

            return Ole.S_OK;
        }

        // Zeigt das Ziehbild selbst, was beim Loslassen geschieht („Nach … kopieren“), bleibt der Zeiger ein
        // gewöhnlicher Pfeil – wie beim Ziehen im Explorer. Sonst zeigen die Zeiger von Windows die Wirkung.
        public int GiveFeedback(uint effect)
        {
            if (!data.ShowsDragImage)
            {
                return Ole.DRAGDROP_S_USEDEFAULTCURSORS;
            }

            // Bei jeder Rückmeldung neu zeichnen lassen: Der Hinweis des Empfängers kann sich mit jeder Bewegung ändern.
            data.RefreshDragImage();
            _ = SetCursor(LoadCursor(0, IDC_ARROW));
            return Ole.S_OK;
        }
    }
}
