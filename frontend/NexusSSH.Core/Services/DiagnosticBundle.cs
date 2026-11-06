using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Services;

/// <summary>Was ins Diagnosepaket eingeht – gesammelt von der App, bereinigt erst hier.</summary>
/// <param name="AppVersion">Version der App.</param>
/// <param name="CoreVersion">Version des Rust-Kerns, wie die Einstellungen sie nennen (oder „nicht geladen“).</param>
/// <param name="IsDemo">Läuft die App mit Beispieldaten?</param>
/// <param name="Profiles">Die Serverprofile – aus ihnen stammen die Namen, die überall ersetzt werden.</param>
/// <param name="Identities">Die Identitäten, ebenso.</param>
/// <param name="SettingsJson">Der Text von <c>settings.json</c>, oder <c>null</c>, wenn es keine gibt.</param>
/// <param name="AppLog">Das Ende von <c>app.log</c>, oder <c>null</c>.</param>
/// <param name="Output">Die Zeilen des Reiters „Ausgabe“.</param>
/// <param name="OpenSessions">Wie viele Sitzungen offen sind.</param>
/// <param name="UserName">Der Name des Windows-Kontos – er steht in Pfaden.</param>
/// <param name="HomeDirectory">Das Benutzerverzeichnis.</param>
public sealed record DiagnosticInput(
    string AppVersion,
    string CoreVersion,
    bool IsDemo,
    IReadOnlyList<ServerProfile> Profiles,
    IReadOnlyList<Identity> Identities,
    string? SettingsJson,
    string? AppLog,
    IReadOnlyList<LogEntry> Output,
    int OpenSessions,
    string UserName,
    string? HomeDirectory);

/// <summary>
/// Das Diagnosepaket: eine ZIP zum Mitschicken bei einem Fehlerbericht – Versionen, das Protokoll, die Ausgabe der
/// App und die Einstellungen. Wer sie bekommt, soll daraus <b>nicht</b> lesen können, mit welchen Servern der
/// Benutzer arbeitet: Namen, Adressen und Benutzer der Profile, Namen von Gruppen und Identitäten, der Name des
/// Windows-Kontos und alles, was wie eine Adresse aussieht, werden ersetzt; aus den Einstellungen kommen nur
/// Schalter, Zahlen und eine feste Liste harmloser Texte mit. Geheimnisse stehen ohnehin in keiner dieser Quellen.
/// Die Datei ist lesbarer Text – der Benutzer kann nachsehen, was er verschickt.
/// </summary>
public static partial class DiagnosticBundle
{
    /// <summary>So viel vom Ende von <c>app.log</c> kommt mit.</summary>
    public const int MaxLogBytes = 256 * 1024;

