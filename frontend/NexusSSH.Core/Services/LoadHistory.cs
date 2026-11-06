using System.Globalization;
using System.Text;

namespace NexusSSH.Core.Services;

/// <summary>Eine Messung, wie der Verlauf sie sich merkt.</summary>
/// <param name="Cpu">Auslastung in Prozent.</param>
/// <param name="Memory">Belegter Arbeitsspeicher in Prozent.</param>
/// <param name="Load">Last über eine Minute; <c>null</c>, wenn der Server sie nicht nennt.</param>
/// <param name="NetReceived">Empfangene Bytes je Sekunde.</param>
public readonly record struct LoadSample(DateTimeOffset Time, double Cpu, double Memory, long MemoryUsed, long SwapUsed, double? Load, long NetReceived, long NetSent);

/// <summary>
/// Der Verlauf der Auslastung über eine ganze Sitzung – die kleinen Kurven im Inspector zeigen nur die letzte
/// Minute. Gemessen wird weiter nur, solange jemand hinsieht; dazwischen hat der Verlauf Lücken, und die werden als
/// Lücken gezeichnet, nicht überbrückt. Der Speicher bleibt begrenzt: Wird es zu viel, rücken die älteren
/// Messungen paarweise zusammen (das Mittel beider) – das Jüngste bleibt genau, das Alte wird gröber.
/// </summary>
public sealed class LoadHistory
{
    /// <summary>Mehr Messungen hält der Verlauf nicht; bei einer alle drei Sekunden sind das gut acht Stunden in voller Schärfe.</summary>
    public const int MaxSamples = 10_000;

    private readonly List<LoadSample> _samples = [];

    public IReadOnlyList<LoadSample> Samples => _samples;

    public int Count => _samples.Count;

    public void Add(LoadSample sample)
    {
        // Die Uhr des Rechners kann zurückspringen; der Verlauf bleibt trotzdem der Reihe nach.
        if (_samples.Count > 0 && sample.Time <= _samples[^1].Time)
        {
            sample = sample with { Time = _samples[^1].Time.AddMilliseconds(1) };
        }

        _samples.Add(sample);
        if (_samples.Count > MaxSamples)
        {
            Thin();
        }
    }

    public void Clear() => _samples.Clear();

    // Die ältere Hälfte auf die halbe Zahl bringen: je zwei Nachbarn werden eine Messung.
    private void Thin()
    {
        var half = _samples.Count / 2;
        var merged = new List<LoadSample>((half / 2) + 1);
        for (var index = 0; index + 1 < half; index += 2)
        {
            var (a, b) = (_samples[index], _samples[index + 1]);
            merged.Add(new LoadSample(
                a.Time + ((b.Time - a.Time) / 2),
                (a.Cpu + b.Cpu) / 2,
                (a.Memory + b.Memory) / 2,
                (a.MemoryUsed + b.MemoryUsed) / 2,
                (a.SwapUsed + b.SwapUsed) / 2,
                a.Load is { } first && b.Load is { } second ? (first + second) / 2 : a.Load ?? b.Load,
                (a.NetReceived + b.NetReceived) / 2,
                (a.NetSent + b.NetSent) / 2));
        }

        _samples.RemoveRange(0, half - (half % 2));
        _samples.InsertRange(0, merged);
    }

    /// <summary>Die Messungen eines Zeitraums bis <paramref name="now"/>; ohne Zeitraum alle.</summary>
    public IReadOnlyList<LoadSample> Within(TimeSpan? window, DateTimeOffset now)
    {
        if (window is not { } span)
        {
            return _samples;
        }

        var from = now - span;
        var start = _samples.FindIndex(sample => sample.Time >= from);
        return start < 0 ? [] : _samples.GetRange(start, _samples.Count - start);
    }

