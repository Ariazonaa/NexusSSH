using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexusSSH.Core;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

// Die Sprungliste (Rechtsklick auf das Symbol in der Taskleiste): ICustomDestinationList und was sie braucht, über
// Quellgeneratoren wie die übrigen COM-Teile (AOT). Nur die Methoden bis zur letzten benutzten – die Reihenfolge ist
// die der Tabelle der Schnittstelle.

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
internal partial interface ICustomDestinationList
{
    [PreserveSig]
    int SetAppID(string id);

    [PreserveSig]
    int BeginList(out uint minSlots, in Guid iid, out nint removed);

    [PreserveSig]
    int AppendCategory(string category, nint objects);

    [PreserveSig]
    int AppendKnownCategory(int category);

    [PreserveSig]
    int AddUserTasks(nint objects);

    [PreserveSig]
    int CommitList();

    [PreserveSig]
    int GetRemovedDestinations(in Guid iid, out nint removed);

    [PreserveSig]
    int DeleteList(string? id);

    [PreserveSig]
    int AbortList();
}

[GeneratedComInterface]
[Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
internal partial interface IObjectCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, in Guid iid, out nint item);

    [PreserveSig]
    int AddObject(nint item);
}

[GeneratedComInterface]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal partial interface IObjectArray
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, in Guid iid, out nint item);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig]
    int GetPath(char* file, int size, nint findData, uint flags);

    [PreserveSig]
    int GetIDList(out nint list);

    [PreserveSig]
    int SetIDList(nint list);

    [PreserveSig]
    int GetDescription(char* text, int size);

    [PreserveSig]
    int SetDescription(string text);

    [PreserveSig]
    int GetWorkingDirectory(char* directory, int size);

    [PreserveSig]
    int SetWorkingDirectory(string directory);

    [PreserveSig]
    int GetArguments(char* arguments, int size);

    [PreserveSig]
    int SetArguments(string arguments);

    [PreserveSig]
    int GetHotkey(out ushort key);

    [PreserveSig]
    int SetHotkey(ushort key);

    [PreserveSig]
    int GetShowCmd(out int show);

    [PreserveSig]
    int SetShowCmd(int show);

    [PreserveSig]
    int GetIconLocation(char* path, int size, out int index);

    [PreserveSig]
    int SetIconLocation(string path, int index);

    [PreserveSig]
    int SetRelativePath(string path, uint reserved);

    [PreserveSig]
    int Resolve(nint window, uint flags);

    [PreserveSig]
    int SetPath(string file);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid FormatId;
    public uint PropertyId;
}

// Ein PROPVARIANT mit einer Zeichenkette (VT_LPWSTR) – mehr braucht es hier nicht.
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Rest;
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal unsafe partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, PROPERTYKEY* key);

    [PreserveSig]
    int GetValue(PROPERTYKEY* key, PROPVARIANT* value);

    [PreserveSig]
    int SetValue(PROPERTYKEY* key, PROPVARIANT* value);

    [PreserveSig]
    int Commit();
}

/// <summary>
/// Die zuletzt benutzten Server in der Sprungliste: je ein Eintrag, der die App mit <c>--open &lt;kennung&gt;</c>
/// startet – läuft sie schon, nimmt sie den Start entgegen (<see cref="LaunchPipe"/>). Die Einträge nennen Namen und
/// Kennungen eigener Server, keine Adressen (Windows bewahrt die Liste unter <c>Recent\CustomDestinations</c> auf,
/// auch nach der Deinstallation); was der Benutzer aus der Liste genommen hat, kommt nicht wieder hinein
/// (sonst lehnte Windows die ganze Rubrik ab). Geht es nicht, bleibt die Liste, wie sie war.
/// </summary>
internal static unsafe class JumpList
{
    private static readonly Guid DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid EnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidCustomDestinationList = new("6332DEBF-87B5-4670-90C0-5E57B408A49E");
    private static readonly Guid IidObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid IidObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");
    private static readonly Guid IidShellLink = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private const ushort VtLpwstr = 31;