    // Texte der Einstellungen, die nichts über Server oder den Rechner sagen und beim Suchen eines Fehlers helfen.
    private static readonly HashSet<string> PlainSettings = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.Language), nameof(AppSettings.TerminalTheme), nameof(AppSettings.TerminalFont), nameof(AppSettings.TerminalCursor),
        nameof(AppSettings.Accent), nameof(AppSettings.LastUpdateCheck), "ProfileSort", "FileSort", "LocalSort", "Density",
    };

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![\w:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![\w:])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"[\w.+-]+@(?!\d+x\b)[\w-]+(?:\.[\w-]+)*")]
    private static partial Regex UserAtHost();

    // Nur klein geschrieben: "System.IO" und "Microsoft.UI.Xaml" in einer Ausnahme sind keine Server.
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|net|org|de|io|eu|fr|es|uk|ch|at|nl|dev|cloud|local|lan|internal|intern|corp|home)\b")]
    private static partial Regex HostName();

    /// <summary>Vorschlag für den Namen der Datei, ohne die Endung.</summary>
    public static string FileName(DateTimeOffset now)
        => "nexusssh-diagnose-" + now.ToLocalTime().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

    /// <summary>Schreibt das Paket nach <paramref name="path"/>. Fehler beim Schreiben gehen an den Aufrufer.</summary>
    public static void Write(string path, DiagnosticInput input, DateTimeOffset now)
    {
        using var file = File.Create(path);
        Write(file, input, now);
    }

    /// <summary>Schreibt das Paket in einen Strom.</summary>
    public static void Write(Stream target, DiagnosticInput input, DateTimeOffset now)
    {
        var clean = Redactor(input);
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "info.txt", Info(input, now));
        Add(zip, "profiles.txt", Profiles(input.Profiles));
        Add(zip, "output.txt", clean(string.Join('\n', input.Output.Select(entry => $"{entry.Time:O} {entry.Severity}: {entry.Text}"))));
        Add(zip, "app.log", input.AppLog is { Length: > 0 } log ? clean(log) : "(leer)");
        Add(zip, "settings.json", input.SettingsJson is { Length: > 0 } settings ? Settings(settings) : "{}");
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text.ReplaceLineEndings("\n"));
    }

    private static string Info(DiagnosticInput input, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            "NexusSSH diagnostics",
            $"created: {now:O}",
            $"app: {input.AppVersion}",
            $"core: {input.CoreVersion}",
            $"mode: {(input.IsDemo ? "demo" : "core")}",
            $"os: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})",
            $"ui language: {Localization.L.Language}, windows: {CultureInfo.CurrentUICulture.Name}, formats: {CultureInfo.CurrentCulture.Name}",
            $"profiles: {input.Profiles.Count}, identities: {input.Identities.Count}, open sessions: {input.OpenSessions}",
            string.Empty,
            "Names of servers, users, groups and identities, addresses and paths are replaced or left out.",
            "This file and the others in the package are plain text - have a look before you send them.",
        };
        return string.Join('\n', lines) + "\n";
    }

    // Die Profile ohne alles, was einen Server erkennbar macht: nur, wie verbunden wird.
    private static string Profiles(IReadOnlyList<ServerProfile> profiles)
    {
        var number = profiles.Select((profile, index) => (profile.Id, Number: index + 1)).ToDictionary(pair => pair.Id, pair => pair.Number, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var profile in profiles)
        {
            var parts = new List<string>
            {
                profile.Protocol is { Length: > 0 } protocol ? protocol : "ssh",
                $"port {profile.Port.ToString(CultureInfo.InvariantCulture)}",
            };
            void Flag(bool set, string name)
            {
                if (set)
                {
                    parts.Add(name);
                }
            }

            Flag(profile.KeyPath is { Length: > 0 }, "key file");
            Flag(profile.UseAgent, "agent");
            Flag(profile.HasSavedSecret, "saved password");
            Flag(profile.IdentityId is { Length: > 0 }, "identity");
            Flag(profile.ForwardAgent, "agent forwarding");
            Flag(profile.ForwardX11, "x11");
            Flag(profile.Mosh, "mosh");
            Flag(profile.Compression, "compression");
            Flag(profile.LegacyAlgorithms, "legacy algorithms");
            Flag(profile.NoReconnect, "no reconnect");
            Flag(profile.SshAlias is { Length: > 0 }, "follows ssh config");
            Flag(profile.Group is { Length: > 0 }, "in a group");
            Flag(profile.Tags.Count > 0, $"{profile.Tags.Count.ToString(CultureInfo.InvariantCulture)} tags");
            if (profile.JumpId is { Length: > 0 } jump)
            {
                parts.Add(number.TryGetValue(jump, out var hop) ? $"jump via server-{hop.ToString(CultureInfo.InvariantCulture)}" : "jump via a missing profile");
            }

            foreach (var (name, value) in new[] { ("address family", profile.AddressFamily), ("ftp tls", profile.FtpTls), ("system", profile.System) })
            {
                if (value is { Length: > 0 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                {
                    parts.Add($"{name} {value}");
                }
            }

            if (profile.ConnectTimeout is { } timeout)
            {
                parts.Add($"timeout {timeout.ToString(CultureInfo.InvariantCulture)} s");
            }

            if (profile.Baud is { } baud)
            {
                parts.Add($"{baud.ToString(CultureInfo.InvariantCulture)} baud");
            }

            text.Append("server-").Append(number[profile.Id].ToString(CultureInfo.InvariantCulture)).Append(": ").Append(string.Join(", ", parts)).Append('\n');
        }

        return text.Length > 0 ? text.ToString() : "(keine Profile)\n";
    }

    /// <summary>
    /// Der Ersetzer für freien Text (Protokoll, Ausgabe): erst die Namen, die die App kennt, dann alles, was wie
    /// eine Adresse aussieht. Bekannte Namen werden zu <c>&lt;server-3&gt;</c>, damit sich im Protokoll noch
    /// verfolgen lässt, dass zweimal derselbe Server gemeint ist.
    /// </summary>
    public static Func<string, string> Redactor(DiagnosticInput input)
    {
        var known = new List<(string Text, string Instead)>();
        void Know(string? text, string instead)
        {
            // Sehr kurze Namen träfen überall („a“, „db“ in „stdbuf“) – und sagen dem Leser auch wenig.
            if (text is { Length: >= 3 })
            {
                known.Add((text, instead));
            }
        }

        for (var index = 0; index < input.Profiles.Count; index++)
        {
            var (profile, number) = (input.Profiles[index], (index + 1).ToString(CultureInfo.InvariantCulture));
            Know(profile.Host, $"<server-{number}>");
            Know(profile.Name, $"<server-{number}>");
            Know(profile.SshAlias, $"<server-{number}>");
            Know(profile.User, "<user>");
            Know(profile.KeyPath, "<key>");
            Know(profile.Group, "<group>");
            foreach (var part in (profile.Group ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Know(part, "<group>");
            }

            foreach (var tag in profile.Tags)
            {
                Know(tag, "<tag>");
            }
        }

        foreach (var identity in input.Identities)
        {
            Know(identity.Name, "<identity>");
            Know(identity.User, "<user>");
            Know(identity.KeyPath, "<key>");
        }

        Know(input.HomeDirectory, "<home>");
        Know(input.UserName, "<user>");

        // Das Längste zuerst: „web01.example.org“ vor „web01“.
        var ordered = known.DistinctBy(pair => pair.Text, StringComparer.OrdinalIgnoreCase).OrderByDescending(pair => pair.Text.Length).ToList();
        return text =>
        {
            foreach (var (name, instead) in ordered)
            {
                text = text.Replace(name, instead, StringComparison.OrdinalIgnoreCase);
            }

            text = UserAtHost().Replace(text, "<user>@<host>");
            text = Ipv4().Replace(text, "<ip>");
            text = Ipv6().Replace(text, match => match.Value.Any(char.IsAsciiDigit) && !LooksLikeTime(match.Value) ? "<ip>" : match.Value);
            return HostName().Replace(text, "<host>");
        };
    }

    // „12:30:05“ ist eine Uhrzeit, keine Adresse.
    private static bool LooksLikeTime(string text)
        => text.Split(':') is { Length: 3 } parts && parts.All(part => part.Length is 1 or 2 && part.All(char.IsAsciiDigit));

    /// <summary>
    /// Die Einstellungen ohne alles Sprechende: Schalter und Zahlen bleiben, Texte nur aus einer festen Liste; ein
    /// anderer Text wird zu „(gesetzt)“ oder „(leer)“, eine Liste zur Zahl ihrer Einträge. Eine neue Einstellung
    /// mit Text bleibt damit von selbst draußen. Unlesbares ergibt <c>{}</c>.
    /// </summary>
    public static string Settings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "{}";
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.Null:
                            property.Value.WriteTo(writer);
                            break;
                        case JsonValueKind.String when PlainSettings.Contains(property.Name) && IsPlain(property.Value.GetString()):
                            property.Value.WriteTo(writer);
                            break;
                        case JsonValueKind.String:
                            writer.WriteStringValue(property.Value.GetString() is { Length: > 0 } ? "(set)" : "(empty)");
                            break;
                        case JsonValueKind.Array:
                            writer.WriteStringValue($"({property.Value.GetArrayLength().ToString(CultureInfo.InvariantCulture)} entries)");
                            break;
                        case JsonValueKind.Object when property.Name == nameof(AppSettings.Shortcuts):
                            // Kennungen von Befehlen und Tasten – nichts über Server.
                            WriteShortcuts(writer, property.Value);
                            break;
                        default:
                            writer.WriteStringValue($"({property.Value.EnumerateObject().Count().ToString(CultureInfo.InvariantCulture)} entries)");
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return "{}";
        }
    }

    private static void WriteShortcuts(Utf8JsonWriter writer, JsonElement shortcuts)
    {
        writer.WriteStartObject();
        foreach (var shortcut in shortcuts.EnumerateObject())
        {
            if (IsPlain(shortcut.Name) && shortcut.Value.ValueKind == JsonValueKind.String && IsPlain(shortcut.Value.GetString()))
            {
                writer.WriteString(shortcut.Name, shortcut.Value.GetString());
            }
        }

        writer.WriteEndObject();
    }

    // Ein kurzes Wort aus Buchstaben, Ziffern und den Zeichen einer Kennung, eines Datums oder eines Schriftnamens.
    private static bool IsPlain(string? text)
        => text is { Length: <= 64 } && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+' or ':' or ' ');
}
