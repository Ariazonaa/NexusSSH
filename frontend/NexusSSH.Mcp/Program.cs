using System.Text;
using NexusSSH.Core;
using NexusSSH.Core.Mcp;

// Ein MCP-Programm (Claude Code …) startet dies und spricht über stdin und stdout. Das Protokoll läuft hier, die App
// bekommt nur den einzelnen Aufruf; läuft sie nicht, sagt es die Antwort. Auf stdout steht nichts außer Antworten.
var pipe = McpPipe.Name(AppDataLocation.Root);
var server = new McpServer(async (call, cancellation) =>
    await McpPipe.CallAsync(pipe, call, cancellation)
        ?? new McpResult("NexusSSH is not running, or AI access is switched off in its settings", IsError: true));

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };
using var writing = new SemaphoreSlim(1, 1);
using var stop = new CancellationTokenSource();

// Jede Zeile läuft für sich: Ein Aufruf kann auf eine Rückfrage warten, und währenddessen muss sein Abbruch ankommen.
var running = new List<Task>();
while (await input.ReadLineAsync(stop.Token) is { } line)
{
    if (line.Length > 0)
    {
        running.RemoveAll(task => task.IsCompleted);
        running.Add(Task.Run(() => AnswerAsync(line), CancellationToken.None));
    }
}

await stop.CancelAsync();
await Task.WhenAll(running);

async Task AnswerAsync(string line)
{
    if (await server.HandleAsync(line, stop.Token) is not { } answer)
    {
        return;
    }

    await writing.WaitAsync(CancellationToken.None);
    try
    {
        await output.WriteLineAsync(answer);
    }
    finally
    {
        writing.Release();
    }
}
