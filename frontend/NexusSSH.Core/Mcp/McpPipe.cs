using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusSSH.Core.Mcp;

/// <summary>
/// Der Weg vom Programm <c>nexusssh-mcp</c> zur laufenden App: eine Named Pipe, die nur Prozesse desselben Benutzers
/// erreichen (<see cref="PipeOptions.CurrentUserOnly"/>, in beiden Richtungen), je Datenordner eine eigene – ein
/// Prüflauf mit <c>NEXUSSSH_DATA_DIR</c> landet nie in der App des Benutzers. Je Aufruf eine Verbindung: Reißt sie,
/// gilt der Aufruf als abgebrochen, und eine offene Rückfrage schließt. Das Benutzerkonto ist alles, was die Pipe prüft;
/// welches Programm anruft, weiß sie nicht – die Sperren sitzen deshalb in der App (<c>ShellViewModel.Mcp.cs</c>).
/// Nachrichten: Länge (4 Bytes), dann JSON.
/// </summary>
public static class McpPipe
{
    /// <summary>So viele Aufrufe laufen höchstens zugleich.</summary>
    private const int MaxCallers = 4;

    /// <summary>Größer ist keine Nachricht; die Antwort auf einen Aufruf bleibt darunter.</summary>
    private const int MaxMessage = 4 * 1024 * 1024;

    /// <summary>So lange wartet ein Aufruf auf die App. Läuft sie nicht, antwortet Windows sofort.</summary>
    private const int ConnectMilliseconds = 1000;

    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(200);

    /// <summary>Der Name der Pipe für diesen Datenordner.</summary>
    public static string Name(string dataFolder)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(dataFolder.ToUpperInvariant()));
        return "NexusSSH-mcp-" + Convert.ToHexString(key, 0, 12);
    }

    /// <summary>Schickt einen Aufruf an die App. <c>null</c>, wenn dort niemand lauscht.</summary>
    public static async Task<McpResult?> CallAsync(string name, McpCall call, CancellationToken cancellation)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ConnectMilliseconds, cancellation);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            await WriteAsync(pipe, EncodeCall(call), cancellation);
            return await ReadAsync(pipe, cancellation) is { } answer && ParseResult(answer) is { } result
                ? result
                : new McpResult("NexusSSH ended the call without an answer", IsError: true);
        }
        catch (IOException)
        {
            return new McpResult("NexusSSH ended the call without an answer", IsError: true);
        }
    }

    /// <summary>
    /// Nimmt Aufrufe an, bis <paramref name="stop"/> fällt; <paramref name="call"/> läuft auf einem Thread des Pools. Eine
    /// zweite App mit demselben Datenordner lauscht nicht.
    /// </summary>
    public static async Task ListenAsync(string name, Func<McpCall, CancellationToken, Task<McpResult>> call, CancellationToken stop)
    {
        var first = true;
        var serving = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, MaxCallers, PipeTransmissionMode.Byte, options);
                first = false;
            }
            catch (Exception exception) when (first && exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                // Alle Verbindungen belegt.
                try
                {
                    await Task.Delay(BusyPause, stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stop);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            serving.RemoveAll(task => task.IsCompleted);
            serving.Add(Task.Run(() => ServeAsync(pipe, call, stop), CancellationToken.None));
        }

        await Task.WhenAll(serving);
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<McpCall, CancellationToken, Task<McpResult>> call, CancellationToken stop)
    {
        await using (pipe)
        {
            try
            {
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop);
                patience.CancelAfter(RequestLimit);
                if (await ReadAsync(pipe, patience.Token) is not { } message || ParseCall(message) is not { } parsed)
                {
                    return;
                }

                // Der Anrufer schickt nichts mehr: Endet das Lesen, ist er weg, und der Aufruf ist abgebrochen.
                using var running = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var watching = WatchAsync(pipe, running);
                McpResult result;
                try
                {
                    result = await call(parsed, running.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await WriteAsync(pipe, EncodeResult(result), stop);
                await pipe.FlushAsync(stop);

                // Erst wenn der Anrufer gelesen und aufgelegt hat: Das Schließen nähme eine ungelesene Antwort mit.
                await watching.WaitAsync(RequestLimit, stop);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or TimeoutException)
            {
                // Der Anrufer ist weg oder die App hört auf; zu sagen gibt es niemandem mehr etwas.
            }
        }
    }

    private static async Task WatchAsync(Stream pipe, CancellationTokenSource running)
    {
        try
        {
            var nothing = new byte[1];
            await pipe.ReadExactlyAsync(nothing, running.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ObjectDisposedException)
        {
            // Gleich danach: Der Anrufer ist weg.
        }

        await running.CancelAsync();
    }

    /// <summary>Liest die Nachricht eines Aufrufs; <c>null</c>, wenn sie keiner ist. Fremde Eingabe.</summary>
    internal static McpCall? ParseCall(ReadOnlySpan<byte> message)
    {
        try
        {
            var reader = new Utf8JsonReader(message);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String
                && root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                    ? new McpCall(tool.GetString()!, arguments.GetRawText())
                    : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static McpResult? ParseResult(ReadOnlySpan<byte> message)
    {
        try
        {
            var reader = new Utf8JsonReader(message);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? new McpResult(text.GetString()!, root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                    : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] EncodeCall(McpCall call) => Encode(writer =>
    {
        writer.WriteString("tool", call.Tool);
        writer.WritePropertyName("arguments");
        writer.WriteRawValue(call.Arguments);
    });

    private static byte[] EncodeResult(McpResult result) => Encode(writer =>
    {
        writer.WriteString("text", result.Text);
        writer.WriteBoolean("error", result.IsError);
    });

    private static byte[] Encode(Action<Utf8JsonWriter> content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            content(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static async Task WriteAsync(Stream pipe, byte[] message, CancellationToken cancellation)
    {
        if (message.Length > MaxMessage)
        {
            throw new IOException("message too long");
        }

        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        await pipe.WriteAsync(length, cancellation);
        await pipe.WriteAsync(message, cancellation);
    }

    private static async Task<byte[]?> ReadAsync(Stream pipe, CancellationToken cancellation)
    {
        var header = new byte[4];
        try
        {
            await pipe.ReadExactlyAsync(header, cancellation);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 0 or > MaxMessage)
            {
                return null;
            }

            var message = new byte[length];
            await pipe.ReadExactlyAsync(message, cancellation);
            return message;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
