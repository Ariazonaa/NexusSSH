using System.Net.Sockets;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.ViewModels;

/// <summary>Sieht nach, ob ein Server auf seinem Port antwortet – ohne sich anzumelden.</summary>
public interface IReachability
{
    Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellation);
}

/// <summary>
/// Ein TCP-Verbindungsaufbau, der sofort wieder endet. Der Server sieht eine Verbindung, die nichts sagt – mehr
/// nicht; angemeldet wird nichts, und kein Geheimnis verlässt den Rechner.
/// </summary>
public sealed class TcpReachability : IReachability
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException or ArgumentException)
        {
            return false;
        }
    }
}

// Erreichbarkeit auf den Kacheln der Startseite. Nur, wenn es eingeschaltet ist, nur solange die Startseite zu
// sehen ist, und nicht öfter als alle paar Minuten: Jede Prüfung ist eine Verbindung, die der Server sieht.
public sealed partial class ShellViewModel
{
    /// <summary>So lange gilt ein Ergebnis; früher wird nicht noch einmal geprüft.</summary>
    public static readonly TimeSpan ReachabilityInterval = TimeSpan.FromMinutes(3);

    private const int ProbesAtOnce = 8;

    private readonly Dictionary<string, bool> _reachable = [];
    private IReachability? _reachability;
    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;
    private bool _probing;

    /// <summary>Die Antworten haben sich geändert – die Kacheln zeigen sie neu.</summary>
    public event Action? ReachabilityChanged;

    /// <summary>Ob die Startseite zeigt, welche Server antworten. Aus heißt: Die App klopft nirgends an.</summary>
    public bool CheckReachability
    {
        get => Settings.CheckReachability;
        set
        {
            if (value == Settings.CheckReachability)
            {
                return;
            }

            Settings.CheckReachability = value;
            OnPropertyChanged();
            if (!value && _reachable.Count > 0)
            {
                _reachable.Clear();
                ReachabilityChanged?.Invoke();
            }

            _probedAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Womit geprüft wird; ohne das (Tests, Beispieldaten) gibt es die Anzeige nicht.</summary>
    public void UseReachability(IReachability reachability) => _reachability = reachability;

    /// <summary><c>true</c>/<c>false</c> = antwortet oder nicht, <c>null</c> = nicht geprüft.</summary>
    public bool? IsReachable(string profileId) => _reachable.TryGetValue(profileId, out var reachable) ? reachable : null;

    /// <summary>
    /// Prüft die Server der Startseite – wenn es eingeschaltet ist, die Startseite zu sehen ist und die letzte
    /// Prüfung lange genug her ist. Server hinter einem Sprung-Server bleiben ungeprüft: Von hier aus sind sie
    /// nicht zu erreichen, und das wäre kein Befund über sie.
    /// </summary>
    public async Task ProbeReachabilityAsync()
    {
        if (!CheckReachability || _reachability is not { } reachability || HasActiveSession || _probing || Now() - _probedAt < ReachabilityInterval)
        {
            return;
        }

        _probing = true;
        _probedAt = Now();
        try
        {
            var targets = _profiles.Where(profile => profile.JumpId is null).ToList();
            var changed = false;
            foreach (var batch in targets.Chunk(ProbesAtOnce))
            {
                var answers = await Task.WhenAll(batch.Select(profile => reachability.IsReachableAsync(profile.Host, profile.Port, CancellationToken.None)));
                for (var index = 0; index < batch.Length; index++)
                {
                    changed |= !_reachable.TryGetValue(batch[index].Id, out var before) || before != answers[index];
                    _reachable[batch[index].Id] = answers[index];
                }
            }

            // Was es nicht mehr gibt oder jetzt über einen Sprung-Server läuft, verliert seinen Punkt.
            foreach (var gone in _reachable.Keys.Except(targets.Select(profile => profile.Id)).ToList())
            {
                _reachable.Remove(gone);
                changed = true;
            }

            if (changed && CheckReachability)
            {
                ReachabilityChanged?.Invoke();
            }
        }
        finally
        {
            _probing = false;
        }
    }
}
