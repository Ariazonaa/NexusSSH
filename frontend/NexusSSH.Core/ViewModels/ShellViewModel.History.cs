using System.Collections.ObjectModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Verlauf der Verbindungen: wann mit welchem Server, wie lange, warum beendet. Nur Verbindungsdaten – nichts von
// dem, was im Terminal getippt oder ausgegeben wurde. Er steht in den Einstellungen dieses Rechners
// (AppSettings.History) und wandert bei Export und Import nicht mit.
public sealed partial class ShellViewModel
{
    private readonly Dictionary<SessionViewModel, DateTimeOffset> _connectedSince = [];

    /// <summary>Die Uhr für den Verlauf; in Tests gestellt.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Beendete Verbindungen, die jüngste zuerst.</summary>
    public ObservableCollection<ConnectionRecord> History { get; } = [];

    /// <summary>
    /// Die Server, deren Host-Schlüssel die App sich gemerkt hat – für die Einstellungen. Wird mit
    /// <see cref="LoadKnownHosts"/> gelesen, nicht von selbst.
    /// </summary>
    public ObservableCollection<KnownHost> KnownHosts { get; } = [];

    public void LoadKnownHosts()
    {
        KnownHosts.Clear();
        foreach (var host in _backend.Profiles.KnownHosts())
        {
            KnownHosts.Add(host);
        }
    }

    /// <summary>Vergisst den Schlüssel eines Servers: Beim nächsten Verbinden wird wieder danach gefragt.</summary>
    public void ForgetHost(KnownHost host)
    {
        if (_backend.Profiles.ForgetHost(host.Name, host.IsAuthority ? host.Fingerprint : null))
        {
            Log(host.IsAuthority ? L.Get("LogAuthorityForgotten", host.Name) : L.Get("LogHostForgotten", host.Name));
        }

        LoadKnownHosts();
    }

    /// <summary>
    /// Trägt eine Zertifizierungsstelle für Host-Zertifikate ein – nur auf ausdrücklichen Wunsch des Benutzers, aus
    /// dem Dialog dafür. Liefert <c>null</c>, wenn es gelang, sonst den Satz, woran es lag.
    /// </summary>
    public string? TrustAuthority(string patterns, string key)
    {
        try
        {
            var added = _backend.Profiles.TrustAuthority(patterns.Trim(), key.Trim());
            Log(L.Get("LogAuthorityTrusted", added.Name, added.Fingerprint));
            LoadKnownHosts();
            return null;
        }
        catch (BackendException error)
        {
            return error.Code switch
            {
                "invalid_patterns" => L.Get("AuthorityInvalidPatterns"),
                "invalid_key" => L.Get("AuthorityInvalidKey"),
                _ => L.Get("AuthorityFailed", error.Message),
            };
        }
    }

    public void ClearHistory()
    {
        History.Clear();
        Settings.History.Clear();
    }

    private void LoadHistory()
    {
        foreach (var record in Settings.History)
        {
            History.Add(record);
        }
    }

    // Eine Sitzung steht: Von jetzt an zählt ihre Dauer.
    private void NoteConnected(SessionViewModel session)
    {
        _connectedSince[session] = Now();
        if (!session.IsVirtual)
        {
            NoteRecent(session.Profile);
        }
    }

    // Eine Verbindung ist zu Ende – von selbst oder weil ihr Tab geschlossen wurde.
    private void NoteEnded(SessionViewModel session, string end)
    {
        if (!_connectedSince.Remove(session, out var since))
        {
            return;
        }

        var seconds = (int)Math.Clamp((Now() - since).TotalSeconds, 0, int.MaxValue);
        var record = new ConnectionRecord(session.Profile.Name, session.Profile.Address, since, seconds, end);
        History.Insert(0, record);
        Settings.History.Insert(0, record);
        while (History.Count > AppSettings.MaxHistory)
        {
            History.RemoveAt(History.Count - 1);
            Settings.History.RemoveAt(Settings.History.Count - 1);
        }
    }

    private void OnSessionStateChanged(SessionViewModel session)
    {
        if (session.State == ConnectionState.Connected && !_connectedSince.ContainsKey(session))
        {
            NoteConnected(session);
            if (!session.IsVirtual)
            {
                Sound(SoundEvent.Connected);
            }
        }
    }
}
