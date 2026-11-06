namespace NexusSSH.Core;

/// <summary>
/// Wo NexusSSH seine Daten ablegt: <c>%LOCALAPPDATA%\NexusSSH</c>. Die Umgebungsvariable
/// <c>NEXUSSSH_DATA_DIR</c> legt einen anderen Ordner fest – für Tests an der laufenden App, die die echten
/// Profile nicht anfassen sollen.
/// </summary>
public static class AppDataLocation
{
    public const string OverrideVariable = "NEXUSSSH_DATA_DIR";

    /// <summary>
    /// Umgebungsvariable für einen anderen Ordner als <c>~/.ssh</c>: Aus ihm kommen <c>known_hosts</c>, die
    /// Standardschlüssel und die <c>config</c> für den Import.
    /// </summary>
    public const string SshDirectoryVariable = "NEXUSSSH_SSH_DIR";

    public static string Root { get; } = Resolve(
        Environment.GetEnvironmentVariable(OverrideVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Der Datenordner: ausdrücklich genannt, sonst unter dem Benutzerprofil.</summary>
    public static string Resolve(string? custom, string localApplicationData)
        => custom is { Length: > 0 }
            ? Path.GetFullPath(custom)
            : Path.Combine(localApplicationData, "NexusSSH");

    /// <summary>Der Ordner aus <see cref="SshDirectoryVariable"/>; <c>null</c>, wenn der des Benutzers gilt.</summary>
    public static string? SshDirectory { get; } = Environment.GetEnvironmentVariable(SshDirectoryVariable) is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : null;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Transfers, die sich noch einmal versuchen lassen – auch nach einem Neustart.</summary>
    public static string TransfersFile => Path.Combine(Root, "transfers.json");

    /// <summary>Benutzerdaten des eingebetteten Browsers (WebView2) für das Terminal.</summary>
    public static string WebViewData => Path.Combine(Root, "WebView2");

    public static string LogFile => Path.Combine(Root, "logs", "app.log");

    /// <summary>Das Transfer-Protokoll – nur geschrieben, wenn der Benutzer es eingeschaltet hat.</summary>
    public static string TransferLogFile => Path.Combine(Root, "logs", "transfers.csv");

    /// <summary>
    /// Unter diesem Ordner liegen die Dateien, die zum Bearbeiten vom Server geholt wurden – im Temp-Ordner, weil
    /// sie nur für die Dauer der Sitzung da sind.
    /// </summary>
    public static string EditRoot => Path.Combine(Path.GetTempPath(), "NexusSSH");
}
