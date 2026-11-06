using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Start mit Ziel: `NexusSSH.exe benutzer@host`, ein Klick auf einen `ssh://`-Link, ein Eintrag der Sprungliste.
// Ein Ziel kommt von außen (ein Skript, eine Seite im Netz) – verbunden wird erst, wenn der Benutzer es bestätigt
// hat, und das Ziel nennt nur Benutzer, Host und Port (LaunchRequest). Ein Eintrag der Sprungliste nennt nur die
// Kennung eines Servers, den der Benutzer selbst gespeichert hat; der öffnet sich ohne Frage.
public sealed partial class ShellViewModel
{
    /// <summary>So viele zuletzt benutzte Server stehen in der Sprungliste.</summary>
    public const int JumpListSize = 6;

    /// <summary>Der Benutzer, wenn ein Link keinen nennt – wie bei <c>ssh host</c>; in Tests gestellt.</summary>
    internal string LocalUserName { get; set; } = Environment.UserName;

    /// <summary>
    /// Öffnet, was ein Start nennt. Ein Ziel erst nach der Rückfrage; nennt ein Link keinen Benutzer, gilt ein
    /// gespeicherter Server mit demselben Host und Port, sonst der Name des Kontos hier (wie bei <c>ssh host</c>).
    /// <c>null</c>, wenn nichts geöffnet wurde.
    /// </summary>
    public async Task<SessionViewModel?> OpenLaunchAsync(LaunchRequest request)
    {
        if (request.ProfileId is { } id)
        {
            if (_profiles.FirstOrDefault(profile => profile.Id == id && !profile.IsTransient) is { } saved)
            {
                return OpenSession(saved);
            }

            Log(L.Get("LogLaunchUnknownServer"), LogSeverity.Warning);
            return null;
        }

        if (request.Target is not { } target)
        {
            return null;
        }

        var known = KnownFor(target);
        if (target.User.Length == 0 && known is null)
        {
            // Wie `ssh host`: der Name des Kontos auf diesem Rechner – er steht in der Rückfrage.
            if (QuickTarget.Parse($"{LocalUserName}@{Shown(target)}") is not { } local)
            {
                Log(L.Get("LogLaunchNoUser", Format.Visible(target.Host)), LogSeverity.Warning);
                return null;
            }

            target = local;
        }

        var where = known is null
            ? L.Get("LaunchAskNew", Format.Visible(Shown(target)))
            : L.Get("LaunchAskKnown", Format.Visible(Shown(target)), known.Name);
        var source = request.FromLink ? L.Get("LaunchFromLink") : L.Get("LaunchFromCommandLine");
        if (!await _prompts.ConfirmAsync(L.Get("LaunchTitle"), $"{source}\n\n{where}", L.Get("LaunchConnect")))
        {
            Log(L.Get("LogLaunchDeclined", Format.Visible(Shown(target))));
            return null;
        }

        return known is not null ? OpenSession(known) : QuickConnect(target.Display);
    }

    /// <summary>Die zuletzt benutzten Server haben sich vielleicht geändert (Liste neu geladen, Verbindung steht).</summary>
    public event Action? RecentServersChanged;

    // Verbindungen dieses Laufs: Der Kern merkt sich die Zeit im Profil, die geladene Liste erfährt das erst beim
    // nächsten Laden.
    private readonly Dictionary<string, DateTimeOffset> _connectedNow = [];

    /// <summary>Die zuletzt benutzten Server, der jüngste zuerst – für die Sprungliste.</summary>
    public IReadOnlyList<ServerProfile> RecentServers() => [.. _profiles
        .Where(profile => !profile.IsTransient)
        .Select(profile => (Profile: profile, When: _connectedNow.TryGetValue(profile.Id, out var now) && now > (profile.LastConnected ?? DateTimeOffset.MinValue) ? now : profile.LastConnected))
        .Where(entry => entry.When is not null)
        .OrderByDescending(entry => entry.When)
        .Take(JumpListSize)
        .Select(entry => entry.Profile)];

    private void NoteRecent(ServerProfile profile)
    {
        if (!profile.IsTransient)
        {
            _connectedNow[profile.Id] = Now();
            RecentServersChanged?.Invoke();
        }
    }

    private void RecentMayHaveChanged() => RecentServersChanged?.Invoke();

    // Ein gespeicherter Server zum Ziel: gleicher Host und Port, und gleicher Benutzer, wenn das Ziel einen nennt.
    private ServerProfile? KnownFor(QuickTarget target) => _profiles
        .Where(profile => profile.IsSsh
            && !profile.IsTransient
            && profile.Port == target.Port
            && string.Equals(profile.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && (target.User.Length == 0 || string.Equals(profile.User, target.User, StringComparison.Ordinal)))
        .OrderByDescending(profile => profile.LastConnected ?? DateTimeOffset.MinValue)
        .FirstOrDefault();

    private static string Shown(QuickTarget target) => target.User.Length == 0 ? target.Display.TrimStart('@') : target.Display;
}
