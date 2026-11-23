using NexusSSH.Core.Localization;

namespace NexusSSH.Core.ViewModels;

// Aktivität und Stille am Reiter: Neue Ausgabe in einem Tab, der nicht zu sehen ist, zeigt einen Punkt; auf Wunsch
// meldet die App, wenn eine Sitzung eine Weile still war (etwa am Ende eines langen Laufs). Gezählt wird in der
// Sitzung (SessionViewModel.CountOutput), entschieden hier.
public sealed partial class ShellViewModel
{
    /// <summary>Kürzeste und längste Zeit für <see cref="SilenceSeconds"/>.</summary>
    public const int MinSilenceSeconds = 5;

    public const int MaxSilenceSeconds = 600;

    // Je Sitzung, auf deren Stille geachtet wird: der Zählerstand beim letzten Blick, seit wann er so steht, und ob
    // seitdem Ausgabe kam (erst dann ist Stille eine Meldung wert – und danach erst wieder nach neuer Ausgabe).
    private readonly Dictionary<SessionViewModel, SilenceWatch> _silence = [];
    private IDisposable? _silenceTimer;

    private sealed class SilenceWatch(long count, DateTimeOffset since)
    {
        public long Count { get; set; } = count;

        public DateTimeOffset Since { get; set; } = since;

        public bool Armed { get; set; }
    }

    /// <summary>Neue Ausgabe in einem Tab, der nicht zu sehen ist, kennzeichnet den Reiter mit einem Punkt.</summary>
    public bool ActivityTab
    {
        get => Settings.ActivityTab;
        set
        {
            if (value != Settings.ActivityTab)
            {
                Settings.ActivityTab = value;
                OnPropertyChanged();
                UpdateInView();
            }
        }
    }

    /// <summary>So lange muss eine Sitzung still sein, bis die App es meldet (Sekunden, 5 bis 600).</summary>
    public int SilenceSeconds
    {
        get => Math.Clamp(Settings.SilenceSeconds, MinSilenceSeconds, MaxSilenceSeconds);
        set
        {
            var seconds = Math.Clamp(value, MinSilenceSeconds, MaxSilenceSeconds);
            if (seconds != Settings.SilenceSeconds)
            {
                Settings.SilenceSeconds = seconds;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Auf die Stille von <paramref name="session"/> achten oder nicht mehr. Gemeldet wird, wenn nach Ausgabe
    /// <see cref="SilenceSeconds"/> lang nichts mehr kam – einmal, bis wieder etwas kommt.
    /// </summary>
    public void ToggleSilenceWatch(SessionViewModel session)
    {
        session.WatchesSilence = !session.WatchesSilence;
        if (session.WatchesSilence)
        {
            _silence[session] = new SilenceWatch(session.OutputCount, Now());
            ScheduleSilenceCheck();
        }
        else
        {
            _silence.Remove(session);
            session.ClearSilence();
        }
    }

    /// <summary>
    /// Sieht nach den Sitzungen, auf deren Stille geachtet wird – einmal je Sekunde, solange es eine gibt. In Tests
    /// ruft der Test es selbst (mit verstellter Uhr).
    /// </summary>
    internal void CheckSilence()
    {
        var now = Now();
        foreach (var (session, watch) in _silence.ToList())
        {
            if (!Sessions.Contains(session))
            {
                _silence.Remove(session);
                continue;
            }

            var count = session.OutputCount;
            if (count != watch.Count)
            {
                (watch.Count, watch.Since, watch.Armed) = (count, now, true);
                continue;
            }

            if (!watch.Armed || now - watch.Since < TimeSpan.FromSeconds(SilenceSeconds))
            {
                continue;
            }

            watch.Armed = false;
            if (!session.IsInView)
            {
                session.MarkSilence();
            }

            Log(L.Get("LogSilence", session.Profile.Name, SilenceSeconds));
            Notify(session.Profile.Name, L.Get("NotifySilence", SilenceSeconds));
        }
    }

    private void ScheduleSilenceCheck()
    {
        if (_silenceTimer is not null || _schedule is null || _silence.Count == 0)
        {
            return;
        }

        _silenceTimer = _schedule(TimeSpan.FromSeconds(1), () => _dispatcher.Post(() =>
        {
            _silenceTimer?.Dispose();
            _silenceTimer = null;
            CheckSilence();
            ScheduleSilenceCheck();
        }));
    }

    // Wer zu sehen ist: die Bereiche des vorderen Tabs (bei einem vergrößerten nur der) und Terminals in eigenen
    // Fenstern. Zu sehen heißt: keine Marken für Aktivität und Stille.
    private void UpdateInView()
    {
        IReadOnlyList<SessionViewModel> shown = Layout.Zoomed is { } zoomed ? [zoomed] : Layout.Panes;
        foreach (var session in Sessions)
        {
            session.SetInView(session.IsDetached || shown.Contains(session), ActivityTab);
        }
    }
}
