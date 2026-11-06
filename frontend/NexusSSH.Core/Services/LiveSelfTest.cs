using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Services;

/// <summary>
/// Der Selbsttest mit einer echten Verbindung (<c>--self-test-live</c>): Die veröffentlichte App verbindet sich mit
/// <c>nexus-testserver</c> auf diesem Rechner, liest die Dateiliste, lädt eine Datei hoch und wieder herunter und
/// vergleicht sie. So zeigt sich unter Native AOT auch der Weg durch den Rust-Kern – der Selbsttest mit
/// Beispieldaten berührt ihn nicht.
/// </summary>
public static class LiveSelfTest
{
    /// <summary>Anmeldung am Testserver, wie <c>tools/self-test.ps1</c> ihn startet.</summary>
    public const string User = "demo";
    public const string Password = "demo";

    /// <summary>Die einzige Adresse, mit der dieser Selbsttest verbindet.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>
    /// Führt den Test aus. Liefert <c>null</c>, wenn alles ging, sonst in einem Satz, woran es lag.
    /// </summary>
    /// <param name="scratch">Ein leerer Ordner für die Datei, die hin- und zurückgeht.</param>
    /// <param name="patience">So lange darf jeder einzelne Schritt dauern.</param>
    public static async Task<string?> RunAsync(ShellViewModel shell, ITransferQueue transfers, int port, string scratch, TimeSpan patience)
    {
        if (shell.SaveProfile(new ServerProfile(string.Empty, "self-test", Host, port, User, string.Empty, [], UseAgent: false)) is not { } profile)
        {
            return "the profile could not be saved";
        }

        var session = shell.OpenSession(profile);
        session.Start();
        if (!await Until(() => session.State is ConnectionState.Connected or ConnectionState.Closed, patience) || session.State != ConnectionState.Connected)
        {
            return $"not connected: {session.StatusText}";
        }

        if (session.Files.FileSystem is not { } files)
        {
            return "connected, but without file access";
        }

        if (!await Until(() => session.Files.Entries.Count > 0, patience))
        {
            return "the file list stayed empty";
        }

        // Eine Datei hin und zurück: Was ankommt, muss sein, was losging.
        Directory.CreateDirectory(scratch);
        var name = $"self-test-{Guid.NewGuid():N}.txt";
        var content = string.Concat(Enumerable.Repeat("NexusSSH self-test äöü\n", 2000));
        var local = Path.Combine(scratch, name);
        await File.WriteAllTextAsync(local, content);
        if (await Finished(transfers, files.Upload(local, session.Files.CurrentPath), patience) is { } upload)
        {
            return $"the upload did not complete: {upload}";
        }

        var back = Path.Combine(scratch, "back");
        Directory.CreateDirectory(back);
        if (await Finished(transfers, files.Download(RemotePath.Combine(session.Files.CurrentPath, name), back), patience) is { } download)
        {
            return $"the download did not complete: {download}";
        }

        var returned = Path.Combine(back, name);
        if (!File.Exists(returned) || await File.ReadAllTextAsync(returned) != content)
        {
            return "the file that came back is not the file that was sent";
        }

        // Die Liste zeigt die neue Datei, wenn sie neu gelesen wird.
        await session.Files.RefreshCommand.ExecuteAsync(null);
        return session.Files.Entries.Any(entry => entry.Name == name) ? null : "the uploaded file is missing from the list";
    }

