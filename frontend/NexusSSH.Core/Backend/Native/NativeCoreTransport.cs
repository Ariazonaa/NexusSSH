using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusSSH.Core.Backend.Native;

/// <summary>
/// <see cref="ICoreTransport"/> über die geladene Rust-Bibliothek. Hält den Kern am Leben und nimmt dessen
/// Rückrufe entgegen – die kommen von Threads des Kerns, nie vom UI-Thread.
/// </summary>
public sealed unsafe class NativeCoreTransport : ICoreTransport
{
    private readonly Lock _gate = new();
    private readonly List<string> _startMessages = [];
    private readonly Action<Exception>? _onCallbackError;
    private GCHandle _self;
    private nint _core;

    /// <summary>Startet den Kern mit <paramref name="dataDirectory"/> als Datenordner. Wirft, wenn das scheitert.</summary>
    /// <param name="sshDirectory">Statt <c>~/.ssh</c> des Benutzers (known_hosts, Standardschlüssel, config); <c>null</c> nimmt dessen Ordner.</param>
    /// <param name="onCallbackError">
    /// Bekommt Ausnahmen, die beim Verarbeiten eines Rückrufs entstehen. Sie dürfen nicht zurück nach Rust
    /// gelangen; ohne Empfänger werden sie verschluckt.
    /// </param>
    public NativeCoreTransport(string dataDirectory, string? sshDirectory = null, Action<Exception>? onCallbackError = null)
    {
        _onCallbackError = onCallbackError;
        var probe = NativeCore.Probe()
            ?? throw new InvalidOperationException("The nexus_core library is missing or not loadable.");
        if (probe.Abi != NativeCore.ExpectedAbi)
        {
            throw new InvalidOperationException(
                $"nexus_core {probe.Version} speaks ABI {probe.Abi}, this app expects {NativeCore.ExpectedAbi}.");
        }

        // Der Kern bekommt einen Handle auf dieses Objekt mit und reicht ihn bei jedem Rückruf zurück.
        _self = GCHandle.Alloc(this);
        Event += Remember;

        // Über den JSON-Writer statt per String-Verkettung: Pfade enthalten Backslashes.
        var data = JsonSerializer.SerializeToUtf8Bytes(dataDirectory, StartJsonContext.Default.String);
        byte[] json = sshDirectory is null
            ? [.. "{\"data_dir\":"u8, .. data, .. "}"u8]
            : [.. "{\"data_dir\":"u8, .. data, .. ",\"ssh_dir\":"u8, .. JsonSerializer.SerializeToUtf8Bytes(sshDirectory, StartJsonContext.Default.String), .. "}"u8];
        fixed (byte* pointer = json)
        {
            _core = NativeCore.Create(pointer, (nuint)json.Length, &OnCallback, GCHandle.ToIntPtr(_self));
        }

        Event -= Remember;
        if (_core == 0)
        {
            _self.Free();
            throw new InvalidOperationException("The core did not start: " + string.Join(" ", _startMessages));
        }

        void Remember(string message) => _startMessages.Add(message);
    }

    public event Action<string>? Event;

    public event Action<ulong, ReadOnlyMemory<byte>>? Output;

    public string Request(string json)
    {
        var request = Encoding.UTF8.GetBytes(json);
        lock (_gate)
        {
            if (_core == 0)
            {
                return """{"ok":false,"error":"core_stopped"}""";
            }

            nuint length = 0;
            byte* response;
            fixed (byte* pointer = request)
            {
                response = NativeCore.Request(_core, pointer, (nuint)request.Length, &length);
            }

            if (response is null)
            {
                return """{"ok":false,"error":"bad_request"}""";
            }

            try
            {
                return Encoding.UTF8.GetString(response, checked((int)length));
            }
            finally
            {
                NativeCore.Free(response, length);
            }
        }
    }

    public bool Write(ulong session, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_core == 0)
            {
                return false;
            }

            fixed (byte* pointer = data)
            {
                return NativeCore.Write(_core, session, pointer, (nuint)data.Length) != 0;
            }
        }
    }

    public void Dispose()
    {
        nint core;
        lock (_gate)
        {
            core = _core;
            _core = 0;
        }

        if (core != 0)
        {
            // Nach Destroy ruft der Kern nicht mehr zurück; erst dann darf der Handle weg.
            NativeCore.Destroy(core);
            _self.Free();
        }
    }

    // Läuft auf einem Thread des Kerns. Die Zeiger gelten nur während des Aufrufs, und keine Ausnahme darf
    // zurück nach Rust gelangen.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCallback(nint userData, uint kind, ulong session, byte* data, nuint length)
    {
        NativeCoreTransport? self = null;
        try
        {
            self = GCHandle.FromIntPtr(userData).Target as NativeCoreTransport;
            if (self is null)
            {
                return;
            }

            var bytes = new ReadOnlySpan<byte>(data, checked((int)length));
            switch (kind)
            {
                case NativeCore.CallbackEvent:
                    self.Event?.Invoke(Encoding.UTF8.GetString(bytes));
                    break;

                case NativeCore.CallbackOutput:
                    self.Output?.Invoke(session, bytes.ToArray());
                    break;
            }
        }
        catch (Exception exception)
        {
            try
            {
                self?._onCallbackError?.Invoke(exception);
            }
            catch (Exception)
            {
                // Auch der Empfänger des Fehlers darf den Rückruf nicht sprengen.
            }
        }
    }
}

[JsonSerializable(typeof(string))]
internal sealed partial class StartJsonContext : JsonSerializerContext;
