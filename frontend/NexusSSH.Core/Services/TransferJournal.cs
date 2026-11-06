using System.Globalization;
using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Woher ein Transfer liest und wohin er schreibt – Quelle als Pfad, Ziel als Ordner.</summary>
public sealed record TransferPaths(string Source, string Target);

/// <summary>
/// Das Transfer-Protokoll: eine CSV-Datei, in der jeder beendete Transfer eine Zeile bekommt – wann, in welche
/// Richtung, mit welchem Server, was, woher, wohin, wie viel und mit welchem Ergebnis. Zum Nachweisen, deshalb nur
/// angehängt, nie umgeschrieben; nur wenn der Benutzer es eingeschaltet hat. Über <see cref="MaxBytes"/> wandert
/// die Datei unter einen zweiten Namen, und eine neue beginnt.
///
/// Namen, Pfade und Fehlertexte kommen vom Server und landen in einem Tabellenprogramm: Jedes Feld steht ohne
/// Steuerzeichen in Anführungszeichen, und was dort als Formel gälte (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c> am
/// Anfang), bekommt einen Apostroph davor.
/// </summary>
public sealed class TransferJournal : IDisposable
{
    /// <summary>Ab dieser Größe beginnt eine neue Datei; die alte bleibt als eine einzige Vorgängerin liegen.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    public const string Header = "time,direction,server,name,source,target,bytes,result,detail";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _noted = [];
    private readonly ITransferQueue _queue;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly char _separator;
    private bool _failed;

    /// <param name="separator">
    /// Das Zeichen zwischen den Feldern; ohne Angabe das Listentrennzeichen dieses Rechners (<see cref="LocalSeparator"/>).
    /// </param>
    public TransferJournal(ITransferQueue queue, string file, Func<bool> enabled, TimeProvider? time = null, char? separator = null)
    {
        (_queue, File, _enabled, _time) = (queue, file, enabled, time ?? TimeProvider.System);
        _separator = separator ?? LocalSeparator(CultureInfo.CurrentCulture);

        // Was beim Start schon beendet in der Liste steht (vom letzten Lauf), ist nicht jetzt zu Ende gegangen.
        _noted.UnionWith(queue.Items.Where(item => item.IsFinished).Select(item => item.Id));
        queue.Changed += OnChanged;
    }

    /// <summary>Das Protokoll ließ sich nicht schreiben (einmal je Störung): die Meldung des Systems. Beliebiger Thread.</summary>
    public event Action<string>? Failed;

    public string File { get; }

    /// <summary>Die Datei, in die ein volles Protokoll wandert.</summary>
    public string Previous => Path.ChangeExtension(File, ".1" + Path.GetExtension(File));

    public void Dispose() => _queue.Changed -= OnChanged;

    /// <summary>
    /// Das Zeichen, an dem ein Tabellenprogramm dieses Rechners eine CSV-Datei beim Öffnen in Spalten teilt: das
    /// Listentrennzeichen der Region – in Deutschland das Semikolon. Mit Kommas stünde dort jede Zeile in einer
    /// einzigen Zelle (geprüft mit Excel, 2026-10-11). Eine Zeile <c>sep=,</c> am Anfang hilft nicht: Excel liest
    /// die Datei dann nicht mehr als UTF-8, und die Umlaute sind dahin. Alles andere als ein Semikolon gilt als Komma.
    /// </summary>
    public static char LocalSeparator(CultureInfo culture) => culture.TextInfo.ListSeparator == ";" ? ';' : ',';

    /// <summary>Die Kopfzeile mit diesem Trennzeichen.</summary>
    public static string HeaderWith(char separator) => Header.Replace(',', separator);

    // Eine Datei, die es schon gibt, behält ihr Trennzeichen – auch wenn der Rechner inzwischen anders eingestellt ist.
    private char SeparatorOf(FileInfo existing)
    {
        if (!existing.Exists || existing.Length == 0)
        {
            return _separator;
        }

        using var reader = new StreamReader(existing.FullName, Encoding.UTF8);
        var first = reader.ReadLine() ?? string.Empty;
        return first.StartsWith("time;", StringComparison.Ordinal) ? ';' : first.StartsWith("time,", StringComparison.Ordinal) ? ',' : _separator;
    }

    /// <summary>Das Ergebnis als festes Wort – die Datei soll sich auch von einem Programm lesen lassen.</summary>
    internal static string Result(TransferItem item) => item.State switch
    {
        TransferState.Completed => "completed",
        TransferState.Cancelled => "cancelled",
        _ when item.Detail?.StartsWith(ITransferQueue.PausedDetail, StringComparison.Ordinal) == true => "paused",
        _ when item.Detail?.StartsWith("interrupted:", StringComparison.Ordinal) == true => "interrupted",
        _ => "failed",
    };

    /// <summary>Ein Feld der Zeile: ohne Steuerzeichen, nie eine Formel, in Anführungszeichen.</summary>
    internal static string Field(string? text)
    {
        var clean = Format.Visible(text ?? string.Empty).Replace('\t', ' ');
        if (clean.TrimStart() is [('=' or '+' or '-' or '@'), ..])
        {
            clean = "'" + clean;
        }

        return "\"" + clean.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static string Line(DateTimeOffset when, TransferItem item, TransferPaths? paths, char separator = ',')
    {
        var detail = item.State == TransferState.Failed ? item.Detail?.Trim().TrimEnd(':') : null;
        return string.Join(
            separator,
            when.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            item.Direction == TransferDirection.Upload ? "upload" : "download",
            Field(item.Server),
            Field(item.FileName),
            Field(paths?.Source),
            Field(paths?.Target),
            item.Bytes.ToString(CultureInfo.InvariantCulture),
            Result(item),
            Field(detail));
    }

    // Beliebiger Thread: Was gerade zu Ende ging, bekommt seine Zeile – einmal je Ende, auch nach „Wiederholen“.
    private void OnChanged()
    {
        var items = _queue.Items;
        lock (_gate)
        {
            _noted.RemoveWhere(id => !items.Any(item => item.Id == id && item.IsFinished));
            var ended = items.Where(item => item.IsFinished && _noted.Add(item.Id)).ToList();
            if (ended.Count == 0 || !_enabled())
            {
                return;
            }

            Append(_time.GetLocalNow(), ended);
        }
    }

    private void Append(DateTimeOffset now, List<TransferItem> ended)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var existing = new FileInfo(File);
            if (existing.Exists && existing.Length >= MaxBytes)
            {
                System.IO.File.Move(File, Previous, overwrite: true);
                existing.Refresh();
            }

            // Mit BOM und Kopfzeile, damit ein Tabellenprogramm Umlaute und Spalten gleich richtig liest.
            var separator = SeparatorOf(existing);
            var text = new StringBuilder();
            if (!existing.Exists || existing.Length == 0)
            {
                text.Append('﻿').Append(HeaderWith(separator)).Append("\r\n");
            }

            foreach (var item in ended)
            {
                text.Append(Line(now, item, _queue.PathsOf(item.Id), separator)).Append("\r\n");
            }

            System.IO.File.AppendAllText(File, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _failed = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Jede Störung einmal melden, nicht bei jedem Transfer.
            if (!_failed)
            {
                _failed = true;
                Failed?.Invoke(error.Message);
            }
        }
    }
}