    // Der Titel eines Eintrags (PKEY_Title).
    private static readonly PROPERTYKEY Title = new() { FormatId = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), PropertyId = 2 };

    private static string? _shown;

    /// <summary>Zeigt diese Server; ohne Änderung seit dem letzten Mal geschieht nichts.</summary>
    public static void Show(IReadOnlyList<ServerProfile> servers, string category)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var wanted = servers.Where(server => LaunchRequest.FromArguments([LaunchRequest.OpenSwitch, server.Id])?.ProfileId == server.Id).ToList();
        var key = category + "\n" + string.Join("\n", wanted.Select(server => server.Id + "\t" + server.Name));
        if (key == _shown)
        {
            return;
        }

        var list = Create<ICustomDestinationList>(DestinationList, IidCustomDestinationList);
        if (list is null)
        {
            return;
        }

        var begun = false;
        try
        {
            if (list.BeginList(out var slots, IidObjectArray, out var removedPointer) < 0)
            {
                return;
            }

            begun = true;
            var removed = RemovedIds(removedPointer);
            var entries = wanted.Where(server => !removed.Contains(server.Id)).Take((int)Math.Max(slots, 1)).ToList();
            if (entries.Count > 0 && !Append(list, entries, category, exe))
            {
                return;
            }

            if (list.CommitList() >= 0)
            {
                _shown = key;
            }

            begun = false;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException or OutOfMemoryException)
        {
            App.WriteCrashLog(exception);
        }
        finally
        {
            // Eine begonnene Liste, die nicht übernommen wurde, bleibt nicht halb stehen.
            if (begun)
            {
                _ = list.AbortList();
            }

            Done(list);
        }
    }

    private static bool Append(ICustomDestinationList list, List<ServerProfile> servers, string category, string exe)
    {
        if (Ole.CoCreateInstance(EnumerableObjectCollection, 0, Ole.CLSCTX_INPROC_SERVER, IidObjectCollection, out var collectionPointer) < 0)
        {
            return false;
        }

        var collection = (IObjectCollection)Ole.Wrappers.GetOrCreateObjectForComInstance(collectionPointer, CreateObjectFlags.UniqueInstance);
        try
        {
            foreach (var server in servers)
            {
                var link = LinkFor(server, exe);
                if (link == 0)
                {
                    continue;
                }

                try
                {
                    _ = collection.AddObject(link);
                }
                finally
                {
                    Marshal.Release(link);
                }
            }

            if (Marshal.QueryInterface(collectionPointer, in IidObjectArray, out var array) < 0)
            {
                return false;
            }

            try
            {
                return list.AppendCategory(category, array) >= 0;
            }
            finally
            {
                Marshal.Release(array);
            }
        }
        finally
        {
            Done(collection);
            Marshal.Release(collectionPointer);
        }
    }

    // Ein Eintrag: die App mit `--open <kennung>`, Titel der Name des Servers.
    private static nint LinkFor(ServerProfile server, string exe)
    {
        if (Ole.CoCreateInstance(ShellLink, 0, Ole.CLSCTX_INPROC_SERVER, IidShellLink, out var pointer) < 0)
        {
            return 0;
        }

        var link = (IShellLinkW)Ole.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        try
        {
            _ = link.SetPath(exe);
            _ = link.SetArguments($"{LaunchRequest.OpenSwitch} {server.Id}");
            _ = link.SetIconLocation(exe, 0);
            if (Marshal.QueryInterface(pointer, in IidPropertyStore, out var storePointer) >= 0)
            {
                var store = (IPropertyStore)Ole.Wrappers.GetOrCreateObjectForComInstance(storePointer, CreateObjectFlags.UniqueInstance);
                var text = Marshal.StringToCoTaskMemUni(Shorten(Format.Visible(server.Name)));
                try
                {
                    var key = Title;
                    var value = new PROPVARIANT { Type = VtLpwstr, Value = text };
                    _ = store.SetValue(&key, &value);
                    _ = store.Commit();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                    Done(store);
                    Marshal.Release(storePointer);
                }
            }
        }
        finally
        {
            Done(link);
        }

        return pointer;
    }

    // Gibt die Referenz eines Wrappers gleich frei, im Thread, in dem das Objekt entstand – nicht erst im Finalizer.
    private static void Done(object wrapper) => (wrapper as ComObject)?.FinalRelease();

    // Die Kennungen der Einträge, die der Benutzer aus der Liste genommen hat.
    private static HashSet<string> RemovedIds(nint arrayPointer)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (arrayPointer == 0)
        {
            return ids;
        }

        try
        {
            var array = (IObjectArray)Ole.Wrappers.GetOrCreateObjectForComInstance(arrayPointer, CreateObjectFlags.UniqueInstance);
            if (array.GetCount(out var count) < 0)
            {
                return ids;
            }

            var buffer = stackalloc char[1024];
            for (uint index = 0; index < count; index++)
            {
                if (array.GetAt(index, IidShellLink, out var linkPointer) < 0)
                {
                    continue;
                }

                var link = (IShellLinkW)Ole.Wrappers.GetOrCreateObjectForComInstance(linkPointer, CreateObjectFlags.UniqueInstance);
                try
                {
                    buffer[0] = '\0';
                    if (link.GetArguments(buffer, 1024) >= 0
                        && LaunchRequest.FromArguments(new string(buffer).Split(' ', StringSplitOptions.RemoveEmptyEntries))?.ProfileId is { } id)
                    {
                        ids.Add(id);
                    }
                }
                finally
                {
                    Done(link);
                    Marshal.Release(linkPointer);
                }
            }

            Done(array);
        }
        finally
        {
            Marshal.Release(arrayPointer);
        }

        return ids;
    }

    private static string Shorten(string text) => text.Length <= 100 ? text : text[..99] + "…";

    private static T? Create<T>(Guid classId, Guid iid)
        where T : class
    {
        if (Ole.CoCreateInstance(classId, 0, Ole.CLSCTX_INPROC_SERVER, iid, out var pointer) < 0)
        {
            return null;
        }

        try
        {
            return Ole.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance) as T;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
