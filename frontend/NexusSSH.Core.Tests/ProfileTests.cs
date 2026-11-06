using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public sealed class ProfileTests
{
    public ProfileTests() => L.Init(AppLanguage.English);

    [Fact]
    public void A_new_profile_starts_with_sensible_defaults_and_is_not_yet_valid()
    {
        var editor = new ProfileEditorViewModel();

        Assert.True(editor.IsNew);
        Assert.Equal("Add server", editor.Title);
        Assert.Equal(22, editor.Port);
        Assert.True(editor.UseAgent);
        Assert.False(editor.IsValid);

        // Leere Pflichtfelder zeigen noch keinen Fehler – erst eine falsche Eingabe.
        Assert.Equal(string.Empty, editor.HostError);
        Assert.Equal(string.Empty, editor.UserError);
    }

    [Fact]
    public void Host_and_user_make_it_valid_and_the_name_falls_back_to_the_host()
    {
        var editor = new ProfileEditorViewModel { Host = " example.org ", User = " root " };

        Assert.True(editor.IsValid);
        var profile = editor.ToProfile();
        Assert.Equal(string.Empty, profile.Id);
        Assert.Equal(("example.org", "example.org", "root", 22), (profile.Name, profile.Host, profile.User, profile.Port));
        Assert.Null(profile.KeyPath);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("tab\there")]
    public void An_unusable_host_is_flagged(string host)
    {
        var editor = new ProfileEditorViewModel { Host = host, User = "root" };

        Assert.False(editor.IsValid);
        Assert.Equal("Not a valid host name.", editor.HostError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(22.5)]
    [InlineData(double.NaN)]
    public void An_unusable_port_is_flagged(double port)
    {
        var editor = new ProfileEditorViewModel { Host = "h", User = "u", Port = port };

        Assert.False(editor.IsValid);
        Assert.NotEqual(string.Empty, editor.PortError);
    }

    [Fact]
    public void Validity_is_announced_when_a_field_changes()
    {
        var editor = new ProfileEditorViewModel { Host = "h" };
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.User = "u";

        Assert.Contains(nameof(ProfileEditorViewModel.IsValid), changed);
        Assert.Contains(nameof(ProfileEditorViewModel.UserError), changed);
    }

    [Fact]
    public void Editing_keeps_identity_and_history_and_parses_tags_and_key_path()
    {
        var connected = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var original = new ServerProfile("p1", "web01", "10.0.1.10", 2222, "ubuntu", "Production", ["web"], true, null, false, connected);
        var editor = new ProfileEditorViewModel(original);
        Assert.False(editor.IsNew);
        Assert.Equal("Edit server", editor.Title);
        Assert.Equal("web", editor.Tags);
        Assert.False(editor.UseAgent);

        editor.Tags = " web, nginx ;; prod ";
        editor.KeyPath = "\"C:\\Users\\me\\.ssh\\id_ed25519\"";
        editor.Group = " Staging ";
        var profile = editor.ToProfile();

        Assert.Equal("p1", profile.Id);
        Assert.Equal(connected, profile.LastConnected);
        Assert.Equal(["web", "nginx", "prod"], profile.Tags);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", profile.KeyPath);
        Assert.Equal("Staging", profile.Group);
        Assert.True(profile.IsFavorite);
    }

    [Fact]
    public void The_shell_saves_and_deletes_through_the_backend_and_regroups()
    {
        var shell = TestShell.Create(out var backend);
        var announced = 0;
        shell.PropertyChanged += (_, e) => announced += e.PropertyName == nameof(ShellViewModel.ProfileGroups) ? 1 : 0;

        var saved = shell.SaveProfile(new ServerProfile("", "build01", "10.0.3.1", 22, "ci", "", []));

        Assert.NotNull(saved);
        Assert.NotEqual(string.Empty, saved.Id);
        Assert.Equal(["Production", "Development", "Ungrouped"], shell.ProfileGroups.Select(group => group.Name));
        Assert.Equal("build01", Assert.Single(shell.SearchProfiles("build")).Name);
        Assert.Equal(["Development", "Production"], shell.GroupNames());

        shell.DeleteProfile(saved);

        Assert.Equal(["Production", "Development"], shell.ProfileGroups.Select(group => group.Name));
        Assert.Equal(2, announced);
        Assert.DoesNotContain(backend.GetProfiles(), profile => profile.Name == "build01");
    }

    [Fact]
    public void A_profile_the_backend_refuses_is_logged_not_thrown()
    {
        var shell = TestShell.Create(out _);

        var saved = shell.SaveProfile(new ServerProfile("", "broken", " ", 22, "u", "", []));

        Assert.Null(saved);
        Assert.Contains(shell.Output, entry => entry.Text.Contains("broken") && entry.Text.Contains("invalid_profile"));
    }

    [Fact]
    public void An_empty_profile_list_is_reported_as_such()
    {
        var shell = TestShell.Create(out var backend);
        Assert.True(shell.HasProfiles);

        foreach (var profile in backend.GetProfiles())
        {
            shell.DeleteProfile(profile);
        }

        Assert.True(shell.HasNoProfiles);
        Assert.Empty(shell.ProfileGroups);
    }

    [Fact]
    public void The_summary_line_names_address_and_tags()
    {
        Assert.Equal("ubuntu@10.0.1.10  ·  web, nginx", Format.ProfileSummary("ubuntu@10.0.1.10", ["web", "nginx"]));
        Assert.Equal("root@h", Format.ProfileSummary("root@h", []));
    }

    [Fact]
    public void A_copy_is_a_new_profile_with_the_same_settings()
    {
        var shell = TestShell.Create(out var backend);
        var original = backend.GetProfiles().Single(profile => profile.Id == "database01") with { LastConnected = DateTimeOffset.UnixEpoch };

        var copy = ShellViewModel.CopyOf(original);
        Assert.Equal(string.Empty, copy.Id);
        Assert.Equal("database01 (copy)", copy.Name);
        Assert.Equal(original.Host, copy.Host);
        Assert.Null(copy.LastConnected);
        Assert.False(copy.HasSavedSecret, "das gespeicherte Passwort gehört zum Original");

        // Der Editor behandelt die Vorlage als neues Profil; gespeichert entsteht ein zweites.
        var editor = new ProfileEditorViewModel(copy, backend.GetProfiles());
        Assert.True(editor.IsNew);
        var saved = shell.SaveProfile(editor.ToProfile())!;
        Assert.NotEqual(original.Id, saved.Id);
        Assert.Equal(2, backend.GetProfiles().Count(profile => profile.Host == original.Host));
    }

    [Fact]
    public void A_saved_password_can_be_forgotten_without_deleting_the_profile()
    {
        var shell = TestShell.Create(out var backend);
        var profile = shell.Profiles.Single(profile => profile.HasSavedSecret);

        shell.ForgetSecrets(profile);

        Assert.DoesNotContain(shell.Profiles, other => other.HasSavedSecret);
        Assert.Contains(backend.GetProfiles(), other => other.Id == profile.Id);
        Assert.Contains(shell.Output, entry => entry.Text == $"Removed the saved password for {profile.Name}");
    }

    [Fact]
    public void Profiles_are_ordered_as_chosen_with_favorites_first()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out var backend, settings);
        foreach (var profile in backend.GetProfiles())
        {
            shell.DeleteProfile(profile);
        }

        var time = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        shell.SaveProfile(new ServerProfile(string.Empty, "zeta", "z", 22, "u", "G", [], LastConnected: time.AddHours(3)));
        shell.SaveProfile(new ServerProfile(string.Empty, "Alpha", "a", 22, "u", "G", [], LastConnected: time.AddHours(1)));
        shell.SaveProfile(new ServerProfile(string.Empty, "mid", "m", 22, "u", "G", [], IsFavorite: true));
        shell.SaveProfile(new ServerProfile(string.Empty, "beta", "b", 22, "u", "G", [], LastConnected: time.AddHours(2)));

        string[] Names() => [.. shell.ProfileGroups.Single().Profiles.Select(profile => profile.Name)];

        Assert.Equal(["mid", "zeta", "Alpha", "beta"], Names());

        shell.ProfileSort = ProfileSort.Name;
        Assert.Equal(["mid", "Alpha", "beta", "zeta"], Names());
        Assert.Equal(ProfileSort.Name, settings.ProfileSort);

        shell.ProfileSort = ProfileSort.Recent;
        Assert.Equal(["mid", "zeta", "beta", "Alpha"], Names());

        // Die Palette hält dieselbe Reihenfolge.
        Assert.Equal(["mid", "zeta", "beta", "Alpha"], shell.SearchProfiles(null).Select(profile => profile.Name));
    }
}
