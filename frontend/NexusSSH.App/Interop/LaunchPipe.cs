using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NexusSSH.Core;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Ein zweiter Start mit Ziel (Link, Kommandozeile, Sprungliste) reicht es an die App weiter, die schon läuft, statt
/// eine zweite daneben zu öffnen: über eine Named Pipe, die nur Prozesse desselben Benutzers erreichen
/// (<see cref="PipeOptions.CurrentUserOnly"/>, in beiden Richtungen – auch der Client prüft, wem die Pipe gehört).
/// Je Datenordner eine eigene Pipe: Ein Prüflauf mit <c>NEXUSSSH_DATA_DIR</c> landet nie in der App des Benutzers.
/// Was durch die Pipe kommt, ist fremde Eingabe wie jedes Argument – es geht durch <see cref="LaunchRequest"/>, und
/// ein Ziel verbindet erst nach der Rückfrage.
/// </summary>
internal static partial class LaunchPipe
{
    private const int MaxBytes = 4096;

    private static string Name()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(AppDataLocation.Root.ToUpperInvariant()));
        return "NexusSSH-launch-" + Convert.ToHexString(key, 0, 12);
    }

    /// <summary>
    /// Versucht, die Argumente an die laufende App zu geben; <c>true</c>, wenn sie angekommen sind (dann endet
    /// dieser Start). Die laufende App darf danach ihr Fenster nach vorn holen – das Recht dazu hat dieser Start,
    /// weil der Benutzer ihn eben ausgelöst hat.
    /// </summary>
    public static bool TryHandOver(IReadOnlyList<string> arguments)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name(), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Eine Sekunde: lange genug, dass eine App, die eben erst startet, schon lauscht. Ohne laufende App
            // kehrt Connect erst nach der Frist zurück – um so viel später beginnt ein Start mit Ziel.
            pipe.Connect(1000);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server))
            {
                _ = AllowSetForegroundWindow(server);
            }

            var text = string.Join('\n', arguments.Take(LaunchRequest.MaxArguments * 2));
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaxBytes)
            {
                return false;
            }

            pipe.Write(bytes);
            pipe.Flush();
            pipe.WaitForPipeDrain();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Nimmt Starts entgegen, solange die App läuft; <paramref name="received"/> läuft im Thread der Oberfläche.
    /// Läuft schon eine andere App mit diesem Datenordner, bleibt es bei ihr – dann lauscht diese nicht.
    /// </summary>
    public static void Listen(Microsoft.UI.Dispatching.DispatcherQueue queue, Action<LaunchRequest> received)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(Name(), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return;
                }

                await using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var buffer = new byte[MaxBytes + 1];
                        var length = 0;
                        int read;
                        while (length <= MaxBytes && (read = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
                        {
                            length += read;
                        }

                        if (length > MaxBytes)
                        {
                            continue;
                        }

                        var arguments = Encoding.UTF8.GetString(buffer, 0, length).Split('\n');
                        if (LaunchRequest.FromArguments(arguments) is { } request)
                        {
                            queue.TryEnqueue(() => received(request));
                        }
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or DecoderFallbackException)
                    {
                        // Ein Start, der nichts Lesbares schickte – der nächste darf.
                    }
                }
            }
        });
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint process);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint process);
}
