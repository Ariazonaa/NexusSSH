using NexusSSH.Core.Models;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Wo die Daten liegen – und zwei Dinge, die nur in der ausgelieferten (Native-AOT-)Fassung auffallen würden.</summary>
[Collection(LanguageCollection.Name)]
public sealed class AppDataLocationTests
{
    [Fact]
    public void The_installed_app_keeps_its_data_in_the_user_profile()
    {
        Assert.Equal(
            Path.Combine(@"C:\Users\x\AppData\Local", "NexusSSH"),
            AppDataLocation.Resolve(null, @"C:\Users\x\AppData\Local"));
        Assert.Equal(
            Path.Combine(@"C:\Users\x\AppData\Local", "NexusSSH"),
            AppDataLocation.Resolve(string.Empty, @"C:\Users\x\AppData\Local"));
    }

    [Fact]
    public void An_explicit_folder_wins_over_everything()
    {
        using var folder = new TempFolder();
        var chosen = Path.Combine(folder.Path, "scratch");

        Assert.Equal(chosen, AppDataLocation.Resolve(chosen, @"C:\Users\x\AppData\Local"));
    }

    /// <summary>
    /// Unter Native AOT kann WinUI nur Listen anzeigen, deren Typ beim Übersetzen bekannt ist. Eine
    /// Collection-Expression für <c>IReadOnlyList</c> ergibt einen namenlosen Typ des Compilers – damit stürzt die
    /// ausgelieferte App ab, während im gewöhnlichen Build alles geht. Deshalb: echte <c>List</c> (BindableList.Of).
    /// </summary>
    [Fact]
    public void Lists_handed_to_the_interface_are_real_lists()
    {
        var shell = TestShell.Create(out var backend);
        var editor = new ProfileEditorViewModel(backend.GetProfiles()[0], backend.GetProfiles());
        var import = new SshConfigImportViewModel(shell.ReadSshConfig());

        Assert.IsType<List<ProfileGroup>>(shell.ProfileGroups);
        Assert.IsType<List<PaletteItem>>(shell.SearchPalette("web"));
        Assert.IsType<List<PaletteItem>>(shell.SearchPalette(null));
        Assert.IsType<List<string>>(shell.GroupNames());
        Assert.IsType<List<TerminalTheme>>(TerminalThemes.All);
        Assert.IsType<List<JumpChoice>>(editor.JumpChoices);
        Assert.IsType<List<JumpChoice>>(new ProfileEditorViewModel().JumpChoices);
        Assert.IsType<List<ImportCandidate>>(import.Candidates);

        // Auch nach einer Änderung an den Profilen.
        shell.DeleteProfile(backend.GetProfiles()[0]);
        Assert.IsType<List<ProfileGroup>>(shell.ProfileGroups);
    }

    [Fact]
    public void A_group_is_the_list_of_its_profiles()
    {
        var shell = TestShell.Create(out _);

        var group = shell.ProfileGroups[0];

        Assert.IsAssignableFrom<IEnumerable<ServerProfile>>(group);
        Assert.Equal(group.Profiles, group.ToList());
        Assert.StartsWith(group.Name + ", ", group.ToString());
        Assert.NotEmpty(group);
    }
}
