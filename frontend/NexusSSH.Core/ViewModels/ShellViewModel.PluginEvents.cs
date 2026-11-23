using System.Text.Json;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Plugins;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Plugins, die ungefragt aufgerufen werden: bei Ereignissen einer Sitzung (Recht „events“) und bei Zeilen der Ausgabe,
// die auf eines ihrer Muster passen (Recht „watch“). Solche Aufrufe dürfen nur Hinweise, Einträge und eigene Daten –
// kein Ergebnis-Dialog, kein Befehl, keine Datei: Es hat ja niemand gefragt. Zeilen werden gesammelt und höchstens
// alle PluginWatchPause an ein Plugin gegeben; was dazwischen nicht in den Korb passt, zählt nur noch.
public sealed partial class ShellViewModel
{
    /// <summary>So viele Zeilen gehen höchstens in einen Aufruf; mehr zählen nur als <c>dropped</c>.</summary>
    public const int MaxPluginLines = 50;

    /// <summary>So viele Ereignisse warten höchstens; mehr fallen weg.</summary>
    public const int MaxPluginEvents = 20;

    private readonly Dictionary<string, PluginInbox> _pluginInboxes = [];

    // Die Muster der Plugins, die beobachten dürfen – gelesen auf dem Faden der Ausgabe, deshalb nur ganz ersetzt.
    private volatile (string Plugin, PluginPattern Pattern)[] _pluginWatchers = [];

    /// <summary>So lange wird gesammelt, bevor ein Plugin Zeilen bekommt, und so lange ruht es danach. Tests setzen es kürzer.</summary>
    internal TimeSpan PluginWatchPause { get; set; } = TimeSpan.FromSeconds(2);

    private sealed class PluginInbox
    {
        public object Gate { get; } = new();

        public List<(string Pattern, string Server, string Text)> Lines { get; } = [];

        public List<(string Name, string Server)> Events { get; } = [];

        public int Dropped { get; set; }

        public bool Scheduled { get; set; }
    }

    // Ein Ereignis einer Sitzung an jedes Plugin, das es hören will und darf.
    private void PluginSessionEvent(SessionViewModel session, string name)
    {
        if (session.IsVirtual)
        {
            return;
        }

        foreach (var plugin in Plugins.Where(plugin => !plugin.IsDisabled && plugin.Has(PluginPermission.Events) && plugin.Manifest.Events.Contains(name)))
        {
            var inbox = InboxOf(plugin.Manifest.Id);
            bool start;
            lock (inbox.Gate)
            {
                if (inbox.Events.Count < MaxPluginEvents)
                {
                    inbox.Events.Add((name, Format.Visible(session.Profile.Name)));
                }

                start = Claim(inbox);
            }

            Start(plugin.Manifest.Id, inbox, start);
        }
    }

    // Setzt bei jeder Sitzung, ob sie ihre Zeilen zeigen muss: nur, solange ein Plugin beobachten darf.
    private void UpdatePluginWatch()
    {
        _pluginWatchers = [.. Plugins
            .Where(plugin => !plugin.IsDisabled && plugin.Has(PluginPermission.Watch))
            .SelectMany(plugin => plugin.Manifest.Watch.Select(pattern => (plugin.Manifest.Id, pattern)))];
        foreach (var session in Sessions)
        {
            WatchPluginLines(session);
        }
    }

    private void WatchPluginLines(SessionViewModel session)
        => session.LineWatcher = _pluginWatchers.Length > 0 ? line => OnPluginLine(session, line) : null;

    // Auf dem Faden der Ausgabe: prüfen, in den Korb legen, und nur beim ersten Eintrag die Zustellung anstoßen.
    private void OnPluginLine(SessionViewModel session, string line)
    {
        var text = line.Length <= 1000 ? line : line[..1000];
        foreach (var (plugin, pattern) in _pluginWatchers)
        {
            if (!pattern.Matches(text))
            {
                continue;
            }

            var inbox = InboxOf(plugin);
            bool start;
            lock (inbox.Gate)
            {
                if (inbox.Lines.Count < MaxPluginLines)
                {
                    inbox.Lines.Add((pattern.Id, Format.Visible(session.Profile.Name), Format.Visible(text)));
                }
                else
                {
                    inbox.Dropped++;
                }

                start = Claim(inbox);
            }

            Start(plugin, inbox, start);
        }
    }

