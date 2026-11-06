using System.Text.Json;
using System.Text.Json.Serialization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Exchange;

/// <summary>
/// Inhalt einer Export-Datei: Profile, gespeicherte Befehle und die Einstellungen, die auf jedem Rechner gleich
/// sein sollen. <b>Nie Geheimnisse</b> – Passwörter und Passphrasen bleiben in der Anmeldeinformationsverwaltung
/// des Rechners, auf dem sie gespeichert wurden, und Schlüsseldateien bleiben, wo sie sind (ein Profil nennt nur
/// den Pfad).
/// </summary>
public sealed class ExportFile
{
    public const string FormatName = "nexusssh-export";

    /// <summary>Wird erhöht, wenn eine ältere App die Datei nicht mehr richtig lesen könnte.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Endung der Datei.</summary>
    public const string Extension = ".json";

    public string? Format { get; set; }

    public int Version { get; set; }

    /// <summary>Version der App, die die Datei geschrieben hat – nur zur Auskunft.</summary>
    public string? App { get; set; }

    public DateTimeOffset? Exported { get; set; }

    public List<ExportedProfile>? Profiles { get; set; }

    public List<Snippet>? Snippets { get; set; }

    /// <summary>Die Identitäten, auf die Profile der Datei verweisen können. Fehlt in Dateien älterer Versionen.</summary>
    public List<ExportedIdentity>? Identities { get; set; }

    public ExportedSettings? Settings { get; set; }

    /// <summary>
    /// Die eigenen Farbschemata – sie gehören zu den Einstellungen und werden mit ihnen übernommen. Fehlt in Dateien
    /// älterer Versionen. Fremde Eingabe wie alles hier: Angenommen wird nur, was <c>ThemeImport.IsValid</c> besteht.
    /// </summary>
    public List<TerminalTheme>? Themes { get; set; }
}

/// <summary>
/// Eine Identität in der Export-Datei. <see cref="KeyPath"/> steht wie bei den Profilen mit <c>~/</c> am Anfang,
/// wenn die Datei im Benutzerordner liegt.
/// </summary>
public sealed record ExportedIdentity(string? Id, string? Name, string? User, string? KeyPath = null, bool UseAgent = true);

/// <summary>
/// Ein Profil in der Export-Datei. <see cref="KeyPath"/> steht mit <c>~/</c> am Anfang, wenn die Datei im
/// Benutzerordner liegt – so stimmt der Pfad auch auf einem Rechner mit anderem Benutzernamen.
/// <see cref="Jump"/> ist die <see cref="Id"/> eines anderen Profils derselben Datei.
/// „Ältere Verfahren erlauben“ fehlt mit Absicht, wie X11-Forwarding: Eine Datei von außen soll keine Verbindung
/// schwächer machen können – das schaltet man an dem Rechner ein, an dem man es braucht.
/// Mosh und seine Ports wandern dagegen mit (<see cref="Mosh"/>): Sie verbinden nichts anderswohin und schwächen nichts –
/// das Terminal läuft über UDP statt über den Kanal von SSH, angemeldet wird wie sonst.
/// </summary>
public sealed record ExportedProfile(
    string? Id,
    string? Name,
    string? Host,
    int Port,
    string? User,
    string? Group = null,
    List<string>? Tags = null,
    bool Favorite = false,
    string? KeyPath = null,
    bool UseAgent = true,
    string? Jump = null,
    bool ForwardAgent = false,
    string? Color = null,
    string? Protocol = null,
    string? System = null,
    string? Identity = null,
    int? Baud = null,
    List<string>? Bookmarks = null,
    string? Notes = null,
    bool NoReconnect = false,
    string? AddressFamily = null,
    int? ConnectTimeout = null,
    bool Compression = false,
    string? Theme = null,
    string? FtpEncoding = null,
    int? KeepAliveSeconds = null,
    int? KeepAliveMissed = null,
    string? Term = null,
    string? Charset = null,
    bool IdentitiesOnly = false,
    bool Mosh = false,
    string? MoshPorts = null);

/// <summary>
/// Die Einstellungen, die mitwandern. Was zum Rechner gehört, fehlt mit Absicht: Fenstergröße, Breite der
/// Bereiche, der Editor, der letzte Download-Ordner, die offenen Tabs. Aufzählungen stehen als Text – ein Wert,
/// den diese Version nicht kennt, wird übergangen, statt den Import scheitern zu lassen.
/// </summary>
public sealed record ExportedSettings(
    string? Language = null,
    string? TerminalTheme = null,
    int? TerminalFontSize = null,
    bool? ShowHiddenFiles = null,
    string? ProfileSort = null,
    bool? RestoreTabs = null,
    Dictionary<string, string>? Shortcuts = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ExportFile))]
internal sealed partial class ExportJsonContext : JsonSerializerContext;
