using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Liest ein Farbschema aus einer Datei, wie andere Terminals sie schreiben: iTerm2 (<c>.itermcolors</c>, ein
/// Property-List-XML), Windows Terminal (ein JSON-Objekt aus dessen <c>schemes</c>) und Xresources
/// (<c>*.color0: #rrggbb</c>). Die Datei ist fremd: Übernommen werden nur Farben, die sich als <c>#rrggbb</c>
/// schreiben lassen – sie landen später in der Terminal-Seite –, und nur ein Schema, das Hintergrund, Vordergrund
/// und alle sechzehn Farben nennt.
/// </summary>
public static partial class ThemeImport
{
    /// <summary>Größer ist keine Datei mit einem Farbschema.</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>So lang darf der Name eines Schemas sein.</summary>
    public const int MaxName = 40;

    private static readonly string[] WindowsTerminalNames =
    [
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    /// <summary>
    /// Das Schema aus dem Text einer Datei; <c>null</c>, wenn es keines der drei Formate ist oder Farben fehlen.
    /// <paramref name="fallbackName"/> (der Dateiname) gilt, wenn die Datei selbst keinen Namen nennt.
    /// </summary>
    public static TerminalTheme? Parse(string? text, string? fallbackName)
    {
        if (text is not { Length: > 0 and <= MaxLength })
        {
            return null;
        }

        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        var colors = trimmed.StartsWith('<') ? ITerm(trimmed)
            : trimmed.StartsWith('{') || trimmed.StartsWith('[') ? WindowsTerminal(trimmed)
            : Xresources(trimmed);
        if (colors is not { } found || found.Background is null || found.Foreground is null || found.Ansi.Any(color => color is null))
        {
            return null;
        }

        var name = CleanName(found.Name) ?? CleanName(Path.GetFileNameWithoutExtension(fallbackName)) ?? "Imported";
        return new TerminalTheme(
            IdOf(name),
            name,
            found.Background,
            found.Foreground,
            found.Cursor ?? found.Foreground,
            found.Selection ?? Blend(found.Background, found.Foreground),
            [.. found.Ansi.Select(color => color!)]);
    }

    /// <summary>Die Kennung eines eigenen Schemas: <c>custom-</c> und der Name in Kleinbuchstaben, Ziffern und Bindestrichen.</summary>
    public static string IdOf(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return "custom-" + (slug.Length > 0 ? slug : "theme");
    }

    /// <summary>Ob ein Schema eines ist, das die Terminal-Seite bekommen darf: sechzehn Farben, alle als <c>#rrggbb</c>.</summary>
    public static bool IsValid(TerminalTheme? theme)
        => theme is { Ansi.Count: 16 }
            && theme.Id is { Length: > 7 and <= 64 } && theme.Id.StartsWith("custom-", StringComparison.Ordinal)
            && theme.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            && CleanName(theme.Name) == theme.Name
            && new[] { theme.Background, theme.Foreground, theme.Cursor, theme.Selection }.Concat(theme.Ansi).All(color => color is not null && HexColor().IsMatch(color));

    private sealed class Found
    {
        public string? Name { get; set; }

        public string? Background { get; set; }

        public string? Foreground { get; set; }

        public string? Cursor { get; set; }

        public string? Selection { get; set; }

        public string?[] Ansi { get; } = new string?[16];
    }

    // --- iTerm2: <plist><dict><key>Ansi 0 Color</key><dict><key>Red Component</key><real>0.1</real>…</dict>… ---

    private static Found? ITerm(string text)
    {
        XDocument document;
        try
        {
            // Keine DTD, keine Verweise nach außen: Die Datei ist fremd.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root?.Element("dict") is not { } dictionary)
        {
            return null;
        }

        var found = new Found();
        string? key = null;
        foreach (var element in dictionary.Elements())
        {
            if (element.Name.LocalName == "key")
            {
                key = element.Value.Trim();
                continue;
            }

            if (key is null || element.Name.LocalName != "dict" || ITermColor(element) is not { } color)
            {
                key = null;
                continue;
            }

            switch (key)
            {
                case "Background Color":
                    found.Background = color;
                    break;
                case "Foreground Color":
                    found.Foreground = color;
                    break;
                case "Cursor Color":
                    found.Cursor = color;
                    break;
                case "Selection Color":
                    found.Selection = color;
                    break;
                default:
                    if (ITermAnsi().Match(key) is { Success: true } match && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is >= 0 and < 16 and var index)
                    {
                        found.Ansi[index] = color;
                    }

                    break;
            }

            key = null;
        }

        return found;
    }

    private static string? ITermColor(XElement dictionary)
    {
        double? red = null, green = null, blue = null;
        string? key = null;
        foreach (var element in dictionary.Elements())
        {
            if (element.Name.LocalName == "key")
            {
                key = element.Value.Trim();
            }
            else if (key is not null && double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            {
                switch (key)
                {
                    case "Red Component":
                        red = value;
                        break;
                    case "Green Component":
                        green = value;
                        break;
                    case "Blue Component":
                        blue = value;
                        break;
                }

                key = null;
            }
        }

        return red is { } r && green is { } g && blue is { } b ? Hex(Channel(r), Channel(g), Channel(b)) : null;

        static int Channel(double share) => (int)Math.Round(Math.Clamp(share, 0, 1) * 255);
    }

    // --- Windows Terminal: {"name":"…","background":"#…","black":"#…",…} oder {"schemes":[…]} bzw. eine Liste davon ---

    private static Found? WindowsTerminal(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
            var scheme = document.RootElement;
            if (scheme.ValueKind == JsonValueKind.Object && scheme.TryGetProperty("schemes", out var schemes))
            {
                scheme = schemes;
            }

            if (scheme.ValueKind == JsonValueKind.Array)
            {
                scheme = scheme.EnumerateArray().FirstOrDefault(entry => entry.ValueKind == JsonValueKind.Object);
            }

            if (scheme.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var found = new Found
            {
                Name = Text(scheme, "name"),
                Background = Color(Text(scheme, "background")),
                Foreground = Color(Text(scheme, "foreground")),
                Cursor = Color(Text(scheme, "cursorColor")),
                Selection = Color(Text(scheme, "selectionBackground")),
            };
            for (var index = 0; index < WindowsTerminalNames.Length; index++)
            {
                found.Ansi[index] = Color(Text(scheme, WindowsTerminalNames[index]));
            }

            return found;
        }
        // Auch ArgumentException: Die wirft der JSON-Leser bei einem halben UTF-16-Zeichen im Text (gefunden von den
        // Zufallstests) – für den Aufrufer ist das dieselbe unlesbare Eingabe.
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }

        static string? Text(JsonElement scheme, string name)
            => scheme.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // --- Xresources: „*.color4: #268bd2“, „URxvt*background: #002b36“, „! Kommentar“ ---

    private static Found? Xresources(string text)
    {
        var found = new Found();
        var any = false;
        foreach (var line in text.Split('\n'))
        {
            if (XresourcesLine().Match(line) is not { Success: true } match || Color(match.Groups[2].Value.Trim()) is not { } color)
            {
                continue;
            }

            any = true;
            var name = match.Groups[1].Value.ToLowerInvariant();
            switch (name)
            {
                case "background":
                    found.Background = color;
                    break;
                case "foreground":
                    found.Foreground = color;
                    break;
                case "cursorcolor":
                    found.Cursor = color;
                    break;
                default:
                    if (name.StartsWith("color", StringComparison.Ordinal) && int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index is >= 0 and < 16)
                    {
                        found.Ansi[index] = color;
                    }

                    break;
            }
        }

        return any ? found : null;
    }

    // --- Farben und Namen ---

    /// <summary>Eine Farbe als <c>#rrggbb</c> in Kleinbuchstaben; auch <c>#rgb</c> und <c>rgb:rr/gg/bb</c> werden gelesen. Sonst <c>null</c>.</summary>
    public static string? Color(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (HexColor().IsMatch(value))
        {
            return value.ToLowerInvariant();
        }

        if (ShortHex().Match(value) is { Success: true } brief)
        {
            var digits = brief.Groups[1].Value.ToLowerInvariant();
            return $"#{digits[0]}{digits[0]}{digits[1]}{digits[1]}{digits[2]}{digits[2]}";
        }

        return XColor().Match(value) is { Success: true } x
            ? $"#{x.Groups[1].Value}{x.Groups[2].Value}{x.Groups[3].Value}".ToLowerInvariant()
            : null;
    }

    /// <summary>Ob zwei Schemata in allem gleich sind – die Liste der Farben zählt nach ihrem Inhalt.</summary>
    public static bool Same(TerminalTheme a, TerminalTheme b)
        => a.Id == b.Id && a.Name == b.Name && a.Background == b.Background && a.Foreground == b.Foreground
            && a.Cursor == b.Cursor && a.Selection == b.Selection && a.Ansi.SequenceEqual(b.Ansi);

    /// <summary>
    /// Der Name eines Schemas, wie er gespeichert wird: ohne Steuerzeichen, Anführungs- und spitze Klammern, gekürzt;
    /// <c>null</c>, wenn nichts übrig bleibt.
    /// </summary>
    public static string? CleanName(string? name)
    {
        var clean = new string([.. (name ?? string.Empty).Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>'))]).Trim();
        if (clean.Length > MaxName)
        {
            clean = clean[..MaxName].TrimEnd();
        }

        return clean.Length > 0 ? clean : null;
    }

    // Für eine Markierung, die die Datei nicht nennt: ein Drittel des Wegs vom Hintergrund zur Schrift.
    private static string Blend(string background, string foreground)
    {
        int Channel(int offset)
        {
            var from = Convert.ToInt32(background.Substring(offset, 2), 16);
            var to = Convert.ToInt32(foreground.Substring(offset, 2), 16);
            return from + ((to - from) / 3);
        }

        return Hex(Channel(1), Channel(3), Channel(5));
    }

    private static string Hex(int red, int green, int blue) => $"#{red:x2}{green:x2}{blue:x2}";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^#([0-9a-fA-F]{3})$")]
    private static partial Regex ShortHex();

    [GeneratedRegex("^rgb:([0-9a-fA-F]{2})/([0-9a-fA-F]{2})/([0-9a-fA-F]{2})$")]
    private static partial Regex XColor();

    [GeneratedRegex(@"^Ansi (\d{1,2}) Color$")]
    private static partial Regex ITermAnsi();

    // „[irgendwas][*.]name: wert“ – der Name ist das letzte Wort vor dem Doppelpunkt; „!“ und „#“ am Anfang sind Kommentare.
    [GeneratedRegex(@"^\s*(?![!#])[\w.*?-]*?([A-Za-z]+\d{0,2})\s*:\s*(\S+)\s*$")]
    private static partial Regex XresourcesLine();
}
