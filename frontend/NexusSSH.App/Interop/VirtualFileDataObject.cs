using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Bietet Dateien und Ordner an, die es auf diesem Rechner noch nicht gibt – so, wie der Explorer es von einem
/// Archiv oder einem Mail-Anhang kennt: eine Liste (<c>FileGroupDescriptorW</c>, mit Unterordnern) und zu jedem
/// Eintrag ein Strom (<c>FileContents</c>), der erst gelesen wird, wenn die Datei geschrieben wird.
/// <para>
/// Der Empfänger holt die Daten in einem eigenen Thread ab (<see cref="IDataObjectAsyncCapability"/>) und zeigt
/// dabei seinen Kopierdialog. Was er verlangt, liefert <see cref="IVirtualFiles"/>; hier wird nur übersetzt.
/// </para>
/// <para>
/// Das Objekt gehört dem Apartment mit vielen Threads (<see cref="Ole.InOtherThreads"/>): Seine Aufrufe warten,
/// bis eine Datei geladen ist – im Thread der Oberfläche stünde die App so lange still.
/// </para>
/// <para>
/// Dasselbe Objekt dient dem Ziehen (<see cref="OleDrag"/>) und der Zwischenablage (<see cref="OleClipboard"/>).
/// Dort kann es mehrmals abgeholt werden – jedes Einfügen ist ein eigener Durchgang.
/// </para>
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class VirtualFileDataObject(IVirtualFiles files, nint ownWindow) : IOleDataObject, IDataObjectAsyncCapability
{
    private const uint FD_ATTRIBUTES = 0x0004;
    private const uint FD_WRITESTIME = 0x0020;
    private const uint FD_FILESIZE = 0x0040;
    private const uint FD_PROGRESSUI = 0x4000;
    private const uint FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // FILEDESCRIPTORW: Schalter, CLSID, Größe und Lage eines Symbols, Attribute, drei Zeiten, Dateigröße, Name.
    private const int DescriptorSize = 592;
    private const int NameOffset = 72;
    private const int NameLength = 260;

    private static readonly ushort Descriptor = (ushort)Ole.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly ushort Contents = (ushort)Ole.RegisterClipboardFormat("FileContents");
    private static readonly ushort PreferredEffect = (ushort)Ole.RegisterClipboardFormat("Preferred DropEffect");

    // Für die Zwischenablage: nicht in ihren Verlauf (Win+V) und nicht auf andere Geräte – was dort läge, gäbe es
    // nach dem Ende der App nicht mehr. Das erste Format wirkt durch sein Vorhandensein, die beiden anderen durch
    // ihren Wert 0.
    private static readonly ushort ExcludeFromMonitors = (ushort)Ole.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly ushort InHistory = (ushort)Ole.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly ushort InCloud = (ushort)Ole.RegisterClipboardFormat("CanUploadToCloudClipboard");

    // Was der Bild-Helfer und der Empfänger hineinschreiben und worauf hier reagiert wird.
    private static readonly ushort DropDescription = (ushort)Ole.RegisterClipboardFormat("DropDescription");
    private static readonly ushort DragWindow = (ushort)Ole.RegisterClipboardFormat("DragWindow");
    private static readonly ushort IsShowingLayered = (ushort)Ole.RegisterClipboardFormat("IsShowingLayered");

    /// <summary>Das Fenster des Ziehbilds soll sich neu zeichnen (DDWM_UPDATEWINDOW).</summary>
    private const uint DDWM_UPDATEWINDOW = 0x0400 + 3;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private int _async = 1;
    private int _inOperation;
    private volatile bool _dragging;

    // Was andere hineingeschrieben haben (SetData): das Bild des Bild-Helfers, der Hinweis des Empfängers („Nach …
    // kopieren“), „Performed DropEffect“. Wer es abfragt, bekommt eine Kopie.
    private readonly Lock _storedGate = new();
    private readonly Dictionary<ushort, Kept> _stored = [];

    public int GetData(FORMATETC* format, STGMEDIUM* medium)
    {
        if (format is null || medium is null)
        {
            return Ole.E_INVALIDARG;
        }

        *medium = default;

        // Was hineingeschrieben wurde, gilt überall – auch über dem eigenen Fenster: Dort beginnt das Ziehen, und der
        // Bild-Helfer liest sein Bild von Anfang an von hier.
        if (Stored(format->Format) is { } stored)
        {
            return (format->Tymed & stored.Tymed) == 0 ? Ole.DV_E_TYMED
                : stored.Tymed == Ole.TYMED_ISTREAM ? StreamOf(stored.Data, medium)
                : Global(stored.Data, medium);
        }

        if (OverOwnWindow())
        {
            return Ole.DV_E_FORMATETC;
        }

        try
        {
            if (format->Format == Descriptor && (format->Tymed & Ole.TYMED_HGLOBAL) != 0)
            {
                if (Entries() is not { Count: > 0 } entries)
                {
                    return Ole.E_FAIL;
                }

                return Global(DescriptorOf(entries), medium);
            }

            if (IsFlag(format->Format) && (format->Tymed & Ole.TYMED_HGLOBAL) != 0)
            {
                var value = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(value, format->Format == PreferredEffect ? Ole.DROPEFFECT_COPY : 0);
                return Global(value, medium);
            }

            if (format->Format == Contents && (format->Tymed & Ole.TYMED_ISTREAM) != 0)
            {
                if (Entries() is not { } entries || format->Index < 0 || format->Index >= entries.Count || entries[format->Index].IsDirectory)
                {
                    return Ole.DV_E_FORMATETC;
                }

                medium->Tymed = Ole.TYMED_ISTREAM;
                medium->Value = Ole.InterfaceOf(new VirtualFileStream(files, format->Index, entries[format->Index].Size), Ole.IID_IStream);
                return Ole.S_OK;
            }

            return Ole.DV_E_FORMATETC;
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
            return Ole.E_FAIL;
        }
    }

    public int GetDataHere(FORMATETC* format, STGMEDIUM* medium) => Ole.E_NOTIMPL;

    public int QueryGetData(FORMATETC* format)
    {
        if (format is null)
        {
            return Ole.E_INVALIDARG;
        }

        if (Stored(format->Format) is { } stored)
        {
            return (format->Tymed & stored.Tymed) != 0 ? Ole.S_OK : Ole.DV_E_TYMED;
        }

        if (OverOwnWindow())
        {
            return Ole.DV_E_FORMATETC;
        }

        var offered = (format->Format == Descriptor || IsFlag(format->Format)) && (format->Tymed & Ole.TYMED_HGLOBAL) != 0
            || format->Format == Contents && (format->Tymed & Ole.TYMED_ISTREAM) != 0;
        return offered ? Ole.S_OK : Ole.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(FORMATETC* format, FORMATETC* result)
    {
        if (result is not null)
        {
            result->TargetDevice = 0;
        }

        return Ole.E_NOTIMPL;
    }

    // Hier legen der Bild-Helfer sein Bild (als Speicher, seinen Zustand als Strom) und der Empfänger seinen Hinweis
    // ab; der Empfänger vermerkt auch, was er getan hat („Performed DropEffect“). Aufbewahrt wird eine Kopie. Was
    // dieses Objekt selbst anbietet, lässt sich nicht überschreiben: Ein „Preferred DropEffect“ von außen machte
    // aus dem Kopieren nichts – der Explorer nähme dann gar nichts mehr an.
    public int SetData(FORMATETC* format, STGMEDIUM* medium, int release)
    {
        if (format is null || medium is null)
        {
            return Ole.E_INVALIDARG;
        }

        byte[]? copy = medium->Value == 0 || IsOwn(format->Format) ? null : medium->Tymed switch
        {
            Ole.TYMED_HGLOBAL => FromGlobal(medium->Value),
            Ole.TYMED_ISTREAM => FromStream(medium->Value),
            _ => null,
        };

        if (copy is not null)
        {
            lock (_storedGate)
            {
                _stored[format->Format] = new Kept(copy, medium->Tymed);
            }

            // Der Empfänger hat seinen Hinweis geändert („Nach … kopieren“). Das Fenster des Ziehbilds erfährt das
            // nicht von selbst – bei Daten des Explorers sorgt dessen eigenes Datenobjekt dafür, hier dieses.
            if (format->Format == DropDescription)
            {
                RefreshDragImage();
            }
        }

        // Gehört der Speicher jetzt uns, geben wir ihn frei – die Kopie reicht.
        if (release != 0)
        {
            Ole.ReleaseStgMedium(medium);
        }

        return copy is not null || IsOwn(format->Format) ? Ole.S_OK : Ole.DV_E_TYMED;
    }

    /// <summary>Lässt das Fenster des Ziehbilds neu zeichnen – samt dem Hinweis des Empfängers.</summary>
    public void RefreshDragImage()
    {
        if (Stored(DragWindow) is { Data.Length: >= 4 } window)
        {
            _ = Ole.PostMessage((nint)BinaryPrimitives.ReadInt32LittleEndian(window.Data), DDWM_UPDATEWINDOW, 0, 0);
        }
    }

    /// <summary>Ob das Ziehbild gerade als eigenes Fenster zu sehen ist – dann zeigt es auch den Hinweis des Empfängers.</summary>
    public bool ShowsDragImage => Stored(IsShowingLayered) is { Data.Length: >= 4 } kept && BinaryPrimitives.ReadInt32LittleEndian(kept.Data) != 0;

    private static bool IsOwn(ushort format) => format == Descriptor || format == Contents || IsFlag(format);

    private static byte[]? FromGlobal(nint memory)
    {
        var size = (int)Ole.GlobalSize(memory);
        var source = Ole.GlobalLock(memory);
        if (source == 0)
        {
            return null;
        }

        try
        {
            return new ReadOnlySpan<byte>((void*)source, size).ToArray();
        }
        finally
        {
            Ole.GlobalUnlock(memory);
        }
    }

    // Der Strom wird von Anfang an ganz gelesen; er ist klein (der Zustand des Bild-Helfers).
    private static byte[]? FromStream(nint pointer)
    {
        var stream = (IOleStream)Ole.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        STATSTG stat;
        if (stream.Stat(&stat, 1 /* STATFLAG_NONAME */) < 0 || stat.Size > 16 * 1024 * 1024 || stream.Seek(0, 0, null) < 0)
        {
            return null;
        }

        var data = new byte[(int)stat.Size];
        fixed (byte* target = data)
        {
            uint read = 0;
            var total = 0;
            while (total < data.Length && stream.Read(target + total, (uint)(data.Length - total), &read) >= 0 && read > 0)
            {
                total += (int)read;
            }

            return total == data.Length ? data : data[..total];
        }
    }

    // Ein neuer Strom mit einer Kopie dessen, was als Strom hineingeschrieben wurde; der Empfänger gibt ihn frei.
    private static int StreamOf(byte[] data, STGMEDIUM* medium)
    {
        fixed (byte* source = data)
        {
            var stream = Ole.SHCreateMemStream(source, (uint)data.Length);
            if (stream == 0)
            {
                return Ole.E_OUTOFMEMORY;
            }

            medium->Tymed = Ole.TYMED_ISTREAM;
            medium->Value = stream;
            return Ole.S_OK;
        }
    }

    /// <summary>Was hineingeschrieben wurde: der Inhalt und ob als Speicher oder als Strom.</summary>
    private readonly record struct Kept(byte[] Data, uint Tymed);

    private Kept? Stored(ushort format)
    {
        lock (_storedGate)
        {
            return _stored.TryGetValue(format, out var kept) ? kept : null;
        }
    }

    public int EnumFormatEtc(uint direction, nint* enumerator)
    {
        if (enumerator is null)
        {
            return Ole.E_INVALIDARG;
        }

        *enumerator = 0;
        if (direction != Ole.DATADIR_GET)
        {
            return Ole.E_NOTIMPL;
        }

        // Über dem eigenen Fenster gar nichts – auch nicht, was hineingeschrieben wurde (siehe OverOwnWindow). Windows
        // merkt sich die Aufzählung von dort für das ganze Ziehen: Stünde darin nur das Bild des Bild-Helfers, fände
        // der Explorer die Dateien nie und nähme nichts an. Eine leere Aufzählung fragt er später neu ab. Der
        // Bild-Helfer fragt seine Formate ohnehin mit Namen ab, nicht über die Aufzählung.
        List<(ushort Format, uint Tymed)> offered = [];
        if (!OverOwnWindow())
        {
            offered.AddRange(
            [
                (Descriptor, Ole.TYMED_HGLOBAL), (Contents, Ole.TYMED_ISTREAM), (PreferredEffect, Ole.TYMED_HGLOBAL),
                (ExcludeFromMonitors, Ole.TYMED_HGLOBAL), (InHistory, Ole.TYMED_HGLOBAL), (InCloud, Ole.TYMED_HGLOBAL),
            ]);
            lock (_storedGate)
            {
                offered.AddRange(_stored.Select(stored => (stored.Key, stored.Value.Tymed)));
            }
        }

        var formats = stackalloc FORMATETC[offered.Count];
        for (var i = 0; i < offered.Count; i++)
        {
            formats[i] = new FORMATETC { Format = offered[i].Format, Aspect = Ole.DVASPECT_CONTENT, Index = -1, Tymed = offered[i].Tymed };
        }

        return Ole.SHCreateStdEnumFmtEtc((uint)offered.Count, formats, out *enumerator);
    }

    public int DAdvise(FORMATETC* format, uint flags, nint sink, uint* connection) => Ole.OLE_E_ADVISENOTSUPPORTED;

    public int DUnadvise(uint connection) => Ole.OLE_E_ADVISENOTSUPPORTED;

    public int EnumDAdvise(nint* enumerator) => Ole.OLE_E_ADVISENOTSUPPORTED;

    public int SetAsyncMode(int doAsync)
    {
        _async = doAsync;
        return Ole.S_OK;
    }

    public int GetAsyncMode(int* isAsync)
    {
        if (isAsync is null)
        {
            return Ole.E_INVALIDARG;
        }

        *isAsync = _async;
        return Ole.S_OK;
    }

    public int StartOperation(nint bindContext)
    {
        _inOperation = 1;
        return Ole.S_OK;
    }

    public int InOperation(int* inOperation)
    {
        if (inOperation is null)
        {
            return Ole.E_INVALIDARG;
        }

        *inOperation = _inOperation;
        return Ole.S_OK;
    }

    public int EndOperation(int result, nint bindContext, uint effects)
    {
        _inOperation = 0;
        Complete(result >= 0 && effects != 0);
        return Ole.S_OK;
    }

    /// <summary>Gesetzt, solange gezogen wird (<see cref="OleDrag"/>).</summary>
    public bool IsDragging
    {
        get => _dragging;
        set => _dragging = value;
    }

    // Solange die Maus beim Ziehen über dem eigenen Fenster steht, wird nichts angeboten. Dort beginnt jedes
    // Ziehen, und WinUI versucht sofort, aus dem Angebot Dateien zu machen: Mit Ordnern darin scheitert das (ein
    // unbehandelter Fehler der Oberfläche), und fallen lassen könnte man die Einträge dort ohnehin nur dorthin, wo
    // sie herkommen. Gefragt wird dabei nicht aus diesem Prozess, sondern über den Vermittler von Windows –
    // deshalb entscheidet die Stelle der Maus, nicht der Aufrufer. Hat ein Empfänger übernommen, gilt das nicht
    // mehr: Er holt ab, wo immer die Maus inzwischen ist.
    private bool OverOwnWindow() => _dragging && _inOperation == 0 && Ole.TopWindowUnderCursor() == ownWindow;

    /// <summary>Ob der Empfänger die Daten gerade im Hintergrund abholt; dann meldet er sich selbst, wenn er fertig ist.</summary>
    public bool IsInOperation => _inOperation != 0;

    private static bool IsFlag(ushort format)
        => format == PreferredEffect || format == ExcludeFromMonitors || format == InHistory || format == InCloud;

    /// <summary>Ein Durchgang ist zu Ende: Der Empfänger hat alles, hat aufgegeben, oder es gab keinen.</summary>
    public void Complete(bool succeeded)
    {
        try
        {
            files.Completed(succeeded);
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    // Wartet auf die Liste – der Aufruf kommt aus einem Thread von COM, nie aus dem der Oberfläche. Aber nicht
    // beliebig lange: Der Empfänger fragt schon, wenn die Maus über ihm steht, und sein Fenster steht, solange er
    // wartet. Ist die Liste bis dahin nicht da, bekommt er diesmal nichts und fragt beim Loslassen wieder.
    private IReadOnlyList<VirtualEntry>? Entries()
    {
        var entries = files.EntriesAsync();
        return entries.Wait(Patience) ? entries.Result : null;
    }

    private static int Global(ReadOnlySpan<byte> data, STGMEDIUM* medium)
    {
        var memory = Ole.GlobalAlloc(Ole.GMEM_MOVEABLE | Ole.GMEM_ZEROINIT, (nuint)data.Length);
        if (memory == 0)
        {
            return Ole.E_OUTOFMEMORY;
        }

        data.CopyTo(new Span<byte>((void*)Ole.GlobalLock(memory), data.Length));
        Ole.GlobalUnlock(memory);
        medium->Tymed = Ole.TYMED_HGLOBAL;
        medium->Value = memory;
        return Ole.S_OK;
    }

    // FILEGROUPDESCRIPTORW: die Anzahl, dann je Eintrag ein FILEDESCRIPTORW.
    private static byte[] DescriptorOf(IReadOnlyList<VirtualEntry> entries)
    {
        var data = new byte[4 + (entries.Count * DescriptorSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var descriptor = data.AsSpan(4 + (i * DescriptorSize), DescriptorSize);
            var flags = FD_ATTRIBUTES | FD_PROGRESSUI | FD_UNICODE;
            if (!entry.IsDirectory)
            {
                flags |= FD_FILESIZE;
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[64..], (uint)((ulong)entry.Size >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[68..], (uint)((ulong)entry.Size & 0xFFFFFFFF));
            }

            if (entry.Modified > DateTimeOffset.MinValue.AddYears(1601))
            {
                flags |= FD_WRITESTIME;
                BinaryPrimitives.WriteInt64LittleEndian(descriptor[56..], entry.Modified.ToFileTime());
            }

            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[36..], entry.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);

            // Der Name samt Unterordnern, mit \ getrennt und mit einer Null am Ende.
            var name = entry.Path.Replace('/', '\\');
            Encoding.Unicode.GetBytes(name.AsSpan(0, Math.Min(name.Length, NameLength - 1)), descriptor[NameOffset..]);
        }

        return data;
    }
}

/// <summary>Der Inhalt einer angebotenen Datei. Geladen wird beim ersten Lesen; so lange wartet der Empfänger.</summary>
[GeneratedComClass]
internal sealed unsafe partial class VirtualFileStream(IVirtualFiles files, int index, long size) : IOleStream
{
    private readonly Lock _gate = new();
    private Stream? _stream;
    private bool _failed;
    private long _position;

    public int Read(byte* buffer, uint count, uint* read)
    {
        if (read is not null)
        {
            *read = 0;
        }

        if (buffer is null)
        {
            return Ole.E_INVALIDARG;
        }

        try
        {
            lock (_gate)
            {
                if (_position >= size && _stream is null && !_failed)
                {
                    return Ole.S_FALSE;
                }

                if (Source() is not { } source)
                {
                    return Ole.STG_E_ACCESSDENIED;
                }

                var target = new Span<byte>(buffer, checked((int)count));
                var total = 0;
                while (total < target.Length)
                {
                    var got = source.Read(target[total..]);
                    if (got == 0)
                    {
                        break;
                    }

                    total += got;
                }

                _position += total;
                if (read is not null)
                {
                    *read = (uint)total;
                }

                // Am Ende gleich schließen: Wann der Empfänger den Strom freigibt, erfährt hier niemand.
                if (total < target.Length)
                {
                    source.Dispose();
                    _stream = null;
                }

                return total == target.Length ? Ole.S_OK : Ole.S_FALSE;
            }
        }
        catch (OperationCanceledException)
        {
            // Der Transfer wurde in der App abgebrochen: Der Empfänger soll aufhören, aber keinen Fehler melden.
            _failed = true;
            return Ole.E_CANCELLED;
        }
        catch (IOException)
        {
            // Die Datei ist nicht vollständig gekommen; warum, steht in der App unter „Transfers“.
            _failed = true;
            return Ole.STG_E_READFAULT;
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
            return Ole.STG_E_ACCESSDENIED;
        }
    }

    public int Write(byte* buffer, uint count, uint* written) => Ole.STG_E_ACCESSDENIED;

    public int Seek(long move, uint origin, ulong* position)
    {
        lock (_gate)
        {
            var target = origin switch
            {
                0 => move,
                1 => _position + move,
                2 => size + move,
                _ => -1,
            };
            if (target < 0)
            {
                return Ole.STG_E_INVALIDFUNCTION;
            }

            if (target != _position)
            {
                _stream?.Dispose();
                _stream = null;
                _position = target;
            }

            if (position is not null)
            {
                *position = (ulong)_position;
            }

            return Ole.S_OK;
        }
    }

    public int SetSize(ulong size) => Ole.STG_E_ACCESSDENIED;

    public int CopyTo(nint target, ulong count, ulong* read, ulong* written) => Ole.E_NOTIMPL;

    public int Commit(uint flags) => Ole.S_OK;

    public int Revert() => Ole.E_NOTIMPL;

    public int LockRegion(ulong offset, ulong count, uint type) => Ole.STG_E_INVALIDFUNCTION;

    public int UnlockRegion(ulong offset, ulong count, uint type) => Ole.STG_E_INVALIDFUNCTION;

    public int Stat(STATSTG* stat, uint flags)
    {
        if (stat is null)
        {
            return Ole.E_INVALIDARG;
        }

        *stat = new STATSTG { Type = Ole.STGTY_STREAM, Size = (ulong)size };
        return Ole.S_OK;
    }

    public int Clone(nint* stream) => Ole.E_NOTIMPL;

    // Der Strom wird nur vorwärts gelesen; wer weiter hinten anfangen will, überliest den Anfang.
    private static void Skip(Stream stream, long bytes)
    {
        var buffer = new byte[1 << 16];
        while (bytes > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
            if (read == 0)
            {
                return;
            }

            bytes -= read;
        }
    }

    // Der Aufruf kommt aus einem Thread des Empfängers, nie aus dem der Oberfläche – er darf warten.
    private Stream? Source()
    {
        if (_stream is null && !_failed)
        {
            _stream = files.OpenAsync(index).GetAwaiter().GetResult();
            if (_stream is null)
            {
                _failed = true;
            }
            else if (_position > 0)
            {
                Skip(_stream, _position);
            }
        }

        return _stream;
    }
}
