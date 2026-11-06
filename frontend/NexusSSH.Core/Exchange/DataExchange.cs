using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Exchange;

/// <summary>Warum sich eine Datei nicht importieren lässt.</summary>
public enum ExchangeError
{
    /// <summary>Kein JSON oder nicht in der erwarteten Form.</summary>
    Malformed,

    /// <summary>JSON, aber keine Export-Datei von NexusSSH.</summary>
    NotAnExport,

    /// <summary>Von einer neueren Version geschrieben, die diese hier nicht lesen kann.</summary>
    TooNew,

    /// <summary>Größer, als eine Export-Datei je wird.</summary>
    TooLarge,

    /// <summary>Die Datei ließ sich nicht lesen.</summary>
    Unreadable,
}

public sealed class ExchangeException(ExchangeError error, string? detail = null) : Exception(detail ?? error.ToString())
{
    public ExchangeError Error { get; } = error;
}

/// <summary>Was der Import mit einem Profil der Datei täte.</summary>
public enum ProfileChange
{
    /// <summary>Gibt es hier noch nicht.</summary>
    New,

    /// <summary>Gibt es, aber mit anderen Angaben – sie würden ersetzt.</summary>
    Changed,

    /// <summary>Gibt es genau so.</summary>
    Unchanged,
}

/// <summary>Ein Profil der Datei, so wie es nach dem Import hier stünde.</summary>
public sealed record PlannedProfile(ServerProfile Profile, ProfileChange Change)
{
    /// <summary>Das vorhandene Profil, das der Import ändern würde; <c>null</c> bei einem neuen.</summary>
    public ServerProfile? Previous { get; init; }

    /// <summary>
    /// Der Import ließe ein vorhandenes Profil auf ein anderes Ziel zeigen (Host, Port, Benutzer oder Protokoll).
    /// Das sieht der Benutzer vor dem Import – eine Datei von jemand anderem könnte ein Profil so umleiten.
    /// </summary>
    public bool Retargets => Change == ProfileChange.Changed && Previous is { } before
        && (!string.Equals(before.Host, Profile.Host, StringComparison.OrdinalIgnoreCase) || before.Port != Profile.Port || before.User != Profile.User || before.Protocol != Profile.Protocol);
}

