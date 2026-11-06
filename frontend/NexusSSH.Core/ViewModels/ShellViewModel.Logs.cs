using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusSSH.Core.ViewModels;

// Die Protokolle des Servers: eine Seite über den Bereichen, wie der Betrachter. Sie gehört zu der Sitzung, für die
// sie geöffnet wurde, und schließt mit ihr.
public sealed partial class ShellViewModel
{
    /// <summary>Die offene Seite mit den Protokollen eines Servers; <c>null</c>, wenn sie zu ist.</summary>
    [ObservableProperty]
    public partial ServerLogsViewModel? Logs { get; private set; }

    /// <summary>
    /// Ob sich für die aktive Sitzung Protokolle lesen lassen: Das braucht Befehle am Server, also SSH und ein
    /// Konto, für das der Server welche ausführt.
    /// </summary>
    // Journal und `tail` gibt es unter Windows nicht.
    public bool CanShowLogs => ActiveSession is { IsVirtual: false, IsClosed: false, Profile.IsSsh: true, Files: { HasCommands: true, IsWindowsServer: false } };

    /// <summary>Ob eine Seite eines Servers über den Bereichen offen ist (die Protokolle oder seine Einzelheiten).</summary>
    public bool HasServerPage => Logs is not null || Details is not null;

    /// <summary>Öffnet die Seite für die aktive Sitzung und liest das Journal.</summary>
    public ServerLogsViewModel? ShowLogs()
    {
        if (!CanShowLogs)
        {
            return null;
        }

        IsSettingsOpen = false;
        IsHelpOpen = false;
        Details = null;
        var logs = new ServerLogsViewModel(ActiveSession!);
        Logs = logs;
        _ = logs.LoadSourcesAsync();
        _ = logs.LoadAsync();
        return logs;
    }

    public void CloseLogs() => Logs = null;
}
