using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NexusSSH.Core.Mcp;

/// <summary>Ein Werkzeug hat etwas nicht getan; der Text geht als Antwort an die KI.</summary>
public sealed class McpRefusedException(string message) : Exception(message);

/// <summary>
/// Antworten der Werkzeuge sind JSON: So lassen sich Felder nicht durch Ausgabe eines Servers vortäuschen, wie es in
/// freiem Text ginge. Geschrieben ohne Reflexion (Native AOT).
/// <para>
/// Die Grenze <see cref="McpTools.MaxAnswerLength"/> gilt für die geschriebenen Bytes. Was wachsen kann (Ausgabe,
/// Dateien, Listen), kürzt das Werkzeug deshalb erst, wenn es weiß, wie viel es geschrieben braucht:
/// <see cref="Middle"/>, <see cref="Start"/>, <see cref="Fitting{T}"/>; Platz ist, was <see cref="Measure"/> für die
/// Antwort ohne diesen Teil übrig lässt. <see cref="Write"/> ist die Sperre dahinter: Größer geht nichts hinaus.
/// </para>
/// </summary>
internal static class McpJson
{
    /// <summary>
    /// Wie die Texte auf dem Weg zum MCP-Programm geschrieben werden – Antworten, Pipe (<see cref="McpPipe"/>) und die
    /// Zeilen auf stdout (<see cref="McpServer"/>). Maskiert wird nur, was JSON verlangt: <c>"</c> als <c>\"</c>,
    /// <c>\</c> und alle Steuerzeichen (auch Zeilenumbrüche – jede Nachricht bleibt eine Zeile); ungültige
    /// Ersatzzeichen werden zu <c>�</c>. Der Standard-Encoder maskierte dazu, was in HTML oder einem Skript
    /// gefährlich wäre (<c>&lt; &gt; &amp; ' " +</c>, je sechs Zeichen wie <c>"</c>), und jedes Zeichen außerhalb
    /// von ASCII. Das schützt nur Text, der ungeprüft in eine Webseite eingebettet wird; diese Texte liest
    /// ausschließlich ein JSON-Parser (das MCP-Programm, die App über die Pipe), dessen Ergebnis ist Zeichen für Zeichen
    /// dasselbe. Sicherer würde hier nichts, nur größer: Eine Ausgabe voller Anführungszeichen wuchs auf das Sechsfache.
    /// Was der Text sagt, bleibt fremd und ist vorher gesäubert (<c>Format.VisibleText</c>); wer ihn in HTML zeigt, muss
    /// ihn dort maskieren.
    /// </summary>
    public static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static McpResult Write(Action<Utf8JsonWriter> content)
    {
        var answer = Encode(content);
        return answer.Length <= McpTools.MaxAnswerLength
            ? new McpResult(Encoding.UTF8.GetString(answer))
            : new McpResult($"the answer would be longer than {McpTools.MaxAnswerLength} bytes; ask for less", IsError: true);
    }

    /// <summary>So viele Bytes ergäbe <paramref name="content"/> als Antwort.</summary>
    public static int Measure(Action<Utf8JsonWriter> content) => Encode(content).Length;

    /// <summary>Ein Objekt mit <paramref name="content"/> darin, so geschrieben, wie es hinausgeht.</summary>
    public static byte[] Encode(Action<Utf8JsonWriter> content) => Value(writer =>
    {
        writer.WriteStartObject();
        content(writer);
        writer.WriteEndObject();
    });

    /// <summary>So viele Bytes braucht <paramref name="text"/> geschrieben als Wert, ohne die Anführungszeichen.</summary>
    public static int Size(string text) => Value(writer => writer.WriteStringValue(text)).Length - 2;

