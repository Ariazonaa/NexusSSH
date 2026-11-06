using NexusSSH.Core.Backend;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>„Verbindung halten“: Abstand der Lebenszeichen und wie viele unbeantwortet bleiben dürfen – allgemein und je Profil.</summary>
[Collection(LanguageCollection.Name)]
public sealed class KeepAliveTests
{
    private static readonly ServerProfile Plain = new("p", "plain", "host.example.org", 22, "me", "", []);

    [Fact]
    public void The_general_values_reach_the_backend_only_when_they_differ_from_the_usual()
    {
        TestShell.Create(out var untouched);
        Assert.Null(untouched.KeepAlive);

        var shell = TestShell.Create(out var backend, new AppSettings { KeepAliveSeconds = 10, KeepAliveMissed = 6 });
        Assert.Equal((10, 6), backend.KeepAlive);

        shell.KeepAliveMissed = 2;
        Assert.Equal((10, 2), backend.KeepAlive);

        // 0 heißt „keine“; dazwischen und dem kürzesten Abstand gibt es nichts.
        shell.KeepAliveSeconds = 0;
        Assert.Equal((0, 2), backend.KeepAlive);
        shell.KeepAliveSeconds = 3;
        Assert.Equal((5, 2), backend.KeepAlive);
        shell.KeepAliveSeconds = 100_000;
        shell.KeepAliveMissed = 500;
        Assert.Equal((3600, AppSettings.MaxKeepAliveMissed), backend.KeepAlive);
        Assert.Equal((3600, AppSettings.MaxKeepAliveMissed), (shell.Settings.KeepAliveSeconds, shell.Settings.KeepAliveMissed));
    }

    [Fact]
    public void Settings_from_disk_outside_the_limits_fall_back_to_the_usual()
    {
        var settings = new AppSettings { KeepAliveSeconds = 2, KeepAliveMissed = 0 };
        settings.Sanitize();
        Assert.Equal((AppSettings.DefaultKeepAliveSeconds, AppSettings.DefaultKeepAliveMissed), (settings.KeepAliveSeconds, settings.KeepAliveMissed));

        settings = new AppSettings { KeepAliveSeconds = 0, KeepAliveMissed = 20 };
        settings.Sanitize();
        Assert.Equal((0, 20), (settings.KeepAliveSeconds, settings.KeepAliveMissed));
    }

    [Fact]
    public void The_core_hears_the_general_values_and_those_of_a_profile()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.save"] =
            """{"ok":true,"profile":{"id":"p","name":"plain","host":"host.example.org","port":22,"user":"me","group":"","tags":[],"favorite":false,"use_agent":true,"keepalive_interval":120,"keepalive_max":1}}""";
        var backend = new CoreBackend(transport);

        backend.SetKeepAlive(15, 4);
        var set = Assert.Single(transport.Sent("keepalive.set"));
        Assert.Equal((15, 4), (set.GetProperty("interval").GetInt32(), set.GetProperty("max").GetInt32()));

        var saved = backend.Save(Plain with { KeepAliveSeconds = 120, KeepAliveMissed = 1 });
        var sent = transport.Sent("profiles.save").Last().GetProperty("profile");
        Assert.Equal((120, 1), (sent.GetProperty("keepalive_interval").GetInt32(), sent.GetProperty("keepalive_max").GetInt32()));
        Assert.Equal((120, 1), (saved.KeepAliveSeconds, saved.KeepAliveMissed));

        // Ohne eigene Werte fehlen die Felder ganz – der Kern nimmt dann das Allgemeine.
        backend.Save(Plain);
        sent = transport.Sent("profiles.save").Last().GetProperty("profile");
        Assert.False(sent.TryGetProperty("keepalive_interval", out _));
        Assert.False(sent.TryGetProperty("keepalive_max", out _));
    }

    [Fact]
    public void An_empty_field_in_the_editor_means_as_in_the_settings()
    {
        TestShell.Create(out _);
        var editor = new ProfileEditorViewModel(null, []) { Host = "h", User = "u" };
        Assert.True(double.IsNaN(editor.KeepAliveSeconds) && double.IsNaN(editor.KeepAliveMissed));
        Assert.Equal((null, null), (editor.ToProfile().KeepAliveSeconds, editor.ToProfile().KeepAliveMissed));

        editor.KeepAliveSeconds = 0;
        editor.KeepAliveMissed = 8;
        Assert.Equal((0, 8), (editor.ToProfile().KeepAliveSeconds, editor.ToProfile().KeepAliveMissed));

        // Was kein Abstand sein kann, gilt als leer.
        editor.KeepAliveSeconds = 3;
        Assert.Null(editor.ToProfile().KeepAliveSeconds);

        // Beim Bearbeiten steht da, was das Profil nennt.
        editor = new ProfileEditorViewModel(Plain with { KeepAliveSeconds = 60 }, [Plain]);
        Assert.Equal(60, editor.KeepAliveSeconds);
        Assert.True(double.IsNaN(editor.KeepAliveMissed));
        Assert.Equal((60, null), (editor.ToProfile().KeepAliveSeconds, editor.ToProfile().KeepAliveMissed));
    }

    [Fact]
    public void A_setting_changed_from_the_usual_counts_as_changed_and_goes_back()
    {
        var shell = TestShell.Create(out var backend);
        Assert.True(shell.IsDefaultSetting("KeepAlive"));

        shell.KeepAliveSeconds = 60;
        Assert.False(shell.IsDefaultSetting("KeepAlive"));

        shell.ResetSetting("KeepAlive");
        Assert.True(shell.IsDefaultSetting("KeepAlive"));
        Assert.Equal((AppSettings.DefaultKeepAliveSeconds, AppSettings.DefaultKeepAliveMissed), backend.KeepAlive);
    }
}
