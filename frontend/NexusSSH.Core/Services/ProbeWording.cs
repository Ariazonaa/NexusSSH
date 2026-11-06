using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Der Bericht eines Verbindungstests in Sätzen: eine Zeile je Schritt, mit Haken oder Kreuz davor. Was vom Server
/// stammt (Vorstellung, Verfahren), ist reiner Text – der Kern hat es schon gekürzt, hier geht es noch einmal durch
/// <see cref="Format.Visible"/>.
/// </summary>
public static class ProbeWording
{
    private const string Done = "✓ ";
    private const string Failed = "✗ ";
    private const string Note = "• ";

    /// <summary>Die Zeilen des Berichts, von oben nach unten in der Reihenfolge der Schritte.</summary>
    public static IReadOnlyList<string> Lines(ProbeReport report)
    {
        var lines = new List<string>();
        if (report.Addresses.Count > 0)
        {
            lines.Add(Done + L.Get("ProbeResolved", string.Join(", ", report.Addresses.Take(4).Select(Format.Visible))));
        }

        if (report.Address is { } address && report.ConnectMs is { } milliseconds)
        {
            lines.Add(Done + L.Get("ProbeConnected", Format.Visible(address), milliseconds));
        }

        if (report.Software is { Length: > 0 } software)
        {
            var known = software.StartsWith("SSH-", StringComparison.Ordinal);
            lines.Add((known ? Done : Note) + L.Get("ProbeSoftware", Format.Visible(software)));
        }

        if (report is { KeyAlgorithm: { } algorithm, Fingerprint: { } fingerprint })
        {
            lines.Add(report.KeyState switch
            {
                ProbeReport.Trusted => Done + L.Get("ProbeKeyTrusted", algorithm, fingerprint),
                ProbeReport.Certified => Done + L.Get("ProbeKeyCertified", algorithm, fingerprint),
                ProbeReport.Changed => Failed + L.Get("ProbeKeyChanged", algorithm, fingerprint),
                _ => Note + L.Get("ProbeKeyUnknown", algorithm, fingerprint),
            });
        }

        if (report.Methods is { Count: > 0 } methods)
        {
            lines.Add(Done + L.Get("ProbeMethods", string.Join(", ", methods.Take(8).Select(Method))));
        }

        if (report.Trouble is { } trouble)
        {
            var sentence = trouble switch
            {
                "jump" => L.Get("ProbeJump"),
                "not_ssh" => L.Get("ProbeNotSsh"),
                _ => ConnectTrouble.Sentence(trouble) ?? L.Get("ProbeFailed"),
            };
            lines.Add(Failed + (string.IsNullOrWhiteSpace(report.Detail) || trouble is "jump" or "not_ssh" ? sentence : $"{sentence} ({Format.Visible(report.Detail)})"));
        }
        else if (lines.Count > 0)
        {
            lines.Add(Done + L.Get("ProbeDone"));
        }

        return lines;
    }

    /// <summary>Alle Zeilen als ein Text.</summary>
    public static string Text(ProbeReport report)
    {
        var text = new StringBuilder();
        foreach (var line in Lines(report))
        {
            text.AppendLine(line);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Die Antwortzeit in einem Satz – für den Inspector.</summary>
    public static string Latency(ProbeReport report)
        => report is { Address: { } address, ConnectMs: { } milliseconds }
            ? L.Get("ProbeLatency", milliseconds, Format.Visible(address))
            : Lines(report).LastOrDefault() ?? L.Get("ProbeFailed");

    // Die Namen der Verfahren, wie man sie kennt; ein unbekanntes bleibt, wie der Server es nennt.
    private static string Method(string name) => name switch
    {
        "publickey" => L.Get("ProbeMethodKey"),
        "password" => L.Get("ProbeMethodPassword"),
        "keyboard-interactive" => L.Get("ProbeMethodInteractive"),
        "none" => L.Get("ProbeMethodNone"),
        _ => Format.Visible(name),
    };
}
