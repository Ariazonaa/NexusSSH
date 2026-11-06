using NexusSSH.Core.Backend;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Import aus <c>~/.ssh/config</c> (Spec §8): Protokoll zum Kern, Auswahl im Dialog, Ergebnis in der Shell.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SshConfigImportTests
{
    private const string Listing =
        """{"ok":true,"path":"C:\\Users\\me\\.ssh\\config","hosts":[{"alias":"bastion","host":"bastion.example.org","port":22,"user":"ops","forward_agent":false,"exists":true},{"alias":"inner","host":"10.0.0.5","port":2222,"user":"app","key_path":"C:\\Users\\me\\.ssh\\id_inner","jump":"bastion","forward_agent":true,"exists":false}]}""";

    [Fact]
    public void The_core_lists_the_hosts_of_the_config()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.read"] = Listing;
        var backend = new CoreBackend(transport);

        var listing = backend.ReadSshConfig();

        Assert.Equal(@"C:\Users\me\.ssh\config", listing.Path);
        Assert.Equal(
            [
                new SshConfigHost("bastion", "bastion.example.org", 22, "ops", null, null, false, true),
                new SshConfigHost("inner", "10.0.0.5", 2222, "app", @"C:\Users\me\.ssh\id_inner", "bastion", true, false),
            ],
            listing.Hosts);
        Assert.Equal("app@10.0.0.5:2222", listing.Hosts[1].Address);
    }

    [Fact]
    public void Without_an_ssh_directory_the_listing_is_empty()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.read"] = """{"ok":true,"hosts":[]}""";

        var listing = new CoreBackend(transport).ReadSshConfig();

        Assert.Null(listing.Path);
        Assert.Empty(listing.Hosts);
    }

    [Fact]
    public void Importing_names_the_aliases_reloads_the_profiles_and_returns_the_count()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.import"] = """{"ok":true,"imported":2}""";
        var backend = new CoreBackend(transport);
        var changes = 0;
        backend.Changed += () => changes++;
        transport.Responses["profiles.list"] =
            """{"ok":true,"profiles":[{"id":"a","name":"bastion","host":"b","port":22,"user":"ops","group":"","tags":[],"favorite":false,"use_agent":true},{"id":"b","name":"inner","host":"10.0.0.5","port":2222,"user":"app","group":"","tags":[],"favorite":false,"use_agent":true,"jump":"a","forward_agent":true}]}""";

        var imported = backend.ImportSshConfig(["inner"]);

        Assert.Equal(2, imported);
        Assert.Equal(["inner"], Assert.Single(transport.Sent("sshconfig.import")).GetProperty("names").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(1, changes);
        var inner = backend.GetProfiles().Single(profile => profile.Name == "inner");
        Assert.Equal("a", inner.JumpId);
        Assert.True(inner.ForwardAgent);
        Assert.Null(backend.GetProfiles()[0].JumpId);
    }

    [Fact]
    public void A_refused_import_is_an_exception_with_the_cores_code()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.import"] = """{"ok":false,"error":"storage","message":"disk full"}""";

        var error = Assert.Throws<BackendException>(() => new CoreBackend(transport).ImportSshConfig(["x"]));

        Assert.Equal("storage", error.Code);
    }

    [Fact]
    public void The_dialog_preselects_what_is_not_a_profile_yet()
    {
        TestShell.Create(out _);
        var import = new SshConfigImportViewModel(new SshConfigListing(
            @"C:\Users\me\.ssh\config",
            [
                new SshConfigHost("bastion", "bastion.example.org", 22, "ops", null, null, false, Exists: true),
                new SshConfigHost("inner", "10.0.0.5", 2222, "app", LocalPath.Of("Users", "me", ".ssh", "id_inner"), "bastion", true, Exists: false),
            ]));

        Assert.True(import.HasCandidates);
        Assert.Equal(@"Servers from C:\Users\me\.ssh\config. Checked ones are created as profiles.", import.Intro);
        Assert.Equal([false, true], import.Candidates.Select(candidate => candidate.IsSelected));
        Assert.Equal("ops@bastion.example.org  ·  already present", import.Candidates[0].Summary);
        Assert.Equal("app@10.0.0.5:2222  ·  via bastion  ·  id_inner", import.Candidates[1].Summary);
        Assert.Equal(["inner"], import.SelectedAliases);
        Assert.True(import.CanImport);
    }

    [Fact]
    public void Nothing_selected_means_nothing_to_import()
    {
        TestShell.Create(out _);
        var import = new SshConfigImportViewModel(new SshConfigListing("~/.ssh/config", [new SshConfigHost("a", "a", 22, "u", null, null, false, false)]));
        var announced = new List<string?>();
        import.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        import.Candidates[0].IsSelected = false;

        Assert.False(import.CanImport);
        Assert.Contains(nameof(SshConfigImportViewModel.CanImport), announced);
        Assert.Empty(import.SelectedAliases);
    }

    [Fact]
    public void An_empty_config_says_so()
    {
        TestShell.Create(out _);
        var import = new SshConfigImportViewModel(new SshConfigListing(null, []));

        Assert.False(import.HasCandidates);
        Assert.False(import.CanImport);
        Assert.Equal("There are no servers in ~/.ssh/config, or the file does not exist.", import.Intro);
    }

    [Fact]
    public void The_shell_imports_through_the_backend_and_logs_the_result()
    {
        var shell = TestShell.Create(out var backend);
        Assert.Equal([true, false, false], shell.ReadSshConfig().Hosts.OrderByDescending(host => host.Exists).Select(host => host.Exists));

        var imported = shell.ImportSshConfig(["build01"]);

        // Der Sprung-Server kommt mit.
        Assert.Equal(2, imported);
        var build = backend.GetProfiles().Single(profile => profile.Name == "build01");
        Assert.Equal(backend.GetProfiles().Single(profile => profile.Name == "bastion").Id, build.JumpId);
        Assert.Contains(shell.ProfileGroups, group => group.Profiles.Contains(build));
        Assert.Contains(shell.Output, entry => entry.Text == "2 profile(s) imported from the SSH config.");
        Assert.Equal(0, shell.ImportSshConfig(["build01"]));
    }

    [Fact]
    public void A_linked_import_says_so_and_the_profile_carries_its_alias()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.import"] = """{"ok":true,"imported":1}""";
        transport.Responses["profiles.list"] =
            """{"ok":true,"profiles":[{"id":"a","name":"web","host":"web.example","port":22,"user":"ops","group":"","tags":[],"favorite":false,"use_agent":true,"ssh_alias":"web"}]}""";
        var backend = new CoreBackend(transport);

        backend.ImportSshConfig(["web"], linked: true);
        backend.ImportSshConfig(["web"]);

        var sent = transport.Sent("sshconfig.import").ToList();
        Assert.True(sent[0].GetProperty("linked").GetBoolean());
        Assert.False(sent[1].TryGetProperty("linked", out _));
        Assert.Equal("web", Assert.Single(backend.GetProfiles()).SshAlias);
    }

    [Fact]
    public void Refreshing_asks_the_core_and_reloads_only_when_something_changed()
    {
        var transport = new FakeTransport();
        transport.Responses["sshconfig.refresh"] = """{"ok":true,"imported":0}""";
        var backend = new CoreBackend(transport);
        var changes = 0;
        backend.Changed += () => changes++;

        Assert.Equal(0, backend.RefreshSshConfig());
        Assert.Equal(0, changes);

        transport.Responses["sshconfig.refresh"] = """{"ok":true,"imported":2}""";
        Assert.Equal(2, backend.RefreshSshConfig());
        Assert.Equal(1, changes);
        Assert.Equal(2, transport.Sent("sshconfig.refresh").Count());
    }

    [Fact]
    public void Only_the_ssh_config_can_be_linked_and_only_the_named_profiles_are()
    {
        var shell = TestShell.Create(out var backend);
        var import = new SshConfigImportViewModel(shell.ReadSshConfig());
        Assert.True(import.CanLink);
        Assert.False(import.Linked);
        Assert.False(new SshConfigImportViewModel([], backend.GetProfiles()).CanLink);

        shell.ImportSshConfig(["build01"], linked: true);

        Assert.Equal("build01", backend.GetProfiles().Single(profile => profile.Name == "build01").SshAlias);
        Assert.Null(backend.GetProfiles().Single(profile => profile.Name == "bastion").SshAlias);
    }

    [Fact]
    public void The_editor_keeps_the_link_until_it_is_removed()
    {
        var profile = new MockBackend().GetProfiles()[0] with { SshAlias = "web" };
        var editor = new ProfileEditorViewModel(profile, [profile]);

        Assert.True(editor.IsLinked);
        Assert.Contains("web", editor.LinkedText, StringComparison.Ordinal);
        Assert.Equal("web", editor.ToProfile().SshAlias);

        editor.Unlink();

        Assert.False(editor.IsLinked);
        Assert.Null(editor.ToProfile().SshAlias);
    }

    private static readonly SshConfigHost[] ConfigHosts =
    [
        new("web", "10.0.0.1", 2200, "deploy", "/keys/id_web", Jump: null, ForwardAgent: true, Exists: false),
        new("db", "10.0.0.2", 22, "postgres", KeyPath: null, Jump: null, ForwardAgent: false, Exists: false),
    ];

    [Fact]
    public void A_profile_can_be_linked_in_the_editor_and_then_shows_what_the_file_says()
    {
        var profile = new MockBackend().GetProfiles()[0];
        var editor = new ProfileEditorViewModel(profile, [profile], configHosts: ConfigHosts);

        // Zur Wahl: „nicht verknüpft“ und die Einträge der Datei; noch gilt, was im Profil steht.
        Assert.Equal([null, "web", "db"], editor.LinkChoices.Select(choice => choice.Alias));
        Assert.True(editor.CanLink);
        Assert.False(editor.IsLinked);
        Assert.Equal(profile.Host, editor.Host);
        Assert.True(editor.CanEditAddress && editor.CanEditPort && editor.CanEditSignIn);

        editor.Link = editor.LinkChoices[1];

        Assert.Equal(("10.0.0.1", 2200d, "deploy", "/keys/id_web", true), (editor.Host, editor.Port, editor.User, editor.KeyPath, editor.ForwardAgent));
        Assert.Equal("web", editor.ToProfile().SshAlias);
        // Was die Datei bestimmt, lässt sich hier nicht mehr ändern.
        Assert.False(editor.CanEditAddress || editor.CanEditPort || editor.CanEditSignIn);

        editor.Unlink();
        Assert.True(editor.CanEditAddress && editor.CanEditPort && editor.CanEditSignIn);
        Assert.Null(editor.ToProfile().SshAlias);
    }

    [Fact]
    public void Opening_a_linked_profile_shows_the_profile_and_keeps_an_entry_the_file_no_longer_has()
    {
        var profile = new MockBackend().GetProfiles()[0] with { SshAlias = "gone" };
        var editor = new ProfileEditorViewModel(profile, [profile], configHosts: ConfigHosts);

        Assert.Equal("gone", editor.Link.Alias);
        Assert.Equal([null, "web", "db", "gone"], editor.LinkChoices.Select(choice => choice.Alias));
        Assert.Equal(profile.Host, editor.Host);
        Assert.Equal("gone", editor.ToProfile().SshAlias);
    }

    [Fact]
    public void With_an_identity_the_link_leaves_user_and_key_alone()
    {
        var profile = new MockBackend().GetProfiles()[0];
        Identity[] identities = [new("work", "Work", "me", "/keys/mine", UseAgent: false)];
        var editor = new ProfileEditorViewModel(profile, [profile], identities, ConfigHosts);
        editor.Identity = editor.IdentityChoices[1];

        editor.Link = editor.LinkChoices[1];

        Assert.Equal(("10.0.0.1", "me", "/keys/mine"), (editor.Host, editor.User, editor.KeyPath));
    }

    [Fact]
    public void Telnet_and_serial_profiles_cannot_be_linked()
    {
        var editor = new ProfileEditorViewModel(configHosts: ConfigHosts) { IsTelnet = true };

        Assert.False(editor.CanLink);
        Assert.False(new ProfileEditorViewModel().CanLink);
    }

    [Fact]
    public void The_command_reads_the_linked_profiles_again_and_logs_how_many_changed()
    {
        var shell = TestShell.Create(out _);

        shell.Commands.Single(command => command.Id == "server.refreshLinked").Run();

        Assert.Contains(shell.Output, entry => entry.Text == "0 linked profile(s) updated from the SSH config.");
    }
}