/// <summary>
/// Was ein Import täte – errechnet, bevor irgendetwas geschieht, damit der Benutzer es vorher sieht.
/// </summary>
/// <param name="Profiles">Die Profile der Datei mit ihrem Schicksal.</param>
/// <param name="Snippets">Die gespeicherten Befehle, wie sie nach dem Import wären (vorhandene plus neue).</param>
/// <param name="NewSnippets">Befehle, die es hier noch nicht gibt.</param>
/// <param name="ChangedSnippets">Befehle gleichen Namens mit anderem Text.</param>
/// <param name="Settings">Die Einstellungen der Datei; <c>null</c>, wenn sie keine enthält.</param>
/// <param name="MissingKeys">Namen der Profile, deren Schlüsseldatei es auf diesem Rechner nicht gibt.</param>
/// <param name="Skipped">Einträge der Datei, mit denen sich nichts anfangen lässt (ohne Host oder Benutzer).</param>
public sealed record ImportPlan(
    IReadOnlyList<PlannedProfile> Profiles,
    IReadOnlyList<Snippet> Snippets,
    int NewSnippets,
    int ChangedSnippets,
    ExportedSettings? Settings,
    IReadOnlyList<string> MissingKeys,
    int Skipped)
{
    public int NewProfiles => Profiles.Count(profile => profile.Change == ProfileChange.New);

    public int ChangedProfiles => Profiles.Count(profile => profile.Change == ProfileChange.Changed);

    public int UnchangedProfiles => Profiles.Count(profile => profile.Change == ProfileChange.Unchanged);

    /// <summary>
    /// Die Identitäten, wie sie nach dem Import wären (vorhandene plus die der Datei). Sie gehören zu den Profilen
    /// und werden mit ihnen übernommen – ein Profil der Datei kann auf eine von ihnen verweisen.
    /// </summary>
    public IReadOnlyList<Identity> Identities { get; init; } = [];

    public int NewIdentities { get; init; }

    public int ChangedIdentities { get; init; }

    /// <summary>
    /// Lesezeichen für Ordner am Server, die mit den Profilen dazukämen: Kennung des Profils → Pfade, die es hier
    /// noch nicht gibt. Sie werden mit den Profilen übernommen.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Bookmarks { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public int NewBookmarks => Bookmarks.Sum(entry => entry.Value.Count);

    /// <summary>
    /// Die eigenen Farbschemata, wie sie nach dem Import wären (vorhandene plus die der Datei). Sie gehören zu den
    /// Einstellungen und werden mit ihnen übernommen.
    /// </summary>
    public IReadOnlyList<TerminalTheme> Themes { get; init; } = [];

    public int NewThemes { get; init; }

    public int ChangedThemes { get; init; }

    /// <summary>
    /// Einträge der Datei, die ein lokales Terminal wären (<c>protocol: local</c>): Sie werden übergangen. Ein lokales
    /// Terminal steht nie in einer Export-Datei und wird nie aus einer angelegt – die Datei ist fremde Eingabe, und
    /// die Shell, die es öffnet, gehört zu diesem Rechner.
    /// </summary>
    public int SkippedLocal { get; init; }

    /// <summary>Ob der Import überhaupt etwas ändern würde.</summary>
    public bool HasChanges => NewProfiles + ChangedProfiles + NewSnippets + ChangedSnippets + NewIdentities + ChangedIdentities + NewBookmarks + NewThemes + ChangedThemes > 0 || Settings is not null;
}

/// <summary>Welche Teile der Datei übernommen werden.</summary>
public sealed record ImportChoice(bool Profiles = true, bool Snippets = true, bool Settings = true);

/// <summary>
/// Export und Import von Profilen, gespeicherten Befehlen und Einstellungen als Datei – um die App auf einem
/// anderen Rechner mit denselben Daten zu benutzen. Geheimnisse sind nie dabei (<see cref="ExportFile"/>).
/// </summary>
public static class DataExchange
{
    /// <summary>Eine Export-Datei ist ein paar Kilobyte groß; mehr als das hier ist keine.</summary>
    public const long MaxFileSize = 8 * 1024 * 1024;

    private const int MaxProfiles = 5000;
    private const int MaxSnippets = 2000;

