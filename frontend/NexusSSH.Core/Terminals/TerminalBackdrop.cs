namespace NexusSSH.Core.Terminals;

/// <summary>
/// Ein Bild hinter dem Terminal – abschaltbar, von Haus aus aus (dann bleibt der Hintergrund die Farbe des
/// Farbschemas). Die Terminal-Seite lädt nichts von der Platte und nichts aus dem Netz: Die App liest die Datei und
/// gibt der Seite das Bild als <c>data:</c>-Adresse. Angenommen wird nur, was am Anfang wie ein PNG, JPEG, WebP oder
/// BMP aussieht, und nur bis zu einer Größe, die sich noch als Nachricht schicken lässt.
/// </summary>
public static class TerminalBackdrop
{
    /// <summary>Größer darf die Bilddatei nicht sein.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>So weit lässt sich das Bild abdunkeln, in Prozent – darüber wäre es nicht mehr zu sehen.</summary>
    public const int MaxDim = 95;

    /// <summary>Die Abdunklung, wenn nichts eingestellt ist: Text muss lesbar bleiben.</summary>
    public const int DefaultDim = 70;

    public static int Dim(int percent) => Math.Clamp(percent, 0, MaxDim);

    /// <summary>
    /// Das Bild als <c>data:</c>-Adresse für die Terminal-Seite; <c>null</c>, wenn es keines der vier Formate ist,
    /// leer oder zu groß.
    /// </summary>
    public static string? DataUrl(ReadOnlySpan<byte> bytes)
        => bytes.Length is > 0 and <= MaxBytes && Kind(bytes) is { } kind
            ? $"data:image/{kind};base64,{Convert.ToBase64String(bytes)}"
            : null;

    /// <summary>Woran die Datei am Anfang zu erkennen ist – der Name der Datei zählt nicht.</summary>
    public static string? Kind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return "jpeg";
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return bytes.Length >= 14 && bytes.StartsWith("BM"u8) ? "bmp" : null;
    }
}
