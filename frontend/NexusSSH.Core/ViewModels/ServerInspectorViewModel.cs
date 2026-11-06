using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Server Inspector einer Sitzung (Spec §6): Status, Auslastung, Systemdaten.</summary>
public sealed partial class ServerInspectorViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private IServerMonitor? _monitor;
    private bool _watched;

    public ServerInspectorViewModel(ServerProfile profile, IUiDispatcher dispatcher)
    {
        Profile = profile;
        _dispatcher = dispatcher;
        StatusText = L.Get("Offline");
    }

    public ServerProfile Profile { get; }

    /// <summary>Die Überwachung der aktuellen Verbindung, für Tests.</summary>
    internal IServerMonitor? Monitor => _monitor;

    /// <summary>Die Verbindung steht.</summary>
    [ObservableProperty]
    public partial bool IsOnline { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Es gibt eine Messung – die Ansicht zeigt den Block mit der Auslastung nur dann.</summary>
    [ObservableProperty]
    public partial bool HasMetrics { get; private set; }

    /// <summary>Verbunden, aber der Server liefert keine Auslastung (kein Linux, Befehle nicht erlaubt).</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; private set; }

    /// <summary>Zu <see cref="IsUnavailable"/>: warum der Server keine Werte liefert, wie der Kern es nennt; nicht bei abgerissener Verbindung.</summary>
    public string? UnavailableDetail => _linkLost is null ? _monitor?.UnavailableDetail : null;

    /// <summary>Was an Stelle der Werte steht, wenn <see cref="IsUnavailable"/>: liefert keine, oder verstummte und wird neu versucht.</summary>
    [ObservableProperty]
    public partial string UnavailableText { get; private set; } = string.Empty;

    /// <summary>Verbunden und sichtbar, die erste Messung steht noch aus.</summary>
    [ObservableProperty]
    public partial bool IsMeasuring { get; private set; }

    [ObservableProperty]
    public partial double CpuPercent { get; private set; }

    [ObservableProperty]
    public partial double MemoryPercent { get; private set; }

    [ObservableProperty]
    public partial double DiskPercent { get; private set; }

    [ObservableProperty]
    public partial string CpuText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DiskText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OsName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Kernel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UptimeText { get; private set; } = string.Empty;

    /// <summary>Lastdurchschnitt über 1, 5 und 15 Minuten; leer, wenn der Server keinen nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoad))]
    public partial string LoadText { get; private set; } = string.Empty;

    /// <summary>Durchsatz des Netzwerks, empfangen und gesendet.</summary>
    [ObservableProperty]
    public partial string NetworkText { get; private set; } = string.Empty;

    /// <summary>Knapp für die Zeile unten: Arbeitsspeicher belegt von insgesamt (<c>2,1 / 8 GB</c>).</summary>
    [ObservableProperty]
    public partial string MemoryShortText { get; private set; } = string.Empty;

    /// <summary>Knapp für die Zeile unten: der Durchsatz des Netzes (<c>↓ 1,2 ↑ 0,3 MB/s</c>).</summary>
    [ObservableProperty]
    public partial string NetworkShortText { get; private set; } = string.Empty;

    /// <summary>Knapp für die Zeile unten: wie voll die Platte ist (<c>64 %</c>).</summary>
    [ObservableProperty]
    public partial string DiskShortText { get; private set; } = string.Empty;

    /// <summary>Für die Zeile unten: wie lange der Server läuft, als Satz (<c>läuft seit 12 Tagen</c>).</summary>
    [ObservableProperty]
    public partial string UptimeSinceText { get; private set; } = string.Empty;

    public bool HasLoad => LoadText.Length > 0;

    /// <summary>Durchsatz der Datenträger: gelesen, geschrieben, wie beschäftigt; leer, wenn der Server ihn nicht nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiskIo))]
    public partial string DiskIoText { get; private set; } = string.Empty;

    public bool HasDiskIo => DiskIoText.Length > 0;

    /// <summary>Der Durchsatz je Platte; leer bei nur einer – die steht schon in der Summe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiskIoDevices))]
    public partial IReadOnlyList<DiskIoDevice> DiskIoDevices { get; private set; } = BindableList.Of<DiskIoDevice>([]);

    public bool HasDiskIoDevices => DiskIoDevices.Count > 0;

    /// <summary>Jeder Kern einzeln; leer, wenn der Server nur einen hat oder sie nicht einzeln nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCores), nameof(CoresText), nameof(HasCpuDetails))]
    public partial IReadOnlyList<CoreLoad> Cores { get; private set; } = BindableList.Of<CoreLoad>([]);

    public bool HasCores => Cores.Count > 1;

    /// <summary>Alle Kerne in einem Satz – die Balken selbst haben für Screenreader keinen Namen.</summary>
    public string CoresText => $"{L.Get("Cores")}: {string.Join(", ", Cores.Select(core => Format.Percent(core.Percent)))}";

    /// <summary>Woraus die Auslastung besteht: Benutzer, System – und, wenn es sie gibt, Warten und Gestohlenes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSplit), nameof(HasCpuDetails))]
    public partial string SplitText { get; private set; } = string.Empty;

    public bool HasSplit => SplitText.Length > 0;

    /// <summary>Ob es zur CPU mehr gibt als den Anteil (der steht in der Leiste unter dem Terminal): Aufteilung oder Kerne.</summary>
    public bool HasCpuDetails => HasSplit || HasCores;

    /// <summary>Was der Speicher neben dem Belegten hält: Zwischenspeicher und Freies. Leer, wenn der Server nicht aufteilt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemorySplit))]
    public partial string MemorySplitText { get; private set; } = string.Empty;

    public bool HasMemorySplit => MemorySplitText.Length > 0;

    /// <summary>Die Auslagerung: Anteil, belegt und Größe. Leer, wenn der Server keine hat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSwap))]
    public partial string SwapText { get; private set; } = string.Empty;

    public bool HasSwap => SwapText.Length > 0;

    /// <summary>Das Netz je Karte, mit ihren Adressen; leer, wenn der Server sie nicht einzeln nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInterfaces))]
    public partial IReadOnlyList<NetInterface> Interfaces { get; private set; } = BindableList.Of<NetInterface>([]);

    public bool HasInterfaces => Interfaces.Count > 0;

    /// <summary>Alle Dateisysteme des Servers mit ihrer Belegung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisks), nameof(ShowsRootDisk))]
    public partial IReadOnlyList<DiskUsage> Disks { get; private set; } = BindableList.Of<DiskUsage>([]);

    public bool HasDisks => Disks.Count > 0;

    /// <summary>Der einzelne Balken für <c>/</c> steht nur da, solange es die Liste nicht gibt – sie nennt ihn auch.</summary>
    public bool ShowsRootDisk => !HasDisks;

    /// <summary>Hersteller und Modell des Rechners; leer, wenn der Server sie nicht nennt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMachine))]
    public partial string MachineText { get; private set; } = string.Empty;

    public bool HasMachine => MachineText.Length > 0;

    /// <summary>Name des Prozessors und die Zahl seiner Kerne.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProcessor))]
    public partial string ProcessorText { get; private set; } = string.Empty;

    public bool HasProcessor => ProcessorText.Length > 0;

    /// <summary>Ob und wie der Server virtuell ist; leer, wenn er es nicht sagt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVirtualization))]
    public partial string VirtualizationText { get; private set; } = string.Empty;

    public bool HasVirtualization => VirtualizationText.Length > 0;

    /// <summary>Uhrzeit und Zeitzone des Servers und wie weit seine Uhr von der eigenen abweicht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClock))]
    public partial string ClockText { get; private set; } = string.Empty;

    public bool HasClock => ClockText.Length > 0;

    private static string Processor(ServerInfo? info)
    {
        var cores = info?.CoreCount switch
        {
            null => string.Empty,
            1 => L.Get("CoreCountOne"),
            { } count => L.Get("CoreCount", count),
        };
        return string.Join(" · ", ((string[])[info?.CpuModel ?? string.Empty, cores]).Where(part => part.Length > 0));
    }

    private static string Virtualization(ServerInfo? info) => info?.Virtualization switch
    {
        null or "" => string.Empty,
        ServerInfo.NoVirtualization => L.Get("VirtNone"),
        ServerInfo.SomeVirtualization => L.Get("VirtUnknown"),
        var kind => kind,
    };

    /// <summary><c>14:03:22 · UTC+02:00 (CEST) · geht 2 min vor</c>.</summary>
    internal static string Describe(ServerClock? clock)
    {
        if (clock is null)
        {
            return string.Empty;
        }

        var offset = clock.Time.Offset;
        var zone = string.Create(CultureInfo.InvariantCulture, $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}");
        if (clock.Zone.Length > 0)
        {
            zone += $" ({clock.Zone})";
        }

        var apart = (int)Math.Min(Math.Round(Math.Abs(clock.Drift.TotalSeconds)), int.MaxValue);

        // Unter zwei Minuten in Sekunden: „1 min“ für 119 Sekunden wäre die halbe Wahrheit.
        var far = apart < 120 ? $"{apart} s" : Format.Duration(apart);
        var drift = clock.Drift.Duration() < ServerClock.Tolerance
            ? L.Get("ClockSame")
            : clock.Drift > TimeSpan.Zero ? L.Get("ClockAhead", far) : L.Get("ClockBehind", far);
        return $"{clock.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {zone} · {drift}";
    }

    private static string Describe(CpuSplit? split)
    {
        if (split is null)
        {
            return string.Empty;
        }

        List<string> parts = [L.Get("CpuUser", Format.Percent(split.User)), L.Get("CpuSystem", Format.Percent(split.System))];

        // Warten und Gestohlenes nur, wenn es sie gibt – die meisten Server haben beides nicht.
        if (split.IoWait >= 1)
        {
            parts.Add(L.Get("CpuIoWait", Format.Percent(split.IoWait)));
        }

        if (split.Steal >= 1)
        {
            parts.Add(L.Get("CpuSteal", Format.Percent(split.Steal)));
        }

        return string.Join(" · ", parts);
    }

    private string? _linkLost;

    /// <summary>
    /// Die SSH-Verbindung unter einem Mosh-Terminal ist weg (<paramref name="text"/> sagt es) oder wieder da (<c>null</c>):
    /// Die Werte kommen von dort, also steht statt ihrer der Text – das Terminal bleibt online.
    /// </summary>
    public void SetLinkLost(string? text)
    {
        _linkLost = text;
        Refresh();
    }

    /// <summary>Zustand der Verbindung, wie ihn die Sitzung meldet.</summary>
    public void SetConnection(bool online, string statusText)
    {
        IsOnline = online;
        StatusText = statusText;
    }

    /// <summary>
    /// Ob die Werte gerade jemand sieht (aktiver Tab, Bereich eingeblendet). Nur dann wird gemessen – auf dem
    /// Server läuft dafür eine Schleife, die sonst niemandem nützt.
    /// </summary>
    public void SetWatched(bool watched)
    {
        if (_watched == watched)
        {
            return;
        }

        _watched = watched;
        _monitor?.SetActive(Wanted || _probing);
        Refresh();
    }

    private bool _alerting;

    /// <summary>Gemessen wird, wenn jemand hinsieht – oder wenn die Hinweise zu den Schwellen es verlangen.</summary>
    private bool Wanted => _watched || _alerting;

    /// <summary>
    /// Auch messen, wenn niemand hinsieht: für die Hinweise zu den Schwellen (<see cref="ShellViewModel.ServerAlerts"/>).
    /// </summary>
    public void SetAlerting(bool alerting)
    {
        if (_alerting != alerting)
        {
            _alerting = alerting;
            _monitor?.SetActive(Wanted || _probing);
        }
    }

    /// <summary>Was zu dieser Verbindung schon gemeldet wurde.</summary>
    internal AlertWatch Alerts { get; } = new();

    /// <summary>Eine neue Messung ist da – nicht bloß ein Auffrischen der Anzeige.</summary>
    public event Action<ServerStats>? Measured;

    private bool _probing;

    /// <summary>
    /// Misst einmal, auch wenn gerade niemand hinsieht – um zu erfahren, welches System der Server ist. Nach der
    /// ersten Antwort (oder der Auskunft, dass es keine gibt) ruht die Messung wieder, wenn sie niemand zeigt.
    /// </summary>
    public void Probe()
    {
        if (_monitor is { } monitor && !_probing && monitor.Current is null && !monitor.IsUnavailable)
        {
            _probing = true;
            monitor.SetActive(true);
        }
    }

    /// <summary>Hängt die Anzeige an die Überwachung einer (neuen) Verbindung; <c>null</c> = keine Werte.</summary>
    public void Attach(IServerMonitor? monitor)
    {
        if (_monitor is { } previous)
        {
            previous.Changed -= OnChanged;
            previous.SetActive(false);
        }

        _monitor = monitor;
        _probing = false;
        ClearHistory();
        Alerts.Reset();
        if (monitor is not null)
        {
            monitor.Changed += OnChanged;
            monitor.SetActive(Wanted);
        }

        Refresh();
    }

    public void Dispose() => Attach(null);

    // --- Kurve des Durchsatzes der Datenträger ---

    /// <summary>So viele Messungen zeigt die Kurve (bei einer Messung alle paar Sekunden: die letzten Minuten).</summary>
    public const int HistoryLength = 60;

    private readonly Queue<double> _diskIo = new();

    /// <summary>
    /// Durchsatz der Datenträger (gelesen + geschrieben) der letzten Messungen, je 0 bis 1, die älteste zuerst –
    /// gemessen am höchsten Wert im gezeigten Zeitraum, denn eine feste Obergrenze gibt es nicht. Bei jeder Messung
    /// eine neue Liste.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> DiskIoHistory { get; private set; } = [];

    /// <summary>
    /// Der Verlauf über die ganze Sitzung – auch über eine neue Verbindung hinweg. Gemessen wird weiter nur,
    /// solange jemand hinsieht.
    /// </summary>
    public LoadHistory History { get; } = new();

    private void Record(ServerStats stats)
    {
        History.Add(new LoadSample(
            DateTimeOffset.Now,
            stats.CpuPercent,
            stats.MemoryPercent,
            stats.MemoryUsed,
            stats.Memory?.SwapUsed ?? 0,
            LoadHistory.FirstLoad(stats.Load),
            stats.NetReceived,
            stats.NetSent));

        // Nennt der Server den Durchsatz der Datenträger nicht (mehr), gibt es auch keine Kurve.
        if (stats.Io is { } io)
        {
            Push(_diskIo, io.Read + io.Write);
            var most = Math.Max(_diskIo.Max(), 1);
            DiskIoHistory = [.. _diskIo.Select(value => Math.Clamp(value / most, 0, 1))];
        }
        else if (_diskIo.Count > 0)
        {
            _diskIo.Clear();
            DiskIoHistory = [];
        }

        static void Push(Queue<double> values, double value)
        {
            values.Enqueue(double.IsFinite(value) ? value : 0);
            while (values.Count > HistoryLength)
            {
                values.Dequeue();
            }
        }
    }

    // Eine neue Verbindung beginnt mit leerer Kurve: Die alten Werte gehören zu einer anderen Zeit.
    private void ClearHistory()
    {
        _diskIo.Clear();
        DiskIoHistory = [];
    }

    private void OnChanged()
    {
        var monitor = _monitor;
        _dispatcher.Post(() =>
        {
            // Die Meldung kann unterwegs gewesen sein, während die Überwachung schon gewechselt hat.
            if (ReferenceEquals(monitor, _monitor))
            {
                if (_probing && monitor is not null && (monitor.Current is not null || monitor.IsUnavailable))
                {
                    _probing = false;
                    monitor.SetActive(Wanted);
                }

                Refresh(sample: true);
            }
        });
    }

    private void Refresh(bool sample = false)
    {
        var unavailable = _linkLost is not null || _monitor?.IsUnavailable == true;
        var stats = unavailable ? null : _monitor?.Current;
        HasMetrics = stats is not null;
        IsUnavailable = unavailable;
        UnavailableText = _linkLost ?? (_monitor?.IsRetrying == true ? L.Get("InspectorSilent")
            : _monitor?.UnavailableDetail?.Contains("MaxSessions", StringComparison.Ordinal) == true ? L.Get("ExecNoChannel")
            : L.Get("InspectorUnavailable"));
        IsMeasuring = _monitor is not null && _watched && stats is null && !unavailable;
        if (stats is null)
        {
            return;
        }

        CpuPercent = stats.CpuPercent;
        MemoryPercent = stats.MemoryPercent;
        DiskPercent = stats.DiskPercent;
        CpuText = Format.Percent(stats.CpuPercent);
        MemoryText = Format.Usage(stats.MemoryPercent, stats.MemoryUsed, stats.MemoryTotal);
        DiskText = Format.Usage(stats.DiskPercent, stats.DiskUsed, stats.DiskTotal);
        OsName = stats.OsName;
        Kernel = stats.Kernel;
        UptimeText = Format.Uptime(stats.Uptime);
        LoadText = stats.Load;
        NetworkText = Format.Network(stats.NetReceived, stats.NetSent);
        MemoryShortText = stats.MemoryTotal > 0 ? Format.UsedOfTotal(stats.MemoryUsed, stats.MemoryTotal) : Format.Percent(stats.MemoryPercent);
        NetworkShortText = Format.NetworkShort(stats.NetReceived, stats.NetSent);
        DiskShortText = Format.Percent(stats.DiskPercent);
        UptimeSinceText = Format.UptimeSince(stats.Uptime);
        DiskIoText = stats.Io?.Text ?? string.Empty;
        SplitText = Describe(stats.Split);
        MemorySplitText = stats.Memory is { HasSplit: true } parts
            ? L.Get("MemorySplit", Format.FileSize(parts.Cache!.Value, isDirectory: false), Format.FileSize(parts.Free!.Value, isDirectory: false))
            : string.Empty;
        SwapText = stats.Memory is { HasSwap: true } swap
            ? L.Get("SwapUsage", Format.Usage(swap.SwapPercent, swap.SwapUsed, swap.SwapTotal))
            : string.Empty;
        MachineText = stats.Info?.Machine ?? string.Empty;
        ProcessorText = Processor(stats.Info);
        VirtualizationText = Virtualization(stats.Info);
        ClockText = Describe(stats.Clock);

        // Die Listen nur ersetzen, wenn sich etwas geändert hat: Sonst baut die Ansicht alle paar Sekunden alles neu.
        var cores = (stats.Cores ?? []).Select((percent, index) => new CoreLoad(index, Math.Round(percent))).ToList();
        if (!cores.SequenceEqual(Cores))
        {
            Cores = BindableList.Of(cores);
        }

        if (!(stats.Interfaces ?? []).SequenceEqual(Interfaces))
        {
            Interfaces = BindableList.Of(stats.Interfaces ?? []);
        }

        IReadOnlyList<DiskIoDevice> devices = stats.IoDevices is { Count: > 1 } several ? several : [];
        if (!devices.SequenceEqual(DiskIoDevices))
        {
            DiskIoDevices = BindableList.Of(devices);
        }

        if (!(stats.Disks ?? []).SequenceEqual(Disks))
        {
            Disks = BindableList.Of(stats.Disks ?? []);
        }
        // Nur eine neue Messung verlängert die Kurven – nicht jedes Auffrischen der Anzeige.
        if (sample)
        {
            Record(stats);
            Measured?.Invoke(stats);
        }
    }
}