    /// <summary>
    /// Misst das Tempo des Terminals: lässt den Testserver <paramref name="lines"/> Zeilen schreiben (<c>flood</c>)
    /// und stoppt die Zeit, bis die letzte beim Terminal angekommen ist – <paramref name="backlog"/> nennt, was die
    /// Seite von der Ausgabe noch nicht bestätigt hat. Liefert Millisekunden und Bytes, oder <c>null</c>, wenn es
    /// in der Geduld nicht fertig wurde.
    /// </summary>
    public static async Task<(long Milliseconds, long Bytes)?> FloodAsync(ShellViewModel shell, SessionViewModel session, int lines, Func<long> backlog, TimeSpan patience)
    {
        // „flood done“ steht am Ende der Ausgabe; ein Block kann mitten im Wort enden.
        var marker = "flood done"u8.ToArray();
        var (tail, bytes, seen) = (new List<byte>(), 0L, 0);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(ReadOnlyMemory<byte> data)
        {
            lock (tail)
            {
                bytes += data.Length;
                foreach (var value in data.Span)
                {
                    seen = value == marker[seen] ? seen + 1 : value == marker[0] ? 1 : 0;
                    if (seen == marker.Length)
                    {
                        done.TrySetResult();
                        seen = 0;
                    }
                }
            }
        }

        session.Terminal.Output += OnOutput;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            shell.Input(session, System.Text.Encoding.ASCII.GetBytes($"flood {lines}\r"));
            if (await Task.WhenAny(done.Task, Task.Delay(patience)) != done.Task
                || !await Until(() => backlog() == 0, patience))
            {
                return null;
            }

            lock (tail)
            {
                return (watch.ElapsedMilliseconds, bytes);
            }
        }
        finally
        {
            session.Terminal.Output -= OnOutput;
        }
    }

    /// <summary>
    /// Öffnet ein lokales Terminal (die Eingabeaufforderung, die es unter Windows immer gibt), lässt sie etwas
    /// rechnen und wartet auf das Ergebnis – das Eingetippte steht dort anders (<c>%OS%</c> wird erst von der Shell
    /// zu „Windows_NT“), ein bloßes Echo der Tasten genügt also nicht –, und schließt den Tab wieder: Dabei muss die
    /// Shell samt ihren Kindprozessen enden. Liefert <c>null</c>, wenn alles ging, sonst in einem Satz, woran es lag;
    /// anderswo als unter Windows gibt es nichts zu tun.
    /// </summary>
    public static async Task<string?> LocalTerminalAsync(ShellViewModel shell, TimeSpan patience)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (shell.OpenLocalTerminal("cmd") is not { } session)
        {
            return "no local terminal was opened";
        }

        var seen = new System.Text.StringBuilder();
        void OnOutput(ReadOnlyMemory<byte> data)
        {
            lock (seen)
            {
                seen.Append(System.Text.Encoding.UTF8.GetString(data.Span));
            }
        }

        session.Terminal.Output += OnOutput;
        try
        {
            session.Start();
            if (!await Until(() => session.State is ConnectionState.Connected or ConnectionState.Closed, patience) || session.State != ConnectionState.Connected)
            {
                return $"the local terminal did not start: {session.StatusText}";
            }

            if (session.Files.FileSystem is not null)
            {
                return "a local terminal offers file access";
            }

            // Die Shell braucht einen Augenblick bis zur ersten Eingabeaufforderung; wer vorher tippt, geht nicht verloren
            // (die Eingabe wartet im Pseudo-Terminal), und gesucht wird nur nach der Antwort.
            shell.Input(session, System.Text.Encoding.ASCII.GetBytes("echo nexus-local-%OS%\r"));
            if (!await Until(() => { lock (seen) { return seen.ToString().Contains("nexus-local-Windows_NT", StringComparison.Ordinal); } }, patience))
            {
                return "the local shell did not answer";
            }
        }
        finally
        {
            session.Terminal.Output -= OnOutput;
        }

        shell.CloseSession(session);
        return shell.Sessions.Contains(session) ? "the local tab did not close" : null;
    }
    // Wartet, bis der Transfer zu Ende ist; null = fertig, sonst der Grund.
    private static async Task<string?> Finished(ITransferQueue transfers, string id, TimeSpan patience)
    {
        TransferItem? Item() => transfers.Items.FirstOrDefault(item => item.Id == id);
        if (!await Until(() => Item()?.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled, patience))
        {
            return "no end in time";
        }

        return Item() is { State: TransferState.Completed } ? null : Item()?.Detail ?? Item()?.State.ToString() ?? "unknown";
    }

    private static async Task<bool> Until(Func<bool> reached, TimeSpan patience)
    {
        var deadline = DateTimeOffset.UtcNow + patience;
        while (!reached())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }
}

/// <summary>
/// Die Rückfragen im Selbsttest mit echter Verbindung: Vertraut wird allein dem Testserver auf diesem Rechner
/// (127.0.0.1 und der genannte Port), und nur dort wird das Passwort des Testservers genannt. Jede andere Frage
/// ist abgelehnt – „nicht gefragt“ heißt auch hier nie „zugestimmt“.
/// </summary>
public sealed class SelfTestPrompts(int port) : IUserPrompts
{
    public Task<bool> ConfirmHostKeyAsync(ConnectionPrompt.HostKey prompt, CancellationToken cancellation)
        => Task.FromResult(prompt.Host == LiveSelfTest.Host && prompt.Port == port && !prompt.Changed);

    public Task<SecretAnswer?> AskSecretAsync(ConnectionPrompt.Secret prompt, CancellationToken cancellation)
        => Task.FromResult(
            prompt is { Kind: SecretKind.Password, Attempt: 1, User: LiveSelfTest.User } && prompt.Host is var host && (host == LiveSelfTest.Host || host == $"{LiveSelfTest.Host}:{port}")
                ? new SecretAnswer([LiveSelfTest.Password], Save: false)
                : null);
}
