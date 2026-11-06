using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NexusSSH.Core.Mcp;

/// <summary>Ein Werkzeug hat etwas nicht getan; der Text geht als Antwort an die KI.</summary>
public sealed class McpRefusedException(string message) : Exception(message);

/// <summary>
/// Antworten der Werkzeuge sind JSON: So lassen sich Felder nicht durch Ausgabe eines Servers vortäuschen, wie es in
/// freiem Text ginge. Geschrieben ohne Reflexion (Native AOT).
/// </summary>
internal static class McpJson
{
    public static McpResult Write(Action<Utf8JsonWriter> content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            content(writer);
            writer.WriteEndObject();
        }

        return new McpResult(Encoding.UTF8.GetString(buffer.WrittenSpan));
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
