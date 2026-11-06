using System.Diagnostics;
using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Den Weg zu einem Server verfolgen – mit dem Werkzeug des Betriebssystems (<c>tracert</c> unter Windows, sonst
/// <c>traceroute</c>), dessen Ausgabe Zeile für Zeile zurückkommt. Der Name des Servers geht als ein Argument an das
/// Programm, nie durch eine Befehlszeile; was kein schlichter Hostname ist, wird gar nicht erst gestartet.
/// </summary>
public static class RouteTrace
{
    /// <summary>Mehr Stationen verfolgt der Lauf nicht.</summary>
    public const int MaxHops = 20;

    /// <summary>
    /// Der Server, zu dem dieser Rechner tatsächlich verbindet: bei einem Weg über Sprung-Server der erste davon.
    /// <c>null</c>, wenn es keinen Weg im Netz gibt (serielle Schnittstelle) oder der Sprung-Server fehlt.
    /// </summary>
    public static string? Target(ServerProfile profile, IReadOnlyList<ServerProfile> profiles)
    {
        var current = profile;
        for (var steps = 0; steps <= profiles.Count && current.JumpId is { } jump; steps++)
        {
            if (profiles.FirstOrDefault(other => other.Id == jump) is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current.IsSerial || current.JumpId is not null || !IsHostName(current.Host) ? null : current.Host;
    }

    /// <summary>
    /// Ein Name oder eine Adresse, wie sie im Profil stehen darf – und nichts, was ein Programm als Schalter läse:
    /// Buchstaben, Ziffern, Punkt, Bindestrich, Doppelpunkt (IPv6), Unterstrich und <c>%</c> (Zone einer IPv6-Adresse),
    /// nicht mit <c>-</c> oder <c>/</c> am Anfang.
    /// </summary>
    public static bool IsHostName(string host)
        => host is { Length: > 0 and <= 255 }
            && host[0] is not ('-' or '/')
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_' or '%' || (c > 127 && char.IsLetterOrDigit(c)));

    /// <summary>Programm und Argumente für diesen Rechner. Ohne Namensauflösung der Stationen – das wäre langsam.</summary>
    public static (string Program, IReadOnlyList<string> Arguments) Command(string host)
        => OperatingSystem.IsWindows()
            ? ("tracert", ["-d", "-h", MaxHops.ToString(System.Globalization.CultureInfo.InvariantCulture), "-w", "1000", host])
            : ("traceroute", ["-n", "-m", MaxHops.ToString(System.Globalization.CultureInfo.InvariantCulture), "-w", "1", host]);

    /// <summary>
    /// Startet den Lauf. <paramref name="line"/> bekommt jede Zeile der Ausgabe (von einem anderen Thread),
    /// <paramref name="ended"/> das Ende – mit <c>null</c>, wenn das Programm lief, sonst mit dem Grund, warum es
    /// nicht startete. Liefert etwas zum Abbrechen.
    /// </summary>
    public static IDisposable Start(string host, Action<string> line, Action<string?> ended)
    {
        if (!IsHostName(host))
        {
            ended("not a host name");
            return new Running(null);
        }

        var (program, arguments) = Command(host);
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // tracert schreibt in der Zeichentabelle der Konsole; ohne sie wären Umlaute in seinen Sätzen entstellt.
        if (ConsoleEncoding() is { } encoding)
        {
            start.StandardOutputEncoding = encoding;
            start.StandardErrorEncoding = encoding;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ended(e.Message);
            return new Running(null);
        }

        void Forward(object _, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                line(e.Data.TrimEnd());
            }
        }

        process.OutputDataReceived += Forward;
        process.ErrorDataReceived += Forward;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ended(null);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Running(process);
    }

    private static Encoding? ConsoleEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class Running(Process? process) : IDisposable
    {
        public void Dispose()
        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Schon zu Ende.
            }

            process.Dispose();
        }
    }
}
