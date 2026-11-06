using NexusSSH.Core.Exchange;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

/// <summary>Größe der Oberfläche: Grenzen, Stufen, Befehle – und dass sie auf diesem Rechner bleibt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class UiZoomTests
{
    [Fact]
    public void The_interface_starts_at_its_normal_size_and_a_broken_value_is_brought_back_into_range()
    {
        Assert.Equal(100, new AppSettings().UiZoom);
        Assert.Equal(AppSettings.MinUiZoom, new AppSettings { UiZoom = 0 }.Sanitize().UiZoom);
        Assert.Equal(AppSettings.MaxUiZoom, new AppSettings { UiZoom = 5000 }.Sanitize().UiZoom);
    }

    [Fact]
    public void Zooming_moves_in_steps_stops_at_the_limits_and_is_remembered()
    {
        var settings = new AppSettings();
        var shell = TestShell.Create(out _, settings);
        var changes = 0;
        shell.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(shell.UiZoom) ? 1 : 0;

        shell.ZoomUi(+1);
        Assert.Equal(110, shell.UiZoom);
        Assert.Equal(110, settings.UiZoom);

        shell.ZoomUi(+50);
        Assert.Equal(AppSettings.MaxUiZoom, shell.UiZoom);
        shell.ZoomUi(+1);

        shell.ZoomUi(-50);
        Assert.Equal(AppSettings.MinUiZoom, shell.UiZoom);

        // An der Grenze ändert sich nichts mehr – und niemand wird benachrichtigt.
        Assert.Equal(3, changes);
    }

    [Fact]
    public void The_commands_make_the_interface_larger_smaller_and_normal_again()
    {
        var shell = TestShell.Create(out _);
        void Run(string id) => shell.Commands.Single(command => command.Id == id).Run();

        Run("view.zoomIn");
        Run("view.zoomIn");
        Run("view.zoomOut");
        Assert.Equal(110, shell.UiZoom);

        Run("view.zoomReset");
        Assert.Equal(100, shell.UiZoom);
    }

    [Fact]
    public void The_size_belongs_to_this_computer_and_is_not_exported()
        => Assert.DoesNotContain(typeof(ExportedSettings).GetProperties(), property => property.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase));
}