    /// <summary>
    /// Der Text, gekürzt auf höchstens <paramref name="room"/> Bytes geschrieben: Anfang und Ende bleiben (ein Drittel zu
    /// zwei Dritteln wie bei einem Befehl, <c>TerminalCommand</c>), dazwischen steht, wie viel fehlt – beim Suchen eines
    /// Fehlers steht das Wichtige am Schluss. Getrennt wird nie ein Ersatzpaar. <paramref name="cut"/>: ob gekürzt wurde.
    /// </summary>
    public static string Middle(string text, int room, out bool cut)
    {
        cut = Size(text) > room;
        if (!cut)
        {
            return text;
        }

        string Kept(int count)
        {
            var head = count / 3;
            var tail = text.Length - (count - head);
            if (head > 0 && char.IsHighSurrogate(text[head - 1]))
            {
                head--;
            }

            if (tail < text.Length && char.IsLowSurrogate(text[tail]))
            {
                tail++;
            }

            return string.Concat(text.AsSpan(0, head), $"\n[... {tail - head} characters left out ...]\n", text.AsSpan(tail));
        }

        // Die meisten Zeichen, die passen; null Zeichen (nur der Vermerk) gilt als Anfang, passt er nicht, bleibt nichts.
        var (low, high) = (0, text.Length - 1);
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Size(Kept(middle)) <= room)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var kept = Kept(low);
        return Size(kept) <= room ? kept : string.Empty;
    }

    /// <summary>
    /// Wie viel vom Anfang von <paramref name="data"/> (Bytes vom Server, UTF-8) als gesäuberter Text
    /// (<c>Format.VisibleText</c>) in <paramref name="room"/> Bytes passt: die Länge in Bytes – ab dort liest der nächste
    /// Aufruf weiter – und der Text dazu. Geschnitten wird zwischen zwei Zeichen.
    /// </summary>
    public static (int Length, string Text) Start(byte[] data, int room)
    {
        string TextOf(int length) => Format.VisibleText(Encoding.UTF8.GetString(data, 0, length));

        // Nicht mitten in einem Zeichen (höchstens drei Folgebytes; ungültige Bytes werden ohnehin zu U+FFFD).
        int Boundary(int length)
        {
            for (var step = 0; step < 3 && length > 0 && length < data.Length && (data[length] & 0xC0) == 0x80; step++)
            {
                length--;
            }

            return length;
        }

        var whole = TextOf(data.Length);
        if (Size(whole) <= room)
        {
            return (data.Length, whole);
        }

        var (low, high) = (0, data.Length - 1);
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Size(TextOf(Boundary(middle))) <= room)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var length = Boundary(low);
        return (length, TextOf(length));
    }

    /// <summary>
    /// Die Einträge, die geschrieben samt Kommas in <paramref name="room"/> Bytes passen – vom Anfang der Liste an, mit
    /// <paramref name="newest"/> vom Ende her –, in der Reihenfolge der Liste. Der erste, der nicht passt, beendet die
    /// Auswahl: Es fehlt nie etwas mittendrin. Geschrieben werden sie mit <see cref="WriteArray"/>.
    /// </summary>
    public static List<byte[]> Fitting<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write, int room, bool newest = false)
    {
        List<byte[]> fitting = [];
        foreach (var item in newest ? Enumerable.Reverse(items) : items)
        {
            var value = Value(writer => write(writer, item));
            room -= value.Length + (fitting.Count > 0 ? 1 : 0);
            if (room < 0)
            {
                break;
            }

            fitting.Add(value);
        }

        if (newest)
        {
            fitting.Reverse();
        }

        return fitting;
    }

    /// <summary>Schreibt Einträge, die <see cref="Fitting{T}"/> ausgewählt hat, als Liste.</summary>
    public static void WriteArray(Utf8JsonWriter writer, string name, IEnumerable<byte[]> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteRawValue(value, skipInputValidation: true);
        }

        writer.WriteEndArray();
    }

    private static byte[] Value(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>Die Argumente eines Aufrufs. Fremde Eingabe: Was nicht passt, lehnt der Aufruf ab, statt es zu deuten.</summary>
internal sealed class McpArguments
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public McpArguments(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new McpRefusedException("the arguments are not JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new McpRefusedException("the arguments are not an object");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                _values[property.Name] = property.Value.Clone();
            }
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Ein Text, oder <c>null</c>, wenn er fehlt.</summary>
    public string? Text(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new McpRefusedException($"{name} must be a string");
    }

    public string Required(string name) => Text(name) ?? throw new McpRefusedException($"{name} is missing");

    /// <summary>Ein Pfad: absolut wie beim Dateidienst des Servers, und ohne Steuerzeichen.</summary>
    public string Path()
    {
        var path = Required("path");
        return path.StartsWith('/') && !path.Any(char.IsControl) ? path : throw new McpRefusedException("path must be absolute");
    }

    public int Integer(string name, int fallback, int minimum, int maximum)
        => (int)Long(name, fallback, minimum, maximum);

    public long Long(string name, long fallback, long minimum, long maximum)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum && number <= maximum
            ? number
            : throw new McpRefusedException($"{name} must be a whole number from {minimum} to {maximum}");
    }
}
