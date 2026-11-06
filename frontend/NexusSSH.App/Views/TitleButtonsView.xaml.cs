using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class TitleButtonsView : UserControl
{
    public TitleButtonsView()
    {
        InitializeComponent();
    }

    public ShellViewModel Shell => App.Shell;

    private void OnFindClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("terminal.find");

    // Das Menü entsteht bei jedem Öffnen neu aus den Befehlen: Was gerade nicht geht (aufheben ohne Teilung), ist
    // ausgegraut, und die Schalter zeigen ihren Stand. Vor den Schaltern steht eine Linie.
    private void OnSplitMenuOpening(object? sender, object e)
    {
        SplitMenu.Items.Clear();
        var toggles = false;
        foreach (var id in ShellViewModel.SplitMenuIds)
        {
            if (Shell.Commands.FirstOrDefault(command => command.Id == id) is not { } command)
            {
                continue;
            }

            var state = Shell.SplitMenuState(id);
            if (state is not null && !toggles && SplitMenu.Items.Count > 0)
            {
                SplitMenu.Items.Add(new MenuFlyoutSeparator());
            }

            toggles |= state is not null;
            MenuFlyoutItem item;
            if (state is { } on)
            {
                // Ein Schalter hört auf seinen Zustand, nicht auf Click: UI Automation (und damit eine Sprachausgabe)
                // legt ihn um, ohne dass Click kommt.
                var toggle = new ToggleMenuFlyoutItem { IsChecked = on };
                toggle.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
                {
                    if (Shell.SplitMenuState(id) != toggle.IsChecked)
                    {
                        command.Run();
                    }
                });
                item = toggle;
            }
            else
            {
                item = new MenuFlyoutItem();
                item.Click += (_, _) => command.Run();
            }

            item.Text = command.Title;
            item.IsEnabled = command.IsAvailable;
            item.KeyboardAcceleratorTextOverride = command.Shortcut?.Display ?? string.Empty;
            AutomationProperties.SetAutomationId(item, id);
            SplitMenu.Items.Add(item);
        }
    }

    /// <summary>Für den Selbsttest: das Menü einmal aufbauen, wie es beim Öffnen entsteht.</summary>
    internal int ExerciseSplitMenu()
    {
        OnSplitMenuOpening(SplitMenu, EventArgs.Empty);
        return SplitMenu.Items.Count;
    }
}