    /// <summary>Stellt zusammen, was exportiert wird. <paramref name="home"/> ist der Benutzerordner dieses Rechners.</summary>
    public static ExportFile Create(IEnumerable<ServerProfile> profiles, AppSettings settings, string appVersion, DateTimeOffset now, string? home)
        => new()
        {
            Format = ExportFile.FormatName,
            Version = ExportFile.CurrentVersion,
            App = appVersion,
            Exported = now,
            Profiles =
            [
                .. profiles.Where(profile => !profile.IsLocal).Select(profile => new ExportedProfile(
                    profile.Id,
                    profile.Name,
                    profile.Host,
                    profile.Port,
                    profile.User,
                    profile.Group.Length > 0 ? profile.Group : null,
                    profile.Tags.Count > 0 ? [.. profile.Tags] : null,
                    profile.IsFavorite,
                    PortableKeyPath(profile.KeyPath, home),
                    profile.UseAgent,
                    profile.JumpId,
                    // Agent Forwarding wandert nicht mit (siehe Plan) – das Feld bleibt nur für ältere Dateien lesbar.
                    false,
                    profile.Color,
                    profile.Protocol,
                    profile.System,
                    profile.IdentityId,
                    profile.Baud,
                    // Die Lesezeichen für Ordner am Server gehören zum Profil und wandern mit ihm.
                    FolderBookmarks.Of(settings, profile.Id) is { Count: > 0 } bookmarks ? [.. bookmarks] : null,
                    profile.Notes.Length > 0 ? profile.Notes : null,
                    profile.NoReconnect,
                    profile.AddressFamily,
                    profile.ConnectTimeout,
                    profile.Compression,
                    profile.ThemeId,
                    profile.IsFtp ? profile.FtpEncoding : null,
                    KeepAliveSeconds: profile.KeepAliveSeconds,
                    KeepAliveMissed: profile.KeepAliveMissed,
                    Term: profile.Term,
                    Charset: profile.Charset,
                    IdentitiesOnly: profile.IdentitiesOnly,
                    Mosh: profile.Mosh && profile.IsSsh,
                    MoshPorts: profile.MoshPorts)),
            ],
            // Nur die eigenen: Die mitgelieferten hat jede App.
            Themes = settings.CustomThemes.Count > 0 ? [.. settings.CustomThemes] : null,
            Snippets = [.. settings.Snippets],
            Identities = [.. settings.Identities.Select(identity => new ExportedIdentity(identity.Id, identity.Name, identity.User, PortableKeyPath(identity.KeyPath, home), identity.UseAgent))],
            Settings = new ExportedSettings(
                settings.Language.ToString(),
                settings.TerminalTheme,
                settings.TerminalFontSize,
                settings.ShowHiddenFiles,
                settings.ProfileSort.ToString(),
                settings.RestoreTabs,
                // Nur, was der Benutzer geändert hat – ohne eigene Kürzel fehlt der Eintrag.
                settings.Shortcuts.Count > 0 ? new(settings.Shortcuts) : null),
        };

    public static string Serialize(ExportFile file) => JsonSerializer.Serialize(file, ExportJsonContext.Default.ExportFile);

