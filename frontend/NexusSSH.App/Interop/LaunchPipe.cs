using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NexusSSH.Core;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Je Datenordner läuft eine App. Ein zweiter Start reicht seine Argumente an die laufende weiter, statt eine zweite
/// daneben zu öffnen: mit Ziel (Link, Kommandozeile, Sprungliste) öffnet sie das Ziel, ohne holt sie ihr Fenster nach
/// vorn. Zwei Apps auf einem Datenordner überschrieben sich beim Beenden gegenseitig die Einstellungen (samt den
/// Freigaben für MCP), und MCP bliebe stumm bei der zuerst gestarteten (gefunden 2026-11-21). Ob schon eine läuft,
/// sagt ein Mutex (<see cref="Claim"/>), sofort und ohne Warten. Weitergereicht wird über eine Named Pipe, die nur
/// Prozesse desselben Benutzers erreichen (<see cref="PipeOptions.CurrentUserOnly"/>, in beiden Richtungen – auch der
/// Client prüft, wem die Pipe gehört). Je Datenordner ein eigener Mutex und eine eigene Pipe: Ein Prüflauf mit
/// <c>NEXUSSSH_DATA_DIR</c> landet nie in der App des Benutzers. Was durch die Pipe kommt, ist fremde Eingabe wie
/// jedes Argument – es geht durch <see cref="LaunchRequest"/>, und ein Ziel verbindet erst nach der Rückfrage.
/// </summary>
internal static partial class LaunchPipe
{
    private const int MaxBytes = 4096;

    // Gehört dem Thread der Oberfläche, solange der Prozess läuft; freigegeben wird er nie von Hand, sondern mit dem
    // Ende des Prozesses (dann gilt er als verlassen, und der nächste Start bekommt ihn). Das Feld hält den Griff
    // offen – räumte die Speicherbereinigung ihn weg, gäbe es den Mutex nicht mehr.
    private static Mutex? _held;

    private static readonly CancellationTokenSource Stopping = new();

    private static string Key()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(AppDataLocation.Root.ToUpperInvariant()));
        return Convert.ToHexString(key, 0, 12);
    }

    private static string Name() => "NexusSSH-launch-" + Key();

    /// <summary>
    /// Nimmt den Datenordner für diese App in Anspruch; <c>false</c>, wenn schon eine andere App ihn hat. Lässt sich
    /// der Mutex gar nicht anlegen (ein fremder Prozess hält den Namen mit anderen Rechten), startet die App wie
    /// früher – lieber zwei Apps als keine.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, @"Local\NexusSSH-app-" + Key(), out var created);
            return created;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Wartet, bis die andere App den Datenordner freigibt – sie beendet sich meist gerade (eben geschlossen, die
    /// Sitzungen werden noch getrennt); <c>true</c>, wenn er jetzt dieser App gehört. Nur nach <see cref="Claim"/>
    /// mit <c>false</c> und im selben Thread.
    /// </summary>
    public static bool AwaitClaim(TimeSpan patience)
    {
        if (_held is not { } mutex)
        {
            return true;
        }

        try
        {
            return mutex.WaitOne(patience);
        }
        catch (AbandonedMutexException)
        {
            // Die andere App endete, ohne ihn freizugeben – so endet jede.
            return true;
        }
    }

    /// <summary>
    /// Nimmt keine Starts mehr an: Die App beendet sich. Ein Start in dieser Zeit findet die Pipe nicht mehr und
    /// wartet, bis der Prozess weg ist (<see cref="AwaitClaim"/>), statt an eine App zu gehen, die nichts mehr zeigt.
    /// </summary>
    public static void Stop() => Stopping.Cancel();

    /// <summary>
    /// Versucht, die Argumente an die laufende App zu geben (nur nach <see cref="Claim"/> mit <c>false</c>);
    /// <c>true</c>, wenn sie angekommen sind (dann endet dieser Start). Die laufende App darf danach ihr Fenster nach
    /// vorn holen – das Recht dazu hat dieser Start, weil der Benutzer ihn eben ausgelöst hat.
    /// </summary>
    public static bool TryHandOver(IReadOnlyList<string> arguments)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name(), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            // Eine Sekunde: lange genug, dass eine App, die eben erst startet, schon lauscht (den Mutex nimmt sie
            // kurz davor). Eine App, die sich gerade beendet, lauscht nicht mehr – dann wartet Connect die Frist ab.
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

            // Angekommen ist der Start erst, wenn die App es sagt – und das tut sie aus dem Thread ihrer Oberfläche
            // (Listen). Verbinden und Schreiben erledigt der Kernel auch für eine App, die nicht mehr läuft: In
            // der Sandbox nahm eine angehaltene App den Start so „an“, und er endete still (2026-11-21). Kommt die
            // Antwort nicht in der Frist, bleibt der Faden hängen, bis der Prozess endet.
            var message = new byte[4 + bytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(message, bytes.Length);
            bytes.CopyTo(message, 4);
            var delivered = Task.Run(() =>
            {
                pipe.Write(message);
                pipe.Flush();
                return pipe.ReadByte() == Taken;
            });
            return delivered.Wait(TimeSpan.FromSeconds(4)) && delivered.Result;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>Die Antwort der laufenden App: Ihre Oberfläche hat den Start angenommen.</summary>
    private const byte Taken = 1;

    private static readonly byte[] TakenReply = [Taken];

    /// <summary>
    /// Nimmt Starts entgegen, bis <see cref="Stop"/> fällt; <paramref name="received"/> läuft im Thread der
    /// Oberfläche, mit dem Ziel oder mit <c>null</c> für einen Start ohne Argumente (dann nur das Fenster nach vorn).
    /// Läuft schon eine andere App mit diesem Datenordner, bleibt es bei ihr – dann lauscht diese nicht. Eine
    /// Nachricht ist ihre Länge (vier Bytes) und die Argumente; die Antwort (<see cref="Taken"/>) geht erst hinaus,
    /// wenn der Thread der Oberfläche den Start angenommen hat – auch einen, der nichts verlangt.
    /// </summary>
    public static void Listen(Microsoft.UI.Dispatching.DispatcherQueue queue, Action<LaunchRequest?> received)
    {
        var stop = Stopping.Token;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(Name(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return;
                }

                await using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(stop);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var head = new byte[4];
                        await pipe.ReadExactlyAsync(head, timeout.Token);
                        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
                        if (length is < 0 or > MaxBytes)
                        {
                            continue;
                        }

                        var buffer = new byte[length];
                        await pipe.ReadExactlyAsync(buffer, timeout.Token);

                        // Ohne Argumente: Der Benutzer hat die App geöffnet und will sie sehen (null). Mit Argumenten,
                        // die kein Ziel ergeben (etwa nur `--no-activate`): angenommen, aber nichts zu tun.
                        var request = length == 0 ? null : LaunchRequest.FromArguments(Encoding.UTF8.GetString(buffer).Split('\n'));
                        var act = length == 0 || request is not null;
                        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!queue.TryEnqueue(() =>
                        {
                            if (act)
                            {
                                received(request);
                            }

                            taken.TrySetResult();
                        }))
                        {
                            continue;
                        }

                        await taken.Task.WaitAsync(TimeSpan.FromSeconds(2), stop);
                        await pipe.WriteAsync(TakenReply, timeout.Token);
                        await pipe.FlushAsync(timeout.Token);
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException or EndOfStreamException)
                    {
                        // Ein Start, der nichts Lesbares schickte, oder eine Oberfläche, die nicht antwortete – ohne
                        // Antwort sagt der Start das selbst. Der nächste darf.
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