    private PluginInbox InboxOf(string plugin)
    {
        lock (_pluginInboxes)
        {
            if (!_pluginInboxes.TryGetValue(plugin, out var inbox))
            {
                _pluginInboxes[plugin] = inbox = new PluginInbox();
            }

            return inbox;
        }
    }

    // Unter dem Schloss des Korbs: ob noch keine Zustellung läuft – dann übernimmt der Aufrufer sie.
    private static bool Claim(PluginInbox inbox)
    {
        var start = !inbox.Scheduled;
        inbox.Scheduled = true;
        return start;
    }

    // Außerhalb des Schlosses: die Zustellung beginnt auf dem Faden der Oberfläche.
    private void Start(string plugin, PluginInbox inbox, bool start)
    {
        if (start)
        {
            _dispatcher.Post(() => _ = DeliverToPluginAsync(plugin, inbox));
        }
    }

    // Leert den Korb eines Plugins, bis er leer bleibt: erst sammeln, dann jedes Ereignis, dann alle Zeilen in einem
    // Aufruf, dann ruhen. Läuft das Plugin gerade über die Palette, wird gewartet.
    private async Task DeliverToPluginAsync(string id, PluginInbox inbox)
    {
        await Task.Delay(PluginWatchPause);
        while (true)
        {
            while (_pluginsRunning.Contains(id))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            List<(string Pattern, string Server, string Text)> lines;
            List<(string Name, string Server)> events;
            int dropped;
            lock (inbox.Gate)
            {
                if (inbox.Lines.Count == 0 && inbox.Events.Count == 0)
                {
                    inbox.Dropped = 0;
                    inbox.Scheduled = false;
                    return;
                }

                (lines, events, dropped) = ([.. inbox.Lines], [.. inbox.Events], inbox.Dropped);
                inbox.Lines.Clear();
                inbox.Events.Clear();
                inbox.Dropped = 0;
            }

            if (_backend.Plugins is not { } runtime || Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id) is not { IsDisabled: false } plugin)
            {
                continue;
            }

            _pluginsRunning.Add(id);
            try
            {
                foreach (var (name, server) in events.Where(_ => plugin.Has(PluginPermission.Events)))
                {
                    await CallPluginAsync(runtime, plugin, L.Get("PluginEventLabel"), interactive: false, writer =>
                    {
                        writer.WriteString("kind", "event");
                        writer.WriteString("event", name);
                        writer.WriteStartObject("session");
                        writer.WriteString("server", server);
                        writer.WriteBoolean("connected", name == PluginEvent.Connected);
                        writer.WriteEndObject();
                    });

                    // Hat es eben versagt, ist es aus: kein weiterer Aufruf.
                    if (IsSwitchedOff(id))
                    {
                        break;
                    }
                }

                if (lines.Count > 0 && plugin.Has(PluginPermission.Watch) && !IsSwitchedOff(id))
                {
                    await CallPluginAsync(runtime, plugin, L.Get("PluginWatchLabel"), interactive: false, writer =>
                    {
                        writer.WriteString("kind", "watch");
                        writer.WriteStartArray("lines");
                        foreach (var (pattern, server, text) in lines)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("pattern", pattern);
                            writer.WriteString("server", server);
                            writer.WriteString("text", text);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteNumber("dropped", dropped);
                    });
                }
            }
            finally
            {
                _pluginsRunning.Remove(id);
            }

            await Task.Delay(PluginWatchPause);
        }
    }

    private bool IsSwitchedOff(string id) => Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id) is not { IsDisabled: false };

    /// <summary>Die Ereignisse eines Plugins, wie die App sie nennt.</summary>
    public static string EventNames(IEnumerable<string> events)
        => string.Join(", ", events.Select(name => name == PluginEvent.Connected ? L.Get("PluginEventConnected") : L.Get("PluginEventDisconnected")));

    /// <summary>Die Muster eines Plugins, wie die Vorschau sie zeigt: Text in Anführungszeichen, Ausdrücke zwischen Schrägstrichen.</summary>
    public static string PatternTexts(IEnumerable<PluginPattern> patterns)
        => string.Join(", ", patterns.Select(pattern => pattern.IsRegex ? $"/{pattern.Text}/" : $"\"{pattern.Text}\""));
}
