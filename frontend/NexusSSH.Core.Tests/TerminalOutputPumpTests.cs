using System.Text;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

/// <summary>Flusskontrolle zwischen Sitzung und Terminal (<c>docs/architecture.md</c> §10).</summary>
public sealed class TerminalOutputPumpTests
{
    private readonly List<(string Id, byte[] Data)> _posted = [];
    private readonly List<(string Id, bool Paused)> _flow = [];
    private readonly Queue<Action> _scheduled = new();
    private readonly TerminalOutputPump _pump;

    public TerminalOutputPumpTests()
    {
        _pump = new TerminalOutputPump(
            (id, data) => _posted.Add((id, data.ToArray())),
            _scheduled.Enqueue,
            (id, paused) => _flow.Add((id, paused)));
    }

    // Der „UI-Thread“ des Tests: alles ausführen, was sich angesammelt hat.
    private void RunUi()
    {
        while (_scheduled.TryDequeue(out var action))
        {
            action();
        }
    }

    private void Add(string id, string text) => _pump.Add(id, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Blocks_between_two_ui_turns_become_one_message_per_session()
    {
        Add("a", "one ");
        Add("b", "other");
        Add("a", "two");

        Assert.Empty(_posted);
        Assert.Single(_scheduled);

        RunUi();

        Assert.Equal(2, _posted.Count);
        Assert.Equal("one two", Encoding.UTF8.GetString(_posted.Single(block => block.Id == "a").Data));
        Assert.Equal("other", Encoding.UTF8.GetString(_posted.Single(block => block.Id == "b").Data));
        Assert.Empty(_flow);
    }

    [Fact]
    public void Output_after_a_flush_is_scheduled_again()
    {
        Add("a", "1");
        RunUi();
        Add("a", "2");
        RunUi();

        Assert.Equal(["1", "2"], _posted.Select(block => Encoding.UTF8.GetString(block.Data)));
    }

    [Fact]
    public void A_large_block_is_cut_into_chunks_without_losing_a_byte()
    {
        var data = new byte[(TerminalOutputPump.MaxChunk * 2) + 10];
        Random.Shared.NextBytes(data);

        _pump.Add("a", data);
        RunUi();

        Assert.Equal(3, _posted.Count);
        Assert.All(_posted, block => Assert.True(block.Data.Length <= TerminalOutputPump.MaxChunk));
        Assert.Equal(data, _posted.SelectMany(block => block.Data).ToArray());
    }

    [Fact]
    public void A_session_is_paused_when_the_terminal_falls_behind_and_resumed_when_it_caught_up()
    {
        _pump.Add("a", new byte[TerminalOutputPump.HighWater]);
        RunUi();
        Assert.Empty(_flow);

        _pump.Add("a", new byte[1]);
        RunUi();
        Assert.Equal([("a", true)], _flow);

        // Mehr Ausgabe hält nicht noch einmal an.
        _pump.Add("a", new byte[100]);
        RunUi();
        Assert.Single(_flow);

        // Erst unter der unteren Marke geht es weiter – nicht schon bei der ersten Bestätigung.
        _pump.Acknowledge("a", 101);
        Assert.Single(_flow);
        _pump.Acknowledge("a", TerminalOutputPump.HighWater - TerminalOutputPump.LowWater);
        Assert.Single(_flow);
        _pump.Acknowledge("a", 1);
        Assert.Equal([("a", true), ("a", false)], _flow);

        // … und nur einmal.
        _pump.Acknowledge("a", 1000);
        Assert.Equal(2, _flow.Count);
    }

    [Fact]
    public void One_flooding_session_does_not_pause_the_others()
    {
        _pump.Add("flood", new byte[TerminalOutputPump.HighWater + 1]);
        Add("quiet", "hello");
        RunUi();

        Assert.Equal([("flood", true)], _flow);
    }

    [Fact]
    public void Acknowledgements_nobody_asked_for_do_no_harm()
    {
        _pump.Acknowledge("unknown", 5000);
        _pump.Acknowledge("unknown", -3);

        Add("a", "x");
        RunUi();
        _pump.Acknowledge("a", int.MaxValue);
        _pump.Acknowledge("a", int.MaxValue);

        // Zu viel Bestätigtes wird nicht gutgeschrieben: Die Marke gilt danach wie am Anfang.
        _pump.Add("a", new byte[TerminalOutputPump.HighWater + 1]);
        RunUi();
        Assert.Equal([("a", true)], _flow);
    }

    [Fact]
    public void A_closed_session_drops_what_was_waiting()
    {
        Add("a", "lost");
        _pump.Remove("a");
        RunUi();

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_reloaded_page_releases_every_paused_session()
    {
        _pump.Add("a", new byte[TerminalOutputPump.HighWater + 1]);
        _pump.Add("b", new byte[TerminalOutputPump.HighWater + 1]);
        RunUi();
        Assert.Equal(2, _flow.Count);

        _pump.Reset();

        Assert.Equal([("a", false), ("b", false)], _flow.Skip(2).OrderBy(entry => entry.Id));

        // Die Zählung beginnt neu: Bis zur oberen Marke läuft wieder alles durch.
        _pump.Add("a", new byte[TerminalOutputPump.HighWater]);
        RunUi();
        Assert.Equal(4, _flow.Count);
    }

    [Fact]
    public async Task Output_from_many_threads_arrives_complete()
    {
        const int Writers = 8;
        const int Blocks = 500;
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            for (var block = 0; block < Blocks; block++)
            {
                _pump.Add($"s{writer}", [(byte)writer]);
            }
        })));
        RunUi();

        for (var writer = 0; writer < Writers; writer++)
        {
            var bytes = _posted.Where(block => block.Id == $"s{writer}").SelectMany(block => block.Data).ToArray();
            Assert.Equal(Blocks, bytes.Length);
            Assert.All(bytes, value => Assert.Equal(writer, value));
        }
    }
}
