using System.Text;
using System.Text.Json;
using NexusSSH.Core.Mcp;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Antworten an ein MCP-Programm: Die Grenze gilt für die geschriebenen Bytes, nicht für den Text davor, und das
/// Maskieren bläht gewöhnliche Zeichen nicht auf. Gefunden 2026-11-01: <c>seq 1 20000</c> ergab 58 KB statt 48 KiB,
/// und jedes <c>"</c> wurde zu <c>"</c>.
/// </summary>
public sealed class McpJsonTests
{
    // Was sich beim Schreiben verlängert: Anführungszeichen und Rückstriche (zwei Bytes), Zeilen und Tabulatoren (zwei),
    // Steuerzeichen (sechs), dazu Zeichen außerhalb von ASCII (zwei bis vier Bytes) und Ersatzpaare.
    public static TheoryData<string> Texts => new()
    {
        new string('"', 70_000),
        string.Concat(Enumerable.Repeat("\\\"\n\t", 20_000)),
        string.Concat(Enumerable.Range(0, 40_000).Select(i => (char)(i % 0x20))),
        string.Concat(Enumerable.Repeat("äöü€中😀", 12_000)),
        string.Join('\n', Enumerable.Range(1, 20_000)) + "\n",
        string.Concat(Enumerable.Repeat("a\"ü\u0001😀\\", 15_000)),
    };

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public void Quotes_and_characters_beyond_ascii_are_written_as_they_are()
    {
        var answer = McpJson.Write(writer => writer.WriteString("text", "say \"hi\" <b>&'+ äü€中 a\\b\n\t\u0001"));

        Assert.Equal("{\"text\":\"say \\\"hi\\\" <b>&'+ äü€中 a\\\\b\\n\\t\\u0001\"}", answer.Text);
        Assert.Equal("say \"hi\" <b>&'+ äü€中 a\\b\n\t\u0001", JsonDocument.Parse(answer.Text).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Every_message_stays_one_line()
    {
        var text = string.Concat("a\nb\rc", (char)0x2028, "d", (char)0x2029, "e", (char)0x85, "f");
        var answer = McpJson.Write(writer => writer.WriteString("text", text));

        Assert.DoesNotContain('\n', answer.Text);
        Assert.DoesNotContain('\r', answer.Text);
        Assert.Equal(text, JsonDocument.Parse(answer.Text).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void A_broken_surrogate_is_replaced_not_passed_on()
    {
        var answer = McpJson.Write(writer => writer.WriteString("text", "a\ud800b"));

        Assert.Equal("a�b", JsonDocument.Parse(answer.Text).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void An_answer_over_the_limit_never_goes_out()
    {
        var answer = McpJson.Write(writer => writer.WriteString("text", new string('x', McpTools.MaxAnswerLength)));

        Assert.True(answer.IsError);
        Assert.True(Encoding.UTF8.GetByteCount(answer.Text) <= McpTools.MaxAnswerLength);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void The_middle_falls_out_until_the_written_text_fits(string text)
    {
        foreach (var room in new[] { 0, 10, 100, 1000, 30_000, McpTools.MaxAnswerLength })
        {
            var kept = McpJson.Middle(text, room, out var cut);

            Assert.True(cut);
            Assert.True(Written(kept) <= room, $"{Written(kept)} > {room}");
            Strict.GetByteCount(kept);
            if (kept.Length > 0)
            {
                var marker = kept.IndexOf("\n[... ", StringComparison.Ordinal);
                Assert.StartsWith(kept[..marker], text, StringComparison.Ordinal);
                Assert.EndsWith(kept[(kept.IndexOf(" left out ...]\n", StringComparison.Ordinal) + " left out ...]\n".Length)..], text, StringComparison.Ordinal);
            }
        }

        // Nah an der Grenze bleibt fast alles: Das Kürzen nimmt nicht mehr weg als nötig.
        var large = McpJson.Middle(text, 30_000, out _);
        Assert.True(Written(large) > 29_900);
    }

    [Fact]
    public void A_text_that_fits_stays_as_it_is()
    {
        var text = "line \"one\"\n😀";

        Assert.Same(text, McpJson.Middle(text, Written(text), out var cut));
        Assert.False(cut);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void The_start_of_a_file_is_cut_between_characters_and_says_how_many_bytes_it_took(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        const int Room = 20_000;

        var (length, shown) = McpJson.Start(data, Room);

        Assert.InRange(length, 1, data.Length - 1);
        Assert.True(Written(shown) <= Room, $"{Written(shown)} > {Room}");
        Assert.True(Written(shown) > Room - 16);
        Assert.NotEqual(0x80, data[length] & 0xC0);
        Assert.Equal(Format.VisibleText(Strict.GetString(data, 0, length)), shown);
    }

    [Fact]
    public void Entries_are_taken_while_they_fit_newest_last()
    {
        var items = Enumerable.Range(0, 1000).Select(i => $"\"{i}\" äü\n").ToList();
        static void Write(Utf8JsonWriter writer, string item) => writer.WriteStringValue(item);

        var oldest = McpJson.Fitting(items, Write, 5000);
        var newest = McpJson.Fitting(items, Write, 5000, newest: true);
        var answer = McpJson.Encode(writer => McpJson.WriteArray(writer, "items", newest));

        Assert.True(oldest.Sum(value => value.Length) + oldest.Count - 1 <= 5000);
        Assert.Equal("\"0\" äü\n", JsonDocument.Parse(oldest[0]).RootElement.GetString());
        Assert.Equal("\"999\" äü\n", JsonDocument.Parse(newest[^1]).RootElement.GetString());
        Assert.True(answer.Length <= 5000 + "{\"items\":[]}".Length);
        var parsed = JsonDocument.Parse(answer).RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(items.TakeLast(parsed.Count), parsed);
    }

    // So viele Bytes stehen geschrieben zwischen den Anführungszeichen – gezählt an dem, was hinausginge.
    private static int Written(string text) => McpJson.Encode(writer => writer.WriteString("t", text)).Length - "{\"t\":\"\"}".Length;
}
