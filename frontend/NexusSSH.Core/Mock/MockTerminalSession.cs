using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Mock;

/// <summary>
/// Spielt eine Shell nach, damit das Terminal ohne SSH bedienbar ist: Begrüßung, Prompt, Echo mit Rücktaste und
/// eine Handvoll fester Befehle. Kein Verlauf, keine Cursor-Tasten – das kann erst eine echte Shell.
/// </summary>
public sealed class MockTerminalSession(ServerProfile profile, ServerStats stats, MockRemoteFileSystem files, string? opening = null, string promptPath = "~") : ITerminalSession
{
    private const string NewLine = "\r\n";

    // Cursor nach oben links, Bild leeren, Verlauf leeren.
    private const string ClearScreen = "\e[H\e[2J\e[3J";

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly Lock _gate = new();
    private bool _started;
    private int _escape; // 0 = Text, 1 = nach ESC, 2 = in einer CSI-/SS3-Sequenz

    public event Action<ReadOnlyMemory<byte>>? Output;

    private string Home => profile.User == "root" ? "/root" : $"/home/{profile.User}";

    private string Prompt => $"\e[1;32m{profile.User}@{profile.Name}\e[0m:\e[1;34m{promptPath}\e[0m$ ";

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        // Das Bild der Beispieldaten beginnt mitten in der Arbeit: ein paar Befehle samt Ausgabe, dann der Prompt.
        // Die Zeile „Verbinde mit …“ der App davor steht im Bild nicht – wie nach `clear` ist sie weg, samt Verlauf.
        Emit(opening is not null
            ? ClearScreen + string.Concat(opening.Split('\n').Select(line => line.StartsWith('$') ? Prompt + line[1..].TrimStart() + NewLine : line + NewLine)) + Prompt
            : $"Welcome to {stats.OsName} (GNU/Linux {stats.Kernel} x86_64){NewLine}{NewLine}" +
              $"\e[2m{L.Get("DemoSessionNotice")}\e[0m{NewLine}{NewLine}" +
              Prompt);
    }

    public void Resize(int columns, int rows)
    {
        // Die nachgespielte Shell bricht nichts um; eine echte Sitzung meldet die Größe an den Server.
    }

    public void SetPaused(bool paused)
    {
        // Die nachgespielte Shell schreibt nie mehr, als ein Terminal sofort zeichnen kann.
    }

    public void Write(ReadOnlyMemory<byte> input)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(input.Length)];
        var output = new StringBuilder();
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            var count = _decoder.GetChars(input.Span, chars, flush: false);
            foreach (var c in chars.AsSpan(0, count))
            {
                Handle(c, output);
            }
        }

        if (output.Length > 0)
        {
            Emit(output.ToString());
        }
    }

    private void Handle(char c, StringBuilder output)
    {
        // Escape-Sequenzen (Pfeiltasten, Funktionstasten) verschlucken, statt ihren Text zu zeigen.
        if (_escape == 1)
        {
            _escape = c is '[' or 'O' ? 2 : 0;
            return;
        }

        if (_escape == 2)
        {
            if (c is >= '@' and <= '~')
            {
                _escape = 0;
            }

            return;
        }

        switch (c)
        {
            case '\e':
                _escape = 1;
                break;
            case '\r':
                output.Append(NewLine);
                Execute(_line.ToString().Trim(), output);
                _line.Clear();
                output.Append(Prompt);
                break;
            case '\n':
                break; // eingefügter Text mit CRLF: die Zeile hat das CR schon abgeschlossen
            case '\x7f' or '\b':
                if (_line.Length > 0)
                {
                    var cut = char.IsLowSurrogate(_line[^1]) && _line.Length > 1 ? 2 : 1;
                    _line.Length -= cut;
                    output.Append("\b \b");
                }

                break;
            case '\x03': // Strg+C
                _line.Clear();
                output.Append("^C").Append(NewLine).Append(Prompt);
                break;
            case '\x0c': // Strg+L
                output.Append("\e[2J\e[H").Append(Prompt).Append(_line);
                break;
            default:
                if (!char.IsControl(c))
                {
                    _line.Append(c);
                    output.Append(c);
                }

                break;
        }
    }

    private void Execute(string command, StringBuilder output)
    {
        switch (command)
        {
            case "":
                break;
            case "help":
                Line(output, "Demo commands: help, ls, pwd, whoami, uname -a, uptime, docker ps, clear");
                break;
            case "ls":
                foreach (var entry in files.List(Home))
                {
                    Line(output, entry.IsDirectory ? $"\e[1;34m{entry.Name}\e[0m" : entry.Name);
                }

                break;
            case "pwd":
                Line(output, Home);
                break;
            case "whoami":
                Line(output, profile.User);
                break;
            case "uname -a":
                Line(output, $"Linux {profile.Name} {stats.Kernel} #1 SMP PREEMPT_DYNAMIC x86_64 GNU/Linux");
                break;
            case "uptime":
                Line(output, $" 09:30:00 up {(int)stats.Uptime.TotalDays} days,  1 user,  load average: 0.42, 0.38, 0.35");
                break;
            case "docker ps":
                Line(output, "CONTAINER ID   IMAGE         STATUS        NAMES");
                Line(output, "4f1c2a9b7d10   nginx:1.27    Up 32 days    nginx");
                Line(output, "9a3e5c7f1b22   redis:7       Up 32 days    redis");
                Line(output, "c81d04be6f33   postgres:16   Up 32 days    postgres");
                break;
            case "clear":
                output.Append("\e[2J\e[H");
                break;
            default:
                Line(output, $"bash: {command.Split(' ')[0]}: command not found");
                break;
        }
    }

    private static void Line(StringBuilder output, string text) => output.Append(text).Append(NewLine);

    private void Emit(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));
}
