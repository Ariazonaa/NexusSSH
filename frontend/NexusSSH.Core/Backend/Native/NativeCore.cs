using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexusSSH.Core.Backend.Native;

/// <summary>
/// Die C-ABI des Rust-Kerns (Quelle: <c>backend/nexus-core/src/ffi.rs</c>). Nur die rohen Aufrufe; benutzt wird der
/// Kern über <see cref="NativeCoreTransport"/>.
/// <para>
/// Bewusst hier und nicht im WinUI-Projekt: Der Name <c>nexus_core</c> wird je Plattform aufgelöst
/// (<c>nexus_core.dll</c>, <c>libnexus_core.so</c>), eine Oberfläche für ein anderes System kann die Anbindung
/// also unverändert übernehmen.
/// </para>
/// </summary>
public static unsafe partial class NativeCore
{
    /// <summary>ABI-Version, gegen die diese App gebaut ist. Zieht mit <c>ABI_VERSION</c> in <c>lib.rs</c> mit.</summary>
    public const uint ExpectedAbi = 16;

    /// <summary><c>kind</c> des Rückrufs: Ereignis als JSON.</summary>
    internal const uint CallbackEvent = 0;

    /// <summary><c>kind</c> des Rückrufs: Terminal-Ausgabe einer Sitzung.</summary>
    internal const uint CallbackOutput = 1;

    private const string Library = "nexus_core";

    /// <summary>Version und ABI des geladenen Kerns; <c>null</c>, wenn die Bibliothek fehlt oder keine von uns ist.</summary>
    public static (string Version, uint Abi)? Probe()
    {
        try
        {
            var abi = AbiVersion();
            var version = Marshal.PtrToStringUTF8(Version());
            return version is null ? null : (version, abi);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    [LibraryImport(Library, EntryPoint = "nexus_core_abi_version")]
    private static partial uint AbiVersion();

    // Statischer, NUL-terminierter UTF-8-String; gehört Rust und wird nicht freigegeben.
    [LibraryImport(Library, EntryPoint = "nexus_core_version")]
    private static partial nint Version();

    [LibraryImport(Library, EntryPoint = "nexus_core_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint Create(
        byte* config,
        nuint configLength,
        delegate* unmanaged[Cdecl]<nint, uint, ulong, byte*, nuint, void> callback,
        nint userData);

    [LibraryImport(Library, EntryPoint = "nexus_core_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint core);

    /// <summary>Liefert einen Puffer, der mit <see cref="Free"/> zurückgegeben werden muss; 0 bei ungültigen Argumenten.</summary>
    [LibraryImport(Library, EntryPoint = "nexus_core_request")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* Request(nint core, byte* request, nuint requestLength, nuint* responseLength);

    [LibraryImport(Library, EntryPoint = "nexus_core_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Free(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "nexus_core_write")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Write(nint core, ulong session, byte* data, nuint length);
}