    /// <summary>
    /// Die Kurve eines Werts (0–100) als Linienzüge für eine Fläche der Größe <paramref name="width"/> ×
    /// <paramref name="height"/>: links der Anfang des Zeitraums, rechts <paramref name="now"/>. Wo länger nicht
    /// gemessen wurde, endet ein Zug und ein neuer beginnt. Mehr Punkte, als die Fläche Spalten hat, werden je
    /// Spalte zu ihrem höchsten Wert – Spitzen sollen nicht verschwinden.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Lines(TimeSpan? window, DateTimeOffset now, Func<LoadSample, double> value, double width, double height)
    {
        var samples = Within(window, now);
        var lines = new List<IReadOnlyList<(double X, double Y)>>();
        if (samples.Count == 0 || width <= 0 || height <= 0)
        {
            return lines;
        }

        var from = window is { } span ? now - span : samples[0].Time;
        var seconds = Math.Max((now - from).TotalSeconds, 1);
        var gap = GapOf(samples);
        var columns = Math.Max((int)width, 1);

        var line = new List<(double X, double Y)>();
        var (column, peak, last) = (-1, 0.0, samples[0].Time);
        void Flush()
        {
            if (column >= 0)
            {
                line.Add((column * width / columns, height - (Math.Clamp(peak, 0, 100) / 100 * height)));
            }
        }

        foreach (var sample in samples)
        {
            var at = (int)Math.Clamp((sample.Time - from).TotalSeconds / seconds * columns, 0, columns);
            if (sample.Time - last > gap)
            {
                Flush();
                if (line.Count > 0)
                {
                    lines.Add(line);
                }

                (line, column) = ([], -1);
            }

            if (at != column)
            {
                Flush();
                (column, peak) = (at, value(sample));
            }
            else
            {
                peak = Math.Max(peak, value(sample));
            }

            last = sample.Time;
        }

        Flush();
        if (line.Count > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    // Ab welchem Abstand zwei Messungen nicht mehr zusammengehören: deutlich mehr als üblich, mindestens 15 s.
    private static TimeSpan GapOf(IReadOnlyList<LoadSample> samples)
    {
        if (samples.Count < 3)
        {
            return TimeSpan.FromSeconds(15);
        }

        var steps = new List<double>(samples.Count - 1);
        for (var index = 1; index < samples.Count; index++)
        {
            steps.Add((samples[index].Time - samples[index - 1].Time).TotalSeconds);
        }

        steps.Sort();
        return TimeSpan.FromSeconds(Math.Max(steps[steps.Count / 2] * 4, 15));
    }

    /// <summary>
    /// Der Verlauf als CSV: eine Zeile je Messung, Zeit nach ISO 8601, Zahlen mit Punkt – so liest es jede
    /// Tabellenkalkulation und jedes Skript, gleich in welcher Sprache die App läuft.
    /// </summary>
    public string ToCsv()
    {
        var text = new StringBuilder("time,cpu_percent,memory_percent,memory_used_bytes,swap_used_bytes,load_1min,net_received_bytes_per_s,net_sent_bytes_per_s\r\n");
        var culture = CultureInfo.InvariantCulture;
        foreach (var sample in _samples)
        {
            text.Append(sample.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", culture)).Append(',')
                .Append(sample.Cpu.ToString("0.#", culture)).Append(',')
                .Append(sample.Memory.ToString("0.#", culture)).Append(',')
                .Append(sample.MemoryUsed.ToString(culture)).Append(',')
                .Append(sample.SwapUsed.ToString(culture)).Append(',')
                .Append(sample.Load?.ToString("0.##", culture) ?? string.Empty).Append(',')
                .Append(sample.NetReceived.ToString(culture)).Append(',')
                .Append(sample.NetSent.ToString(culture)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Die Last über eine Minute aus „0.68 0.57 0.49“; <c>null</c>, wenn dort keine Zahl steht.</summary>
    public static double? FirstLoad(string? load)
        => load?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
            && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
}

/// <summary>Der Name der CSV-Datei eines Verlaufs.</summary>
public static class LoadHistoryFile
{
    /// <summary>Vorschlag ohne Endung: der Name des Servers, soweit er als Dateiname taugt, und das Datum.</summary>
    public static string Name(string server, DateTimeOffset now)
    {
        var safe = new string([.. server.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(40)]).Trim('.');
        return $"{(safe.Length > 0 ? safe : "server")}-load-{now.ToLocalTime().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}";
    }
}
