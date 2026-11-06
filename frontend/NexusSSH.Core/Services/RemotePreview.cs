using System.Text;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>Was sich von einer Datei des Servers ansehen lässt, ohne ein anderes Programm zu starten.</summary>
public enum PreviewKind
{
    /// <summary>Nichts: zu groß, kein Text, oder die Datei ließ sich nicht holen.</summary>
    None,
    Text,
    Image,
}

/// <summary>
/// Der Inhalt für die Vorschau. <see cref="Text"/> ist reiner Text ohne Steuerzeichen (außer Zeilenumbruch und
/// Tabulator); <see cref="ImagePath"/> die geholte Kopie auf diesem Rechner. <see cref="Problem"/> nennt in der
/// Sprache der Oberfläche, warum es keine Vorschau gibt.
/// </summary>
public sealed record PreviewContent(string Name, PreviewKind Kind, string? Text = null, string? ImagePath = null, bool Truncated = false, string? Problem = null);

/// <summary>
/// Vorschau im Dateibrowser: holt eine Datei in einen Arbeitsordner und liest sie als Text oder nennt sie als
/// Bild. Der Inhalt kommt vom Server und ist fremd – er wird nur angezeigt, nie ausgeführt oder einem anderen
/// Programm übergeben; was kein Text und kein Bild in einem der bekannten Formate ist, zeigt die Vorschau nicht.
/// </summary>
public static class RemotePreview
{
    /// <summary>Größere Textdateien holt die Vorschau nicht.</summary>
    public const long MaxTextSize = 2L * 1024 * 1024;

    /// <summary>
    /// Mehr lädt ein Abruf für Vorschau, Betrachter oder Vergleich nie – über jeder Grenze, die vorher an der Liste
    /// geprüft wird, aber ein Ende: Die Liste nennt, was der Server behauptet.
    /// </summary>
    public const long FetchLimit = 64L * 1024 * 1024;

    /// <summary>Größere Bilder holt die Vorschau nicht.</summary>
    public const long MaxImageSize = 20L * 1024 * 1024;

    /// <summary>So viele Zeichen Text zeigt die Vorschau höchstens.</summary>
    public const int MaxShownCharacters = 200_000;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"];

    /// <summary>Wie die Datei gezeigt würde – nach Endung und Größe, noch ohne sie zu holen.</summary>
    public static PreviewKind KindOf(string name, long size)
    {
        var image = ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
        return (image, size) switch
        {
            (true, <= MaxImageSize) => PreviewKind.Image,
            (false, <= MaxTextSize) => PreviewKind.Text,
            _ => PreviewKind.None,
        };
    }

    /// <summary>
    /// Liest den Anfang einer geholten Datei als Text: UTF-8 (auch mit BOM), sonst die westeuropäische Kodierung.
    /// <c>null</c>, wenn es kein Text ist (ein Nullbyte unter den ersten Bytes) – eine Binärdatei als Zeichensalat
    /// hilft niemandem.
    /// </summary>
    public static (string Text, bool Truncated)? ReadText(byte[] bytes)
    {
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        var truncated = text.Length > MaxShownCharacters;
        var shown = truncated ? text[..MaxShownCharacters] : text;

        // Steuerzeichen (Escape-Sequenzen eines Protokolls etwa) und Zeichen, die die Schreibrichtung umkehren,
        // haben in der Anzeige nichts verloren; Zeilenumbruch und Tabulator bleiben.
        var clean = new StringBuilder(shown.Length);
        foreach (var line in shown.TrimStart('﻿').ReplaceLineEndings("\n").Split('\n'))
        {
            clean.Append(Format.Visible(line.Replace("\t", "    ", StringComparison.Ordinal))).Append('\n');
        }

        return (clean.ToString().TrimEnd('\n'), truncated);
    }

    /// <summary>
    /// Holt die Datei nach <paramref name="folder"/> und wartet auf das Ende des Downloads. Liefert den lokalen Pfad
    /// oder – als zweiten Wert – die Meldung des Transfers, wenn er scheiterte.
    /// </summary>
    public static async Task<(string? LocalPath, string? Failure)> FetchAsync(ITransferQueue queue, IRemoteFileSystem files, string remotePath, string folder)
    {
        // Wie die Datei hier heißt, legt das Backend fest; sie muss in ihrem Ordner liegen, nirgends sonst.
        if (files.LocalTarget(remotePath, folder) is not { } local || Path.GetDirectoryName(local) != folder)
        {
            return (null, "unsafe_name: ");
        }

        Directory.CreateDirectory(folder);
        var done = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? id = null;
        void Check()
        {
            if (id is not null && queue.Items.FirstOrDefault(item => item.Id == id) is { IsFinished: true } item)
            {
                done.TrySetResult(item);
            }
        }

        queue.Changed += Check;
        try
        {
            id = files.Download(remotePath, folder, FetchLimit);
            Check();
            var item = await done.Task;
            return item.State == TransferState.Completed ? (local, null) : (null, item.Detail ?? item.State.ToString());
        }
        finally
        {
            queue.Changed -= Check;
        }
    }
}
