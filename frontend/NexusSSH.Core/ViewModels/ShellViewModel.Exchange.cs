using System.Globalization;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Export und Import von Profilen, gespeicherten Befehlen und Einstellungen (ohne Geheimnisse) – siehe DataExchange.
public sealed partial class ShellViewModel
{
    /// <summary>Der Benutzerordner dieses Rechners: Schlüsselpfade darunter stehen in der Datei als <c>~/…</c>.</summary>
    internal string? HomeDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Vorschlag für den Namen der Export-Datei, ohne die Endung: <c>nexusssh-export-2026-10-02</c>.</summary>
    public static string ExportFileName(DateTimeOffset now)
        => "nexusssh-export-" + now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Schreibt Profile, gespeicherte Befehle und Einstellungen in eine Datei – ohne Passwörter und Passphrasen.
    /// <c>false</c>, wenn das Schreiben scheitert; der Grund steht im Protokoll.
    /// </summary>
    public bool ExportData(string path)
    {
        var version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        var file = DataExchange.Create(_backend.Profiles.GetProfiles(), Settings, version, DateTimeOffset.Now, HomeDirectory);
        try
        {
            File.WriteAllText(path, DataExchange.Serialize(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(L.Get("LogExportFailed", e.Message), LogSeverity.Error);
            return false;
        }

        Log(L.Get("LogExported", file.Profiles!.Count, file.Snippets!.Count, path));
        return true;
    }

    /// <summary>
    /// Schreibt das Diagnosepaket (<see cref="DiagnosticBundle"/>) nach <paramref name="path"/>: Versionen, Protokoll,
    /// Ausgabe und bereinigte Einstellungen – ohne Servernamen und ohne Geheimnisse. <c>false</c>, wenn das
    /// Schreiben scheitert; der Grund steht im Protokoll.
    /// </summary>
    /// <param name="coreVersion">Die Version des Kerns, wie die Oberfläche sie nennt.</param>
    /// <param name="isDemo">Läuft die App mit Beispieldaten?</param>
    public bool SaveDiagnostics(string path, string coreVersion, bool isDemo)
    {
        try
        {
            var input = new DiagnosticInput(
                typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
                coreVersion,
                isDemo,
                _backend.Profiles.GetProfiles(),
                Settings.Identities,
                ReadEnd(AppDataLocation.SettingsFile, int.MaxValue),
                ReadEnd(AppDataLocation.LogFile, DiagnosticBundle.MaxLogBytes),
                [.. Output],
                Sessions.Count,
                Environment.UserName,
                HomeDirectory);
            DiagnosticBundle.Write(path, input, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log(L.Get("LogDiagnosticsFailed", e.Message), LogSeverity.Error);
            return false;
        }

        Log(L.Get("LogDiagnosticsSaved", path));
        return true;
    }

    /// <summary>Schreibt den Verlauf der Auslastung einer Sitzung als CSV. <c>false</c>, wenn das Schreiben scheitert.</summary>
    public bool SaveLoadHistory(SessionViewModel session, string path)
    {
        try
        {
            File.WriteAllText(path, session.Inspector.History.ToCsv());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(L.Get("LoadHistoryFailed", e.Message), LogSeverity.Error);
            return false;
        }

        Log(L.Get("LoadHistorySaved", session.Inspector.History.Count, path));
        return true;
    }

    // Das Ende einer Textdatei, höchstens so viele Bytes; null, wenn es sie nicht gibt oder sie sich nicht lesen lässt.
    private static string? ReadEnd(string file, int most)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - most), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Liest eine Export-Datei und errechnet, was ihr Import täte – noch ohne etwas zu ändern. <c>null</c>, wenn die
    /// Datei nicht taugt; <paramref name="error"/> sagt dann, warum.
    /// </summary>
    public ImportPlan? PrepareImport(string path, out string? error)
    {
        try
        {
            error = null;
            return DataExchange.Plan(DataExchange.Read(path), _backend.Profiles.GetProfiles(), Settings, HomeDirectory);
        }
        catch (ExchangeException e)
        {
            error = e.Error switch
            {
                ExchangeError.NotAnExport => L.Get("DataImportNotAnExport"),
                ExchangeError.TooNew => L.Get("DataImportTooNew"),
                ExchangeError.TooLarge => L.Get("DataImportTooLarge"),
                ExchangeError.Unreadable => L.Get("DataImportUnreadable"),
                _ => L.Get("DataImportMalformed"),
            };
            return null;
        }
    }

    /// <summary>Was der Import täte, in Sätzen – für die Rückfrage davor. Texte aus der Datei stehen als reiner Text da.</summary>
    public static IReadOnlyList<string> DescribeImport(ImportPlan plan)
    {
        var lines = new List<string>();
        if (plan.Skipped > 0)
        {
            lines.Add(L.Get("DataImportSkipped", plan.Skipped));
        }

        if (plan.SkippedLocal > 0)
        {
            lines.Add(L.Get("DataImportSkippedLocal", plan.SkippedLocal));
        }

        if (plan.NewBookmarks > 0)
        {
            lines.Add(L.Get("DataImportBookmarks", plan.NewBookmarks));
        }

        // Profile, die danach woandershin zeigen: beim Namen genannt, mit altem und neuem Ziel.
        foreach (var moved in plan.Profiles.Where(profile => profile.Retargets).Take(8))
        {
            lines.Add(L.Get("DataImportRetargeted", Format.Visible(moved.Profile.Name), Format.Visible(moved.Profile.Address), Format.Visible(moved.Previous!.Address)));
        }

        if (plan.MissingKeys.Count > 0)
        {
            lines.Add(L.Get("DataImportMissingKeys", string.Join(", ", plan.MissingKeys.Take(8).Select(Format.Visible)) + (plan.MissingKeys.Count > 8 ? " …" : string.Empty)));
        }

        lines.Add(L.Get("DataImportNoSecrets"));
        return lines;
    }

    /// <summary>Übernimmt die gewählten Teile. Was das Backend an Profilen ablehnt, steht danach unter „Probleme“.</summary>
    public void ImportData(ImportPlan plan, ImportChoice choice)
    {
        var (added, changed) = (0, 0);
        if (choice.Profiles)
        {
            // Die Identitäten zuerst: Die Profile der Datei verweisen auf sie.
            if (plan.NewIdentities + plan.ChangedIdentities > 0)
            {
                // Welche sich geändert haben: Ihre Profile ziehen gleich nach, statt erst beim nächsten Speichern
                // unbemerkt Benutzer und Schlüssel zu wechseln.
                HashSet<string> changedIdentities = [.. plan.Identities.Where(identity => Settings.Identities.Any(own => own.Id == identity.Id && own != identity)).Select(identity => identity.Id)];
                Settings.Identities = [.. plan.Identities];
                foreach (var profile in _profiles.Where(profile => profile.IdentityId is { } id && changedIdentities.Contains(id)).ToList())
                {
                    SaveProfile(profile);
                }

                OnPropertyChanged(nameof(Identities));
            }

            // Was abgelehnt wurde, ist nicht angekommen – die Zahlen im Protokoll sollen stimmen.
            var refused = DataExchange.SaveProfiles(plan, _backend.Profiles);
            added = plan.NewProfiles - refused.Count(entry => entry.Profile.Change == ProfileChange.New);
            changed = plan.ChangedProfiles - refused.Count(entry => entry.Profile.Change == ProfileChange.Changed);
            foreach (var (profile, reason) in refused)
            {
                Log(L.Get("LogImportRefused", Format.Visible(profile.Profile.Name), reason), LogSeverity.Warning);
            }

            foreach (var name in plan.MissingKeys)
            {
                Log(L.Get("LogImportMissingKey", Format.Visible(name)), LogSeverity.Warning);
            }

            // Die Lesezeichen der übernommenen Profile kommen zu den vorhandenen dazu – nicht für Profile, die das
            // Backend abgelehnt hat.
            var arrived = false;
            foreach (var (id, paths) in plan.Bookmarks.Where(entry => refused.All(lost => lost.Profile.Profile.Id != entry.Key)))
            {
                foreach (var path in paths)
                {
                    arrived |= FolderBookmarks.Add(Settings, id, path);
                }
            }

            if (arrived)
            {
                BookmarksChanged();
            }
        }

        var snippets = 0;
        if (choice.Snippets && plan.NewSnippets + plan.ChangedSnippets > 0)
        {
            SetSnippets(plan.Snippets);
            snippets = plan.NewSnippets + plan.ChangedSnippets;
        }

        // Die eigenen Farbschemata gehören zu den Einstellungen – und kommen zuerst: Das gewählte kann eines davon sein.
        if (choice.Settings && plan.NewThemes + plan.ChangedThemes > 0)
        {
            Settings.CustomThemes = [.. plan.Themes];
            ThemesChanged();
        }

        if (choice.Settings && plan.Settings is { } settings)
        {
            var (language, theme, fontSize, showHidden, sort, restoreTabs) = DataExchange.Resolve(settings, Settings.CustomThemes);
            TerminalTheme = theme ?? TerminalTheme;
            TerminalFontSize = fontSize ?? TerminalFontSize;
            ShowHiddenFiles = showHidden ?? ShowHiddenFiles;
            ProfileSort = sort ?? ProfileSort;
            RestoreTabs = restoreTabs ?? RestoreTabs;
            if (settings.Shortcuts is { } shortcuts)
            {
                ImportShortcuts(shortcuts);
            }

            // Die Sprache gilt ab dem nächsten Start – wie beim Umstellen in den Einstellungen.
            Settings.Language = language ?? Settings.Language;
        }

        Log(L.Get("LogImportedData", added, changed, snippets));
    }
}
