using System.Globalization;
using Microsoft.Win32;
using NexusSSH.Core.Exchange;

namespace NexusSSH.App.Interop;

/// <summary>
/// Holt die gespeicherten Sitzungen anderer Programme von diesem Rechner: PuTTY und WinSCP aus der Registry des
/// Benutzers, MobaXterm aus seiner Konfigurationsdatei. Nur lesend; gedeutet wird alles in Core
/// (<see cref="ForeignSessions"/>).
/// </summary>
internal static class ForeignSessionReader
{
    private const string PuttyKey = @"Software\SimonTatham\PuTTY\Sessions";
    private const string WinScpKey = @"Software\Martin Prikryl\WinSCP 2\Sessions";

    /// <summary>Größer ist keine Konfigurationsdatei von MobaXterm; was darüber liegt, wird nicht gelesen.</summary>
    private const long MaxFileSize = 16 * 1024 * 1024;

    public static IReadOnlyList<ForeignSession> ReadInstalled()
    {
        var user = Environment.UserName;
        var sessions = new List<ForeignSession>();
        sessions.AddRange(ForeignSessions.FromPutty(Keys(PuttyKey), user));
        sessions.AddRange(ForeignSessions.FromWinScp(Keys(WinScpKey), user));
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "MobaXterm", "MobaXterm.ini");
            if (File.Exists(path) && ReadFile(path, out _) is { } found)
            {
                sessions.AddRange(found);
                break;
            }
        }

        return sessions;
    }

    /// <summary>
    /// Die Sitzungen aus einer Datei eines anderen Programms (welches, sagt ihr Inhalt – <see cref="ForeignFiles"/>);
    /// <c>null</c>, wenn sie sich nicht lesen lässt.
    /// </summary>
    public static IReadOnlyList<ForeignSession>? ReadFile(string path, out string? error)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileSize)
            {
                error = "file too large";
                return null;
            }

            error = null;
            return ForeignFiles.Read(path, File.ReadAllBytes(path), Environment.UserName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }
    }

    // Jeder Unterschlüssel ist eine Sitzung; Zahlen (DWORD) werden zu Text, alles andere als Text bleibt weg.
    private static List<StoredSession> Keys(string path)
    {
        var sessions = new List<StoredSession>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(path);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                if (key is null)
                {
                    continue;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in key.GetValueNames())
                {
                    if (key.GetValue(value) is string or int)
                    {
                        values[value] = Convert.ToString(key.GetValue(value), CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }

                sessions.Add(new StoredSession(name, values));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Ohne Zugriff gibt es von dort eben nichts zu übernehmen.
        }

        return sessions;
    }
}
