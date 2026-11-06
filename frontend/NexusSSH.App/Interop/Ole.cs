using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NexusSSH.App.Interop;

// Was OLE für das Ziehen eigener Daten braucht – nur die Teile, die VirtualFileDataObject und OleDrag benutzen.
// Alles über Quellgeneratoren (GeneratedComInterface, LibraryImport): Die eingebaute COM-Anbindung von .NET gibt es
// unter Native AOT nicht.

[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    public ushort Format;
    public nint TargetDevice;
    public uint Aspect;
    public int Index;
    public uint Tymed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STGMEDIUM
{
    public uint Tymed;
    public nint Value;
    public nint UnknownForRelease;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STATSTG
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public long Modified;
    public long Created;
    public long Accessed;
    public uint Mode;
    public uint LocksSupported;
    public Guid ClassId;
    public uint StateBits;
    public uint Reserved;
}

[GeneratedComInterface]
[Guid("0000010E-0000-0000-C000-000000000046")]
internal unsafe partial interface IOleDataObject
{
    [PreserveSig]
    int GetData(FORMATETC* format, STGMEDIUM* medium);

    [PreserveSig]
    int GetDataHere(FORMATETC* format, STGMEDIUM* medium);

    [PreserveSig]
    int QueryGetData(FORMATETC* format);

    [PreserveSig]
    int GetCanonicalFormatEtc(FORMATETC* format, FORMATETC* result);

    [PreserveSig]
    int SetData(FORMATETC* format, STGMEDIUM* medium, int release);

    [PreserveSig]
    int EnumFormatEtc(uint direction, nint* enumerator);

    [PreserveSig]
    int DAdvise(FORMATETC* format, uint flags, nint sink, uint* connection);

    [PreserveSig]
    int DUnadvise(uint connection);

    [PreserveSig]
    int EnumDAdvise(nint* enumerator);
}

/// <summary>
/// Sagt dem Empfänger, dass er die Daten in einem eigenen Thread abholen darf – der Explorer zeigt dann seinen
/// Kopierdialog, statt selbst stillzustehen – und erfährt, wann er fertig ist.
/// </summary>
[GeneratedComInterface]
[Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4")]
internal unsafe partial interface IDataObjectAsyncCapability
{
    [PreserveSig]
    int SetAsyncMode(int doAsync);

    [PreserveSig]
    int GetAsyncMode(int* isAsync);

    [PreserveSig]
    int StartOperation(nint bindContext);

    [PreserveSig]
    int InOperation(int* inOperation);

    [PreserveSig]
    int EndOperation(int result, nint bindContext, uint effects);
}

[GeneratedComInterface]
[Guid("0C733A30-2A1C-11CE-ADE5-00AA0044773D")]
internal unsafe partial interface ISequentialStream
{
    [PreserveSig]
    int Read(byte* buffer, uint count, uint* read);

    [PreserveSig]
    int Write(byte* buffer, uint count, uint* written);
}

[GeneratedComInterface]
[Guid("0000000C-0000-0000-C000-000000000046")]
internal unsafe partial interface IOleStream : ISequentialStream
{
    [PreserveSig]
    int Seek(long move, uint origin, ulong* position);

    [PreserveSig]
    int SetSize(ulong size);

    [PreserveSig]
    int CopyTo(nint target, ulong count, ulong* read, ulong* written);

    [PreserveSig]
    int Commit(uint flags);

    [PreserveSig]
    int Revert();

    [PreserveSig]
    int LockRegion(ulong offset, ulong count, uint type);

    [PreserveSig]
    int UnlockRegion(ulong offset, ulong count, uint type);

    [PreserveSig]
    int Stat(STATSTG* stat, uint flags);

    [PreserveSig]
    int Clone(nint* stream);
}

[GeneratedComInterface]
[Guid("00000121-0000-0000-C000-000000000046")]
internal partial interface IOleDropSource
{
    [PreserveSig]
    int QueryContinueDrag(int escapePressed, uint keyState);

    [PreserveSig]
    int GiveFeedback(uint effect);
}

/// <summary>
/// Der Bild-Helfer von Windows: zeigt beim Ziehen ein Bild der Einträge unter dem Mauszeiger – und, was der Empfänger
/// hineinschreibt („Nach Downloads kopieren“). Beides legt er im Datenobjekt ab (<c>SetData</c>) und liest es von dort.
/// </summary>
[GeneratedComInterface]
[Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
internal unsafe partial interface IDragSourceHelper
{
    [PreserveSig]
    int InitializeFromBitmap(nint image, nint dataObject);

    [PreserveSig]
    int InitializeFromWindow(nint window, Ole.POINT* point, nint dataObject);
}

[GeneratedComInterface]
[Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40")]
internal unsafe partial interface IDragSourceHelper2 : IDragSourceHelper
{
    [PreserveSig]
    int SetFlags(uint flags);
}

internal static unsafe partial class Ole
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    public const int STG_E_READFAULT = unchecked((int)0x8003001E);

    /// <summary>„Vom Benutzer abgebrochen“ (ERROR_CANCELLED) – der Explorer bricht dann ab, ohne einen Fehler zu melden.</summary>
    public const int E_CANCELLED = unchecked((int)0x800704C7);
    public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    public const uint TYMED_HGLOBAL = 1;
    public const uint TYMED_ISTREAM = 4;
    public const uint DVASPECT_CONTENT = 1;
    public const uint DATADIR_GET = 1;
    public const uint DROPEFFECT_COPY = 1;
    public const uint MK_LBUTTON = 1;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;
    public const uint STGTY_STREAM = 2;

    public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    public static readonly Guid IID_IStream = new("0000000C-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDropSource = new("00000121-0000-0000-C000-000000000046");
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    public static readonly Guid IID_IDragSourceHelper2 = new("83E07D0D-0C5F-4163-BF1A-60B274051E40");
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const uint CLSCTX_INPROC_SERVER = 1;

    /// <summary>Der Bild-Helfer darf den Hinweis des Empfängers zeigen („Nach … kopieren“).</summary>
    public const uint DSH_ALLOWDROPDESCRIPTIONTEXT = 1;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial void ReleaseStgMedium(STGMEDIUM* medium);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint memory);

    /// <summary>Ein Strom im Speicher mit einer Kopie dieser Bytes; der Empfänger gibt ihn frei.</summary>
    [LibraryImport("shlwapi.dll")]
    public static partial nint SHCreateMemStream(byte* data, uint size);

    /// <summary>Unsere COM-Objekte entstehen hierüber – ohne die eingebaute COM-Anbindung, die es unter AOT nicht gibt.</summary>
    public static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Der Zeiger auf die Schnittstelle <paramref name="iid"/> eines unserer Objekte; der Aufrufer gibt ihn wieder frei.</summary>
    public static nint InterfaceOf(object instance, Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var pointer));
            return pointer;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static readonly Lock PrepareGate = new();
    private static bool _prepared;

    /// <summary>Richtet einmal ein, was Ziehen und Zwischenablage brauchen. Im Thread der Oberfläche rufen.</summary>
    public static void Prepare()
    {
        lock (PrepareGate)
        {
            if (_prepared)
            {
                return;
            }

            // OLE im Thread der Oberfläche; WinUI hat es meist schon eingerichtet (dann S_FALSE).
            _ = OleInitialize(0);

            // Das Apartment mit vielen Threads soll bestehen bleiben, auch wenn gerade kein Thread darin arbeitet:
            // Dort leben die Datenobjekte, solange ein Empfänger abholt.
            _ = CoIncrementMTAUsage(out _);
            _prepared = true;
        }
    }

    /// <summary>
    /// Macht eines unserer Objekte in einem Thread des Pools bekannt und liefert für den Thread der Oberfläche einen
    /// Stellvertreter. So kommen die Aufrufe eines Empfängers in Threads von COM an und dürfen warten, bis eine
    /// Datei geladen ist; gehörte das Objekt dem Thread der Oberfläche, stünde die App so lange still.
    /// </summary>
    public static nint InOtherThreads(object instance, Guid iid)
    {
        var stream = Task.Run(() =>
        {
            var pointer = InterfaceOf(instance, iid);
            try
            {
                Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(iid, pointer, out var marshalled));
                return marshalled;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }).GetAwaiter().GetResult();

        Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, iid, out var proxy));
        return proxy;
    }

    [LibraryImport("ole32.dll")]
    public static partial int OleSetClipboard(nint dataObject);

    [LibraryImport("ole32.dll")]
    public static partial int OleIsCurrentClipboard(nint dataObject);

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    public static partial int DoDragDrop(nint dataObject, nint dropSource, uint allowedEffects, out uint effect);

    [LibraryImport("ole32.dll")]
    public static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint kind);

    /// <summary>Das Fenster der obersten Ebene, über dem die Maus steht – auch wenn darin ein fremdes Kindfenster liegt.</summary>
    public static nint TopWindowUnderCursor()
    {
        _ = GetCursorPos(out var point);
        var window = WindowFromPoint(point);
        return window == 0 ? 0 : GetAncestor(window, 2 /* GA_ROOT */);
    }

    [LibraryImport("ole32.dll")]
    public static partial int CoMarshalInterThreadInterfaceInStream(in Guid iid, nint unknown, out nint stream);

    [LibraryImport("ole32.dll")]
    public static partial int CoGetInterfaceAndReleaseStream(nint stream, in Guid iid, out nint instance);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateStdEnumFmtEtc(uint count, FORMATETC* formats, out nint enumerator);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string name);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static partial int ScreenToClient(nint window, ref POINT point);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    public static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);
}