    /// <summary>Liest den Text einer Export-Datei. Wirft <see cref="ExchangeException"/>, wenn es keine ist.</summary>
    public static ExportFile Parse(string json)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ExportJsonContext.Default.ExportFile);
        }
        // Auch ArgumentException: Die wirft der JSON-Leser bei einem halben UTF-16-Zeichen im Text (gefunden von den
        // Zufallstests) – für den Aufrufer ist das dieselbe unlesbare Eingabe.
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new ExchangeException(ExchangeError.Malformed, e.Message);
        }

        if (file is not { Format: ExportFile.FormatName })
        {
            throw new ExchangeException(ExchangeError.NotAnExport);
        }

        if (file.Version > ExportFile.CurrentVersion)
        {
            throw new ExchangeException(ExchangeError.TooNew, file.App);
        }

        return file;
    }

    /// <summary>Liest eine Export-Datei von der Platte. Wirft <see cref="ExchangeException"/>.</summary>
    public static ExportFile Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileSize)
            {
                throw new ExchangeException(ExchangeError.TooLarge);
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ExchangeException(ExchangeError.Unreadable, e.Message);
        }
    }

    /// <summary>
    /// Errechnet, was der Import täte. Ein Profil der Datei gilt als dasselbe wie ein vorhandenes, wenn die Kennung
    /// übereinstimmt (die Datei stammt aus einer Kopie dieser Daten) oder Name und Ziel gleich sind (auf beiden
    /// Rechnern von Hand angelegt) – dann wird das vorhandene ersetzt statt ein zweites daneben gelegt.
    /// </summary>
    public static ImportPlan Plan(ExportFile file, IReadOnlyList<ServerProfile> existing, AppSettings settings, string? home, Func<string, bool>? keyExists = null)
    {
        keyExists ??= File.Exists;

        // Ein lokales Terminal gehört zu diesem Rechner: Die Datei kann keines anlegen, und sie kann auch keines
        // ersetzen – weder über die Kennung noch über Name und Ziel. Ihr Eintrag mit der Kennung eines solchen
        // Profils wird ein neues Profil, nie dieses.
        var localIds = existing.Where(profile => profile.IsLocal).Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        var byId = existing.Where(profile => !profile.IsLocal).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var entries = (file.Profiles ?? []).Take(MaxProfiles).ToList();

        // Erst festlegen, unter welcher Kennung jedes Profil der Datei hier steht – die Sprung-Server verweisen darauf.
        var targets = new List<(ExportedProfile Entry, ServerProfile? Existing, string Id)>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var (skipped, skippedLocal) = (0, 0);
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Protocol?.Trim(), ServerProfile.Local, StringComparison.OrdinalIgnoreCase))
            {
                skippedLocal++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Host) || (string.IsNullOrWhiteSpace(entry.User) && !IsPlain(entry.Protocol)) || entry.Port is < 1 or > 65535)
            {
                skipped++;
                continue;
            }

            var fileId = IsUsableId(entry.Id) && !localIds.Contains(entry.Id!) ? entry.Id! : string.Empty;
            var match = fileId.Length > 0 && byId.TryGetValue(fileId, out var same)
                ? same
                : existing.FirstOrDefault(profile => !profile.IsLocal && !taken.Contains(profile.Id) && IsSameServer(profile, entry));

            // Zwei Einträge der Datei können nicht dasselbe Profil werden; der zweite wäre nur eine Wiederholung.
            var id = match?.Id ?? fileId;
            if (id.Length > 0 && !taken.Add(id))
            {
                skipped++;
                continue;
            }

            if (fileId.Length > 0)
            {
                ids[fileId] = id;
            }

            targets.Add((entry, match, id));
        }

        // Identitäten: Eine der Datei ist dieselbe wie eine hiesige, wenn die Kennung oder der Name übereinstimmt –
        // dann wird die hiesige ersetzt. Was es nur hier gibt, bleibt.
        var identities = settings.Identities.ToList();
        var identityIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var (newIdentities, changedIdentities) = (0, 0);
        foreach (var entry in (file.Identities ?? []).Take(MaxProfiles))
        {
            var (name, user) = (entry.Name?.Trim() ?? string.Empty, entry.User?.Trim() ?? string.Empty);
            if (!IsUsableId(entry.Id) || name.Length == 0 || !Identity.IsValidUser(user) || identityIds.ContainsKey(entry.Id!))
            {
                continue;
            }

            // Ersetzt wird nur dieselbe Identität (dieselbe Kennung – die Datei stammt dann von hier oder von einem
            // eigenen anderen Rechner). Trägt eine fremde bloß denselben Namen, bleibt die hiesige, wie sie ist:
            // Eine Datei von jemand anderem soll Benutzer und Schlüssel einer Identität nicht austauschen können.
            // Profile der Datei, die auf sie verweisen, bekommen die hiesige.
            var index = identities.FindIndex(own => own.Id == entry.Id);
            if (index < 0
                && identities.FindIndex(own => string.Equals(own.Name, name, StringComparison.CurrentCultureIgnoreCase)) is >= 0 and var sameName)
            {
                identityIds[entry.Id!] = identities[sameName].Id;
                continue;
            }

            var planned = new Identity(index < 0 ? entry.Id! : identities[index].Id, name, user, LocalKeyPath(entry.KeyPath, home), entry.UseAgent);
            identityIds[entry.Id!] = planned.Id;
            if (index < 0)
            {
                identities.Add(planned);
                newIdentities++;
            }
            else if (identities[index] != planned)
            {
                identities[index] = planned;
                changedIdentities++;
            }
        }

        // Eigene Farbschemata: gleiche Kennung ersetzt, neue kommen dazu – nur, was die Terminal-Seite bekommen darf.
        var themes = settings.CustomThemes.ToList();
        var (newThemes, changedThemes) = (0, 0);
        foreach (var theme in (file.Themes ?? []).Where(ThemeImport.IsValid).DistinctBy(theme => theme.Id).Take(TerminalThemes.MaxCustom))
        {
            var index = themes.FindIndex(own => own.Id == theme.Id);
            if (index < 0 && themes.Count < TerminalThemes.MaxCustom)
            {
                themes.Add(theme);
                newThemes++;
            }
            else if (index >= 0 && !ThemeImport.Same(themes[index], theme))
            {
                themes[index] = theme;
                changedThemes++;
            }
        }

        var profiles = new List<PlannedProfile>();
        var bookmarks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var missingKeys = new List<string>();
        foreach (var (entry, match, id) in targets)
        {
            // Mit einer Identität kommen Benutzer und Schlüssel von ihr – so, wie die App sie auch beim Speichern
            // ins Profil schriebe. Eine Kennung, zu der die Datei keine Identität mitbringt, ist keine.
            var identity = entry.Identity is { } wantedIdentity && !IsPlain(entry.Protocol) && entry.Protocol != ServerProfile.Ftp && identityIds.TryGetValue(wantedIdentity, out var localIdentity)
                ? identities.Find(own => own.Id == localIdentity)
                : null;

            // Der Sprung-Server ist ein anderes Profil der Datei – oder eines, das es hier unter dieser Kennung gibt.
            var jump = entry.Jump is { Length: > 0 } wanted
                ? ids.TryGetValue(wanted, out var mapped) && mapped.Length > 0 ? mapped : byId.ContainsKey(wanted) ? wanted : null
                : null;
            if (jump == id)
            {
                jump = null;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Host!.Trim() : entry.Name.Trim();
            // Mosh gibt es nur für SSH und nie über einen Sprung-Server (UDP geht nicht hindurch) – wo es nicht trägt, bleibt
            // es aus, statt dass der Kern das Profil später ablehnt. Das Terminal über UDP schwächt nichts: Mosh wandert mit.
            var overSsh = !IsPlain(entry.Protocol) && entry.Protocol != ServerProfile.Ftp;
            var mosh = entry.Mosh && overSsh && jump is null;
            var planned = new ServerProfile(
                id,
                name,
                entry.Host!.Trim(),
                entry.Port,
                identity?.User ?? entry.User?.Trim() ?? string.Empty,
                entry.Group?.Trim() ?? string.Empty,
                [.. (entry.Tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length > 0)],
                entry.Favorite,
                identity is null ? LocalKeyPath(entry.KeyPath, home) : identity.KeyPath,
                identity?.UseAgent ?? entry.UseAgent,
                match?.LastConnected,
                jump,
                // Agent Forwarding kommt wie alles, was eine Verbindung schwächt, nie aus der Datei: Der Server des
                // Profils könnte sonst mit den Schlüsseln des Agenten weiterspringen. Es bleibt, wie es hier ist.
                // Mit Mosh gibt es kein Agent Forwarding (der Kern schaltet es ab).
                !mosh && (match?.ForwardAgent ?? false),
                match?.HasSavedSecret ?? false,
                Color: ProfileColors.Find(entry.Color) is { Id.Length: > 0 } color ? color.Id : null,
                Protocol: IsPlain(entry.Protocol) || entry.Protocol == ServerProfile.Ftp ? entry.Protocol : null,
                // Nur eine Kennung, die diese Version kennt; sonst bleibt, was hier schon erkannt wurde. Für den
                // Vergleich „geändert?“ zählt das System nicht – es stellt sich beim Verbinden von selbst ein.
                System: ServerSystems.Find(entry.System)?.Id ?? match?.System,
                IdentityId: identity?.Id,
                Baud: entry.Protocol == ServerProfile.Serial && entry.Baud is >= 50 and <= 4_000_000 ? entry.Baud : null,
                // Die Datei ist fremd: Die Notiz ist reiner Text mit einem Ende, die Wünsche zur Verbindung gelten nur
                // in den Grenzen, die auch der Dialog kennt. Was die Verbindung schwächer macht, kommt nie aus der
                // Datei – es bleibt, wie es hier schon eingestellt ist.
                Notes: CleanNotes(entry.Notes),
                NoReconnect: entry.NoReconnect,
                AddressFamily: entry.AddressFamily is "ipv4" or "ipv6" && entry.Protocol != ServerProfile.Serial ? entry.AddressFamily : null,
                ConnectTimeout: entry.ConnectTimeout is >= 3 and <= 120 && entry.Protocol != ServerProfile.Serial ? entry.ConnectTimeout : null,
                // Lebenszeichen: nur in den Grenzen, die auch der Dialog kennt.
                KeepAliveSeconds: entry.KeepAliveSeconds is { } seconds && AppSettings.IsKeepAliveSeconds(seconds) ? seconds : null,
                KeepAliveMissed: entry.KeepAliveMissed is { } missed && AppSettings.IsKeepAliveMissed(missed) ? missed : null,
                // Terminal und Zeichensatz sind Eigenschaften des Servers und wandern mit – nur, was ein Name bzw. ein
                // bekannter Zeichensatz ist. Umgebungsvariablen kommen nie aus der Datei: Es bleiben die von hier.
                Term: ProfileEnv.IsTerminalName(entry.Term) && entry.Term != ProfileEnv.DefaultTerm ? entry.Term : null,
                Charset: !mosh && TerminalCharset.IsKnown(entry.Charset) ? entry.Charset : null,
                Env: match?.Env,
                // „Nur dieser Schlüssel“ engt ein, was angeboten wird – das darf aus der Datei kommen.
                IdentitiesOnly: entry.IdentitiesOnly,
                Mosh: mosh,
                MoshPorts: overSsh ? MoshPorts.Clean(entry.MoshPorts) : null,
                Compression: entry.Compression && !IsPlain(entry.Protocol) && entry.Protocol != ServerProfile.Ftp,
                LegacyAlgorithms: match?.LegacyAlgorithms ?? false,
                // Ein Farbschema nur, wenn es das hier gibt oder die Datei es mitbringt – sonst gilt das allgemeine.
                ThemeId: themes.Concat(TerminalThemes.All).Any(theme => theme.Id == entry.Theme) ? entry.Theme : null,
                // Ebenso FTP ohne Verschlüsselung: Aus der Datei kommt nur „FTP“, und das heißt verschlüsselt. Ob
                // TLS mit dem ersten Byte beginnt (implizit), ist wie „ohne“ eine Sache dieses Rechners.
                FtpTls: entry.Protocol == ServerProfile.Ftp && match is { IsFtp: true } ? match.FtpTls : null,
                // Der aktive Modus hängt am Netz dieses Rechners und lässt ihn lauschen: nie aus einer Datei.
                FtpActive: entry.Protocol == ServerProfile.Ftp && match is { IsFtp: true, FtpActive: true },
                // Wie der Server Dateinamen schreibt, ist seine Eigenschaft und wandert mit – nur die bekannten Werte.
                FtpEncoding: entry.Protocol == ServerProfile.Ftp && entry.FtpEncoding is ServerProfile.FtpLatin1 or ServerProfile.FtpWindows1252 ? entry.FtpEncoding : null,
                // Die Verknüpfung mit der ssh_config dieses Rechners gehört hierher und kommt nie aus einer Datei.
                SshAlias: match?.SshAlias,
                // Tunnel öffnen Ports auf diesem Rechner und am Server: Sie kommen nie aus einer Datei; was hier
                // eingerichtet ist, bleibt.
                Tunnels: match?.Tunnels);

            var change = match is null ? ProfileChange.New : SameContent(match, planned) ? ProfileChange.Unchanged : ProfileChange.Changed;
            profiles.Add(new PlannedProfile(planned, change) { Previous = match });

            // Lesezeichen der Datei, die es hier für dieses Profil noch nicht gibt – nur absolute Pfade, wie beim
            // Lesen der eigenen Einstellungen. Sie kommen zu den vorhandenen dazu.
            if (id.Length > 0 && entry.Bookmarks is { Count: > 0 } paths)
            {
                var known = FolderBookmarks.Of(settings, id);
                var fresh = FolderBookmarks.Sanitize(new() { [id] = paths }).GetValueOrDefault(id, [])
                    .Where(path => !known.Contains(path, StringComparer.Ordinal))
                    .Take(Math.Max(0, FolderBookmarks.MaxPerProfile - known.Count))
                    .ToList();
                if (fresh.Count > 0)
                {
                    bookmarks[id] = fresh;
                }
            }
            if (change != ProfileChange.Unchanged && planned.KeyPath is { } key && !keyExists(key))
            {
                missingKeys.Add(name);
            }
        }

        // Befehle gleichen Namens werden ersetzt, neue angehängt; was es nur hier gibt, bleibt.
        var snippets = settings.Snippets.ToList();
        var (newSnippets, changedSnippets) = (0, 0);
        foreach (var snippet in (file.Snippets ?? []).Take(MaxSnippets).Where(snippet => snippet is { Name.Length: > 0, Text.Length: > 0 }).Select(SnippetFolders.Cleaned))
        {
            var index = snippets.FindIndex(own => own.Name == snippet.Name);
            if (index < 0)
            {
                snippets.Add(snippet);
                newSnippets++;
            }
            else if (snippets[index] != snippet)
            {
                snippets[index] = snippet;
                changedSnippets++;
            }
        }

        return new ImportPlan(profiles, snippets, newSnippets, changedSnippets, file.Settings, missingKeys, skipped)
        {
            Themes = themes,
            NewThemes = newThemes,
            ChangedThemes = changedThemes,
            Identities = identities,
            Bookmarks = bookmarks,
            NewIdentities = newIdentities,
            ChangedIdentities = changedIdentities,
            SkippedLocal = skippedLocal,
        };
    }

    /// <summary>
    /// Legt die neuen und geänderten Profile an. Liefert die, die das Backend abgelehnt hat, mit dem Grund – die
    /// Datei kann von Hand bearbeitet sein, geprüft wird jedes Profil wie ein eingetipptes.
    /// </summary>
    public static IReadOnlyList<(PlannedProfile Profile, string Reason)> SaveProfiles(ImportPlan plan, IProfileStore store)
    {
        var refused = new List<(PlannedProfile, string)>();
        foreach (var planned in plan.Profiles.Where(profile => profile.Change != ProfileChange.Unchanged))
        {
            try
            {
                store.Save(planned.Profile);
            }
            catch (BackendException e)
            {
                refused.Add((planned, $"{e.Code}: {e.Message}"));
            }
        }

        return refused;
    }

    /// <summary>
    /// Die Einstellungen der Datei als Werte dieser App. Was fehlt oder was diese Version nicht kennt, bleibt
    /// <c>null</c> – dann gilt weiter, was hier eingestellt ist.
    /// </summary>
    /// <param name="custom">Die eigenen Farbschemata, die es nach dem Import gibt – das gewählte kann eines davon sein.</param>
    public static (AppLanguage? Language, TerminalTheme? Theme, int? FontSize, bool? ShowHiddenFiles, ProfileSort? Sort, bool? RestoreTabs) Resolve(ExportedSettings settings, IReadOnlyList<TerminalTheme>? custom = null)
        => (
            Enum.TryParse<AppLanguage>(settings.Language, ignoreCase: true, out var language) && Enum.IsDefined(language) ? language : null,
            TerminalThemes.All.Concat(custom ?? []).FirstOrDefault(theme => theme.Id == settings.TerminalTheme),
            settings.TerminalFontSize is { } size ? Math.Clamp(size, TerminalThemes.MinFontSize, TerminalThemes.MaxFontSize) : null,
            settings.ShowHiddenFiles,
            Enum.TryParse<ProfileSort>(settings.ProfileSort, ignoreCase: true, out var sort) && Enum.IsDefined(sort) ? sort : null,
            settings.RestoreTabs);

    /// <summary>Ein Pfad im Benutzerordner beginnt in der Datei mit <c>~/</c>; alles andere bleibt, wie es ist.</summary>
    public static string? PortableKeyPath(string? path, string? home)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(home))
        {
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        var root = home.TrimEnd('/', '\\');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Length > root.Length + 1 && path.StartsWith(root, comparison) && path[root.Length] is '/' or '\\'
            ? "~/" + path[(root.Length + 1)..].Replace('\\', '/')
            : path;
    }

    /// <summary>
    /// Ein Pfad auf einen anderen Rechner oder ein Gerät (<c>\\server\freigabe</c>, <c>//server/…</c>, <c>\\?\…</c>).
    /// Aus einer fremden Datei wird so einer nie übernommen: Schon das Nachsehen, ob es die Schlüsseldatei gibt,
    /// meldete diesen Rechner samt Benutzer bei dem Server an, den die Datei nennt.
    /// </summary>
    public static bool IsNetworkPath(string path)
        => path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>
    /// Macht aus <c>~/…</c> wieder einen Pfad im Benutzerordner dieses Rechners. Ein Pfad ins Netz
    /// (<see cref="IsNetworkPath"/>) ergibt <c>null</c>.
    /// </summary>
    public static string? LocalKeyPath(string? path, string? home)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed) || IsNetworkPath(trimmed))
        {
            return null;
        }

        return !string.IsNullOrEmpty(home) && (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            ? Path.Combine([home, .. trimmed[2..].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)])
            : trimmed;
    }

    // Die Kennung steht später in Dateien und in den Namen der gespeicherten Geheimnisse – nur harmlose Zeichen.
    // Telnet und seriell: Profile ohne Anmeldung durch den Kern – ein Benutzer ist dort keine Pflicht, und eine
    // Identität gibt es nicht.
    private static bool IsPlain(string? protocol) => protocol is ServerProfile.Telnet or ServerProfile.Serial;

    private static bool IsUsableId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSameServer(ServerProfile profile, ExportedProfile entry)
        => profile.Port == entry.Port
            && string.Equals(profile.Host, entry.Host?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.User, entry.User?.Trim(), StringComparison.Ordinal)
            && string.Equals(profile.Name, entry.Name?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameContent(ServerProfile a, ServerProfile b)
        => a.Name == b.Name
            && a.Host == b.Host
            && a.Port == b.Port
            && a.User == b.User
            && a.Group == b.Group
            && a.Tags.SequenceEqual(b.Tags)
            && a.IsFavorite == b.IsFavorite
            && a.KeyPath == b.KeyPath
            && a.UseAgent == b.UseAgent
            && a.JumpId == b.JumpId
            && a.ForwardAgent == b.ForwardAgent
            && a.Color == b.Color
            && a.Protocol == b.Protocol
            && a.Baud == b.Baud
            && a.IdentityId == b.IdentityId
            && a.Notes == b.Notes
            && a.NoReconnect == b.NoReconnect
            && a.AddressFamily == b.AddressFamily
            && a.ConnectTimeout == b.ConnectTimeout
            && a.KeepAliveSeconds == b.KeepAliveSeconds
            && a.KeepAliveMissed == b.KeepAliveMissed
            && a.Term == b.Term
            && a.Charset == b.Charset
            && a.IdentitiesOnly == b.IdentitiesOnly
            && a.Mosh == b.Mosh
            && a.MoshPorts == b.MoshPorts
            && a.Compression == b.Compression
            && a.ThemeId == b.ThemeId
            && a.FtpEncoding == b.FtpEncoding;

    /// <summary>So lang darf die Notiz eines Profils sein (wie im Kern: <c>MAX_NOTES</c>).</summary>
    public const int MaxNotes = 4000;

    // Die Notiz aus einer Datei: Zeilenumbrüche bleiben, andere Steuerzeichen nicht, und sie hat ein Ende.
    private static string CleanNotes(string? notes)
    {
        var clean = new string([.. (notes ?? string.Empty).ReplaceLineEndings("\n").Where(c => c == '\n' || !char.IsControl(c)).Take(MaxNotes)]);
        return clean.Trim();
    }
}
